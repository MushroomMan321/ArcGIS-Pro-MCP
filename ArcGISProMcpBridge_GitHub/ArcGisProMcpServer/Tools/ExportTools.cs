using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class ExportTools
{
    [McpServerTool(Name = "export.layout"), Description("Export a layout to PNG or PDF.")]
    public static Task<CallToolResult> ExportLayout(
        BridgeInvoker bridge,
        [Description("Output path.")] string outputPath,
        [Description("Layout ID.")] string? layoutId = null,
        [Description("Layout name fallback when ID is not available.")] string? layoutName = null,
        [Description("Export format, usually PNG or PDF.")] string format = "PDF",
        [Description("Export DPI.")] int? dpi = null,
        [Description("Embed fonts for PDF exports.")] bool embedFonts = true,
        [Description("Include georeference information where supported by the export format.")] bool georeference = true,
        [Description("Overwrite output path when supported.")] bool overwrite = false,
        [Description("Required when overwrite=true and overwrite confirmation is enabled.")] bool confirmOverwrite = false,
        [Description("Return intended changes without exporting.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("export.layout", new { layoutId, layoutName, outputPath, format, dpi, embedFonts, georeference, overwrite, confirmOverwrite }, timeoutMs, dryRun, true, cancellationToken);
    }
}
