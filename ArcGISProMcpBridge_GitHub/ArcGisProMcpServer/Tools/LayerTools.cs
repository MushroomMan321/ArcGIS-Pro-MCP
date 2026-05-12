using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class LayerTools
{
    [McpServerTool(Name = "layer.list", ReadOnly = true), Description("List layers in the current ArcGIS Pro project, optionally filtered by map.")]
    public static Task<CallToolResult> List(
        BridgeInvoker bridge,
        [Description("Optional map ID filter.")] string? mapId = null,
        [Description("Optional map name filter.")] string? mapName = null,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layer.list", new { mapId, mapName }, timeoutMs, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "layer.get_state", ReadOnly = true), Description("Inspect a layer by ID or name.")]
    public static Task<CallToolResult> GetState(
        BridgeInvoker bridge,
        [Description("Layer ID.")] string? layerId = null,
        [Description("Layer name fallback when ID is not available.")] string? layerName = null,
        [Description("Optional map ID filter.")] string? mapId = null,
        [Description("Optional map name filter.")] string? mapName = null,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layer.get_state", new { layerId, layerName, mapId, mapName }, timeoutMs, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "layer.set_visibility"), Description("Set layer visibility by ID.")]
    public static Task<CallToolResult> SetVisibility(
        BridgeInvoker bridge,
        [Description("Layer ID from layer.list or layer.get_state.")] string layerId,
        [Description("Visibility value to set.")] bool visible,
        [Description("Return intended changes without mutating the layer.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layer.set_visibility", new { visible, layerId }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "layer.set_definition_query"), Description("Set a feature layer definition query by layer ID.")]
    public static Task<CallToolResult> SetDefinitionQuery(
        BridgeInvoker bridge,
        [Description("Layer ID from layer.list or layer.get_state.")] string layerId,
        [Description("SQL definition query. Use an empty string to clear.")] string definitionQuery,
        [Description("Return intended changes without mutating the layer.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layer.set_definition_query", new { definitionQuery, layerId }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "layer.set_transparency"), Description("Set layer transparency by ID. 0 is opaque and 100 is fully transparent.")]
    public static Task<CallToolResult> SetTransparency(
        BridgeInvoker bridge,
        [Description("Layer ID from layer.list or layer.get_state.")] string layerId,
        [Description("Transparency percentage. 0 is opaque and 100 is fully transparent.")] double transparency,
        [Description("Return intended changes without mutating the layer.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layer.set_transparency", new { layerId, transparency }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "layer.apply_symbology_from_layer"), Description("Apply symbology from a .lyrx layer file by layer ID.")]
    public static Task<CallToolResult> ApplySymbologyFromLayer(
        BridgeInvoker bridge,
        [Description("Target layer ID from layer.list or layer.get_state.")] string layerId,
        [Description("Symbology .lyrx path.")] string symbologyLayerPath,
        [Description("Return intended changes without mutating the layer.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layer.apply_symbology_from_layer", new { layerId, symbologyLayerPath }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }
}
