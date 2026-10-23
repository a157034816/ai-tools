using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiTools.Abstractions;

namespace AiTools.Core.Sessions;

/// <summary>一条聊天消息的可序列化快照。</summary>
public sealed class ConversationMessage
{
    /// <summary>获取或设置角色：<c>system</c>、<c>user</c>、<c>assistant</c> 或 <c>tool</c>。</summary>
    public string Role { get; set; } = "user";

    /// <summary>获取或设置纯文本内容。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>获取或设置可选的参与者名称。</summary>
    public string? Name { get; set; }

    /// <summary>获取或设置工具结果消息对应的工具调用标识。</summary>
    public string? ToolCallId { get; set; }

    /// <summary>
    /// 获取或设置气泡中显示的文本。斜杠命令展开后 <see cref="Text"/> 是完整提示词，
    /// 这里保存 <c>/goal</c> 这类简短命令名；为 null 时显示 <see cref="Text"/>。
    /// </summary>
    public string? DisplayText { get; set; }

    /// <summary>获取或设置消息创建时间（UTC）。</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>将抽象层消息转换为持久化消息。仅保留文本内容。</summary>
    public static ConversationMessage FromChatMessage(ChatMessage message, DateTimeOffset? timestamp = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return new ConversationMessage
        {
            Role = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool => "tool",
                _ => "user"
            },
            Text = message.GetText(),
            Name = message.Name,
            ToolCallId = message.ToolCallId,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow
        };
    }

    /// <summary>将持久化消息转换回抽象层消息。</summary>
    public ChatMessage ToChatMessage()
    {
        var text = Text ?? string.Empty;
        switch (Role)
        {
            case "system":
                return ChatMessage.System(text);
            case "assistant":
                return ChatMessage.Assistant(text);
            case "tool":
                return ToolCallId == null ? ChatMessage.User(text) : ChatMessage.ToolResult(ToolCallId, text);
            default:
                return ChatMessage.User(text);
        }
    }
}

/// <summary>一条持久化的对话。</summary>
public sealed class ConversationRecord
{
    /// <summary>获取或设置对话标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>获取或设置标题，通常由首条用户消息派生。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>获取或设置创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>获取或设置最后更新时间（UTC）。</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>获取或设置分叉来源的父对话标识。</summary>
    public string? ParentId { get; set; }

    /// <summary>获取或设置保存对话时选用的服务商。</summary>
    public string? ProviderId { get; set; }

    /// <summary>获取或设置保存对话时选用的模型。</summary>
    public string? ModelId { get; set; }

    /// <summary>获取或设置按时间顺序排列的消息。</summary>
    public List<ConversationMessage> Messages { get; set; } = new();

    /// <summary>
    /// 获取或设置扩展的会话状态（JSON 文本，按扩展名分桶）。该状态不进入模型上下文，
    /// 只用于恢复 /goal、/plan 这类跨轮次的簿记。
    /// </summary>
    public string? ExtensionState { get; set; }

    /// <summary>创建供选择器使用的摘要。</summary>
    public ConversationSummary ToSummary() => new()
    {
        Id = Id,
        Title = string.IsNullOrWhiteSpace(Title) ? "(未命名对话)" : Title,
        MessageCount = Messages.Count,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        ParentId = ParentId,
        ProviderId = ProviderId,
        ModelId = ModelId
    };

    /// <summary>创建带新标识与父级链接的深拷贝，供 <see cref="IConversationStore.ForkAsync"/> 使用。</summary>
    public ConversationRecord CreateFork(string? title = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title)
                ? (string.IsNullOrWhiteSpace(Title) ? "(分叉对话)" : Title + "（分叉）")
                : title!,
            CreatedAt = now,
            UpdatedAt = now,
            ParentId = Id,
            ProviderId = ProviderId,
            ModelId = ModelId,
            ExtensionState = ExtensionState,
            Messages = Messages.ConvertAll(message => new ConversationMessage
            {
                Role = message.Role,
                Text = message.Text,
                Name = message.Name,
                ToolCallId = message.ToolCallId,
                DisplayText = message.DisplayText,
                Timestamp = message.Timestamp
            })
        };
    }

    /// <summary>从首条用户消息派生标题。</summary>
    /// <param name="messages">用于检查的消息。</param>
    /// <param name="maxLength">截断前的最大字符数。默认为 40。</param>
    public static string DeriveTitle(IEnumerable<ConversationMessage> messages, int maxLength = 40)
    {
        foreach (var message in messages)
        {
            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(message.Text))
            {
                // 斜杠命令的正文是长提示词，标题应使用用户实际输入的命令名。
                var text = string.IsNullOrWhiteSpace(message.DisplayText) ? message.Text : message.DisplayText!;
                text = text.Trim().Replace("\r", " ").Replace("\n", " ");
                return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";
            }
        }

        return "(未命名对话)";
    }
}

/// <summary>对话持久化共享的 JSON 选项。</summary>
public static class ConversationJson
{
    /// <summary>获取对话存储使用的序列化选项。</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return options;
    }
}
