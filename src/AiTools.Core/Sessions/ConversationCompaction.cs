using System;

namespace AiTools.Core.Sessions;

/// <summary>
/// 一条持久化的上下文压缩状态：前 <see cref="CoveredMessageCount"/> 条消息已被摘要取代，
/// 发送请求时按该状态折叠，消息本体仍完整保留在 <see cref="ConversationRecord.Messages"/>。
/// </summary>
/// <remarks>
/// 该状态与会话一起保存，<c>/resume</c> 与 <c>/fork</c> 后依然生效；继续压缩时在现有
/// 摘要之上滚动合并，<see cref="CoveredMessageCount"/> 指向新的切分点。
/// </remarks>
public sealed class ConversationCompaction
{
    /// <summary>获取或设置摘要正文。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>获取或设置摘要覆盖的消息数（从对话开头计）。</summary>
    public int CoveredMessageCount { get; set; }

    /// <summary>获取或设置本次压缩生成时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>获取或设置压缩前的估算 token 数。</summary>
    public int OriginalTokenCount { get; set; }

    /// <summary>获取或设置压缩后的估算 token 数。</summary>
    public int CompactedTokenCount { get; set; }

    /// <summary>摘要覆盖的消息数是否仍然有效（不超过消息总数）。</summary>
    /// <param name="messageCount">当前消息总数。</param>
    public bool IsValidFor(int messageCount)
        => !string.IsNullOrWhiteSpace(Summary) && CoveredMessageCount > 0 && CoveredMessageCount <= messageCount;
}
