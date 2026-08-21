using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ArcGisProMcpServer.Prompts;

[McpServerPromptType]
public static class WorkflowPrompts
{
    [McpServerPrompt(Name = "workflow_inspect_project", Title = "Inspect ArcGIS Pro Project")]
    [Description("Inspect the current ArcGIS Pro project and summarize map, layer, layout, and data-source state.")]
    public static string InspectProject(
        [Description("Optional focus such as a map name, layout name, layer type, or production question.")] string? focus = null)
    {
        return WithContext($"""
Use the ArcGIS Pro MCP tools to inspect the current project and produce a concise production summary.

Focus: {ValueOrDefault(focus, "overall project, active map, layouts, visible layers, broken sources, and export readiness")}

Workflow:
1. Call `pro_health`. Stop and report the bridge state if ArcGIS Pro is not ready.
2. Call `project_get_current`, `object_registry`, `map_list`, `layer_list`, and `layout_list`.
3. For the active map and any map relevant to the focus, call `map_get_state`.
4. For important visible layers, thematic layers, broken layers, or layers relevant to the focus, call `layer_get_state`.
5. For important layouts or layouts relevant to the focus, call `layout_get_state`.
6. Summarize maps, layer groups, definition queries, renderers, labels, layout elements, map frames, legends, map series state, broken sources, and obvious production risks.
7. Do not mutate the project. Include stable object IDs for any map, layer, layout, map frame, legend, or text element that a later workflow may need.

Final response shape:
- Project status and active context.
- Map and layer summary.
- Layout and export readiness summary.
- Broken data sources or risks.
- Recommended next actions with object IDs.
""");
    }

    [McpServerPrompt(Name = "workflow_iterate_layout_change", Title = "Iterate Layout Change")]
    [Description("Make a requested layout change, preview it, critique the rendered result, and iterate conservatively.")]
    public static string IterateLayoutChange(
        [Description("Requested layout change, for example update title, adjust map frame, or hide an existing surround.")] string changeRequest,
        [Description("Optional target layout ID from layout_list or object_registry.")] string? layoutId = null,
        [Description("Optional target layout name when the ID is not known yet.")] string? layoutName = null,
        [Description("Preview export DPI.")] int? dpi = null)
    {
        return WithContext($"""
Use the ArcGIS Pro MCP tools to make the requested layout change, preview it, critique the result, and iterate if needed.

Change request: {changeRequest}
Target layout ID: {ValueOrDefault(layoutId, "discover from layout_list/layout_get_state")}
Target layout name: {ValueOrDefault(layoutName, "not provided")}
Preview DPI: {dpi?.ToString() ?? "default"}

Workflow:
1. Call `pro_health`, then `layout_list`. Resolve the target layout by ID first; use name only to discover an ID.
2. Call `layout_get_state` for the target layout and identify the stable IDs for affected text elements, map frames, legends, north arrows, or scale bars.
3. Before mutating, call the relevant mutation tool with `dryRun=true`: `layout_set_text`, `layout_set_map_frame_camera`, or `layout_set_surround_visibility`.
4. If the dry run matches the request, run the same mutation with `dryRun=false`.
5. Call `visual_export_layout_preview` for the layout and inspect the returned image.
6. Critique the preview for text fit, occlusion, map frame extent, legend/surround overlap, page balance, and whether the requested change actually appears.
7. If one conservative follow-up edit is clearly needed, dry-run it first, apply it, and export one more preview.
8. Do not call `project_save` unless the user explicitly requested saving. Prefer reporting the preview artifact URI and remaining risks.

Final response shape:
- What changed, with object IDs.
- Preview artifact URI(s).
- Visual critique and any iteration made.
- Whether the project is unsaved and what save/export action remains.
""");
    }

    [McpServerPrompt(Name = "workflow_improve_legend", Title = "Improve Legend Layout")]
    [Description("Improve a cramped or unbalanced legend, preview it, and iterate without dropping essential thematic information.")]
    public static string ImproveLegend(
        [Description("Optional target layout ID from layout_list or object_registry.")] string? layoutId = null,
        [Description("Optional legend element ID from layout_get_state or object_registry.")] string? legendElementId = null,
        [Description("Optional target layout name when the ID is not known yet.")] string? layoutName = null,
        [Description("Specific legend concern, such as cramped text, too many rows, or overlap.")] string? concern = null,
        [Description("Preview export DPI.")] int? dpi = null)
    {
        return WithContext($"""
Use the ArcGIS Pro MCP tools to improve an existing layout legend while preserving map meaning.

Target layout ID: {ValueOrDefault(layoutId, "discover from layout_list/layout_get_state")}
Legend element ID: {ValueOrDefault(legendElementId, "discover from layout_get_state/object_registry")}
Target layout name: {ValueOrDefault(layoutName, "not provided")}
Concern: {ValueOrDefault(concern, "legend is cramped, unbalanced, overflowing, or visually competing with the map")}
Preview DPI: {dpi?.ToString() ?? "default"}

Workflow:
1. Call `pro_health`, `layout_list`, and `layout_get_state`; resolve the target layout and legend IDs.
2. Call `legend_get_state` and inspect item count, title, columns, fitting strategy, bounds, linked map frame, excluded layers, and fit-risk metadata.
3. Export a before preview with `visual_export_layout_preview`.
4. Prefer edits in this order: compact typography/patch spacing with `legend_apply_compact_style`, adjust columns/fitting/bounds with `legend_set_layout`, then rename overly long labels with `legend_rename_items` only when the replacement is clear.
5. Do not hide thematic legend items unless the user explicitly authorized it; if using `legend_set_items` to hide anything, explain the map-meaning impact and set `allowHideThematic=true` only after explicit user intent.
6. Dry-run each proposed edit before applying it.
7. Apply one conservative edit, export an after preview, and critique before/after fit, readability, overlap, balance, and preserved thematic information.
8. If a second conservative edit is clearly needed, dry-run it, apply it, and export one more preview.
9. Do not call `project_save` unless the user explicitly requested saving.

Final response shape:
- Legend diagnosis.
- Edits made and object IDs.
- Before/after artifact URI(s).
- Visual critique and remaining tradeoffs.
- Confirmation that essential thematic information was preserved or a clear warning if not.
""");
    }

