using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using AiTools.Abstractions;
using AiTools.Core.Http;

namespace AiTools.Gemini.Internal;

/// <summary>用于 Gemini 请求构建和响应检查的共享辅助方法。</summary>
internal static class GeminiInfrastructure
{
    internal const string ProviderName = "gemini";

    internal const string DefaultMimeType = "application/octet-stream";

    internal static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    /// <summary>去除模型名中可选的 <c>models/</c> 前缀及首尾斜杠。</summary>
    internal static string NormalizeModel(string? model)
    {
        var value = (model ?? string.Empty).Trim();
        while (value.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring("models/".Length);
        }

        return value.Trim('/');
    }

    /// <summary>为提供的模型构建完整的 generateContent 风格端点 URI。</summary>
    internal static Uri BuildUri(GeminiOptions options, string model, string method, bool stream)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var normalizedModel = NormalizeModel(model);
        if (normalizedModel.Length == 0)
        {
            throw new ArgumentException("A model name is required.", nameof(model));
        }

        var baseAddress = options.BaseAddress ?? GeminiOptions.DefaultBaseAddress;
        var baseText = baseAddress.IsAbsoluteUri ? baseAddress.AbsoluteUri : baseAddress.OriginalString;

        string? existingQuery = null;
        var fragmentIndex = baseText.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            baseText = baseText.Substring(0, fragmentIndex);
        }

        var queryIndex = baseText.IndexOf('?');
        if (queryIndex >= 0)
        {
            existingQuery = baseText.Substring(queryIndex + 1);
            baseText = baseText.Substring(0, queryIndex);
        }

        baseText = baseText.TrimEnd('/');

        var rooted = baseText.StartsWith("/", StringComparison.Ordinal);
        var version = (options.ApiVersion ?? string.Empty).Trim().Trim('/');
        if (version.Length > 0)
        {
            var slashIndex = baseText.LastIndexOf('/');
            var lastSegment = slashIndex >= 0 ? baseText.Substring(slashIndex + 1) : baseText;
            if (LooksLikeVersionSegment(lastSegment))
            {
                baseText = slashIndex >= 0 ? baseText.Substring(0, slashIndex) : string.Empty;
            }

            if (baseText.Length == 0)
            {
                baseText = (rooted ? "/" : string.Empty) + version;
            }
            else
            {
                baseText = baseText + "/" + version;
            }
        }

        var path = (baseText.Length == 0 ? string.Empty : baseText + "/") +
                   "models/" + normalizedModel + ":" + method;

        var query = new List<string>(3);
        if (!string.IsNullOrEmpty(existingQuery))
        {
            query.Add(existingQuery!);
        }

        if (stream)
        {
            query.Add("alt=sse");
        }

        var apiKey = options.ApiKey;
        if (options.UseQueryStringApiKey && !string.IsNullOrEmpty(apiKey))
        {
            query.Add("key=" + Uri.EscapeDataString(apiKey));
        }

        if (query.Count > 0)
        {
            path += "?" + string.Join("&", query);
        }

        // 优先使用绝对 URI，使没有 BaseAddress 的注入 HttpClient 也能正常工作。
        return Uri.TryCreate(path, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(path, UriKind.RelativeOrAbsolute);
    }

    private static bool LooksLikeVersionSegment(string segment)
    {
        return segment.Length >= 2 &&
               (segment[0] == 'v' || segment[0] == 'V') &&
               char.IsDigit(segment[1]);
    }

    /// <summary>创建带 Gemini 认证请求头的 HTTP 请求。</summary>
    internal static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        GeminiOptions options,
        string? body,
        bool stream)
    {
        var request = new HttpRequestMessage(method, uri);
        if (body != null)
        {
            request.Content = AiContent.Json(body);
        }

        request.Headers.TryAddWithoutValidation("Accept", stream ? "text/event-stream" : "application/json");

        if (!string.IsNullOrEmpty(options.AuthorizationHeader))
        {
            request.Headers.TryAddWithoutValidation("Authorization", options.AuthorizationHeader);
        }
        else if (!options.UseQueryStringApiKey && !string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", options.ApiKey);
        }

        if (options.DefaultHeaders != null)
        {
            foreach (var header in options.DefaultHeaders)
            {
                if (!request.Headers.Contains(header.Key))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return request;
    }

    /// <summary>创建用于强制实施 <see cref="AiHttpOptions.Timeout"/> 的链接取消源。</summary>
    internal static CancellationTokenSource? CreateTimeoutSource(GeminiOptions options, CancellationToken cancellationToken)
    {
        var timeout = options.Timeout;
        if (timeout <= TimeSpan.Zero || timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        // 如果客户端由调用方提供，HttpClient.Timeout 也会覆盖响应流式传输，
        // 因此改为在此处强制执行整体截止时间。
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    /// <summary>创建一个由调用方拥有的专用 HttpClient，其配置与共享服务商客户端相同。</summary>
    internal static HttpClient CreateDedicatedHttpClient(GeminiOptions options)
    {
#if NETSTANDARD2_0
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            UseCookies = false
        };
#else
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
#endif
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        if (options.BaseAddress != null)
        {
            client.BaseAddress = options.BaseAddress;
        }

        return client;
    }

    /// <summary>运行 JSON 写入回调并返回生成的 UTF-8 JSON 字符串。</summary>
    internal static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>按 Gemini 的格式写入一个内容部分。无法映射时返回 false。</summary>
    internal static bool TryWriteContentPart(Utf8JsonWriter writer, ContentPart part)
    {
        if (part == null)
        {
            return false;
        }

        if (part is TextPart text)
        {
            if (text.Text.Length == 0)
            {
                // Gemini 会拒绝空的文本部分；跳过它们，而不是让整个请求失败。
                return false;
            }

            writer.WriteStartObject();
            writer.WriteString("text", text.Text);
            writer.WriteEndObject();
            return true;
        }

        if (part is ImagePart image)
        {
            return TryWriteBinaryPart(writer, image.Source);
        }

        if (part is FilePart file)
        {
            return TryWriteBinaryPart(writer, file.Source);
        }

        if (part is AudioPart audio)
        {
            return TryWriteBinaryPart(writer, audio.Source);
        }

        return false;
    }

    private static bool TryWriteBinaryPart(Utf8JsonWriter writer, BinarySource? source)
    {
        if (source == null)
        {
            return false;
        }

        if (source.Data != null)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("inlineData");
            writer.WriteStartObject();
            writer.WriteString("mimeType", source.MediaType ?? DefaultMimeType);
            writer.WriteString("data", Convert.ToBase64String(source.Data));
            writer.WriteEndObject();
            writer.WriteEndObject();
            return true;
        }

        if (source.Uri != null)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("fileData");
            writer.WriteStartObject();
            writer.WriteString(
                "mimeType",
                string.IsNullOrEmpty(source.MediaType) ? InferMediaType(source.Uri) : source.MediaType);
            writer.WriteString("fileUri", source.Uri.IsAbsoluteUri ? source.Uri.AbsoluteUri : source.Uri.OriginalString);
            writer.WriteEndObject();
            writer.WriteEndObject();
            return true;
        }

        return false;
    }

    private static string InferMediaType(Uri uri)
    {
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".png":
                return "image/png";
            case ".jpg":
            case ".jpeg":
                return "image/jpeg";
            case ".webp":
                return "image/webp";
            case ".gif":
                return "image/gif";
            case ".pdf":
                return "application/pdf";
            case ".mp3":
                return "audio/mp3";
            case ".wav":
                return "audio/wav";
            case ".mp4":
                return "video/mp4";
            case ".txt":
                return "text/plain";
            case ".json":
                return "application/json";
            default:
                return "application/octet-stream";
        }
    }

    /// <summary>
    /// 将 <paramref name="additionalParameters"/> 浅合并到序列化的 JSON 对象中。
    /// 调用方提供的属性会替换同名的内置属性；其他属性保持不变。非对象的附加参数会被忽略。
    /// </summary>
    internal static string MergeTopLevel(string json, JsonElement? additionalParameters)
    {
        if (additionalParameters is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return json;
        }

        using var document = JsonDocument.Parse(json);
        return WriteJson(writer =>
        {
            writer.WriteStartObject();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (element.TryGetProperty(property.Name, out _))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            foreach (var property in element.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        });
    }

    internal static string? ReadString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    internal static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    internal static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    internal static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    /// <summary>
    /// 按 camelCase 名称查找属性，并回退到 snake_case 以兼容代理。
    /// </summary>
    internal static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        var snake = ToSnakeCase(propertyName);
        return !string.Equals(snake, propertyName, StringComparison.Ordinal) &&
               element.TryGetProperty(snake, out value);
    }

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>解析 <c>usageMetadata</c>（若存在）；字段缺失时返回 null。</summary>
    internal static Usage? ParseUsage(JsonElement root)
    {
        if (!TryGetProperty(root, "usageMetadata", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = ReadInt(usage, "promptTokenCount");
        var output = ReadInt(usage, "candidatesTokenCount");
        var total = ReadInt(usage, "totalTokenCount");
        var cached = ReadInt(usage, "cachedContentTokenCount");
        var reasoning = ReadInt(usage, "thoughtsTokenCount");

        if (input == null && output == null && total == null && cached == null && reasoning == null)
        {
            return null;
        }

        return new Usage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total,
            CacheReadInputTokens = cached,
            ReasoningTokens = reasoning
        };
    }

    /// <summary>返回响应中的第一个对象候选结果；没有则返回 null。</summary>
    internal static JsonElement? GetFirstCandidate(JsonElement root)
    {
        if (!TryGetProperty(root, "candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.ValueKind == JsonValueKind.Object)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>当 Prompt 被拦截时返回 <c>promptFeedback.blockReason</c>。</summary>
    internal static string? GetBlockReason(JsonElement root)
    {
        if (!TryGetProperty(root, "promptFeedback", out var feedback) ||
            feedback.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var reason = ReadString(feedback, "blockReason");
        return string.IsNullOrEmpty(reason) ? null : reason;
    }

    /// <summary>将 Gemini 的结束原因字符串映射到统一枚举。</summary>
    internal static ChatFinishReason MapFinishReason(string? finishReason)
    {
        if (string.IsNullOrWhiteSpace(finishReason))
        {
            return ChatFinishReason.None;
        }

        switch (finishReason!.Trim().ToUpperInvariant())
        {
            case "STOP":
                return ChatFinishReason.Stop;
            case "MAX_TOKENS":
                return ChatFinishReason.Length;
            case "SAFETY":
            case "RECITATION":
            case "BLOCKLIST":
            case "PROHIBITED_CONTENT":
            case "SPII":
                return ChatFinishReason.ContentFilter;
            case "TOOL_CALLS":
            case "FUNCTION_CALL":
                return ChatFinishReason.ToolCalls;
            case "FINISH_REASON_UNSPECIFIED":
                return ChatFinishReason.None;
            case "MALFORMED_FUNCTION_CALL":
            case "OTHER":
            default:
                return ChatFinishReason.Other;
        }
    }

    /// <summary>创建携带 Gemini thought signature 的服务商数据负载。</summary>
    internal static JsonElement CreateThoughtSignatureData(string signature)
    {
        var json = WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("thoughtSignature", signature);
            writer.WriteEndObject();
        });

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>尝试解析 JSON 文档；输入为 null、空或格式错误时返回 null。</summary>
    internal static JsonDocument? TryParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(value!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>尝试解码标准 base64 字符串；解码失败时返回 null。</summary>
    internal static byte[]? TryDecodeBase64(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value!);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>为无效的 JSON 负载抛出统一的服务异常。</summary>
    internal static AiServiceException InvalidJson(string body, JsonException? innerException = null)
    {
        return new AiServiceException(
            "Gemini returned an invalid JSON response.",
            ProviderName,
            responseBody: body,
            innerException: innerException);
    }
}
