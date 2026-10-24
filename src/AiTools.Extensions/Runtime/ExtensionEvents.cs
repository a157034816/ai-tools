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
    ToolResult,

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
    ProviderStreamEvent
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
