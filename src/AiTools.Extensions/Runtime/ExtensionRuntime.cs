using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>
/// 扩展注册表与事件分发器。扩展在激活时登记命令、工具与事件处理器；
/// 事件按注册顺序执行，转换类事件链式传递，拦截类事件 fail-closed。
/// </summary>
/// <remarks>
/// 该类型不做持久化、不持有 UI，也不感知 WPF；宿主通过它拿到命令表、工具表与事件流。
/// </remarks>
public sealed class ExtensionRuntime
{
    private readonly List<IExtension> _extensions = new();
    private readonly List<ExtensionCommand> _commands = new();
    private readonly List<ExtensionTool> _tools = new();
    private readonly List<EventSubscription> _subscriptions = new();
    private readonly Dictionary<string, bool> _activationState = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>创建运行时。</summary>
    /// <param name="state">扩展状态存储；为 null 时新建。</param>
    public ExtensionRuntime(ExtensionStateStore? state = null)
    {
        State = state ?? new ExtensionStateStore();
    }

    /// <summary>获取扩展状态存储。</summary>
    public ExtensionStateStore State { get; }

    /// <summary>获取已注册的扩展，按激活顺序。</summary>
    public IReadOnlyList<IExtension> Extensions
    {
        get
        {
            lock (_gate)
            {
                return _extensions.ToArray();
            }
        }
    }

