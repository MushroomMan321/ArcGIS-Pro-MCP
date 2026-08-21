using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace ArcGisProBridgeContracts;

public sealed class BridgeConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public string PipeName { get; set; } = BridgeDefaults.PipeName;

    public List<string> AllowedRoots { get; set; } = new();

    public string? ArtifactDirectory { get; set; }

    public List<string> EnabledToolGroups { get; set; } = new()
    {
        "core",
        "read",
        "map",
        "layer",
        "layout",
        "legend",
        "visual",
        "export",
        "project",
        "geoprocessing",
        "python",
        "diagnostics"
    };

    public BridgeDestructiveOperationPolicy DestructiveOperations { get; set; } = new();

    public BridgeConfirmationPolicy Confirmations { get; set; } = new();

    public BridgeTimeoutPolicy Timeouts { get; set; } = new();

    public string? AuditLogPath { get; set; }

    [JsonIgnore]
    public string? SourcePath { get; set; }

    public static BridgeConfiguration Load()
    {
        var config = new BridgeConfiguration();
        var path = FindConfigPath();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var loaded = JsonSerializer.Deserialize<BridgeConfiguration>(File.ReadAllText(path), JsonOptions);
            if (loaded is not null)
            {
                config = loaded;
                config.SourcePath = path;
            }
        }

        ApplyEnvironmentOverrides(config);
        config.Normalize();
        return config;
    }

    public string GetAuditLogPath()
    {
        if (!string.IsNullOrWhiteSpace(AuditLogPath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(AuditLogPath));
        }

        return Path.Combine(GetDefaultConfigDirectory(), "logs", "audit.jsonl");
    }

    public string[] GetConfiguredAllowedRoots()
    {
        return AllowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string? GetArtifactDirectory()
    {
        return string.IsNullOrWhiteSpace(ArtifactDirectory)
            ? null
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(ArtifactDirectory));
    }

    public bool IsOperationEnabled(string op)
    {
        var group = GetToolGroup(op);
        if (string.IsNullOrWhiteSpace(group))
        {
            return true;
        }

        return EnabledToolGroups.Count == 0
            || EnabledToolGroups.Contains(group, StringComparer.OrdinalIgnoreCase);
    }

    public static string GetToolGroup(string op)
    {
        if (op.StartsWith("pro.", StringComparison.OrdinalIgnoreCase)
            || op.StartsWith("object.", StringComparison.OrdinalIgnoreCase)
            || op.StartsWith("artifact.", StringComparison.OrdinalIgnoreCase))
        {
            return "core";
        }

        if (op.StartsWith("project.get_", StringComparison.OrdinalIgnoreCase)
            || op is "map.list" or "map.get_state" or "layer.list" or "layer.get_state" or "layout.list" or "layout.get_state"
            || op.StartsWith("legend.get_", StringComparison.OrdinalIgnoreCase))
        {
            return "read";
        }

        if (op.StartsWith("map.", StringComparison.OrdinalIgnoreCase))
        {
            return "map";
        }

        if (op.StartsWith("layer.", StringComparison.OrdinalIgnoreCase))
        {
            return "layer";
        }

        if (op.StartsWith("layout.", StringComparison.OrdinalIgnoreCase))
        {
            return "layout";
        }

        if (op.StartsWith("legend.", StringComparison.OrdinalIgnoreCase))
        {
            return "legend";
        }

        if (op.StartsWith("visual.", StringComparison.OrdinalIgnoreCase))
        {
            return "visual";
        }

        if (op.StartsWith("export.", StringComparison.OrdinalIgnoreCase))
        {
            return "export";
        }

        if (op.StartsWith("project.", StringComparison.OrdinalIgnoreCase))
        {
            return "project";
        }

        if (op.StartsWith("geoprocessing.", StringComparison.OrdinalIgnoreCase))
        {
            return "geoprocessing";
        }

        if (op.StartsWith("python.", StringComparison.OrdinalIgnoreCase))
        {
            return "python";
        }

        if (op.StartsWith("bridge.diagnostics.", StringComparison.OrdinalIgnoreCase))
        {
            return "diagnostics";
        }

        return "core";
    }

    public static string GetDefaultConfigDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISProMcpBridge");
    }

    public static string GetDefaultConfigPath()
    {
        return Path.Combine(GetDefaultConfigDirectory(), "config.json");
    }

    private static string? FindConfigPath()
    {
        var configured = Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_CONFIG");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }

        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "arcgis-pro-mcp.config.json"),
            Path.Combine(AppContext.BaseDirectory, "arcgis-pro-mcp.config.json"),
            GetDefaultConfigPath()
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void ApplyEnvironmentOverrides(BridgeConfiguration config)
    {
        var pipe = Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_PIPE");
        if (!string.IsNullOrWhiteSpace(pipe))
        {
            config.PipeName = pipe;
        }

        var allowedRoots = Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_ALLOWED_ROOTS");
        if (!string.IsNullOrWhiteSpace(allowedRoots))
        {
            config.AllowedRoots = allowedRoots
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        var artifactDirectory = Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_ARTIFACT_DIR");
        if (!string.IsNullOrWhiteSpace(artifactDirectory))
        {
            config.ArtifactDirectory = artifactDirectory;
        }

        var auditLogPath = Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_AUDIT_LOG");
        if (!string.IsNullOrWhiteSpace(auditLogPath))
        {
            config.AuditLogPath = auditLogPath;
        }

        if (TryReadIntEnvironment("ARCGIS_PRO_MCP_TIMEOUT_MS", out var defaultTimeoutMs))
        {
            config.Timeouts.DefaultMs = defaultTimeoutMs;
        }

        if (TryReadIntEnvironment("ARCGIS_PRO_MCP_MAX_TIMEOUT_MS", out var maxTimeoutMs))
        {
            config.Timeouts.MaxMs = maxTimeoutMs;
        }

        var destructiveGp = Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_ENABLE_DESTRUCTIVE_GP");
        if (!string.IsNullOrWhiteSpace(destructiveGp))
        {
            config.DestructiveOperations.EnableDestructiveGeoprocessing = IsTruthy(destructiveGp);
        }
    }

    private static bool TryReadIntEnvironment(string name, out int value)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out value) && value > 0;
    }

    private static bool IsTruthy(string value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(PipeName))
        {
            PipeName = BridgeDefaults.PipeName;
        }

        Timeouts.DefaultMs = Timeouts.DefaultMs <= 0 ? BridgeDefaults.DefaultTimeoutMs : Timeouts.DefaultMs;
        Timeouts.MaxMs = Math.Max(Timeouts.DefaultMs, Timeouts.MaxMs <= 0 ? 120000 : Timeouts.MaxMs);
        EnabledToolGroups = EnabledToolGroups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class BridgeDestructiveOperationPolicy
{
    public bool EnableDestructiveGeoprocessing { get; set; }

    public bool EnableFeatureEdits { get; set; }
}

