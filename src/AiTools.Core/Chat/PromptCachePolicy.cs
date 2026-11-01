using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AiTools.Core.Chat;

/// <summary>
/// 计费单价（每百万 token，美元）。只用于预估缓存预热的收益，
/// 不参与实际计费；未知单价时留空，预热器会按"经济性不可知"处理。
/// </summary>
public sealed class PromptCachePricing
{
    /// <summary>获取或设置普通输入 token 单价。</summary>
    public double InputPerMillion { get; set; }

    /// <summary>获取或设置命中缓存的输入 token 单价。</summary>
    public double CachedInputPerMillion { get; set; }

    /// <summary>
    /// 获取或设置写入缓存的输入 token 单价（仅供展示与外部评估）。
    /// </summary>
    /// <remarks>
    /// 预热器的经济性估算<b>不使用</b>该值：预热重放的语义是"缓存读"，会续期已有条目而不是新建，
    /// 因此 <c>warmCost</c> 只计 <see cref="CachedInputPerMillion"/> 加一个输出 token。
    /// 真正付写入溢价的是<b>真实请求</b>首次写缓存那一次，那笔开销已经发生，预热无法挽回。
    /// </remarks>
    public double CacheWritePerMillion { get; set; }

    /// <summary>获取或设置输出 token 单价。</summary>
    public double OutputPerMillion { get; set; }

    /// <summary>是否可用于经济性估算：输入、缓存读、输出三项单价都必须大于 0。</summary>
    public bool IsUsable
        => InputPerMillion > 0 && CachedInputPerMillion > 0 && OutputPerMillion > 0;
}

/// <summary>
/// 服务商的提示缓存能力。不同协议的缓存写入方式差异很大：
/// Anthropic 需要显式放置 <c>cache_control</c>，OpenAI 与 Gemini 由服务端自动前缀缓存。
/// </summary>
public enum PromptCacheKind
{
    /// <summary>服务商或模型不支持提示缓存。</summary>
    None,

    /// <summary>显式标记：请求中需要用 <c>cache_control</c> 之类的标记指明缓存边界（Anthropic）。</summary>
    Explicit,

    /// <summary>隐式前缀缓存：服务端自动缓存稳定前缀，客户端无需标记（OpenAI 兼容端点、Gemini）。</summary>
    Implicit
}

/// <summary>某个模型上的提示缓存策略。</summary>
public sealed class PromptCachePolicy
{
    /// <summary>获取缓存类型。</summary>
    public PromptCacheKind Kind { get; init; }

    /// <summary>获取缓存条目寿命；null 表示服务端不承诺寿命，预热无意义。</summary>
    public TimeSpan? Lifetime { get; init; }

    /// <summary>获取可选的计费单价；null 时预热器无法估算收益。</summary>
    public PromptCachePricing? Pricing { get; init; }

    /// <summary>
    /// 获取可缓存的最小提示长度（token）。低于该值时服务端不会创建缓存条目，
    /// 但也不会报错——预热只会白付一次请求。0 表示无已知下限。
    /// </summary>
    public int MinimumCacheableTokens { get; init; }

    /// <summary>
    /// 获取预热请求应使用的输出上限。Anthropic 支持 <c>max_tokens: 0</c>（零输出计费），
    /// 其他服务商至少需要 1 个 token。
    /// </summary>
    public int WarmOutputTokens { get; init; } = 1;

    /// <summary>获取是否值得为它安排预热：需要寿命与可用单价。</summary>
    public bool IsWarmable => Kind != PromptCacheKind.None && Lifetime.HasValue && Lifetime.Value > TimeSpan.Zero;

    /// <summary>不缓存的策略。</summary>
    public static PromptCachePolicy None { get; } = new() { Kind = PromptCacheKind.None };
}

