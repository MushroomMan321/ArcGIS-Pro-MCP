using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class LayoutTools
{
    [McpServerTool(Name = "layout_list", ReadOnly = true), Description("List layouts in the current ArcGIS Pro project.")]
    public static Task<CallToolResult> List(
        BridgeInvoker bridge,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layout.list", timeoutMs: timeoutMs, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "layout_get_state", ReadOnly = true), Description("Inspect a layout by ID or name.")]
    public static Task<CallToolResult> GetState(
        BridgeInvoker bridge,
        [Description("Layout ID.")] string? layoutId = null,
        [Description("Layout name fallback when ID is not available.")] string? layoutName = null,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layout.get_state", new { layoutId, layoutName }, timeoutMs, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "layout_set_text"), Description("Set layout text element content by layout and element IDs.")]
    public static Task<CallToolResult> SetText(
        BridgeInvoker bridge,
        [Description("Layout ID from layout_list or layout_get_state.")] string layoutId,
        [Description("Text element ID.")] string elementId,
        [Description("Replacement text.")] string text,
        [Description("Return intended changes without mutating the layout.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layout.set_text", new { layoutId, elementId, text }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "layout_set_map_frame_camera"), Description("Set a layout map frame extent, scale, or heading by layout and map frame IDs.")]
    public static Task<CallToolResult> SetMapFrameCamera(
        BridgeInvoker bridge,
        [Description("Layout ID from layout_list or layout_get_state.")] string layoutId,
        [Description("Map frame ID from layout_get_state or object_registry.")] string mapFrameId,
        [Description("Minimum X coordinate for the target extent. Required with yMin, xMax, and yMax when setting extent.")] double? xMin = null,
        [Description("Minimum Y coordinate for the target extent. Required with xMin, xMax, and yMax when setting extent.")] double? yMin = null,
        [Description("Maximum X coordinate for the target extent. Required with xMin, yMin, and yMax when setting extent.")] double? xMax = null,
        [Description("Maximum Y coordinate for the target extent. Required with xMin, yMin, and xMax when setting extent.")] double? yMax = null,
        [Description("Optional WKID for the extent spatial reference. Defaults to the map frame map spatial reference.")] int? wkid = null,
        [Description("Optional target map scale.")] double? scale = null,
        [Description("Optional target heading/rotation in degrees.")] double? heading = null,
        [Description("Return intended changes without mutating the layout.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync(
            "layout.set_map_frame_camera",
            new { layoutId, mapFrameId, xMin, yMin, xMax, yMax, wkid, scale, heading },
            timeoutMs,
            dryRun,
            cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "layout_set_surround_visibility"), Description("Set visibility for an existing north arrow or scale bar element by layout and element IDs.")]
    public static Task<CallToolResult> SetSurroundVisibility(
        BridgeInvoker bridge,
        [Description("Layout ID from layout_list or layout_get_state.")] string layoutId,
        [Description("North arrow or scale bar element ID from layout_get_state or object_registry.")] string elementId,
        [Description("New visibility state.")] bool visible,
        [Description("Return intended changes without mutating the layout.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("layout.set_surround_visibility", new { layoutId, elementId, visible }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }
}