public sealed class BridgeConfirmationPolicy
{
    public bool RequireSaveConfirmation { get; set; } = true;

    public bool RequireOverwriteConfirmation { get; set; } = true;

    public bool RequireFeatureEditConfirmation { get; set; } = true;

    public bool RequireDestructiveGeoprocessingConfirmation { get; set; } = true;

    public bool RequireScriptExecutionConfirmation { get; set; } = true;
}

public sealed class BridgeTimeoutPolicy
{
    public int DefaultMs { get; set; } = BridgeDefaults.DefaultTimeoutMs;

    public int MaxMs { get; set; } = 120000;
}

public static class BridgeAuditLog
{
    private const int DefaultMaxStringLength = 300;
    private const int FullFidelityMaxLength = 20000;
    private const long MaxArchivedScriptBytes = 1024 * 1024;
    private const int AppendAttempts = 5;

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string? ActorUser = ReadEnvironmentValue(() => Environment.UserName);
    private static readonly string? ActorDomain = ReadEnvironmentValue(() => Environment.UserDomainName);
    private static readonly string? ActorMachine = ReadEnvironmentValue(() => Environment.MachineName);

    // Argument keys that carry the action itself rather than a handle to it. These are recorded
    // verbatim regardless of operation so the log records what actually ran.
    private static readonly HashSet<string> FullFidelityKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "definitionQuery",
        "parameters",
        "environments",
        "arguments",
        "scriptPath",
        "workingDirectory",
        "outputDirectory",
        "expression",
        "sql",
        "whereClause"
    };

    public static void Append(string path, string process, BridgeRequest? request, BridgeResponse response)
    {
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);

            var fullFidelity = request is not null && RequiresFullFidelity(request.Op);
            var record = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                process,
                id = response.Id,
                client = request?.Client,
                actor = new
                {
                    user = ActorUser,
                    domain = ActorDomain,
                    machine = ActorMachine,
                    processId = Environment.ProcessId
                },
                op = request?.Op,
                group = request is null ? null : BridgeConfiguration.GetToolGroup(request.Op),
                dryRun = request?.DryRun,
                createdUtc = request?.CreatedUtc,
                timeoutMs = request?.TimeoutMs,
                argsFidelity = request is null ? null : (fullFidelity ? "full" : "summary"),
                argsSummary = SummarizeArgs(request?.Args, fullFidelity),
                targetIds = ExtractTargetIds(request?.Args),
                script = DescribeScript(request, directory),
                result = new
                {
                    ok = response.Ok,
                    code = response.Error?.Code ?? "ok",
                    message = response.Error?.Message,
                    elapsedMs = response.ElapsedMs,
                    warningCount = response.Warnings.Count,
                    messageCount = response.Messages.Count
                },
                artifacts = response.Artifacts.Select(artifact => new
                {
                    artifact.Id,
                    artifact.Uri,
                    artifact.Path,
                    artifact.MimeType,
                    artifact.SourceObjectId,
                    artifact.SourceObjectKind,
                    artifact.SourceObjectName
                }).ToArray()
            };

            AppendLine(fullPath, JsonSerializer.Serialize(record, JsonOptions));
        }
        catch
        {
            // Audit logging must not interfere with bridge request handling.
        }
    }

    // Operations whose arguments are the executed action itself, not a reference to an object.
    private static bool RequiresFullFidelity(string op)
    {
        var group = BridgeConfiguration.GetToolGroup(op);
        return string.Equals(group, "python", StringComparison.OrdinalIgnoreCase)
            || string.Equals(group, "geoprocessing", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, object?> SummarizeArgs(JsonObjectMap? args, bool fullFidelity)
    {
        if (args is null)
        {
            return new Dictionary<string, object?>();
        }

        return args.ToDictionary(
            item => item.Key,
            item => SummarizeJson(item.Value, fullFidelity || FullFidelityKeys.Contains(item.Key)),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? SummarizeJson(JsonElement value, bool full = false)
    {
        if (full)
        {
            var raw = value.GetRawText();
            return raw.Length <= FullFidelityMaxLength
                ? value
                : new { truncated = true, rawLength = raw.Length, preview = raw[..FullFidelityMaxLength] };
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => Truncate(value.GetString(), DefaultMaxStringLength),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => $"array[{value.GetArrayLength()}]",
            JsonValueKind.Object => $"object[{value.EnumerateObject().Count()}]",
            _ => value.ValueKind.ToString()
        };
    }

    private static IReadOnlyDictionary<string, object?> ExtractTargetIds(JsonObjectMap? args)
    {
        if (args is null)
        {
            return new Dictionary<string, object?>();
        }

        return args
            .Where(item => item.Key.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                || item.Key.EndsWith("Ids", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                item => item.Key,
                item => SummarizeJson(item.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    // A script path alone does not establish what ran, because the file can change afterwards.
    // Hash the contents at execution time and keep a content-addressed copy beside the log.
    private static object? DescribeScript(BridgeRequest? request, string auditDirectory)
    {
        if (request?.Args is null
            || !string.Equals(BridgeConfiguration.GetToolGroup(request.Op), "python", StringComparison.OrdinalIgnoreCase)
            || !TryGetStringArgument(request.Args, "scriptPath", out var scriptPath))
        {
            return null;
        }

        try
        {
            var resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(scriptPath));
            if (!File.Exists(resolved))
            {
                return new { path = resolved, exists = false };
            }

            var info = new FileInfo(resolved);
            var content = File.ReadAllBytes(resolved);
            var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            return new
            {
                path = resolved,
                exists = true,
                bytes = info.Length,
                lastWriteUtc = info.LastWriteTimeUtc,
                sha256,
                archivedCopy = content.LongLength <= MaxArchivedScriptBytes
                    ? ArchiveScript(auditDirectory, sha256, resolved, content)
                    : null
            };
        }
        catch (Exception ex)
        {
            return new { path = scriptPath, error = ex.GetType().Name };
        }
    }

    private static string? ArchiveScript(string auditDirectory, string sha256, string sourcePath, byte[] content)
    {
        try
        {
            var archiveDirectory = Path.Combine(auditDirectory, "audit-scripts");
            Directory.CreateDirectory(archiveDirectory);

            var extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
            {
                extension = ".py";
            }

            var target = Path.Combine(archiveDirectory, sha256 + extension);
            if (!File.Exists(target))
            {
                File.WriteAllBytes(target, content);
            }

            return target;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetStringArgument(JsonObjectMap args, string key, out string value)
    {
        foreach (var item in args)
        {
            if (!string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.Value.ValueKind == JsonValueKind.String)
            {
                var text = item.Value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    value = text;
                    return true;
                }
            }

            break;
        }

        value = string.Empty;
        return false;
    }

    // The MCP server and the add-in append to this file from separate processes, so an
    // in-process lock alone does not prevent records being lost to sharing violations.
    private static void AppendLine(string fullPath, string line)
    {
        lock (Lock)
        {
            for (var attempt = 0; attempt < AppendAttempts; attempt++)
            {
                try
                {
                    using var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream);
                    writer.WriteLine(line);
                    return;
                }
                catch (IOException) when (attempt < AppendAttempts - 1)
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
    }

    private static string? ReadEnvironmentValue(Func<string> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }
}
