using System;
using AiTools.Abstractions;

namespace AiTools.Core.Chat;

/// <summary>
/// 从 <see cref="Usage"/> 里还原"这次请求实际发送的提示 token 数"。
/// 三个协议的计数口径不同，直接用 <see cref="Usage.InputTokens"/> 会低估或重复计算。
/// </summary>
public static class PromptCacheAccounting
{
    /// <summary>
    /// 计算提示 token 总数。用于预热器的经济性估算与状态展示，不参与计费。
    /// </summary>
    /// <param name="providerName">服务商标识（区分 Anthropic 的拆分口径）。</param>
    /// <param name="usage">服务商返回的用量；null 时返回 0。</param>
    /// <remarks>
    /// <para>
    /// Anthropic 的 <c>input_tokens</c> <b>不含</b>缓存读写，总提示量是三者之和。
    /// </para>
    /// <para>
    /// OpenAI 与 Gemini 的 <c>InputTokens</c> 已经包含命中的缓存 token，
    /// 再加 <see cref="Usage.CacheReadInputTokens"/> 会重复计算，因此直接取输入总量。
    /// </para>
    /// </remarks>
    public static int GetPromptTokens(string? providerName, Usage? usage)
    {
        if (usage == null)
        {
            return 0;
        }

        if (IsAnthropic(providerName))
        {
            var anthropicTotal = (usage.InputTokens ?? 0)
                                 + (usage.CacheReadInputTokens ?? 0)
                                 + (usage.CacheCreationInputTokens ?? 0);
            if (anthropicTotal > 0)
            {
                return anthropicTotal;
            }
        }

        if (usage.InputTokens.HasValue && usage.InputTokens.Value > 0)
        {
            return usage.InputTokens.Value;
        }

        return usage.TotalTokens ?? 0;
    }

    /// <summary>判断是否 Anthropic 口径。</summary>
    public static bool IsAnthropic(string? providerName)
        => providerName != null &&
           providerName.StartsWith("anthropic", StringComparison.OrdinalIgnoreCase);

    /// <summary>缓存命中率；服务商未报告缓存字段时返回 null。</summary>
    /// <param name="providerName">服务商标识。</param>
    /// <param name="usage">服务商返回的用量。</param>
    public static double? GetCacheHitRate(string? providerName, Usage? usage)
    {
        if (usage?.CacheReadInputTokens is not int read || read <= 0)
        {
            return null;
        }

        var total = GetPromptTokens(providerName, usage);
        return total <= 0 ? null : (double)read / total;
    }
}
