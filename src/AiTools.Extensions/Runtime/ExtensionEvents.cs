using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>扩展可订阅的事件。语义对齐 Pi 与 Claude Code/Codex 的 hook 时机。</summary>
/// <remarks>
/// 事件分三类：
/// <list type="bullet">
/// <item><b>通知</b>：处理器异常被吞掉，不打断对话（<see cref="SessionStart"/>、<see cref="AgentStart"/>、<see cref="TurnEnd"/> 等）。</item>
/// <item><b>转换</b>：处理器按注册顺序链式改写事件对象（<see cref="Context"/>、<see cref="ToolResult"/>、<see cref="MessageEnd"/>）。</item>
/// <item><b>拦截</b>：处理器可阻止操作，失败时按 fail-closed 处理（<see cref="ToolCall"/>、<see cref="PermissionRequest"/>、<see cref="BeforeProviderRequest"/>）。</item>
/// </list>
/// </remarks>
public enum ExtensionEvent
{
    /// <summary>会话开始。扩展在此登记工具/命令之外的长生命周期资源。</summary>
    SessionStart,

    /// <summary>会话关闭（切换、重载或进程退出前）。处理器必须幂等。</summary>
    SessionShutdown,

    /// <summary>
    /// 会话即将被替换：<c>/new</c>、<c>/resume</c>、<c>/fork</c>、<c>/clear</c>。
    /// 可取消迁移；<see cref="SessionBeforeSwitchEvent.Reason"/> 与 <see cref="SessionBeforeSwitchEvent.TargetSessionId"/> 说明去向。
    /// </summary>
    SessionBeforeSwitch,

    /// <summary>会话替换完成（状态已导入、会话 ID 已更新）。用于重建分支相关的扩展状态。</summary>
    SessionSwitched,

    /// <summary>用户输入到达。可改写或吞掉（返回非 null 表示已处理）。</summary>
    Input,

    /// <summary>共享指令（<c>.agents</c> 等）即将并入系统提示。可改写或丢弃条目。</summary>
    InstructionsLoaded,

    /// <summary>模型请求发出前。可追加提示词段落与消息。</summary>
    BeforeAgentStart,

    /// <summary>一次 agent 循环开始（本轮首次请求前）。只通知。</summary>
    AgentStart,

    /// <summary>一次模型轮次开始。只通知。</summary>
    TurnStart,

    /// <summary>一条消息定稿后（用户、助手或工具结果）。可改写正文。</summary>
    MessageEnd,

    /// <summary>一次模型轮次结束后。可请求追加一轮。</summary>
    TurnEnd,

    /// <summary>一次 agent 循环正常运行结束（未被取消、未抛异常）。只通知。</summary>
    AgentEnd,

    /// <summary>一次 agent 循环因取消或异常结束。只通知；<see cref="AgentFailedEvent.Error"/> 区分原因。</summary>
    AgentFailed,

    /// <summary>工具调用即将执行。可修改参数或拦截。</summary>
    ToolCall,

    /// <summary>
    /// 工具调用需要授权判定时（策略给出 <see cref="PermissionDecision.Ask"/>）。
    /// 处理器可把结论改写为 Allow/Deny 或补充提示，未改写时由宿主询问用户。
    /// </summary>
    PermissionRequest,

    /// <summary>工具返回结果后。可改写结果文本。</summary>
    ToolResult,

    /// <summary>工具执行失败后（抛异常或返回错误）。可观察失败原因，不影响结果。</summary>
    ToolError,

    /// <summary>工具开始执行（只通知，用于渲染与计时）。</summary>
    ToolExecutionStart,

    /// <summary>工具执行过程中的增量输出（只通知）。</summary>
    ToolExecutionUpdate,

    /// <summary>工具执行结束（只通知）。</summary>
    ToolExecutionEnd,

    /// <summary>构造 provider 请求之前。可改写消息列表（对应 Pi 的 <c>context</c>）。</summary>
    Context,

    /// <summary>允许扩展同时改写系统消息的上下文钩子（对应 Pi 的 <c>context_with_system</c>）。</summary>
    ContextWithSystem,

    /// <summary>provider 请求头构造前。可就地改写请求头。</summary>
    BeforeProviderHeaders,

    /// <summary>provider 请求体序列化后、发出前。可改写 JSON 负载（对应 Pi 的 <c>before_provider_request</c>）。</summary>
    BeforeProviderRequest,

