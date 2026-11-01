using System;

namespace AiTools.Configuration;

/// <summary>
/// 提示缓存（prompt caching）的使用策略。三个内置协议都会读取它：
/// Anthropic 据此写入 <c>cache_control</c>，OpenAI 与 Gemini 用来决定是否声明
/// "前缀稳定"以便服务端命中自动缓存，并决定预热器的行为。
/// </summary>
/// <remarks>
/// 关闭后客户端不发送任何缓存标记，预热器也停止工作；用量统计中的缓存读写字段仍然照常解析。
/// </remarks>
public sealed class PromptCacheOptions
{
    /// <summary>
    /// 获取或设置是否启用提示缓存。默认 true。
    /// 关闭后 Anthropic 不再发送 <c>cache_control</c>，预热也不会进行。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 获取或设置是否启用缓存预热。默认 true（仅在轮内预热）。
    /// 与 <see cref="Enabled"/> 独立：可以只写缓存不预热，也可以只预热已有缓存。
    /// </summary>
    public bool WarmingEnabled { get; set; } = true;

    /// <summary>
    /// 获取或设置 Anthropic 是否请求 1 小时长保留（<c>ttl: "1h"</c>）。
    /// 默认 false，使用 5 分钟短保留。长保留的写入单价更高，适合长时间停顿时仍希望命中的场景。
    /// </summary>
    public bool LongRetention { get; set; }

    /// <summary>
    /// 获取或设置 Anthropic 是否缓存工具定义。默认 true。
    /// 工具定义通常是请求中最稳定的部分，命中率最高。
    /// </summary>
    public bool CacheTools { get; set; } = true;

    /// <summary>
    /// 获取或设置 Anthropic 是否缓存系统提示。默认 true。
    /// 系统提示由宿主与扩展拼装，轮与轮之间通常不变。
    /// </summary>
    public bool CacheSystem { get; set; } = true;

    /// <summary>
    /// 获取或设置 Anthropic 是否在消息末尾追加滚动缓存点。默认 true。
    /// 关闭后只缓存工具与系统提示，适合历史每轮都大幅变化的场景。
    /// </summary>
    public bool CacheMessages { get; set; } = true;

    /// <summary>获取或设置滚动缓存点回退的消息数：0 表示最后一条消息。默认 0。</summary>
    public int MessageCacheOffset { get; set; }

    /// <summary>创建深拷贝。</summary>
    public PromptCacheOptions Clone() => new()
    {
        Enabled = Enabled,
        WarmingEnabled = WarmingEnabled,
        LongRetention = LongRetention,
        CacheTools = CacheTools,
        CacheSystem = CacheSystem,
        CacheMessages = CacheMessages,
        MessageCacheOffset = MessageCacheOffset
    };
}
