using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ArcGisProBridgeContracts;
using Microsoft.Extensions.Logging;

namespace ArcGisProMcpServer.Ipc;

public sealed class BridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly BridgeConfiguration _config;
    private readonly ILogger<BridgeClient>? _logger;

    public BridgeClient(string pipeName, BridgeConfiguration config, ILogger<BridgeClient>? logger = null)
    {
        _pipeName = pipeName;
        _config = config;
        _logger = logger;
    }

    public async Task<BridgeResponse> SendAsync(BridgeRequest request, CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1000, request.TimeoutMs));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        _logger?.LogInformation(
            "Bridge request {RequestId} starting: op={Operation} dryRun={DryRun} timeoutMs={TimeoutMs}",
            request.Id,
            request.Op,
            request.DryRun,
            request.TimeoutMs);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, timeoutCts.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                var emptyResponse = BridgeResponse.Failure(request.Id, "bridge.empty_response", "ArcGIS Pro bridge returned no response.", stopwatch.ElapsedMilliseconds);
                LogResponse(request, emptyResponse);
                AppendAudit(request, emptyResponse);
                return emptyResponse;
            }

            var response = JsonSerializer.Deserialize<BridgeResponse>(line, JsonOptions)
                ?? BridgeResponse.Failure(request.Id, "bridge.deserialize", "ArcGIS Pro bridge response could not be deserialized.", stopwatch.ElapsedMilliseconds);
            LogResponse(request, response);
            AppendAudit(request, response);
            return response;
        }
        catch (JsonException ex)
        {
            var response = BridgeResponse.Failure(request.Id, "bridge.deserialize", ex.Message, stopwatch.ElapsedMilliseconds);
            LogResponse(request, response);
            AppendAudit(request, response);
            return response;
        }
        catch (TimeoutException ex)
        {
            var response = BridgeResponse.Failure(request.Id, "bridge.timeout", ex.Message, stopwatch.ElapsedMilliseconds);
            LogResponse(request, response);
            AppendAudit(request, response);
            return response;
        }
        catch (OperationCanceledException ex)
        {
            var code = cancellationToken.IsCancellationRequested ? "bridge.cancelled" : "bridge.timeout";
            var message = cancellationToken.IsCancellationRequested
                ? "Bridge request was cancelled by the MCP client."
                : $"Bridge request timed out after {timeout.TotalMilliseconds:0} ms.";
            var response = BridgeResponse.Failure(request.Id, code, message, stopwatch.ElapsedMilliseconds, details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().Name }));
            LogResponse(request, response);
            AppendAudit(request, response);
            return response;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var response = BridgeResponse.Failure(request.Id, "bridge.unavailable", ex.Message, stopwatch.ElapsedMilliseconds);
            LogResponse(request, response);
            AppendAudit(request, response);
            return response;
        }
    }

    public Task<BridgeResponse> OpAsync(
        string op,
        JsonObjectMap? args = null,
        int timeoutMs = BridgeDefaults.DefaultTimeoutMs,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(BridgeRequest.Create(op, args, timeoutMs, dryRun), cancellationToken);
    }

    private void LogResponse(BridgeRequest request, BridgeResponse response)
    {
        if (response.Ok)
        {
            _logger?.LogInformation(
                "Bridge request {RequestId} completed: op={Operation} elapsedMs={ElapsedMs}",
                request.Id,
                request.Op,
                response.ElapsedMs);
            return;
        }

        _logger?.LogWarning(
            "Bridge request {RequestId} failed: op={Operation} code={Code} message={Message} elapsedMs={ElapsedMs}",
            request.Id,
            request.Op,
            response.Error?.Code,
            response.Error?.Message,
            response.ElapsedMs);
    }

    private void AppendAudit(BridgeRequest request, BridgeResponse response)
    {
        BridgeAuditLog.Append(_config.GetAuditLogPath(), "mcp-server", request, response);
    }
}
