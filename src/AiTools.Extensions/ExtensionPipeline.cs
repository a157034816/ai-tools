using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Extensions.Workflows;

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
    private readonly PluginHost _mcpHost;
    private static readonly AsyncLocal<ToolCallEvent?> HostToolCallPreflight = new();


    /// <summary>创建管道。</summary>
    /// <param name="options">管道选项。</param>
    public ExtensionPipeline(ExtensionPipelineOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Runtime = options.Runtime ?? new ExtensionRuntime(options.State);
        Plugins = options.Plugins;

        var workingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory;
        Session = ExtensionSession.Create(Runtime, workingDirectory);
        var hostOptions = options.PluginHostOptions ?? new PluginHostOptions
        {
            ProjectDirectory = workingDirectory,
            EnableShellExecution = false
        };

        _pluginHost = Plugins == null ? null : new PluginHost(Plugins, hostOptions);

        // 扩展也可以注册 MCP 服务器；这需要一个宿主来持有连接，
        // 即使没有加载任何 Claude Code 插件。
        _mcpHost = _pluginHost ?? new PluginHost(PluginCatalog.FromPlugins(Array.Empty<AiToolsPlugin>()), hostOptions);

        OutputStyles = OutputStyleCatalog.LoadFromPlugins(Plugins ?? PluginCatalog.FromPlugins(Array.Empty<AiToolsPlugin>()));
        SelectedStyle = OutputStyleSelection.Resolve(OutputStyles, options.OutputStyle);

        // 与 Pi 的工厂语义一致：扩展在构造阶段登记命令与工具，
        // session_start 只负责启动资源，因此命令表在这里就能完整建立。
        foreach (var extension in Options.Extensions)
        {
            _activationDiagnostics.AddRange(Runtime.Activate(extension));
        }

        ActivatePipelineFeatures();

        Commands = CommandCatalog.Create(Runtime, Plugins);
    }
    private void ActivatePipelineFeatures()
    {
        if (Options.EnableWorkflows && Plugins != null && Plugins.Workflows.Count > 0)
        {
            var optionsFactory = Options.WorkflowOptionsFactory ?? CreateDefaultWorkflowOptions;
            _activationDiagnostics.AddRange(Runtime.Activate(WorkflowCommandAdapter.CreateExtension(
                Plugins.Workflows,
                optionsFactory,
                CreateUniqueExtensionName("ai-tools-workflows"))));
        }

        if (Options.Permissions != null)
        {
            _activationDiagnostics.AddRange(Runtime.Activate(new PipelinePermissionBackstopExtension(
                Options.Permissions,
                CreateUniqueExtensionName("ai-tools-permission-backstop"))));
        }
    }

    private string CreateUniqueExtensionName(string prefix)
    {
        var names = new HashSet<string>(Runtime.Extensions.Select(extension => extension.Name), StringComparer.OrdinalIgnoreCase);
        var name = prefix;
        for (var suffix = 2; names.Contains(name); suffix++)
        {
            name = prefix + "-" + suffix;
        }

        return name;
    }


    /// <summary>获取管道选项。</summary>
    public ExtensionPipelineOptions Options { get; }

    /// <summary>获取扩展运行时。</summary>
    public ExtensionRuntime Runtime { get; }

    /// <summary>获取扩展会话。热重载会替换它，因此旧会话上下文不得在重载后复用。</summary>
    public ExtensionSession Session { get; private set; }

    /// <summary>获取插件目录；未配置时为 null。</summary>
    public PluginCatalog? Plugins { get; }

    /// <summary>获取合并后的命令目录。</summary>
    public CommandCatalog Commands { get; private set; }

    /// <summary>获取插件宿主；未配置插件时为 null。</summary>
    public PluginHost? PluginHost => _pluginHost;

    /// <summary>获取插件提供的输出风格目录。</summary>
    public OutputStyleCatalog OutputStyles { get; }

    /// <summary>获取当前选中的输出风格。</summary>
    public OutputStyleSelection SelectedStyle { get; private set; } = OutputStyleSelection.None;

    /// <summary>按名称选择输出风格；不存在的名称会清除当前选择。返回选择是否变化。</summary>
    /// <param name="name">风格名称；null 或未知名称清除当前选择。</param>
    /// <returns>选择是否变化。</returns>
    public bool SelectOutputStyle(string? name)
    {
        var next = OutputStyleSelection.Resolve(OutputStyles, name);
        if (ReferenceEquals(SelectedStyle.Style, next.Style))
        {
            return false;
        }

        SelectedStyle = next;
        return true;
    }

    private WorkflowRuntimeOptions CreateDefaultWorkflowOptions()
    {
        var sessionContext = Session.CreateContext(null, null);
        return new WorkflowRuntimeOptions
        {
            ToolInvoker = async (name, json) =>
                (await Runtime.ExecuteToolAsync(name, json, sessionContext).ConfigureAwait(false)).Text,
            Log = _ => { },
            Policy = Options.Permissions
        };
    }

    /// <summary>
    /// 管道工具权限回退：拦截所有事件分发路径（fail-closed），并从模型声明中移除明确拒绝的工具。
    /// </summary>
    private sealed class PipelinePermissionBackstopExtension : IExtension
    {
        private readonly PermissionPolicy _policy;

        public PipelinePermissionBackstopExtension(PermissionPolicy policy, string name)
        {
            _policy = policy;
            Name = name;
        }

        public string Name { get; }

        public string? Description => "管道工具权限后备。";

        public void Activate(IExtensionApi api)
        {
            api.On<ToolCallEvent>(ExtensionEvent.ToolCall, (toolCall, _, _) =>
            {
                var decision = _policy.Evaluate(toolCall.ToolName, toolCall.ArgumentsJson);
                if (decision != PermissionDecision.Allow &&
                    !(decision == PermissionDecision.Ask && ReferenceEquals(HostToolCallPreflight.Value, toolCall)))
                {
                    toolCall.Block("denied by policy");
                }

                return Task.FromResult<ToolCallEvent?>(toolCall);
            });

            api.RegisterLoadoutPreparer(loadout =>
            {
                loadout.Declared = loadout.Declared
                    .Where(tool => _policy.Evaluate(tool.Definition.Name, "{}") != PermissionDecision.Deny)
                    .ToArray();
                return loadout;
            });
        }
    }


    private readonly List<PluginDiagnostic> _activationDiagnostics = new();

    /// <summary>获取或设置宿主的交互界面（供 <c>ctx.Ui</c> 使用）。</summary>
    public IExtensionUi? Ui { get; set; }

    /// <summary>获取扩展激活阶段产生的诊断。</summary>
    public IReadOnlyList<PluginDiagnostic> ActivationDiagnostics => _activationDiagnostics;

    /// <summary>启动扩展会话（分发 <c>session_start</c>）。应在首次发送消息前调用一次。</summary>
    /// <param name="sessionId">会话 ID（可为 null）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>扩展激活阶段产生的诊断（构造时已确定）。</returns>
    public async Task<IReadOnlyList<PluginDiagnostic>> StartAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        Session.Ui = Ui;
        await Session.StartAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await ConnectExtensionMcpServersAsync(cancellationToken).ConfigureAwait(false);
        return ActivationDiagnostics;
    }

    /// <summary>
    /// 连接扩展注册的 MCP 服务器，并把它们提供的工具登记为扩展工具
    /// （名称前缀 <c>ext_&lt;server&gt;_</c>）。连接失败的服务器记录诊断但不阻止启动。
    /// </summary>
    private async Task ConnectExtensionMcpServersAsync(CancellationToken cancellationToken)
    {
        foreach (var server in Runtime.McpServers)
        {
            try
            {
                var client = await _mcpHost.ConnectMcpServerAsync(server, cancellationToken).ConfigureAwait(false);
                var mcpClient = _mcpHost.TryGetMcpClient(server.QualifiedName);
                if (mcpClient == null)
                {
                    continue;
                }

                var tools = await mcpClient.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var tool in tools)
                {
                    var name = "ext_" + SanitizeToolName(server.Name) + "_" + SanitizeToolName(tool.Name);
                    Runtime.RegisterExternalTool(
                        ToolDefinition.Create(name, tool.Description, tool.InputSchema),
                        async (call, token) =>
                        {
                            System.Text.Json.JsonElement? arguments = null;
                            var json = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
                            try
                            {
                                using var document = System.Text.Json.JsonDocument.Parse(json);
                                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    arguments = document.RootElement.Clone();
                                }
                            }
                            catch (System.Text.Json.JsonException)
                            {
                                return "{\"error\":\"Invalid JSON arguments\"}";
                            }

                            var result = await mcpClient.CallToolAsync(tool.Name, arguments, token).ConfigureAwait(false);
                            var text = result.GetText();
                            if (text.Length == 0 && result.StructuredContent.HasValue)
                            {
                                text = result.StructuredContent.Value.GetRawText();
                            }

                            return text;
                        },
                        server.PluginName);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _activationDiagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    PluginName = server.PluginName,
                    Path = server.SourcePath,
                    Message = $"MCP server '{server.QualifiedName}' failed to connect: {ex.Message}"
                });
            }
        }
    }

    private static string SanitizeToolName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        return builder.Length == 0 ? "x" : builder.ToString();
    }

    /// <summary>关闭会话（幂等）。</summary>
    public async Task ShutdownAsync()
    {
        await Runtime.CloseMcpServersAsync().ConfigureAwait(false);
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
    /// <param name="baseSystemPrompt">宿主当前系统提示；选中输出风格时用于生成完整提示词。</param>
    public async Task<ExtensionTurnPreparation> PrepareTurnAsync(
        CommandContext context,
        string model,
        CancellationToken cancellationToken = default,
        string? baseSystemPrompt = null)
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

        var systemPromptOverride = SelectedStyle.HasStyle ? SelectedStyle.ApplyTo(baseSystemPrompt) : null;
        if (systemPromptOverride != null)
        {
            var extensionSections = builder.ToString();
            builder.Clear().Append(systemPromptOverride);
            if (extensionSections.Length > 0)
            {
                builder.AppendLine().AppendLine().Append(extensionSections);
            }
        }

        return new ExtensionTurnPreparation
        {
            SystemPromptSections = builder.ToString(),
            SystemPromptOverride = systemPromptOverride,
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

    /// <summary>分发宿主工具调用事件；权限回退由之后的 <see cref="GuardToolAsync"/> 交互处理。</summary>
    /// <param name="toolCall">宿主收到的工具调用事件。</param>
    /// <param name="context">命令上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ToolCallEvent> DispatchHostToolCallAsync(
        ToolCallEvent toolCall,
        CommandContext context,
        CancellationToken cancellationToken = default)
    {
        var previous = HostToolCallPreflight.Value;
        HostToolCallPreflight.Value = toolCall;
        var failClosed = new ToolCallEvent
        {
            ToolName = toolCall.ToolName,
            BlockReason = "tool_call 处理器失败，按安全默认拒绝执行。"
        };

        try
        {
            var dispatched = await Runtime.DispatchAsync(
                ExtensionEvent.ToolCall,
                toolCall,
                context,
                failClosed,
                cancellationToken).ConfigureAwait(false);
            return dispatched as ToolCallEvent ?? failClosed;
        }
        finally
        {
            HostToolCallPreflight.Value = previous;
        }
    }


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
    /// 构建暴露给模型的工具：扩展工具（按装载结果声明、曝光级别与准备器改写）。
    /// </summary>
    public IReadOnlyList<ToolRegistration> BuildToolRegistrations()
    {
        var registrations = new List<ToolRegistration>();
        foreach (var tool in Runtime.BuildLoadout().Declared)
        {
            registrations.Add(new ToolRegistration(tool.Definition, tool));
        }

        return registrations;
    }

    /// <summary>
    /// 守卫工具调用。未配置策略时返回 null，表示宿主应直接执行；配置策略后，宿主 seam 可交互处理 Ask。
    /// </summary>
    /// <remarks>tool_call handler 是所有事件分发路径的 fail-closed backstop；此 seam 交互式解决 Ask。</remarks>
    /// <param name="toolName">工具名。</param>
    /// <param name="argumentsJson">调用参数的 JSON 文本。</param>
    /// <param name="execute">实际执行体。</param>
    /// <param name="confirm">Ask 决策的确认回调；null 表示拒绝。</param>
    public Task<string>? GuardToolAsync(
        string toolName,
        string argumentsJson,
        Func<Task<string>> execute,
        Func<string, Task<bool>>? confirm)
    {
        if (Options.Permissions == null)
        {
            return null;
        }

        return PermissionGate.GuardAsync(Options.Permissions, toolName, argumentsJson, execute, confirm);
    }

    /// <summary>整体替换激活的工具集合；未登记的名字被忽略。返回集合是否发生变化。</summary>
    /// <param name="names">要激活的工具名。</param>
    public bool SetActiveTools(IEnumerable<string> names) => Runtime.SetActiveTools(names);

    /// <summary>激活一个 <c>codemode</c>/<c>deferred</c>/<c>hidden</c> 工具。返回集合是否发生变化。</summary>
    /// <param name="name">工具名。</param>
    public bool ActivateTool(string name) => Runtime.ActivateTool(name);

    /// <summary>获取全部扩展状态的 JSON 快照（用于持久化）。</summary>
    public string ExportState() => Runtime.State.ToJson();

    /// <summary>从持久化 JSON 恢复扩展状态。</summary>
    public void ImportState(string? json) => Runtime.State.LoadFromJson(json);

    /// <summary>
    /// 构建 HTTP 观察器：把它挂到 Provider 的 <c>AiHttpOptions.Observer</c> 后，
    /// <c>Context</c>/<c>BeforeProviderHeaders</c>/<c>BeforeProviderRequest</c>/
    /// <c>AfterProviderResponse</c>/<c>ProviderStreamEvent</c> 会作用于真实请求。
    /// </summary>
    /// <param name="contextFactory">每次回调时构建上下文（避免持有过期状态）。</param>
    public AiTools.Core.Http.IAiHttpObserver CreateHttpObserver(Func<CommandContext>? contextFactory = null)
        => new ExtensionHttpObserver(Runtime, contextFactory ?? (() => Session.CreateContext(null, null)));

    /// <summary>
    /// 分发 <c>context</c> / <c>context_with_system</c> 钩子：Provider 请求前改写消息与系统提示。
    /// </summary>
    /// <param name="context">命令上下文。</param>
    /// <param name="messages">当前消息列表。</param>
    /// <param name="systemPrompt">当前系统提示。</param>
    /// <param name="model">模型名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ContextEvent> ApplyContextHooksAsync(
        CommandContext context,
        IReadOnlyList<ChatMessage> messages,
        string? systemPrompt,
        string model,
        CancellationToken cancellationToken = default)
    {
        var withoutSystem = new ContextEvent { Messages = messages, SystemPrompt = systemPrompt, Model = model };
        var dispatched = await Runtime.DispatchAsync(
            ExtensionEvent.Context,
            withoutSystem,
            context,
            failClosed: withoutSystem,
            cancellationToken).ConfigureAwait(false);

        var resolved = (ContextEvent?)dispatched ?? withoutSystem;

        var withSystem = new ContextEvent { Messages = resolved.Messages, SystemPrompt = resolved.SystemPrompt, Model = model };
        var dispatchedWithSystem = await Runtime.DispatchAsync(
            ExtensionEvent.ContextWithSystem,
            withSystem,
            context,
            failClosed: withSystem,
            cancellationToken).ConfigureAwait(false);

        return (ContextEvent?)dispatchedWithSystem ?? withSystem;
    }

    /// <summary>
    /// 重新激活扩展：先关闭会话（触发 <c>session_shutdown</c>），清空注册表，再重新登记并启动。
    /// 用于开发期热重载。旧运行时上的状态会被保留（同一 <see cref="ExtensionStateStore"/>）。
    /// </summary>
    /// <param name="extensions">新的扩展集合。</param>
    /// <param name="sessionId">会话 ID。</param>
    public async Task<IReadOnlyList<PluginDiagnostic>> ReloadAsync(
        IReadOnlyList<IExtension>? extensions = null,
        string? sessionId = null)
    {
        await Runtime.CloseMcpServersAsync().ConfigureAwait(false);
        await Session.ShutdownAsync().ConfigureAwait(false);
        Runtime.Reset();
        _activationDiagnostics.Clear();

        var next = extensions ?? Options.Extensions;
        foreach (var extension in next)
        {
            _activationDiagnostics.AddRange(Runtime.Activate(extension));
        }
        ActivatePipelineFeatures();
        Commands = CommandCatalog.Create(Runtime, Plugins);


        var restarted = ExtensionSession.Create(Runtime, Session.WorkingDirectory);
        await restarted.StartAsync(sessionId ?? Session.SessionId).ConfigureAwait(false);
        Session = restarted;

        return _activationDiagnostics;
    }

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
    /// <summary>获取要合并进系统提示的内容；选中风格时以风格调整后的宿主提示为前缀。</summary>
    public string SystemPromptSections { get; init; } = string.Empty;

    /// <summary>获取需要替换宿主系统提示的内容；null 表示宿主保留自己的系统提示。</summary>
    public string? SystemPromptOverride { get; init; }

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
    /// <summary>获取或设置是否激活插件工作流。默认为 true。</summary>
    public bool EnableWorkflows { get; set; } = true;

    /// <summary>获取或设置工作流运行时选项工厂。</summary>
    public Func<WorkflowRuntimeOptions>? WorkflowOptionsFactory { get; set; }

    /// <summary>获取或设置初始输出风格名称。</summary>
    public string? OutputStyle { get; set; }

    /// <summary>获取或设置工具权限策略；为 null 时不启用权限闸门。</summary>
    public PermissionPolicy? Permissions { get; set; }
}