    /// <summary>provider 响应解析后（对应 Pi 的 <c>after_provider_response</c>）。</summary>
    AfterProviderResponse,

    /// <summary>provider 流中的每个事件（只通知，对应 Pi 的 <c>provider_stream_event</c>）。</summary>
    ProviderStreamEvent,

    /// <summary>
    /// 上下文即将被压缩（对应 Claude Code 的 <c>PreCompact</c>）。
    /// 处理器可取消压缩，或直接提供摘要跳过模型调用。
    /// </summary>
    PreCompact,

    /// <summary>上下文压缩完成后（对应 Claude Code 的 <c>PostCompact</c>）。只通知。</summary>
    PostCompact,

    /// <summary>上下文压缩失败后。可观察失败原因，不影响对话继续。</summary>
    CompactFailed,

    /// <summary>
    /// 提示缓存即将刷新前（对应 Pi 的 <c>cache_warming_decision</c>）。
    /// 处理器可把 <see cref="CacheWarmingDecisionEvent.Action"/> 改写为 <c>warm</c> 或 <c>stop</c>，
    /// 最后一个处理器给出的结论生效。
    /// </summary>
    CacheWarmingDecision
}

/// <summary>用户输入事件。返回非 null 表示已消费，宿主不再继续处理。</summary>
public sealed class InputEvent
{
    /// <summary>获取或设置原始输入。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>获取或设置阻止本次输入并不再处理。</summary>
    public bool Suppress { get; set; }
}

/// <summary>共享指令并入系统提示前的事件。可编辑、禁用条目或改写最终提示。</summary>
/// <remarks>对应 Claude Code 的 <c>InstructionsLoaded</c> 与 Codex 的 AGENTS.md 加载时机。</remarks>
public sealed class InstructionsEvent
{
    /// <summary>获取或设置每条指令的来源标签（例如 <c>agents</c>、<c>skill</c>）。</summary>
    public IList<InstructionsSection> Sections { get; } = new List<InstructionsSection>();

    /// <summary>
    /// 获取或设置替换后的完整提示；null 表示按 <see cref="Sections"/> 拼接。
    /// 设置后 <see cref="Sections"/> 不再参与拼接，但仍对处理器可见。
    /// </summary>
    public string? OverridePrompt { get; set; }

    /// <summary>按当前配置拼出最终提示：设置了 <see cref="OverridePrompt"/> 时直接返回它。</summary>
    /// <returns>并入系统提示的文本；全部条目被丢弃时为空串。</returns>
    public string Build()
    {
        if (OverridePrompt != null)
        {
            return OverridePrompt;
        }

        var enabled = Sections.Where(section => section.Enabled && !string.IsNullOrWhiteSpace(section.Text)).ToArray();
        if (enabled.Length == 0)
        {
            return string.Empty;
        }

        return string.Join("\n\n", enabled.Select(section => section.Text!.TrimEnd()));
    }
}

/// <summary>一条待并入系统提示的指令。</summary>
public sealed class InstructionsSection
{
    /// <summary>获取来源标签（例如 <c>agents</c>、<c>skill</c>、<c>project</c>）。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>获取或设置指令正文。</summary>
    public string? Text { get; set; }

    /// <summary>获取或设置是否参与拼接；设为 false 等同于丢弃该条指令。</summary>
    public bool Enabled { get; set; } = true;
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

/// <summary>agent 循环开始/轮次开始时的事件。</summary>
public sealed class AgentLifecycleEvent
{
    /// <summary>获取模型名（循环开始时为空串）。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>获取本轮序号（从 0 开始；<see cref="ExtensionEvent.AgentStart"/> 恒为 -1）。</summary>
    public int TurnIndex { get; init; } = -1;
}

/// <summary>agent 循环异常结束时的事件。</summary>
public sealed class AgentFailedEvent
{
    /// <summary>获取失败原因，例如 <c>cancelled</c> 或异常消息。</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>获取是否因用户取消而结束。</summary>
    public bool Cancelled { get; init; }

    /// <summary>获取本轮已产出的部分文本。</summary>
    public string PartialText { get; init; } = string.Empty;

    /// <summary>获取已完成的工具调用次数。</summary>
    public int ToolCallCount { get; init; }
}

/// <summary>一条消息定稿时的事件。可就地改写正文。</summary>
public sealed class MessageEvent
{
    /// <summary>获取消息角色（<c>user</c>、<c>assistant</c>、<c>tool</c>）。</summary>
    public ChatRole Role { get; init; }

