using System.Text.Json;
using ArcGisProMcpServer.Tools;
using Xunit;

namespace ArcGisProMcpServer.Tests;

public class JsonArgumentNormalizerTests
{
    [Fact]
    public void Normalize_Null_ReturnsNull()
    {
        Assert.Null(JsonArgumentNormalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_StringifiedArray_ReturnsArrayElement()
    {
        var result = JsonArgumentNormalizer.Normalize("[\"Net green space change\"]");

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        Assert.Equal(1, element.GetArrayLength());
        Assert.Equal("Net green space change", element[0].GetString());
    }

    [Fact]
    public void Normalize_StringifiedObject_ReturnsObjectElement()
    {
        var result = JsonArgumentNormalizer.Normalize("{\"in_rows\": \"Net green space change\"}");

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("Net green space change", element.GetProperty("in_rows").GetString());
    }

    [Fact]
    public void Normalize_JsonElementStringHoldingArray_ReturnsArrayElement()
    {
        var wrapped = JsonSerializer.SerializeToElement("[\"a\", \"b\"]");
        Assert.Equal(JsonValueKind.String, wrapped.ValueKind);

        var result = JsonArgumentNormalizer.Normalize(wrapped);

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        Assert.Equal(2, element.GetArrayLength());
        Assert.Equal("a", element[0].GetString());
        Assert.Equal("b", element[1].GetString());
    }

    [Fact]
    public void Normalize_JsonElementStringHoldingObject_ReturnsObjectElement()
    {
        var wrapped = JsonSerializer.SerializeToElement("{\"key\": \"value\"}");

        var result = JsonArgumentNormalizer.Normalize(wrapped);

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("value", element.GetProperty("key").GetString());
    }

    [Fact]
    public void Normalize_LeadingWhitespaceBeforeJson_StillParses()
    {
        var result = JsonArgumentNormalizer.Normalize("  \t[1, 2]");

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        Assert.Equal(2, element.GetArrayLength());
    }

    [Fact]
    public void Normalize_PlainString_PassesThroughUnchanged()
    {
        var result = JsonArgumentNormalizer.Normalize("Net green space change");

        Assert.Equal("Net green space change", result);
    }

    [Fact]
    public void Normalize_JsonElementPlainString_PassesThroughUnchanged()
    {
        var wrapped = JsonSerializer.SerializeToElement("Net green space change");

        var result = JsonArgumentNormalizer.Normalize(wrapped);

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.String, element.ValueKind);
        Assert.Equal("Net green space change", element.GetString());
    }

    [Fact]
    public void Normalize_MalformedJsonStartingWithBracket_PassesThroughUnchanged()
    {
        var result = JsonArgumentNormalizer.Normalize("[not valid json");

        Assert.Equal("[not valid json", result);
    }

    [Fact]
    public void Normalize_JsonWithTrailingGarbage_PassesThroughUnchanged()
    {
        var result = JsonArgumentNormalizer.Normalize("[1, 2] extra");

        Assert.Equal("[1, 2] extra", result);
    }

    [Fact]
    public void Normalize_ActualArrayElement_PassesThroughUnchanged()
    {
        var array = JsonSerializer.SerializeToElement(new[] { "a", "b" });

        var result = JsonArgumentNormalizer.Normalize(array);

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        Assert.Equal(2, element.GetArrayLength());
    }

    [Fact]
    public void Normalize_ActualObjectElement_PassesThroughUnchanged()
    {
        var obj = JsonSerializer.SerializeToElement(new { in_rows = "layer" });

        var result = JsonArgumentNormalizer.Normalize(obj);

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("layer", element.GetProperty("in_rows").GetString());
    }

    [Fact]
    public void Normalize_EmptyString_PassesThroughUnchanged()
    {
        Assert.Equal(string.Empty, JsonArgumentNormalizer.Normalize(string.Empty));
    }

    [Fact]
    public void Normalize_WhitespaceString_PassesThroughUnchanged()
    {
        Assert.Equal("   ", JsonArgumentNormalizer.Normalize("   "));
    }

    [Fact]
    public void Normalize_NonStringScalars_PassThroughUnchanged()
    {
        Assert.Equal(42, JsonArgumentNormalizer.Normalize(42));
        Assert.Equal(true, JsonArgumentNormalizer.Normalize(true));

        var number = JsonSerializer.SerializeToElement(3.5);
        var result = Assert.IsType<JsonElement>(JsonArgumentNormalizer.Normalize(number));
        Assert.Equal(JsonValueKind.Number, result.ValueKind);
    }

    [Fact]
    public void Normalize_NestedStringifiedJson_ParsesOuterLayerOnly()
    {
        var result = JsonArgumentNormalizer.Normalize("[\"[inner]\"]");

        var element = Assert.IsType<JsonElement>(result);
        Assert.Equal(JsonValueKind.Array, element.ValueKind);
        Assert.Equal("[inner]", element[0].GetString());
    }
}