/// <summary>
/// 按服务商协议与模型 id 解析提示缓存策略。表按模型 id 前缀匹配，最长前缀优先。
/// </summary>
/// <remarks>
/// 这里的寿命是服务商的<b>默认</b>承诺值，用于决定多久刷新一次：
/// Anthropic 的 5 分钟（短保留）与 1 小时（长保留）、OpenAI 兼容端点的 5–10 分钟最佳努力（best-effort）、
/// Gemini 隐式缓存的保守估计（服务端决定实际 TTL）。服务商可能提前淘汰缓存，预热只是提高命中概率。
/// </remarks>
public static class PromptCachePolicyCatalog
{
    /// <summary>OpenAI 前缀缓存的典型寿命（服务端最佳努力，通常 5–10 分钟；无承诺值）。</summary>
    public static readonly TimeSpan OpenAIInteractiveLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gemini 隐式缓存的寿命假设。官方不承诺隐式缓存的 TTL（由服务端决定并可能提前淘汰），
    /// 因此取比常见观察值更短的 1 小时，宁可多刷新一次，也不要长期等待一个已经不存在的缓存。
    /// </summary>
    public static readonly TimeSpan GeminiImplicitLifetime = TimeSpan.FromHours(1);

    /// <summary>Anthropic 短保留（5 分钟）的默认寿命。</summary>
    public static readonly TimeSpan AnthropicShortLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Anthropic 长保留（1 小时，需 <c>extended-cache-ttl</c> beta）的寿命。</summary>
    public static readonly TimeSpan AnthropicLongLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Anthropic 的最小可缓存长度（token）。官方文档给的是按模型分代的值，
    /// 这里取各代中的<b>最大值 4096</b>（Opus 4.6/4.5、Haiku 4.5）作为保守下限：
    /// 宁可少预热，也不要在没写进缓存的请求上白付费。
    /// </summary>
    /// <remarks>
    /// 各代实际值：Opus 5 / Fable 5 / Mythos 5 = 512；Opus 4.8 / Sonnet 5 / Sonnet 4.6 /
    /// Sonnet 4.5 / Opus 4.1 / Opus 4 = 1024；Opus 4.7 = 2048；Opus 4.6 / Opus 4.5 /
    /// Haiku 4.5 = 4096。要按模型精调时覆盖此值即可。
    /// </remarks>
    public const int AnthropicConservativeMinimumTokens = 4096;

    /// <summary>OpenAI 自动前缀缓存的最小长度（token）。</summary>
    public const int OpenAiMinimumTokens = 1024;

    /// <summary>Gemini 隐式缓存的最小长度（token）。</summary>
    public const int GeminiMinimumTokens = 1024;

    private static readonly PromptCachePricing OpenAiPricing = new()
    {
        InputPerMillion = 2.5,
        CachedInputPerMillion = 1.25,
        CacheWritePerMillion = 2.5,
        OutputPerMillion = 10
    };

    private static readonly PromptCachePricing AnthropicPricing = new()
    {
        InputPerMillion = 3,
        CachedInputPerMillion = 0.3,
        CacheWritePerMillion = 3.75,
        OutputPerMillion = 15
    };

    private static readonly PromptCachePricing GeminiPricing = new()
    {
        InputPerMillion = 1.25,
        CachedInputPerMillion = 0.31,
        CacheWritePerMillion = 1.25,
        OutputPerMillion = 10
    };

