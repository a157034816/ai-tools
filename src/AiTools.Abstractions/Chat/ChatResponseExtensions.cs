using System;
using System.Text.Json;

namespace AiTools.Abstractions;

/// <summary>响应解析的便捷扩展。</summary>
public static class ChatResponseExtensions
{
    /// <summary>
    /// 将助手回复的文本按 JSON 反序列化为指定类型。
    /// 适用于配合 <see cref="ResponseFormatKind.JsonSchema"/> / <see cref="ResponseFormatKind.JsonObject"/> 使用。
    /// </summary>
    /// <exception cref="JsonException">文本不是合法 JSON 或与目标类型不匹配。</exception>
    public static T? GetJson<T>(this ChatResponse response, JsonSerializerOptions? options = null)
    {
        if (response == null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var text = response.GetText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(text, options ?? CaseInsensitiveOptions);
    }

    /// <summary>尝试将助手回复解析为指定类型，失败时返回 false。</summary>
    public static bool TryGetJson<T>(this ChatResponse response, out T? value, JsonSerializerOptions? options = null)
    {
        try
        {
            value = response.GetJson<T>(options);
            return value != null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
