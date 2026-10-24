using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>
/// 扩展注册的斜杠命令。与插件命令不同，命令由代码实现：
/// 它可以访问扩展状态、弹 UI、调用其它工具，或直接结束本轮而不调用模型。
/// </summary>
public sealed class ExtensionCommand
{
    /// <summary>获取命令名（不带 <c>/</c>），例如 <c>goal</c>。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取命令描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>获取参数提示。</summary>
    public string? ArgumentHint { get; init; }

    /// <summary>获取拥有该命令的扩展名。</summary>
    public string ExtensionName { get; init; } = string.Empty;

    /// <summary>获取处理函数。</summary>
    public Func<CommandContext, CommandArguments, CancellationToken, Task<CommandResult>> Handler { get; init; }
        = (_, _, _) => Task.FromResult(CommandResult.Noop);

    /// <inheritdoc />
    public override string ToString() => "/" + Name;
}

/// <summary>扩展注册的工具。<see cref="PluginCommand"/> 与扩展工具共用同一个模型可调用面。</summary>
public sealed class ExtensionTool
{
    /// <summary>获取工具定义（名称、描述、参数 Schema）。</summary>
    public ToolDefinition Definition { get; init; } = ToolDefinition.Create("tool");

    /// <summary>获取拥有该工具的扩展名。</summary>
    public string ExtensionName { get; init; } = string.Empty;

    /// <summary>获取曝光级别。</summary>
    public ToolExposure Exposure { get; init; } = ToolExposure.Direct;

    /// <summary>获取执行函数：参数为原始 JSON，返回结果文本。</summary>
    public Func<ToolCall, ToolContext, CancellationToken, Task<string>> Execute { get; init; }
        = (_, _, _) => Task.FromResult(string.Empty);

    /// <summary>获取只读提示（对应 MCP <c>readOnlyHint</c>）。</summary>
    public bool ReadOnlyHint { get; init; }

    /// <summary>获取所属命名空间名；null 表示不分组。查找时 <c>my-docs</c> 与 <c>my_docs</c> 等价。</summary>
    public string? Namespace { get; init; }

    /// <summary>获取工具可达范围（编排型工具用来声明一次调用能触达哪些工具）；null 表示未声明。</summary>
    public ToolReach? ReachesTools { get; init; }

    /// <inheritdoc />
    public override string ToString() => Definition.Name;
}

/// <summary>工具对模型的曝光方式。语义对齐 Pi。</summary>
/// <remarks>
/// “声明”指进入模型请求的工具清单；“可调用”指可被其它工具通过 <c>ctx.ExecuteToolAsync</c>
/// （或在 <c>codemode</c> 脚本中通过 <c>executeTool</c>）调用。
/// </remarks>
public enum ToolExposure
{
    /// <summary>登记即激活：声明给模型，也可被其它工具调用。</summary>
    Direct,

    /// <summary>声明给模型，但不可被其它工具调用（用于编排或提问型工具）。</summary>
    ModelOnly,

    /// <summary>
    /// 可被其它工具调用；只有本次脚本推导范围与配置的 <c>reachesTools</c>、父工具范围都允许时，
    /// 才列入 <c>codemode</c> 的可达工具清单。登记后不激活，除非显式激活，激活后即声明给模型。
    /// </summary>
    Codemode,

    /// <summary>
    /// 可被其它工具调用；登记后不激活，除非显式激活（例如 <c>tool_search</c>），
    /// 激活后即声明给模型。是否列入 codemode 的 <c>tools</c> 清单由本次脚本可达范围决定。
    /// </summary>
    Deferred,

    /// <summary>登记但不可达：既不声明给模型，也不可被其它工具调用。</summary>
    Hidden
}

/// <summary>命令执行结果。</summary>
public sealed class CommandResult
{
    private CommandResult(string? userMessage, CommandResultKind kind)
    {
        UserMessage = userMessage;
        Kind = kind;
    }

    /// <summary>获取结果的种类。</summary>
    public CommandResultKind Kind { get; }

    /// <summary>获取要发送给模型的用户消息；<see cref="CommandResultKind.Noop"/> 时为 null。</summary>
    public string? UserMessage { get; }

    /// <summary>不发送任何内容（命令已自行处理，例如只更新状态）。</summary>
    public static CommandResult Noop { get; } = new(null, CommandResultKind.Noop);

    /// <summary>把给定的提示词作为一条用户消息发送给模型。</summary>
    public static CommandResult Prompt(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("A prompt is required.", nameof(userMessage));
        }

        return new CommandResult(userMessage, CommandResultKind.Prompt);
    }

    /// <summary>只在对话中显示一条助手消息，不调用模型（例如 <c>/help</c>）。</summary>
    public static CommandResult Display(string assistantText) => new(assistantText, CommandResultKind.Display);
}

/// <summary><see cref="CommandResult"/> 的种类。</summary>
public enum CommandResultKind
{
    /// <summary>不产生任何动作。</summary>
    Noop,

    /// <summary>发送一条用户消息（触发模型轮次）。</summary>
    Prompt,

    /// <summary>直接显示助手文本，不调用模型。</summary>
    Display
}