    /// <summary>获取或设置消息正文（可改写，例如补全、脱敏或标注）。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>获取是否为助手消息。</summary>
    public bool IsAssistant => Role == ChatRole.Assistant;
}

/// <summary>会话替换前的事件。设置 <see cref="CancelReason"/> 即取消本次迁移。</summary>
public sealed class SessionBeforeSwitchEvent
{
    /// <summary>获取迁移原因：<c>new</c>、<c>resume</c>、<c>fork</c> 或 <c>clear</c>。</summary>
    public string Reason { get; init; } = "new";

    /// <summary>获取目标会话 ID；<c>new</c> / <c>clear</c> 为 null。</summary>
    public string? TargetSessionId { get; init; }

    /// <summary>获取或设置取消原因；非空即阻止迁移。</summary>
    public string? CancelReason { get; set; }

    /// <summary>阻止本次会话迁移。</summary>
    /// <param name="reason">展示给用户的原因。</param>
    public void Cancel(string reason) => CancelReason = reason;
}

/// <summary>会话替换完成后的事件。</summary>
public sealed class SessionSwitchedEvent
{
    /// <summary>获取迁移原因：<c>new</c>、<c>resume</c>、<c>fork</c> 或 <c>clear</c>。</summary>
    public string Reason { get; init; } = "new";

    /// <summary>获取上一个会话 ID；之前没有持久化会话时为 null。</summary>
    public string? PreviousSessionId { get; init; }

    /// <summary>获取新的会话 ID；<c>new</c> / <c>clear</c> 为 null。</summary>
    public string? SessionId { get; init; }
}

/// <summary>上下文压缩前的事件。设置 <see cref="CancelReason"/> 即取消；设置 <see cref="Summary"/> 即跳过模型调用。</summary>
/// <remarks>
/// 对应 Claude Code 的 <c>PreCompact</c>。手动压缩与阈值触发的自动压缩都会分发；
/// 此前若已压缩过，<see cref="PreviousSummary"/> 与 <see cref="CoveredMessageCount"/> 描述现有状态。
/// </remarks>
public sealed class PreCompactEvent
{
    /// <summary>获取触发原因：<c>threshold</c>（达到窗口 90%）、<c>manual</c>（/compact）或 <c>overflow</c>。</summary>
    public string Reason { get; init; } = "threshold";

    /// <summary>获取模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>获取当前请求的估算 token 数。</summary>
    public int TokenCount { get; init; }

    /// <summary>获取模型上下文窗口。</summary>
    public int ContextWindow { get; init; }

    /// <summary>获取本次将折叠的消息数。</summary>
    public int MessageCount { get; init; }

    /// <summary>获取此前的摘要；尚未压缩过时为 null。</summary>
    public string? PreviousSummary { get; init; }

    /// <summary>获取此前摘要已覆盖的消息数。</summary>
    public int CoveredMessageCount { get; init; }

    /// <summary>获取或设置取消原因；非空即取消本次压缩。</summary>
    public string? CancelReason { get; set; }

    /// <summary>获取或设置由处理器提供的摘要；非空时跳过模型调用。</summary>
    public string? Summary { get; set; }

    /// <summary>获取是否已被取消。</summary>
    public bool Cancelled => !string.IsNullOrEmpty(CancelReason);

    /// <summary>取消本次压缩。</summary>
    /// <param name="reason">展示给用户的原因。</param>
    public void Cancel(string reason) => CancelReason = reason;
}

/// <summary>上下文压缩完成后的事件。只通知。</summary>
/// <remarks>对应 Claude Code 的 <c>PostCompact</c>。摘要本身以用户消息形式进入上下文。</remarks>
public sealed class PostCompactEvent
{
    /// <summary>获取触发原因。</summary>
    public string Reason { get; init; } = "threshold";

    /// <summary>获取模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>获取摘要正文。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>获取摘要覆盖的消息数（从对话开头计）。</summary>
    public int CoveredMessageCount { get; init; }

    /// <summary>获取压缩前的估算 token 数。</summary>
    public int OriginalTokenCount { get; init; }

    /// <summary>获取压缩后的估算 token 数。</summary>
    public int CompactedTokenCount { get; init; }
}

/// <summary>上下文压缩失败的事件。可观察失败原因，不影响对话继续。</summary>
public sealed class CompactFailedEvent
{
    /// <summary>获取触发原因。</summary>
    public string Reason { get; init; } = "threshold";

