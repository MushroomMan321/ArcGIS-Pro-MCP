using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class ProTools
{
    [McpServerTool(Name = "pro_ping", ReadOnly = true), Description("Ping test for the MCP server only. Does not require ArcGIS Pro.")]
    public static object Ping()
    {
        return new
        {
            ok = true,
            message = "pong",
            checkedUtc = DateTimeOffset.UtcNow
        };
    }

    [McpServerTool(Name = "pro_health", ReadOnly = true), Description("Get ArcGIS Pro bridge health and current project/view status.")]
    public static Task<CallToolResult> Health(
        BridgeInvoker bridge,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("pro.health", timeoutMs: timeoutMs, cancellationToken: cancellationToken);
    }
}
