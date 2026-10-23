using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using AiTools.Abstractions;

namespace AiTools.Core.Tools;

/// <summary>以 C# 代码构建 JSON Schema 的流式构建器，用于定义工具参数与结构化输出。</summary>
/// <example>
/// <code>
/// var schema = JsonSchemaBuilder.Object()
///     .Property("city", JsonSchemaBuilder.String("城市名称"))
///     .Required("city")
///     .Build();
///
/// var tool = ToolDefinition.Create("get_weather", "查询天气", schema);
/// </code>
/// </example>
public sealed class JsonSchemaBuilder
{
    private readonly string _type;
    private readonly string? _description;
    private readonly List<string> _requiredArray = new();
    private string? _enumJson;
    private Dictionary<string, JsonSchemaBuilder>? _propertyMap;
    private JsonSchemaBuilder? _items;

    private JsonSchemaBuilder(string type, string? description = null)
    {
        _type = type;
        _description = description;
    }

    /// <summary>创建对象类型的 Schema 构建器。</summary>
    public static JsonSchemaBuilder Object(string? description = null) => new("object", description);

    /// <summary>创建字符串类型的 Schema 构建器。</summary>
    public static JsonSchemaBuilder String(string? description = null) => new("string", description);

    /// <summary>创建整数类型的 Schema 构建器。</summary>
    public static JsonSchemaBuilder Integer(string? description = null) => new("integer", description);

    /// <summary>创建数字类型的 Schema 构建器。</summary>
    public static JsonSchemaBuilder Number(string? description = null) => new("number", description);

    /// <summary>创建布尔类型的 Schema 构建器。</summary>
    public static JsonSchemaBuilder Boolean(string? description = null) => new("boolean", description);

    /// <summary>创建数组类型的 Schema 构建器。</summary>
    public static JsonSchemaBuilder Array(JsonSchemaBuilder items, string? description = null)
    {
        var builder = new JsonSchemaBuilder("array", description);
        builder._items = items;
        return builder;
    }

    /// <summary>创建枚举类型的 Schema 构建器。</summary>
    public static JsonSchemaBuilder Enumeration(IEnumerable<string> values, string? description = null)
    {
        var builder = new JsonSchemaBuilder("string", description);
        builder._enumJson = SerializeStringArray(values);
        return builder;
    }

    /// <summary>添加对象属性。</summary>
    public JsonSchemaBuilder Property(string name, JsonSchemaBuilder schema)
    {
        if (_propertyMap == null)
        {
            _propertyMap = new Dictionary<string, JsonSchemaBuilder>(StringComparer.Ordinal);
        }

        _propertyMap[name] = schema;
        return this;
    }

    /// <summary>标记属性为必填。</summary>
    public JsonSchemaBuilder Required(params string[] names)
    {
        if (names == null)
        {
            throw new ArgumentNullException(nameof(names));
        }

        foreach (var name in names)
        {
            if (!_requiredArray.Contains(name))
            {
                _requiredArray.Add(name);
            }
        }

        return this;
    }

    /// <summary>构建 Schema 的 JSON 元素。</summary>
    public JsonElement Build()
    {
        var json = BuildJson();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>构建 Schema 的 JSON 字符串。</summary>
    public string BuildJson()
    {
        var builder = new StringBuilder();
        builder.Append('{');
        builder.Append("\"type\":").Append(Quote(_type));

        if (_description != null)
        {
            builder.Append(",\"description\":").Append(Quote(_description));
        }

        if (_enumJson != null)
        {
            builder.Append(",\"enum\":").Append(_enumJson);
        }

        if (_type == "object" && _propertyMap != null)
        {
            builder.Append(",\"properties\":{");
            var first = true;
            foreach (var pair in _propertyMap)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(Quote(pair.Key)).Append(':').Append(pair.Value.BuildJson());
            }

            builder.Append('}');

            if (_requiredArray.Count > 0)
            {
                builder.Append(",\"required\":[");
                for (var i = 0; i < _requiredArray.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(Quote(_requiredArray[i]));
                }

                builder.Append(']');
            }
        }

        if (_type == "array" && _items != null)
        {
            builder.Append(",\"items\":").Append(_items.BuildJson());
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(c); break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string SerializeStringArray(IEnumerable<string> values)
    {
        var builder = new StringBuilder("[");
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(Quote(value));
        }

        builder.Append(']');
        return builder.ToString();
    }
}

/// <summary>工具定义的便捷构建器。</summary>
public static class ToolBuilder
{
    /// <summary>创建带参数 Schema 的工具定义。</summary>
    public static ToolDefinition Create(string name, string description, JsonSchemaBuilder parameters)
        => ToolDefinition.Create(name, description, parameters.Build());
}