    /// <summary>获取模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>获取错误描述。</summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// 提示缓存刷新的决策事件，对应 Pi 的 <c>cache_warming_decision</c>。
/// </summary>
/// <remarks>
/// 预热器会在每次刷新前分发该事件，载荷里带上它自己的结论与经济性估算；
/// 扩展可以基于会话状态、成本偏好或外部信号改写结论。
/// 只有最后一个处理器返回的结论生效；处理器抛异常时回退到内置结论。
/// </remarks>
public sealed class CacheWarmingDecisionEvent
{
    /// <summary>获取阶段：<c>streaming</c>（轮内）或 <c>idle</c>（轮间）。</summary>
    public string Phase { get; init; } = "streaming";

    /// <summary>获取模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>获取最近一次真实请求的提示 token 数。</summary>
    public int PromptTokens { get; init; }

    /// <summary>获取本次刷新的价格估算（美元）。</summary>
    public double WarmCost { get; init; }

    /// <summary>获取缓存丢失时下一次真实请求多付的价格（美元）。</summary>
    public double MissCost { get; init; }

    /// <summary>获取"下一次真实请求会在过期前到来"的概率估计。</summary>
    public double ContinuationProbability { get; init; }

    /// <summary>获取预期节省（美元）。</summary>
    public double ExpectedSavings { get; init; }

    /// <summary>获取经济性是否可估算（提示规模与单价都已知时为 true）。</summary>
    public bool EconomicsAvailable { get; init; }

    /// <summary>获取或设置结论：<c>warm</c> 发送刷新，<c>stop</c> 停止预热直到下一次真实请求。</summary>
    public string Action { get; set; } = "warm";

    /// <summary>结果是否为 <c>warm</c>。</summary>
    public bool IsWarm =>
        string.Equals(Action, "warm", StringComparison.OrdinalIgnoreCase);

    /// <summary>把结论设为 <c>warm</c>。</summary>
    public void Warm() => Action = "warm";

    /// <summary>把结论设为 <c>stop</c>。</summary>
    public void Stop() => Action = "stop";
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

/// <summary>
/// 工具授权的交互点。策略判定为 <see cref="PermissionDecision.Ask"/> 时分发；
/// 处理器可把 <see cref="Decision"/> 改写为 Allow 或 Deny 以跳过用户确认。
/// </summary>
public sealed class PermissionRequestEvent
{
    /// <summary>获取工具名。</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>获取工具参数 JSON。</summary>
    public string ArgumentsJson { get; init; } = "{}";

    /// <summary>获取或设置判定结论；默认 <see cref="PermissionDecision.Ask"/> 表示交回宿主询问用户。</summary>
    public PermissionDecision Decision { get; set; } = PermissionDecision.Ask;

    /// <summary>获取或设置命中该判定的规则来源（例如 <c>project</c>）。</summary>
    public string? RuleSource { get; init; }

    /// <summary>获取或设置展示给用户的提示文本；null 使用宿主默认文案。</summary>
    public string? Prompt { get; set; }

    /// <summary>获取或设置原因说明；拒绝时会写入工具结果。</summary>
    public string? Reason { get; set; }
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

/// <summary>工具执行失败的事件（只通知）。</summary>
public sealed class ToolErrorEvent
{
    /// <summary>获取工具名。</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>获取工具调用 ID。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>获取失败原因。</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>获取是否被取消而非失败。</summary>
    public bool Cancelled { get; init; }
}

/// <summary>一轮模型交互结束的事件。</summary>
public sealed class TurnEndEvent
{
    /// <summary>获取本轮助手文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>获取本轮工具调用次数。</summary>
    public int ToolCallCount { get; init; }

    /// <summary>获取本轮序号（从 0 开始）。</summary>
    public int TurnIndex { get; init; }

    /// <summary>获取或设置是否要求再追加一轮（扩展可给出后续指令）。</summary>
    public bool Continue { get; set; }

    /// <summary>获取或设置追加轮次时使用的用户消息。</summary>
    public string? ContinuationPrompt { get; set; }
}

/// <summary>provider 请求头事件。处理器就地修改，后续处理器看到修改后的值。</summary>
public sealed class ProviderHeadersEvent
{
    /// <summary>获取服务商标识。</summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>获取或设置可变的请求头集合（不含 Content-* 等受保护头）。</summary>
    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>provider 请求事件：可改写最终 JSON 负载与模型名。</summary>
public sealed class ProviderRequestEvent
{
    /// <summary>获取服务商标识。</summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>获取请求类别，例如 <c>chat</c>、<c>embedding</c>。</summary>
    public string Operation { get; init; } = "chat";