/// <summary>扩展命令的上下文：可读状态、写状态、发消息、查询工具。</summary>
public sealed class CommandContext
{
    /// <summary>获取扩展运行时。</summary>
    public ExtensionRuntime Runtime { get; init; } = null!;

    /// <summary>获取会话状态存储。</summary>
    public ExtensionStateStore State { get; init; } = null!;

    /// <summary>获取当前会话 ID（尚未持久化的会话为 null）。</summary>
    public string? SessionId { get; init; }

    /// <summary>获取项目的会话消息快照（最旧在前）。</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = Array.Empty<ChatMessage>();

    /// <summary>获取当前工作目录。</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>获取自定义条目存储（对应 Pi 的 <c>appendEntry</c>）。</summary>
    public Func<string, object?, object?>? EntryStore { get; init; }

    /// <summary>获取宿主的交互界面；无 UI 时为 null。</summary>
    public IExtensionUi? Ui { get; init; }

    /// <summary>获取发送 UI 通知的回调（宿主可忽略）。</summary>
    public Action<string, ExtensionNoticeKind>? Notify { get; init; }

    /// <summary>获取向对话追加一条助手消息的回调。</summary>
    public Action<string>? AppendAssistantMessage { get; init; }

    /// <summary>发送 UI 通知。</summary>
    public void NotifyUser(string message, ExtensionNoticeKind kind = ExtensionNoticeKind.Info)
        => Notify?.Invoke(message, kind);

    /// <summary>追加一条不经过模型的助手消息到对话。</summary>
    public void AppendMessage(string text) => AppendAssistantMessage?.Invoke(text);

    /// <summary>
    /// 持久化一条不属于对话上下文的自定义条目（对应 Pi 的 <c>appendEntry</c>）。
    /// 条目存进扩展状态桶，不进入模型上下文。
    /// </summary>
    /// <param name="kind">条目类型（同一扩展内用于区分）。</param>
    /// <param name="value">任意可 JSON 序列化的值。</param>
    public void AppendEntry(string kind, object? value)
    {
        EntryStore?.Invoke(kind, value);
    }

    /// <summary>读取某类自定义条目的最新值。</summary>
    public object? ReadEntry(string kind) => EntryStore?.Invoke(kind, null);

    /// <summary>调用另一个已注册的工具（经 <c>tool_call</c>/<c>tool_result</c> 处理器）。</summary>
    public Task<string> ExecuteToolAsync(string name, string argumentsJson, CancellationToken cancellationToken = default)
        => Runtime.ExecuteToolAsync(name, argumentsJson, cancellationToken);

    /// <summary>获取可调用工具按命名空间的分组（对应 Pi 的 <c>ctx.tools</c> 列表）。</summary>
    public IReadOnlyList<ToolGroup> ToolGroups() => Runtime.ToolGroups();
}

/// <summary>工具执行上下文。</summary>
public sealed class ToolContext
{
    /// <summary>获取扩展运行时。</summary>
    public ExtensionRuntime Runtime { get; init; } = null!;

    /// <summary>获取会话状态存储。</summary>
    public ExtensionStateStore State { get; init; } = null!;

    /// <summary>获取发起调用的工具名（模型直接调用时为 null）。</summary>
    public string? ParentToolName { get; init; }

    /// <summary>获取当前会话 ID。</summary>
    public string? SessionId { get; init; }

    /// <summary>获取宿主的交互界面；无 UI 时为 null。</summary>
    public IExtensionUi? Ui { get; init; }

    /// <summary>获取自定义条目存储（对应 Pi 的 <c>appendEntry</c>）。</summary>
    public Func<string, object?, object?>? EntryStore { get; init; }

    /// <summary>获取发起本次调用的命令上下文；嵌套调用据此继承会话与可达范围。</summary>
    internal CommandContext? CallContext { get; init; }

    /// <summary>获取当前正在执行的工具名；嵌套调用的可达范围按它判定（null 表示非工具内调用）。</summary>
    internal string? CallerToolName { get; init; }

    /// <summary>持久化一条自定义条目。</summary>
    public void AppendEntry(string kind, object? value) => EntryStore?.Invoke(kind, value);

    /// <summary>调用另一个已注册的工具（走完整事件链，并受本工具声明的可达范围限制）。</summary>
    public async Task<string> ExecuteToolAsync(string name, string argumentsJson, CancellationToken cancellationToken = default)
    {
        var context = CallContext ?? new CommandContext
        {
            Runtime = Runtime,
            State = State,
            SessionId = SessionId,
            Ui = Ui,
            EntryStore = EntryStore,
            WorkingDirectory = Environment.CurrentDirectory
        };

        var result = await Runtime
            .ExecuteToolAsync(name, argumentsJson, context, CallerToolName, cancellationToken)
            .ConfigureAwait(false);

        return result.Text;
    }

    /// <summary>获取可调用工具按命名空间的分组（对应 Pi 的 <c>ctx.tools</c>）。</summary>
    public IReadOnlyList<ToolGroup> ToolGroups() => Runtime.ToolGroups();
}

/// <summary>UI 通知的严重级别。</summary>
public enum ExtensionNoticeKind
{
    /// <summary>中性信息。</summary>
    Info,

    /// <summary>成功。</summary>
    Success,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error
}
