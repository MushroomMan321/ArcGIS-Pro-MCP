using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ArcGisProBridgeContracts;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;

namespace ArcGisProBridgeAddIn;

internal sealed class ProBridgeService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly BridgeConfiguration _config;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly SemaphoreSlim _operationQueue = new(1, 1);
    private readonly object _logLock = new();
    private readonly SessionObjectRegistry _objectRegistry = new();
    private readonly object _runnerToolboxLock = new();
    private readonly HashSet<string> _runnerToolboxesRefreshed = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _logPath;
    private readonly string _auditLogPath;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private string _lastStatus = "Bridge service is starting.";

    public ProBridgeService(BridgeConfiguration config)
    {
        _config = config;
        _pipeName = config.PipeName;
        var logRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISProMcpBridge",
            "logs");
        Directory.CreateDirectory(logRoot);
        _logPath = Path.Combine(logRoot, "addin-bridge.log");
        _auditLogPath = config.GetAuditLogPath();
    }

    public void Start()
    {
        if (_serverTask is { IsCompleted: false })
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => RunAsync(_cts.Token));
        _lastStatus = $"Bridge service started on pipe '{_pipeName}' at {_startedUtc:O}.";
    }

    public string GetLastStatusSummary()
    {
        return _lastStatus;
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _serverTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ArcGIS Pro is shutting down; do not block unload on bridge cleanup.
        }
        finally
        {
            _cts?.Dispose();
            _operationQueue.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = CreatePipeServerStream();

                await server.WaitForConnectionAsync(cancellationToken);
                await HandleConnectionAsync(server, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastStatus = $"Bridge service error: {ex.Message}";
                AppendLog($"service error: {ex}");
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        var line = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        BridgeRequest? request = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonOptions);
            var validationErrors = BridgeRequestValidator.Validate(request);
            if (validationErrors.Count > 0)
            {
                var invalidResponse = BridgeResponse.Failure(
                    request?.Id ?? "unknown",
                    "bridge.invalid_request",
                    string.Join(" ", validationErrors),
                    stopwatch.ElapsedMilliseconds,
                    warnings: validationErrors);
                AppendLogResponse(request, invalidResponse);
                AppendAudit(request, invalidResponse);
                await WriteResponseAsync(writer, invalidResponse);
                return;
            }

            AppendLogRequest(request!);
            var response = await HandleQueuedRequestAsync(request!, stopwatch, cancellationToken);
            AppendLogResponse(request, response);
            AppendAudit(request, response);
            await WriteResponseAsync(writer, response);
        }
        catch (JsonException ex)
        {
            var response = BridgeResponse.Failure(
                request?.Id ?? "unknown",
                "bridge.parse",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                details: JsonSerializer.SerializeToElement(new { path = ex.Path, line = ex.LineNumber, bytePosition = ex.BytePositionInLine }));
            AppendLogResponse(request, response);
            AppendAudit(request, response);
            await WriteResponseAsync(writer, response);
        }
        catch (Exception ex)
        {
            var response = BridgeResponse.Failure(
                request?.Id ?? "unknown",
                "bridge.unhandled",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().FullName }));
            AppendLogResponse(request, response);
            AppendAudit(request, response);
            await WriteResponseAsync(writer, response);
        }
    }

    private async Task<BridgeResponse> HandleQueuedRequestAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var timeoutMs = request.TimeoutMs <= 0
            ? BridgeDefaults.DefaultTimeoutMs
            : Math.Max(1000, request.TimeoutMs);
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(timeoutMs);

        if (request.Op == "pro.health")
        {
            var bridgeBusy = _operationQueue.CurrentCount == 0;
            return await HandleRequestAsync(request, stopwatch, requestCts.Token, bridgeBusy);
        }

        var queueEntered = false;
        try
        {
            await _operationQueue.WaitAsync(requestCts.Token);
            queueEntered = true;
            return await HandleRequestAsync(request, stopwatch, requestCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BridgeResponse.Failure(
                request.Id,
                "bridge.timeout",
                $"Operation '{request.Op}' timed out after {timeoutMs} ms.",
                stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            if (queueEntered)
            {
                _operationQueue.Release();
            }
        }
    }

    private async Task<BridgeResponse> HandleRequestAsync(
        BridgeRequest request,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        bool bridgeBusy = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mutationIdError = ValidateMutationObjectIds(request, stopwatch);
        if (mutationIdError is not null)
        {
            return mutationIdError;
        }

        var policyError = ValidateConfiguredOperation(request, stopwatch);
        if (policyError is not null)
        {
            return policyError;
        }

        return request.Op switch
        {
            "pro.health" => BridgeResponse.Success(request.Id, await CaptureHealthAsync(bridgeBusy, cancellationToken), stopwatch.ElapsedMilliseconds),
            "project.get_current" => BridgeResponse.Success(request.Id, await CaptureProjectStateAsync(cancellationToken), stopwatch.ElapsedMilliseconds),
            "object.registry" => BridgeResponse.Success(request.Id, await CaptureObjectRegistryAsync(cancellationToken), stopwatch.ElapsedMilliseconds),
            "map.list" => BridgeResponse.Success(request.Id, await CaptureMapListAsync(cancellationToken), stopwatch.ElapsedMilliseconds),
            "map.get_state" => BridgeResponse.Success(request.Id, await CaptureMapStateAsync(request, cancellationToken), stopwatch.ElapsedMilliseconds),
            "layer.list" => BridgeResponse.Success(request.Id, await CaptureLayerListAsync(request, cancellationToken), stopwatch.ElapsedMilliseconds),
            "layer.get_state" => BridgeResponse.Success(request.Id, await CaptureLayerStateAsync(request, cancellationToken), stopwatch.ElapsedMilliseconds),
            "layout.list" => BridgeResponse.Success(request.Id, await CaptureLayoutListAsync(cancellationToken), stopwatch.ElapsedMilliseconds),
            "layout.get_state" => BridgeResponse.Success(request.Id, await CaptureLayoutStateAsync(request, cancellationToken), stopwatch.ElapsedMilliseconds),
            "map.activate" => await HandleMapActivateAsync(request, stopwatch, cancellationToken),
            "map.set_extent" => await HandleMapSetExtentAsync(request, stopwatch, cancellationToken),
            "map.zoom_to_layer" => await HandleMapZoomToLayerAsync(request, stopwatch, cancellationToken),
            "map.set_basemap" => await HandleMapSetBasemapAsync(request, stopwatch, cancellationToken),
            "layer.set_visibility" => await HandleLayerSetVisibilityAsync(request, stopwatch, cancellationToken),
            "layer.set_definition_query" => await HandleLayerSetDefinitionQueryAsync(request, stopwatch, cancellationToken),
            "layer.set_transparency" => await HandleLayerSetTransparencyAsync(request, stopwatch, cancellationToken),
            "layer.apply_symbology_from_layer" => await HandleLayerApplySymbologyFromLayerAsync(request, stopwatch, cancellationToken),
            "layout.set_text" => await HandleLayoutSetTextAsync(request, stopwatch, cancellationToken),
            "layout.set_map_frame_camera" => await HandleLayoutSetMapFrameCameraAsync(request, stopwatch, cancellationToken),
            "layout.set_surround_visibility" => await HandleLayoutSetSurroundVisibilityAsync(request, stopwatch, cancellationToken),
            "legend.get_state" => BridgeResponse.Success(request.Id, await CaptureLegendStateAsync(request, cancellationToken), stopwatch.ElapsedMilliseconds),
            "legend.set_visibility" => await HandleLegendSetVisibilityAsync(request, stopwatch, cancellationToken),
            "legend.set_layout" => await HandleLegendSetLayoutAsync(request, stopwatch, cancellationToken),
            "legend.set_items" => await HandleLegendSetItemsAsync(request, stopwatch, cancellationToken),
            "legend.rename_items" => await HandleLegendRenameItemsAsync(request, stopwatch, cancellationToken),
            "legend.apply_compact_style" => await HandleLegendApplyCompactStyleAsync(request, stopwatch, cancellationToken),
            "legend.qa_preview" => await HandleLegendQaPreviewAsync(request, stopwatch, cancellationToken),
            "export.layout" => await HandleExportLayoutAsync(request, stopwatch, cancellationToken),
            "project.save" => await HandleProjectSaveAsync(request, stopwatch, cancellationToken),
            "project.save_copy" => await HandleProjectSaveCopyAsync(request, stopwatch, cancellationToken),
            "visual.capture_active_view" => await HandleCaptureActiveViewAsync(request, stopwatch, cancellationToken),
            "visual.export_active_map" => await HandleExportActiveMapAsync(request, stopwatch, cancellationToken),
            "visual.export_layout_preview" => await HandleExportLayoutPreviewAsync(request, stopwatch, cancellationToken),
            "geoprocessing.execute_tool" => await HandleGeoprocessingExecuteToolAsync(request, stopwatch, cancellationToken),
            "python.run_arcpy_script" => await HandlePythonRunArcpyScriptAsync(request, stopwatch, cancellationToken),
            "artifact.get" => BridgeResponse.Success(request.Id, await CaptureArtifactStateAsync(request, cancellationToken), stopwatch.ElapsedMilliseconds),
            "bridge.diagnostics.delay" => await HandleDiagnosticDelayAsync(request, stopwatch, cancellationToken),
            _ => BridgeResponse.Failure(request.Id, "bridge.unknown_op", $"Unknown operation '{request.Op}'.", stopwatch.ElapsedMilliseconds)
        };
    }

    private static BridgeResponse? ValidateMutationObjectIds(BridgeRequest request, Stopwatch stopwatch)
    {
        static bool Missing(JsonObjectMap? args, string key) => string.IsNullOrWhiteSpace(args?.GetString(key));

        var missing = request.Op switch
        {
            "map.activate" when Missing(request.Args, "mapId") => new[] { "mapId" },
            "map.set_extent" when Missing(request.Args, "mapId") => new[] { "mapId" },
            "map.zoom_to_layer" when Missing(request.Args, "mapId") && Missing(request.Args, "layerId") => new[] { "mapId", "layerId" },
            "map.zoom_to_layer" when Missing(request.Args, "mapId") => new[] { "mapId" },
            "map.zoom_to_layer" when Missing(request.Args, "layerId") => new[] { "layerId" },
            "map.set_basemap" when Missing(request.Args, "mapId") => new[] { "mapId" },
            "layer.set_visibility" when Missing(request.Args, "layerId") => new[] { "layerId" },
            "layer.set_definition_query" when Missing(request.Args, "layerId") => new[] { "layerId" },
            "layer.set_transparency" when Missing(request.Args, "layerId") => new[] { "layerId" },
            "layer.apply_symbology_from_layer" when Missing(request.Args, "layerId") => new[] { "layerId" },
            "layout.set_text" when Missing(request.Args, "layoutId") && Missing(request.Args, "elementId") => new[] { "layoutId", "elementId" },
            "layout.set_text" when Missing(request.Args, "layoutId") => new[] { "layoutId" },
            "layout.set_text" when Missing(request.Args, "elementId") => new[] { "elementId" },
            "layout.set_map_frame_camera" when Missing(request.Args, "layoutId") && Missing(request.Args, "mapFrameId") => new[] { "layoutId", "mapFrameId" },
            "layout.set_map_frame_camera" when Missing(request.Args, "layoutId") => new[] { "layoutId" },
            "layout.set_map_frame_camera" when Missing(request.Args, "mapFrameId") => new[] { "mapFrameId" },
            "layout.set_surround_visibility" when Missing(request.Args, "layoutId") && Missing(request.Args, "elementId") => new[] { "layoutId", "elementId" },
            "layout.set_surround_visibility" when Missing(request.Args, "layoutId") => new[] { "layoutId" },
            "layout.set_surround_visibility" when Missing(request.Args, "elementId") => new[] { "elementId" },
            "legend.set_visibility" when Missing(request.Args, "layoutId") && Missing(request.Args, "elementId") => new[] { "layoutId", "elementId" },
            "legend.set_visibility" when Missing(request.Args, "layoutId") => new[] { "layoutId" },
            "legend.set_visibility" when Missing(request.Args, "elementId") => new[] { "elementId" },
            "legend.set_layout" when Missing(request.Args, "layoutId") && Missing(request.Args, "elementId") => new[] { "layoutId", "elementId" },
            "legend.set_layout" when Missing(request.Args, "layoutId") => new[] { "layoutId" },
            "legend.set_layout" when Missing(request.Args, "elementId") => new[] { "elementId" },
            "legend.set_items" when Missing(request.Args, "layoutId") && Missing(request.Args, "elementId") => new[] { "layoutId", "elementId" },
            "legend.set_items" when Missing(request.Args, "layoutId") => new[] { "layoutId" },
            "legend.set_items" when Missing(request.Args, "elementId") => new[] { "elementId" },
            "legend.rename_items" when Missing(request.Args, "layoutId") && Missing(request.Args, "elementId") => new[] { "layoutId", "elementId" },
            "legend.rename_items" when Missing(request.Args, "layoutId") => new[] { "layoutId" },
            "legend.rename_items" when Missing(request.Args, "elementId") => new[] { "elementId" },
            "legend.apply_compact_style" when Missing(request.Args, "layoutId") && Missing(request.Args, "elementId") => new[] { "layoutId", "elementId" },
            "legend.apply_compact_style" when Missing(request.Args, "layoutId") => new[] { "layoutId" },
            "legend.apply_compact_style" when Missing(request.Args, "elementId") => new[] { "elementId" },
            "legend.qa_preview" when Missing(request.Args, "layoutId") && Missing(request.Args, "elementId") => new[] { "layoutId", "elementId" },
            "legend.qa_preview" when Missing(request.Args, "layoutId") => new[] { "layoutId" },
            "legend.qa_preview" when Missing(request.Args, "elementId") => new[] { "elementId" },
            _ => Array.Empty<string>()
        };

        if (missing.Length == 0)
        {
            return null;
        }

        return BridgeResponse.Failure(
            request.Id,
            "bridge.invalid_args",
            $"Operation '{request.Op}' requires object ID argument(s): {string.Join(", ", missing)}.",
            stopwatch.ElapsedMilliseconds,
            warnings: missing.Select(item => $"{item} is required for mutation operations.").ToArray(),
            details: JsonSerializer.SerializeToElement(new { missingObjectIds = missing }));
    }

    private BridgeResponse? ValidateConfiguredOperation(BridgeRequest request, Stopwatch stopwatch)
    {
        if (!_config.IsOperationEnabled(request.Op))
        {
            var group = BridgeConfiguration.GetToolGroup(request.Op);
            return BridgeResponse.Failure(
                request.Id,
                "bridge.tool_group_disabled",
                $"Operation '{request.Op}' belongs to disabled tool group '{group}'. Enable the group in the bridge config before calling it.",
                stopwatch.ElapsedMilliseconds,
                warnings: new[] { $"Enabled tool groups: {string.Join(", ", _config.EnabledToolGroups)}" });
        }

        if (request.DryRun)
        {
            return null;
        }

        if (request.Op.StartsWith("feature.", StringComparison.OrdinalIgnoreCase)
            && !_config.DestructiveOperations.EnableFeatureEdits)
        {
            return BridgeResponse.Failure(
                request.Id,
                "bridge.feature_edits_disabled",
                "Feature editing operations are disabled by bridge configuration.",
                stopwatch.ElapsedMilliseconds);
        }

        if (request.Op.StartsWith("feature.", StringComparison.OrdinalIgnoreCase)
            && _config.Confirmations.RequireFeatureEditConfirmation
            && !HasConfirmation(request, "confirmFeatureEdit"))
        {
            return ConfirmationRequired(request, stopwatch, "confirmFeatureEdit", "Feature edit operations require confirmFeatureEdit=true.");
        }

        if (request.Op is "project.save" or "project.save_copy"
            && _config.Confirmations.RequireSaveConfirmation
            && !HasConfirmation(request, "confirmSave"))
        {
            return ConfirmationRequired(request, stopwatch, "confirmSave", "Project save operations require confirmSave=true.");
        }

        var overwrite = request.Args?.GetBoolean("overwrite") ?? false;
        if (overwrite
            && request.Op is "export.layout" or "project.save_copy"
            && _config.Confirmations.RequireOverwriteConfirmation
            && !HasConfirmation(request, "confirmOverwrite"))
        {
            return ConfirmationRequired(request, stopwatch, "confirmOverwrite", "Overwrite operations require confirmOverwrite=true.");
        }

        var syntaxOnly = request.Args?.GetBoolean("syntaxOnly") ?? false;
        if (request.Op == "python.run_arcpy_script"
            && !syntaxOnly
            && _config.Confirmations.RequireScriptExecutionConfirmation
            && !HasConfirmation(request, "confirmScriptExecution"))
        {
            return ConfirmationRequired(request, stopwatch, "confirmScriptExecution", "ArcPy script execution requires confirmScriptExecution=true.");
        }

        return null;
    }

    private static bool HasConfirmation(BridgeRequest request, string key)
    {
        return request.Args?.GetBoolean(key) == true
            || request.Args?.GetBoolean("confirmed") == true;
    }

    private static BridgeResponse ConfirmationRequired(BridgeRequest request, Stopwatch stopwatch, string requiredFlag, string message)
    {
        return BridgeResponse.Failure(
            request.Id,
            "bridge.confirmation_required",
            message,
            stopwatch.ElapsedMilliseconds,
            warnings: new[] { $"Pass {requiredFlag}=true only after the user explicitly approves this action." },
            details: JsonSerializer.SerializeToElement(new { requiredFlag }));
    }

    private static async Task<BridgeResponse> HandleDiagnosticDelayAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var delayMs = request.Args?.GetInt32("delayMs") ?? 1000;
        if (delayMs < 0)
        {
            return BridgeResponse.Failure(
                request.Id,
                "bridge.invalid_args",
                "delayMs must be zero or a positive integer.",
                stopwatch.ElapsedMilliseconds);
        }

        await Task.Delay(delayMs, cancellationToken);
        return BridgeResponse.Success(
            request.Id,
            new
            {
                delayedMs = delayMs,
                dryRun = request.DryRun
            },
            stopwatch.ElapsedMilliseconds,
            messages: new[] { "Diagnostic delay completed." });
    }

    private async Task<ProHealth> CaptureHealthAsync(bool bridgeBusy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var health = await QueuedTask.Run<ProHealth>(() =>
        {
            var project = Project.Current;
            var activeMap = MapView.Active?.Map;

            return new ProHealth(
                Ready: true,
                Busy: bridgeBusy,
                PipeName: _pipeName,
                ProName: FrameworkApplication.Name,
                ProjectName: project?.Name,
                ProjectPath: project?.Path ?? project?.URI,
                HomeFolder: project?.HomeFolderPath,
                DefaultGeodatabase: project?.DefaultGeodatabasePath,
                ActiveMap: activeMap?.Name,
                ActiveView: LayoutView.Active is not null ? "LayoutView" : MapView.Active is null ? null : "MapView",
                ActiveLayout: LayoutView.Active?.Layout?.Name,
                Dirty: project?.IsDirty ?? false,
                ServiceStartedUtc: _startedUtc,
                CheckedUtc: DateTimeOffset.UtcNow);
        });

        _lastStatus =
            $"Ready: {health.Ready}\n" +
            $"Busy: {health.Busy}\n" +
            $"Pipe: {health.PipeName}\n" +
            $"Project: {health.ProjectName ?? "<none>"}\n" +
            $"Project path: {health.ProjectPath ?? "<none>"}\n" +
            $"Active map: {health.ActiveMap ?? "<none>"}\n" +
            $"Checked UTC: {health.CheckedUtc:O}";

        return health ?? new ProHealth(
            Ready: false,
            Busy: bridgeBusy,
            PipeName: _pipeName,
            ProName: FrameworkApplication.Name,
            ProjectName: null,
            ProjectPath: null,
            HomeFolder: null,
            DefaultGeodatabase: null,
            ActiveMap: null,
            ActiveView: null,
            ActiveLayout: null,
            Dirty: false,
            ServiceStartedUtc: _startedUtc,
            CheckedUtc: DateTimeOffset.UtcNow);
    }

    private async Task<object> CaptureProjectStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await QueuedTask.Run<object>(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return new
                {
                    loaded = false,
                    registry = RegistrySummary(context.Registry),
                    checkedUtc = DateTimeOffset.UtcNow
                };
            }

            var project = context.Project;
            var maps = context.Maps.Select(MapSummary).ToArray();
            var layouts = context.Layouts.Select(LayoutSummary).ToArray();
            var activeMap = context.Maps.FirstOrDefault(item => item.IsActive);
            var activeLayout = context.Layouts.FirstOrDefault(item => item.IsActive);

            return new
            {
                loaded = true,
                id = context.ProjectObject?.Id,
                name = project.Name,
                displayName = project.Name,
                type = "Project",
                path = project.Path,
                uri = project.URI,
                homeFolder = project.HomeFolderPath,
                defaultGeodatabase = project.DefaultGeodatabasePath,
                dirty = project.IsDirty,
                activeMap = activeMap is null ? null : MapSummary(activeMap),
                activeLayout = activeLayout is null ? null : LayoutSummary(activeLayout),
                mapCount = maps.Length,
                layoutCount = layouts.Length,
                mapFrameCount = context.MapFrames.Count,
                layoutElementCount = context.LayoutElements.Count,
                maps,
                layouts,
                registry = RegistrySummary(context.Registry),
                checkedUtc = DateTimeOffset.UtcNow
            };
        });
    }

    private async Task<object> CaptureObjectRegistryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await QueuedTask.Run<object>(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            return new
            {
                projectLoaded = context.Project is not null,
                count = context.Registry.Count,
                objects = context.Registry.Objects,
                refreshedUtc = context.Registry.RefreshedUtc
            };
        });
    }

    private async Task<object> CaptureMapListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await QueuedTask.Run<object>(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var maps = context.Maps.Select(MapSummary).ToArray();

            return new
            {
                projectLoaded = context.Project is not null,
                count = maps.Length,
                maps,
                registry = RegistrySummary(context.Registry),
                checkedUtc = DateTimeOffset.UtcNow
            };
        });
    }

    private async Task<object> CaptureMapStateAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await QueuedTask.Run<object>(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return new
                {
                    projectLoaded = false,
                    found = false,
                    checkedUtc = DateTimeOffset.UtcNow
                };
            }

            var match = ResolveMap(context, request.Args?.GetString("mapId"), request.Args?.GetString("mapName"));
            if (match is null)
            {
                return NotFound("map", request.Args?.GetString("mapId"), request.Args?.GetString("mapName"), context.Registry);
            }

            var layers = context.Layers
                .Where(item => item.MapObject.Id == match.Object.Id)
                .Select(LayerSummary)
                .ToArray();
            var mapFrames = context.MapFrames
                .Where(item => item.MapId == match.Object.Id)
                .Select(MapFrameSummary)
                .ToArray();

            return new
            {
                projectLoaded = true,
                found = true,
                map = MapDetail(match),
                layerCount = layers.Length,
                layers,
                layerTree = LayerTree(context, match.Object.Id),
                mapFrameCount = mapFrames.Length,
                mapFrames,
                checkedUtc = DateTimeOffset.UtcNow
            };
        });
    }

    private async Task<object> CaptureLayerListAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await QueuedTask.Run<object>(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return new
                {
                    projectLoaded = false,
                    count = 0,
                    maps = Array.Empty<object>(),
                    layers = Array.Empty<object>(),
                    checkedUtc = DateTimeOffset.UtcNow
                };
            }

            var mapId = request.Args?.GetString("mapId");
            var mapName = request.Args?.GetString("mapName");
            var maps = context.Maps
                .Where(map => MatchesMap(map, mapId, mapName))
                .ToArray();
            var mapIds = maps.Select(item => item.Object.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var layerRows = context.Layers
                .Where(layer => mapIds.Contains(layer.MapObject.Id))
                .Select(LayerSummary)
                .ToArray();

            return new
            {
                projectLoaded = true,
                mapCount = maps.Length,
                count = layerRows.Length,
                maps = maps.Select(MapSummary).ToArray(),
                layers = layerRows,
                registry = RegistrySummary(context.Registry),
                checkedUtc = DateTimeOffset.UtcNow
            };
        });
    }

    private async Task<object> CaptureLayerStateAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await QueuedTask.Run<object>(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return new
                {
                    projectLoaded = false,
                    found = false,
                    checkedUtc = DateTimeOffset.UtcNow
                };
            }

            var match = ResolveLayer(
                context,
                request.Args?.GetString("layerId"),
                request.Args?.GetString("layerName"),
                request.Args?.GetString("mapId"),
                request.Args?.GetString("mapName"));
            if (match is null)
            {
                return NotFound("layer", request.Args?.GetString("layerId"), request.Args?.GetString("layerName"), context.Registry);
            }

            return new
            {
                projectLoaded = true,
                found = true,
                layer = LayerDetail(match),
                checkedUtc = DateTimeOffset.UtcNow
            };
        });
    }

    private async Task<object> CaptureLayoutListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await QueuedTask.Run<object>(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var layouts = context.Layouts.Select(LayoutSummary).ToArray();

            return new
            {
                projectLoaded = context.Project is not null,
                count = layouts.Length,
                layouts,
                mapFrameCount = context.MapFrames.Count,
                mapFrames = context.MapFrames.Select(MapFrameSummary).ToArray(),
                layoutElementCount = context.LayoutElements.Count,
                layoutElements = context.LayoutElements.Select(LayoutElementSummary).ToArray(),
                registry = RegistrySummary(context.Registry),
                checkedUtc = DateTimeOffset.UtcNow
            };
        });
    }

    private async Task<object> CaptureLayoutStateAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await QueuedTask.Run<object>(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return new
                {
                    projectLoaded = false,
                    found = false,
                    checkedUtc = DateTimeOffset.UtcNow
                };
            }

            var match = ResolveLayout(context, request.Args?.GetString("layoutId"), request.Args?.GetString("layoutName"));
            if (match is null)
            {
                return NotFound("layout", request.Args?.GetString("layoutId"), request.Args?.GetString("layoutName"), context.Registry);
            }

            var mapFrames = context.MapFrames
                .Where(item => item.LayoutObject.Id == match.Object.Id)
                .Select(MapFrameSummary)
                .ToArray();
            var layoutElements = context.LayoutElements
                .Where(item => item.LayoutObject.Id == match.Object.Id)
                .Select(LayoutElementDetail)
                .ToArray();

            return new
            {
                projectLoaded = true,
                found = true,
                layout = LayoutDetail(match),
                mapFrameCount = mapFrames.Length,
                mapFrames,
                layoutElementCount = layoutElements.Length,
                layoutElements,
                checkedUtc = DateTimeOffset.UtcNow
            };
        });
    }

    private async Task<object> CaptureLegendStateAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await QueuedTask.Run<object>(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return new
                {
                    projectLoaded = false,
                    found = false,
                    checkedUtc = DateTimeOffset.UtcNow
                };
            }

            var elementId = request.Args?.GetString("elementId") ?? request.Args?.GetString("legendId");
            var layoutId = request.Args?.GetString("layoutId");
            var layout = ResolveLayout(context, layoutId, request.Args?.GetString("layoutName"));
            if (layout is null && string.IsNullOrWhiteSpace(layoutId))
            {
                layout = context.LayoutElements
                    .Where(item => string.Equals(item.Object.Id, elementId, StringComparison.OrdinalIgnoreCase))
                    .Select(item => ResolveLayout(context, item.LayoutObject.Id, null))
                    .FirstOrDefault(item => item is not null);
            }

            if (layout is null)
            {
                return NotFound("layout", layoutId, request.Args?.GetString("layoutName"), context.Registry);
            }

            var legend = ResolveLegendElement(context, elementId, layout.Object.Id);
            if (legend is null)
            {
                return NotFound("legend", elementId, request.Args?.GetString("legendName"), context.Registry);
            }

            return new
            {
                projectLoaded = true,
                found = true,
                layout = LayoutSummary(layout),
                legend = LegendDetail(legend, context),
                guardrails = LegendGuardrailSummary(),
                checkedUtc = DateTimeOffset.UtcNow
            };
        });
    }

    private async Task<BridgeResponse> HandleMapActivateAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return MutationOperationResult.Failure("project.not_loaded", "No ArcGIS Pro project is loaded.");
            }

            var match = ResolveMap(context, request.Args?.GetString("mapId"), request.Args?.GetString("mapName"));
            if (match is null)
            {
                return MutationOperationResult.Failure("map.not_found", $"Map '{request.Args?.GetString("mapId")}' was not found.");
            }

            var beforeActive = match.IsActive;
            if (!request.DryRun)
            {
                var pane = match.Map.GetMapPanes().FirstOrDefault();
                if (pane is null)
                {
                    return MutationOperationResult.Failure(
                        "map.no_open_view",
                        "The target map does not have an open map view to activate.");
                }

                if (!ActivatePane(pane))
                {
                    return MutationOperationResult.Failure(
                        "map.activate_failed",
                        "ArcGIS Pro did not expose an activation method for the target map pane.");
                }
            }

            var afterContext = request.DryRun ? context : RefreshObjectRegistry(Project.Current);
            var after = ResolveMap(afterContext, match.Object.Id, null) ?? match;
            return MutationOperationResult.Success(new
            {
                operation = "map.activate",
                dryRun = request.DryRun,
                changed = !beforeActive || request.DryRun,
                target = MapSummary(match),
                before = new { active = beforeActive },
                after = new { active = request.DryRun ? true : after.IsActive },
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleMapSetExtentAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return MutationOperationResult.Failure("project.not_loaded", "No ArcGIS Pro project is loaded.");
            }

            var match = ResolveMap(context, request.Args?.GetString("mapId"), request.Args?.GetString("mapName"));
            if (match is null)
            {
                return MutationOperationResult.Failure("map.not_found", $"Map '{request.Args?.GetString("mapId")}' was not found.");
            }

            if (!TryReadExtent(request.Args, match.Map.SpatialReference, out var extent, out var extentError))
            {
                return MutationOperationResult.Failure("bridge.invalid_args", extentError ?? "Invalid extent arguments.");
            }

            var mapView = FindMapView(match.Map);
            var beforeExtent = EnvelopeSummary(mapView?.Extent);
            if (!request.DryRun)
            {
                if (mapView is null)
                {
                    return MutationOperationResult.Failure(
                        "map.no_open_view",
                        "The target map does not have an open map view. Open or activate the map before setting its visible extent.");
                }

                if (!mapView.ZoomTo(extent, TimeSpan.Zero, true))
                {
                    return MutationOperationResult.Failure("map.zoom_failed", "ArcGIS Pro did not accept the requested map extent.");
                }
            }

            return MutationOperationResult.Success(new
            {
                operation = "map.set_extent",
                dryRun = request.DryRun,
                changed = true,
                target = MapSummary(match),
                before = new { extent = beforeExtent },
                after = new { extent = EnvelopeSummary(extent) },
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleMapZoomToLayerAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return MutationOperationResult.Failure("project.not_loaded", "No ArcGIS Pro project is loaded.");
            }

            var map = ResolveMap(context, request.Args?.GetString("mapId"), request.Args?.GetString("mapName"));
            if (map is null)
            {
                return MutationOperationResult.Failure("map.not_found", $"Map '{request.Args?.GetString("mapId")}' was not found.");
            }

            var layer = ResolveLayer(context, request.Args?.GetString("layerId"), null, map.Object.Id, null);
            if (layer is null || !string.Equals(layer.MapObject.Id, map.Object.Id, StringComparison.OrdinalIgnoreCase))
            {
                return MutationOperationResult.Failure("layer.not_found", "The target layer was not found in the target map.");
            }

            var selectionOnly = request.Args?.GetBoolean("selectionOnly") ?? false;
            var mapView = FindMapView(map.Map);
            var beforeExtent = EnvelopeSummary(mapView?.Extent);
            var layerExtent = SafeValue(() => layer.Layer.QueryExtent(selectionOnly));
            if (!request.DryRun)
            {
                if (mapView is null)
                {
                    return MutationOperationResult.Failure(
                        "map.no_open_view",
                        "The target map does not have an open map view. Open or activate the map before zooming to a layer.");
                }

                if (!mapView.ZoomTo(layer.Layer, selectionOnly, TimeSpan.Zero, true))
                {
                    return MutationOperationResult.Failure("map.zoom_failed", "ArcGIS Pro did not accept the requested layer zoom.");
                }
            }

            return MutationOperationResult.Success(new
            {
                operation = "map.zoom_to_layer",
                dryRun = request.DryRun,
                changed = true,
                target = MapSummary(map),
                layer = LayerSummary(layer),
                before = new { extent = beforeExtent },
                after = new { extent = EnvelopeSummary(layerExtent) },
                selectionOnly,
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleMapSetBasemapAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return MutationOperationResult.Failure("project.not_loaded", "No ArcGIS Pro project is loaded.");
            }

            var match = ResolveMap(context, request.Args?.GetString("mapId"), request.Args?.GetString("mapName"));
            if (match is null)
            {
                return MutationOperationResult.Failure("map.not_found", $"Map '{request.Args?.GetString("mapId")}' was not found.");
            }

            var basemapText = request.Args?.GetString("basemap");
            if (!TryParseAllowedBasemap(basemapText, out var basemap, out var allowed))
            {
                return MutationOperationResult.Failure(
                    "map.basemap_not_allowed",
                    $"Basemap '{basemapText}' is not allowed. Allowed values: {string.Join(", ", allowed)}.");
            }

            if (!request.DryRun)
            {
                match.Map.SetBasemapLayers(basemap);
                RefreshObjectRegistry(Project.Current);
            }

            return MutationOperationResult.Success(new
            {
                operation = "map.set_basemap",
                dryRun = request.DryRun,
                changed = true,
                target = MapSummary(match),
                before = new { basemap = (string?)null },
                after = new { basemap = basemap.ToString() },
                allowedBasemaps = allowed,
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLayerSetVisibilityAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var match = ResolveMutationLayer(context, request.Args?.GetString("layerId"));
            if (match.Result is not null)
            {
                return match.Result;
            }

            var layer = match.Layer!;
            var visible = request.Args?.GetBoolean("visible");
            if (visible is null)
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "visible must be true or false.");
            }

            var before = layer.Layer.IsVisible;
            if (!request.DryRun && before != visible.Value)
            {
                layer.Layer.SetVisibility(visible.Value);
                context = RefreshObjectRegistry(Project.Current);
                layer = ResolveLayer(context, layer.Object.Id, null, null, null) ?? layer;
            }

            return MutationOperationResult.Success(new
            {
                operation = "layer.set_visibility",
                dryRun = request.DryRun,
                changed = before != visible.Value,
                target = LayerSummary(layer),
                before = new { visible = before },
                after = new { visible = visible.Value },
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLayerSetDefinitionQueryAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var match = ResolveMutationLayer(context, request.Args?.GetString("layerId"));
            if (match.Result is not null)
            {
                return match.Result;
            }

            var layer = match.Layer!;
            if (layer.Layer is not BasicFeatureLayer featureLayer)
            {
                return MutationOperationResult.Failure("layer.unsupported_type", "Definition queries can only be set on basic feature layers.");
            }

            var query = request.Args?.GetString("definitionQuery") ?? string.Empty;
            var before = featureLayer.DefinitionQuery ?? string.Empty;
            if (!request.DryRun && !string.Equals(before, query, StringComparison.Ordinal))
            {
                featureLayer.SetDefinitionQuery(query);
                context = RefreshObjectRegistry(Project.Current);
                layer = ResolveLayer(context, layer.Object.Id, null, null, null) ?? layer;
            }

            return MutationOperationResult.Success(new
            {
                operation = "layer.set_definition_query",
                dryRun = request.DryRun,
                changed = !string.Equals(before, query, StringComparison.Ordinal),
                target = LayerSummary(layer),
                before = new { definitionQuery = before },
                after = new { definitionQuery = query },
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLayerSetTransparencyAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var match = ResolveMutationLayer(context, request.Args?.GetString("layerId"));
            if (match.Result is not null)
            {
                return match.Result;
            }

            var layer = match.Layer!;
            var transparency = request.Args?.GetDouble("transparency");
            if (transparency is null || transparency < 0 || transparency > 100)
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "transparency must be a number from 0 through 100.");
            }

            var before = layer.Layer.Transparency;
            if (!request.DryRun && Math.Abs(before - transparency.Value) > 0.0001)
            {
                layer.Layer.SetTransparency(transparency.Value);
                context = RefreshObjectRegistry(Project.Current);
                layer = ResolveLayer(context, layer.Object.Id, null, null, null) ?? layer;
            }

            return MutationOperationResult.Success(new
            {
                operation = "layer.set_transparency",
                dryRun = request.DryRun,
                changed = Math.Abs(before - transparency.Value) > 0.0001,
                target = LayerSummary(layer),
                before = new { transparency = before },
                after = new { transparency = transparency.Value },
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLayerApplySymbologyFromLayerAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var match = ResolveMutationLayer(context, request.Args?.GetString("layerId"));
            if (match.Result is not null)
            {
                return match.Result;
            }

            var layer = match.Layer!;
            if (layer.Layer is not FeatureLayer featureLayer)
            {
                return MutationOperationResult.Failure("layer.unsupported_type", "Symbology from .lyrx is currently supported for feature layers only.");
            }

            var symbologyLayerPath = request.Args?.GetString("symbologyLayerPath");
            if (!TryResolveAllowedLayerFile(context.Project, symbologyLayerPath, out var fullPath, out var pathError, out var allowedRoots))
            {
                return MutationOperationResult.Failure(
                    "bridge.path_not_allowed",
                    pathError ?? "Symbology layer path is not allowed.",
                    warnings: allowedRoots.Select(root => $"Allowed root: {root}").ToArray());
            }

            CIMRenderer? renderer;
            string? sourceLayerName;
            try
            {
                (renderer, sourceLayerName) = ReadFirstFeatureRendererFromLayerFile(fullPath!);
            }
            catch (Exception ex)
            {
                return MutationOperationResult.Failure(
                    "layer.symbology_read_failed",
                    ex.Message,
                    warnings: new[] { $"Failed to read renderer from '{fullPath}'." });
            }

            if (renderer is null)
            {
                return MutationOperationResult.Failure("layer.symbology_not_found", "No feature renderer was found in the .lyrx file.");
            }

            var beforeRenderer = RendererSummary(featureLayer);
            if (!request.DryRun)
            {
                if (!featureLayer.CanSetRenderer(renderer))
                {
                    return MutationOperationResult.Failure(
                        "layer.renderer_not_supported",
                        "ArcGIS Pro rejected the .lyrx renderer for the target layer.");
                }

                featureLayer.SetRenderer(renderer);
                context = RefreshObjectRegistry(Project.Current);
                layer = ResolveLayer(context, layer.Object.Id, null, null, null) ?? layer;
            }

            return MutationOperationResult.Success(new
            {
                operation = "layer.apply_symbology_from_layer",
                dryRun = request.DryRun,
                changed = true,
                target = LayerSummary(layer),
                source = new
                {
                    path = fullPath,
                    layerName = sourceLayerName,
                    rendererType = renderer.GetType().Name
                },
                before = new { renderer = beforeRenderer },
                after = new { renderer = request.DryRun ? new { type = renderer.GetType().Name } : RendererSummary(layer.Layer) },
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLayoutSetTextAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var layoutMatch = ResolveMutationLayout(context, request.Args?.GetString("layoutId"));
            if (layoutMatch.Result is not null)
            {
                return layoutMatch.Result;
            }

            var element = ResolveLayoutElement(context, request.Args?.GetString("elementId"), layoutMatch.Layout!.Object.Id);
            if (element is null)
            {
                return MutationOperationResult.Failure("layout.element_not_found", "The target layout element was not found in the target layout.");
            }

            if (element.Element is not TextElement textElement)
            {
                return MutationOperationResult.Failure("layout.unsupported_element", "layout.set_text only supports text elements.");
            }

            var newText = request.Args?.GetString("text") ?? string.Empty;
            var before = textElement.TextProperties.Text ?? string.Empty;
            if (!request.DryRun && !string.Equals(before, newText, StringComparison.Ordinal))
            {
                var properties = textElement.TextProperties;
                properties.Text = newText;
                textElement.SetTextProperties(properties);
                context = RefreshObjectRegistry(Project.Current);
                element = ResolveLayoutElement(context, element.Object.Id, layoutMatch.Layout.Object.Id) ?? element;
            }

            return MutationOperationResult.Success(new
            {
                operation = "layout.set_text",
                dryRun = request.DryRun,
                changed = !string.Equals(before, newText, StringComparison.Ordinal),
                layout = LayoutSummary(layoutMatch.Layout),
                target = LayoutElementSummary(element),
                before = new { text = before },
                after = new { text = newText },
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLayoutSetMapFrameCameraAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var layoutMatch = ResolveMutationLayout(context, request.Args?.GetString("layoutId"));
            if (layoutMatch.Result is not null)
            {
                return layoutMatch.Result;
            }

            var mapFrame = ResolveMapFrame(context, request.Args?.GetString("mapFrameId"), layoutMatch.Layout!.Object.Id);
            if (mapFrame is null)
            {
                return MutationOperationResult.Failure("layout.map_frame_not_found", "The target map frame was not found in the target layout.");
            }

            var hasExtentArgs = request.Args?.GetDouble("xMin") is not null
                || request.Args?.GetDouble("yMin") is not null
                || request.Args?.GetDouble("xMax") is not null
                || request.Args?.GetDouble("yMax") is not null;
            var scale = request.Args?.GetDouble("scale");
            var heading = request.Args?.GetDouble("heading");
            if (!hasExtentArgs && scale is null && heading is null)
            {
                return MutationOperationResult.Failure(
                    "bridge.invalid_args",
                    "Provide an extent (xMin, yMin, xMax, yMax), scale, heading, or a combination of those values.");
            }

            if (scale is not null && scale <= 0)
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "scale must be greater than zero.");
            }

            Envelope? targetExtent = null;
            if (hasExtentArgs && !TryReadExtent(request.Args, mapFrame.MapFrame.Map?.SpatialReference, out targetExtent, out var extentError))
            {
                return MutationOperationResult.Failure("bridge.invalid_args", extentError ?? "Invalid map frame extent arguments.");
            }

            var beforeCamera = CameraSummary(SafeValue(() => mapFrame.MapFrame.Camera));
            var beforeExtent = EnvelopeSummary(SafeValue(() => mapFrame.MapFrame.GetViewExtent()));
            var warnings = new List<string>();
            if (SafeValue(() => mapFrame.MapFrame.IsMapSeriesMapFrame()) is true)
            {
                warnings.Add("The target map frame is associated with a map series; ArcGIS Pro may ignore camera changes while the series drives the extent.");
            }

            if (!request.DryRun)
            {
                if (targetExtent is not null)
                {
                    mapFrame.MapFrame.SetCamera(targetExtent);
                }

                if (scale is not null || heading is not null)
                {
                    var camera = mapFrame.MapFrame.Camera;
                    if (scale is not null)
                    {
                        camera.Scale = scale.Value;
                    }

                    if (heading is not null)
                    {
                        camera.Heading = heading.Value;
                    }

                    mapFrame.MapFrame.SetCamera(camera);
                }

                context = RefreshObjectRegistry(Project.Current);
                mapFrame = ResolveMapFrame(context, mapFrame.Object.Id, layoutMatch.Layout.Object.Id) ?? mapFrame;
            }

            return MutationOperationResult.Success(
                new
                {
                    operation = "layout.set_map_frame_camera",
                    dryRun = request.DryRun,
                    changed = true,
                    layout = LayoutSummary(layoutMatch.Layout),
                    target = MapFrameSummary(mapFrame),
                    before = new
                    {
                        camera = beforeCamera,
                        extent = beforeExtent
                    },
                    after = new
                    {
                        extent = targetExtent is null ? null : EnvelopeSummary(targetExtent),
                        scale,
                        heading,
                        camera = request.DryRun ? null : CameraSummary(SafeValue(() => mapFrame.MapFrame.Camera)),
                        viewExtent = request.DryRun ? null : EnvelopeSummary(SafeValue(() => mapFrame.MapFrame.GetViewExtent()))
                    },
                    checkedUtc = DateTimeOffset.UtcNow
                },
                warnings: warnings);
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLayoutSetSurroundVisibilityAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var layoutMatch = ResolveMutationLayout(context, request.Args?.GetString("layoutId"));
            if (layoutMatch.Result is not null)
            {
                return layoutMatch.Result;
            }

            var element = ResolveLayoutElement(context, request.Args?.GetString("elementId"), layoutMatch.Layout!.Object.Id);
            if (element is null)
            {
                return MutationOperationResult.Failure("layout.element_not_found", "The target layout element was not found in the target layout.");
            }

            if (element.Element is not NorthArrow && element.Element is not ScaleBar)
            {
                return MutationOperationResult.Failure(
                    "layout.unsupported_element",
                    "layout.set_surround_visibility only supports existing north arrow and scale bar elements.");
            }

            var visible = request.Args?.GetBoolean("visible");
            if (visible is null)
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "visible must be true or false.");
            }

            var before = element.Element.IsVisible;
            if (!request.DryRun && before != visible.Value)
            {
                element.Element.SetVisible(visible.Value);
                context = RefreshObjectRegistry(Project.Current);
                element = ResolveLayoutElement(context, element.Object.Id, layoutMatch.Layout.Object.Id) ?? element;
            }

            return MutationOperationResult.Success(new
            {
                operation = "layout.set_surround_visibility",
                dryRun = request.DryRun,
                changed = before != visible.Value,
                layout = LayoutSummary(layoutMatch.Layout),
                target = LayoutElementSummary(element),
                before = new { visible = before },
                after = new { visible = visible.Value },
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLegendSetVisibilityAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var resolved = ResolveMutationLegend(context, request.Args?.GetString("layoutId"), request.Args?.GetString("elementId"));
            if (resolved.Result is not null)
            {
                return resolved.Result;
            }

            var visible = request.Args?.GetBoolean("visible");
            if (visible is null)
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "visible must be true or false.");
            }

            var legend = resolved.Legend!;
            var before = legend.Element.IsVisible;
            if (!request.DryRun && before != visible.Value)
            {
                legend.Element.SetVisible(visible.Value);
                context = RefreshObjectRegistry(Project.Current);
                legend = ResolveLegendElement(context, legend.Object.Id, resolved.Layout!.Object.Id) ?? legend;
            }

            return MutationOperationResult.Success(new
            {
                operation = "legend.set_visibility",
                dryRun = request.DryRun,
                changed = before != visible.Value,
                layout = LayoutSummary(resolved.Layout!),
                target = LegendSummary(legend, context),
                before = new { visible = before },
                after = new { visible = visible.Value },
                checkedUtc = DateTimeOffset.UtcNow
            });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLegendSetLayoutAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var resolved = ResolveMutationLegend(context, request.Args?.GetString("layoutId"), request.Args?.GetString("elementId"));
            if (resolved.Result is not null)
            {
                return resolved.Result;
            }

            var legend = resolved.Legend!;
            var x = request.Args?.GetDouble("x");
            var y = request.Args?.GetDouble("y");
            var width = request.Args?.GetDouble("width");
            var height = request.Args?.GetDouble("height");
            var columns = request.Args?.GetInt32("columns");
            var fittingStrategyText = request.Args?.GetString("fittingStrategy");
            var showTitle = request.Args?.GetBoolean("showTitle");
            var title = request.Args?.GetString("title");
            var minFontSize = request.Args?.GetDouble("minFontSize");
            var balanceColumns = request.Args?.GetBoolean("balanceColumns");
            var makeColumnsSameWidth = request.Args?.GetBoolean("makeColumnsSameWidth");

            if (width is not null && width <= 0 || height is not null && height <= 0)
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "Legend width and height must be greater than zero when provided.");
            }

            if (columns is not null && (columns < 1 || columns > 12))
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "columns must be between 1 and 12.");
            }

            if (minFontSize is not null && (minFontSize < 4 || minFontSize > 24))
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "minFontSize must be between 4 and 24 points.");
            }

            LegendFittingStrategy? fittingStrategy = null;
            if (!string.IsNullOrWhiteSpace(fittingStrategyText)
                && !TryParseLegendFittingStrategy(fittingStrategyText, out fittingStrategy, out var allowed))
            {
                return MutationOperationResult.Failure(
                    "bridge.invalid_args",
                    $"Unsupported fittingStrategy '{fittingStrategyText}'. Supported values: {string.Join(", ", allowed)}.");
            }

            if (x is null && y is null && width is null && height is null && columns is null
                && fittingStrategy is null && showTitle is null && title is null && minFontSize is null
                && balanceColumns is null && makeColumnsSameWidth is null)
            {
                return MutationOperationResult.Failure(
                    "bridge.invalid_args",
                    "Provide at least one layout property: x, y, width, height, columns, fittingStrategy, showTitle, title, minFontSize, balanceColumns, or makeColumnsSameWidth.");
            }

            var before = LegendDetail(legend, context);
            if (!request.DryRun)
            {
                if (x is not null)
                {
                    legend.Element.SetX(x.Value);
                }

                if (y is not null)
                {
                    legend.Element.SetY(y.Value);
                }

                if (width is not null)
                {
                    legend.Element.SetWidth(width.Value);
                }

                if (height is not null)
                {
                    legend.Element.SetHeight(height.Value);
                }

                var cim = GetLegendDefinition(legend);
                if (cim is not null)
                {
                    if (columns is not null)
                    {
                        cim.Columns = columns.Value;
                    }

                    if (fittingStrategy is not null)
                    {
                        cim.FittingStrategy = fittingStrategy.Value;
                    }

                    if (showTitle is not null)
                    {
                        cim.ShowTitle = showTitle.Value;
                    }

                    if (title is not null)
                    {
                        cim.Title = title;
                    }

                    if (minFontSize is not null)
                    {
                        cim.MinFontSize = minFontSize.Value;
                    }

                    if (balanceColumns is not null)
                    {
                        cim.BalanceColumns = balanceColumns.Value;
                    }

                    if (makeColumnsSameWidth is not null)
                    {
                        cim.MakeColumnsSameWidth = makeColumnsSameWidth.Value;
                    }

                    legend.Legend.SetDefinition(cim);
                }

                context = RefreshObjectRegistry(Project.Current);
                legend = ResolveLegendElement(context, legend.Object.Id, resolved.Layout!.Object.Id) ?? legend;
            }

            return MutationOperationResult.Success(
                new
                {
                    operation = "legend.set_layout",
                    dryRun = request.DryRun,
                    changed = true,
                    layout = LayoutSummary(resolved.Layout!),
                    target = LegendSummary(legend, context),
                    before,
                    requested = new
                    {
                        x,
                        y,
                        width,
                        height,
                        columns,
                        fittingStrategy = fittingStrategy?.ToString() ?? fittingStrategyText,
                        showTitle,
                        title,
                        minFontSize,
                        balanceColumns,
                        makeColumnsSameWidth
                    },
                    after = request.DryRun ? null : LegendDetail(legend, context),
                    checkedUtc = DateTimeOffset.UtcNow
                },
                warnings: new[] { "Legend layout updates use ArcGIS Pro CIM properties for fitting strategy and columns; preview the layout before saving." });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLegendSetItemsAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var resolved = ResolveMutationLegend(context, request.Args?.GetString("layoutId"), request.Args?.GetString("elementId"));
            if (resolved.Result is not null)
            {
                return resolved.Result;
            }

            var showLayerIds = ReadStringArray(request.Args, "showLayerIds", "showLayerIdsCsv");
            var hideLayerIds = ReadStringArray(request.Args, "hideLayerIds", "hideLayerIdsCsv");
            var showItemNames = ReadStringArray(request.Args, "showItemNames", "showItemNamesCsv");
            var hideItemNames = ReadStringArray(request.Args, "hideItemNames", "hideItemNamesCsv");
            var allowHideThematic = request.Args?.GetBoolean("allowHideThematic") ?? false;
            if ((hideLayerIds.Length > 0 || hideItemNames.Length > 0) && !allowHideThematic)
            {
                return MutationOperationResult.Failure(
                    "legend.hide_requires_explicit_allow",
                    "Hiding legend items can remove map meaning. Set allowHideThematic=true only when the requested items are nonessential or explicitly approved.",
                    warnings: new[] { "No legend items were hidden." });
            }

            if (showLayerIds.Length == 0 && hideLayerIds.Length == 0 && showItemNames.Length == 0 && hideItemNames.Length == 0)
            {
                return MutationOperationResult.Failure(
                    "bridge.invalid_args",
                    "Provide showLayerIds/showLayerIdsCsv, hideLayerIds/hideLayerIdsCsv, showItemNames/showItemNamesCsv, or hideItemNames/hideItemNamesCsv.");
            }

            var legend = resolved.Legend!;
            var cim = GetLegendDefinition(legend);
            if (cim?.Items is null)
            {
                return MutationOperationResult.Failure(
                    "legend.items_unavailable",
                    "ArcGIS Pro did not expose editable legend items for this legend.",
                    warnings: new[] { "Use layer visibility or renderer labels as a fallback only when that preserves map meaning." });
            }

            var before = LegendDetail(legend, context);
            var layerUrisToShow = LayerUrisForIds(context, showLayerIds);
            var layerUrisToHide = LayerUrisForIds(context, hideLayerIds);
            var changes = ApplyLegendItemVisibilityChanges(
                cim.Items,
                layerUrisToShow,
                layerUrisToHide,
                showItemNames,
                hideItemNames);

            if (!request.DryRun && changes.ChangedCount > 0)
            {
                legend.Legend.SetDefinition(cim);
                context = RefreshObjectRegistry(Project.Current);
                legend = ResolveLegendElement(context, legend.Object.Id, resolved.Layout!.Object.Id) ?? legend;
            }

            var warnings = new List<string>
            {
                "Legend item visibility was edited on the legend only; underlying map layer visibility was not changed.",
                "CIM legend item changes can be overwritten by legend synchronization or renderer changes; preview before saving."
            };
            warnings.AddRange(changes.Warnings);

            return MutationOperationResult.Success(
                new
                {
                    operation = "legend.set_items",
                    dryRun = request.DryRun,
                    changed = changes.ChangedCount > 0,
                    layout = LayoutSummary(resolved.Layout!),
                    target = LegendSummary(legend, context),
                    requested = new { showLayerIds, hideLayerIds, showItemNames, hideItemNames, allowHideThematic },
                    changes = changes.Items,
                    before,
                    after = request.DryRun ? null : LegendDetail(legend, context),
                    checkedUtc = DateTimeOffset.UtcNow
                },
                warnings: warnings);
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLegendRenameItemsAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var resolved = ResolveMutationLegend(context, request.Args?.GetString("layoutId"), request.Args?.GetString("elementId"));
            if (resolved.Result is not null)
            {
                return resolved.Result;
            }

            var exactName = request.Args?.GetString("exactName");
            var contains = request.Args?.GetString("contains");
            var replacement = request.Args?.GetString("replacement");
            var find = request.Args?.GetString("find");
            var replace = request.Args?.GetString("replace");
            if ((string.IsNullOrWhiteSpace(exactName) && string.IsNullOrWhiteSpace(contains) && string.IsNullOrWhiteSpace(find))
                || (replacement is null && replace is null))
            {
                return MutationOperationResult.Failure(
                    "bridge.invalid_args",
                    "Provide exactName or contains with replacement, or provide find and replace.");
            }

            var legend = resolved.Legend!;
            var cim = GetLegendDefinition(legend);
            if (cim?.Items is null)
            {
                return MutationOperationResult.Failure("legend.items_unavailable", "ArcGIS Pro did not expose editable legend items for this legend.");
            }

            var before = LegendDetail(legend, context);
            var changes = new List<object>();
            foreach (var item in cim.Items)
            {
                var original = item.Name ?? string.Empty;
                string? next = null;
                if (!string.IsNullOrWhiteSpace(find))
                {
                    next = original.Replace(find, replace ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                }
                else if (!string.IsNullOrWhiteSpace(exactName)
                    && string.Equals(original, exactName, StringComparison.OrdinalIgnoreCase))
                {
                    next = replacement ?? string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(contains)
                    && original.Contains(contains, StringComparison.OrdinalIgnoreCase))
                {
                    next = replacement ?? string.Empty;
                }

                if (next is null || string.Equals(original, next, StringComparison.Ordinal))
                {
                    continue;
                }

                item.Name = next;
                changes.Add(new { itemLayer = item.Layer, before = original, after = next });
            }

            if (!request.DryRun && changes.Count > 0)
            {
                legend.Legend.SetDefinition(cim);
                context = RefreshObjectRegistry(Project.Current);
                legend = ResolveLegendElement(context, legend.Object.Id, resolved.Layout!.Object.Id) ?? legend;
            }

            return MutationOperationResult.Success(
                new
                {
                    operation = "legend.rename_items",
                    dryRun = request.DryRun,
                    changed = changes.Count > 0,
                    layout = LayoutSummary(resolved.Layout!),
                    target = LegendSummary(legend, context),
                    changes,
                    before,
                    after = request.DryRun ? null : LegendDetail(legend, context),
                    checkedUtc = DateTimeOffset.UtcNow
                },
                warnings: new[] { "Legend item names are CIM legend labels only; layer names and renderer class labels were not changed." });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLegendApplyCompactStyleAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var resolved = ResolveMutationLegend(context, request.Args?.GetString("layoutId"), request.Args?.GetString("elementId"));
            if (resolved.Result is not null)
            {
                return resolved.Result;
            }

            var legend = resolved.Legend!;
            var cim = GetLegendDefinition(legend);
            if (cim is null)
            {
                return MutationOperationResult.Failure("legend.cim_unavailable", "ArcGIS Pro did not expose a CIM legend definition.");
            }

            var before = LegendDetail(legend, context);
            var itemCount = cim.Items?.Length ?? 0;
            var requestedColumns = request.Args?.GetInt32("columns");
            var columns = requestedColumns ?? Math.Clamp((int)Math.Ceiling(Math.Max(itemCount, 1) / 8.0), 1, 3);
            var minFontSize = request.Args?.GetDouble("minFontSize") ?? 6.5;
            var fontSize = request.Args?.GetDouble("fontSize") ?? 7.5;
            var patchWidth = request.Args?.GetDouble("patchWidth") ?? 10.0;
            var patchHeight = request.Args?.GetDouble("patchHeight") ?? 6.0;
            var showTitle = request.Args?.GetBoolean("showTitle");
            if (columns < 1 || columns > 6)
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "columns must be between 1 and 6 for compact style.");
            }

            if (fontSize < 5 || fontSize > 18 || minFontSize < 4 || minFontSize > fontSize)
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "fontSize must be 5-18 and minFontSize must be 4 through fontSize.");
            }

            if (patchWidth <= 0 || patchHeight <= 0)
            {
                return MutationOperationResult.Failure("bridge.invalid_args", "patchWidth and patchHeight must be greater than zero.");
            }

            ApplyCompactLegendCim(cim, columns, minFontSize, fontSize, patchWidth, patchHeight, showTitle);

            if (!request.DryRun)
            {
                legend.Legend.SetDefinition(cim);
                context = RefreshObjectRegistry(Project.Current);
                legend = ResolveLegendElement(context, legend.Object.Id, resolved.Layout!.Object.Id) ?? legend;
            }

            return MutationOperationResult.Success(
                new
                {
                    operation = "legend.apply_compact_style",
                    dryRun = request.DryRun,
                    changed = true,
                    layout = LayoutSummary(resolved.Layout!),
                    target = LegendSummary(legend, context),
                    style = new
                    {
                        fittingStrategy = LegendFittingStrategy.AdjustColumnsAndSize.ToString(),
                        columns,
                        minFontSize,
                        fontSize,
                        patchWidth,
                        patchHeight,
                        showTitle
                    },
                    before,
                    after = request.DryRun ? null : LegendDetail(legend, context),
                    checkedUtc = DateTimeOffset.UtcNow
                },
                warnings: new[]
                {
                    "Compact style uses CIM-only legend formatting; preview before saving.",
                    "No legend items or map layers were hidden."
                });
        });
        return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleLegendQaPreviewAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dpi = NormalizeDpi(request.Args?.GetInt32("dpi"), 144);
        var result = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            var resolved = ResolveMutationLegend(context, request.Args?.GetString("layoutId"), request.Args?.GetString("elementId"));
            if (resolved.Result is not null)
            {
                return new LegendQaOperationResult(resolved.Result, Array.Empty<BridgeArtifact>());
            }

            var legend = resolved.Legend!;
            var beforeState = LegendDetail(legend, context);
            var beforePreview = request.DryRun ? null : ExportLayoutPreview(resolved.Layout!.Object.Id, null, dpi);
            var risks = LegendQaRisks(legend, context);
            var action = ChooseLegendQaAction(legend);
            object? afterState = null;
            VisualOperationResult? afterPreview = null;

            if (!request.DryRun && action.Applied)
            {
                var cim = GetLegendDefinition(legend);
                if (cim is not null)
                {
                    ApplyCompactLegendCim(
                        cim,
                        action.Columns,
                        action.MinFontSize,
                        action.FontSize,
                        action.PatchWidth,
                        action.PatchHeight,
                        showTitle: null);
                    legend.Legend.SetDefinition(cim);
                }

                context = RefreshObjectRegistry(Project.Current);
                legend = ResolveLegendElement(context, legend.Object.Id, resolved.Layout!.Object.Id) ?? legend;
                afterState = LegendDetail(legend, context);
                afterPreview = ExportLayoutPreview(resolved.Layout.Object.Id, null, dpi);
            }

            var artifacts = new List<BridgeArtifact>();
            if (beforePreview?.Artifact is not null)
            {
                artifacts.Add(beforePreview.Artifact);
            }

            if (afterPreview?.Artifact is not null)
            {
                artifacts.Add(afterPreview.Artifact);
            }

            var data = new
            {
                operation = "legend.qa_preview",
                dryRun = request.DryRun,
                layout = LayoutSummary(resolved.Layout!),
                legend = LegendSummary(legend, context),
                before = beforeState,
                beforePreview = beforePreview?.Data,
                qa = new
                {
                    risks,
                    conservativeAction = action
                },
                after = afterState,
                afterPreview = afterPreview?.Data,
                checkedUtc = DateTimeOffset.UtcNow
            };

            var warnings = new List<string>
            {
                "QA preview uses metadata heuristics plus exported layout previews; inspect artifacts before saving.",
                "The automatic edit only compacts typography, patches, columns, and fitting strategy. It does not hide legend items."
            };
            if (beforePreview is { Ok: false })
            {
                warnings.Add(beforePreview.ErrorMessage ?? "Before preview export failed.");
            }

            if (afterPreview is { Ok: false })
            {
                warnings.Add(afterPreview.ErrorMessage ?? "After preview export failed.");
            }

            return new LegendQaOperationResult(
                MutationOperationResult.Success(data, warnings: warnings),
                artifacts);
        });

        return result.Result.Ok
            ? BridgeResponse.Success(
                request.Id,
                result.Result.Data,
                stopwatch.ElapsedMilliseconds,
                messages: result.Result.Messages,
                warnings: result.Result.Warnings,
                artifacts: result.Artifacts)
            : result.Result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
    }

    private async Task<BridgeResponse> HandleExportLayoutAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dpi = NormalizeDpi(request.Args?.GetInt32("dpi"), 300);
        var format = NormalizeExportFormat(request.Args?.GetString("format"));
        var overwrite = request.Args?.GetBoolean("overwrite") ?? false;
        var embedFonts = request.Args?.GetBoolean("embedFonts") ?? true;
        var georeference = request.Args?.GetBoolean("georeference") ?? true;

        try
        {
            var result = await QueuedTask.Run<ExportOperationResult>(() =>
                ExportLayout(
                    request.Args?.GetString("layoutId"),
                    request.Args?.GetString("layoutName"),
                    request.Args?.GetString("outputPath"),
                    format,
                    dpi,
                    overwrite,
                    embedFonts,
                    georeference,
                    request.DryRun));
            return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return BridgeResponse.Failure(
                request.Id,
                "export.failed",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().FullName }));
        }
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        // Project.SaveAsync/SaveAsAsync must run on the ArcGIS Pro GUI thread; calling them
        // from a QueuedTask worker throws a WPF thread-affinity InvalidOperationException.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private async Task<BridgeResponse> HandleProjectSaveAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = await QueuedTask.Run(() =>
        {
            var project = Project.Current;
            if (project is null)
            {
                return ProjectOperationSnapshot.NotLoaded();
            }

            return ProjectOperationSnapshot.FromProject(project);
        });

        if (!before.Loaded)
        {
            return BridgeResponse.Failure(request.Id, "project.not_loaded", "No ArcGIS Pro project is loaded.", stopwatch.ElapsedMilliseconds);
        }

        if (request.DryRun)
        {
            return BridgeResponse.Success(
                request.Id,
                new
                {
                    operation = "project.save",
                    dryRun = true,
                    wouldSave = true,
                    project = before,
                    checkedUtc = DateTimeOffset.UtcNow
                },
                stopwatch.ElapsedMilliseconds);
        }

        try
        {
            var saved = await RunOnUiThreadAsync(() =>
            {
                var project = Project.Current;
                if (project is null)
                {
                    throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
                }

                return project.SaveAsync();
            });
            var after = await QueuedTask.Run(() =>
            {
                RefreshObjectRegistry(Project.Current);
                return Project.Current is null ? ProjectOperationSnapshot.NotLoaded() : ProjectOperationSnapshot.FromProject(Project.Current);
            });

            return BridgeResponse.Success(
                request.Id,
                new
                {
                    operation = "project.save",
                    dryRun = false,
                    saved,
                    before,
                    after,
                    checkedUtc = DateTimeOffset.UtcNow
                },
                stopwatch.ElapsedMilliseconds,
                messages: saved ? new[] { "Project saved." } : new[] { "ArcGIS Pro reported that the project was not saved." });
        }
        catch (Exception ex)
        {
            return BridgeResponse.Failure(
                request.Id,
                "project.save_failed",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().FullName }));
        }
    }

    private async Task<BridgeResponse> HandleProjectSaveCopyAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var overwrite = request.Args?.GetBoolean("overwrite") ?? false;
        var preflight = await QueuedTask.Run(() =>
        {
            var context = RefreshObjectRegistry(Project.Current);
            if (context.Project is null)
            {
                return ProjectSaveCopyPreflight.Failure("project.not_loaded", "No ArcGIS Pro project is loaded.", null, Array.Empty<string>(), null);
            }

            if (!TryResolveAllowedOutputPath(
                    context.Project,
                    request.Args?.GetString("path"),
                    "APRX",
                    overwrite,
                    out var fullPath,
                    out var pathError,
                    out var allowedRoots))
            {
                return ProjectSaveCopyPreflight.Failure(
                    "bridge.path_not_allowed",
                    pathError ?? "Project copy path is not allowed.",
                    fullPath,
                    allowedRoots,
                    ProjectOperationSnapshot.FromProject(context.Project));
            }

            return ProjectSaveCopyPreflight.Success(fullPath!, allowedRoots, ProjectOperationSnapshot.FromProject(context.Project));
        });

        if (!preflight.Ok)
        {
            return BridgeResponse.Failure(
                request.Id,
                preflight.ErrorCode ?? "project.save_copy_failed",
                preflight.ErrorMessage ?? "Project save copy preflight failed.",
                stopwatch.ElapsedMilliseconds,
                warnings: preflight.AllowedRoots.Select(root => $"Allowed root: {root}").ToArray());
        }

        if (request.DryRun)
        {
            return BridgeResponse.Success(
                request.Id,
                new
                {
                    operation = "project.save_copy",
                    dryRun = true,
                    wouldSaveCopy = true,
                    outputPath = preflight.Path,
                    overwrite,
                    project = preflight.Project,
                    checkedUtc = DateTimeOffset.UtcNow
                },
                stopwatch.ElapsedMilliseconds,
                warnings: new[] { "ArcGIS Pro SDK SaveAsAsync opens the saved copy as the current project when run non-dry-run." });
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preflight.Path!)!);
            var saved = await RunOnUiThreadAsync(() =>
            {
                var project = Project.Current;
                if (project is null)
                {
                    throw new InvalidOperationException("No ArcGIS Pro project is loaded.");
                }

                return project.SaveAsAsync(preflight.Path!, overwrite);
            });
            var after = await QueuedTask.Run(() =>
            {
                RefreshObjectRegistry(Project.Current);
                return Project.Current is null ? ProjectOperationSnapshot.NotLoaded() : ProjectOperationSnapshot.FromProject(Project.Current);
            });

            return BridgeResponse.Success(
                request.Id,
                new
                {
                    operation = "project.save_copy",
                    dryRun = false,
                    saved,
                    outputPath = preflight.Path,
                    overwrite,
                    before = preflight.Project,
                    after,
                    checkedUtc = DateTimeOffset.UtcNow
                },
                stopwatch.ElapsedMilliseconds,
                messages: saved ? new[] { "Project saved as copy." } : new[] { "ArcGIS Pro reported that the project copy was not saved." },
                warnings: new[] { "ArcGIS Pro SDK SaveAsAsync opens the saved copy as the current project." });
        }
        catch (Exception ex)
        {
            return BridgeResponse.Failure(
                request.Id,
                "project.save_copy_failed",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().FullName }));
        }
    }

    private async Task<BridgeResponse> HandleCaptureActiveViewAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = NormalizePixelDimension(request.Args?.GetInt32("width"), 1000);
        var height = NormalizePixelDimension(request.Args?.GetInt32("height"), 700);
        var dpi = NormalizeDpi(request.Args?.GetInt32("dpi"), 96);

        try
        {
            var thumbnail = CaptureActiveViewThumbnail(width, height);
            if (thumbnail is null)
            {
                return BridgeResponse.Failure(
                    request.Id,
                    "visual.no_active_view",
                    "No ready active map or layout view is available to capture.",
                    stopwatch.ElapsedMilliseconds);
            }

            var source = await QueuedTask.Run<VisualSource>(
                () => ResolveVisualSource(thumbnail.SourceKind, thumbnail.SourceUri, thumbnail.SourceName));
            var outputPath = CreateArtifactPath(source.ArtifactRoot, "active_view", thumbnail.SourceName);
            SaveBitmapSourceAsPng(thumbnail.Bitmap, outputPath);
            var dimensions = ReadPngDimensions(outputPath);
            var artifact = RegisterImageArtifact(
                outputPath,
                source.SourceObject,
                dimensions.Width,
                dimensions.Height,
                dpi,
                "visual.capture_active_view");

            return BridgeResponse.Success(
                request.Id,
                new
                {
                    captured = true,
                    viewType = thumbnail.SourceKind,
                    source = VisualSourceSummary(source.SourceObject, thumbnail.SourceKind, thumbnail.SourceName),
                    artifact,
                    width = dimensions.Width,
                    height = dimensions.Height,
                    dpi,
                    artifactDirectory = Path.GetDirectoryName(outputPath),
                    checkedUtc = DateTimeOffset.UtcNow
                },
                stopwatch.ElapsedMilliseconds,
                artifacts: new[] { artifact });
        }
        catch (Exception ex)
        {
            return BridgeResponse.Failure(
                request.Id,
                "visual.capture_failed",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().FullName }));
        }
    }

    private async Task<BridgeResponse> HandleExportActiveMapAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = NormalizePixelDimension(request.Args?.GetInt32("width"), 1280);
        var height = NormalizePixelDimension(request.Args?.GetInt32("height"), 800);
        var dpi = NormalizeDpi(request.Args?.GetInt32("dpi"), 144);

        try
        {
            var result = await QueuedTask.Run<VisualOperationResult>(
                () => ExportActiveMapPreview(width, height, dpi));
            return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return BridgeResponse.Failure(
                request.Id,
                "visual.export_failed",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().FullName }));
        }
    }

    private async Task<BridgeResponse> HandleExportLayoutPreviewAsync(BridgeRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dpi = NormalizeDpi(request.Args?.GetInt32("dpi"), 144);

        try
        {
            var result = await QueuedTask.Run<VisualOperationResult>(
                () => ExportLayoutPreview(
                    request.Args?.GetString("layoutId"),
                    request.Args?.GetString("layoutName"),
                    dpi));
            return result.ToBridgeResponse(request.Id, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return BridgeResponse.Failure(
                request.Id,
                "visual.export_failed",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().FullName }));
        }
    }

    private VisualOperationResult ExportActiveMapPreview(int width, int height, int dpi)
    {
        var context = RefreshObjectRegistry(Project.Current);
        var mapView = MapView.Active ?? LayoutView.Active?.ActivatedMapView;
        if (mapView is null)
        {
            return VisualOperationResult.Failure("visual.no_active_map", "No active map view is available to export.");
        }

        if (SafeValue(() => mapView.IsReady) is bool ready && !ready)
        {
            return VisualOperationResult.Failure("visual.view_not_ready", "The active map view is not ready to export.");
        }

        var sourceMap = ResolveMapEntry(context.Maps, mapView.Map);
        var sourceObject = sourceMap?.Object;
        var artifactRoot = GetArtifactRoot(context.Project?.HomeFolderPath, context.Project?.Path);
        var outputPath = CreateArtifactPath(artifactRoot, "active_map", sourceObject?.DisplayName ?? mapView.Map?.Name);
        var png = new PNGFormat
        {
            Resolution = dpi,
            Width = width,
            Height = height,
            OutputFileName = outputPath
        };

        if (!png.ValidateOutputFilePath())
        {
            return VisualOperationResult.Failure("visual.invalid_output_path", $"ArcGIS Pro rejected the export path '{outputPath}'.");
        }

        mapView.Export(png);
        var dimensions = ReadPngDimensions(outputPath);
        var artifact = RegisterImageArtifact(
            outputPath,
            sourceObject,
            dimensions.Width,
            dimensions.Height,
            dpi,
            "visual.export_active_map");

        return VisualOperationResult.Success(new
        {
            exported = true,
            exportType = "activeMap",
            map = sourceMap is null ? null : MapSummary(sourceMap),
            source = VisualSourceSummary(sourceObject, "map", mapView.Map?.Name),
            artifact,
            width = dimensions.Width,
            height = dimensions.Height,
            dpi,
            artifactDirectory = Path.GetDirectoryName(outputPath),
            checkedUtc = DateTimeOffset.UtcNow
        }, artifact);
    }

    private VisualOperationResult ExportLayoutPreview(string? layoutId, string? layoutName, int dpi)
    {
        var context = RefreshObjectRegistry(Project.Current);
        if (context.Project is null)
        {
            return VisualOperationResult.Failure("project.not_loaded", "No ArcGIS Pro project is loaded.");
        }

        var match = ResolveLayout(context, layoutId, layoutName)
            ?? context.Layouts.FirstOrDefault(item => item.IsActive);
        if (match is null)
        {
            return VisualOperationResult.Failure("layout.not_found", "No matching layout was found, and no active layout is available.");
        }

        var artifactRoot = GetArtifactRoot(context.Project.HomeFolderPath, context.Project.Path);
        var outputPath = CreateArtifactPath(artifactRoot, "layout_preview", match.Object.DisplayName);
        var png = new PNGFormat
        {
            Resolution = dpi,
            OutputFileName = outputPath,
            DoClipToGraphicExtent = false
        };

        if (!png.ValidateOutputFilePath())
        {
            return VisualOperationResult.Failure("visual.invalid_output_path", $"ArcGIS Pro rejected the export path '{outputPath}'.");
        }

        match.Layout.Export(png);
        var dimensions = ReadPngDimensions(outputPath);
        var artifact = RegisterImageArtifact(
            outputPath,
            match.Object,
            dimensions.Width,
            dimensions.Height,
            dpi,
            "visual.export_layout_preview");

        return VisualOperationResult.Success(new
        {
            exported = true,
            exportType = "layoutPreview",
            layout = LayoutSummary(match),
            source = VisualSourceSummary(match.Object, "layout", match.Object.DisplayName),
            artifact,
            width = dimensions.Width,
            height = dimensions.Height,
            dpi,
            artifactDirectory = Path.GetDirectoryName(outputPath),
            checkedUtc = DateTimeOffset.UtcNow
        }, artifact);
    }

    private ExportOperationResult ExportLayout(
        string? layoutId,
        string? layoutName,
        string? outputPath,
        string format,
        int dpi,
        bool overwrite,
        bool embedFonts,
        bool georeference,
        bool dryRun)
    {
        var context = RefreshObjectRegistry(Project.Current);
        if (context.Project is null)
        {
            return ExportOperationResult.Failure("project.not_loaded", "No ArcGIS Pro project is loaded.");
        }

        var match = ResolveLayout(context, layoutId, layoutName);
        if (match is null)
        {
            return ExportOperationResult.Failure("layout.not_found", "No matching layout was found.");
        }

        if (format is not ("PNG" or "PDF"))
        {
            return ExportOperationResult.Failure("export.unsupported_format", $"Unsupported layout export format '{format}'. Supported values: PNG, PDF.");
        }

        if (!TryResolveAllowedOutputPath(context.Project, outputPath, format, overwrite, out var fullPath, out var pathError, out var allowedRoots))
        {
            return ExportOperationResult.Failure(
                "bridge.path_not_allowed",
                pathError ?? "Layout export path is not allowed.",
                allowedRoots.Select(root => $"Allowed root: {root}").ToArray());
        }

        var warnings = new List<string>();
        if (string.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase) && georeference)
        {
            warnings.Add("PNG world files are not generated for full layout exports; GeoReferenceMapFrameName is set when a map frame is available.");
        }

        BridgeArtifact? artifact = null;
        object? exportOptions = null;
        if (!dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath!)!);
            if (File.Exists(fullPath) && overwrite)
            {
                File.Delete(fullPath);
            }

            if (string.Equals(format, "PDF", StringComparison.OrdinalIgnoreCase))
            {
                var pdf = new PDFFormat
                {
                    Resolution = dpi,
                    OutputFileName = fullPath!,
                    DoEmbedFonts = embedFonts,
                    HasGeoRefInfo = georeference,
                    DoCompressVectorGraphics = true
                };

                if (!pdf.ValidateOutputFilePath())
                {
                    return ExportOperationResult.Failure("export.invalid_output_path", $"ArcGIS Pro rejected the export path '{fullPath}'.");
                }

                match.Layout.Export(pdf);
                artifact = RegisterFileArtifact(fullPath!, "application/pdf", match.Object, dpi, "export.layout");
                exportOptions = new
                {
                    format = "PDF",
                    dpi,
                    embedFonts,
                    georeference,
                    compressVectorGraphics = true
                };
            }
            else
            {
                var georefMapFrameName = context.MapFrames
                    .FirstOrDefault(item => string.Equals(item.LayoutObject.Id, match.Object.Id, StringComparison.OrdinalIgnoreCase))
                    ?.Object.DisplayName
                    ?? string.Empty;
                var png = new PNGFormat
                {
                    Resolution = dpi,
                    OutputFileName = fullPath!,
                    DoClipToGraphicExtent = false,
                    HasWorldFile = georeference,
                    GeoReferenceMapFrameName = georefMapFrameName
                };

                if (!png.ValidateOutputFilePath())
                {
                    return ExportOperationResult.Failure("export.invalid_output_path", $"ArcGIS Pro rejected the export path '{fullPath}'.");
                }

                match.Layout.Export(png);
                var dimensions = ReadPngDimensions(fullPath!);
                artifact = RegisterImageArtifact(fullPath!, match.Object, dimensions.Width, dimensions.Height, dpi, "export.layout");
                exportOptions = new
                {
                    format = "PNG",
                    dpi,
                    georeference,
                    geoReferenceMapFrameName = georefMapFrameName,
                    width = dimensions.Width,
                    height = dimensions.Height
                };
            }
        }
        else
        {
            exportOptions = new
            {
                format,
                dpi,
                embedFonts = string.Equals(format, "PDF", StringComparison.OrdinalIgnoreCase) ? embedFonts : null as bool?,
                georeference
            };
        }

        return ExportOperationResult.Success(
            new
            {
                operation = "export.layout",
                dryRun,
                exported = !dryRun,
                layout = LayoutSummary(match),
                outputPath = fullPath,
                overwrite,
                options = exportOptions,
                artifact,
                artifactDirectory = Path.GetDirectoryName(fullPath!),
                checkedUtc = DateTimeOffset.UtcNow
            },
            artifact,
            warnings);
    }

    private async Task<BridgeResponse> HandleGeoprocessingExecuteToolAsync(
        BridgeRequest request,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preflight = await QueuedTask.Run(() => PrepareGeoprocessingExecution(request));
        if (!preflight.Ok)
        {
            return BridgeResponse.Failure(
                request.Id,
                preflight.ErrorCode ?? "geoprocessing.preflight_failed",
                preflight.ErrorMessage ?? "Geoprocessing preflight failed.",
                stopwatch.ElapsedMilliseconds,
                warnings: preflight.Warnings);
        }

        if (request.DryRun)
        {
            return BridgeResponse.Success(
                request.Id,
                new
                {
                    operation = "geoprocessing.execute_tool",
                    dryRun = true,
                    wouldExecute = true,
                    toolName = preflight.ToolName,
                    parameters = preflight.Parameters,
                    environments = preflight.Environments,
                    addOutputsToMap = preflight.AddOutputsToMap,
                    allowDestructive = preflight.AllowDestructive,
                    destructiveTool = preflight.DestructiveTool,
                    allowedRoots = preflight.AllowedRoots,
                    checkedPaths = preflight.CheckedPaths,
                    checkedUtc = DateTimeOffset.UtcNow
                },
                stopwatch.ElapsedMilliseconds,
                warnings: preflight.Warnings);
        }

        var eventMessages = new List<object>();
        var eventLock = new object();
        GPToolExecuteEventHandler callback = (eventName, payload) =>
        {
            lock (eventLock)
            {
                if (payload is IGPMessage gpMessage)
                {
                    eventMessages.Add(GpMessageSummary(gpMessage, eventName));
                }
                else if (payload is IGPMessage[] gpMessages)
                {
                    foreach (var message in gpMessages)
                    {
                        eventMessages.Add(GpMessageSummary(message, eventName));
                    }
                }
                else if (payload is not null)
                {
                    eventMessages.Add(new
                    {
                        eventName,
                        text = payload.ToString()
                    });
                }
                else
                {
                    eventMessages.Add(new
                    {
                        eventName,
                        text = (string?)null
                    });
                }
            }
        };

        var flags = GPExecuteToolFlags.RefreshProjectItems | GPExecuteToolFlags.AddToHistory | GPExecuteToolFlags.GPThread;
        if (preflight.AddOutputsToMap)
        {
            flags |= GPExecuteToolFlags.AddOutputsToMap;
        }

        try
        {
            var beforeLayerIds = await QueuedTask.Run(() =>
                RefreshObjectRegistry(Project.Current)
                    .Layers
                    .Select(layer => layer.Object.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
            var gpStopwatch = Stopwatch.StartNew();
            var result = await Geoprocessing.ExecuteToolAsync(
                preflight.ToolName!,
                preflight.Values,
                preflight.EnvironmentValues,
                cancellationToken,
                callback,
                flags);
            gpStopwatch.Stop();

            var resultMessages = (result.Messages ?? Array.Empty<IGPMessage>())
                .Select(message => GpMessageSummary(message, null))
                .ToArray();
            var warnings = preflight.Warnings
                .Concat((result.Messages ?? Array.Empty<IGPMessage>())
                    .Where(message => message.Type == GPMessageType.Warning)
                    .Select(message => message.Text))
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var responseMessages = (result.Messages ?? Array.Empty<IGPMessage>())
                .Where(message => message.Type != GPMessageType.Warning && message.Type != GPMessageType.Error)
                .Select(message => message.Text)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var outputPaths = ExtractGpOutputPaths(preflight, result).ToArray();
            var artifacts = RegisterGpOutputArtifacts(outputPaths);
            var addedLayers = await QueuedTask.Run(() =>
            {
                var context = RefreshObjectRegistry(Project.Current);
                return context.Layers
                    .Where(layer => !beforeLayerIds.Contains(layer.Object.Id))
                    .Select(LayerSummary)
                    .ToArray();
            });

            var succeeded = !result.IsFailed && !result.IsCanceled && result.ErrorCode == 0;
            return BridgeResponse.Success(
                request.Id,
                new
                {
                    operation = "geoprocessing.execute_tool",
                    dryRun = false,
                    executed = true,
                    succeeded,
                    failed = result.IsFailed,
                    canceled = result.IsCanceled,
                    errorCode = result.ErrorCode,
                    returnValue = result.ReturnValue,
                    toolName = preflight.ToolName,
                    parameters = preflight.Parameters,
                    environments = preflight.Environments,
                    addOutputsToMap = preflight.AddOutputsToMap,
                    allowDestructive = preflight.AllowDestructive,
                    destructiveTool = preflight.DestructiveTool,
                    flags = flags.ToString(),
                    gpElapsedMs = gpStopwatch.ElapsedMilliseconds,
                    values = result.Values?.ToArray() ?? Array.Empty<string>(),
                    valueTypes = result.ValueTypes?.ToArray() ?? Array.Empty<string>(),
                    outputPaths,
                    addedLayerCount = addedLayers.Length,
                    addedLayers,
                    outputArtifacts = artifacts,
                    gpMessages = resultMessages,
                    eventMessages = eventMessages.ToArray(),
                    checkedPaths = preflight.CheckedPaths,
                    checkedUtc = DateTimeOffset.UtcNow
                },
                stopwatch.ElapsedMilliseconds,
                messages: responseMessages,
                warnings: warnings,
                artifacts: artifacts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BridgeResponse.Failure(
                request.Id,
                "geoprocessing.execute_failed",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                warnings: preflight.Warnings,
                details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().FullName }));
        }
    }

    private async Task<BridgeResponse> HandlePythonRunArcpyScriptAsync(
        BridgeRequest request,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preflight = await QueuedTask.Run(() => PrepareArcpyScriptExecution(request));
        if (!preflight.Ok)
        {
            return BridgeResponse.Failure(
                request.Id,
                preflight.ErrorCode ?? "python.preflight_failed",
                preflight.ErrorMessage ?? "ArcPy script preflight failed.",
                stopwatch.ElapsedMilliseconds,
                warnings: preflight.Warnings);
        }

        if (request.DryRun)
        {
            return BridgeResponse.Success(
                request.Id,
                new
                {
                    operation = "python.run_arcpy_script",
                    dryRun = true,
                    wouldRun = !preflight.SyntaxOnly,
                    wouldSyntaxCheck = true,
                    scriptPath = preflight.ScriptPath,
                    workingDirectory = preflight.WorkingDirectory,
                    outputDirectory = preflight.OutputDirectory,
                    manifestPath = preflight.ManifestPath,
                    toolboxPath = preflight.ToolboxPath,
                    toolPath = preflight.ToolPath,
                    arguments = JsonSerializer.Deserialize<object?>(preflight.ArgumentsJson ?? "null", JsonOptions),
                    allowedRoots = preflight.AllowedRoots,
                    checkedPaths = preflight.CheckedPaths,
                    checkedUtc = DateTimeOffset.UtcNow
                },
                stopwatch.ElapsedMilliseconds,
                warnings: preflight.Warnings);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preflight.ToolboxPath!)!);
            Directory.CreateDirectory(Path.GetDirectoryName(preflight.ManifestPath!)!);
            EnsureArcpyRunnerToolbox(preflight.ToolboxPath!);
        }
        catch (Exception ex)
        {
            return BridgeResponse.Failure(
                request.Id,
                "python.runner_prepare_failed",
                $"Failed to prepare the ArcPy runner toolbox: {ex.Message}",
                stopwatch.ElapsedMilliseconds,
                warnings: preflight.Warnings,
                details: JsonSerializer.SerializeToElement(new { exception = ex.GetType().FullName }));
        }

        var eventMessages = new List<object>();
        var eventLock = new object();
        GPToolExecuteEventHandler callback = (eventName, payload) =>
        {
            lock (eventLock)
            {
                if (payload is IGPMessage gpMessage)
                {
                    eventMessages.Add(GpMessageSummary(gpMessage, eventName));
                }
                else if (payload is IGPMessage[] gpMessages)
                {
                    foreach (var message in gpMessages)
                    {
                        eventMessages.Add(GpMessageSummary(message, eventName));
                    }
                }
                else
                {
                    eventMessages.Add(new
                    {
                        eventName,
                        text = payload?.ToString()
                    });
                }
            }
        };

        var flags = GPExecuteToolFlags.RefreshProjectItems | GPExecuteToolFlags.AddToHistory | GPExecuteToolFlags.GPThread;
        try
        {
            var beforeLayerIds = await QueuedTask.Run(() =>
                RefreshObjectRegistry(Project.Current)
                    .Layers
                    .Select(layer => layer.Object.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
            var runStopwatch = Stopwatch.StartNew();
            var result = await Geoprocessing.ExecuteToolAsync(
                preflight.ToolPath!,
                new[]
                {
                    preflight.ScriptPath!,
                    preflight.ArgumentsJson ?? "[]",
                    preflight.WorkingDirectory!,
                    preflight.OutputDirectory!,
                    preflight.ManifestPath!
                },
                Array.Empty<KeyValuePair<string, string>>(),
                cancellationToken,
                callback,
                flags);
            runStopwatch.Stop();

            var manifest = TryReadJsonFile(preflight.ManifestPath!);
            var artifacts = RegisterArcpyScriptArtifacts(preflight, manifest);
            var addedLayers = await QueuedTask.Run(() =>
            {
                var context = RefreshObjectRegistry(Project.Current);
                return context.Layers
                    .Where(layer => !beforeLayerIds.Contains(layer.Object.Id))
                    .Select(LayerSummary)
                    .ToArray();
            });

            var manifestSucceeded = manifest.HasValue && TryGetJsonBoolean(manifest.Value, "succeeded") == true;
            var manifestSyntaxOk = manifest.HasValue && TryGetJsonBoolean(manifest.Value, "syntaxOk") == true;
            var succeeded = !result.IsFailed
                && !result.IsCanceled
                && result.ErrorCode == 0
                && (preflight.SyntaxOnly ? manifestSyntaxOk : manifestSucceeded);
            var resultMessages = (result.Messages ?? Array.Empty<IGPMessage>())
                .Select(message => GpMessageSummary(message, null))
                .ToArray();
            var warnings = preflight.Warnings
                .Concat((result.Messages ?? Array.Empty<IGPMessage>())
                    .Where(message => message.Type == GPMessageType.Warning)
                    .Select(message => message.Text))
                .Concat(ExtractJsonStringArray(manifest, "warnings"))
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var responseMessages = (result.Messages ?? Array.Empty<IGPMessage>())
                .Where(message => message.Type != GPMessageType.Warning && message.Type != GPMessageType.Error)
                .Select(message => message.Text)
                .Concat(ExtractJsonStringArray(manifest, "messages"))
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return BridgeResponse.Success(
                request.Id,
                new
                {
                    operation = "python.run_arcpy_script",
                    dryRun = false,
                    executed = !preflight.SyntaxOnly,
                    syntaxOnly = preflight.SyntaxOnly,
                    succeeded,
                    failed = result.IsFailed || (!preflight.SyntaxOnly && !manifestSucceeded),
                    canceled = result.IsCanceled,
                    errorCode = result.ErrorCode,
                    returnValue = result.ReturnValue,
                    scriptPath = preflight.ScriptPath,
                    workingDirectory = preflight.WorkingDirectory,
                    outputDirectory = preflight.OutputDirectory,
                    manifestPath = preflight.ManifestPath,
                    toolboxPath = preflight.ToolboxPath,
                    toolPath = preflight.ToolPath,
                    flags = flags.ToString(),
                    runElapsedMs = runStopwatch.ElapsedMilliseconds,
                    values = result.Values?.ToArray() ?? Array.Empty<string>(),
                    valueTypes = result.ValueTypes?.ToArray() ?? Array.Empty<string>(),
                    addedLayerCount = addedLayers.Length,
                    addedLayers,
                    generatedFiles = ExtractGeneratedFilePaths(manifest),
                    outputArtifacts = artifacts,
                    manifest = manifest.HasValue ? manifest.Value : (JsonElement?)null,
                    gpMessages = resultMessages,
                    eventMessages = eventMessages.ToArray(),
                    allowedRoots = preflight.AllowedRoots,
                    checkedPaths = preflight.CheckedPaths,
                    checkedUtc = DateTimeOffset.UtcNow
                },
                stopwatch.ElapsedMilliseconds,
                messages: responseMessages,
                warnings: warnings,
                artifacts: artifacts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var manifest = TryReadJsonFile(preflight.ManifestPath!);
            return BridgeResponse.Failure(
                request.Id,
                "python.execute_failed",
                ex.Message,
                stopwatch.ElapsedMilliseconds,
                warnings: preflight.Warnings.Concat(ExtractJsonStringArray(manifest, "warnings")).ToArray(),
                details: JsonSerializer.SerializeToElement(new
                {
                    exception = ex.GetType().FullName,
                    scriptPath = preflight.ScriptPath,
                    manifest = manifest.HasValue ? manifest.Value : (JsonElement?)null
                }));
        }
    }

    private Task<object> CaptureArtifactStateAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var artifactId = request.Args?.GetString("artifactId");
        var artifact = _objectRegistry.FindById(artifactId);
        if (artifact is null || !string.Equals(artifact.Kind, "artifact", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<object>(new
            {
                found = false,
                artifactId,
                checkedUtc = DateTimeOffset.UtcNow
            });
        }

        return Task.FromResult<object>(new
        {
            found = true,
            artifact,
            exists = artifact.Path is not null && File.Exists(artifact.Path),
            checkedUtc = DateTimeOffset.UtcNow
        });
    }

    private static object LegendGuardrailSummary()
    {
        return new
        {
            preserveMapMeaning = true,
            neverHideItemsWithoutAllowHideThematic = true,
            preferCompactTypographyColumnsAndPatchesBeforeHiding = true,
            cimOnlyEditsReturnWarnings = true,
            previewBeforeSaveRecommended = true
        };
    }

    private static object LegendSummary(LayoutElementEntry entry, RegistryContext context)
    {
        var cim = GetLegendDefinition(entry);
        var mapFrame = ResolveMapFrameForLegend(context, entry);
        var linkedMap = mapFrame is null
            ? null
            : context.Maps.FirstOrDefault(map => string.Equals(map.Object.Id, mapFrame.MapId, StringComparison.OrdinalIgnoreCase));

        return new
        {
            entry.Object.Id,
            name = entry.Object.DisplayName,
            displayName = entry.Object.DisplayName,
            entry.Object.Type,
            entry.Object.ParentId,
            entry.Object.ParentKind,
            entry.Object.ParentName,
            layoutId = entry.LayoutObject.Id,
            layoutName = entry.LayoutObject.DisplayName,
            mapFrameId = entry.MapFrameId,
            mapFrameName = mapFrame?.Object.DisplayName,
            mapId = linkedMap?.Object.Id,
            mapName = linkedMap?.Object.DisplayName,
            visible = entry.Element.IsVisible,
            locked = entry.Element.IsLocked,
            zOrder = entry.Element.ZOrder,
            title = cim?.Title,
            showTitle = cim?.ShowTitle,
            fittingStrategy = cim?.FittingStrategy.ToString(),
            columns = cim?.Columns,
            itemCount = cim?.Items?.Length ?? 0,
            overflowing = LegendOverflowing(entry.Legend)
        };
    }

    private static object LegendDetail(LayoutElementEntry entry, RegistryContext context)
    {
        var cim = GetLegendDefinition(entry);
        var mapFrame = ResolveMapFrameForLegend(context, entry);
        var linkedMap = mapFrame is null
            ? null
            : context.Maps.FirstOrDefault(map => string.Equals(map.Object.Id, mapFrame.MapId, StringComparison.OrdinalIgnoreCase));
        var items = cim?.Items is null
            ? Array.Empty<object>()
            : cim.Items.Select((item, index) => LegendItemSummary(item, index, context)).ToArray();

        return new
        {
            summary = LegendSummary(entry, context),
            linked = new
            {
                layout = LayoutSummary(new LayoutEntry(entry.Layout, entry.LayoutObject, SameUri(LayoutView.Active?.Layout?.URI, entry.Layout.URI))),
                mapFrame = mapFrame is null ? null : MapFrameSummary(mapFrame),
                map = linkedMap is null ? null : MapSummary(linkedMap)
            },
            bounds = new
            {
                x = SafeValue(() => entry.Element.GetX()),
                y = SafeValue(() => entry.Element.GetY()),
                width = SafeValue(() => entry.Element.GetWidth()),
                height = SafeValue(() => entry.Element.GetHeight()),
                rotation = SafeValue(() => entry.Element.GetRotation())
            },
            fitting = cim is null
                ? null
                : new
                {
                    strategy = cim.FittingStrategy.ToString(),
                    columns = cim.Columns,
                    minFontSize = cim.MinFontSize,
                    autoFonts = cim.AutoFonts,
                    autoAdd = cim.AutoAdd,
                    autoReorder = cim.AutoReorder,
                    autoVisibility = cim.AutoVisibility,
                    balanceColumns = cim.BalanceColumns,
                    makeColumnsSameWidth = cim.MakeColumnsSameWidth,
                    rightToLeft = cim.RightToLeft
                },
            title = cim is null
                ? null
                : new
                {
                    text = cim.Title,
                    showTitle = cim.ShowTitle,
                    titleGap = cim.TitleGap,
                    symbol = TextSymbolSummary(cim.TitleSymbol)
                },
            spacing = cim is null
                ? null
                : new
                {
                    itemGap = cim.ItemGap,
                    classGap = cim.ClassGap,
                    groupGap = cim.GroupGap,
                    headingGap = cim.HeadingGap,
                    layerNameGap = cim.LayerNameGap,
                    patchGap = cim.PatchGap,
                    textGap = cim.TextGap,
                    defaultPatchWidth = cim.DefaultPatchWidth,
                    defaultPatchHeight = cim.DefaultPatchHeight
                },
            items,
            excludedLayers = cim?.ExcludedLayers ?? Array.Empty<string>(),
            qa = LegendQaRisks(entry, context)
        };
    }

    private static object LegendItemSummary(CIMLegendItem item, int index, RegistryContext context)
    {
        var linkedLayer = context.Layers.FirstOrDefault(layer => SameUri(layer.Layer.URI, item.Layer));
        return new
        {
            index,
            name = item.Name,
            layerUri = item.Layer,
            layerId = linkedLayer?.Object.Id,
            layerName = linkedLayer?.Object.DisplayName,
            visible = item.IsVisible,
            autoVisibility = item.AutoVisibility,
            showLayerName = item.ShowLayerName,
            showGroupLayerName = item.ShowGroupLayerName,
            showHeading = item.ShowHeading,
            showLabels = item.ShowLabels,
            showDescription = item.ShowDescription,
            showCounts = item.ShowCounts,
            patchWidth = item.PatchWidth,
            patchHeight = item.PatchHeight,
            scaleToPatch = item.ScaleToPatch,
            newColumn = item.NewColumn,
            manualColumn = item.ManualColumn,
            layerNameSymbol = TextSymbolSummary(item.LayerNameSymbol),
            labelSymbol = TextSymbolSummary(item.LabelSymbol),
            headingSymbol = TextSymbolSummary(item.HeadingSymbol),
            descriptionSymbol = TextSymbolSummary(item.DescriptionSymbol)
        };
    }

    private static object TextSymbolSummary(object? symbol)
    {
        if (symbol is null)
        {
            return new
            {
                available = false
            };
        }

        return new
        {
            available = true,
            type = symbol.GetType().Name,
            fontFamily = SafeProperty(symbol, "FontFamilyName") ?? SafeProperty(symbol, "FontFamily"),
            fontStyle = SafeProperty(symbol, "FontStyleName") ?? SafeProperty(symbol, "FontStyle"),
            height = SafeProperty(symbol, "Height"),
            size = SafeProperty(symbol, "Size"),
            fontSize = SafeProperty(symbol, "FontSize")
        };
    }

    private static object LegendQaRisks(LayoutElementEntry entry, RegistryContext context)
    {
        var cim = GetLegendDefinition(entry);
        var itemCount = cim?.Items?.Length ?? 0;
        var columns = Math.Max(cim?.Columns ?? 1, 1);
        var width = SafeDouble(SafeValue(() => entry.Element.GetWidth()));
        var height = SafeDouble(SafeValue(() => entry.Element.GetHeight()));
        var overflowing = LegendOverflowing(entry.Legend);
        var visibleItems = cim?.Items?.Count(item => item.IsVisible) ?? 0;
        var area = width * height;

        return new
        {
            overflowing,
            itemCount,
            visibleItemCount = visibleItems,
            columns,
            itemsPerColumn = columns == 0 ? itemCount : (double)itemCount / columns,
            boundsArea = area,
            risks = new[]
                {
                    overflowing is true ? "overflow" : null,
                    itemCount > columns * 9 ? "many_items_per_column" : null,
                    area > 0 && itemCount > 0 && area / Math.Max(itemCount, 1) < 0.22 ? "low_area_per_item" : null,
                    width > 0 && width < 1.2 ? "narrow_legend" : null,
                    height > 0 && height < 0.8 ? "short_legend" : null
                }
                .Where(item => item is not null)
                .ToArray(),
            recommendation = overflowing is true || itemCount > columns * 9
                ? "Use AdjustColumnsAndSize, smaller readable typography, compact patches, and modest additional columns before hiding any items."
                : "Legend metadata does not indicate an obvious fit problem; preview remains authoritative."
        };
    }

    private static LegendQaAction ChooseLegendQaAction(LayoutElementEntry entry)
    {
        var cim = GetLegendDefinition(entry);
        var itemCount = cim?.Items?.Length ?? 0;
        var currentColumns = Math.Max(cim?.Columns ?? 1, 1);
        var columns = Math.Clamp(Math.Max(currentColumns, (int)Math.Ceiling(Math.Max(itemCount, 1) / 8.0)), 1, 3);
        return new LegendQaAction(
            Applied: cim is not null,
            Description: "Apply compact legend style without hiding items.",
            Columns: columns,
            FittingStrategy: LegendFittingStrategy.AdjustColumnsAndSize.ToString(),
            MinFontSize: 6.5,
            FontSize: 7.5,
            PatchWidth: 10.0,
            PatchHeight: 6.0);
    }

    private static void ApplyCompactLegendCim(
        CIMLegend cim,
        int columns,
        double minFontSize,
        double fontSize,
        double patchWidth,
        double patchHeight,
        bool? showTitle)
    {
        cim.FittingStrategy = LegendFittingStrategy.AdjustColumnsAndSize;
        cim.Columns = columns;
        cim.MinFontSize = minFontSize;
        cim.BalanceColumns = true;
        cim.MakeColumnsSameWidth = false;
        cim.DefaultPatchWidth = patchWidth;
        cim.DefaultPatchHeight = patchHeight;
        cim.ItemGap = Math.Min(4, cim.ItemGap <= 0 ? 3 : cim.ItemGap);
        cim.ClassGap = Math.Min(3, cim.ClassGap <= 0 ? 2 : cim.ClassGap);
        cim.GroupGap = Math.Min(5, cim.GroupGap <= 0 ? 4 : cim.GroupGap);
        cim.HeadingGap = Math.Min(3, cim.HeadingGap <= 0 ? 2 : cim.HeadingGap);
        cim.LayerNameGap = Math.Min(3, cim.LayerNameGap <= 0 ? 2 : cim.LayerNameGap);
        cim.PatchGap = Math.Min(3, cim.PatchGap <= 0 ? 2 : cim.PatchGap);
        cim.TextGap = Math.Min(2, cim.TextGap <= 0 ? 1.5 : cim.TextGap);
        if (showTitle is not null)
        {
            cim.ShowTitle = showTitle.Value;
        }

        SetTextSymbolSizeIfPresent(cim.TitleSymbol, Math.Max(fontSize + 1.0, 8.0));
        if (cim.DefaultLegendItem is not null)
        {
            ApplyCompactLegendItem(cim.DefaultLegendItem, fontSize, patchWidth, patchHeight);
        }

        if (cim.Items is null)
        {
            return;
        }

        foreach (var item in cim.Items)
        {
            ApplyCompactLegendItem(item, fontSize, patchWidth, patchHeight);
        }
    }

    private static void ApplyCompactLegendItem(CIMLegendItem item, double fontSize, double patchWidth, double patchHeight)
    {
        item.PatchWidth = patchWidth;
        item.PatchHeight = patchHeight;
        item.ScaleToPatch = true;
        item.ShowCounts = false;
        SetTextSymbolSizeIfPresent(item.LayerNameSymbol, fontSize);
        SetTextSymbolSizeIfPresent(item.LabelSymbol, fontSize);
        SetTextSymbolSizeIfPresent(item.HeadingSymbol, fontSize);
        SetTextSymbolSizeIfPresent(item.DescriptionSymbol, Math.Max(fontSize - 0.5, 5));
    }

    private static void SetTextSymbolSizeIfPresent(object? symbol, double size)
    {
        if (symbol is null)
        {
            return;
        }

        SetNumericPropertyIfExists(symbol, "Size", size);
        SetNumericPropertyIfExists(symbol, "FontSize", size);
        SetNumericPropertyIfExists(symbol, "Height", size);
    }

    private static void SetNumericPropertyIfExists(object target, string propertyName, double value)
    {
        try
        {
            var property = target.GetType().GetProperty(propertyName);
            if (property is null || !property.CanWrite)
            {
                return;
            }

            if (property.PropertyType == typeof(double))
            {
                property.SetValue(target, value);
            }
            else if (property.PropertyType == typeof(float))
            {
                property.SetValue(target, (float)value);
            }
            else if (property.PropertyType == typeof(int))
            {
                property.SetValue(target, (int)Math.Round(value));
            }
        }
        catch
        {
            // Some symbol implementations expose read-only size members; compacting should still continue.
        }
    }

    private static LegendItemVisibilityChanges ApplyLegendItemVisibilityChanges(
        CIMLegendItem[] items,
        IReadOnlySet<string> layerUrisToShow,
        IReadOnlySet<string> layerUrisToHide,
        IReadOnlyList<string> showItemNames,
        IReadOnlyList<string> hideItemNames)
    {
        var changes = new List<object>();
        var warnings = new List<string>();
        var matchedShowLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedHideLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedShowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedHideNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var before = item.IsVisible;
            bool? target = null;
            var itemName = item.Name ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(item.Layer) && layerUrisToShow.Contains(item.Layer))
            {
                target = true;
                matchedShowLayers.Add(item.Layer);
            }

            if (!string.IsNullOrWhiteSpace(item.Layer) && layerUrisToHide.Contains(item.Layer))
            {
                target = false;
                matchedHideLayers.Add(item.Layer);
            }

            if (showItemNames.Any(name => string.Equals(name, itemName, StringComparison.OrdinalIgnoreCase)))
            {
                target = true;
                matchedShowNames.Add(itemName);
            }

            if (hideItemNames.Any(name => string.Equals(name, itemName, StringComparison.OrdinalIgnoreCase)))
            {
                target = false;
                matchedHideNames.Add(itemName);
            }

            if (target is null || before == target.Value)
            {
                continue;
            }

            item.IsVisible = target.Value;
            changes.Add(new
            {
                name = itemName,
                layerUri = item.Layer,
                before,
                after = target.Value
            });
        }

        foreach (var uri in layerUrisToShow.Except(matchedShowLayers, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"No editable legend item matched show layer URI '{uri}'.");
        }

        foreach (var uri in layerUrisToHide.Except(matchedHideLayers, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"No editable legend item matched hide layer URI '{uri}'.");
        }

        foreach (var name in showItemNames.Except(matchedShowNames, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"No editable legend item matched show item name '{name}'.");
        }

        foreach (var name in hideItemNames.Except(matchedHideNames, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"No editable legend item matched hide item name '{name}'.");
        }

        return new LegendItemVisibilityChanges(changes.Count, changes, warnings);
    }

    private static IReadOnlySet<string> LayerUrisForIds(RegistryContext context, IReadOnlyList<string> layerIds)
    {
        return context.Layers
            .Where(layer => layerIds.Contains(layer.Object.Id, StringComparer.OrdinalIgnoreCase))
            .Select(layer => layer.Layer.URI)
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    }

    private static string[] ReadStringArray(JsonObjectMap? args, string arrayKey, string csvKey)
    {
        if (args is null)
        {
            return Array.Empty<string>();
        }

        if (args.TryGetValue(arrayKey, out var arrayValue) && arrayValue.ValueKind == JsonValueKind.Array)
        {
            return arrayValue
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var csv = args.GetString(csvKey) ?? args.GetString(arrayKey);
        return string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    private static bool TryParseLegendFittingStrategy(string value, out LegendFittingStrategy? strategy, out string[] allowed)
    {
        var aliases = new Dictionary<string, LegendFittingStrategy>(StringComparer.OrdinalIgnoreCase)
        {
            ["AdjustColumns"] = LegendFittingStrategy.AdjustColumns,
            ["AdjustColumnsAndFont"] = LegendFittingStrategy.AdjustColumnsAndSize,
            ["AdjustColumnsAndSize"] = LegendFittingStrategy.AdjustColumnsAndSize,
            ["AdjustFontSize"] = LegendFittingStrategy.AdjustSize,
            ["AdjustSize"] = LegendFittingStrategy.AdjustSize,
            ["AdjustFrame"] = LegendFittingStrategy.AdjustFrame,
            ["ManualColumns"] = LegendFittingStrategy.ManualColumns
        };
        allowed = aliases.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        if (aliases.TryGetValue(value.Trim(), out var parsed))
        {
            strategy = parsed;
            return true;
        }

        strategy = null;
        return false;
    }

    private static CIMLegend? GetLegendDefinition(LayoutElementEntry entry)
    {
        try
        {
            return entry.Legend.GetDefinition() as CIMLegend;
        }
        catch
        {
            return null;
        }
    }

    private static MapFrameEntry? ResolveMapFrameForLegend(RegistryContext context, LayoutElementEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.MapFrameId))
        {
            return null;
        }

        return context.MapFrames.FirstOrDefault(item => string.Equals(item.Object.Id, entry.MapFrameId, StringComparison.OrdinalIgnoreCase));
    }

    private static double SafeDouble(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToDouble(value);
        }
        catch
        {
            return 0;
        }
    }

    private static bool? LegendOverflowing(Legend legend)
    {
        return SafeProperty(legend, "IsOverflowing") as bool?
            ?? SafeProperty(legend, "IsOverflow") as bool?
            ?? SafeProperty(legend, "Overflowing") as bool?;
    }

    private static IReadOnlyList<Map> GetMaps(Project project)
    {
        return project.GetItems<MapProjectItem>()
            .Select(item => item.GetMap())
            .Where(map => map is not null)
            .ToArray()!;
    }

    private static IReadOnlyList<Layout> GetLayouts(Project project)
    {
        return project.GetItems<LayoutProjectItem>()
            .Select(item => item.GetLayout())
            .Where(layout => layout is not null)
            .ToArray()!;
    }

    private static (LayerEntry? Layer, MutationOperationResult? Result) ResolveMutationLayer(RegistryContext context, string? layerId)
    {
        if (context.Project is null)
        {
            return (null, MutationOperationResult.Failure("project.not_loaded", "No ArcGIS Pro project is loaded."));
        }

        var layer = ResolveLayer(context, layerId, null, null, null);
        return layer is null
            ? (null, MutationOperationResult.Failure("layer.not_found", $"Layer '{layerId}' was not found."))
            : (layer, null);
    }

    private static (LayoutEntry? Layout, MutationOperationResult? Result) ResolveMutationLayout(RegistryContext context, string? layoutId)
    {
        if (context.Project is null)
        {
            return (null, MutationOperationResult.Failure("project.not_loaded", "No ArcGIS Pro project is loaded."));
        }

        var layout = ResolveLayout(context, layoutId, null);
        return layout is null
            ? (null, MutationOperationResult.Failure("layout.not_found", $"Layout '{layoutId}' was not found."))
            : (layout, null);
    }

    private static (LayoutEntry? Layout, LayoutElementEntry? Legend, MutationOperationResult? Result) ResolveMutationLegend(
        RegistryContext context,
        string? layoutId,
        string? elementId)
    {
        var layout = ResolveMutationLayout(context, layoutId);
        if (layout.Result is not null)
        {
            return (null, null, layout.Result);
        }

        var legend = ResolveLegendElement(context, elementId, layout.Layout!.Object.Id);
        if (legend is null)
        {
            return (layout.Layout, null, MutationOperationResult.Failure("legend.not_found", "The target legend element was not found in the target layout."));
        }

        return (layout.Layout, legend, null);
    }

    private static MapFrameEntry? ResolveMapFrame(RegistryContext context, string? mapFrameId, string layoutId)
    {
        if (string.IsNullOrWhiteSpace(mapFrameId))
        {
            return null;
        }

        return context.MapFrames.FirstOrDefault(item =>
            string.Equals(item.Object.Id, mapFrameId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.LayoutObject.Id, layoutId, StringComparison.OrdinalIgnoreCase));
    }

    private static LayoutElementEntry? ResolveLayoutElement(RegistryContext context, string? elementId, string layoutId)
    {
        if (string.IsNullOrWhiteSpace(elementId))
        {
            return null;
        }

        return context.LayoutElements.FirstOrDefault(item =>
            string.Equals(item.Object.Id, elementId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.LayoutObject.Id, layoutId, StringComparison.OrdinalIgnoreCase));
    }

    private static LayoutElementEntry? ResolveLegendElement(RegistryContext context, string? elementId, string layoutId)
    {
        var element = ResolveLayoutElement(context, elementId, layoutId);
        return element?.Element is Legend ? element : null;
    }

    private static MapView? FindMapView(Map map)
    {
        var activeView = MapView.Active;
        if (activeView is not null && SameUri(activeView.Map?.URI, map.URI))
        {
            return activeView;
        }

        return map.GetMapPanes()
            .FirstOrDefault()
            ?.MapView;
    }

    private static bool ActivatePane(object pane)
    {
        var method = pane.GetType().GetMethod("Activate", Type.EmptyTypes);
        if (method is null)
        {
            return false;
        }

        method.Invoke(pane, null);
        return true;
    }

    private static bool TryReadExtent(
        JsonObjectMap? args,
        SpatialReference? fallbackSpatialReference,
        out Envelope? extent,
        out string? error)
    {
        extent = null;
        error = null;
        var xMin = args?.GetDouble("xMin");
        var yMin = args?.GetDouble("yMin");
        var xMax = args?.GetDouble("xMax");
        var yMax = args?.GetDouble("yMax");
        if (xMin is null || yMin is null || xMax is null || yMax is null)
        {
            error = "xMin, yMin, xMax, and yMax are required numeric extent arguments.";
            return false;
        }

        if (xMin >= xMax || yMin >= yMax)
        {
            error = "Extent minimum coordinates must be less than maximum coordinates.";
            return false;
        }

        var spatialReference = args?.GetInt32("wkid") is { } wkid
            ? SpatialReferenceBuilder.CreateSpatialReference(wkid)
            : fallbackSpatialReference;
        extent = EnvelopeBuilderEx.CreateEnvelope(xMin.Value, yMin.Value, xMax.Value, yMax.Value, spatialReference);
        return true;
    }

    private static bool TryParseAllowedBasemap(string? value, out Basemap basemap, out string[] allowed)
    {
        allowed = new[]
        {
            nameof(Basemap.None),
            nameof(Basemap.ProjectDefault),
            nameof(Basemap.Gray),
            nameof(Basemap.DarkGray),
            nameof(Basemap.Topographic),
            nameof(Basemap.Streets),
            nameof(Basemap.Satellite),
            nameof(Basemap.Hybrid),
            nameof(Basemap.Oceans),
            nameof(Basemap.Terrain),
            nameof(Basemap.OpenStreetMap),
            nameof(Basemap.NavigationVector),
            nameof(Basemap.GrayVector),
            nameof(Basemap.DarkGrayVector),
            nameof(Basemap.TopographicVector),
            nameof(Basemap.StreetsVector),
            nameof(Basemap.StreetsNightVector),
            nameof(Basemap.StreetsWithReliefVector)
        };
        basemap = Basemap.None;
        return !string.IsNullOrWhiteSpace(value)
            && allowed.Contains(value, StringComparer.OrdinalIgnoreCase)
            && Enum.TryParse(value, ignoreCase: true, out basemap);
    }

    private bool TryResolveAllowedLayerFile(
        Project? project,
        string? path,
        out string? fullPath,
        out string? error,
        out string[] allowedRoots)
    {
        fullPath = null;
        error = null;
        allowedRoots = GetAllowedRoots(project);

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "symbologyLayerPath is required.";
            return false;
        }

        fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!string.Equals(Path.GetExtension(fullPath), ".lyrx", StringComparison.OrdinalIgnoreCase))
        {
            error = "symbologyLayerPath must point to a .lyrx file.";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = $"Symbology layer file '{fullPath}' does not exist.";
            return false;
        }

        var resolvedPath = fullPath;
        if (!allowedRoots.Any(root => IsPathUnderRoot(resolvedPath, root)))
        {
            error = $"Symbology layer file '{fullPath}' is outside the allowed roots.";
            return false;
        }

        return true;
    }

    private bool TryResolveAllowedOutputPath(
        Project? project,
        string? path,
        string format,
        bool overwrite,
        out string? fullPath,
        out string? error,
        out string[] allowedRoots)
    {
        fullPath = null;
        error = null;
        allowedRoots = GetAllowedRoots(project);

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "An output path is required.";
            return false;
        }

        if (!IsSupportedOutputFormat(format))
        {
            error = $"Unsupported output format '{format}'. Supported values: PNG, PDF, APRX.";
            return false;
        }

        fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var extension = Path.GetExtension(fullPath);
        var expectedExtension = "." + format.ToLowerInvariant();
        if (!string.Equals(extension, expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Output path must use the {expectedExtension} extension for format {format}.";
            return false;
        }

        var resolvedPath = fullPath;
        if (!allowedRoots.Any(root => IsPathUnderRoot(resolvedPath, root)))
        {
            error = $"Output path '{fullPath}' is outside the allowed roots.";
            return false;
        }

        if (File.Exists(fullPath) && !overwrite)
        {
            error = $"Output path '{fullPath}' already exists and overwrite is false.";
            return false;
        }

        return true;
    }

    private static string NormalizeExportFormat(string? value)
    {
        var format = string.IsNullOrWhiteSpace(value) ? "PDF" : value.Trim().ToUpperInvariant();
        return format is "PNG" or "PDF" ? format : format;
    }

    private static bool IsSupportedOutputFormat(string format)
    {
        return format is "PNG" or "PDF" or "APRX";
    }

    private GpExecutionPreflight PrepareGeoprocessingExecution(BridgeRequest request)
    {
        var context = RefreshObjectRegistry(Project.Current);
        if (context.Project is null)
        {
            return GpExecutionPreflight.Failure("project.not_loaded", "No ArcGIS Pro project is loaded.", Array.Empty<string>(), Array.Empty<string>());
        }

        var toolName = request.Args?.GetString("toolName")?.Trim();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return GpExecutionPreflight.Failure("bridge.invalid_args", "toolName is required.", GetAllowedRoots(context.Project), Array.Empty<string>());
        }

        if (!IsSafeGpToolName(toolName))
        {
            return GpExecutionPreflight.Failure(
                "geoprocessing.tool_name_not_allowed",
                "toolName must be a system tool alias/name such as management.Buffer or a path to a toolbox under an allowed root.",
                GetAllowedRoots(context.Project),
                Array.Empty<string>());
        }

        var allowedRoots = GetAllowedRoots(context.Project);
        if (LooksLikePath(toolName) && !TryValidateGpPath(context.Project, toolName, "toolName", allowedRoots, out var checkedToolPath, out var toolPathError))
        {
            return GpExecutionPreflight.Failure(
                "bridge.path_not_allowed",
                toolPathError ?? "Tool path is outside the allowed roots.",
                allowedRoots,
                checkedToolPath is null ? Array.Empty<string>() : new[] { checkedToolPath });
        }

        var allowDestructive = request.Args?.GetBoolean("allowDestructive") ?? _config.DestructiveOperations.EnableDestructiveGeoprocessing;
        var destructiveTool = IsDestructiveGpTool(toolName);
        if (destructiveTool && !allowDestructive)
        {
            return GpExecutionPreflight.Failure(
                "geoprocessing.destructive_tool_denied",
                $"Geoprocessing tool '{toolName}' is blocked by the default destructive-tool denylist. Pass allowDestructive=true only when the edit is intentional.",
                allowedRoots,
                Array.Empty<string>());
        }

        if (destructiveTool
            && !request.DryRun
            && _config.Confirmations.RequireDestructiveGeoprocessingConfirmation
            && !HasConfirmation(request, "confirmDestructive"))
        {
            return GpExecutionPreflight.Failure(
                "bridge.confirmation_required",
                $"Destructive geoprocessing tool '{toolName}' requires confirmDestructive=true.",
                allowedRoots,
                Array.Empty<string>());
        }

        var parameterRead = ReadGpParameters(request.Args);
        if (!parameterRead.Ok)
        {
            return GpExecutionPreflight.Failure(parameterRead.ErrorCode!, parameterRead.ErrorMessage!, allowedRoots, Array.Empty<string>());
        }

        var environmentRead = ReadGpEnvironments(request.Args);
        if (!environmentRead.Ok)
        {
            return GpExecutionPreflight.Failure(environmentRead.ErrorCode!, environmentRead.ErrorMessage!, allowedRoots, Array.Empty<string>());
        }

        var checkedPaths = new List<string>();
        foreach (var parameter in parameterRead.Parameters)
        {
            if (!TryValidateGpValuePaths(context.Project, parameter.Value, parameter.Name ?? $"parameters[{parameter.Index}]", allowedRoots, checkedPaths, out var pathError))
            {
                return GpExecutionPreflight.Failure("bridge.path_not_allowed", pathError!, allowedRoots, checkedPaths.ToArray());
            }
        }

        foreach (var environment in environmentRead.Environments)
        {
            if (!TryValidateGpValuePaths(context.Project, environment.Value, $"environments.{environment.Name}", allowedRoots, checkedPaths, out var pathError))
            {
                return GpExecutionPreflight.Failure("bridge.path_not_allowed", pathError!, allowedRoots, checkedPaths.ToArray());
            }
        }

        return GpExecutionPreflight.Success(
            toolName,
            parameterRead.Parameters,
            environmentRead.Environments,
            context.Project.HomeFolderPath,
            context.Project.Path,
            request.Args?.GetBoolean("addOutputsToMap") ?? false,
            allowDestructive,
            destructiveTool,
            allowedRoots,
            checkedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static GpParameterReadResult ReadGpParameters(JsonObjectMap? args)
    {
        if (args is null || !args.TryGetValue("parameters", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return GpParameterReadResult.Success(Array.Empty<GpParameterValue>());
        }

        var parameters = new List<GpParameterValue>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                parameters.Add(new GpParameterValue(index, null, JsonToGpValue(item)));
                index++;
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            var index = 0;
            foreach (var property in element.EnumerateObject())
            {
                parameters.Add(new GpParameterValue(index, property.Name, JsonToGpValue(property.Value)));
                index++;
            }
        }
        else
        {
            parameters.Add(new GpParameterValue(0, null, JsonToGpValue(element)));
        }

        return GpParameterReadResult.Success(parameters);
    }

    private static GpEnvironmentReadResult ReadGpEnvironments(JsonObjectMap? args)
    {
        if (args is null || !args.TryGetValue("environments", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return GpEnvironmentReadResult.Success(Array.Empty<GpEnvironmentValue>());
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return GpEnvironmentReadResult.Failure("bridge.invalid_args", "environments must be a JSON object of geoprocessing environment name/value pairs.");
        }

        var environments = element.EnumerateObject()
            .Where(property => !string.IsNullOrWhiteSpace(property.Name))
            .Select(property => new GpEnvironmentValue(property.Name, JsonToGpValue(property.Value)))
            .ToArray();
        return GpEnvironmentReadResult.Success(environments);
    }

    private static string JsonToGpValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Array => string.Join(";", value.EnumerateArray().Select(JsonToGpValue)),
            JsonValueKind.Object when value.TryGetProperty("value", out var nestedValue) => JsonToGpValue(nestedValue),
            JsonValueKind.Object when value.TryGetProperty("path", out var nestedPath) => JsonToGpValue(nestedPath),
            _ => value.GetRawText()
        };
    }

    private static bool IsSafeGpToolName(string toolName)
    {
        if (LooksLikePath(toolName))
        {
            var extension = Path.GetExtension(toolName);
            return string.Equals(extension, ".atbx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pyt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tbx", StringComparison.OrdinalIgnoreCase)
                || toolName.Contains(".atbx", StringComparison.OrdinalIgnoreCase)
                || toolName.Contains(".pyt", StringComparison.OrdinalIgnoreCase)
                || toolName.Contains(".tbx", StringComparison.OrdinalIgnoreCase);
        }

        return toolName.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    private static bool IsDestructiveGpTool(string toolName)
    {
        var name = ExtractGpToolLeafName(toolName);
        var destructiveTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "delete",
            "delete_management",
            "deleterows",
            "delete_rows",
            "deleterows_management",
            "deletefeatures",
            "delete_features",
            "deletefeatures_management",
            "truncate_table",
            "truncatetable",
            "truncatetable_management",
            "calculatefield",
            "calculate_field",
            "calculatefield_management",
            "append",
            "append_management",
            "mergebranch",
            "deletefield",
            "delete_field",
            "deletefield_management",
            "addfield",
            "add_field",
            "addfield_management",
            "alterfield",
            "alter_field",
            "alterfield_management",
            "repairgeometry",
            "repair_geometry",
            "repairgeometry_management",
            "recalculatefeatureclassextent",
            "recalculate_feature_class_extent",
            "recalculatefeatureclassextent_management"
        };

        return destructiveTools.Contains(name);
    }

    private static string ExtractGpToolLeafName(string toolName)
    {
        var normalized = toolName.Trim();
        var lastSlash = normalized.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        if (lastSlash >= 0 && lastSlash + 1 < normalized.Length)
        {
            normalized = normalized[(lastSlash + 1)..];
        }

        var lastDot = normalized.LastIndexOf('.');
        if (lastDot >= 0 && lastDot + 1 < normalized.Length)
        {
            normalized = normalized[(lastDot + 1)..];
        }

        return new string(normalized.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
    }

    private static bool TryValidateGpValuePaths(
        Project? project,
        string value,
        string label,
        string[] allowedRoots,
        List<string> checkedPaths,
        out string? error)
    {
        error = null;
        foreach (var token in ExtractPotentialGpPathTokens(value))
        {
            if (!LooksLikePath(token) || IsInMemoryGpPath(token))
            {
                continue;
            }

            if (!TryValidateGpPath(project, token, label, allowedRoots, out var checkedPath, out error))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(checkedPath))
            {
                checkedPaths.Add(checkedPath);
            }
        }

        return true;
    }

    private static bool TryValidateGpPath(
        Project? project,
        string value,
        string label,
        string[] allowedRoots,
        out string? checkedPath,
        out string? error)
    {
        checkedPath = null;
        error = null;
        var resolved = ResolveGpPath(project, value);
        checkedPath = resolved;
        if (!allowedRoots.Any(root => IsPathUnderRoot(resolved, root)))
        {
            error = $"Geoprocessing {label} path '{resolved}' is outside the allowed roots.";
            return false;
        }

        return true;
    }

    private static string ResolveGpPath(Project? project, string value)
    {
        return ResolveGpPath(project?.HomeFolderPath, project?.Path, value);
    }

    private static string ResolveGpPath(string? homeFolderPath, string? projectPath, string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(TrimGpPathToken(value));
        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        var baseRoot = !string.IsNullOrWhiteSpace(homeFolderPath)
            ? homeFolderPath
            : !string.IsNullOrWhiteSpace(projectPath)
                ? Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory
                : Environment.CurrentDirectory;
        return Path.GetFullPath(Path.Combine(baseRoot, expanded));
    }

    private static IEnumerable<string> ExtractPotentialGpPathTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var token in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = TrimGpPathToken(token);
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                yield return trimmed;
            }
        }
    }

    private static string TrimGpPathToken(string value)
    {
        return value.Trim().Trim('"', '\'');
    }

    private static bool LooksLikePath(string value)
    {
        var token = TrimGpPathToken(value);
        if (string.IsNullOrWhiteSpace(token) || IsInMemoryGpPath(token))
        {
            return false;
        }

        if (Path.IsPathRooted(token)
            || token.StartsWith(@".\", StringComparison.Ordinal)
            || token.StartsWith(@"..\", StringComparison.Ordinal)
            || token.StartsWith("./", StringComparison.Ordinal)
            || token.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }

        if (token.Contains(".gdb", StringComparison.OrdinalIgnoreCase)
            || token.Contains(".sde", StringComparison.OrdinalIgnoreCase)
            || token.Contains(".atbx", StringComparison.OrdinalIgnoreCase)
            || token.Contains(".tbx", StringComparison.OrdinalIgnoreCase)
            || token.Contains(".pyt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (token.Contains('\\') || token.Contains('/')) && Path.HasExtension(token);
    }

    private static bool IsInMemoryGpPath(string value)
    {
        var token = TrimGpPathToken(value);
        return string.Equals(token, "memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "in_memory", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith(@"memory\", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith(@"in_memory\", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("memory/", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("in_memory/", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> ExtractGpOutputPaths(GpExecutionPreflight preflight, IGPResult result)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.ReturnValue))
        {
            candidates.Add(result.ReturnValue);
        }

        if (result.Values is not null)
        {
            candidates.AddRange(result.Values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        candidates.AddRange(preflight.Parameters
            .Where(parameter => IsLikelyOutputParameter(parameter.Name))
            .Select(parameter => parameter.Value));

        return candidates
            .SelectMany(ExtractPotentialGpPathTokens)
            .Where(path => LooksLikePath(path) && !IsInMemoryGpPath(path))
            .Select(path => ResolveGpPath(preflight.ProjectHomeFolder, preflight.ProjectPath, path))
            .Where(path => File.Exists(path) || Directory.Exists(path) || path.Contains(".gdb", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsLikelyOutputParameter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("out", StringComparison.OrdinalIgnoreCase)
            || name.Contains("output", StringComparison.OrdinalIgnoreCase)
            || name.Contains("target", StringComparison.OrdinalIgnoreCase);
    }

    private BridgeArtifact[] RegisterGpOutputArtifacts(IEnumerable<string> outputPaths)
    {
        var artifacts = new List<BridgeArtifact>();
        foreach (var outputPath in outputPaths)
        {
            artifacts.Add(RegisterFileArtifact(
                outputPath,
                GuessMimeType(outputPath),
                null,
                null,
                "geoprocessing.execute_tool"));
        }

        return artifacts.ToArray();
    }

    private static string GuessMimeType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".tif" or ".tiff" => "image/tiff",
            ".csv" => "text/csv",
            ".txt" or ".log" => "text/plain",
            ".json" or ".geojson" => "application/json",
            ".shp" => "application/vnd.esri.shapefile",
            ".gdb" => "application/vnd.esri.file-geodatabase",
            _ when path.Contains(".gdb", StringComparison.OrdinalIgnoreCase) => "application/vnd.esri.dataset",
            _ => "application/octet-stream"
        };
    }

    private static object GpMessageSummary(IGPMessage message, string? eventName)
    {
        return new
        {
            eventName,
            type = message.Type.ToString(),
            text = message.Text,
            errorCode = message.ErrorCode
        };
    }

    private ArcpyScriptPreflight PrepareArcpyScriptExecution(BridgeRequest request)
    {
        var context = RefreshObjectRegistry(Project.Current);
        var allowedRoots = GetAllowedRoots(context.Project);
        if (allowedRoots.Length == 0)
        {
            return ArcpyScriptPreflight.Failure(
                "bridge.no_allowed_roots",
                "No allowed roots are configured and no ArcGIS Pro project home folder is available.",
                allowedRoots,
                Array.Empty<string>());
        }

        var scriptPathValue = request.Args?.GetString("scriptPath");
        if (string.IsNullOrWhiteSpace(scriptPathValue))
        {
            return ArcpyScriptPreflight.Failure("bridge.invalid_args", "scriptPath is required.", allowedRoots, Array.Empty<string>());
        }

        var scriptPath = ResolveGpPath(context.Project, scriptPathValue);
        var checkedPaths = new List<string> { scriptPath };
        if (!string.Equals(Path.GetExtension(scriptPath), ".py", StringComparison.OrdinalIgnoreCase))
        {
            return ArcpyScriptPreflight.Failure("python.invalid_script_path", "scriptPath must point to a .py file.", allowedRoots, checkedPaths.ToArray());
        }

        if (!File.Exists(scriptPath))
        {
            return ArcpyScriptPreflight.Failure("python.script_not_found", $"Script '{scriptPath}' does not exist.", allowedRoots, checkedPaths.ToArray());
        }

        if (!allowedRoots.Any(root => IsPathUnderRoot(scriptPath, root)))
        {
            return ArcpyScriptPreflight.Failure("bridge.path_not_allowed", $"Script '{scriptPath}' is outside the allowed roots.", allowedRoots, checkedPaths.ToArray());
        }

        var workingDirectoryValue = request.Args?.GetString("workingDirectory");
        var workingDirectory = string.IsNullOrWhiteSpace(workingDirectoryValue)
            ? Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory
            : ResolveGpPath(context.Project, workingDirectoryValue);
        checkedPaths.Add(workingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            return ArcpyScriptPreflight.Failure("python.working_directory_not_found", $"Working directory '{workingDirectory}' does not exist.", allowedRoots, checkedPaths.ToArray());
        }

        if (!allowedRoots.Any(root => IsPathUnderRoot(workingDirectory, root)))
        {
            return ArcpyScriptPreflight.Failure("bridge.path_not_allowed", $"Working directory '{workingDirectory}' is outside the allowed roots.", allowedRoots, checkedPaths.ToArray());
        }

        var outputDirectoryValue = request.Args?.GetString("outputDirectory");
        var outputDirectory = string.IsNullOrWhiteSpace(outputDirectoryValue)
            ? workingDirectory
            : ResolveGpPath(context.Project, outputDirectoryValue);
        checkedPaths.Add(outputDirectory);
        if (!allowedRoots.Any(root => IsPathUnderRoot(outputDirectory, root)))
        {
            return ArcpyScriptPreflight.Failure("bridge.path_not_allowed", $"Output directory '{outputDirectory}' is outside the allowed roots.", allowedRoots, checkedPaths.ToArray());
        }

        var artifactRoot = GetArtifactRoot(context.Project?.HomeFolderPath, context.Project?.Path);
        var safeRequestId = SafeArtifactName(request.Id);
        var manifestPath = Path.Combine(
            artifactRoot,
            "arcpy_runs",
            $"arcpy_run_{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}_{safeRequestId}.json");
        var toolboxPath = Path.Combine(
            artifactRoot,
            "arcpy_runner",
            "ArcPyRunner.pyt");
        checkedPaths.Add(manifestPath);
        checkedPaths.Add(toolboxPath);

        var syntaxOnly = request.Args?.GetBoolean("syntaxOnly") ?? false;
        var argumentsJson = syntaxOnly
            ? """{"__syntaxOnly":true}"""
            : ReadRawJsonArgument(request.Args, "arguments", "[]");
        var warnings = new[]
        {
            "ArcPy scripts can mutate the current ArcGIS Pro project or data sources; only run reviewed scripts from allowed roots.",
            "Syntax checks use ast.parse and do not write Python bytecode."
        };

        return ArcpyScriptPreflight.Success(
            scriptPath,
            workingDirectory,
            outputDirectory,
            manifestPath,
            toolboxPath,
            Path.Combine(toolboxPath, "RunArcPyScript"),
            argumentsJson,
            syntaxOnly,
            allowedRoots,
            checkedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings);
    }

    private BridgeArtifact[] RegisterArcpyScriptArtifacts(ArcpyScriptPreflight preflight, JsonElement? manifest)
    {
        var artifacts = new List<BridgeArtifact>();
        if (!string.IsNullOrWhiteSpace(preflight.ManifestPath) && File.Exists(preflight.ManifestPath))
        {
            artifacts.Add(RegisterFileArtifact(
                preflight.ManifestPath,
                "application/json",
                null,
                null,
                "python.run_arcpy_script"));
        }

        foreach (var path in ExtractGeneratedFilePaths(manifest))
        {
            if (string.IsNullOrWhiteSpace(path)
                || !File.Exists(path)
                || !preflight.AllowedRoots.Any(root => IsPathUnderRoot(path, root))
                || string.Equals(Path.GetFullPath(path), Path.GetFullPath(preflight.ManifestPath ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            artifacts.Add(RegisterFileArtifact(
                path,
                GuessMimeType(path),
                null,
                null,
                "python.run_arcpy_script"));
        }

        return artifacts
            .GroupBy(artifact => artifact.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static JsonElement? TryReadJsonFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryGetJsonBoolean(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : null;
    }

    private static string[] ExtractJsonStringArray(JsonElement? element, string propertyName)
    {
        if (!element.HasValue
            || element.Value.ValueKind != JsonValueKind.Object
            || !element.Value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static string[] ExtractGeneratedFilePaths(JsonElement? manifest)
    {
        if (!manifest.HasValue
            || manifest.Value.ValueKind != JsonValueKind.Object
            || !manifest.Value.TryGetProperty("generatedFiles", out var generatedFiles)
            || generatedFiles.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return generatedFiles.EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    return item.GetString();
                }

                return item.ValueKind == JsonValueKind.Object && item.TryGetProperty("path", out var pathElement)
                    ? pathElement.GetString()
                    : null;
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
    }

    private static string ReadRawJsonArgument(JsonObjectMap? args, string key, string fallback)
    {
        if (args is null || !args.TryGetValue(key, out var element) || element.ValueKind == JsonValueKind.Undefined)
        {
            return fallback;
        }

        return element.GetRawText();
    }

    private static string SafeArtifactName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch)
            .ToArray())
            .Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "item" : safe;
    }

    // ArcGIS Pro keys its trusted-toolbox list (%LOCALAPPDATA%\ESRI\ArcGISPro\Geoprocessing\.access_pyt)
    // on the toolbox path plus its last-write time, so rewriting a byte-identical file still re-raises the
    // "third-party code" prompt. Rewrite once per session to prompt the user a single time, then leave the
    // file untouched so every later request reuses the approval.
    private void EnsureArcpyRunnerToolbox(string toolboxPath)
    {
        var content = BuildArcpyRunnerToolbox();
        var encoding = new UTF8Encoding(false);

        lock (_runnerToolboxLock)
        {
            var firstUseThisSession = _runnerToolboxesRefreshed.Add(toolboxPath);
            if (!firstUseThisSession
                && File.Exists(toolboxPath)
                && string.Equals(File.ReadAllText(toolboxPath, encoding), content, StringComparison.Ordinal))
            {
                return;
            }

            File.WriteAllText(toolboxPath, content, encoding);
        }
    }

    private static string BuildArcpyRunnerToolbox()
    {
        return """
# -*- coding: utf-8 -*-
import arcpy
import ast
import contextlib
import io
import json
import os
import runpy
import sys
import time
import traceback


class Toolbox(object):
    def __init__(self):
        self.label = "ArcGIS Pro MCP ArcPy Runner"
        self.alias = "arcgis_pro_mcp_arcpy_runner"
        self.tools = [RunArcPyScript]


class RunArcPyScript(object):
    def __init__(self):
        self.label = "Run ArcPy Script"
        self.description = "Runs a guarded ArcPy script for the ArcGIS Pro MCP bridge."
        self.canRunInBackground = False

    def getParameterInfo(self):
        script_path = arcpy.Parameter(
            displayName="Script Path",
            name="script_path",
            datatype="GPString",
            parameterType="Required",
            direction="Input")
        arguments_json = arcpy.Parameter(
            displayName="Arguments JSON",
            name="arguments_json",
            datatype="GPString",
            parameterType="Optional",
            direction="Input")
        working_directory = arcpy.Parameter(
            displayName="Working Directory",
            name="working_directory",
            datatype="GPString",
            parameterType="Required",
            direction="Input")
        output_directory = arcpy.Parameter(
            displayName="Output Directory",
            name="output_directory",
            datatype="GPString",
            parameterType="Required",
            direction="Input")
        manifest_path = arcpy.Parameter(
            displayName="Manifest Path",
            name="manifest_path",
            datatype="GPString",
            parameterType="Required",
            direction="Output")
        return [script_path, arguments_json, working_directory, output_directory, manifest_path]

    def execute(self, parameters, messages):
        script_path = parameters[0].valueAsText
        arguments_json = parameters[1].valueAsText or "[]"
        working_directory = parameters[2].valueAsText
        output_directory = parameters[3].valueAsText
        manifest_path = parameters[4].valueAsText
        manifest = _run_script(script_path, arguments_json, working_directory, output_directory, manifest_path, messages)
        parameters[4].value = manifest_path
        if not manifest.get("syntaxOk", False):
            raise arcpy.ExecuteError("ArcPy script syntax check failed.")
        if not manifest.get("succeeded", False) and not manifest.get("syntaxOnly", False):
            raise arcpy.ExecuteError("ArcPy script execution failed.")


def _run_script(script_path, arguments_json, working_directory, output_directory, manifest_path, messages):
    started = time.time()
    started_utc = _utc_now()
    stdout_buffer = io.StringIO()
    stderr_buffer = io.StringIO()
    warnings = []
    manifest = {
        "scriptPath": os.path.abspath(script_path),
        "workingDirectory": os.path.abspath(working_directory),
        "outputDirectory": os.path.abspath(output_directory),
        "manifestPath": os.path.abspath(manifest_path),
        "startedUtc": started_utc,
        "syntaxOnly": False,
        "syntaxOk": False,
        "succeeded": False,
        "exitCode": 0,
        "stdout": "",
        "stderr": "",
        "warnings": warnings,
        "messages": [],
        "generatedFiles": [],
    }

    before = _snapshot(output_directory, warnings)
    old_argv = list(sys.argv)
    old_cwd = os.getcwd()
    old_args_json = os.environ.get("ARCGIS_PRO_MCP_SCRIPT_ARGS_JSON")
    try:
        with open(script_path, "r", encoding="utf-8-sig") as handle:
            source = handle.read()
        ast.parse(source, filename=script_path)
        manifest["syntaxOk"] = True

        arguments = _read_arguments(arguments_json)
        manifest["arguments"] = arguments
        if _syntax_only(arguments):
            manifest["syntaxOnly"] = True
            manifest["succeeded"] = True
            messages.addMessage("ArcPy script syntax check passed.")
            return _finish_manifest(manifest, before, output_directory, started, stdout_buffer, stderr_buffer, manifest_path, warnings)

        os.chdir(working_directory)
        os.environ["ARCGIS_PRO_MCP_SCRIPT_ARGS_JSON"] = arguments_json
        sys.argv = [script_path] + _argv(arguments)
        with contextlib.redirect_stdout(stdout_buffer), contextlib.redirect_stderr(stderr_buffer):
            try:
                runpy.run_path(script_path, run_name="__main__")
            except SystemExit as exc:
                manifest["exitCode"] = _exit_code(exc)
                if manifest["exitCode"] != 0:
                    manifest["error"] = {
                        "type": "SystemExit",
                        "message": str(exc),
                        "traceback": traceback.format_exc(),
                    }
            except BaseException as exc:
                manifest["exitCode"] = 1
                manifest["error"] = {
                    "type": type(exc).__name__,
                    "message": str(exc),
                    "traceback": traceback.format_exc(),
                }

        manifest["succeeded"] = manifest["exitCode"] == 0 and "error" not in manifest
        return _finish_manifest(manifest, before, output_directory, started, stdout_buffer, stderr_buffer, manifest_path, warnings)
    except BaseException as exc:
        manifest["exitCode"] = 1
        manifest["error"] = {
            "type": type(exc).__name__,
            "message": str(exc),
            "traceback": traceback.format_exc(),
        }
        _finish_manifest(manifest, before, output_directory, started, stdout_buffer, stderr_buffer, manifest_path, warnings)
        raise
    finally:
        sys.argv = old_argv
        os.chdir(old_cwd)
        if old_args_json is None:
            os.environ.pop("ARCGIS_PRO_MCP_SCRIPT_ARGS_JSON", None)
        else:
            os.environ["ARCGIS_PRO_MCP_SCRIPT_ARGS_JSON"] = old_args_json


def _finish_manifest(manifest, before, output_directory, started, stdout_buffer, stderr_buffer, manifest_path, warnings):
    manifest["stdout"] = _limit(stdout_buffer.getvalue())
    manifest["stderr"] = _limit(stderr_buffer.getvalue())
    manifest["generatedFiles"] = _generated_files(before, output_directory, started, warnings)
    manifest["finishedUtc"] = _utc_now()
    manifest["elapsedMs"] = int((time.time() - started) * 1000)
    try:
        manifest["arcpyMessages"] = arcpy.GetMessages()
    except Exception:
        manifest["arcpyMessages"] = None

    os.makedirs(os.path.dirname(manifest_path), exist_ok=True)
    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=2, sort_keys=True)

    if manifest["stdout"]:
        arcpy.AddMessage(_limit(manifest["stdout"], 4000))
    if manifest["stderr"]:
        arcpy.AddWarning(_limit(manifest["stderr"], 4000))
    if manifest.get("error"):
        arcpy.AddError(manifest["error"].get("message") or "ArcPy script failed.")
    return manifest


def _read_arguments(arguments_json):
    try:
        value = json.loads(arguments_json or "[]")
    except Exception as exc:
        raise ValueError("arguments must be valid JSON") from exc
    if value is None:
        return []
    if isinstance(value, (list, dict)):
        return value
    return [value]


def _syntax_only(arguments):
    if isinstance(arguments, dict):
        return bool(arguments.pop("__syntaxOnly", False))
    if isinstance(arguments, list) and "--mcp-syntax-only" in [str(item) for item in arguments]:
        arguments[:] = [item for item in arguments if str(item) != "--mcp-syntax-only"]
        return True
    return False


def _argv(arguments):
    if isinstance(arguments, list):
        return [str(item) for item in arguments]
    result = []
    for key, value in arguments.items():
        if value is None:
            result.append("--" + str(key))
        elif isinstance(value, bool):
            if value:
                result.append("--" + str(key))
        elif isinstance(value, (list, tuple)):
            for item in value:
                result.extend(["--" + str(key), str(item)])
        else:
            result.extend(["--" + str(key), str(value)])
    return result


def _snapshot(root, warnings):
    files = {}
    if not root:
        return files
    root = os.path.abspath(root)
    if not os.path.isdir(root):
        return files
    limit = 20000
    for current, dirs, names in os.walk(root):
        dirs[:] = [item for item in dirs if item not in {".git", "__pycache__"}]
        for name in names:
            path = os.path.join(current, name)
            try:
                stat = os.stat(path)
                files[path] = (stat.st_mtime, stat.st_size)
            except OSError:
                continue
            if len(files) >= limit:
                warnings.append("Output directory snapshot reached the 20000-file limit.")
                return files
    return files


def _generated_files(before, root, started, warnings):
    after = _snapshot(root, warnings)
    generated = []
    for path, state in after.items():
        previous = before.get(path)
        if previous is None or previous != state or state[0] >= started:
            try:
                generated.append({
                    "path": os.path.abspath(path),
                    "size": state[1],
                    "modifiedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(state[0])),
                })
            except Exception:
                generated.append({"path": os.path.abspath(path)})
    return generated[:500]


def _exit_code(exc):
    code = getattr(exc, "code", 0)
    if code is None:
        return 0
    if isinstance(code, int):
        return code
    return 1


def _limit(value, length=200000):
    if value is None:
        return ""
    text = str(value)
    if len(text) <= length:
        return text
    return text[:length] + "\n... truncated ..."


def _utc_now():
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
""";
    }

    private string[] GetAllowedRoots(Project? project)
    {
        var roots = new List<string>(_config.GetConfiguredAllowedRoots());

        if (!string.IsNullOrWhiteSpace(project?.HomeFolderPath))
        {
            roots.Add(project.HomeFolderPath);
        }

        if (!string.IsNullOrWhiteSpace(project?.Path))
        {
            var projectDirectory = Path.GetDirectoryName(project.Path);
            if (!string.IsNullOrWhiteSpace(projectDirectory))
            {
                roots.Add(projectDirectory);
            }
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static (CIMRenderer? Renderer, string? SourceLayerName) ReadFirstFeatureRendererFromLayerFile(string path)
    {
        var document = new LayerDocument(path);
        var cimDocument = document.GetCIMLayerDocument();
        foreach (var definition in cimDocument.LayerDefinitions ?? Array.Empty<CIMDefinition>())
        {
            if (definition is CIMGeoFeatureLayerBase geoFeatureLayer && geoFeatureLayer.Renderer is not null)
            {
                return (geoFeatureLayer.Renderer, SafeProperty(definition, "Name")?.ToString());
            }
        }

        return (null, null);
    }

    private RegistryContext RefreshObjectRegistry(Project? project)
    {
        if (project is null)
        {
            var emptySnapshot = _objectRegistry.ReplaceLiveObjects(Array.Empty<RegistryObject>());
            return new RegistryContext(
                null,
                null,
                Array.Empty<MapEntry>(),
                Array.Empty<LayerEntry>(),
                Array.Empty<LayoutEntry>(),
                Array.Empty<MapFrameEntry>(),
                Array.Empty<LayoutElementEntry>(),
                emptySnapshot);
        }

        var liveObjects = new List<RegistryObject>();
        var projectKey = ProjectStableKey(project);
        var projectId = _objectRegistry.GetOrCreateId("project", projectKey);
        var projectObject = new RegistryObject(
            Id: projectId,
            Kind: "project",
            DisplayName: project.Name,
            Type: "Project",
            Uri: project.URI,
            Path: project.Path,
            DataSource: project.Path,
            ParentId: null,
            ParentKind: null,
            ParentName: null,
            StableKey: projectKey,
            Properties: new Dictionary<string, object?>
            {
                ["homeFolder"] = project.HomeFolderPath,
                ["defaultGeodatabase"] = project.DefaultGeodatabasePath,
                ["dirty"] = project.IsDirty
            });
        liveObjects.Add(projectObject);

        var maps = BuildMapEntries(project, projectObject, liveObjects);
        var layers = BuildLayerEntries(maps, liveObjects);
        var layouts = BuildLayoutEntries(project, projectObject, liveObjects);
        var mapFrames = BuildMapFrameEntries(layouts, maps, liveObjects);
        var layoutElements = BuildLayoutElementEntries(layouts, mapFrames, liveObjects);
        var snapshot = _objectRegistry.ReplaceLiveObjects(liveObjects);

        return new RegistryContext(project, projectObject, maps, layers, layouts, mapFrames, layoutElements, snapshot);
    }

    private IReadOnlyList<MapEntry> BuildMapEntries(Project project, RegistryObject projectObject, List<RegistryObject> liveObjects)
    {
        var maps = GetMaps(project);
        return maps.Select((map, index) =>
        {
            var stableKey = MapStableKey(projectObject.StableKey, map, index);
            var id = _objectRegistry.GetOrCreateId("map", stableKey);
            var layerCount = map.GetLayersAsFlattenedList().Count;
            var isActive = SameUri(MapView.Active?.Map?.URI, map.URI);
            var item = new RegistryObject(
                Id: id,
                Kind: "map",
                DisplayName: map.Name,
                Type: map.MapType.ToString(),
                Uri: map.URI,
                Path: null,
                DataSource: null,
                ParentId: projectObject.Id,
                ParentKind: projectObject.Kind,
                ParentName: projectObject.DisplayName,
                StableKey: stableKey,
                Properties: new Dictionary<string, object?>
                {
                    ["layerCount"] = layerCount,
                    ["active"] = isActive
                });
            liveObjects.Add(item);
            return new MapEntry(map, item, isActive);
        }).ToArray();
    }

    private IReadOnlyList<LayerEntry> BuildLayerEntries(IReadOnlyList<MapEntry> maps, List<RegistryObject> liveObjects)
    {
        var result = new List<LayerEntry>();

        foreach (var mapEntry in maps)
        {
            var layers = mapEntry.Map.GetLayersAsFlattenedList();
            var idsByLayer = new Dictionary<Layer, string>();
            var keysByLayer = new Dictionary<Layer, string>();

            for (var index = 0; index < layers.Count; index++)
            {
                var layer = layers[index];
                var stableKey = LayerStableKey(mapEntry.Object.StableKey, layer, index);
                var id = _objectRegistry.GetOrCreateId("layer", stableKey);
                idsByLayer[layer] = id;
                keysByLayer[layer] = stableKey;
            }

            for (var index = 0; index < layers.Count; index++)
            {
                var layer = layers[index];
                var parentLayer = layer.Parent as Layer;
                var parentId = parentLayer is not null && idsByLayer.TryGetValue(parentLayer, out var foundParentId)
                    ? foundParentId
                    : mapEntry.Object.Id;
                var parentKind = parentLayer is not null ? "layer" : "map";
                var parentName = parentLayer?.Name ?? mapEntry.Object.DisplayName;
                var dataSource = TryGetPath(layer);
                var isGroupLayer = layer is CompositeLayer;
                var childCount = layer is CompositeLayer compositeLayer ? compositeLayer.Layers.Count : 0;
                var item = new RegistryObject(
                    Id: idsByLayer[layer],
                    Kind: "layer",
                    DisplayName: layer.Name,
                    Type: layer.MapLayerType.ToString(),
                    Uri: layer.URI,
                    Path: dataSource,
                    DataSource: dataSource,
                    ParentId: parentId,
                    ParentKind: parentKind,
                    ParentName: parentName,
                    StableKey: keysByLayer[layer],
                    Properties: new Dictionary<string, object?>
                    {
                        ["mapId"] = mapEntry.Object.Id,
                        ["mapName"] = mapEntry.Object.DisplayName,
                        ["visible"] = layer.IsVisible,
                        ["transparency"] = layer.Transparency,
                        ["connectionStatus"] = SafeString(() => layer.ConnectionStatus.ToString()),
                        ["definitionQuery"] = layer is BasicFeatureLayer featureLayer ? featureLayer.DefinitionQuery : null,
                        ["geometryType"] = layer is BasicFeatureLayer geometryLayer ? geometryLayer.ShapeType.ToString() : null,
                        ["isGroupLayer"] = isGroupLayer,
                        ["childCount"] = childCount,
                        ["index"] = index
                    });
                liveObjects.Add(item);
                result.Add(new LayerEntry(mapEntry.Map, layer, mapEntry.Object, item));
            }
        }

        return result;
    }

    private IReadOnlyList<LayoutEntry> BuildLayoutEntries(Project project, RegistryObject projectObject, List<RegistryObject> liveObjects)
    {
        var layouts = GetLayouts(project);
        return layouts.Select((layout, index) =>
        {
            var stableKey = LayoutStableKey(projectObject.StableKey, layout, index);
            var id = _objectRegistry.GetOrCreateId("layout", stableKey);
            var isActive = SameUri(LayoutView.Active?.Layout?.URI, layout.URI);
            var item = new RegistryObject(
                Id: id,
                Kind: "layout",
                DisplayName: layout.Name,
                Type: "Layout",
                Uri: layout.URI,
                Path: null,
                DataSource: null,
                ParentId: projectObject.Id,
                ParentKind: projectObject.Kind,
                ParentName: projectObject.DisplayName,
                StableKey: stableKey,
                Properties: new Dictionary<string, object?>
                {
                    ["elementCount"] = layout.Elements.Count,
                    ["hasMapSeries"] = layout.MapSeries is not null,
                    ["active"] = isActive
                });
            liveObjects.Add(item);
            return new LayoutEntry(layout, item, isActive);
        }).ToArray();
    }

    private IReadOnlyList<MapFrameEntry> BuildMapFrameEntries(
        IReadOnlyList<LayoutEntry> layouts,
        IReadOnlyList<MapEntry> maps,
        List<RegistryObject> liveObjects)
    {
        var result = new List<MapFrameEntry>();

        foreach (var layoutEntry in layouts)
        {
            var frames = layoutEntry.Layout.GetElementsAsFlattenedList()
                .OfType<MapFrame>()
                .ToArray();

            for (var index = 0; index < frames.Length; index++)
            {
                var frame = frames[index];
                var linkedMap = ResolveMapEntry(maps, frame.Map);
                var stableKey = MapFrameStableKey(layoutEntry.Object.StableKey, frame, index);
                var id = _objectRegistry.GetOrCreateId("mapFrame", stableKey);
                var item = new RegistryObject(
                    Id: id,
                    Kind: "mapFrame",
                    DisplayName: frame.Name,
                    Type: "MapFrame",
                    Uri: null,
                    Path: null,
                    DataSource: null,
                    ParentId: layoutEntry.Object.Id,
                    ParentKind: layoutEntry.Object.Kind,
                    ParentName: layoutEntry.Object.DisplayName,
                    StableKey: stableKey,
                    Properties: new Dictionary<string, object?>
                    {
                        ["layoutId"] = layoutEntry.Object.Id,
                        ["layoutName"] = layoutEntry.Object.DisplayName,
                        ["mapId"] = linkedMap?.Object.Id,
                        ["mapName"] = linkedMap?.Object.DisplayName,
                        ["visible"] = frame.IsVisible,
                        ["locked"] = frame.IsLocked,
                        ["zOrder"] = frame.ZOrder,
                        ["activated"] = frame.IsActivated,
                        ["index"] = index
                    });
                liveObjects.Add(item);
                result.Add(new MapFrameEntry(frame, layoutEntry.Layout, layoutEntry.Object, linkedMap?.Object.Id, item));
            }
        }

        return result;
    }

    private IReadOnlyList<LayoutElementEntry> BuildLayoutElementEntries(
        IReadOnlyList<LayoutEntry> layouts,
        IReadOnlyList<MapFrameEntry> mapFrames,
        List<RegistryObject> liveObjects)
    {
        var result = new List<LayoutElementEntry>();

        foreach (var layoutEntry in layouts)
        {
            var elements = layoutEntry.Layout.GetElementsAsFlattenedList()
                .Where(element => element is not MapFrame)
                .ToArray();

            for (var index = 0; index < elements.Length; index++)
            {
                var element = elements[index];
                var mapFrameId = ResolveMapFrameId(mapFrames, element);
                var elementKind = LayoutElementKind(element);
                var stableKey = LayoutElementStableKey(layoutEntry.Object.StableKey, element, index);
                var id = _objectRegistry.GetOrCreateId("layoutElement", stableKey);
                var properties = new Dictionary<string, object?>
                {
                    ["layoutId"] = layoutEntry.Object.Id,
                    ["layoutName"] = layoutEntry.Object.DisplayName,
                    ["elementKind"] = elementKind,
                    ["visible"] = element.IsVisible,
                    ["locked"] = element.IsLocked,
                    ["zOrder"] = element.ZOrder,
                    ["x"] = SafeValue(() => element.GetX()),
                    ["y"] = SafeValue(() => element.GetY()),
                    ["width"] = SafeValue(() => element.GetWidth()),
                    ["height"] = SafeValue(() => element.GetHeight()),
                    ["rotation"] = SafeValue(() => element.GetRotation()),
                    ["mapFrameId"] = mapFrameId,
                    ["index"] = index
                };

                if (element is TextElement textElement)
                {
                    properties["text"] = textElement.TextProperties.Text;
                    properties["font"] = textElement.TextProperties.Font?.ToString();
                    properties["fontSize"] = textElement.TextProperties.FontSize;
                    properties["fontStyle"] = textElement.TextProperties.FontStyle.ToString();
                }

                var item = new RegistryObject(
                    Id: id,
                    Kind: "layoutElement",
                    DisplayName: element.Name,
                    Type: elementKind,
                    Uri: null,
                    Path: null,
                    DataSource: null,
                    ParentId: layoutEntry.Object.Id,
                    ParentKind: layoutEntry.Object.Kind,
                    ParentName: layoutEntry.Object.DisplayName,
                    StableKey: stableKey,
                    Properties: properties);
                liveObjects.Add(item);
                result.Add(new LayoutElementEntry(element, layoutEntry.Layout, layoutEntry.Object, mapFrameId, item));
            }
        }

        return result;
    }

    private static object MapSummary(MapEntry entry)
    {
        return new
        {
            entry.Object.Id,
            name = entry.Object.DisplayName,
            displayName = entry.Object.DisplayName,
            entry.Object.Type,
            entry.Object.Uri,
            entry.Object.Path,
            entry.Object.DataSource,
            entry.Object.ParentId,
            entry.Object.ParentKind,
            entry.Object.ParentName,
            layerCount = entry.Object.Properties["layerCount"],
            spatialReference = SpatialReferenceSummary(entry.Map.SpatialReference),
            referenceScale = entry.Map.ReferenceScale,
            selectionCount = SafeValue(() => entry.Map.SelectionCount),
            defaultViewingMode = SafeString(() => entry.Map.DefaultViewingMode.ToString()),
            colorModel = SafeString(() => entry.Map.ColorModel.ToString()),
            active = entry.IsActive
        };
    }

    private static object MapDetail(MapEntry entry)
    {
        var definition = SafeValue(() => entry.Map.GetDefinition());
        return new
        {
            summary = MapSummary(entry),
            camera = CameraSummary(ActiveCameraForMap(entry) ?? SafeProperty(definition, "DefaultCamera")),
            defaultExtent = EnvelopeSummary(SafeProperty(definition, "DefaultExtent")),
            customFullExtent = EnvelopeSummary(SafeProperty(definition, "CustomFullExtent")),
            defaultScale = SafeProperty(definition, "DefaultScale"),
            defaultRotation = SafeProperty(definition, "DefaultRotation"),
            description = SafeProperty(definition, "Description"),
            bookmarkCount = SafeCount(SafeProperty(definition, "Bookmarks")),
            standaloneTableCount = SafeValue(() => entry.Map.GetStandaloneTablesAsFlattenedList().Count)
        };
    }

    private static object LayoutSummary(LayoutEntry entry)
    {
        return new
        {
            entry.Object.Id,
            name = entry.Object.DisplayName,
            displayName = entry.Object.DisplayName,
            entry.Object.Type,
            entry.Object.Uri,
            entry.Object.Path,
            entry.Object.DataSource,
            entry.Object.ParentId,
            entry.Object.ParentKind,
            entry.Object.ParentName,
            elementCount = entry.Object.Properties["elementCount"],
            hasMapSeries = entry.Object.Properties["hasMapSeries"],
            page = PageSummary(SafeValue(() => entry.Layout.GetPage())),
            active = entry.IsActive
        };
    }

    private static object LayoutDetail(LayoutEntry entry)
    {
        return new
        {
            summary = LayoutSummary(entry),
            page = PageSummary(SafeValue(() => entry.Layout.GetPage())),
            mapSeries = MapSeriesSummary(entry.Layout.MapSeries)
        };
    }

    private static object LayerSummary(LayerEntry entry)
    {
        var connectionStatus = entry.Object.Properties["connectionStatus"]?.ToString();
        return new
        {
            entry.Object.Id,
            name = entry.Object.DisplayName,
            displayName = entry.Object.DisplayName,
            entry.Object.Type,
            entry.Object.Uri,
            entry.Object.Path,
            entry.Object.DataSource,
            entry.Object.ParentId,
            entry.Object.ParentKind,
            entry.Object.ParentName,
            mapId = entry.MapObject.Id,
            mapName = entry.MapObject.DisplayName,
            mapUri = entry.MapObject.Uri,
            visible = entry.Object.Properties["visible"],
            transparency = entry.Object.Properties["transparency"],
            connectionStatus,
            broken = IsBrokenConnectionStatus(connectionStatus),
            definitionQuery = entry.Object.Properties["definitionQuery"],
            geometryType = entry.Object.Properties["geometryType"],
            isGroupLayer = entry.Object.Properties["isGroupLayer"],
            childCount = entry.Object.Properties["childCount"]
        };
    }

    private static object LayerDetail(LayerEntry entry)
    {
        var layer = entry.Layer;
        return new
        {
            summary = LayerSummary(entry),
            source = new
            {
                path = entry.Object.Path,
                dataSource = entry.Object.DataSource,
                connectionStatus = entry.Object.Properties["connectionStatus"],
                hasJoins = SafeValue(() => layer.HasJoins),
                hasRelates = SafeValue(() => layer.HasRelates)
            },
            scaleRange = new
            {
                minScale = SafeValue(() => layer.MinScale),
                maxScale = SafeValue(() => layer.MaxScale),
                showAtAllScales = SafeValue(() => layer.ShowLayerAtAllScales)
            },
            geometry = FeatureGeometrySummary(layer),
            fields = LayerFieldSummaries(layer),
            labels = LabelSummary(layer),
            renderer = RendererSummary(layer),
            legend = new
            {
                status = SafeString(() => layer.LegendStatus.ToString()),
                groupCount = SafeCount(SafeValue(() => layer.LegendGroups))
            }
        };
    }

    private static object MapFrameSummary(MapFrameEntry entry)
    {
        return new
        {
            entry.Object.Id,
            name = entry.Object.DisplayName,
            displayName = entry.Object.DisplayName,
            entry.Object.Type,
            entry.Object.Uri,
            entry.Object.Path,
            entry.Object.DataSource,
            entry.Object.ParentId,
            entry.Object.ParentKind,
            entry.Object.ParentName,
            layoutId = entry.LayoutObject.Id,
            layoutName = entry.LayoutObject.DisplayName,
            mapId = entry.MapId,
            mapName = entry.Object.Properties["mapName"],
            visible = entry.Object.Properties["visible"],
            locked = entry.Object.Properties["locked"],
            zOrder = entry.Object.Properties["zOrder"],
            activated = entry.Object.Properties["activated"],
            camera = CameraSummary(SafeValue(() => entry.MapFrame.Camera)),
            viewExtent = EnvelopeSummary(SafeValue(() => entry.MapFrame.GetViewExtent()))
        };
    }

    private static object LayoutElementSummary(LayoutElementEntry entry)
    {
        return new
        {
            entry.Object.Id,
            name = entry.Object.DisplayName,
            displayName = entry.Object.DisplayName,
            entry.Object.Type,
            entry.Object.ParentId,
            entry.Object.ParentKind,
            entry.Object.ParentName,
            layoutId = entry.LayoutObject.Id,
            layoutName = entry.LayoutObject.DisplayName,
            elementKind = entry.Object.Properties["elementKind"],
            visible = entry.Object.Properties["visible"],
            locked = entry.Object.Properties["locked"],
            zOrder = entry.Object.Properties["zOrder"],
            mapFrameId = entry.MapFrameId
        };
    }

    private static object LayoutElementDetail(LayoutElementEntry entry)
    {
        return new
        {
            summary = LayoutElementSummary(entry),
            bounds = new
            {
                x = entry.Object.Properties["x"],
                y = entry.Object.Properties["y"],
                width = entry.Object.Properties["width"],
                height = entry.Object.Properties["height"],
                rotation = entry.Object.Properties["rotation"]
            },
            text = entry.Element is TextElement
                ? new
                {
                    value = entry.Object.Properties["text"],
                    font = entry.Object.Properties["font"],
                    fontSize = entry.Object.Properties["fontSize"],
                    fontStyle = entry.Object.Properties["fontStyle"]
                }
                : null,
            surround = entry.Element is MapSurround
                ? new
                {
                    mapFrameId = entry.MapFrameId
                }
                : null
        };
    }

    private static object RegistrySummary(RegistrySnapshot snapshot)
    {
        return new
        {
            snapshot.Count,
            snapshot.RefreshedUtc
        };
    }

    private VisualSource ResolveVisualSource(string sourceKind, string? sourceUri, string? sourceName)
    {
        var context = RefreshObjectRegistry(Project.Current);
        RegistryObject? sourceObject = null;
        if (string.Equals(sourceKind, "layout", StringComparison.OrdinalIgnoreCase))
        {
            sourceObject = context.Layouts
                .FirstOrDefault(item => SameUri(item.Layout.URI, sourceUri))
                ?.Object
                ?? context.Layouts
                    .FirstOrDefault(item => string.Equals(item.Object.DisplayName, sourceName, StringComparison.OrdinalIgnoreCase))
                    ?.Object;
        }
        else if (string.Equals(sourceKind, "map", StringComparison.OrdinalIgnoreCase))
        {
            sourceObject = context.Maps
                .FirstOrDefault(item => SameUri(item.Map.URI, sourceUri))
                ?.Object
                ?? context.Maps
                    .FirstOrDefault(item => string.Equals(item.Object.DisplayName, sourceName, StringComparison.OrdinalIgnoreCase))
                    ?.Object;
        }

        return new VisualSource(
            sourceObject,
            GetArtifactRoot(context.Project?.HomeFolderPath, context.Project?.Path));
    }

    private BridgeArtifact RegisterImageArtifact(
        string path,
        RegistryObject? sourceObject,
        int width,
        int height,
        int dpi,
        string operation)
    {
        var createdUtc = DateTimeOffset.UtcNow;
        var registryObject = _objectRegistry.RegisterArtifact(
            path,
            "image/png",
            Path.GetFileName(path),
            sourceObject?.Id,
            sourceObject?.Kind,
            sourceObject?.DisplayName,
            new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["sourceObjectId"] = sourceObject?.Id,
                ["sourceObjectKind"] = sourceObject?.Kind,
                ["sourceObjectName"] = sourceObject?.DisplayName,
                ["width"] = width,
                ["height"] = height,
                ["dpi"] = dpi,
                ["createdUtc"] = createdUtc
            });

        return new BridgeArtifact(
            registryObject.Id,
            registryObject.Uri ?? $"arcgispro://artifact/{Uri.EscapeDataString(registryObject.Id)}",
            registryObject.Path ?? path,
            "image/png",
            createdUtc,
            sourceObject?.Id,
            sourceObject?.Kind,
            sourceObject?.DisplayName,
            width,
            height,
            dpi);
    }

    private BridgeArtifact RegisterFileArtifact(
        string path,
        string mimeType,
        RegistryObject? sourceObject,
        int? dpi,
        string operation)
    {
        var createdUtc = DateTimeOffset.UtcNow;
        var registryObject = _objectRegistry.RegisterArtifact(
            path,
            mimeType,
            Path.GetFileName(path),
            sourceObject?.Id,
            sourceObject?.Kind,
            sourceObject?.DisplayName,
            new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["sourceObjectId"] = sourceObject?.Id,
                ["sourceObjectKind"] = sourceObject?.Kind,
                ["sourceObjectName"] = sourceObject?.DisplayName,
                ["dpi"] = dpi,
                ["createdUtc"] = createdUtc
            });

        return new BridgeArtifact(
            registryObject.Id,
            registryObject.Uri ?? $"arcgispro://artifact/{Uri.EscapeDataString(registryObject.Id)}",
            registryObject.Path ?? path,
            mimeType,
            createdUtc,
            sourceObject?.Id,
            sourceObject?.Kind,
            sourceObject?.DisplayName,
            null,
            null,
            dpi);
    }

    private static object VisualSourceSummary(RegistryObject? sourceObject, string fallbackKind, string? fallbackName)
    {
        return new
        {
            id = sourceObject?.Id,
            kind = sourceObject?.Kind ?? fallbackKind,
            name = sourceObject?.DisplayName ?? fallbackName,
            uri = sourceObject?.Uri,
            path = sourceObject?.Path
        };
    }

    private static ThumbnailCapture? CaptureActiveViewThumbnail(int width, int height)
    {
        return InvokeOnUiThread(() =>
        {
            var layoutView = LayoutView.Active;
            if (layoutView is not null && layoutView.IsReady)
            {
                var bitmap = layoutView.CaptureThumbnail(width, height);
                if (bitmap is not null)
                {
                    FreezeBitmap(bitmap);
                    return new ThumbnailCapture(bitmap, "layout", layoutView.Layout?.URI, layoutView.Layout?.Name);
                }
            }

            var mapView = MapView.Active;
            if (mapView is not null && mapView.IsReady)
            {
                var bitmap = mapView.CaptureThumbnail(width, height);
                if (bitmap is not null)
                {
                    FreezeBitmap(bitmap);
                    return new ThumbnailCapture(bitmap, "map", mapView.Map?.URI, mapView.Map?.Name);
                }
            }

            return null;
        });
    }

    private static T InvokeOnUiThread<T>(Func<T> read)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return read();
        }

        return dispatcher.Invoke(read);
    }

    private static void SaveBitmapSourceAsPng(BitmapSource bitmap, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static void FreezeBitmap(BitmapSource bitmap)
    {
        if (bitmap.CanFreeze)
        {
            bitmap.Freeze();
        }
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private string GetArtifactRoot(string? homeFolder, string? projectPath)
    {
        var configuredRoot = _config.GetArtifactDirectory();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return configuredRoot;
        }

        if (!string.IsNullOrWhiteSpace(homeFolder))
        {
            return Path.Combine(homeFolder, "ArcGISProMcpBridge", "artifacts");
        }

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrWhiteSpace(projectDirectory))
            {
                return Path.Combine(projectDirectory, "ArcGISProMcpBridge", "artifacts");
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISProMcpBridge",
            "artifacts");
    }

    private static string CreateArtifactPath(string artifactRoot, string prefix, string? sourceName)
    {
        Directory.CreateDirectory(artifactRoot);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff");
        var safeSourceName = SanitizeFileName(string.IsNullOrWhiteSpace(sourceName) ? "active" : sourceName);
        var safePrefix = SanitizeFileName(prefix);
        return Path.Combine(artifactRoot, $"{safePrefix}_{safeSourceName}_{timestamp}_{Guid.NewGuid():N}.png");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value
            .Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch)
            .ToArray())
            .Trim(' ', '.', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "artifact" : cleaned;
    }

    private static int NormalizePixelDimension(int? value, int fallback)
    {
        return Math.Clamp(value.GetValueOrDefault(fallback), 64, 8192);
    }

    private static int NormalizeDpi(int? value, int fallback)
    {
        return Math.Clamp(value.GetValueOrDefault(fallback), 24, 1200);
    }

    private static object NotFound(string kind, string? id, string? name, RegistrySnapshot registry)
    {
        return new
        {
            projectLoaded = true,
            found = false,
            kind,
            requestedId = id,
            requestedName = name,
            registry = RegistrySummary(registry),
            checkedUtc = DateTimeOffset.UtcNow
        };
    }

    private static object[] LayerTree(RegistryContext context, string mapId)
    {
        return LayerTreeChildren(context, mapId);
    }

    private static object[] LayerTreeChildren(RegistryContext context, string parentId)
    {
        return context.Layers
            .Where(item => string.Equals(item.Object.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => SafeInt(item.Object.Properties.TryGetValue("index", out var index) ? index : null))
            .Select(item => new
            {
                layer = LayerSummary(item),
                children = LayerTreeChildren(context, item.Object.Id)
            })
            .Cast<object>()
            .ToArray();
    }

    private static object? ActiveCameraForMap(MapEntry entry)
    {
        var activeView = MapView.Active;
        return activeView is not null && SameUri(activeView.Map?.URI, entry.Map.URI)
            ? activeView.Camera
            : null;
    }

    private static object? SpatialReferenceSummary(object? spatialReference)
    {
        if (spatialReference is null)
        {
            return null;
        }

        return new
        {
            name = SafeProperty(spatialReference, "Name")?.ToString(),
            wkid = SafeProperty(spatialReference, "Wkid"),
            latestWkid = SafeProperty(spatialReference, "LatestWkid"),
            verticalWkid = SafeProperty(spatialReference, "VerticalWkid"),
            latestVerticalWkid = SafeProperty(spatialReference, "LatestVerticalWkid")
        };
    }

    private static object? CameraSummary(object? camera)
    {
        if (camera is null)
        {
            return null;
        }

        return new
        {
            x = SafeProperty(camera, "X"),
            y = SafeProperty(camera, "Y"),
            z = SafeProperty(camera, "Z"),
            scale = SafeProperty(camera, "Scale"),
            heading = SafeProperty(camera, "Heading"),
            pitch = SafeProperty(camera, "Pitch"),
            roll = SafeProperty(camera, "Roll"),
            viewpoint = SafeProperty(camera, "Viewpoint")?.ToString(),
            spatialReference = SpatialReferenceSummary(SafeProperty(camera, "SpatialReference"))
        };
    }

    private static object? EnvelopeSummary(object? envelope)
    {
        if (envelope is null)
        {
            return null;
        }

        return new
        {
            xMin = SafeProperty(envelope, "XMin"),
            yMin = SafeProperty(envelope, "YMin"),
            xMax = SafeProperty(envelope, "XMax"),
            yMax = SafeProperty(envelope, "YMax"),
            zMin = SafeProperty(envelope, "ZMin"),
            zMax = SafeProperty(envelope, "ZMax"),
            spatialReference = SpatialReferenceSummary(SafeProperty(envelope, "SpatialReference"))
        };
    }

    private static object? PageSummary(object? page)
    {
        if (page is null)
        {
            return null;
        }

        return new
        {
            width = SafeProperty(page, "Width"),
            height = SafeProperty(page, "Height"),
            units = SafeProperty(page, "Units")?.ToString()
        };
    }

    private static object? MapSeriesSummary(MapSeries? mapSeries)
    {
        if (mapSeries is null)
        {
            return null;
        }

        return new
        {
            type = mapSeries.GetType().Name,
            enabled = SafeValue(() => mapSeries.Enabled),
            pageCount = SafeValue(() => mapSeries.PageCount),
            currentPageName = SafeString(() => mapSeries.CurrentPageName),
            currentPageNumber = SafeValue(() => mapSeries.CurrentPageNumber),
            firstPageNumber = SafeValue(() => mapSeries.FirstPageNumber),
            lastPageNumber = SafeValue(() => mapSeries.LastPageNumber),
            startingPageNumber = SafeValue(() => mapSeries.StartingPageNumber),
            mapFrameName = SafeString(() => mapSeries.MapFrame?.Name)
        };
    }

    private static object? FeatureGeometrySummary(Layer layer)
    {
        if (layer is not BasicFeatureLayer basicFeatureLayer)
        {
            return null;
        }

        var featureClassSummary = layer is FeatureLayer featureLayer
            ? SafeFeatureClassSummary(featureLayer)
            : null;

        return new
        {
            shapeType = SafeString(() => basicFeatureLayer.ShapeType.ToString()),
            featureClass = featureClassSummary
        };
    }

    private static object? SafeFeatureClassSummary(FeatureLayer featureLayer)
    {
        try
        {
            using var featureClass = featureLayer.GetFeatureClass();
            var definition = featureClass.GetDefinition();
            return new
            {
                shapeType = definition.GetShapeType().ToString(),
                shapeField = definition.GetShapeField(),
                hasZ = definition.HasZ(),
                hasM = definition.HasM(),
                hasSpatialIndex = definition.HasSpatialIndex(),
                spatialReference = SpatialReferenceSummary(definition.GetSpatialReference()),
                extent = EnvelopeSummary(definition.GetExtent())
            };
        }
        catch
        {
            return null;
        }
    }

    private static object[] LayerFieldSummaries(Layer layer)
    {
        if (layer is not BasicFeatureLayer featureLayer)
        {
            return Array.Empty<object>();
        }

        try
        {
            var fields = ReadTableFields(featureLayer);
            return featureLayer.GetFieldDescriptions()
                .Select(description =>
                {
                    fields.TryGetValue(description.Name, out var field);
                    return new
                    {
                        name = description.Name,
                        alias = description.Alias,
                        type = description.Type.ToString(),
                        visible = description.IsVisible,
                        highlighted = description.IsHighlighted,
                        readOnly = description.IsReadOnly,
                        nullable = field?.IsNullable,
                        required = field?.IsRequired,
                        editable = field?.IsEditable,
                        length = field?.Length,
                        precision = field?.Precision,
                        scale = field?.Scale
                    };
                })
                .Cast<object>()
                .ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static Dictionary<string, FieldSchema> ReadTableFields(BasicFeatureLayer featureLayer)
    {
        try
        {
            using var table = featureLayer.GetTable();
            return table.GetDefinition()
                .GetFields()
                .ToDictionary(
                    field => field.Name,
                    field => new FieldSchema(
                        field.IsNullable,
                        field.IsRequired,
                        field.IsEditable,
                        field.Length,
                        field.Precision,
                        field.Scale),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, FieldSchema>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static object? LabelSummary(Layer layer)
    {
        if (layer is not FeatureLayer featureLayer)
        {
            return null;
        }

        return new
        {
            visible = SafeValue(() => featureLayer.IsLabelVisible),
            classCount = SafeCount(SafeValue(() => featureLayer.LabelClasses)),
            classes = SafeLabelClasses(featureLayer)
        };
    }

    private static object[] SafeLabelClasses(FeatureLayer featureLayer)
    {
        try
        {
            return featureLayer.LabelClasses
                .Select(labelClass => new
                {
                    name = labelClass.Name,
                    visible = labelClass.Visibility,
                    expression = labelClass.Expression,
                    expressionTitle = SafeProperty(labelClass, "ExpressionTitle"),
                    expressionEngine = labelClass.ExpressionEngine.ToString(),
                    whereClause = labelClass.WhereClause,
                    minimumScale = labelClass.MinimumScale,
                    maximumScale = labelClass.MaximumScale,
                    priority = labelClass.Priority
                })
                .Cast<object>()
                .ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static object? RendererSummary(Layer layer)
    {
        if (layer is not FeatureLayer featureLayer)
        {
            return null;
        }

        var renderer = SafeValue(() => featureLayer.GetRenderer());
        if (renderer is null)
        {
            return null;
        }

        return new
        {
            type = renderer.GetType().Name,
            label = SafeProperty(renderer, "Label"),
            description = SafeProperty(renderer, "Description"),
            fields = SafeStringArray(SafeProperty(renderer, "Fields")),
            groups = SafeCount(SafeProperty(renderer, "Groups")),
            breaks = SafeCount(SafeProperty(renderer, "Breaks")),
            normalizationField = SafeProperty(renderer, "NormalizationField"),
            normalizationType = SafeProperty(renderer, "NormalizationType")?.ToString(),
            useDefaultSymbol = SafeProperty(renderer, "UseDefaultSymbol"),
            defaultLabel = SafeProperty(renderer, "DefaultLabel"),
            visualVariableCount = SafeCount(SafeProperty(renderer, "VisualVariables"))
        };
    }

    private static bool IsBrokenConnectionStatus(string? connectionStatus)
    {
        return !string.IsNullOrWhiteSpace(connectionStatus)
            && !string.Equals(connectionStatus, "Connected", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveMapFrameId(IReadOnlyList<MapFrameEntry> mapFrames, Element element)
    {
        if (element is MapSurround surround)
        {
            return mapFrames.FirstOrDefault(item => ReferenceEquals(item.MapFrame, surround.MapFrame))?.Object.Id;
        }

        return null;
    }

    private static string LayoutElementKind(Element element)
    {
        return element switch
        {
            TextElement => "TextElement",
            Legend => "Legend",
            MapSurround => element.GetType().Name,
            _ => element.GetType().Name
        };
    }

    private static object? SafeValue(Func<object?> read)
    {
        try
        {
            return SanitizeJsonScalar(read());
        }
        catch
        {
            return null;
        }
    }

    private static object? SafeProperty(object? value, string propertyName)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return SanitizeJsonScalar(value.GetType().GetProperty(propertyName)?.GetValue(value));
        }
        catch
        {
            return null;
        }
    }

    private static object? SanitizeJsonScalar(object? value)
    {
        return value switch
        {
            double number when double.IsNaN(number) || double.IsInfinity(number) => null,
            float number when float.IsNaN(number) || float.IsInfinity(number) => null,
            _ => value
        };
    }

    private static int SafeCount(object? value)
    {
        return value switch
        {
            null => 0,
            System.Collections.ICollection collection => collection.Count,
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>().Count(),
            _ => 0
        };
    }

    private static int SafeInt(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static string[] SafeStringArray(object? value)
    {
        if (value is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return value is System.Collections.IEnumerable enumerable && value is not string
                ? enumerable.Cast<object>().Select(item => item.ToString() ?? string.Empty).ToArray()
                : new[] { value.ToString() ?? string.Empty };
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool MatchesMap(MapEntry map, string? mapId, string? mapName)
    {
        if (!string.IsNullOrWhiteSpace(mapId))
        {
            return string.Equals(map.Object.Id, mapId, StringComparison.OrdinalIgnoreCase);
        }

        return string.IsNullOrWhiteSpace(mapName) || string.Equals(map.Object.DisplayName, mapName, StringComparison.OrdinalIgnoreCase);
    }

    private static MapEntry? ResolveMap(RegistryContext context, string? mapId, string? mapName)
    {
        if (!string.IsNullOrWhiteSpace(mapId))
        {
            return context.Maps.FirstOrDefault(item => string.Equals(item.Object.Id, mapId, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        var matches = context.Maps
            .Where(item => string.Equals(item.Object.DisplayName, mapName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static LayerEntry? ResolveLayer(RegistryContext context, string? layerId, string? layerName, string? mapId, string? mapName)
    {
        if (!string.IsNullOrWhiteSpace(layerId))
        {
            return context.Layers.FirstOrDefault(item => string.Equals(item.Object.Id, layerId, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(layerName))
        {
            return null;
        }

        var layers = context.Layers.AsEnumerable();
        var map = ResolveMap(context, mapId, mapName);
        if (map is not null)
        {
            layers = layers.Where(item => item.MapObject.Id == map.Object.Id);
        }

        var matches = layers
            .Where(item => string.Equals(item.Object.DisplayName, layerName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static LayoutEntry? ResolveLayout(RegistryContext context, string? layoutId, string? layoutName)
    {
        if (!string.IsNullOrWhiteSpace(layoutId))
        {
            return context.Layouts.FirstOrDefault(item => string.Equals(item.Object.Id, layoutId, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(layoutName))
        {
            return null;
        }

        var matches = context.Layouts
            .Where(item => string.Equals(item.Object.DisplayName, layoutName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static MapEntry? ResolveMapEntry(IReadOnlyList<MapEntry> maps, Map? map)
    {
        if (map is null)
        {
            return null;
        }

        return maps.FirstOrDefault(item => ReferenceEquals(item.Map, map))
            ?? maps.FirstOrDefault(item => SameUri(item.Map.URI, map.URI))
            ?? maps.FirstOrDefault(item => string.Equals(item.Map.Name, map.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static string ProjectStableKey(Project project)
    {
        return project.Path ?? project.URI ?? project.Name;
    }

    private static string MapStableKey(string projectKey, Map map, int index)
    {
        return map.URI ?? $"{projectKey}/maps/{index}:{map.Name}";
    }

    private static string LayoutStableKey(string projectKey, Layout layout, int index)
    {
        return layout.URI ?? $"{projectKey}/layouts/{index}:{layout.Name}";
    }

    private static string LayerStableKey(string mapKey, Layer layer, int index)
    {
        return layer.URI ?? $"{mapKey}/layers/{index}:{layer.Name}";
    }

    private static string MapFrameStableKey(string layoutKey, MapFrame frame, int index)
    {
        return $"{layoutKey}/mapFrames/{index}:{frame.Name}";
    }

    private static string LayoutElementStableKey(string layoutKey, Element element, int index)
    {
        return $"{layoutKey}/elements/{index}:{element.GetType().Name}:{element.Name}";
    }

    private static bool SameUri(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetPath(object item)
    {
        try
        {
            var path = ((dynamic)item).GetPath();
            return path?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeString(Func<string?> read)
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

    private sealed record RegistryContext(
        Project? Project,
        RegistryObject? ProjectObject,
        IReadOnlyList<MapEntry> Maps,
        IReadOnlyList<LayerEntry> Layers,
        IReadOnlyList<LayoutEntry> Layouts,
        IReadOnlyList<MapFrameEntry> MapFrames,
        IReadOnlyList<LayoutElementEntry> LayoutElements,
        RegistrySnapshot Registry);

    private sealed record MapEntry(Map Map, RegistryObject Object, bool IsActive);

    private sealed record LayerEntry(Map Map, Layer Layer, RegistryObject MapObject, RegistryObject Object);

    private sealed record LayoutEntry(Layout Layout, RegistryObject Object, bool IsActive);

    private sealed record MapFrameEntry(MapFrame MapFrame, Layout Layout, RegistryObject LayoutObject, string? MapId, RegistryObject Object);

    private sealed record LayoutElementEntry(Element Element, Layout Layout, RegistryObject LayoutObject, string? MapFrameId, RegistryObject Object)
    {
        public Legend Legend => (Legend)Element;
    }

    private sealed record FieldSchema(bool IsNullable, bool IsRequired, bool IsEditable, int Length, int Precision, int Scale);

    private sealed record VisualSource(RegistryObject? SourceObject, string ArtifactRoot);

    private sealed record ThumbnailCapture(BitmapSource Bitmap, string SourceKind, string? SourceUri, string? SourceName);

    private sealed record GpParameterValue(int Index, string? Name, string Value);

    private sealed record GpEnvironmentValue(string Name, string Value);

    private sealed record GpParameterReadResult(
        bool Ok,
        IReadOnlyList<GpParameterValue> Parameters,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static GpParameterReadResult Success(IReadOnlyList<GpParameterValue> parameters)
        {
            return new GpParameterReadResult(true, parameters, null, null);
        }

        public static GpParameterReadResult Failure(string code, string message)
        {
            return new GpParameterReadResult(false, Array.Empty<GpParameterValue>(), code, message);
        }
    }

    private sealed record GpEnvironmentReadResult(
        bool Ok,
        IReadOnlyList<GpEnvironmentValue> Environments,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static GpEnvironmentReadResult Success(IReadOnlyList<GpEnvironmentValue> environments)
        {
            return new GpEnvironmentReadResult(true, environments, null, null);
        }

        public static GpEnvironmentReadResult Failure(string code, string message)
        {
            return new GpEnvironmentReadResult(false, Array.Empty<GpEnvironmentValue>(), code, message);
        }
    }

    private sealed record GpExecutionPreflight(
        bool Ok,
        string? ToolName,
        IReadOnlyList<GpParameterValue> Parameters,
        IReadOnlyList<GpEnvironmentValue> Environments,
        string[] Values,
        IReadOnlyList<KeyValuePair<string, string>> EnvironmentValues,
        string? ProjectHomeFolder,
        string? ProjectPath,
        bool AddOutputsToMap,
        bool AllowDestructive,
        bool DestructiveTool,
        string[] AllowedRoots,
        string[] CheckedPaths,
        IReadOnlyList<string> Warnings,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static GpExecutionPreflight Success(
            string toolName,
            IReadOnlyList<GpParameterValue> parameters,
            IReadOnlyList<GpEnvironmentValue> environments,
            string? projectHomeFolder,
            string? projectPath,
            bool addOutputsToMap,
            bool allowDestructive,
            bool destructiveTool,
            string[] allowedRoots,
            string[] checkedPaths)
        {
            var warnings = destructiveTool && allowDestructive
                ? new[] { $"Destructive geoprocessing tool '{toolName}' was explicitly enabled for this request." }
                : Array.Empty<string>();
            return new GpExecutionPreflight(
                true,
                toolName,
                parameters,
                environments,
                parameters.OrderBy(parameter => parameter.Index).Select(parameter => parameter.Value).ToArray(),
                environments.Select(environment => new KeyValuePair<string, string>(environment.Name, environment.Value)).ToArray(),
                projectHomeFolder,
                projectPath,
                addOutputsToMap,
                allowDestructive,
                destructiveTool,
                allowedRoots,
                checkedPaths,
                warnings,
                null,
                null);
        }

        public static GpExecutionPreflight Failure(
            string code,
            string message,
            string[] allowedRoots,
            string[] checkedPaths)
        {
            return new GpExecutionPreflight(
                false,
                null,
                Array.Empty<GpParameterValue>(),
                Array.Empty<GpEnvironmentValue>(),
                Array.Empty<string>(),
                Array.Empty<KeyValuePair<string, string>>(),
                null,
                null,
                false,
                false,
                false,
                allowedRoots,
                checkedPaths,
                allowedRoots.Select(root => $"Allowed root: {root}").ToArray(),
                code,
                message);
        }
    }

    private sealed record ArcpyScriptPreflight(
        bool Ok,
        string? ScriptPath,
        string? WorkingDirectory,
        string? OutputDirectory,
        string? ManifestPath,
        string? ToolboxPath,
        string? ToolPath,
        string? ArgumentsJson,
        bool SyntaxOnly,
        string[] AllowedRoots,
        string[] CheckedPaths,
        IReadOnlyList<string> Warnings,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static ArcpyScriptPreflight Success(
            string scriptPath,
            string workingDirectory,
            string outputDirectory,
            string manifestPath,
            string toolboxPath,
            string toolPath,
            string argumentsJson,
            bool syntaxOnly,
            string[] allowedRoots,
            string[] checkedPaths,
            IReadOnlyList<string> warnings)
        {
            return new ArcpyScriptPreflight(
                true,
                scriptPath,
                workingDirectory,
                outputDirectory,
                manifestPath,
                toolboxPath,
                toolPath,
                argumentsJson,
                syntaxOnly,
                allowedRoots,
                checkedPaths,
                warnings,
                null,
                null);
        }

        public static ArcpyScriptPreflight Failure(
            string code,
            string message,
            string[] allowedRoots,
            string[] checkedPaths)
        {
            return new ArcpyScriptPreflight(
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                allowedRoots,
                checkedPaths,
                allowedRoots.Select(root => $"Allowed root: {root}").ToArray(),
                code,
                message);
        }
    }

    private sealed record ProjectOperationSnapshot(
        bool Loaded,
        string? Name,
        string? Path,
        string? Uri,
        string? HomeFolder,
        string? DefaultGeodatabase,
        bool Dirty,
        bool ReadOnly)
    {
        public static ProjectOperationSnapshot NotLoaded()
        {
            return new ProjectOperationSnapshot(false, null, null, null, null, null, false, false);
        }

        public static ProjectOperationSnapshot FromProject(Project project)
        {
            return new ProjectOperationSnapshot(
                true,
                project.Name,
                project.Path,
                project.URI,
                project.HomeFolderPath,
                project.DefaultGeodatabasePath,
                project.IsDirty,
                project.ReadOnly);
        }
    }

    private sealed record ProjectSaveCopyPreflight(
        bool Ok,
        string? Path,
        string[] AllowedRoots,
        ProjectOperationSnapshot? Project,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static ProjectSaveCopyPreflight Success(string path, string[] allowedRoots, ProjectOperationSnapshot project)
        {
            return new ProjectSaveCopyPreflight(true, path, allowedRoots, project, null, null);
        }

        public static ProjectSaveCopyPreflight Failure(
            string code,
            string message,
            string? path,
            string[] allowedRoots,
            ProjectOperationSnapshot? project)
        {
            return new ProjectSaveCopyPreflight(false, path, allowedRoots, project, code, message);
        }
    }

    private sealed record MutationOperationResult(
        bool Ok,
        object? Data,
        string? ErrorCode,
        string? ErrorMessage,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Messages)
    {
        public static MutationOperationResult Success(
            object data,
            IReadOnlyList<string>? messages = null,
            IReadOnlyList<string>? warnings = null)
        {
            return new MutationOperationResult(
                true,
                data,
                null,
                null,
                warnings ?? Array.Empty<string>(),
                messages ?? Array.Empty<string>());
        }

        public static MutationOperationResult Failure(
            string code,
            string message,
            IReadOnlyList<string>? warnings = null,
            IReadOnlyList<string>? messages = null)
        {
            return new MutationOperationResult(
                false,
                null,
                code,
                message,
                warnings ?? Array.Empty<string>(),
                messages ?? Array.Empty<string>());
        }

        public BridgeResponse ToBridgeResponse(string requestId, long elapsedMs)
        {
            return Ok
                ? BridgeResponse.Success(
                    requestId,
                    Data,
                    elapsedMs,
                    messages: Messages,
                    warnings: Warnings)
                : BridgeResponse.Failure(
                    requestId,
                    ErrorCode ?? "mutation.error",
                    ErrorMessage ?? "Mutation operation failed.",
                    elapsedMs,
                    messages: Messages,
                    warnings: Warnings);
        }
    }

    private sealed record VisualOperationResult(
        bool Ok,
        object? Data,
        BridgeArtifact? Artifact,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static VisualOperationResult Success(object data, BridgeArtifact artifact)
        {
            return new VisualOperationResult(true, data, artifact, null, null);
        }

        public static VisualOperationResult Failure(string code, string message)
        {
            return new VisualOperationResult(false, null, null, code, message);
        }

        public BridgeResponse ToBridgeResponse(string requestId, long elapsedMs)
        {
            return Ok
                ? BridgeResponse.Success(
                    requestId,
                    Data,
                    elapsedMs,
                    artifacts: Artifact is null ? Array.Empty<BridgeArtifact>() : new[] { Artifact })
                : BridgeResponse.Failure(
                    requestId,
                    ErrorCode ?? "visual.error",
                    ErrorMessage ?? "Visual operation failed.",
                    elapsedMs);
        }
    }

    private sealed record ExportOperationResult(
        bool Ok,
        object? Data,
        BridgeArtifact? Artifact,
        string? ErrorCode,
        string? ErrorMessage,
        IReadOnlyList<string> Warnings)
    {
        public static ExportOperationResult Success(
            object data,
            BridgeArtifact? artifact,
            IReadOnlyList<string>? warnings = null)
        {
            return new ExportOperationResult(true, data, artifact, null, null, warnings ?? Array.Empty<string>());
        }

        public static ExportOperationResult Failure(
            string code,
            string message,
            IReadOnlyList<string>? warnings = null)
        {
            return new ExportOperationResult(false, null, null, code, message, warnings ?? Array.Empty<string>());
        }

        public BridgeResponse ToBridgeResponse(string requestId, long elapsedMs)
        {
            return Ok
                ? BridgeResponse.Success(
                    requestId,
                    Data,
                    elapsedMs,
                    warnings: Warnings,
                    artifacts: Artifact is null ? Array.Empty<BridgeArtifact>() : new[] { Artifact })
                : BridgeResponse.Failure(
                    requestId,
                    ErrorCode ?? "export.error",
                    ErrorMessage ?? "Export operation failed.",
                    elapsedMs,
                    warnings: Warnings);
        }
    }

    private sealed record LegendQaOperationResult(
        MutationOperationResult Result,
        IReadOnlyList<BridgeArtifact> Artifacts);

    private sealed record LegendQaAction(
        bool Applied,
        string Description,
        int Columns,
        string FittingStrategy,
        double MinFontSize,
        double FontSize,
        double PatchWidth,
        double PatchHeight);

    private sealed record LegendItemVisibilityChanges(
        int ChangedCount,
        IReadOnlyList<object> Items,
        IReadOnlyList<string> Warnings);

    private NamedPipeServerStream CreatePipeServerStream()
    {
        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private void AppendLogRequest(BridgeRequest request)
    {
        AppendLog($"request id={request.Id} op={request.Op} client={request.Client} dryRun={request.DryRun} timeoutMs={request.TimeoutMs}");
    }

    private void AppendLogResponse(BridgeRequest? request, BridgeResponse response)
    {
        var op = request?.Op ?? "<unknown>";
        var code = response.Error?.Code ?? "ok";
        AppendLog($"response id={response.Id} op={op} ok={response.Ok} code={code} elapsedMs={response.ElapsedMs}");
    }

    private void AppendAudit(BridgeRequest? request, BridgeResponse response)
    {
        BridgeAuditLog.Append(_auditLogPath, "arcgis-pro-addin", request, response);
    }

    private void AppendLog(string message)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must not interfere with ArcGIS Pro request handling.
        }
    }

    private static Task WriteResponseAsync(StreamWriter writer, BridgeResponse response)
    {
        return writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
    }
}
