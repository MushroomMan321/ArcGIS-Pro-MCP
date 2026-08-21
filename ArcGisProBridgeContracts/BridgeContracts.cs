using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcGisProBridgeContracts;

public static class BridgeDefaults
{
    public const string PipeName = "ArcGisProMcpBridge";
    public const int DefaultTimeoutMs = 10000;
    public const string ClientName = "ArcGisProMcpServer";
}

public sealed record BridgeRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("args")] JsonObjectMap? Args,
    [property: JsonPropertyName("timeoutMs")] int TimeoutMs,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("client")] string Client,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc)
{
    public static BridgeRequest Create(
        string op,
        JsonObjectMap? args = null,
        int timeoutMs = BridgeDefaults.DefaultTimeoutMs,
        bool dryRun = false,
        string client = BridgeDefaults.ClientName)
    {
        return new BridgeRequest(
            Guid.NewGuid().ToString("n"),
            op,
            args,
            timeoutMs,
            dryRun,
            client,
            DateTimeOffset.UtcNow);
    }
}

public sealed record BridgeResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("messages")] IReadOnlyList<string> Messages,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<BridgeArtifact> Artifacts,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("error")] BridgeError? Error)
{
    public static BridgeResponse Success(
        string id,
        object? data,
        long elapsedMs,
        IReadOnlyList<string>? messages = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<BridgeArtifact>? artifacts = null)
    {
        return new BridgeResponse(
            id,
            true,
            JsonSerializer.SerializeToElement(data),
            warnings ?? Array.Empty<string>(),
            messages ?? Array.Empty<string>(),
            artifacts ?? Array.Empty<BridgeArtifact>(),
            elapsedMs,
            null);
    }

    public static BridgeResponse Failure(
        string id,
        string code,
        string message,
        long elapsedMs,
        IReadOnlyList<string>? messages = null,
        IReadOnlyList<string>? warnings = null,
        JsonElement? details = null,
        bool recoverable = true)
    {
        return new BridgeResponse(
            id,
            false,
            null,
            warnings ?? Array.Empty<string>(),
            messages ?? Array.Empty<string>(),
            Array.Empty<BridgeArtifact>(),
            elapsedMs,
            new BridgeError(code, message, details, recoverable));
    }
}

public sealed record BridgeError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] JsonElement? Details,
    [property: JsonPropertyName("recoverable")] bool Recoverable);

public sealed record BridgeArtifact(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("sourceObjectId")] string? SourceObjectId = null,
    [property: JsonPropertyName("sourceObjectKind")] string? SourceObjectKind = null,
    [property: JsonPropertyName("sourceObjectName")] string? SourceObjectName = null,
    [property: JsonPropertyName("width")] int? Width = null,
    [property: JsonPropertyName("height")] int? Height = null,
    [property: JsonPropertyName("dpi")] int? Dpi = null);

public sealed record ProHealth(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("busy")] bool Busy,
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("proName")] string? ProName,
    [property: JsonPropertyName("projectName")] string? ProjectName,
    [property: JsonPropertyName("projectPath")] string? ProjectPath,
    [property: JsonPropertyName("homeFolder")] string? HomeFolder,
    [property: JsonPropertyName("defaultGeodatabase")] string? DefaultGeodatabase,
    [property: JsonPropertyName("activeMap")] string? ActiveMap,
    [property: JsonPropertyName("activeView")] string? ActiveView,
    [property: JsonPropertyName("activeLayout")] string? ActiveLayout,
    [property: JsonPropertyName("dirty")] bool Dirty,
    [property: JsonPropertyName("serviceStartedUtc")] DateTimeOffset ServiceStartedUtc,
    [property: JsonPropertyName("checkedUtc")] DateTimeOffset CheckedUtc);

[JsonConverter(typeof(JsonObjectMapConverter))]
public sealed class JsonObjectMap : Dictionary<string, JsonElement>
{
    public JsonObjectMap()
    {
    }

    public JsonObjectMap(IDictionary<string, object?> values)
    {
        foreach (var item in values)
        {
            this[item.Key] = JsonSerializer.SerializeToElement(item.Value);
        }
    }

    public string? GetString(string key)
    {
        return TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
    }

    public bool? GetBoolean(string key)
    {
        return TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    public int? GetInt32(string key)
    {
        if (!TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    public double? GetDouble(string key)
    {
        if (!TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
    }
}

internal sealed class JsonObjectMapConverter : JsonConverter<JsonObjectMap>
{
    public override JsonObjectMap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Bridge args must be a JSON object.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var map = new JsonObjectMap();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            map[property.Name] = property.Value.Clone();
        }

        return map;
    }

    public override void Write(Utf8JsonWriter writer, JsonObjectMap value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var item in value)
        {
            writer.WritePropertyName(item.Key);
            item.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

public static class BridgeRequestValidator
{
    public static IReadOnlyList<string> Validate(BridgeRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("Request body was empty.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.Id))
        {
            errors.Add("Request id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Op))
        {
            errors.Add("Operation name is required.");
        }

        if (request.TimeoutMs < 0)
        {
            errors.Add("timeoutMs must be zero or a positive integer.");
        }

        if (string.IsNullOrWhiteSpace(request.Client))
        {
            errors.Add("Client name is required.");
        }

        return errors;
    }
}