    /// <summary>获取或设置请求体 JSON。</summary>
    public string BodyJson { get; set; } = string.Empty;

    /// <summary>获取是否流式请求。</summary>
    public bool IsStreaming { get; init; }

    /// <summary>获取或设置拦截原因；非空则不发出请求。</summary>
    public string? BlockReason { get; set; }
}

/// <summary>provider 响应事件（只通知）。</summary>
public sealed class ProviderResponseEvent
{
    /// <summary>获取服务商标识。</summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>获取 HTTP 状态码。</summary>
    public int StatusCode { get; init; }

    /// <summary>获取响应体长度（流式响应为 0）。</summary>
    public long ContentLength { get; init; }

    /// <summary>获取耗时。</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>provider 流事件（只通知）。</summary>
public sealed class ProviderStreamEvent
{
    /// <summary>获取服务商标识。</summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>获取原始事件数据（SSE data 或 NDJSON 行）。</summary>
    public string Data { get; init; } = string.Empty;

    /// <summary>获取事件类型（SSE <c>event:</c> 字段）。</summary>
    public string? EventType { get; init; }
}

/// <summary>工具执行生命周期事件（只通知）。</summary>
public sealed class ToolExecutionEvent
{
    /// <summary>获取工具名。</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>获取工具调用 ID。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>获取参数 JSON。</summary>
    public string ArgumentsJson { get; init; } = "{}";

    /// <summary>获取增量输出（仅 <c>ToolExecutionUpdate</c>）。</summary>
    public string? PartialOutput { get; init; }

    /// <summary>获取结果文本（仅 <c>ToolExecutionEnd</c>）。</summary>
    public string? ResultText { get; init; }

    /// <summary>获取执行是否失败（仅 <c>ToolExecutionEnd</c>）。</summary>
    public bool IsError { get; init; }

    /// <summary>获取耗时（仅 <c>ToolExecutionEnd</c>）。</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>上下文事件：provider 请求前的消息列表，可改写。</summary>
public sealed class ContextEvent
{
    /// <summary>获取或设置消息列表。</summary>
    public IReadOnlyList<ChatMessage> Messages { get; set; } = Array.Empty<ChatMessage>();

    /// <summary>获取或设置系统提示词（仅 <c>ContextWithSystem</c> 生效）。</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>获取模型名。</summary>
    public string Model { get; init; } = string.Empty;
}

/// <summary>会话开始/关闭事件。</summary>
public sealed class SessionStartEvent
{
    /// <summary>获取会话 ID。</summary>
    public string? SessionId { get; init; }

    /// <summary>获取工作目录。</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>获取启动原因：<c>startup</c>、<c>reload</c> 或 <c>resume</c>。</summary>
    public string Reason { get; init; } = "startup";
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

/// <summary>
/// 由扩展包产出的扩展额外实现此接口，把随包技能与包名一并交给宿主。
/// </summary>
/// <remarks>
/// 工作区路径下技能由 <c>ExtensionPackage</c> 统一登记，不需要这个接缝；
/// 只有在宿主手动 <c>ExtensionPackageLoader.LoadAsync</c> 后把
/// <c>package.Extensions</c> 直接交给 <c>ExtensionPipelineOptions.Extensions</c> 时才需要，
/// 否则包里的 <c>SKILL.md</c> 会随代码一起被激活、却看不到技能。
/// </remarks>
public interface IPackagedExtension
{
    /// <summary>获取产出该扩展的包名。</summary>
    string PackageName { get; }

    /// <summary>获取随包携带的技能。</summary>
    IReadOnlyList<AiTools.Skills.SkillDefinition> Skills { get; }
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
    /// <param name="definition">工具定义。</param>
    /// <param name="execute">执行函数。</param>
    /// <param name="exposure">曝光级别；<see cref="ToolExposure.Direct"/> 与 <see cref="ToolExposure.ModelOnly"/> 登记即激活。</param>
    /// <param name="readOnlyHint">只读提示。</param>
    /// <param name="namespace">所属命名空间名（需先通过 <see cref="RegisterNamespace"/> 登记）；null 表示不分组。</param>
    /// <param name="reachesTools">可达范围；编排型工具用它声明一次调用能触达哪些工具。</param>
    void RegisterTool(
        ToolDefinition definition,
        Func<ToolCall, ToolContext, CancellationToken, Task<string>> execute,
        ToolExposure exposure = ToolExposure.Direct,
        bool readOnlyHint = false,
        string? @namespace = null,
        ToolReach? reachesTools = null);

