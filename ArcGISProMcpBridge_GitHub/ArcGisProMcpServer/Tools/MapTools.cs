using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class MapTools
{
    [McpServerTool(Name = "map.list", ReadOnly = true), Description("List maps in the current ArcGIS Pro project.")]
    public static Task<CallToolResult> List(
        BridgeInvoker bridge,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("map.list", timeoutMs: timeoutMs, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "map.activate"), Description("Activate a map by ID.")]
    public static Task<CallToolResult> Activate(
        BridgeInvoker bridge,
        [Description("Map ID from map.list or project.get_current.")] string mapId,
        [Description("Return intended changes without activating the map.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("map.activate", new { mapId }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "map.get_state", ReadOnly = true), Description("Inspect a map by ID or name.")]
    public static Task<CallToolResult> GetState(
        BridgeInvoker bridge,
        [Description("Map ID.")] string? mapId = null,
        [Description("Map name fallback when ID is not available.")] string? mapName = null,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("map.get_state", new { mapId, mapName }, timeoutMs, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "map.set_extent"), Description("Set the visible extent for an open map view by map ID.")]
    public static Task<CallToolResult> SetExtent(
        BridgeInvoker bridge,
        [Description("Map ID from map.list or project.get_current.")] string mapId,
        [Description("Extent minimum X.")] double xMin,
        [Description("Extent minimum Y.")] double yMin,
        [Description("Extent maximum X.")] double xMax,
        [Description("Extent maximum Y.")] double yMax,
        [Description("Optional spatial reference WKID. Defaults to the map spatial reference.")] int? wkid = null,
        [Description("Return intended changes without changing the visible map extent.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("map.set_extent", new { mapId, xMin, yMin, xMax, yMax, wkid }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "map.zoom_to_layer"), Description("Zoom an open map view to a layer by map and layer IDs.")]
    public static Task<CallToolResult> ZoomToLayer(
        BridgeInvoker bridge,
        [Description("Map ID from map.list or project.get_current.")] string mapId,
        [Description("Layer ID from layer.list or layer.get_state.")] string layerId,
        [Description("Use only selected features when supported.")] bool selectionOnly = false,
        [Description("Return intended changes without changing the visible map extent.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("map.zoom_to_layer", new { mapId, layerId, selectionOnly }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "map.set_basemap"), Description("Set a map basemap from the guarded allowlist.")]
    public static Task<CallToolResult> SetBasemap(
        BridgeInvoker bridge,
        [Description("Map ID from map.list or project.get_current.")] string mapId,
        [Description("Allowlisted basemap name such as Gray, DarkGray, Topographic, Streets, Satellite, Oceans, OpenStreetMap, or None.")] string basemap,
        [Description("Return intended changes without mutating the map.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("map.set_basemap", new { mapId, basemap }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }
}
