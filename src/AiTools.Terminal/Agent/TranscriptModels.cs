using System;
using System.Collections.Generic;
using System.Linq;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Core.Sessions;
using AiTools.Extensions;

namespace AiTools.Terminal.Agent;

/// <summary>一条对话消息及其在界面上的展示文本。</summary>
public sealed class TranscriptMessage
{
    /// <summary>创建条目。</summary>
    /// <param name="message">模型看到的消息。</param>
    /// <param name="displayText">气泡中显示的文本；null 表示显示 <paramref name="message"/> 的正文。</param>
    public TranscriptMessage(ChatMessage message, string? displayText = null)
    {
        Message = message;
        DisplayText = displayText;
    }

    /// <summary>获取模型看到的消息。</summary>
    public ChatMessage Message { get; }

    /// <summary>获取气泡中显示的文本。</summary>
    public string? DisplayText { get; }

    /// <summary>获取是否为用户消息。</summary>
    public bool IsUser => Message.Role == ChatRole.User;

    /// <inheritdoc />
    public override string ToString() => Message.GetText();
}

/// <summary>一次斜杠命令的结果。</summary>
public sealed class CommandOutcome
{
    /// <summary>获取结果种类。</summary>
    public CommandOutcomeKind Kind { get; init; }

    /// <summary>获取要发送给模型的提示词。</summary>
    public string? Prompt { get; init; }

    /// <summary>获取要直接显示的文本。</summary>
    public string? DisplayText { get; init; }

    /// <summary>获取错误信息。</summary>
    public string? Error { get; init; }

    /// <summary>获取触发本次结果的命令名（含 <c>/</c>）。</summary>
    public string CommandName { get; init; } = string.Empty;
}

/// <summary>命令结果种类。</summary>
public enum CommandOutcomeKind
{
    /// <summary>不是命令，按普通消息处理。</summary>
    NotACommand,

    /// <summary>发送提示词。</summary>
    Prompt,

    /// <summary>显示文本。</summary>
    Display,

    /// <summary>无动作。</summary>
    Noop,

    /// <summary>执行失败。</summary>
    Error
}

/// <summary>可选择的模型条目。</summary>
public sealed class ModelOption
{
    /// <summary>创建选项。</summary>
    /// <param name="provider">服务商。</param>
    /// <param name="model">模型。</param>
    public ModelOption(ProviderConfig provider, ModelConfig model)
    {
        Provider = provider;
        Model = model;
    }

    /// <summary>获取服务商。</summary>
    public ProviderConfig Provider { get; }

    /// <summary>获取模型。</summary>
    public ModelConfig Model { get; }

    /// <summary>获取显示文本，例如 <c>anthropic/claude-sonnet-4-5</c>。</summary>
    public string Label => (string.IsNullOrWhiteSpace(Provider.DisplayName) ? Provider.Id : Provider.DisplayName)
                           + "/" + Model.GetDisplayName();
}

/// <summary>
/// 会话在界面上的快照：标题、所选模型、消息数、状态。
/// </summary>
public sealed class SessionState
{
    /// <summary>获取会话标题。</summary>
    public string Title { get; init; } = "新对话";

    /// <summary>获取会话 ID；未保存时为 null。</summary>
    public string? ConversationId { get; init; }

    /// <summary>获取消息数量。</summary>
    public int MessageCount { get; init; }

    /// <summary>获取当前服务商。</summary>
    public ProviderConfig? Provider { get; init; }

    /// <summary>获取当前模型。</summary>
    public ModelConfig? Model { get; init; }

    /// <summary>获取当前输出风格名。</summary>
    public string? OutputStyle { get; init; }

    /// <summary>获取是否正在进行一轮请求。</summary>
    public bool IsBusy { get; init; }

    /// <summary>获取可用模型列表。</summary>
    public IReadOnlyList<ModelOption> Models { get; init; } = Array.Empty<ModelOption>();
}