    [McpServerPrompt(Name = "workflow_gp_style_export", Title = "Run GP, Style, Export")]
    [Description("Run geoprocessing, add output to the map, style it when possible, preview, and export.")]
    public static string RunGeoprocessingStyleExport(
        [Description("Geoprocessing goal or exact ArcGIS tool name, for example management.Buffer.")] string geoprocessingGoal,
        [Description("Optional parameter notes or JSON-like parameter plan.")] string? parameters = null,
        [Description("Optional target map ID.")] string? mapId = null,
        [Description("Optional output layer symbology .lyrx path under an allowed root.")] string? symbologyLayerPath = null,
        [Description("Optional target layout ID to preview/export after adding outputs.")] string? layoutId = null,
        [Description("Optional final export path under an allowed root.")] string? exportPath = null)
    {
        return WithContext($"""
Use the ArcGIS Pro MCP tools to run a guarded geoprocessing workflow, add the output to the map, style it when possible, and create a preview/export.

Geoprocessing goal/tool: {geoprocessingGoal}
Parameter notes: {ValueOrDefault(parameters, "discover or ask if required parameters are missing")}
Target map ID: {ValueOrDefault(mapId, "active or discovered map")}
Symbology .lyrx path: {ValueOrDefault(symbologyLayerPath, "none")}
Target layout ID: {ValueOrDefault(layoutId, "optional; discover if needed")}
Export path: {ValueOrDefault(exportPath, "optional; only export when requested")}

Workflow:
1. Call `pro_health`, `project_get_current`, `map_list`, and `layer_list`. Resolve input layer IDs and data paths.
2. Prepare the `geoprocessing_execute_tool` request. Use `dryRun=true` first with `addOutputsToMap=true`; include environments only when needed.
3. Do not set `allowDestructive=true` unless the user explicitly requested a destructive or in-place operation and the risk is acceptable.
4. If required parameters or output paths are ambiguous, stop and ask for the missing value instead of guessing.
5. Run `geoprocessing_execute_tool` with `addOutputsToMap=true`.
6. Call `layer_list` again and identify newly added output layers.
7. If a `.lyrx` path was supplied, call `layer_apply_symbology_from_layer` on the output layer with `dryRun=true`, then apply it if the dry run is valid.
8. Preview the map or layout with `visual_export_active_map` or `visual_export_layout_preview`.
9. If an export path was requested and a target layout is known, call `export_layout`; otherwise report the preview artifact and output artifacts.
10. Do not save unless the user explicitly requested saving.

Final response shape:
- GP tool, parameters, messages, and outputs.
- Added layer IDs and any styling applied.
- Preview/export artifact URI(s).
- Warnings, unsaved project state, and recommended next action.
""");
    }

    [McpServerPrompt(Name = "workflow_diagnose_broken_sources", Title = "Diagnose Broken Data Sources")]
    [Description("Diagnose broken layers or missing data sources in the current ArcGIS Pro project.")]
    public static string DiagnoseBrokenSources(
        [Description("Optional layer ID, layer name, workspace path, or symptom to focus on.")] string? focus = null)
    {
        return WithContext($"""
Use the ArcGIS Pro MCP tools to diagnose broken layers or missing data sources without mutating the project.

Focus: {ValueOrDefault(focus, "all broken layers and missing data-source risks")}

Workflow:
1. Call `pro_health`; stop if ArcGIS Pro is not ready.
2. Call `project_get_current`, `object_registry`, `map_list`, and `layer_list`.
3. Identify layers marked broken, layers with empty or suspicious data sources, duplicate names, and layers relevant to the focus.
4. For each suspect layer, call `layer_get_state` and capture layer ID, map ID, parent group, data source, URI, definition query, geometry type, renderer, labels, and broken-state details.
5. Call `map_get_state` for maps containing suspect layers to understand group context and visibility.
6. Check `arcgispro://logs/current` if recent bridge operations may have changed sources.
7. Do not change connection properties or remove layers. This bridge does not currently expose a repair-data-source mutation, so provide a repair plan rather than inventing a tool.
8. If enough information is available, suggest concrete candidate paths or workspace checks under the project home/default geodatabase/allowed roots; otherwise state exactly what is missing.

Final response shape:
- Broken/suspect layers table with stable IDs.
- Most likely causes.
- Data paths or workspaces to verify.
- Repair plan for ArcGIS Pro or a future bridge tool.
- Any limitations in what the MCP bridge can currently fix directly.
""");
    }

    private static string WithContext(string workflow)
    {
        return $"""
You are operating ArcGIS Pro through the local ArcGIS Pro MCP Bridge.

Bridge operating rules:
- Use stable object IDs from read tools for mutations; use names only to discover IDs.
- Prefer read and dry-run calls before any mutation.
- Preserve project meaning and cartographic intent.
- Do not save, overwrite exports, run destructive geoprocessing, or execute arbitrary scripts unless the user explicitly requested it.
- When a rendered preview is available, inspect it before claiming visual success.
- Report artifact URIs, warnings, and unsaved project state.

{workflow}
""";
    }

    private static string ValueOrDefault(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
