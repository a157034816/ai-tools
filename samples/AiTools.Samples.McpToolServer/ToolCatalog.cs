using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiTools.Abstractions;

namespace AiTools.Samples.McpToolServer;

/// <summary>当工具拒绝其参数或无法完成请求时引发。</summary>
internal sealed class ToolExecutionException : Exception
{
    /// <summary>使用面向用户的消息创建工具级失败。</summary>
    /// <param name="message">失败描述。</param>
    internal ToolExecutionException(string message)
        : base(message)
    {
    }
}

/// <summary>MCP 服务器暴露的一个工具：面向 AI 的定义及其本地处理程序。</summary>
internal sealed class McpServerTool
{
    /// <summary>创建一个工具。</summary>
    /// <param name="definition">面向 AI 的工具定义，包含其参数的 JSON Schema。</param>
    /// <param name="title">用于 MCP 注解的简短、便于阅读的标题。</param>
    /// <param name="idempotent">使用相同参数重复调用是否会产生相同结果。</param>
    /// <param name="handler">返回文本形式工具结果的同步处理程序。</param>
    internal McpServerTool(ToolDefinition definition, string title, bool idempotent, Func<JsonElement, string> handler)
    {
        Definition = definition;
        Title = title;
        Idempotent = idempotent;
        Handler = handler;
    }

    /// <summary>获取面向 AI 的工具定义；这是 MCP 目录的唯一事实来源。</summary>
    internal ToolDefinition Definition { get; }

    /// <summary>获取简短的显示标题。</summary>
    internal string Title { get; }

    /// <summary>获取使用相同参数重复调用是否无副作用并产生相同结果。</summary>
    internal bool Idempotent { get; }

    /// <summary>获取本地处理程序。</summary>
    internal Func<JsonElement, string> Handler { get; }
}

