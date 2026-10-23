using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using AiTools.Abstractions;
using AiTools.Core.Json;

namespace AiTools.OpenAI;

/// <summary>OpenAI 客户端共享的内部请求/响应辅助方法。</summary>
internal static class OpenAiHelpers
{
    /// <summary>根据配置的基地址构建绝对端点 URI。</summary>
    internal static Uri BuildUri(OpenAiOptions options, string path)
    {
        var baseAddress = options.BaseAddress ?? new Uri(OpenAiOptions.DefaultBaseAddress, UriKind.Absolute);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new InvalidOperationException(
                "OpenAiOptions.BaseAddress must be an absolute URI, for example https://api.openai.com/v1.");
        }

        var prefix = baseAddress.ToString().TrimEnd('/');
        return new Uri(prefix + "/" + path.TrimStart('/'), UriKind.Absolute);
    }

    /// <summary>创建已应用服务商标头的请求。</summary>
    internal static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        OpenAiOptions options,
        HttpContent? content)
    {
        var request = new HttpRequestMessage(method, BuildUri(options, path));
        if (content != null)
        {
            request.Content = content;
        }

        ApplyHeaders(request, options);
        return request;
    }

    /// <summary>将身份验证信息和已配置的标头应用到请求。</summary>
    internal static void ApplyHeaders(HttpRequestMessage request, OpenAiOptions options)
    {
        if (options.DefaultHeaders != null)
        {
            foreach (var header in options.DefaultHeaders)
            {
                if (string.IsNullOrEmpty(header.Key))
                {
                    continue;
                }

                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) && request.Content != null)
                {
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        if (!string.IsNullOrEmpty(options.AuthorizationHeader))
        {
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", options.AuthorizationHeader);
        }
        else if (!string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + options.ApiKey);
        }

        AddOptionalHeader(request, "OpenAI-Organization", options.Organization);
        AddOptionalHeader(request, "OpenAI-Project", options.Project);
        AddOptionalHeader(request, "OpenAI-User", options.User);
    }

    /// <summary>序列化请求体，并在其上浅合并服务商专属的顶层参数。</summary>
    internal static string MergeAdditionalParameters(object body, JsonElement? additionalParameters)
    {
        var json = JsonSerializer.Serialize(body, JsonHelpers.DefaultOptions);
        if (!additionalParameters.HasValue ||
            additionalParameters.Value.ValueKind != JsonValueKind.Object)
        {
            return json;
        }

        var additional = additionalParameters.Value;
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!additional.TryGetProperty(property.Name, out _))
                {
                    property.WriteTo(writer);
                }
            }

            foreach (var property in additional.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>读取数值属性，并接受兼容服务商返回的数字字符串。</summary>
    internal static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
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

    /// <summary>将 JSON 值转换为表单字段字符串。</summary>
    internal static string? StringifyJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText()
        };
    }

    /// <summary>将 OpenAI 用量对象映射为共享的 <see cref="Usage"/> 结构。</summary>
    internal static Usage? MapUsage(JsonElement? usage)
    {
        if (!usage.HasValue || usage.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var element = usage.Value;
        int? cacheRead = null;
        if (JsonHelpers.GetObject(element, "prompt_tokens_details") is { } promptDetails)
        {
            cacheRead = JsonHelpers.GetInt32(promptDetails, "cached_tokens");
        }

        int? reasoning = null;
        if (JsonHelpers.GetObject(element, "completion_tokens_details") is { } completionDetails)
        {
            reasoning = JsonHelpers.GetInt32(completionDetails, "reasoning_tokens");
        }

        var input = JsonHelpers.GetInt32(element, "prompt_tokens");
        var output = JsonHelpers.GetInt32(element, "completion_tokens");
        var total = JsonHelpers.GetInt32(element, "total_tokens");

        if (input == null && output == null && total == null && cacheRead == null && reasoning == null)
        {
            return null;
        }

        return new Usage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total,
            CacheReadInputTokens = cacheRead,
            ReasoningTokens = reasoning
        };
    }

    /// <summary>
    /// 创建一个取消源，将调用方 Token 与配置的请求超时合并。
    /// 调用方必须释放返回的取消源。
    /// </summary>
    internal static CancellationTokenSource CreateRequestCancellationSource(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(timeout);
        }

        return source;
    }

    private static void AddOptionalHeader(HttpRequestMessage request, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }
}
