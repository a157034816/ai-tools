using System.Text.Json;

namespace AiTools.Anthropic;

/// <summary>
/// 用于处理 Anthropic 提示缓存（prompt caching）的辅助方法。
/// </summary>
/// <remarks>
/// 统一的 AiTools 内容模型不公开逐内容块的 <c>cache_control</c> 标记。
/// 此辅助方法返回该标记的结构，以便调用方在构建自定义负载时，
/// 通过服务商专属的请求参数放置它。
/// </remarks>
public static class AnthropicCacheControl
{
    private static readonly JsonElement EphemeralMarker = CreateEphemeralMarker();

    /// <summary>
    /// 创建适合用作 Anthropic <c>cache_control</c> 字段的全新 <c>{ "type": "ephemeral" }</c>
    /// JSON 元素。
    /// </summary>
    /// <returns>克隆的 JSON 元素；调用方可安全地保存它。</returns>
    public static JsonElement CreateEphemeral() => EphemeralMarker.Clone();

    private static JsonElement CreateEphemeralMarker()
    {
        using var document = JsonDocument.Parse("{\"type\":\"ephemeral\"}");
        return document.RootElement.Clone();
    }
}
