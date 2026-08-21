using System.Text.Json;

namespace ArcGisProMcpServer.Tools;

/// <summary>
/// Normalizes loosely typed MCP tool arguments before they are forwarded to the add-in.
/// Some MCP clients deliver JSON array/object arguments as a JSON-encoded string, which the
/// add-in would otherwise treat as a single positional string value.
/// </summary>
public static class JsonArgumentNormalizer
{
    public static object? Normalize(object? value)
    {
        return value switch
        {
            null => null,
            string text => ParseEmbeddedJson(text) ?? value,
            JsonElement { ValueKind: JsonValueKind.String } element => ParseEmbeddedJson(element.GetString()) ?? value,
            _ => value
        };
    }

    private static object? ParseEmbeddedJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.TrimStart();
        if (trimmed[0] is not ('[' or '{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
