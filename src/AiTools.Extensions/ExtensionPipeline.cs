using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>
/// 扩展管道：把扩展命令、插件命令、扩展工具与插件技能/ MCP 汇总成宿主可直接使用的一组操作。
/// </summary>
/// <remarks>
/// 宿主只需要构造一次本对象，然后在发送消息前后调用这些方法；所有与 Provider 无关的
/// 逻辑（命令展开、提示词注入、工具注册、事件分发）都在这里完成。
/// </remarks>
public sealed class ExtensionPipeline : IAsyncDisposable
{
    private readonly PluginHost? _pluginHost;

    /// <summary>创建管道。</summary>
    /// <param name="options">管道选项。</param>
    public ExtensionPipeline(ExtensionPipelineOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Runtime = options.Runtime ?? new ExtensionRuntime(options.State);
        Plugins = options.Plugins;

        var workingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory;
        Session = ExtensionSession.Create(Runtime, workingDirectory);
        _pluginHost = Plugins == null
            ? null
            : new PluginHost(Plugins, options.PluginHostOptions ?? new PluginHostOptions
            {
                ProjectDirectory = workingDirectory,
                EnableShellExecution = false
            });

        // 与 Pi 的工厂语义一致：扩展在构造阶段登记命令与工具，
        // session_start 只负责启动资源，因此命令表在这里就能完整建立。
        foreach (var extension in Options.Extensions)
        {
            _activationDiagnostics.AddRange(Runtime.Activate(extension));
        }

        Commands = CommandCatalog.Create(Runtime, Plugins);
    }

    /// <summary>获取管道选项。</summary>
    public ExtensionPipelineOptions Options { get; }

    /// <summary>获取扩展运行时。</summary>
    public ExtensionRuntime Runtime { get; }

    /// <summary>获取扩展会话。</summary>
    public ExtensionSession Session { get; }

    /// <summary>获取插件目录；未配置时为 null。</summary>
    public PluginCatalog? Plugins { get; }

    /// <summary>获取合并后的命令目录。</summary>
    public CommandCatalog Commands { get; }

    /// <summary>获取插件宿主；未配置插件时为 null。</summary>
    public PluginHost? PluginHost => _pluginHost;

    private readonly List<PluginDiagnostic> _activationDiagnostics = new();

    /// <summary>获取扩展激活阶段产生的诊断。</summary>
    public IReadOnlyList<PluginDiagnostic> ActivationDiagnostics => _activationDiagnostics;