    /// <summary>
    /// 解析某个模型上的缓存策略。
    /// </summary>
    /// <param name="protocol">协议标识（<c>openai</c> / <c>anthropic</c> / <c>gemini</c>），大小写不敏感。</param>
    /// <param name="modelId">模型标识，可带 <c>provider/</c> 或 <c>models/</c> 前缀。</param>
    /// <param name="longRetention">
    /// 是否请求了长保留。仅 Anthropic 受影响：开启后寿命为 1 小时，预热排程据此推后，
    /// 否则会在缓存仍然有效时反复重写，白付写入溢价。
    /// </param>
    /// <returns>该模型上的缓存策略；不支持时返回 <see cref="PromptCachePolicy.None"/>。</returns>
    public static PromptCachePolicy Resolve(string? protocol, string? modelId, bool longRetention = false)
    {
        var normalized = NormalizeProtocol(protocol);
        var model = NormalizeModelId(modelId);
        if (normalized.Length == 0)
        {
            return PromptCachePolicy.None;
        }

        // 生成类模型走各自的图像/音频端点，不参与对话前缀缓存。
        if (IsNonChatModel(model))
        {
            return PromptCachePolicy.None;
        }

        switch (normalized)
        {
            case "anthropic":
                // Anthropic 的全部 Messages 模型都支持 cache_control；非 claude 前缀的兼容端点
                // 仍走 anthropic 协议，因此不按模型名过滤。
                return new PromptCachePolicy
                {
                    Kind = PromptCacheKind.Explicit,
                    Lifetime = longRetention ? AnthropicLongLifetime : AnthropicShortLifetime,
                    Pricing = AnthropicPricing,
                    MinimumCacheableTokens = AnthropicConservativeMinimumTokens,
                    // 官方推荐的预热写法：max_tokens 为 0 时不产生输出 token 计费。
                    WarmOutputTokens = 0
                };

            case "gemini":
                // Gemini 2.5+ 的隐式缓存对 generateContent 自动生效；1.5 也支持，
                // 但本地/代理网关可能不转发缓存指标，预热只是最佳努力。
                return new PromptCachePolicy
                {
                    Kind = PromptCacheKind.Implicit,
                    Lifetime = GeminiImplicitLifetime,
                    Pricing = GeminiPricing,
                    MinimumCacheableTokens = GeminiMinimumTokens
                };

            default:
                // OpenAI 及兼容端点：只有部分模型（gpt-4o 及以上、deepseek、kimi 等）自动前缀缓存。
                if (!SupportsOpenAiPrefixCache(model))
                {
                    return PromptCachePolicy.None;
                }

                return new PromptCachePolicy
                {
                    Kind = PromptCacheKind.Implicit,
                    Lifetime = OpenAIInteractiveLifetime,
                    Pricing = OpenAiPricing,
                    MinimumCacheableTokens = OpenAiMinimumTokens
                };
        }
    }

    private static bool SupportsOpenAiPrefixCache(string model)
    {
        if (model.Length == 0)
        {
            // 模型名未知时按不支持处理：宁可少预热，也不要为不缓存的服务付费。
            return false;
        }

        // 明确不缓存的模型族。
        if (model.StartsWith("o1-mini", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("ft:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var prefix in OpenAiCachePrefixes)
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] OpenAiCachePrefixes =
    {
        "gpt-4o",
        "gpt-4.1",
        "gpt-5",
        "o3",
        "o4",
        "deepseek",
        "kimi",
        "moonshot",
        "qwen",
        "glm",
        "gemini"
    };

    private static bool IsNonChatModel(string model)
    {
        if (model.Length == 0)
        {
            return false;
        }

        return model.StartsWith("text-embedding", StringComparison.OrdinalIgnoreCase) ||
               model.IndexOf("embedding", StringComparison.OrdinalIgnoreCase) >= 0 ||
               model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase) ||
               model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase) ||
               model.StartsWith("imagen", StringComparison.OrdinalIgnoreCase) ||
               model.StartsWith("tts-", StringComparison.OrdinalIgnoreCase) ||
               model.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) ||
               model.IndexOf("veo", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string NormalizeProtocol(string? protocol)
    {
        var value = (protocol ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        switch (value.ToLowerInvariant())
        {
            case "anthropic":
            case "claude":
                return "anthropic";
            case "gemini":
            case "google":
                return "gemini";
            case "openai":
            default:
                return "openai";
        }
    }

    private static string NormalizeModelId(string? modelId)
    {
        var value = (modelId ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var slash = value.LastIndexOf('/');
        if (slash >= 0 && slash < value.Length - 1)
        {
            value = value.Substring(slash + 1);
        }

        return value;
    }
}
