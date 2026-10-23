using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>扩展可订阅的事件。语义对齐 Pi：有的只通知，有的可改写，有的可拦截。</summary>
public enum ExtensionEvent
{
    /// <summary>会话开始。扩展在此登记工具/命令之外的长生命周期资源。</summary>
    SessionStart,

    /// <summary>会话关闭（切换、重载或进程退出前）。处理器必须幂等。</summary>
    SessionShutdown,

    /// <summary>用户输入到达。可改写或吞掉（返回非 null 表示已处理）。</summary>
    Input,

    /// <summary>模型请求发出前。可追加提示词段落与消息。</summary>
    BeforeAgentStart,

    /// <summary>一次模型轮次结束后。可请求追加一轮。</summary>
    TurnEnd,

    /// <summary>工具调用即将执行。可修改参数或拦截。</summary>
    ToolCall,

    /// <summary>工具返回结果后。可改写结果文本。</summary>
    ToolResult
}

/// <summary>用户输入事件。返回非 null 表示已消费，宿主不再继续处理。</summary>
public sealed class InputEvent
{
    /// <summary>获取或设置原始输入。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>获取或设置阻止本次输入并不再处理。</summary>
    public bool Suppress { get; set; }
}

/// <summary>模型请求前的可变上下文。处理器就地修改，后续处理器看到修改后的值。</summary>
public sealed class BeforeAgentStartEvent
{
    /// <summary>获取插件命令系统提示（可追加）。</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>获取或设置要追加的额外段落（按序合并）。</summary>
    public List<string> PromptSections { get; } = new();

    /// <summary>获取或设置额外消息，插入在本轮用户消息之前。</summary>
    public List<ChatMessage> PrependMessages { get; } = new();

    /// <summary>获取模型名。</summary>
    public string Model { get; init; } = string.Empty;
}

/// <summary>工具调用事件。设置 <see cref="BlockReason"/> 即阻止执行。</summary>
public sealed class ToolCallEvent
{
    /// <summary>获取工具名。</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>获取或设置参数 JSON（可改写）。</summary>
    public string ArgumentsJson { get; set; } = "{}";

    /// <summary>获取或设置拦截原因；非空即视为阻止。</summary>
    public string? BlockReason { get; set; }

    /// <summary>阻止本次调用。</summary>
    public void Block(string reason) => BlockReason = reason;
}

/// <summary>工具结果事件。可就地改写结果文本。</summary>
public sealed class ToolResultEvent
{
    /// <summary>获取工具名。</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>获取或设置结果文本（可改写，例如脱敏或截断）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>获取工具是否报告了错误。</summary>
    public bool IsError { get; init; }
}

/// <summary>一轮模型交互结束的事件。</summary>
public sealed class TurnEndEvent
{
    /// <summary>获取本轮助手文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>获取本轮工具调用次数。</summary>
    public int ToolCallCount { get; init; }

    /// <summary>获取或设置是否要求再追加一轮（扩展可给出后续指令）。</summary>
    public bool Continue { get; set; }

    /// <summary>获取或设置追加轮次时使用的用户消息。</summary>
    public string? ContinuationPrompt { get; set; }
}

/// <summary>会话开始事件。</summary>
public sealed class SessionStartEvent
{
    /// <summary>获取会话 ID。</summary>
    public string? SessionId { get; init; }

    /// <summary>获取工作目录。</summary>
    public string WorkingDirectory { get; init; } = string.Empty;
}

/// <summary>一个可编程扩展：由宿主注册到 <see cref="ExtensionRuntime"/>。</summary>
public interface IExtension
{
    /// <summary>获取扩展标识（同时用作状态桶名）。</summary>
    string Name { get; }

    /// <summary>获取扩展描述。</summary>
    string? Description { get; }

    /// <summary>在运行时登记命令、工具与事件处理器。此方法必须快速返回且不启动进程。</summary>
    /// <param name="api">注册接口。</param>
    void Activate(IExtensionApi api);
}

/// <summary>扩展在 <see cref="IExtension.Activate"/> 中使用的注册接口。</summary>
public interface IExtensionApi
{
    /// <summary>获取扩展名。</summary>
    string Name { get; }

    /// <summary>获取扩展的会话状态存储。</summary>
    ExtensionStateStore State { get; }

    /// <summary>注册斜杠命令（同名命令后注册者覆盖先注册者）。</summary>
    void RegisterCommand(string name, string description, Func<CommandContext, CommandArguments, CancellationToken, Task<CommandResult>> handler, string? argumentHint = null);

    /// <summary>注册模型可调用的工具（同名工具后注册者覆盖先注册者）。</summary>
    void RegisterTool(ToolDefinition definition, Func<ToolCall, ToolContext, CancellationToken, Task<string>> execute, ToolExposure exposure = ToolExposure.Direct, bool readOnlyHint = false);

    /// <summary>订阅事件。返回取消订阅的函数。</summary>
    Action On(ExtensionEvent @event, Func<object, CommandContext, CancellationToken, Task> handler);

    /// <summary>订阅事件并使用强类型负载；处理器返回非 null 时替换事件对象（用于转换类事件）。</summary>
    Action On<TEvent>(ExtensionEvent @event, Func<TEvent, CommandContext, CancellationToken, Task<TEvent?>> handler)
        where TEvent : class;
}

internal sealed class EventSubscription
{
    public EventSubscription(string extensionName, ExtensionEvent @event, Func<object, CommandContext, CancellationToken, Task<object?>> handler)
    {
        ExtensionName = extensionName;
        Event = @event;
        Handler = handler;
    }

    public string ExtensionName { get; }

    public ExtensionEvent Event { get; }

    /// <summary>返回非 null 时替换事件对象。</summary>
    public Func<object, CommandContext, CancellationToken, Task<object?>> Handler { get; }
}
