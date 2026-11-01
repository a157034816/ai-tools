using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AiTools.Anthropic;

/// <summary>
/// 配置 Anthropic 提示缓存（prompt caching）在请求中的写入点。
/// </summary>
/// <remarks>
/// <para>
/// Anthropic 的缓存是<b>前缀</b>缓存：<c>cache_control</c> 标记之前的所有内容都会被缓存。
/// 因此标记必须放在"稳定前缀的末尾"，否则每次请求都会写新的缓存条目、命中率为零。
/// </para>
/// <para>
/// 推荐顺序是 <b>工具 → 系统提示 → 最后一条消息</b>，这样工具定义与系统提示每轮都命中，
/// 消息历史也能在后续轮次命中。默认策略 <see cref="Automatic"/> 就按这个顺序放置。
/// </para>
/// </remarks>
public sealed class AnthropicPromptCachingOptions
{
    /// <summary>获取或设置是否请求长保留（1 小时）。需要 <c>extended-cache-ttl-2025-04-11</c> beta。</summary>
    public bool UseLongRetention { get; init; }

    /// <summary>获取或设置是否缓存工具定义。默认 true。</summary>
    public bool CacheTools { get; init; } = true;

    /// <summary>获取或设置是否缓存系统提示。默认 true。</summary>
    public bool CacheSystem { get; init; } = true;

    /// <summary>
    /// 获取或设置是否在消息列表末尾追加一个缓存点（滚动缓存对话历史）。默认 true。
    /// 关闭后只缓存工具与系统提示，适合历史每轮都大幅变化的场景。
    /// </summary>
    public bool CacheMessages { get; init; } = true;

    /// <summary>获取或设置滚动缓存点回退的消息数：0 表示最后一条消息，1 表示倒数第二条。默认 0。</summary>
    public int MessageCacheOffset { get; init; }

    /// <summary>自动放置缓存标记（工具 → 系统提示 → 末尾消息）的默认策略。</summary>
    /// <remarks>每次访问都返回新实例，避免调用方误以为可以共享同一份可变配置。</remarks>
    public static AnthropicPromptCachingOptions Automatic => new();
}
