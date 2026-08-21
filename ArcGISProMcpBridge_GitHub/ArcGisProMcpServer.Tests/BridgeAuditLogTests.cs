using System.Text.Json;
using ArcGisProBridgeContracts;
using Xunit;

namespace ArcGisProMcpServer.Tests;

public class BridgeAuditLogTests : IDisposable
{
    private readonly string _directory;
    private readonly string _auditPath;

    public BridgeAuditLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "arcgis-pro-mcp-audit-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        _auditPath = Path.Combine(_directory, "audit.jsonl");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failures must not fail the test run.
        }
    }

    private JsonElement AppendAndRead(BridgeRequest request, BridgeResponse? response = null)
    {
        BridgeAuditLog.Append(
            _auditPath,
            "tests",
            request,
            response ?? BridgeResponse.Success(request.Id, new { done = true }, 12));

        var lines = File.ReadAllLines(_auditPath);
        var line = Assert.Single(lines);
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    private static JsonObjectMap Args(object values)
    {
        return JsonSerializer.Deserialize<JsonObjectMap>(JsonSerializer.Serialize(values))!;
    }

    [Fact]
    public void Append_RecordsActorIdentity()
    {
        var record = AppendAndRead(BridgeRequest.Create("map.list"));

        var actor = record.GetProperty("actor");
        Assert.Equal(Environment.UserName, actor.GetProperty("user").GetString());
        Assert.Equal(Environment.UserDomainName, actor.GetProperty("domain").GetString());
        Assert.Equal(Environment.MachineName, actor.GetProperty("machine").GetString());
        Assert.Equal(Environment.ProcessId, actor.GetProperty("processId").GetInt32());
    }

    [Fact]
    public void Append_GeoprocessingParameters_AreRecordedVerbatim()
    {
        var record = AppendAndRead(BridgeRequest.Create(
            "geoprocessing.execute_tool",
            Args(new
            {
                toolName = "analysis.Buffer",
                parameters = new object[] { "parcels", "parcels_buf", "150 Feet" }
            })));

        Assert.Equal("full", record.GetProperty("argsFidelity").GetString());

        var parameters = record.GetProperty("argsSummary").GetProperty("parameters");
        Assert.Equal(JsonValueKind.Array, parameters.ValueKind);
        Assert.Equal(3, parameters.GetArrayLength());
        Assert.Equal("150 Feet", parameters[2].GetString());
    }

    [Fact]
    public void Append_LongDefinitionQuery_IsRecordedVerbatim()
    {
        var query = string.Join(" OR ", Enumerable.Range(0, 60).Select(i => $"LANDUSE = 'GREEN_{i}'"));
        Assert.True(query.Length > 300, "The query must exceed the summary truncation limit.");

        var record = AppendAndRead(BridgeRequest.Create(
            "layer.set_definition_query",
            Args(new { layerId = "layer-1", definitionQuery = query })));

        // The layer group is summarized, but the query argument itself is always recorded in full.
        Assert.Equal("summary", record.GetProperty("argsFidelity").GetString());
        Assert.Equal(query, record.GetProperty("argsSummary").GetProperty("definitionQuery").GetString());
    }

    [Fact]
    public void Append_UnremarkableStringArgument_IsStillTruncated()
    {
        var note = new string('x', 400);

        var record = AppendAndRead(BridgeRequest.Create(
            "layout.set_text",
            Args(new { elementId = "text-1", text = note })));

        var recorded = record.GetProperty("argsSummary").GetProperty("text").GetString();
        Assert.Equal(303, recorded!.Length);
        Assert.EndsWith("...", recorded);
    }

    [Fact]
    public void Append_ArcPyScript_RecordsHashAndArchivesContent()
    {
        var scriptPath = Path.Combine(_directory, "greenspace.py");
        const string body = "import arcpy\narcpy.management.Delete('scratch')\n";
        File.WriteAllText(scriptPath, body);

        var record = AppendAndRead(BridgeRequest.Create(
            "python.run_arcpy_script",
            Args(new { scriptPath, confirmScriptExecution = true })));

        var script = record.GetProperty("script");
        Assert.True(script.GetProperty("exists").GetBoolean());
        Assert.Equal(64, script.GetProperty("sha256").GetString()!.Length);

        var archived = script.GetProperty("archivedCopy").GetString();
        Assert.NotNull(archived);
        Assert.Equal(body, File.ReadAllText(archived!));

        // The archived copy must survive later edits to the original script.
        File.WriteAllText(scriptPath, "import arcpy\n# replaced\n");
        Assert.Equal(body, File.ReadAllText(archived!));
    }

    [Fact]
    public void Append_MissingScript_IsRecordedAsMissing()
    {
        var record = AppendAndRead(BridgeRequest.Create(
            "python.run_arcpy_script",
            Args(new { scriptPath = Path.Combine(_directory, "absent.py") })));

        Assert.False(record.GetProperty("script").GetProperty("exists").GetBoolean());
    }

    [Fact]
    public void Append_NonPythonOperation_HasNoScriptBlock()
    {
        var record = AppendAndRead(BridgeRequest.Create("map.list"));

        Assert.Equal(JsonValueKind.Null, record.GetProperty("script").ValueKind);
    }

    [Fact]
    public void Append_DeniedOperation_IsStillRecorded()
    {
        var request = BridgeRequest.Create("python.run_arcpy_script", Args(new { scriptPath = "nope.py" }));
        var response = BridgeResponse.Failure(request.Id, "bridge.tool_group_disabled", "Disabled group.", 3);

        var record = AppendAndRead(request, response);

        var result = record.GetProperty("result");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("bridge.tool_group_disabled", result.GetProperty("code").GetString());
    }
}
