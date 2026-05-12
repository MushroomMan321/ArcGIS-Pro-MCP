using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class GeoprocessingTools
{
    [McpServerTool(Name = "geoprocessing.execute_tool"), Description("Execute a guarded ArcGIS Pro geoprocessing tool.")]
    public static Task<CallToolResult> ExecuteTool(
        BridgeInvoker bridge,
        [Description("Geoprocessing tool name, for example management.Buffer.")] string toolName,
        [Description("Tool parameters as a JSON object or array.")] object? parameters = null,
        [Description("Geoprocessing environments as a JSON object.")] object? environments = null,
        [Description("Add output layers to the active map when supported.")] bool addOutputsToMap = false,
        [Description("Explicitly allow denylisted destructive tools that mutate or delete input data.")] bool allowDestructive = false,
        [Description("Required with allowDestructive=true when destructive geoprocessing confirmation is enabled.")] bool confirmDestructive = false,
        [Description("Return intended changes without executing.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds; also cancels the GP job when reached.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync(
            "geoprocessing.execute_tool",
            new { toolName, parameters, environments, addOutputsToMap, allowDestructive, confirmDestructive },
            timeoutMs,
            dryRun,
            cancellationToken: cancellationToken);
    }
}