    /// <summary>登记工具命名空间（同名后注册者覆盖先注册者）。</summary>
    /// <param name="namespace">命名空间定义。</param>
    void RegisterNamespace(ToolNamespace @namespace);

    /// <summary>
    /// 登记一个 skill（同名后注册者覆盖先注册者）。登记后即可被 <c>load_skill</c> 加载，
    /// 并出现在 <c>/skill:&lt;名称&gt;</c> 命令中；扩展可以在激活或会话期间随时扩充技能。
    /// </summary>
    /// <param name="skill">技能定义；名称与描述不能为空。</param>
    void RegisterSkill(AiTools.Skills.SkillDefinition skill);

    /// <summary>
    /// 注册装载准备器：在构建 <see cref="ToolLoadout"/> 时按注册顺序改写它。
    /// 抛异常的准备器被跳过，装载回退到它之前的结果。
    /// </summary>
    /// <param name="preparer">准备函数。</param>
    void RegisterLoadoutPreparer(Func<ToolLoadout, ToolLoadout> preparer);

    /// <summary>订阅事件。返回取消订阅的函数。</summary>
    Action On(ExtensionEvent @event, Func<object, CommandContext, CancellationToken, Task> handler);

    /// <summary>订阅事件并使用强类型负载；处理器返回非 null 时替换事件对象（用于转换类事件）。</summary>
    Action On<TEvent>(ExtensionEvent @event, Func<TEvent, CommandContext, CancellationToken, Task<TEvent?>> handler)
        where TEvent : class;

    /// <summary>注册工具渲染器：决定工具调用在对话中的显示文本。</summary>
    /// <param name="renderer">渲染函数；返回 null 时交给下一个渲染器或默认渲染。</param>
    void RegisterToolRenderer(Func<ToolRenderContext, string?> renderer);

    /// <summary>注册一个模型 provider，覆盖/新增 <see cref="IExtensionApi.Name"/> 之外的命名空间。</summary>
    /// <param name="registration">provider 注册信息。</param>
    void RegisterProvider(ExtensionProviderRegistration registration);

    /// <summary>注册一个 MCP 服务器（连接时使用宿主提供的工厂）。</summary>
    /// <param name="server">服务器声明（名称在扩展内唯一）。</param>
    /// <returns>取消注册并关闭连接。</returns>
    Func<Task> RegisterMcpServer(PluginMcpServer server);

    /// <summary>注册一个虚拟模型：按请求路由到实际的 provider/model。</summary>
    /// <param name="registration">路由规则。</param>
    void RegisterVirtualModel(VirtualModelRegistration registration);
}

/// <summary>工具调用渲染的上下文。</summary>
public sealed class ToolRenderContext
{
    /// <summary>获取工具名。</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>获取参数 JSON。</summary>
    public string ArgumentsJson { get; init; } = "{}";

    /// <summary>获取结果文本；调用阶段为 null。</summary>
    public string? ResultText { get; init; }

    /// <summary>获取是否为结果渲染。</summary>
    public bool IsResult { get; init; }

    /// <summary>获取工具是否报告错误。</summary>
    public bool IsError { get; init; }
}

/// <summary>扩展注册的模型 provider。</summary>
public sealed class ExtensionProviderRegistration
{
    /// <summary>获取 provider 名称（供 <see cref="VirtualModelRegistration"/> 与设置引用）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取协议标识，例如 <c>openai</c>。</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>获取 Base URL。</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>获取 API Key。</summary>
    public string? ApiKey { get; init; }

    /// <summary>获取默认模型名。</summary>
    public string? DefaultModel { get; init; }

    /// <summary>获取额外请求头。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>虚拟模型路由规则：把对 <see cref="VirtualModelName"/> 的请求转给实际模型。</summary>
public sealed class VirtualModelRegistration
{
    /// <summary>获取虚拟模型名（用户可选的 model id）。</summary>
    public string VirtualModelName { get; init; } = string.Empty;

    /// <summary>获取目标 provider 名。</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>获取目标模型名。</summary>
    public string TargetModel { get; init; } = string.Empty;

    /// <summary>获取可选的请求改写钩子。</summary>
    public Func<ChatRequest, ChatRequest>? Rewrite { get; init; }

    /// <summary>获取描述（显示在选择器中）。</summary>
    public string? Description { get; init; }
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
