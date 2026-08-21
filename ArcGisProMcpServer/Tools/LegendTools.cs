using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class LegendTools
{
    [McpServerTool(Name = "legend_get_state", ReadOnly = true), Description("Inspect an existing layout legend by layout and legend element IDs.")]
    public static Task<CallToolResult> GetState(
        BridgeInvoker bridge,
        [Description("Legend element ID from layout_get_state or object_registry.")] string elementId,
        [Description("Layout ID from layout_list or layout_get_state.")] string? layoutId = null,
        [Description("Layout name fallback when ID is not available.")] string? layoutName = null,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("legend.get_state", new { layoutId, layoutName, elementId }, timeoutMs, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "legend_set_visibility"), Description("Set visibility for an existing legend element.")]
    public static Task<CallToolResult> SetVisibility(
        BridgeInvoker bridge,
        [Description("Layout ID from layout_list or layout_get_state.")] string layoutId,
        [Description("Legend element ID from layout_get_state or object_registry.")] string elementId,
        [Description("New legend visibility state.")] bool visible,
        [Description("Return intended changes without mutating the layout.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("legend.set_visibility", new { layoutId, elementId, visible }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "legend_set_layout"), Description("Conservatively update legend placement, size, columns, fitting strategy, or title settings.")]
    public static Task<CallToolResult> SetLayout(
        BridgeInvoker bridge,
        [Description("Layout ID from layout_list or layout_get_state.")] string layoutId,
        [Description("Legend element ID from layout_get_state or object_registry.")] string elementId,
        [Description("Legend X position in page units.")] double? x = null,
        [Description("Legend Y position in page units.")] double? y = null,
        [Description("Legend width in page units.")] double? width = null,
        [Description("Legend height in page units.")] double? height = null,
        [Description("Legend column count, 1-12.")] int? columns = null,
        [Description("Fitting strategy: AdjustColumns, AdjustColumnsAndSize, AdjustFontSize, AdjustFrame, or ManualColumns.")] string? fittingStrategy = null,
        [Description("Show or hide the legend title.")] bool? showTitle = null,
        [Description("Legend title text.")] string? title = null,
        [Description("Minimum font size in points for fitting strategies that shrink text.")] double? minFontSize = null,
        [Description("Balance legend columns where supported.")] bool? balanceColumns = null,
        [Description("Make legend columns the same width where supported.")] bool? makeColumnsSameWidth = null,
        [Description("Return intended changes without mutating the layout.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync(
            "legend.set_layout",
            new { layoutId, elementId, x, y, width, height, columns, fittingStrategy, showTitle, title, minFontSize, balanceColumns, makeColumnsSameWidth },
            timeoutMs,
            dryRun,
            cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "legend_set_items"), Description("Show or hide existing legend items by layer ID or legend item name, without changing map layer visibility.")]
    public static Task<CallToolResult> SetItems(
        BridgeInvoker bridge,
        [Description("Layout ID from layout_list or layout_get_state.")] string layoutId,
        [Description("Legend element ID from layout_get_state or object_registry.")] string elementId,
        [Description("Comma-separated layer IDs to show in the legend.")] string? showLayerIdsCsv = null,
        [Description("Comma-separated layer IDs to hide in the legend. Requires allowHideThematic=true.")] string? hideLayerIdsCsv = null,
        [Description("Comma-separated legend item names to show.")] string? showItemNamesCsv = null,
        [Description("Comma-separated legend item names to hide. Requires allowHideThematic=true.")] string? hideItemNamesCsv = null,
        [Description("Explicitly allow hiding thematic legend items after confirming map meaning is preserved.")] bool allowHideThematic = false,
        [Description("Return intended changes without mutating the layout.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync(
            "legend.set_items",
            new { layoutId, elementId, showLayerIdsCsv, hideLayerIdsCsv, showItemNamesCsv, hideItemNamesCsv, allowHideThematic },
            timeoutMs,
            dryRun,
            cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "legend_rename_items"), Description("Rename existing CIM legend item labels without changing layer names or renderer class labels.")]
    public static Task<CallToolResult> RenameItems(
        BridgeInvoker bridge,
        [Description("Layout ID from layout_list or layout_get_state.")] string layoutId,
        [Description("Legend element ID from layout_get_state or object_registry.")] string elementId,
        [Description("Exact legend item name to replace.")] string? exactName = null,
        [Description("Legend item name substring to match.")] string? contains = null,
        [Description("Replacement label for exactName or contains.")] string? replacement = null,
        [Description("Find text to replace inside matching legend item names.")] string? find = null,
        [Description("Replacement text for find.")] string? replace = null,
        [Description("Return intended changes without mutating the layout.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync(
            "legend.rename_items",
            new { layoutId, elementId, exactName, contains, replacement, find, replace },
            timeoutMs,
            dryRun,
            cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "legend_apply_compact_style"), Description("Apply a restrained compact legend style without hiding items.")]
    public static Task<CallToolResult> ApplyCompactStyle(
        BridgeInvoker bridge,
        [Description("Layout ID from layout_list or layout_get_state.")] string layoutId,
        [Description("Legend element ID from layout_get_state or object_registry.")] string elementId,
        [Description("Optional column count. Defaults to a conservative count based on item count.")] int? columns = null,
        [Description("Legend item text size in points.")] double? fontSize = null,
        [Description("Minimum text size in points for fitting.")] double? minFontSize = null,
        [Description("Patch width in points.")] double? patchWidth = null,
        [Description("Patch height in points.")] double? patchHeight = null,
        [Description("Optional title visibility override.")] bool? showTitle = null,
        [Description("Return intended changes without mutating the layout.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync(
            "legend.apply_compact_style",
            new { layoutId, elementId, columns, fontSize, minFontSize, patchWidth, patchHeight, showTitle },
            timeoutMs,
            dryRun,
            cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "legend_qa_preview"), Description("Export before/after layout previews around one conservative legend compacting edit.")]
    public static Task<CallToolResult> QaPreview(
        BridgeInvoker bridge,
        [Description("Layout ID from layout_list or layout_get_state.")] string layoutId,
        [Description("Legend element ID from layout_get_state or object_registry.")] string elementId,
        [Description("Preview export DPI.")] int? dpi = null,
        [Description("Inspect and plan without exporting or mutating.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync(
            "legend.qa_preview",
            new { layoutId, elementId, dpi },
            timeoutMs,
            dryRun,
            includeImageArtifacts: true,
            cancellationToken: cancellationToken);
    }
}
