using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AiTools.Gemini;

/// <summary>
/// 将 JSON Schema 文档规范化为 Gemini 接受的 OpenAPI 风格 Schema 方言。
/// </summary>
/// <remarks>
/// Gemini 要求类型名大写（例如 <c>OBJECT</c> 和 <c>STRING</c>），并拒绝若干标准 JSON Schema
/// 关键字。<see cref="Normalize(JsonElement)"/> 会递归改写类型名、丢弃 <c>$schema</c> 和
/// <c>additionalProperties</c> 等不支持的关键字，并原样保留 Gemini 能理解的任何关键字。
/// 该规范化器刻意保持宽容：未知关键字会被忽略，而不是引发异常。
/// </remarks>
public static class GeminiSchemaNormalizer
{
    private static readonly HashSet<string> SupportedKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "type",
        "format",
        "title",
        "description",
        "nullable",
        "enum",
        "maxItems",
        "minItems",
        "properties",
        "required",
        "items",
        "minProperties",
        "maxProperties",
        "minimum",
        "maximum",
        "minLength",
        "maxLength",
        "pattern",
        "example",
        "anyOf",
        "propertyOrdering",
        "default"
    };

    /// <summary>
    /// 返回 <paramref name="schema"/> 的规范化副本，适用于 Gemini 的
    /// <c>responseSchema</c> 或 function 声明中的 <c>parameters</c> 字段。
    /// </summary>
    /// <param name="schema">源 JSON Schema 元素。</param>
    /// <returns>克隆并规范化后的 JSON 元素。</returns>
    public static JsonElement Normalize(JsonElement schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
        }))
        {
            NormalizeValue(writer, schema);
        }

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 便捷重载：解析 JSON Schema 字符串并将其规范化。
    /// </summary>
    /// <param name="schemaJson">一个 JSON Schema 文档。</param>
    /// <returns>克隆并规范化后的 JSON 元素。</returns>
    public static JsonElement Normalize(string schemaJson)
    {
        if (schemaJson == null)
        {
            throw new ArgumentNullException(nameof(schemaJson));
        }

        using var document = JsonDocument.Parse(schemaJson);
        return Normalize(document.RootElement);
    }

    private static void NormalizeValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteNormalizedObject(writer, value);
                break;
            case JsonValueKind.Array:
                WriteNormalizedArray(writer, value);
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                value.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteNormalizedObject(Utf8JsonWriter writer, JsonElement value)
    {
        writer.WriteStartObject();
        var wroteType = false;
        var wroteNullable = false;
        var needsNullable = false;

        foreach (var property in value.EnumerateObject())
        {
            var name = property.Name;

            if (string.Equals(name, "properties", StringComparison.Ordinal))
            {
                WriteProperties(writer, property.Value);
                continue;
            }

            if (string.Equals(name, "items", StringComparison.Ordinal))
            {
                WriteItems(writer, property.Value);
                continue;
            }

            if (string.Equals(name, "anyOf", StringComparison.Ordinal))
            {
                WriteAnyOf(writer, property.Value, "anyOf");
                continue;
            }

            if (!SupportedKeywords.Contains(name))
            {
                // $schema、additionalProperties、$ref、oneOf、allOf、definitions 等。
                continue;
            }

            if (string.Equals(name, "type", StringComparison.Ordinal))
            {
                needsNullable = WriteType(writer, property.Value);
                wroteType = true;
                continue;
            }

            if (string.Equals(name, "nullable", StringComparison.Ordinal))
            {
                wroteNullable = true;
            }

            writer.WritePropertyName(name);
            if (string.Equals(name, "enum", StringComparison.Ordinal) ||
                string.Equals(name, "default", StringComparison.Ordinal) ||
                string.Equals(name, "example", StringComparison.Ordinal) ||
                string.Equals(name, "required", StringComparison.Ordinal) ||
                string.Equals(name, "propertyOrdering", StringComparison.Ordinal))
            {
                // 字面量值，而非子 Schema：原样复制可避免过滤其成员。
                property.Value.WriteTo(writer);
                continue;
            }

            NormalizeValue(writer, property.Value);
        }

        if (needsNullable && !wroteNullable)
        {
            // 形如 ["string", "null"] 的 JSON Schema 类型数组已转换为 type + nullable。
            writer.WriteBoolean("nullable", true);
        }

        if (!wroteType && !value.TryGetProperty("anyOf", out _))
        {
            // Gemini 拒绝没有 type 的 Schema。
            if (value.TryGetProperty("properties", out _))
            {
                writer.WriteString("type", "OBJECT");
            }
            else if (value.TryGetProperty("enum", out _))
            {
                writer.WriteString("type", "STRING");
            }
        }

        writer.WriteEndObject();
    }

    private static bool WriteType(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            writer.WriteString("type", NormalizeTypeName(value.GetString()) ?? "STRING");
            return false;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            // ["string", "null"] 在 JSON Schema 中很常见；Gemini 使用单一 type 加 nullable。
            string? first = null;
            var nullable = false;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var candidate = item.GetString();
                if (string.Equals(candidate, "null", StringComparison.OrdinalIgnoreCase))
                {
                    nullable = true;
                    continue;
                }

                if (first == null)
                {
                    first = candidate;
                }
            }

            writer.WriteString("type", NormalizeTypeName(first) ?? "STRING");
            return nullable;
        }

        writer.WriteString("type", "STRING");
        return false;
    }

    private static string? NormalizeTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        return typeName!.Trim().ToUpperInvariant();
    }

    private static void WriteProperties(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            WriteNormalizedObject(writer, property.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteItems(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WritePropertyName("items");
            WriteNormalizedObject(writer, value);
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            // 不支持元组校验；使用第一个元素作为 items Schema。
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("items");
                    WriteNormalizedObject(writer, item);
                    return;
                }
            }
        }
    }

    private static void WriteAnyOf(Utf8JsonWriter writer, JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                WriteNormalizedObject(writer, item);
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteNormalizedArray(Utf8JsonWriter writer, JsonElement value)
    {
        writer.WriteStartArray();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                WriteNormalizedObject(writer, item);
            }
            else
            {
                NormalizeValue(writer, item);
            }
        }

        writer.WriteEndArray();
    }
}
