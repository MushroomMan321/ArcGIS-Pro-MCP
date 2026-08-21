using System.ComponentModel;
using System.Text.Json;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Resources;

[McpServerResourceType]
public static class ArcGisResources
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [McpServerResource(UriTemplate = "arcgispro://project/current", Name = "current-project", MimeType = "application/json")]
    [Description("Current ArcGIS Pro project state.")]
    public static async Task<TextResourceContents> CurrentProject(
        BridgeInvoker bridge,
        CancellationToken cancellationToken = default)
    {
        return JsonResource(
            "arcgispro://project/current",
            await bridge.InvokeJsonResourceAsync("project.get_current", cancellationToken: cancellationToken));
    }

    [McpServerResource(UriTemplate = "arcgispro://maps", Name = "maps", MimeType = "application/json")]
    [Description("Maps in the current ArcGIS Pro project.")]
    public static async Task<TextResourceContents> Maps(
        BridgeInvoker bridge,
        CancellationToken cancellationToken = default)
    {
        return JsonResource(
            "arcgispro://maps",
            await bridge.InvokeJsonResourceAsync("map.list", cancellationToken: cancellationToken));
    }

    [McpServerResource(UriTemplate = "arcgispro://registry", Name = "object-registry", MimeType = "application/json")]
    [Description("Stable session object registry for maps, layers, layouts, map frames, and artifacts.")]
    public static async Task<TextResourceContents> Registry(
        BridgeInvoker bridge,
        CancellationToken cancellationToken = default)
    {
        return JsonResource(
            "arcgispro://registry",
            await bridge.InvokeJsonResourceAsync("object.registry", cancellationToken: cancellationToken));
    }

    [McpServerResource(UriTemplate = "arcgispro://map/{mapId}", Name = "map", MimeType = "application/json")]
    [Description("Map state by map ID.")]
    public static async Task<TextResourceContents> Map(
        BridgeInvoker bridge,
        string mapId,
        CancellationToken cancellationToken = default)
    {
        return JsonResource(
            $"arcgispro://map/{Uri.EscapeDataString(mapId)}",
            await bridge.InvokeJsonResourceAsync("map.get_state", new { mapId }, cancellationToken: cancellationToken));
    }

    [McpServerResource(UriTemplate = "arcgispro://layers", Name = "layers", MimeType = "application/json")]
    [Description("Layers in the current ArcGIS Pro project.")]
    public static async Task<TextResourceContents> Layers(
        BridgeInvoker bridge,
        CancellationToken cancellationToken = default)
    {
        return JsonResource(
            "arcgispro://layers",
            await bridge.InvokeJsonResourceAsync("layer.list", cancellationToken: cancellationToken));
    }

    [McpServerResource(UriTemplate = "arcgispro://layer/{layerId}", Name = "layer", MimeType = "application/json")]
    [Description("Layer state by layer ID.")]
    public static async Task<TextResourceContents> Layer(
        BridgeInvoker bridge,
        string layerId,
        CancellationToken cancellationToken = default)
    {
        return JsonResource(
            $"arcgispro://layer/{Uri.EscapeDataString(layerId)}",
            await bridge.InvokeJsonResourceAsync("layer.get_state", new { layerId }, cancellationToken: cancellationToken));
    }

    [McpServerResource(UriTemplate = "arcgispro://layouts", Name = "layouts", MimeType = "application/json")]
    [Description("Layouts in the current ArcGIS Pro project.")]
    public static async Task<TextResourceContents> Layouts(
        BridgeInvoker bridge,
        CancellationToken cancellationToken = default)
    {
        return JsonResource(
            "arcgispro://layouts",
            await bridge.InvokeJsonResourceAsync("layout.list", cancellationToken: cancellationToken));
    }

    [McpServerResource(UriTemplate = "arcgispro://layout/{layoutId}", Name = "layout", MimeType = "application/json")]
    [Description("Layout state by layout ID.")]
    public static async Task<TextResourceContents> Layout(
        BridgeInvoker bridge,
        string layoutId,
        CancellationToken cancellationToken = default)
    {
        return JsonResource(
            $"arcgispro://layout/{Uri.EscapeDataString(layoutId)}",
            await bridge.InvokeJsonResourceAsync("layout.get_state", new { layoutId }, cancellationToken: cancellationToken));
    }

    [McpServerResource(UriTemplate = "arcgispro://artifact/{artifactId}", Name = "artifact", MimeType = "application/octet-stream")]
    [Description("Generated bridge artifact by artifact ID.")]
    public static async Task<BlobResourceContents> Artifact(
        BridgeInvoker bridge,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        var response = await bridge.InvokeAsync("artifact.get", new { artifactId }, cancellationToken: cancellationToken);
        if (!response.Ok)
        {
            throw new McpException(response.Error?.Message ?? $"artifact.error: Failed to retrieve artifact '{artifactId}'.");
        }

        if (!response.Data.HasValue)
        {
            throw new McpException($"artifact.not_found: Artifact '{artifactId}' was not found.");
        }

        var data = response.Data.Value;
        if (!data.TryGetProperty("found", out var foundElement) || !foundElement.GetBoolean())
        {
            throw new McpException($"artifact.not_found: Artifact '{artifactId}' was not found.");
        }

        var artifact = data.GetProperty("artifact");
        var path = artifact.GetProperty("path").GetString();
        var mimeType = artifact.GetProperty("type").GetString() ?? "application/octet-stream";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new McpException($"artifact.not_found: Artifact file for '{artifactId}' was not found on disk.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new BlobResourceContents
        {
            Uri = $"arcgispro://artifact/{Uri.EscapeDataString(artifactId)}",
            MimeType = mimeType,
            Blob = Convert.ToBase64String(bytes)
        };
    }

    [McpServerResource(UriTemplate = "arcgispro://logs/current", Name = "current-logs", MimeType = "application/json")]
    [Description("Recent ArcGIS Pro MCP bridge operations and errors.")]
    public static TextResourceContents CurrentLogs()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISProMcpBridge",
            "logs",
            "addin-bridge.log");

        if (!File.Exists(logPath))
        {
            return new TextResourceContents
            {
                Uri = "arcgispro://logs/current",
                MimeType = "application/json",
                Text = JsonSerializer.Serialize(new
                {
                    exists = false,
                    path = logPath,
                    entries = Array.Empty<object>(),
                    errors = Array.Empty<object>(),
                    checkedUtc = DateTimeOffset.UtcNow
                }, JsonOptions)
            };
        }

        var entries = File.ReadLines(logPath)
            .TakeLast(200)
            .Select(ParseLogEntry)
            .ToArray();
        var errors = entries
            .Where(entry => entry.IsError)
            .ToArray();

        return new TextResourceContents
        {
            Uri = "arcgispro://logs/current",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(new
            {
                exists = true,
                path = logPath,
                count = entries.Length,
                errorCount = errors.Length,
                entries,
                errors,
                checkedUtc = DateTimeOffset.UtcNow
            }, JsonOptions)
        };
    }

    private static TextResourceContents JsonResource(string uri, string json)
    {
        return new TextResourceContents
        {
            Uri = uri,
            MimeType = "application/json",
            Text = json
        };
    }

    private static LogEntry ParseLogEntry(string line)
    {
        var firstSpace = line.IndexOf(' ');
        var timestampText = firstSpace > 0 ? line[..firstSpace] : null;
        var message = firstSpace > 0 ? line[(firstSpace + 1)..] : line;
        DateTimeOffset? timestampUtc = DateTimeOffset.TryParse(timestampText, out var parsed)
            ? parsed
            : null;
        var code = ExtractLogToken(message, "code");
        var okText = ExtractLogToken(message, "ok");
        var isError = message.Contains("error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(okText, "False", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(code) && !string.Equals(code, "ok", StringComparison.OrdinalIgnoreCase));

        return new LogEntry(
            TimestampUtc: timestampUtc,
            Message: message,
            Kind: message.StartsWith("request ", StringComparison.OrdinalIgnoreCase)
                ? "request"
                : message.StartsWith("response ", StringComparison.OrdinalIgnoreCase)
                    ? "response"
                    : "service",
            Id: ExtractLogToken(message, "id"),
            Op: ExtractLogToken(message, "op"),
            Client: ExtractLogToken(message, "client"),
            Ok: bool.TryParse(okText, out var ok) ? ok : null,
            Code: code,
            ElapsedMs: int.TryParse(ExtractLogToken(message, "elapsedMs"), out var elapsedMs) ? elapsedMs : null,
            IsError: isError);
    }

    private static string? ExtractLogToken(string message, string key)
    {
        var marker = $"{key}=";
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = message.IndexOf(' ', start);
        return end < 0 ? message[start..] : message[start..end];
    }

    private sealed record LogEntry(
        DateTimeOffset? TimestampUtc,
        string Message,
        string Kind,
        string? Id,
        string? Op,
        string? Client,
        bool? Ok,
        string? Code,
        int? ElapsedMs,
        bool IsError);
}
