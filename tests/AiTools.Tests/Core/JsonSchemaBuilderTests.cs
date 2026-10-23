using System.Text.Json;
using AiTools.Core.Tools;
using Xunit;

namespace AiTools.Tests.Core;

public class JsonSchemaBuilderTests
{
    [Fact]
    public void Build_CreatesObjectWithPropertiesAndRequired()
    {
        var schema = JsonSchemaBuilder.Object("天气查询参数")
            .Property("city", JsonSchemaBuilder.String("城市名称"))
            .Property("days", JsonSchemaBuilder.Integer("预报天数"))
            .Required("city")
            .Build();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("天气查询参数", schema.GetProperty("description").GetString());

        var properties = schema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("city").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("days").GetProperty("type").GetString());

        var required = schema.GetProperty("required");
        Assert.Single(required.EnumerateArray());
        Assert.Equal("city", required[0].GetString());
    }

    [Fact]
    public void Build_CreatesArrayOfObjects()
    {
        var itemSchema = JsonSchemaBuilder.Object()
            .Property("name", JsonSchemaBuilder.String())
            .Required("name");

        var schema = JsonSchemaBuilder.Array(itemSchema).Build();

        Assert.Equal("array", schema.GetProperty("type").GetString());
        Assert.Equal("object", schema.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("items").GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
    }

    [Fact]
    public void Build_CreatesEnum()
    {
        var schema = JsonSchemaBuilder.Enumeration(new[] { "low", "medium", "high" }).Build();

        var values = schema.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "low", "medium", "high" }, values);
    }

    [Fact]
    public void Build_EscapesSpecialCharactersInDescriptions()
    {
        var schema = JsonSchemaBuilder.String("引号\"与\\反斜杠").Build();

        Assert.Equal("引号\"与\\反斜杠", schema.GetProperty("description").GetString());
    }

    [Fact]
    public void ToolBuilder_CreatesToolDefinition()
    {
        var tool = ToolBuilder.Create(
            "search",
            "搜索",
            JsonSchemaBuilder.Object().Property("q", JsonSchemaBuilder.String()).Required("q"));

        Assert.Equal("search", tool.Name);
        Assert.NotNull(tool.ParametersSchema);
        Assert.Equal("object", tool.ParametersSchema!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public void Build_EmptyObject_ProducesValidSchema()
    {
        var schema = JsonSchemaBuilder.Object().Build();

        var json = schema.GetRawText();
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("object", parsed.RootElement.GetProperty("type").GetString());
    }
}
