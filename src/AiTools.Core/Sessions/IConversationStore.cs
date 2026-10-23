using System;
using System.Collections.Generic;

namespace AiTools.Core.Sessions;

/// <summary>供恢复/分叉选择器使用的持久化对话摘要。</summary>
public sealed class ConversationSummary
{
    /// <summary>获取对话标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>获取对话标题，通常为首条用户消息。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>获取消息数量。</summary>
    public int MessageCount { get; init; }

    /// <summary>获取创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>获取最后更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>获取分叉来源的父对话标识；若并非分叉则为 null。</summary>
    public string? ParentId { get; init; }

    /// <summary>获取对话最近一次保存时选用的服务商标识。</summary>
    public string? ProviderId { get; init; }

    /// <summary>获取对话最近一次保存时选用的模型标识。</summary>
    public string? ModelId { get; init; }

    /// <summary>获取简短的时间标签，例如 <c>刚刚</c> 或 <c>3 小时前</c>。</summary>
    public string GetRelativeTime()
    {
        var delta = DateTimeOffset.UtcNow - UpdatedAt;
        if (delta.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours} 小时前";
        }

        if (delta.TotalDays < 30)
        {
            return $"{(int)delta.TotalDays} 天前";
        }

        return UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd");
    }
}

/// <summary>持久化对话历史，以便后续恢复或分叉。</summary>
public interface IConversationStore
{
    /// <summary>列出对话，最近更新的排在前面。</summary>
    System.Threading.Tasks.Task<IReadOnlyList<ConversationSummary>> ListAsync(System.Threading.CancellationToken cancellationToken = default);

    /// <summary>按标识加载对话；不存在时返回 null。</summary>
    System.Threading.Tasks.Task<ConversationRecord?> LoadAsync(string id, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>保存（创建或替换）对话。</summary>
    System.Threading.Tasks.Task SaveAsync(ConversationRecord record, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>删除对话。</summary>
    System.Threading.Tasks.Task DeleteAsync(string id, System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建一个新对话，复制 <paramref name="source"/> 的消息并将其记录为父对话。
    /// 新对话会获得全新的标识与时间戳；源对话保持不变。
    /// </summary>
    System.Threading.Tasks.Task<ConversationRecord> ForkAsync(ConversationRecord source, string? title = null, System.Threading.CancellationToken cancellationToken = default);
}
