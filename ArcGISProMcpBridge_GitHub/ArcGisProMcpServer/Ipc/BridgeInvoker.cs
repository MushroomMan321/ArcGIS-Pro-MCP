using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGisProBridgeContracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace ArcGisProMcpServer.Ipc;

public sealed record BridgeInvokerOptions(int DefaultTimeoutMs, int MaxTimeoutMs);

public sealed class BridgeInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly BridgeClient _client;
    private readonly BridgeConfiguration _config;
    private readonly BridgeInvokerOptions _options;

    public BridgeInvoker(BridgeClient client, BridgeConfiguration config, BridgeInvokerOptions options)
    {
        _client = client;
        _config = config;
        _options = options;
    }

    public async Task<BridgeResponse> InvokeAsync(
        string op,
        object? args = null,
        int? timeoutMs = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = NormalizeTimeout(timeoutMs);
        var request = BridgeRequest.Create(
            op,
            ToObjectMap(args),
            effectiveTimeout,
            dryRun);

        if (!_config.IsOperationEnabled(op))
        {
            var group = BridgeConfiguration.GetToolGroup(op);
            var response = BridgeResponse.Failure(
                request.Id,
                "bridge.tool_group_disabled",
                $"Operation '{op}' belongs to disabled tool group '{group}'. Enable the group in the bridge config before calling it.",
                0,
                warnings: new[] { $"Enabled tool groups: {string.Join(", ", _config.EnabledToolGroups)}" });
            BridgeAuditLog.Append(_config.GetAuditLogPath(), "mcp-server", request, response);
            return response;
        }

        return await _client.SendAsync(request, cancellationToken);
    }

    public async Task<CallToolResult> InvokeToolAsync(
        string op,
        object? args = null,
        int? timeoutMs = null,
        bool dryRun = false,
        bool includeImageArtifacts = false,
        CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync(op, args, timeoutMs, dryRun, cancellationToken);
        return response.Ok
            ? await ToSuccessToolResultAsync(response, includeImageArtifacts, cancellationToken)
            : ToErrorToolResult(response);
    }

    public async Task<string> InvokeJsonResourceAsync(
        string op,
        object? args = null,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync(op, args, timeoutMs, cancellationToken: cancellationToken);
        if (!response.Ok)
        {
            throw new McpException(FormatError(response));
        }

        var payload = CreateStructuredPayload(response);
        return payload.ToJsonString(JsonOptions);
    }

    private int NormalizeTimeout(int? timeoutMs)
    {
        var requested = timeoutMs.GetValueOrDefault(_options.DefaultTimeoutMs);
        requested = requested <= 0 ? _options.DefaultTimeoutMs : requested;
        return Math.Clamp(requested, 1000, _options.MaxTimeoutMs);
    }

    private static JsonObjectMap? ToObjectMap(object? args)
    {
        if (args is null)
        {
            return null;
        }

        if (args is JsonObjectMap map)
        {
            return map;
        }

        var element = JsonSerializer.SerializeToElement(args, JsonOptions);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Bridge arguments must serialize to a JSON object.", nameof(args));
        }

        var result = new JsonObjectMap();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static async Task<CallToolResult> ToSuccessToolResultAsync(
        BridgeResponse response,
        bool includeImageArtifacts,
        CancellationToken cancellationToken)
    {
        var structured = CreateStructuredPayload(response);
        var content = new List<ContentBlock>
        {
            new TextContentBlock
            {
                Type = "text",
                Text = structured.ToJsonString(JsonOptions)
            }
        };

        if (includeImageArtifacts)
        {
            foreach (var artifact in response.Artifacts.Where(a => a.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
            {
                if (!File.Exists(artifact.Path))
                {
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(artifact.Path, cancellationToken);
                content.Add(new ImageContentBlock
                {
                    Type = "image",
                    MimeType = artifact.MimeType,
                    Data = Convert.ToBase64String(bytes)
                });
            }
        }

        return new CallToolResult
        {
            Content = content,
            StructuredContent = structured,
            IsError = false
        };
    }

    private static CallToolResult ToErrorToolResult(BridgeResponse response)
    {
        var structured = new JsonObject
        {
            ["ok"] = false,
            ["error"] = response.Error is null
                ? null
                : JsonSerializer.SerializeToNode(response.Error, JsonOptions),
            ["warnings"] = JsonSerializer.SerializeToNode(response.Warnings, JsonOptions),
            ["messages"] = JsonSerializer.SerializeToNode(response.Messages, JsonOptions),
            ["elapsedMs"] = response.ElapsedMs
        };

        return new CallToolResult
        {
            IsError = true,
            StructuredContent = structured,
            Content = new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Type = "text",
                    Text = FormatError(response)
                }
            }
        };
    }

    private static JsonObject CreateStructuredPayload(BridgeResponse response)
    {
        return new JsonObject
        {
            ["ok"] = response.Ok,
            ["data"] = response.Data.HasValue
                ? JsonNode.Parse(response.Data.Value.GetRawText())
                : null,
            ["warnings"] = JsonSerializer.SerializeToNode(response.Warnings, JsonOptions),
            ["messages"] = JsonSerializer.SerializeToNode(response.Messages, JsonOptions),
            ["artifacts"] = JsonSerializer.SerializeToNode(response.Artifacts, JsonOptions),
            ["elapsedMs"] = response.ElapsedMs
        };
    }

    private static string FormatError(BridgeResponse response)
    {
        var code = response.Error?.Code ?? "bridge.error";
        var message = response.Error?.Message ?? "ArcGIS Pro bridge request failed.";
        return $"{code}: {message}";
    }
}
