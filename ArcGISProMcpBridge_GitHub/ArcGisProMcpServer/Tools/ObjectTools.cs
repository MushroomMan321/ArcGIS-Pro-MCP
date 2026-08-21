using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class ObjectTools
{
    [McpServerTool(Name = "object_registry", ReadOnly = true), Description("List the stable session object registry for the current ArcGIS Pro project.")]
    public static Task<CallToolResult> Registry(
        BridgeInvoker bridge,
        [Description("Optional bridge request timeout in milliseconds.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync("object.registry", timeoutMs: timeoutMs, cancellationToken: cancellationToken);
    }
}
