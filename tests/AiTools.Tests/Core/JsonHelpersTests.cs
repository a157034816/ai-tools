using System.Text.Json;
using AiTools.Core.Json;
using Xunit;

namespace AiTools.Tests.Core;

public class JsonHelpersTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void GetString_ReturnsValueOrNull()
    {
        var element = Parse("{\"s\":\"text\",\"n\":1}");

        Assert.Equal("text", JsonHelpers.GetString(element, "s"));
        Assert.Null(JsonHelpers.GetString(element, "n"));
        Assert.Null(JsonHelpers.GetString(element, "missing"));
    }

    [Fact]
    public void GetInt32_HandlesNumbersAndNumericStrings()
    {
        var element = Parse("{\"n\":42,\"s\":\"7\",\"bad\":\"x\"}");

        Assert.Equal(42, JsonHelpers.GetInt32(element, "n"));
        Assert.Equal(7, JsonHelpers.GetInt32(element, "s"));
        Assert.Null(JsonHelpers.GetInt32(element, "bad"));
    }

    [Fact]
    public void GetBool_ReturnsValueOrNull()
    {
        var element = Parse("{\"t\":true,\"f\":false,\"s\":\"true\"}");

        Assert.True(JsonHelpers.GetBool(element, "t"));
        Assert.False(JsonHelpers.GetBool(element, "f"));
        Assert.Null(JsonHelpers.GetBool(element, "s"));
    }

    [Fact]
    public void GetObject_ReturnsNestedObject()
    {
        var element = Parse("{\"o\":{\"x\":1},\"s\":\"text\"}");

        var nested = JsonHelpers.GetObject(element, "o");
        Assert.NotNull(nested);
        Assert.Equal(1, nested!.Value.GetProperty("x").GetInt32());
        Assert.Null(JsonHelpers.GetObject(element, "s"));
    }

    [Fact]
    public void GetArray_EnumeratesElements()
    {
        var element = Parse("{\"a\":[1,2,3]}");
        var items = new List<int>();

        var array = JsonHelpers.GetArray(element, "a");
        Assert.NotNull(array);
        foreach (var item in array!.Value)
        {
            items.Add(item.GetInt32());
        }

        Assert.Equal(new[] { 1, 2, 3 }, items);
    }

    [Fact]
    public void DefaultOptions_IgnoresNullsAndKeepsUnicode()
    {
        var json = JsonSerializer.Serialize(new { Text = "你好", Missing = (string?)null }, JsonHelpers.DefaultOptions);

        Assert.Contains("你好", json);
        Assert.DoesNotContain("Missing", json);
    }
}
