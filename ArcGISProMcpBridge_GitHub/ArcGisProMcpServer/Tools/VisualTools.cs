using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class VisualTools
{
    [McpServerTool(Name = "visual.capture_active_view", ReadOnly = true), Description("Capture the active ArcGIS Pro view as an image artifact.")]
    public static Task<CallToolResult> CaptureActiveView(
        BridgeInvoker bridge,
        [Description("Optional output image width in pixels.")] int? width = null,
        [Description("Optional output image height in pixels.")] int? height = null,
        [Description("Metadata DPI to associate with the captured thumbnail.")] int? dpi = null,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("visual.capture_active_view", new { width, height, dpi }, timeoutMs, includeImageArtifacts: true, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "visual.export_active_map", ReadOnly = true), Description("Export the active ArcGIS Pro map view to a PNG image artifact.")]
    public static Task<CallToolResult> ExportActiveMap(
        BridgeInvoker bridge,
        [Description("Output image width in pixels.")] int? width = null,
        [Description("Output image height in pixels.")] int? height = null,
        [Description("Export DPI.")] int? dpi = null,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("visual.export_active_map", new { width, height, dpi }, timeoutMs, includeImageArtifacts: true, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "visual.export_layout_preview", ReadOnly = true), Description("Export a layout preview as an image artifact.")]
    public static Task<CallToolResult> ExportLayoutPreview(
        BridgeInvoker bridge,
        [Description("Layout ID.")] string? layoutId = null,
        [Description("Layout name fallback when ID is not available.")] string? layoutName = null,
        [Description("Preview DPI.")] int? dpi = null,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("visual.export_layout_preview", new { layoutId, layoutName, dpi }, timeoutMs, includeImageArtifacts: true, cancellationToken: cancellationToken);
    }
}