    /// <summary>获取已注册的命令，按名称排序；同名命令后注册者覆盖先注册者。</summary>
    public IReadOnlyList<ExtensionCommand> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands
                    .GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .OrderBy(command => command.Name, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>获取已注册的工具，按名称排序；同名工具后注册者覆盖先注册者。</summary>
    public IReadOnlyList<ExtensionTool> Tools
    {
        get
        {
            lock (_gate)
            {
                return _tools
                    .GroupBy(tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .OrderBy(tool => tool.Definition.Name, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>获取声明给模型的工具（<see cref="ToolExposure.Direct"/> 与 <see cref="ToolExposure.ModelOnly"/>）。</summary>
    public IReadOnlyList<ExtensionTool> DeclaredTools => Tools
        .Where(tool => tool.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly)
        .ToArray();

    /// <summary>激活一个扩展。激活过程抛出的异常会被捕获并记录，不影响其它扩展。</summary>
    public IReadOnlyList<PluginDiagnostic> Activate(IExtension extension)
    {
        if (extension == null)
        {
            throw new ArgumentNullException(nameof(extension));
        }

        var diagnostics = new List<PluginDiagnostic>();
        lock (_gate)
        {
            if (_activationState.ContainsKey(extension.Name))
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    PluginName = extension.Name,
                    Message = "Extension is already activated; the duplicate activation was ignored."
                });
                return diagnostics;
            }

            _activationState[extension.Name] = true;
            _extensions.Add(extension);
        }

        try
        {
            extension.Activate(new ExtensionApi(this, extension.Name));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Error,
                PluginName = extension.Name,
                Message = "Extension failed to activate: " + ex.Message
            });
            lock (_gate)
            {
                _extensions.Remove(extension);
                _commands.RemoveAll(command => string.Equals(command.ExtensionName, extension.Name, StringComparison.OrdinalIgnoreCase));
                _tools.RemoveAll(tool => string.Equals(tool.ExtensionName, extension.Name, StringComparison.OrdinalIgnoreCase));
                _subscriptions.RemoveAll(subscription => string.Equals(subscription.ExtensionName, extension.Name, StringComparison.OrdinalIgnoreCase));
                _activationState.Remove(extension.Name);
            }
        }

        return diagnostics;
    }

    /// <summary>按名称查找命令。</summary>
    public ExtensionCommand? TryGetCommand(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Commands.FirstOrDefault(command => string.Equals(command.Name, name!.Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按名称查找工具（包含隐藏工具，供程序化调用）。</summary>
    public ExtensionTool? TryGetTool(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Tools.FirstOrDefault(tool => string.Equals(tool.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>分发通知类事件；处理器异常被捕获并忽略，避免打断对话。</summary>
    public async Task PublishAsync(ExtensionEvent @event, object payload, CommandContext context, CancellationToken cancellationToken = default)
    {
        foreach (var subscription in Snapshot(@event, context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await subscription.Handler(payload, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 通知型事件失败不应打断对话。
            }
        }
    }

    /// <summary>
    /// 分发转换/拦截类事件：按注册顺序执行，处理器可返回替换对象；
    /// 处理器异常按 fail-closed 处理并返回 <paramref name="failClosed"/> 的结果。
    /// </summary>
    public async Task<object?> DispatchAsync(
        ExtensionEvent @event,
        object payload,
        CommandContext context,
        object? failClosed = null,
        CancellationToken cancellationToken = default)
    {
        var current = payload;
        foreach (var subscription in Snapshot(@event, context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var replacement = await subscription.Handler(current, context, cancellationToken).ConfigureAwait(false);
                if (replacement != null)
                {
                    current = replacement;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return failClosed;
            }
        }

        return current;
    }

    /// <summary>判断某个事件是否至少有一个订阅者。</summary>
    public bool HasHandlers(ExtensionEvent @event)
    {
        lock (_gate)
        {
            return _subscriptions.Any(subscription => subscription.Event == @event);
        }
    }

    /// <summary>
    /// 执行工具本体，不再分发 <c>tool_call</c>/<c>tool_result</c> 事件。
    /// 供宿主已经自己分发过事件时使用（例如 WPF 对话页对所有工具统一拦截）。
    /// </summary>
    public async Task<ExtensionToolResult> InvokeToolAsync(
        string name,
        string argumentsJson,
        ToolContext toolContext,
        CancellationToken cancellationToken = default)
    {
        var tool = TryGetTool(name);
        if (tool == null)
        {
            return new ExtensionToolResult
            {
                ToolName = name,
                Text = "{\"error\":\"Unknown tool: " + name + "\"}",
                IsError = true,
                NotFound = true
            };
        }

        if (tool.Exposure == ToolExposure.Hidden)
        {
            return new ExtensionToolResult
            {
                ToolName = name,
                Text = "{\"error\":\"Tool is not reachable: " + name + "\"}",
                IsError = true
            };
        }

        try
        {
            var text = await tool.Execute(
                new ToolCall("inv-" + Guid.NewGuid().ToString("N"), name, string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson),
                toolContext,
                cancellationToken).ConfigureAwait(false);

            return new ExtensionToolResult { ToolName = name, Text = text ?? string.Empty };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExtensionToolResult
            {
                ToolName = name,
                Text = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}",
                IsError = true
            };
        }
    }

    /// <summary>
    /// 调用工具。动态调用（扩展调用扩展）与模型调用走同一条路径：
    /// 都会经过 <c>tool_call</c> 与 <c>tool_result</c> 处理器。
    /// </summary>
    /// <param name="name">工具名。</param>
    /// <param name="argumentsJson">参数 JSON。</param>
    /// <param name="context">命令上下文（用于事件分发）。</param>
    /// <param name="parentToolName">发起调用的工具名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ExtensionToolResult> ExecuteToolAsync(
        string name,
        string argumentsJson,
        CommandContext context,
        string? parentToolName = null,
        CancellationToken cancellationToken = default)
    {
        var tool = TryGetTool(name);
        if (tool == null)
        {
            return new ExtensionToolResult { ToolName = name, Text = "{\"error\":\"Unknown tool: " + name + "\"}", IsError = true, NotFound = true };
        }

        var call = new ToolCall("ext-" + Guid.NewGuid().ToString("N"), name, string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var callEvent = new ToolCallEvent { ToolName = name, ArgumentsJson = call.ArgumentsJson };
        var dispatched = await DispatchAsync(ExtensionEvent.ToolCall, callEvent, context, failClosed: new ToolCallEvent { ToolName = name, BlockReason = "tool_call handler failed" }, cancellationToken).ConfigureAwait(false);

        var resolved = dispatched as ToolCallEvent ?? callEvent;
        if (!string.IsNullOrEmpty(resolved.BlockReason))
        {
            return new ExtensionToolResult { ToolName = name, Text = resolved.BlockReason!, IsError = true, Blocked = true };
        }

        string text;
        var isError = false;
        try
        {
            var toolContext = new ToolContext
            {
                Runtime = this,
                State = State,
                ParentToolName = parentToolName,
                SessionId = context.SessionId
            };

            text = await tool.Execute(
                new ToolCall(call.Id, name, resolved.ArgumentsJson),
                toolContext,
                cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            text = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}";
            isError = true;
        }

        var resultEvent = new ToolResultEvent { ToolName = name, Text = text, IsError = isError };
        var transformed = await DispatchAsync(ExtensionEvent.ToolResult, resultEvent, context, failClosed: resultEvent, cancellationToken).ConfigureAwait(false);
        var final = transformed as ToolResultEvent ?? resultEvent;

        return new ExtensionToolResult
        {
            ToolName = name,
            Text = final.Text,
            IsError = isError || final.IsError
        };
    }

    /// <summary>供命令上下文使用的简化工具调用。</summary>
    public async Task<string> ExecuteToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        var context = new CommandContext
        {
            Runtime = this,
            State = State,
            WorkingDirectory = Environment.CurrentDirectory
        };

        var result = await ExecuteToolAsync(name, argumentsJson, context, parentToolName: null, cancellationToken).ConfigureAwait(false);
        return result.Text;
    }

    private IReadOnlyList<EventSubscription> Snapshot(ExtensionEvent @event, CommandContext context)
    {
        lock (_gate)
        {
            return _subscriptions.Where(subscription => subscription.Event == @event).ToArray();
        }
    }

    /// <summary>扩展注册 API 的实现。</summary>
    private sealed class ExtensionApi : IExtensionApi
    {
        private readonly ExtensionRuntime _runtime;

        public ExtensionApi(ExtensionRuntime runtime, string name)
        {
            _runtime = runtime;
            Name = name;
        }

        public string Name { get; }

        public ExtensionStateStore State => _runtime.State;

        public void RegisterCommand(
            string name,
            string description,
            Func<CommandContext, CommandArguments, CancellationToken, Task<CommandResult>> handler,
            string? argumentHint = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A command name is required.", nameof(name));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var command = new ExtensionCommand
            {
                Name = name.Trim().TrimStart('/'),
                Description = description ?? string.Empty,
                ArgumentHint = argumentHint,
                ExtensionName = Name,
                Handler = handler
            };

            lock (_runtime._gate)
            {
                _runtime._commands.Add(command);
            }
        }

        public void RegisterTool(
            ToolDefinition definition,
            Func<ToolCall, ToolContext, CancellationToken, Task<string>> execute,
            ToolExposure exposure = ToolExposure.Direct,
            bool readOnlyHint = false)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (execute == null)
            {
                throw new ArgumentNullException(nameof(execute));
            }

            var tool = new ExtensionTool
            {
                Definition = definition,
                ExtensionName = Name,
                Exposure = exposure,
                Execute = execute,
                ReadOnlyHint = readOnlyHint
            };

            lock (_runtime._gate)
            {
                _runtime._tools.Add(tool);
            }
        }

        public Action On(ExtensionEvent @event, Func<object, CommandContext, CancellationToken, Task> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var subscription = new EventSubscription(Name, @event, async (payload, context, token) =>
            {
                await handler(payload, context, token).ConfigureAwait(false);
                return null;
            });

            lock (_runtime._gate)
            {
                _runtime._subscriptions.Add(subscription);
            }

            return () =>
            {
                lock (_runtime._gate)
                {
                    _runtime._subscriptions.Remove(subscription);
                }
            };
        }

        public Action On<TEvent>(ExtensionEvent @event, Func<TEvent, CommandContext, CancellationToken, Task<TEvent?>> handler)
            where TEvent : class
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var subscription = new EventSubscription(Name, @event, async (payload, context, token) =>
            {
                if (payload is not TEvent typed)
                {
                    return null;
                }

                var replacement = await handler(typed, context, token).ConfigureAwait(false);
                return replacement;
            });

            lock (_runtime._gate)
            {
                _runtime._subscriptions.Add(subscription);
            }

            return () =>
            {
                lock (_runtime._gate)
                {
                    _runtime._subscriptions.Remove(subscription);
                }
            };
        }
    }
}

/// <summary>一次工具调用的结果。</summary>
public sealed class ExtensionToolResult
{
    /// <summary>获取工具名。</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>获取结果文本（失败时为错误描述）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>获取是否失败。</summary>
    public bool IsError { get; init; }

    /// <summary>获取是否被 <c>tool_call</c> 处理器拦截。</summary>
    public bool Blocked { get; init; }

    /// <summary>获取工具是否不存在。</summary>
    public bool NotFound { get; init; }
}
