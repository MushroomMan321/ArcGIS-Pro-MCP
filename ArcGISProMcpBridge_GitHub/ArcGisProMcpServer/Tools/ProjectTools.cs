using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "project.get_current", ReadOnly = true), Description("Inspect the current ArcGIS Pro project.")]
    public static Task<CallToolResult> GetCurrent(
        BridgeInvoker bridge,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("project.get_current", timeoutMs: timeoutMs, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "project.save"), Description("Save the current ArcGIS Pro project.")]
    public static Task<CallToolResult> Save(
        BridgeInvoker bridge,
        [Description("Required for non-dry-run saves when save confirmation is enabled.")] bool confirmSave = false,
        [Description("Return intended changes without saving.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("project.save", new { confirmSave }, timeoutMs: timeoutMs, dryRun: dryRun, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "project.save_copy"), Description("Save a copy of the current ArcGIS Pro project.")]
    public static Task<CallToolResult> SaveCopy(
        BridgeInvoker bridge,
        [Description("Output .aprx path.")] string path,
        [Description("Overwrite output .aprx when it already exists.")] bool overwrite = false,
        [Description("Required for non-dry-run save-copy operations when save confirmation is enabled.")] bool confirmSave = false,
        [Description("Required when overwrite=true and overwrite confirmation is enabled.")] bool confirmOverwrite = false,
        [Description("Return intended changes without writing a copy.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("project.save_copy", new { path, overwrite, confirmSave, confirmOverwrite }, timeoutMs, dryRun, cancellationToken: cancellationToken);
    }
}
