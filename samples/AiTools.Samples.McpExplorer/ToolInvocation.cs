using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiTools.Mcp;
using AiTools.Samples.McpExplorer.ViewModels;

namespace AiTools.Samples.McpExplorer;

/// <summary>用于解析参数 JSON 和呈现 MCP 内容的静态辅助方法。</summary>
internal static class ToolInvocation
{
    /// <summary>单个文本块渲染的最大字符数。</summary>
    internal const int DefaultTextLimit = 4000;

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>解析适合用作 <c>tools/call</c> 参数的 JSON 对象。</summary>
    /// <param name="json">原始 JSON 文本；空文本将视为空对象。</param>
    /// <param name="arguments">解析后的参数对象。</param>
    /// <param name="error">解析失败时人类可读的错误信息。</param>
    /// <returns>解析成功时返回 <see langword="true"/>。</returns>
    internal static bool TryParseArgumentsObject(string? json, out JsonElement arguments, out string? error)
    {
        arguments = default;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            arguments = JsonSerializer.SerializeToElement(new { });
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "参数必须是 JSON 对象，例如 {\"text\":\"你好\"}。";
                return false;
            }

            arguments = document.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = "JSON 无效：" + ex.Message;
            return false;
        }
    }

    /// <summary>格式化 JSON 文本并缩进；无效的 JSON 原样返回。</summary>
    /// <param name="json">原始 JSON 文本。</param>
    /// <returns>缩进后的 JSON 或原始文本。</returns>
    internal static string PrettyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, PrettyOptions);
        }
        catch (JsonException)
        {
            return json!;
        }
    }

    /// <summary>将 JSON 元素序列化为缩进格式。</summary>
    /// <param name="element">要序列化的元素。</param>
    /// <returns>缩进后的 JSON。</returns>
    internal static string PrettyJson(JsonElement element)
    {
        return JsonSerializer.Serialize(element, PrettyOptions);
    }

    /// <summary>将 JSON 节点序列化为缩进格式。</summary>
    /// <param name="node">要序列化的节点。</param>
    /// <returns>缩进后的 JSON。</returns>
    internal static string PrettyJson(JsonNode node) => node.ToJsonString(PrettyOptions);

    /// <summary>截断过长的文本，并附加一条说明原始大小的注释。</summary>
    /// <param name="text">要截断的文本。</param>
    /// <param name="max">最大字符数。</param>
    /// <returns>原始文本或截断后的形式。</returns>
    internal static string Truncate(string text, int max = DefaultTextLimit)
    {
        if (max <= 0 || text.Length <= max)
        {
            return text;
        }

        return text[..max] + $"\n…（已截断，共 {text.Length} 字符）";
    }

    /// <summary>描述架构属性的类型，包括枚举和组合架构。</summary>
    /// <param name="schema">属性架构。</param>
    /// <returns>诸如 <c>string</c> 或 <c>enum</c> 的简短类型标签。</returns>
    internal static string DescribeSchemaType(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "unknown";
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
        {
            return "enum";
        }

        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                return type.GetString() ?? "unknown";
            }

            if (type.ValueKind == JsonValueKind.Array)
            {
                var types = new List<string>();
                foreach (var item in type.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        types.Add(item.GetString()!);
                    }
                }

                if (types.Count > 0)
                {
                    return string.Join(" | ", types);
                }
            }
        }

        if (schema.TryGetProperty("properties", out _))
        {
            return "object";
        }

        if (schema.TryGetProperty("items", out _))
        {
            return "array";
        }

        return "unknown";
    }

    /// <summary>从属性架构中读取枚举值。</summary>
    /// <param name="schema">属性架构。</param>
    /// <returns>允许值的字符串表示形式。</returns>
    internal static List<string> ReadEnumValues(JsonElement schema)
    {
        var values = new List<string>();
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("enum", out var enumValues)
            || enumValues.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var item in enumValues.EnumerateArray())
        {
            values.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText());
        }

        return values;
    }

    /// <summary>读取字符串类型的架构关键字，例如 <c>description</c> 或 <c>title</c>。</summary>
    /// <param name="schema">属性架构。</param>
    /// <param name="name">关键字名称。</param>
    /// <returns>字符串值或 null。</returns>
    internal static string? ReadString(JsonElement schema, string name)
    {
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>将 <c>default</c> 关键字读取为显示文本。</summary>
    /// <param name="schema">属性架构。</param>
    /// <returns>默认值的文本形式，或 null。</returns>
    internal static string? ReadDefaultText(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("default", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }

    /// <summary>把工具结果的内容块转换为显示块。</summary>
    /// <param name="result">工具结果。</param>
    /// <returns>每个内容块对应一个显示块。</returns>
    internal static List<ResultBlockViewModel> BuildResultBlocks(McpCallToolResult result)
    {
        var blocks = new List<ResultBlockViewModel>();
        foreach (var content in result.Content)
        {
            blocks.Add(BuildResultBlock(content));
        }

        return blocks;
    }

    /// <summary>把单个 MCP 内容块转换为显示块。</summary>
    /// <param name="content">内容块。</param>
    /// <returns>包含类型标签、摘要和可选正文的显示块。</returns>
    internal static ResultBlockViewModel BuildResultBlock(McpContent content)
    {
        switch (content)
        {
            case McpTextContent text:
                return new ResultBlockViewModel("文本", "text/plain", Truncate(text.Text), UiBrushes.Accent);

            case McpImageContent image:
                return new ResultBlockViewModel(
                    "图片",
                    $"{DisplayMime(image.MimeType)} · base64 {image.Data.Length} 字符（约 {(long)image.Data.Length * 3 / 4} 字节）",
                    string.Empty,
                    UiBrushes.Success);

            case McpAudioContent audio:
                return new ResultBlockViewModel(
                    "音频",
                    $"{DisplayMime(audio.MimeType)} · base64 {audio.Data.Length} 字符（约 {(long)audio.Data.Length * 3 / 4} 字节）",
                    string.Empty,
                    UiBrushes.Warning);

            case McpResourceContent resource:
                var resourceBody = !string.IsNullOrEmpty(resource.Text)
                    ? Truncate(resource.Text)
                    : !string.IsNullOrEmpty(resource.Blob)
                        ? $"blob: base64 {resource.Blob!.Length} 字符"
                        : string.Empty;
                return new ResultBlockViewModel(
                    "资源链接",
                    resource.Uri + (string.IsNullOrWhiteSpace(resource.MimeType) ? string.Empty : $" · {resource.MimeType}"),
                    resourceBody,
                    UiBrushes.Accent);

            case McpEmbeddedResourceContent embedded:
                var embeddedBody = !string.IsNullOrEmpty(embedded.Resource.Text)
                    ? Truncate(embedded.Resource.Text!)
                    : !string.IsNullOrEmpty(embedded.Resource.Blob)
                        ? $"blob: base64 {embedded.Resource.Blob!.Length} 字符"
                        : string.Empty;
                return new ResultBlockViewModel(
                    "嵌入资源",
                    embedded.Resource.Uri + (string.IsNullOrWhiteSpace(embedded.Resource.MimeType) ? string.Empty : $" · {embedded.Resource.MimeType}"),
                    embeddedBody,
                    UiBrushes.Accent);

            case McpUnknownContent unknown:
                var raw = unknown.Raw.ValueKind == JsonValueKind.Undefined
                    ? string.Empty
                    : Truncate(unknown.Raw.GetRawText(), 1000);
                return new ResultBlockViewModel(
                    "未知内容",
                    "类型: " + (unknown.Type ?? "(未提供)"),
                    raw,
                    UiBrushes.TextSecondary);

            default:
                return new ResultBlockViewModel(
                    "未知内容",
                    content.GetType().Name,
                    string.Empty,
                    UiBrushes.TextSecondary);
        }
    }

    /// <summary>生成描述工具结果的格式化 JSON 文档，优先使用服务器返回的原始负载。</summary>
    /// <param name="result">工具结果。</param>
    /// <param name="elapsedMs">经过的毫秒数。</param>
    /// <returns>缩进后的 JSON。</returns>
    internal static string BuildRawResultJson(McpCallToolResult result, long elapsedMs)
    {
        if (result.Raw is { } raw && raw.ValueKind == JsonValueKind.Object)
        {
            return PrettyJson(raw);
        }

        var content = new JsonArray();
        foreach (var block in result.Content)
        {
            content.Add(ContentToJson(block));
        }

        var node = new JsonObject
        {
            ["isError"] = result.IsError,
            ["elapsedMs"] = elapsedMs,
            ["text"] = result.GetText(),
            ["content"] = content
        };

        if (result.StructuredContent is { } structured
            && structured.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            node["structuredContent"] = JsonNode.Parse(structured.GetRawText());
        }

        return PrettyJson(node);
    }

    /// <summary>把单个 MCP 内容块转换为 JSON 表示形式。</summary>
    /// <param name="content">内容块。</param>
    /// <returns>保留类型专属字段的 JSON 节点。</returns>
    internal static JsonNode ContentToJson(McpContent content)
    {
        return content switch
        {
            McpTextContent text => new JsonObject { ["type"] = "text", ["text"] = text.Text },
            McpImageContent image => new JsonObject
            {
                ["type"] = "image",
                ["mimeType"] = image.MimeType,
                ["data"] = image.Data
            },
            McpAudioContent audio => new JsonObject
            {
                ["type"] = "audio",
                ["mimeType"] = audio.MimeType,
                ["data"] = audio.Data
            },
            McpResourceContent resource => new JsonObject
            {
                ["type"] = "resource_link",
                ["uri"] = resource.Uri,
                ["mimeType"] = resource.MimeType,
                ["text"] = resource.Text,
                ["blob"] = resource.Blob
            },
            McpEmbeddedResourceContent embedded => new JsonObject
            {
                ["type"] = "resource",
                ["resource"] = new JsonObject
                {
                    ["uri"] = embedded.Resource.Uri,
                    ["mimeType"] = embedded.Resource.MimeType,
                    ["text"] = embedded.Resource.Text,
                    ["blob"] = embedded.Resource.Blob
                }
            },
            McpUnknownContent unknown => new JsonObject
            {
                ["type"] = unknown.Type ?? "unknown",
                ["raw"] = unknown.Raw.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(unknown.Raw.GetRawText())
            },
            _ => new JsonObject { ["type"] = "unknown", ["dotnetType"] = content.GetType().Name }
        };
    }

    /// <summary>以简体中文格式化相对注册时间。</summary>
    /// <param name="utc">UTC 格式的注册时间戳。</param>
    /// <returns>诸如 <c>3 分钟前 · 2026-10-22 14:00:00</c> 的标签。</returns>
    internal static string FormatRegisteredAt(DateTime utc)
    {
        if (utc == default)
        {
            return "未知";
        }

        var universal = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        var elapsed = DateTime.UtcNow - universal;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var relative = elapsed.TotalMinutes < 1
            ? "刚刚"
            : elapsed.TotalMinutes < 60
                ? $"{(int)elapsed.TotalMinutes} 分钟前"
                : elapsed.TotalHours < 24
                    ? $"{(int)elapsed.TotalHours} 小时前"
                    : $"{(int)elapsed.TotalDays} 天前";

        return $"{relative} · {utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)}";
    }

    /// <summary>以适合状态栏的简洁形式描述异常。</summary>
    /// <param name="exception">要描述的异常。</param>
    /// <returns>友好易读的中文消息。</returns>
    internal static string DescribeError(Exception exception)
    {
        return exception switch
        {
            McpException mcp => $"MCP 错误 {mcp.Code}: {mcp.Message}",
            TimeoutException timeout => "请求超时：" + timeout.Message,
            OperationCanceledException => "操作已取消。",
            _ => exception.GetType().Name + ": " + exception.Message
        };
    }

    private static string DisplayMime(string mimeType)
    {
        return string.IsNullOrWhiteSpace(mimeType) ? "未知类型" : mimeType;
    }
}
