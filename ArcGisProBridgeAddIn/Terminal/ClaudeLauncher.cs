using System.IO;
using System.Text;
using System.Text.Json;
using ArcGisProBridgeContracts;

namespace ArcGisProBridgeAddIn.Terminal;

/// <summary>
/// Raised when the pane cannot be started. The message is shown to the user
/// verbatim, so it should always say what to do next.
/// </summary>
internal sealed class ClaudeLaunchException : Exception
{
    public ClaudeLaunchException(string message)
        : base(message)
    {
    }
}

internal sealed record ClaudeLaunchPlan(
    string CommandLine,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> EnvironmentOverrides,
    string ExecutablePath,
    string McpConfigPath,
    string ServerPath);

/// <summary>
/// Works out how to start Claude Code so that it comes up already connected to
/// the running ArcGIS Pro session: the correct executable, a generated MCP
/// config pointing at the published bridge server, and a working directory
/// inside the open project.
/// </summary>
internal static class ClaudeLauncher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] ExecutableNames =
    {
        "claude.cmd",
        "claude.exe",
        "claude.bat"
    };

    public static ClaudeLaunchPlan CreatePlan(BridgeConfiguration config, string workingDirectory)
    {
        var options = config.ClaudePane;
        var executable = ResolveExecutable(options);
        var server = ResolveServer(options);
        var mcpConfigPath = WriteMcpConfig(config, server);

        var arguments = new List<string> { "--mcp-config", mcpConfigPath };
        if (options.StrictMcpConfig)
        {
            arguments.Add("--strict-mcp-config");
        }

        arguments.AddRange(options.Arguments);

        return new ClaudeLaunchPlan(
            BuildCommandLine(executable, arguments),
            workingDirectory,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                // Point the server process the pane spawns at the same bridge
                // config and pipe the add-in itself loaded, so the pane cannot
                // end up talking to a differently configured bridge.
                ["ARCGIS_PRO_MCP_CONFIG"] = config.SourcePath,
                ["ARCGIS_PRO_MCP_PIPE"] = config.PipeName
            },
            executable,
            mcpConfigPath,
            server);
    }

    /// <summary>
    /// Finds the Claude Code executable, preferring an explicit setting, then
    /// PATH, then the locations the npm and native installers use.
    /// </summary>
    public static string ResolveExecutable(ClaudePaneOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            var configured = Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.ExecutablePath));
            if (!File.Exists(configured))
            {
                throw new ClaudeLaunchException(
                    $"Claude Code was not found at the configured path:\n{configured}\n\n" +
                    "Correct claudePane.executablePath in the bridge configuration file.");
            }

            return configured;
        }

        foreach (var candidate in EnumerateCandidatePaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new ClaudeLaunchException(
            "Claude Code was not found.\n\n" +
            "Install it with \"npm install -g @anthropic-ai/claude-code\", or set " +
            "claudePane.executablePath in the bridge configuration file to the full " +
            "path of the claude executable.");
    }

    private static IEnumerable<string> EnumerateCandidatePaths()
    {
        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in ExecutableNames)
            {
                // A malformed PATH entry should skip that entry, not abort the search.
                var combined = TryCombine(directory.Trim().Trim('"'), name);
                if (combined is not null)
                {
                    yield return combined;
                }
            }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var name in ExecutableNames)
        {
            var combined = TryCombine(Path.Combine(appData, "npm"), name);
            if (combined is not null)
            {
                yield return combined;
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in ExecutableNames)
        {
            var combined = TryCombine(Path.Combine(profile, ".local", "bin"), name);
            if (combined is not null)
            {
                yield return combined;
            }
        }
    }

    private static string? TryCombine(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            return Path.Combine(directory, name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string ResolveServer(ClaudePaneOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ServerPath))
        {
            var configured = Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.ServerPath));
            if (!File.Exists(configured))
            {
                throw new ClaudeLaunchException(
                    $"The MCP server was not found at the configured path:\n{configured}\n\n" +
                    "Correct claudePane.serverPath in the bridge configuration file.");
            }

            return configured;
        }

        var candidates = new[]
        {
            Path.Combine(BridgeConfiguration.GetDefaultConfigDirectory(), "server", "ArcGisProMcpServer.exe"),
            Path.Combine(AppContext.BaseDirectory, "ArcGisProMcpServer.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new ClaudeLaunchException(
            "The ArcGIS Pro MCP server was not found.\n\n" +
            "Publish it with scripts\\publish-server.ps1, then either copy the output to\n" +
            $"{candidates[0]}\n" +
            "or set claudePane.serverPath in the bridge configuration file.");
    }

    /// <summary>
    /// Writes the MCP config handed to Claude Code. It matches the shape
    /// scripts/publish-server.ps1 emits, so a session started from the pane and
    /// one started from an external client see the same server definition.
    /// </summary>
    private static string WriteMcpConfig(BridgeConfiguration config, string serverPath)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ARCGIS_PRO_MCP_PIPE"] = config.PipeName
        };

        if (!string.IsNullOrWhiteSpace(config.SourcePath))
        {
            environment["ARCGIS_PRO_MCP_CONFIG"] = config.SourcePath;
        }

        var document = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["arcgis-pro"] = new
                {
                    type = "stdio",
                    command = serverPath,
                    args = Array.Empty<string>(),
                    env = environment
                }
            }
        };

        var directory = Path.Combine(BridgeConfiguration.GetDefaultConfigDirectory(), "pane");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "mcp-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions), Encoding.UTF8);
        return path;
    }

    /// <summary>
    /// Builds the command line for CreateProcess. npm installs Claude Code as a
    /// .cmd shim, which CreateProcess cannot execute directly, so those are run
    /// through the command interpreter.
    /// </summary>
    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteArgument(executable));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(QuoteArgument(argument));
        }

        var extension = Path.GetExtension(executable);
        if (!extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return builder.ToString();
        }

        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(comSpec))
        {
            comSpec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        }

        // /d skips AutoRun scripts, and /s with the whole command wrapped in one
        // pair of quotes tells cmd to strip only those outer quotes and take the
        // rest verbatim, which is the only form that survives quoted paths.
        return $"{QuoteArgument(comSpec)} /d /s /c \"{builder}\"";
    }

    /// <summary>
    /// Quotes a single argument using the rules the Windows command line parser
    /// applies in reverse: backslashes are only special immediately before a
    /// quote or the closing quote.
    /// </summary>
    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        for (var index = 0; index < argument.Length; index++)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == argument.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[index]);
            }
        }

        return builder.Append('"').ToString();
    }
}