/// <summary>
/// 服务器的工具目录。每个工具都声明为 <see cref="ToolDefinition"/>（名称、描述与 JSON
/// Schema），并被转换为 MCP <c>tools/list</c> 条目，从而让 AI 可识别的定义保持权威。
/// </summary>
internal static class ToolCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly System.Text.RegularExpressions.Regex WordRegex = new(
        @"[\p{L}\p{N}_]+(?:['’\-][\p{L}\p{N}_]+)*",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>获取按向客户端公布顺序排列的所有工具。</summary>
    internal static IReadOnlyList<McpServerTool> Tools { get; } = new[]
    {
        new McpServerTool(
            ToolDefinition.FromJsonSchema(
                "echo",
                "原样返回输入的文本，适合验证 MCP 连接与工具调用链路。",
                """
                {"type":"object","properties":{"text":{"type":"string","description":"要回显的文本"}},"required":["text"],"additionalProperties":false}
                """),
            "回显文本",
            idempotent: true,
            Echo),

        new McpServerTool(
            ToolDefinition.FromJsonSchema(
                "get_time",
                "获取当前时间，可指定 IANA 时区（例如 Asia/Shanghai）；同时返回 UTC 时间与本地显示格式。",
                """
                {"type":"object","properties":{"timezone":{"type":"string","description":"IANA 时区名称，例如 Asia/Shanghai；省略时使用服务器本地时区"}},"additionalProperties":false}
                """),
            "获取当前时间",
            idempotent: false,
            GetTime),

        new McpServerTool(
            ToolDefinition.FromJsonSchema(
                "calculate",
                "执行一次算术运算，支持 + - * / %（取余）和 ^（乘方）。",
                """
                {"type":"object","properties":{"a":{"type":"number","description":"左操作数"},"op":{"type":"string","enum":["+","-","*","/","%","^"],"description":"运算符：+ - * / % ^（^ 为乘方）"},"b":{"type":"number","description":"右操作数"}},"required":["a","op","b"],"additionalProperties":false}
                """),
            "算术计算",
            idempotent: true,
            Calculate),

        new McpServerTool(
            ToolDefinition.FromJsonSchema(
                "text_stats",
                "统计文本的字符数、单词数、行数，并找出最长单词。",
                """
                {"type":"object","properties":{"text":{"type":"string","description":"要统计的文本，可为空字符串"}},"required":["text"],"additionalProperties":false}
                """),
            "文本统计",
            idempotent: true,
            TextStats),

        new McpServerTool(
            ToolDefinition.FromJsonSchema(
                "random_number",
                "生成指定闭区间内的随机整数；提供 seed 时可复现相同结果。",
                """
                {"type":"object","properties":{"min":{"type":"integer","description":"最小值（含）"},"max":{"type":"integer","description":"最大值（含）"},"seed":{"type":"integer","description":"可选随机种子；相同种子产生相同结果"}},"required":["min","max"],"additionalProperties":false}
                """),
            "生成随机整数",
            idempotent: false,
            RandomNumber),

        new McpServerTool(
            ToolDefinition.FromJsonSchema(
                "hash_text",
                "计算文本的 SHA-256、SHA-1 或 MD5 十六进制摘要。",
                """
                {"type":"object","properties":{"text":{"type":"string","description":"要计算哈希的文本（按 UTF-8 编码）"},"algorithm":{"type":"string","enum":["sha256","sha1","md5"],"description":"哈希算法，默认 sha256"}},"required":["text"],"additionalProperties":false}
                """),
            "计算文本哈希",
            idempotent: true,
            HashText),

        new McpServerTool(
            ToolDefinition.FromJsonSchema(
                "base64",
                "对文本做 Base64 编码或解码（UTF-8）。",
                """
                {"type":"object","properties":{"text":{"type":"string","description":"要编码的文本，或要解码的 Base64 字符串"},"operation":{"type":"string","enum":["encode","decode"],"description":"encode 将文本编码为 Base64；decode 将 Base64 解码为文本。默认 encode"}},"required":["text"],"additionalProperties":false}
                """),
            "Base64 编解码",
            idempotent: true,
            Base64),

        new McpServerTool(
            ToolDefinition.FromJsonSchema(
                "uuid",
                "生成一个或多个 UUID（v4）。",
                """
                {"type":"object","properties":{"count":{"type":"integer","minimum":1,"maximum":100,"description":"生成数量，默认 1，最大 100"}},"additionalProperties":false}
                """),
            "生成 UUID",
            idempotent: false,
            Uuid)
    };

    /// <summary>获取所有已暴露工具的名称。</summary>
    internal static IEnumerable<string> ToolNames
    {
        get
        {
            foreach (var tool in Tools)
            {
                yield return tool.Definition.Name;
            }
        }
    }

    /// <summary>按精确名称查找工具。</summary>
    /// <param name="name">工具名称。</param>
    /// <returns>匹配的工具；没有匹配时返回 null。</returns>
    internal static McpServerTool? Find(string name)
    {
        foreach (var tool in Tools)
        {
            if (string.Equals(tool.Definition.Name, name, StringComparison.Ordinal))
            {
                return tool;
            }
        }

        return null;
    }

    /// <summary>按名称执行工具。</summary>
    /// <param name="name">工具名称。</param>
    /// <param name="arguments">客户端提供的参数对象。</param>
    /// <returns>文本结果，通常为 JSON。</returns>
    /// <exception cref="ToolExecutionException">工具未知或拒绝其参数。</exception>
    internal static string Invoke(string name, JsonElement arguments)
    {
        var tool = Find(name);
        if (tool == null)
        {
            throw new ToolExecutionException(
                "未知工具 '" + name + "'。可用工具：" + string.Join("、", ToolNames) + "。");
        }

        return tool.Handler(arguments);
    }

    /// <summary>将 MCP <c>tools/list</c> 条目序列化为 JSON 数组。</summary>
    /// <param name="indented">是否格式化输出。</param>
    /// <returns>MCP 工具对象的 JSON 数组。</returns>
    internal static string BuildToolsJson(bool indented = false)
    {
        return McpJson.WriteJson(
            writer =>
            {
                writer.WriteStartArray();
                foreach (var tool in Tools)
                {
                    WriteTool(writer, tool);
                }

                writer.WriteEndArray();
            },
            indented);
    }

    /// <summary>写入一个 MCP 工具对象，包括由 <see cref="ToolDefinition.ParametersSchema"/> 派生的 JSON Schema。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="tool">要写入的工具。</param>
    internal static void WriteTool(Utf8JsonWriter writer, McpServerTool tool)
    {
        writer.WriteStartObject();
        writer.WriteString("name", tool.Definition.Name);
        if (tool.Definition.Description != null)
        {
            writer.WriteString("description", tool.Definition.Description);
        }

        writer.WritePropertyName("inputSchema");
        if (tool.Definition.ParametersSchema is { } schema)
        {
            schema.WriteTo(writer);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteEndObject();
        }

        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteString("title", tool.Title);
        writer.WriteBoolean("readOnlyHint", true);
        writer.WriteBoolean("destructiveHint", false);
        writer.WriteBoolean("idempotentHint", tool.Idempotent);
        writer.WriteBoolean("openWorldHint", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string Echo(JsonElement arguments)
    {
        var text = ToolArguments.GetRequiredString(arguments, "text");
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["text"] = text }, JsonOptions);
    }

    private static string GetTime(JsonElement arguments)
    {
        var timezoneName = ToolArguments.GetOptionalString(arguments, "timezone");
        var timezone = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(timezoneName))
        {
            try
            {
                timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneName.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
                throw new ToolExecutionException(
                    "无法识别时区 '" + timezoneName + "'。请使用 IANA 时区名称，例如 Asia/Shanghai、Europe/London 或 UTC。");
            }
            catch (InvalidTimeZoneException)
            {
                throw new ToolExecutionException("时区数据无效：" + timezoneName + "。");
            }
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var now = TimeZoneInfo.ConvertTime(nowUtc, timezone);
        var payload = new Dictionary<string, object?>
        {
            ["timezone"] = timezone.Id,
            ["display"] = now.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture),
            ["iso8601"] = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["utc"] = nowUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["utcOffsetHours"] = now.Offset.TotalHours,
            ["isDaylightSavingTime"] = timezone.IsDaylightSavingTime(now)
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string Calculate(JsonElement arguments)
    {
        var left = ToolArguments.GetRequiredDecimal(arguments, "a");
        var right = ToolArguments.GetRequiredDecimal(arguments, "b");
        var operation = ToolArguments.GetRequiredString(arguments, "op").Trim();

        object result;
        try
        {
            result = operation switch
            {
                "+" => left + right,
                "-" => left - right,
                "*" => left * right,
                "/" => right == 0 ? throw new ToolExecutionException("除数不能为 0。") : left / right,
                "%" => right == 0 ? throw new ToolExecutionException("取余运算的除数不能为 0。") : left % right,
                "^" => Power(left, right),
                _ => throw new ToolExecutionException("不支持的运算符 '" + operation + "'。请使用 + - * / % ^。")
            };
        }
        catch (OverflowException)
        {
            throw new ToolExecutionException("计算结果超出数值范围。");
        }

        var payload = new Dictionary<string, object?>
        {
            ["expression"] = FormatDecimal(left) + " " + operation + " " + FormatDecimal(right),
            ["result"] = result
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static double Power(decimal left, decimal right)
    {
        var value = Math.Pow((double)left, (double)right);
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ToolExecutionException("乘方结果不是有限数值，请检查操作数。");
        }

        return value;
    }

    private static string FormatDecimal(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string TextStats(JsonElement arguments)
    {
        var text = ToolArguments.GetRequiredString(arguments, "text");

        var lines = CountLines(text);
        var charactersWithoutWhitespace = 0;
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                charactersWithoutWhitespace++;
            }
        }

        var matches = WordRegex.Matches(text);
        string? longestWord = null;
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (longestWord == null || match.Length > longestWord.Length)
            {
                longestWord = match.Value;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["characters"] = text.Length,
            ["charactersWithoutWhitespace"] = charactersWithoutWhitespace,
            ["words"] = matches.Count,
            ["lines"] = lines,
            ["longestWord"] = longestWord,
            ["longestWordLength"] = longestWord?.Length ?? 0
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var lines = 1;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\n')
            {
                lines++;
            }
            else if (character == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n'))
            {
                lines++;
            }
        }

        return lines;
    }

    private static string RandomNumber(JsonElement arguments)
    {
        var min = ToolArguments.GetRequiredInt32(arguments, "min", int.MinValue, int.MaxValue);
        var max = ToolArguments.GetRequiredInt32(arguments, "max", int.MinValue, int.MaxValue);
        if (min > max)
        {
            throw new ToolExecutionException("参数 min 不能大于 max。");
        }

        var seed = ToolArguments.GetOptionalInt64(arguments, "seed");
        var random = seed.HasValue ? new Random(unchecked((int)seed.Value)) : Random.Shared;
        var value = random.NextInt64(min, (long)max + 1);

        var payload = new Dictionary<string, object?>
        {
            ["min"] = min,
            ["max"] = max,
            ["value"] = value,
            ["seeded"] = seed.HasValue
        };

        if (seed.HasValue)
        {
            payload["seed"] = seed.Value;
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string HashText(JsonElement arguments)
    {
        var text = ToolArguments.GetRequiredString(arguments, "text");
        var algorithm = (ToolArguments.GetOptionalString(arguments, "algorithm") ?? "sha256").Trim().ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(text);

        byte[] digest;
        string canonicalName;
        switch (algorithm.Replace("-", string.Empty).Replace("_", string.Empty))
        {
            case "sha256":
                digest = SHA256.HashData(bytes);
                canonicalName = "SHA-256";
                break;

            case "sha1":
                digest = SHA1.HashData(bytes);
                canonicalName = "SHA-1";
                break;

            case "md5":
                digest = MD5.HashData(bytes);
                canonicalName = "MD5";
                break;

            default:
                throw new ToolExecutionException(
                    "不支持的哈希算法 '" + algorithm + "'。可选值：sha256、sha1、md5。");
        }

        var payload = new Dictionary<string, object?>
        {
            ["algorithm"] = canonicalName,
            ["hash"] = Convert.ToHexString(digest).ToLowerInvariant(),
            ["byteLength"] = bytes.Length
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string Base64(JsonElement arguments)
    {
        var text = ToolArguments.GetRequiredString(arguments, "text");
        var operation = (ToolArguments.GetOptionalString(arguments, "operation") ?? "encode").Trim().ToLowerInvariant();

        if (operation == "encode")
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var payload = new Dictionary<string, object?>
            {
                ["operation"] = "encode",
                ["result"] = Convert.ToBase64String(bytes),
                ["byteLength"] = bytes.Length
            };

            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        if (operation == "decode")
        {
            try
            {
                var bytes = Convert.FromBase64String(text.Trim());
                var decoded = StrictUtf8.GetString(bytes);
                var payload = new Dictionary<string, object?>
                {
                    ["operation"] = "decode",
                    ["result"] = decoded,
                    ["byteLength"] = bytes.Length
                };

                return JsonSerializer.Serialize(payload, JsonOptions);
            }
            catch (FormatException)
            {
                throw new ToolExecutionException("输入不是有效的 Base64 字符串。");
            }
            catch (DecoderFallbackException)
            {
                throw new ToolExecutionException("Base64 解码后的字节不是有效的 UTF-8 文本。");
            }
        }

        throw new ToolExecutionException("不支持的 Base64 操作 '" + operation + "'。请使用 encode 或 decode。");
    }

    private static string Uuid(JsonElement arguments)
    {
        var count = ToolArguments.GetOptionalInt32(arguments, "count", 1, 1, 100);

        var values = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            values.Add(Guid.NewGuid().ToString("D"));
        }

        var payload = new Dictionary<string, object?>
        {
            ["count"] = count,
            ["uuids"] = values
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}

/// <summary>用于从 JSON 对象读取工具参数的防御性辅助方法。</summary>
internal static class ToolArguments
{
    /// <summary>读取必填字符串参数。</summary>
    /// <param name="arguments">参数对象。</param>
    /// <param name="name">参数名称。</param>
    /// <returns>字符串值；可能为空字符串。</returns>
    internal static string GetRequiredString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new ToolExecutionException("缺少必填参数 '" + name + "'。");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ToolExecutionException("参数 '" + name + "' 必须是字符串。");
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>读取可选字符串参数。</summary>
    /// <param name="arguments">参数对象。</param>
    /// <param name="name">参数名称。</param>
    /// <returns>字符串值；缺省时返回 null。</returns>
    internal static string? GetOptionalString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ToolExecutionException("参数 '" + name + "' 必须是字符串。");
        }

        return value.GetString();
    }

    /// <summary>读取闭区间内的必填整数参数。</summary>
    /// <param name="arguments">参数对象。</param>
    /// <param name="name">参数名称。</param>
    /// <param name="min">允许的最小值。</param>
    /// <param name="max">允许的最大值。</param>
    /// <returns>解析后的整数。</returns>
    internal static int GetRequiredInt32(JsonElement arguments, string name, int min, int max)
    {
        var value = GetRequiredInt64(arguments, name);
        if (value < min || value > max)
        {
            throw new ToolExecutionException(
                "参数 '" + name + "' 必须在 " + min + " 到 " + max + " 之间。");
        }

        return (int)value;
    }

    /// <summary>读取闭区间内的可选整数参数。</summary>
    /// <param name="arguments">参数对象。</param>
    /// <param name="name">参数名称。</param>
    /// <param name="defaultValue">参数缺省时使用的值。</param>
    /// <param name="min">允许的最小值。</param>
    /// <param name="max">允许的最大值。</param>
    /// <returns>解析后的整数或默认值。</returns>
    internal static int GetOptionalInt32(JsonElement arguments, string name, int defaultValue, int min, int max)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return defaultValue;
        }

        return GetRequiredInt32(arguments, name, min, max);
    }

    /// <summary>读取必填的 64 位整数参数。</summary>
    /// <param name="arguments">参数对象。</param>
    /// <param name="name">参数名称。</param>
    /// <returns>解析后的整数。</returns>
    internal static long GetRequiredInt64(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new ToolExecutionException("缺少必填参数 '" + name + "'。");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw new ToolExecutionException("参数 '" + name + "' 必须是整数。");
    }

    /// <summary>读取可选的 64 位整数参数。</summary>
    /// <param name="arguments">参数对象。</param>
    /// <param name="name">参数名称。</param>
    /// <returns>解析后的整数；缺省时返回 null。</returns>
    internal static long? GetOptionalInt64(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return GetRequiredInt64(arguments, name);
    }

    /// <summary>读取必填的 decimal 参数，接受数字或固定区域性的数字字符串。</summary>
    /// <param name="arguments">参数对象。</param>
    /// <param name="name">参数名称。</param>
    /// <returns>解析后的 decimal。</returns>
    internal static decimal GetRequiredDecimal(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new ToolExecutionException("缺少必填参数 '" + name + "'。");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw new ToolExecutionException("参数 '" + name + "' 必须是数字。");
    }
}
