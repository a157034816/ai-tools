using System;
using System.Collections.Generic;
using AiTools.Abstractions;

namespace AiTools.Terminal.Agent;

/// <summary>会话向界面推送的更新种类。</summary>
public enum AgentUpdateKind
{
    /// <summary>一轮开始（助手气泡已创建）。</summary>
    TurnStarted,

    /// <summary>正文增量。</summary>
    TextDelta,

    /// <summary>推理过程增量。</summary>
    ReasoningDelta,

    /// <summary>工具开始执行。</summary>
    ToolStarted,

    /// <summary>工具执行结束。</summary>
    ToolFinished,

    /// <summary>一轮正常结束。</summary>
    TurnCompleted,

    /// <summary>一轮失败或被取消。</summary>
    TurnFailed,

    /// <summary>更新状态栏文本。</summary>
    Status,

    /// <summary>显示一条通知（不进入模型上下文）。</summary>
    Notice,

    /// <summary>直接显示一段助手文本（命令输出，不调用模型）。</summary>
    DisplayText,

    /// <summary>会话元数据变化（标题、模型、消息数）。</summary>
    StateChanged
}

/// <summary>一条会话更新。界面按 <see cref="Kind"/> 解释其余字段。</summary>
public sealed class AgentUpdate
{
    /// <summary>获取更新种类。</summary>
    public AgentUpdateKind Kind { get; init; }

    /// <summary>获取正文/推理增量、通知文本或显示文本。</summary>
    public string? Text { get; init; }

    /// <summary>获取工具调用。</summary>
    public ToolCall? ToolCall { get; init; }

    /// <summary>获取工具结果文本。</summary>
    public string? ToolResult { get; init; }

    /// <summary>获取工具执行耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>获取工具是否失败。</summary>
    public bool IsError { get; init; }

    /// <summary>获取通知严重级别。</summary>
    public NoticeKind NoticeKind { get; init; }

    /// <summary>获取本轮用量。</summary>
    public Usage? Usage { get; init; }

    /// <summary>获取错误信息。</summary>
    public string? Error { get; init; }

    /// <summary>获取附带的对象（例如 <see cref="TurnResult"/>）。</summary>
    public object? Payload { get; init; }

    /// <summary>创建正文增量。</summary>
    public static AgentUpdate TextDelta(string text) => new() { Kind = AgentUpdateKind.TextDelta, Text = text };

    /// <summary>创建推理增量。</summary>
    public static AgentUpdate ReasoningDelta(string text) => new() { Kind = AgentUpdateKind.ReasoningDelta, Text = text };
}

/// <summary>一轮请求的结果。</summary>
public sealed class TurnResult
{
    /// <summary>获取助手文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>获取推理文本。</summary>
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>获取用量。</summary>
    public Usage? Usage { get; init; }

    /// <summary>获取工具调用次数。</summary>
    public int ToolCallCount { get; init; }

    /// <summary>获取耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>获取结束原因。</summary>
    public ChatFinishReason FinishReason { get; init; }

    /// <summary>获取错误信息；成功时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>获取是否被用户取消。</summary>
    public bool Cancelled { get; init; }

    /// <summary>获取是否成功完成。</summary>
    public bool Succeeded => Error == null && !Cancelled;
}
