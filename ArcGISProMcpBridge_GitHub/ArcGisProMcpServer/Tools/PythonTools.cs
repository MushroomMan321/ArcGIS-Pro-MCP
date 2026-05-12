using System.ComponentModel;
using ArcGisProMcpServer.Ipc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Tools;

[McpServerToolType]
public static class PythonTools
{
    [McpServerTool(Name = "python.run_arcpy_script"), Description("Run an allowed ArcPy .py script through ArcGIS Pro geoprocessing so CURRENT project workflows can work.")]
    public static Task<CallToolResult> RunArcPyScript(
        BridgeInvoker bridge,
        [Description("Path to an existing .py script under an allowed root.")] string scriptPath,
        [Description("Optional script arguments as a JSON array or object. Arrays become sys.argv items; objects become --key value pairs.")] object? arguments = null,
        [Description("Optional working directory under an allowed root. Defaults to the script folder.")] string? workingDirectory = null,
        [Description("Optional directory to scan for generated files. Defaults to the working directory.")] string? outputDirectory = null,
        [Description("Parse the script with ast.parse but do not execute it.")] bool syntaxOnly = false,
        [Description("Required for non-syntax-only script execution when script execution confirmation is enabled.")] bool confirmScriptExecution = false,
        [Description("Return intended checks without running Python.")] bool dryRun = false,
        [Description("Optional bridge request timeout in milliseconds; also cancels the geoprocessing script tool when reached.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        return bridge.InvokeToolAsync(
            "python.run_arcpy_script",
            new { scriptPath, arguments, workingDirectory, outputDirectory, syntaxOnly, confirmScriptExecution },
            timeoutMs,
            dryRun,
            includeImageArtifacts: true,
            cancellationToken: cancellationToken);
    }
}