    /// <summary>启动扩展会话（分发 <c>session_start</c>）。应在首次发送消息前调用一次。</summary>
    /// <param name="sessionId">会话 ID（可为 null）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>扩展激活阶段产生的诊断（构造时已确定）。</returns>
    public async Task<IReadOnlyList<PluginDiagnostic>> StartAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        await Session.StartAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return ActivationDiagnostics;
    }

    /// <summary>关闭会话（幂等）。</summary>
    public async Task ShutdownAsync()
    {
        await Session.ShutdownAsync().ConfigureAwait(false);
        if (_pluginHost != null)
        {
            await _pluginHost.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 处理用户输入：先分发 <c>input</c> 事件，再解析斜杠命令。
    /// 返回 null 表示不是命令，调用方按普通消息处理。
    /// </summary>
    /// <param name="text">用户输入。</param>
    /// <param name="context">命令上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<CommandExecution?> ExecuteCommandAsync(
        string text,
        CommandContext context,
        CancellationToken cancellationToken = default)
    {
        if (!PluginHost.LooksLikeCommand(text))
        {
            return null;
        }

        var (name, raw) = PluginHost.SplitCommand(text);
        if (!Commands.TryResolve(name, out var command) || command == null)
        {
            return null;
        }

        var arguments = CommandArguments.Parse(raw);

        if (command.ExtensionCommand != null)
        {
            CommandResult result;
            try
            {
                result = await command.ExtensionCommand.Handler(context, arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CommandExecution
                {
                    Command = command,
                    Arguments = raw,
                    Kind = CommandExecutionKind.Error,
                    Error = $"/{command.Name} 执行失败：{ex.Message}"
                };
            }

            return new CommandExecution
            {
                Command = command,
                Arguments = raw,
                Kind = result.Kind switch
                {
                    CommandResultKind.Prompt => CommandExecutionKind.Prompt,
                    CommandResultKind.Display => CommandExecutionKind.Display,
                    _ => CommandExecutionKind.Noop
                },
                Prompt = result.Kind == CommandResultKind.Prompt ? result.UserMessage : null,
                DisplayText = result.Kind == CommandResultKind.Display ? result.UserMessage : null
            };
        }

        // 插件命令：渲染 Markdown 正文为提示词。
        var pluginCommand = command.PluginCommand!;
        var host = _pluginHost;
        if (host == null)
        {
            return new CommandExecution
            {
                Command = command,
                Arguments = raw,
                Kind = CommandExecutionKind.Error,
                Error = $"/{command.Name} 需要插件宿主，但未配置插件。"
            };
        }

        try
        {
            var invocation = host.Render(pluginCommand, arguments, context.SessionId);
            return new CommandExecution
            {
                Command = command,
                Arguments = raw,
                Kind = CommandExecutionKind.Prompt,
                Prompt = invocation.Text,
                ShellCommands = invocation.ShellCommands
            };
        }
        catch (CommandExecutionException ex)
        {
            return new CommandExecution
            {
                Command = command,
                Arguments = raw,
                Kind = CommandExecutionKind.Error,
                Error = $"/{command.Name} 执行失败：{ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new CommandExecution
            {
                Command = command,
                Arguments = raw,
                Kind = CommandExecutionKind.Error,
                Error = $"/{command.Name} 渲染失败：{ex.Message}"
            };
        }
    }

    /// <summary>
    /// 构建本轮请求前的提示词补充：先分发 <c>before_agent_start</c>，再汇总扩展工具定义。
    /// </summary>
    /// <param name="context">命令上下文。</param>
    /// <param name="model">模型名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ExtensionTurnPreparation> PrepareTurnAsync(
        CommandContext context,
        string model,
        CancellationToken cancellationToken = default)
    {
        var startEvent = new BeforeAgentStartEvent { Model = model };
        var dispatched = await Runtime.DispatchAsync(
            ExtensionEvent.BeforeAgentStart,
            startEvent,
            context,
            failClosed: startEvent,
            cancellationToken).ConfigureAwait(false);

        var resolved = dispatched as BeforeAgentStartEvent ?? startEvent;

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(resolved.SystemPrompt))
        {
            builder.AppendLine(resolved.SystemPrompt!.TrimEnd());
        }

        foreach (var section in resolved.PromptSections)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append(section.TrimEnd());
        }

        return new ExtensionTurnPreparation
        {
            SystemPromptSections = builder.ToString(),
            PrependMessages = resolved.PrependMessages,
            Tools = BuildToolRegistrations()
        };
    }

    /// <summary>分发工具调用前后的拦截/改写事件。</summary>
    public Task<object?> DispatchToolEventAsync(
        ExtensionEvent @event,
        object payload,
        CommandContext context,
        object? failClosed,
        CancellationToken cancellationToken)
        => Runtime.DispatchAsync(@event, payload, context, failClosed, cancellationToken);

    /// <summary>分发轮次结束事件。</summary>
    public async Task<TurnEndEvent> PublishTurnEndAsync(
        CommandContext context,
        string text,
        int toolCallCount,
        CancellationToken cancellationToken = default)
    {
        var endEvent = new TurnEndEvent { Text = text, ToolCallCount = toolCallCount };
        var dispatched = await Runtime.DispatchAsync(
            ExtensionEvent.TurnEnd,
            endEvent,
            context,
            failClosed: endEvent,
            cancellationToken).ConfigureAwait(false);

        return dispatched as TurnEndEvent ?? endEvent;
    }

    /// <summary>
    /// 构建暴露给模型的工具：扩展工具（按曝光级别）+ 扩展提供的工具处理器。
    /// </summary>
    public IReadOnlyList<ToolRegistration> BuildToolRegistrations()
    {
        var registrations = new List<ToolRegistration>();
        foreach (var tool in Runtime.DeclaredTools)
        {
            registrations.Add(new ToolRegistration(tool.Definition, tool));
        }

        return registrations;
    }

    /// <summary>获取全部扩展状态的 JSON 快照（用于持久化）。</summary>
    public string ExportState() => Runtime.State.ToJson();

    /// <summary>从持久化 JSON 恢复扩展状态。</summary>
    public void ImportState(string? json) => Runtime.State.LoadFromJson(json);

    /// <summary>构建给模型的插件命令清单（不含扩展命令，扩展命令通过工具暴露）。</summary>
    public string BuildPluginCommandPrompt() => _pluginHost?.BuildSystemPrompt() ?? string.Empty;

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}

/// <summary>一个工具注册：定义 + 执行器 + 归属信息。</summary>
public sealed class ToolRegistration
{
    /// <summary>创建注册项。</summary>
    public ToolRegistration(ToolDefinition definition, ExtensionTool tool)
    {
        Definition = definition;
        Tool = tool;
    }

    /// <summary>获取工具定义。</summary>
    public ToolDefinition Definition { get; }

    /// <summary>获取扩展工具。</summary>
    public ExtensionTool Tool { get; }
}

/// <summary>一轮请求的扩展准备工作。</summary>
public sealed class ExtensionTurnPreparation
{
    /// <summary>获取要合并进系统提示的段落（已用空行连接）。</summary>
    public string SystemPromptSections { get; init; } = string.Empty;

    /// <summary>获取要插入在用户消息之前的消息。</summary>
    public IReadOnlyList<ChatMessage> PrependMessages { get; init; } = Array.Empty<ChatMessage>();

    /// <summary>获取本轮可用的工具。</summary>
    public IReadOnlyList<ToolRegistration> Tools { get; init; } = Array.Empty<ToolRegistration>();
}

/// <summary>管道选项。</summary>
public sealed class ExtensionPipelineOptions
{
    /// <summary>获取或设置要激活的扩展。</summary>
    public IReadOnlyList<IExtension> Extensions { get; set; } = new IExtension[]
    {
        new BuiltIn.GoalExtension(),
        new BuiltIn.PlanExtension()
    };

    /// <summary>获取或设置扩展运行时；为 null 时新建。</summary>
    public ExtensionRuntime? Runtime { get; set; }

    /// <summary>获取或设置扩展状态存储；为 null 时使用运行时自带的存储。</summary>
    public ExtensionStateStore? State { get; set; }

    /// <summary>获取或设置插件目录（Claude Code 兼容插件）。</summary>
    public PluginCatalog? Plugins { get; set; }

    /// <summary>获取或设置插件命令的渲染选项。</summary>
    public PluginHostOptions? PluginHostOptions { get; set; }

    /// <summary>获取或设置工作目录。</summary>
    public string? WorkingDirectory { get; set; }
}
