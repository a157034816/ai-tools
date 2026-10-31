using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Core.Chat;
using AiTools.Core.Sessions;
using AiTools.Extensions;
using AiTools.Extensions.Packages;
using AiTools.Extensions.Scripting;
using AiTools.Skills;
using AiTools.Terminal.Skills;

namespace AiTools.Terminal.Agent;

/// <summary>
/// 与界面无关的 Agent 会话：负责插件/扩展/技能的加载、每轮请求的拼装、
/// 工具调用循环、权限闸门、扩展事件与对话持久化。
/// </summary>
/// <remarks>
/// 这是 WPF 对话页逻辑的终端对应实现。界面只通过 <see cref="Updates"/> 事件接收增量，
/// 通过 <see cref="SubmitAsync"/> 提交输入，因此同一套会话可以被全屏 TUI、行式 REPL
/// 或非交互的一次性命令复用。
/// </remarks>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly TerminalOptions _options;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly List<TranscriptMessage> _history = new();
    private readonly List<IChatToolProvider> _registeredProviders = new();
    private readonly object _toolProviderGate = new();
    private AppSettings? _settings;
    private PluginHost? _pluginHost;
    private ExtensionWorkspace? _workspace;
    private ExtensionPipeline? _pipeline;
    private TerminalSkillProvider? _skillProvider;
    private PluginMcpToolProvider? _mcpProvider;
    private Agent.TerminalProviderRouter? _router;
    private ConversationRecord? _conversation;
    private CancellationTokenSource? _turnCancellation;
    private string? _pendingContinuation;
    private bool _disposed;

    /// <summary>创建会话。</summary>
    /// <param name="options">运行选项。</param>
    public AgentSession(TerminalOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Normalize();
        WorkingDirectory = Path.GetFullPath(_options.WorkingDirectory ?? Environment.CurrentDirectory);
    }

    /// <summary>会话更新事件。可能在后台线程触发，界面需自行封送。</summary>
    public event Action<AgentUpdate>? Updates;

    /// <summary>获取工作目录。</summary>
    public string WorkingDirectory { get; }

    /// <summary>获取运行选项。</summary>
    public TerminalOptions Options => _options;

    /// <summary>获取或设置交互式提问宿主（影响扩展 <c>ask</c> 与权限确认）。</summary>
    public IInteractivePrompt Prompt { get; set; } = HeadlessPrompt.Instance;

    /// <summary>获取模型看到的对话历史（最旧在前）。</summary>
    public IReadOnlyList<TranscriptMessage> History
    {
        get
        {
            lock (_history)
            {
                return _history.ToArray();
            }
        }
    }

    /// <summary>获取扩展管道；未启用扩展时为 null。</summary>
    public ExtensionPipeline? Pipeline => _pipeline;

    /// <summary>获取插件宿主；未加载插件时为 null。</summary>
    public PluginHost? PluginHost => _pluginHost;

    /// <summary>获取扩展工作区；未启用扩展包时为 null。</summary>
    public ExtensionWorkspace? Workspace => _workspace;

    /// <summary>获取当前服务商。</summary>
    public ProviderConfig? CurrentProvider { get; private set; }

    /// <summary>获取当前模型。</summary>
    public ModelConfig? CurrentModel { get; private set; }

    /// <summary>获取当前会话标题。</summary>
    public string Title { get; private set; } = "新对话";

    /// <summary>获取当前会话 ID；未保存时为 null。</summary>
    public string? ConversationId => _conversation?.Id;

    /// <summary>获取是否正在进行一轮请求。</summary>
    public bool IsBusy { get; private set; }

    /// <summary>获取当前状态栏文本。</summary>
    public string Status { get; private set; } = "就绪";

    /// <summary>获取可选择的模型。</summary>
    public IReadOnlyList<ModelOption> AvailableModels { get; private set; } = Array.Empty<ModelOption>();

    /// <summary>获取加载过程中的诊断（插件、扩展包、MCP 连接等）。</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; private set; } = Array.Empty<PluginDiagnostic>();

    /// <summary>获取共享 <c>.agents</c> 发现结果。</summary>
    public SharedAgentsCatalog? SharedAgents { get; private set; }

    /// <summary>获取技能 provider（插件 + 扩展 + 共享技能）；没有任何技能时为 null。</summary>
    public TerminalSkillProvider? SkillProvider => _skillProvider;

    /// <summary>获取命令目录（扩展 + 插件 + 技能）；未启用扩展时为 null。</summary>
    public CommandCatalog? Commands => _pipeline?.Commands;

    /// <summary>获取输出风格目录。</summary>
    public OutputStyleCatalog? OutputStyles => _pipeline?.OutputStyles;

    /// <summary>获取当前选中的输出风格。</summary>
    public OutputStyleSelection SelectedStyle => _pipeline?.SelectedStyle ?? OutputStyleSelection.None;

    /// <summary>
    /// 加载设置、插件、扩展与技能，并决定初始服务商/模型。
    /// 应在首次交互前调用一次。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<PluginDiagnostic>();
        _settings = await _options.SettingsStore!.LoadAsync(cancellationToken).ConfigureAwait(false);

        LoadSharedAgents();
        LoadPlugins(diagnostics);
        LoadPipeline(diagnostics);
        await LoadWorkspaceAsync(diagnostics, cancellationToken).ConfigureAwait(false);

        Diagnostics = diagnostics.ToArray();
        AvailableModels = BuildModelOptions();
        SelectInitialModel();

        if (_pipeline != null)
        {
            _pipeline.Ui = new TerminalExtensionUi(
                (message, kind) => Raise(new AgentUpdate
                {
                    Kind = AgentUpdateKind.Notice,
                    Text = message,
                    NoticeKind = kind
                }),
                text => SetStatus(text ?? string.Empty),
                Prompt);

            try
            {
                var startDiagnostics = await _pipeline.StartAsync(null, cancellationToken).ConfigureAwait(false);
                if (startDiagnostics.Count > 0)
                {
                    Diagnostics = Diagnostics.Concat(startDiagnostics).ToArray();
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Error,
                    Message = "扩展会话启动失败：" + ex.Message
                });
                Diagnostics = diagnostics.ToArray();
            }
        }

        await ConnectPluginMcpAsync().ConfigureAwait(false);

        SetStatus(AvailableModels.Count == 0
            ? "未配置可用的服务商，请在 ~/.ai-tools/settings.json 中配置或设置 API Key 环境变量"
            : "就绪 · " + DescribeCurrentModel());
        RaiseStateChanged();
    }

    /// <summary>提交一条用户输入：命令先交给扩展/插件，其余作为普通消息发起一轮请求。</summary>
    /// <param name="text">用户输入。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本轮结果；命令或空输入返回 null。</returns>
    public async Task<TurnResult?> SubmitAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsBusy)
            {
                SetStatus("正在生成，请先按 Esc 停止。");
                return null;
            }

            var input = text.Trim();

            // input 事件：扩展可以观察、改写或吞掉原始输入。
            input = await DispatchInputAsync(input, cancellationToken).ConfigureAwait(false);
            if (input == null)
            {
                return null;
            }

            var command = await TryExecuteCommandAsync(input, cancellationToken).ConfigureAwait(false);
            if (command != null)
            {
                switch (command.Kind)
                {
                    case CommandOutcomeKind.Display:
                        AppendDisplay(command.DisplayText ?? string.Empty);
                        return null;
                    case CommandOutcomeKind.Noop:
                        PersistExtensionState();
                        return null;
                    case CommandOutcomeKind.Error:
                        Raise(new AgentUpdate
                        {
                            Kind = AgentUpdateKind.Notice,
                            Text = command.Error ?? "命令执行失败。",
                            NoticeKind = NoticeKind.Error
                        });
                        return null;
                    case CommandOutcomeKind.Prompt:
                        PersistExtensionState();
                        var result = await SendTurnAsync(command.Prompt!, command.CommandName, cancellationToken).ConfigureAwait(false);
                        await DrainContinuationsAsync(cancellationToken).ConfigureAwait(false);
                        return result;
                }
            }

            if (CurrentProvider == null || CurrentModel == null)
            {
                Raise(new AgentUpdate
                {
                    Kind = AgentUpdateKind.Notice,
                    Text = "请先选择可用的服务商和模型（/model 切换）。",
                    NoticeKind = NoticeKind.Warning
                });
                return null;
            }

            var turnResult = await SendTurnAsync(input, null, cancellationToken).ConfigureAwait(false);
            await DrainContinuationsAsync(cancellationToken).ConfigureAwait(false);
            await PersistAsync().ConfigureAwait(false);
            return turnResult;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>取消当前请求。</summary>
    public void CancelTurn()
    {
        try
        {
            _turnCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>开始新对话：清空历史、扩展状态与会话 ID。</summary>
    public async Task NewConversationAsync()
    {
        await PersistAsync().ConfigureAwait(false);
        lock (_history)
        {
            _history.Clear();
        }

        _conversation = null;
        _pipeline?.Runtime.State.ClearAll();
        _pipeline?.Session.UpdateSessionId(null);
        Title = "新对话";
        SetStatus("已开始新对话");
        RaiseStateChanged();
    }

    /// <summary>列出已保存的对话。</summary>
    public Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(CancellationToken cancellationToken = default)
        => _options.ConversationStore!.ListAsync(cancellationToken);

    /// <summary>恢复一个已保存的对话。</summary>
    /// <param name="id">对话 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> ResumeAsync(string id, CancellationToken cancellationToken = default)
    {
        var record = await _options.ConversationStore!.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            Raise(new AgentUpdate { Kind = AgentUpdateKind.Notice, Text = "该对话已不存在。", NoticeKind = NoticeKind.Warning });
            return false;
        }

        await PersistAsync().ConfigureAwait(false);
        ApplyConversation(record);
        SetStatus($"已恢复「{record.Title}」（{record.Messages.Count} 条消息）");
        RaiseStateChanged();
        return true;
    }

    /// <summary>从任意对话分叉：复制消息为新会话。</summary>
    /// <param name="id">源对话 ID；null 表示当前对话。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<bool> ForkAsync(string? id = null, CancellationToken cancellationToken = default)
    {
        await PersistAsync().ConfigureAwait(false);
        var source = _conversation;
        if (source == null)
        {
            Raise(new AgentUpdate { Kind = AgentUpdateKind.Notice, Text = "当前对话尚未保存，无法分叉。", NoticeKind = NoticeKind.Warning });
            return false;
        }

        var fork = await _options.ConversationStore!.ForkAsync(source, null, cancellationToken).ConfigureAwait(false);
        ApplyConversation(fork);
        SetStatus($"已分叉为「{fork.Title}」");
        RaiseStateChanged();
        return true;
    }

    /// <summary>删除一个已保存的对话。</summary>
    /// <param name="id">对话 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task DeleteConversationAsync(string id, CancellationToken cancellationToken = default)
        => _options.ConversationStore!.DeleteAsync(id, cancellationToken);

    /// <summary>切换模型。</summary>
    /// <param name="providerId">服务商 ID。</param>
    /// <param name="modelId">模型 ID。</param>
    public void SelectModel(string providerId, string modelId)
    {
        var provider = _settings?.Providers.FirstOrDefault(item =>
            string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
        var model = provider?.FindModel(modelId);
        if (provider == null || model == null)
        {
            return;
        }

        CurrentProvider = provider;
        CurrentModel = model;
        if (_settings != null)
        {
            _settings.SelectedProviderId = provider.Id;
            _settings.SelectedModelId = model.Id;
            _ = PersistSettingsAsync(_settings);
        }

        SetStatus("模型已切换为 " + DescribeCurrentModel());
        RaiseStateChanged();
    }

    /// <summary>选择输出风格；名称为空或不存在时清除。</summary>
    /// <param name="name">风格名。</param>
    public bool SelectOutputStyle(string? name)
    {
        var changed = _pipeline?.SelectOutputStyle(name) ?? false;
        if (changed)
        {
            RaiseStateChanged();
        }

        return changed;
    }

    /// <summary>重新扫描扩展包工作区。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ScanWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        if (_workspace == null)
        {
            return;
        }

        var packages = await _workspace.ScanAsync(cancellationToken).ConfigureAwait(false);
        var failed = packages.Count(package => !package.IsLoaded);
        SetStatus($"扩展包：{packages.Count} 个，失败 {failed} 个");
        RaiseStateChanged();
    }

    /// <summary>获取合并后的系统提示（不含本轮扩展注入）。</summary>
    public string DescribeCurrentModel()
    {
        var provider = CurrentProvider?.DisplayName;
        var model = CurrentModel?.GetDisplayName();
        return string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)
            ? "未选择模型"
            : provider + " · " + model;
    }

    /// <summary>获取当前环境变量兜底出的服务商（用于 <c>/model</c> 提示）。</summary>
    public IReadOnlyList<string> DescribeMissingConfiguration()
    {
        var missing = new List<string>();
        if (AvailableModels.Count == 0)
        {
            missing.Add("未发现可用的服务商配置。");
            missing.Add("在 ~/.ai-tools/settings.json 中配置服务商，或设置 OPENAI_API_KEY / ANTHROPIC_API_KEY / GEMINI_API_KEY 后重启。");
        }

        foreach (var diagnostic in Diagnostics.Where(item => item.Kind == PluginDiagnosticKind.Error).Take(5))
        {
            missing.Add(diagnostic.ToString());
        }

        return missing;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelTurn();
        await PersistAsync().ConfigureAwait(false);

        if (_pipeline != null)
        {
            try
            {
                await _pipeline.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (_workspace != null)
        {
            try
            {
                await _workspace.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        if (_pluginHost != null)
        {
            try
            {
                await _pluginHost.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _sendGate.Dispose();
    }

    // ------------------------------------------------------------------
    // 加载
    // ------------------------------------------------------------------

    private void LoadSharedAgents()
    {
        if (!_options.EnableSharedAgents)
        {
            return;
        }

        try
        {
            SharedAgents = AiTools.Skills.SharedAgents.Discover(
                WorkingDirectory,
                includeUser: _options.EnableUserSharedAgents,
                userDirectory: _options.SharedAgentsUserDirectory,
                includeNativeSkills: _options.EnableNativeSkills);
        }
        catch (Exception)
        {
            SharedAgents = null;
        }
    }

    private void LoadPlugins(List<PluginDiagnostic> diagnostics)
    {
        var hostOptions = new PluginHostOptions
        {
            ProjectDirectory = WorkingDirectory,
            EnableShellExecution = _options.AllowPluginShellExecution,
            PluginDataDirectory = AiTools.Core.AiToolsPaths.GetRootDirectory()
        };

        var catalog = _options.Plugins;
        if (catalog == null && _options.AutoDiscoverPlugins)
        {
            var trust = ProjectTrust.Evaluate(WorkingDirectory, _options.TrustedRoots, _options.RequireProjectTrust);
            if (!trust.IsTrusted)
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Info,
                    Path = WorkingDirectory,
                    Message = "项目目录未受信任，跳过项目级插件加载：" + (trust.Reason ?? "未声明受信任根")
                });
            }
            else
            {
                catalog = DiscoverPlugins(WorkingDirectory, diagnostics);
            }
        }

        if (catalog == null)
        {
            return;
        }

        diagnostics.AddRange(catalog.Diagnostics);
        _pluginHost = new PluginHost(catalog, hostOptions);
    }

    private PluginCatalog? DiscoverPlugins(string workingDirectory, List<PluginDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(_options.PluginsDirectory))
        {
            return PluginCatalog.LoadFromDirectory(_options.PluginsDirectory!, new PluginCatalogOptions
            {
                AdditionalDirectories = new[] { AiTools.Core.AiToolsPaths.GetRootDirectory() + Path.DirectorySeparatorChar + "plugins" }
            });
        }

        var candidates = new List<string>
        {
            Path.Combine(workingDirectory, ".claude", "plugins"),
            Path.Combine(workingDirectory, "plugins"),
            Path.Combine(AiTools.Core.AiToolsPaths.GetRootDirectory(), "plugins")
        };

        var existing = candidates.Where(Directory.Exists).ToArray();
        if (existing.Length == 0)
        {
            return null;
        }

        var plugins = new List<AiToolsPlugin>();
        foreach (var directory in existing)
        {
            var catalog = PluginCatalog.LoadFromDirectory(directory);
            plugins.AddRange(catalog.Plugins);
            diagnostics.AddRange(catalog.Diagnostics);
        }

        return plugins.Count == 0 ? null : PluginCatalog.FromPlugins(plugins);
    }

    private void LoadPipeline(List<PluginDiagnostic> diagnostics)
    {
        if (!_options.EnableExtensions)
        {
            return;
        }

        ExtensionWorkspace? workspace = null;
        if (_options.EnableExtensionPackages)
        {
            var directories = _options.ExtensionDirectories is { Count: > 0 } configured
                ? configured.ToArray()
                : ExtensionWorkspacePaths.ResolveDefaultRoots(WorkingDirectory);

            if (directories.Count > 0)
            {
                try
                {
                    workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions
                    {
                        Directories = directories,
                        DataDirectory = AiTools.Core.AiToolsPaths.GetExtensionDataDirectory(),
                        BuildOnLoad = true,
                        EnableDotnetBuild = true,
                        Watch = _options.WatchExtensions,
                        ScriptProvider = new ScriptExtensionProvider()
                    });
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new PluginDiagnostic
                    {
                        Kind = PluginDiagnosticKind.Warning,
                        Message = "扩展包工作区创建失败：" + ex.Message
                    });
                }
            }
        }

        _workspace = workspace;

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = _options.Extensions ?? new IExtension[]
            {
                new AiTools.Extensions.BuiltIn.AskExtension(),
                new AiTools.Extensions.BuiltIn.GoalExtension(),
                new AiTools.Extensions.BuiltIn.PlanExtension()
            },
            Plugins = _pluginHost?.Catalog,
            PluginHostOptions = _pluginHost?.Options,
            WorkingDirectory = WorkingDirectory,
            EnableWorkflows = _options.EnableWorkflows,
            OutputStyle = _options.OutputStyle,
            Permissions = _options.Permissions,
            Workspace = workspace,
            SharedSkills = SharedAgents?.Skills.Skills,
            WorkflowOptionsFactory = CreateWorkflowOptions
        });

        _pipeline = pipeline;
        diagnostics.AddRange(pipeline.ActivationDiagnostics);

        _router = new TerminalProviderRouter(pipeline.Runtime, _options.ProviderFactory);
    }

    private AiTools.Extensions.Workflows.WorkflowRuntimeOptions CreateWorkflowOptions()
    {
        var pipeline = _pipeline!;
        var sessionContext = pipeline.Session.CreateContext(null, null);
        return new AiTools.Extensions.Workflows.WorkflowRuntimeOptions
        {
            ToolInvoker = async (name, json) =>
                (await pipeline.Runtime.ExecuteToolAsync(name, json, sessionContext).ConfigureAwait(false)).Text,
            Log = message => SetStatus(message),
            Policy = _options.Permissions,
            Confirm = prompt => Prompt.ConfirmAsync("确认工作流工具调用", prompt, defaultValue: false)
        };
    }

    private async Task LoadWorkspaceAsync(List<PluginDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (_workspace == null)
        {
            BuildSkillProvider();
            return;
        }

        try
        {
            var packages = await _workspace.ScanAsync(cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(_workspace.Diagnostics);
            foreach (var package in packages)
            {
                diagnostics.AddRange(package.Diagnostics);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Add(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Error,
                Message = "扩展包扫描失败：" + ex.Message
            });
        }

        BuildSkillProvider();
    }

    private void BuildSkillProvider()
    {
        var pluginSkills = _pluginHost?.Catalog;
        var pipeline = _pipeline;
        var shared = SharedAgents;

        var hasExtensionSkills = pipeline != null && pipeline.Skills.Count > 0;
        var hasPluginSkills = pluginSkills != null && pluginSkills.Skills.Count > 0;
        var hasSharedSkills = shared != null && shared.Skills.Count > 0;
        if (!hasExtensionSkills && !hasPluginSkills && !hasSharedSkills)
        {
            return;
        }

        _skillProvider = new TerminalSkillProvider(
            () => TerminalSkillProvider.Merge(pluginSkills, shared, pipeline?.Skills));
        lock (_toolProviderGate)
        {
            if (!_registeredProviders.Contains(_skillProvider))
            {
                _registeredProviders.Add(_skillProvider);
            }
        }
    }

    private async Task ConnectPluginMcpAsync()
    {
        if (_pluginHost == null || _pluginHost.Catalog.McpServers.Count == 0)
        {
            return;
        }

        _mcpProvider = new PluginMcpToolProvider(_pluginHost);
        lock (_toolProviderGate)
        {
            _registeredProviders.Add(_mcpProvider);
        }

        try
        {
            await _mcpProvider.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 连接失败记录在 provider.LastErrors，不阻止对话。
        }
    }

    // ------------------------------------------------------------------
    // 请求
    // ------------------------------------------------------------------

    private async Task<TurnResult> SendTurnAsync(string prompt, string? displayText, CancellationToken cancellationToken)
    {
        var provider = CurrentProvider;
        var model = CurrentModel;
        if (provider == null || model == null)
        {
            return new TurnResult { Error = "请先选择可用的服务商和模型。" };
        }

        var started = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var textBuffer = new StringBuilder();
        var reasoningBuffer = new StringBuilder();
        var toolCallCount = 0;

        lock (_history)
        {
            _history.Add(new TranscriptMessage(ChatMessage.User(prompt), displayText));
        }

        IsBusy = true;
        _turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _turnCancellation.Token;

        Raise(new AgentUpdate { Kind = AgentUpdateKind.TurnStarted });
        SetStatus("正在生成…");

        try
        {
            if (_pipeline != null && !_pipeline.Session.IsStarted)
            {
                await _pipeline.StartAsync(_conversation?.Id, token).ConfigureAwait(false);
            }

            var tools = BuildToolRegistrations();
            var systemPrompt = BuildSystemPrompt(model);

            IReadOnlyList<ChatMessage> historySnapshot;
            lock (_history)
            {
                historySnapshot = _history.Select(entry => entry.Message).ToArray();
            }

            if (_pipeline != null)
            {
                var preparation = await _pipeline.PrepareTurnAsync(
                    CreateExtensionContext(),
                    model.Id,
                    token,
                    systemPrompt).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(preparation.SystemPromptOverride))
                {
                    systemPrompt = preparation.SystemPromptOverride!;
                }
                else if (!string.IsNullOrWhiteSpace(preparation.SystemPromptSections))
                {
                    systemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                        ? preparation.SystemPromptSections
                        : systemPrompt.TrimEnd() + "\n\n" + preparation.SystemPromptSections;
                }

                if (preparation.PrependMessages.Count > 0)
                {
                    historySnapshot = preparation.PrependMessages.Concat(historySnapshot).ToArray();
                }

                foreach (var tool in preparation.Tools)
                {
                    if (tools.All(item => !string.Equals(item.Definition.Name, tool.Definition.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        tools.Add(new ToolRegistration(tool.Definition, null));
                    }
                }

                var contextEvent = await _pipeline.ApplyContextHooksAsync(
                    CreateExtensionContext(),
                    historySnapshot,
                    systemPrompt,
                    model.Id,
                    token).ConfigureAwait(false);

                if (contextEvent.Messages != null)
                {
                    historySnapshot = contextEvent.Messages;
                }

                systemPrompt = contextEvent.SystemPrompt;
            }

            var sharedPrompt = SharedAgents?.BuildInstructionsPrompt();
            if (!string.IsNullOrWhiteSpace(sharedPrompt))
            {
                systemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                    ? sharedPrompt
                    : systemPrompt!.TrimEnd() + "\n\n" + sharedPrompt;
            }

            var skillPrompt = _skillProvider?.Toolset.BuildSystemPrompt();
            if (!string.IsNullOrWhiteSpace(skillPrompt))
            {
                systemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                    ? skillPrompt
                    : systemPrompt!.TrimEnd() + "\n\n" + skillPrompt;
            }

            var toolArray = tools.Select(item => item.Definition).ToArray();
            var request = new ChatRequest
            {
                Model = model.Id,
                SystemPrompt = systemPrompt,
                Messages = historySnapshot,
                Tools = toolArray,
                ToolChoice = toolArray.Length > 0 ? ToolChoice.Auto : null
            };

            var observer = _pipeline?.CreateHttpObserver(CreateExtensionContext);
            var client = _pipeline != null
                ? _router!.CreateChatClient(provider, model.Id, observer)
                : _options.ProviderFactory!.CreateChatClient(provider);

            async Task<string> InvokeToolAsync(ToolCall call, CancellationToken toolToken)
            {
                toolCallCount++;
                var toolWatch = System.Diagnostics.Stopwatch.StartNew();
                Raise(new AgentUpdate
                {
                    Kind = AgentUpdateKind.ToolStarted,
                    ToolCall = call
                });

                if (_pipeline != null)
                {
                    await _pipeline.Runtime.PublishAsync(
                        ExtensionEvent.ToolExecutionStart,
                        new ToolExecutionEvent
                        {
                            ToolName = call.Name,
                            ToolCallId = call.Id,
                            ArgumentsJson = call.ArgumentsJson
                        },
                        CreateExtensionContext(),
                        toolToken).ConfigureAwait(false);
                }

                var registration = tools.FirstOrDefault(item =>
                    string.Equals(item.Definition.Name, call.Name, StringComparison.OrdinalIgnoreCase));

                var callEvent = new ToolCallEvent
                {
                    ToolName = call.Name,
                    ArgumentsJson = call.ArgumentsJson
                };

                if (_pipeline != null)
                {
                    callEvent = await _pipeline
                        .DispatchHostToolCallAsync(callEvent, CreateExtensionContext(), toolToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(callEvent.BlockReason))
                    {
                        var blocked = JsonSerializer.Serialize(new { error = callEvent.BlockReason });
                        RaiseToolFinished(call, blocked, toolWatch.Elapsed, isError: true);
                        return blocked;
                    }

                    call = new ToolCall(call.Id, call.Name, callEvent.ArgumentsJson);
                }

                async Task<string> RunAsync()
                {
                    if (registration?.Provider != null)
                    {
                        return await registration.Provider.InvokeAsync(call, toolToken).ConfigureAwait(false) ?? string.Empty;
                    }

                    if (_pipeline?.Runtime.TryGetTool(call.Name) != null)
                    {
                        var toolContext = new ToolContext
                        {
                            Runtime = _pipeline.Runtime,
                            State = _pipeline.Runtime.State,
                            ParentToolName = null,
                            SessionId = _conversation?.Id,
                            WorkingDirectory = WorkingDirectory,
                            Ui = _pipeline.Ui,
                            EntryStore = _pipeline.Session.CreateContext(null, null).EntryStore
                        };

                        var toolResult = await _pipeline.Runtime
                            .InvokeToolAsync(call.Name, call.ArgumentsJson, toolContext, toolToken)
                            .ConfigureAwait(false);

                        return toolResult.Text;
                    }

                    return JsonSerializer.Serialize(new { error = $"未知工具：{call.Name}" });
                }

                string result;
                try
                {
                    var guarded = _pipeline?.GuardToolAsync(
                        call.Name,
                        call.ArgumentsJson,
                        RunAsync,
                        async promptText => Prompt.IsInteractive &&
                                            await Prompt.ConfirmAsync(
                                                "确认工具调用",
                                                promptText,
                                                defaultValue: false,
                                                cancellationToken: toolToken).ConfigureAwait(false));

                    result = guarded == null
                        ? await RunAsync().ConfigureAwait(false)
                        : await guarded.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = JsonSerializer.Serialize(new { error = ex.Message });
                }

                var isError = result.StartsWith("{\"error\"", StringComparison.Ordinal);

                if (_pipeline != null)
                {
                    var resultEvent = new ToolResultEvent
                    {
                        ToolName = call.Name,
                        Text = result,
                        IsError = isError
                    };
                    var transformed = await _pipeline.Runtime.DispatchAsync(
                        ExtensionEvent.ToolResult,
                        resultEvent,
                        CreateExtensionContext(),
                        failClosed: resultEvent,
                        toolToken).ConfigureAwait(false);

                    result = (transformed as ToolResultEvent ?? resultEvent).Text;
                    toolWatch.Stop();

                    await _pipeline.Runtime.PublishAsync(
                        ExtensionEvent.ToolExecutionEnd,
                        new ToolExecutionEvent
                        {
                            ToolName = call.Name,
                            ToolCallId = call.Id,
                            ArgumentsJson = call.ArgumentsJson,
                            ResultText = result,
                            Duration = toolWatch.Elapsed,
                            IsError = isError
                        },
                        CreateExtensionContext(),
                        toolToken).ConfigureAwait(false);
                }

                RaiseToolFinished(call, result, toolWatch.Elapsed, isError);
                return result;
            }

            ChatResponse response;
            if (toolArray.Length > 0)
            {
                response = await client.StreamWithToolsExactAsync(
                    request,
                    InvokeToolAsync,
                    onText: delta => AppendDelta(textBuffer, delta, reasoning: false),
                    onReasoning: delta => AppendDelta(reasoningBuffer, delta, reasoning: true),
                    maxToolRounds: _options.MaxToolRounds,
                    cancellationToken: token).ConfigureAwait(false);
            }
            else
            {
                var turn = await client
                    .StreamTurnAsync(
                        request,
                        update =>
                        {
                            if (update.TextDelta != null)
                            {
                                AppendDelta(textBuffer, update.TextDelta, reasoning: false);
                            }

                            if (update.ReasoningDelta != null)
                            {
                                AppendDelta(reasoningBuffer, update.ReasoningDelta, reasoning: true);
                            }
                        },
                        token)
                    .ConfigureAwait(false);

                response = new ChatResponse
                {
                    Message = turn.Message,
                    FinishReason = turn.FinishReason,
                    Usage = turn.Usage
                };
            }

            var text = textBuffer.ToString();
            if (text.Length == 0)
            {
                text = response.GetText();
            }

            lock (_history)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _history.Add(new TranscriptMessage(ChatMessage.Assistant(text)));
                }
            }

            TurnEndEvent? turnEnd = null;
            if (_pipeline != null)
            {
                turnEnd = await _pipeline.PublishTurnEndAsync(
                    CreateExtensionContext(),
                    text,
                    toolCallCount,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (turnEnd is { Continue: true, ContinuationPrompt: { Length: > 0 } continuation })
            {
                _pendingContinuation = continuation;
            }

            stopwatch.Stop();
            PersistExtensionState();

            var result = new TurnResult
            {
                Text = text,
                Reasoning = reasoningBuffer.ToString(),
                Usage = response.Usage,
                ToolCallCount = toolCallCount,
                Duration = stopwatch.Elapsed,
                FinishReason = response.FinishReason
            };

            Raise(new AgentUpdate
            {
                Kind = AgentUpdateKind.TurnCompleted,
                Text = text,
                Usage = response.Usage,
                Payload = result
            });

            SetStatus(response.Usage == null
                ? "完成"
                : $"完成 · {FormatUsage(response.Usage)} · {stopwatch.Elapsed.TotalSeconds:0.0}s");
            _ = started;
            return result;
        }
        catch (OperationCanceledException)
        {
            lock (_history)
            {
                RemoveLastUserTurn();
            }

            stopwatch.Stop();
            var partial = textBuffer.ToString();
            Raise(new AgentUpdate
            {
                Kind = AgentUpdateKind.TurnFailed,
                Error = "已停止生成。",
                Text = partial,
                Payload = new TurnResult
                {
                    Text = partial,
                    Reasoning = reasoningBuffer.ToString(),
                    ToolCallCount = toolCallCount,
                    Duration = stopwatch.Elapsed,
                    Cancelled = true
                }
            });
            SetStatus("已停止生成");
            return new TurnResult
            {
                Text = partial,
                Cancelled = true,
                ToolCallCount = toolCallCount,
                Duration = stopwatch.Elapsed
            };
        }
        catch (AiServiceException ex)
        {
            lock (_history)
            {
                RemoveLastUserTurn();
            }

            stopwatch.Stop();
            var message = DescribeServiceException(ex);
            Raise(new AgentUpdate
            {
                Kind = AgentUpdateKind.TurnFailed,
                Error = message,
                Text = textBuffer.ToString(),
                Payload = new TurnResult
                {
                    Text = textBuffer.ToString(),
                    Error = message,
                    ToolCallCount = toolCallCount,
                    Duration = stopwatch.Elapsed
                }
            });
            SetStatus("请求失败：" + message);
            return new TurnResult { Error = message, Text = textBuffer.ToString() };
        }
        catch (Exception ex)
        {
            lock (_history)
            {
                RemoveLastUserTurn();
            }

            stopwatch.Stop();
            Raise(new AgentUpdate
            {
                Kind = AgentUpdateKind.TurnFailed,
                Error = ex.Message,
                Text = textBuffer.ToString(),
                Payload = new TurnResult
                {
                    Text = textBuffer.ToString(),
                    Error = ex.Message,
                    ToolCallCount = toolCallCount,
                    Duration = stopwatch.Elapsed
                }
            });
            SetStatus("请求失败：" + ex.Message);
            return new TurnResult { Error = ex.Message, Text = textBuffer.ToString() };
        }
        finally
        {
            IsBusy = false;
            _turnCancellation?.Dispose();
            _turnCancellation = null;
            RaiseStateChanged();
        }
    }

    private void RaiseToolFinished(ToolCall call, string result, TimeSpan duration, bool isError)
        => Raise(new AgentUpdate
        {
            Kind = AgentUpdateKind.ToolFinished,
            ToolCall = call,
            ToolResult = result,
            Duration = duration,
            IsError = isError
        });

    private void AppendDelta(StringBuilder buffer, string delta, bool reasoning)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        buffer.Append(delta);
        Raise(reasoning ? AgentUpdate.ReasoningDelta(delta) : AgentUpdate.TextDelta(delta));
    }

    private async Task DrainContinuationsAsync(CancellationToken cancellationToken)
    {
        for (var round = 0; round < 3 && _pendingContinuation != null; round++)
        {
            var continuation = _pendingContinuation;
            _pendingContinuation = null;
            await SendTurnAsync(continuation, "/continue", cancellationToken).ConfigureAwait(false);
        }

        _pendingContinuation = null;
    }

    private string? BuildSystemPrompt(ModelConfig model)
    {
        var prompt = string.IsNullOrWhiteSpace(_settings?.SystemPrompt)
            ? _options.DefaultSystemPrompt
            : _settings!.SystemPrompt;

        var pluginPrompt = _pipeline?.BuildPluginCommandPrompt();
        if (!string.IsNullOrWhiteSpace(pluginPrompt))
        {
            prompt = string.IsNullOrWhiteSpace(prompt) ? pluginPrompt : prompt!.TrimEnd() + "\n\n" + pluginPrompt;
        }

        if (!string.IsNullOrWhiteSpace(_options.ExtraSystemPrompt))
        {
            prompt = string.IsNullOrWhiteSpace(prompt)
                ? _options.ExtraSystemPrompt
                : prompt!.TrimEnd() + "\n\n" + _options.ExtraSystemPrompt;
        }

        _ = model;
        return prompt ?? string.Empty;
    }

    private List<ToolRegistration> BuildToolRegistrations()
    {
        var registrations = new List<ToolRegistration>();
        if (!_options.EnableTools)
        {
            return registrations;
        }

        lock (_toolProviderGate)
        {
            foreach (var provider in _options.ToolProviders)
            {
                if (!_registeredProviders.Contains(provider))
                {
                    _registeredProviders.Add(provider);
                }
            }
        }

        IChatToolProvider[] providers;
        lock (_toolProviderGate)
        {
            providers = _registeredProviders.ToArray();
        }

        foreach (var provider in providers)
        {
            IReadOnlyList<ToolDefinition> definitions;
            try
            {
                definitions = provider.GetTools();
            }
            catch (Exception)
            {
                continue;
            }

            if (definitions == null)
            {
                continue;
            }

            foreach (var definition in definitions)
            {
                if (string.IsNullOrWhiteSpace(definition.Name) ||
                    registrations.Any(item => string.Equals(item.Definition.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                registrations.Add(ToolRegistration.FromProvider(definition, provider));
            }
        }

        return registrations;
    }

    // ------------------------------------------------------------------
    // 命令
    // ------------------------------------------------------------------

    private async Task<string?> DispatchInputAsync(string text, CancellationToken cancellationToken)
    {
        if (_pipeline == null)
        {
            return text;
        }

        var inputEvent = new InputEvent { Text = text };
        var dispatched = await _pipeline.Runtime.DispatchAsync(
            ExtensionEvent.Input,
            inputEvent,
            CreateExtensionContext(),
            failClosed: inputEvent,
            cancellationToken).ConfigureAwait(false);

        var resolved = dispatched as InputEvent ?? inputEvent;
        if (resolved.Suppress)
        {
            SetStatus("输入已被扩展拦截");
            return null;
        }

        return string.IsNullOrWhiteSpace(resolved.Text) ? null : resolved.Text;
    }

    private async Task<CommandOutcome?> TryExecuteCommandAsync(string text, CancellationToken cancellationToken)
    {
        if (_pipeline == null)
        {
            return null;
        }

        var execution = await _pipeline.ExecuteCommandAsync(text, CreateExtensionContext(), cancellationToken).ConfigureAwait(false);
        if (execution == null)
        {
            return null;
        }

        return execution.Kind switch
        {
            CommandExecutionKind.Error => new CommandOutcome
            {
                Kind = CommandOutcomeKind.Error,
                Error = execution.Error,
                CommandName = "/" + execution.Command.Name
            },
            CommandExecutionKind.Display => new CommandOutcome
            {
                Kind = CommandOutcomeKind.Display,
                DisplayText = execution.DisplayText,
                CommandName = "/" + execution.Command.Name
            },
            CommandExecutionKind.Noop => new CommandOutcome
            {
                Kind = CommandOutcomeKind.Noop,
                CommandName = "/" + execution.Command.Name
            },
            _ => new CommandOutcome
            {
                Kind = CommandOutcomeKind.Prompt,
                Prompt = execution.Prompt,
                CommandName = "/" + execution.Command.ShortName
            }
        };
    }

    private CommandContext CreateExtensionContext()
    {
        var session = _pipeline?.Session;
        if (session == null)
        {
            return new CommandContext
            {
                Runtime = new ExtensionRuntime(),
                State = new ExtensionStateStore(),
                WorkingDirectory = WorkingDirectory
            };
        }

        var context = session.CreateContext(
            (message, kind) => Raise(new AgentUpdate
            {
                Kind = AgentUpdateKind.Notice,
                Text = message,
                NoticeKind = kind switch
                {
                    ExtensionNoticeKind.Success => NoticeKind.Success,
                    ExtensionNoticeKind.Warning => NoticeKind.Warning,
                    ExtensionNoticeKind.Error => NoticeKind.Error,
                    _ => NoticeKind.Info
                }
            }),
            text => AppendDisplay(text));

        IReadOnlyList<ChatMessage> messages;
        lock (_history)
        {
            messages = _history.Select(entry => entry.Message).ToArray();
        }

        return new CommandContext
        {
            Runtime = context.Runtime,
            State = context.State,
            SessionId = _conversation?.Id,
            Messages = messages,
            WorkingDirectory = context.WorkingDirectory,
            Ui = context.Ui,
            Notify = context.Notify,
            AppendAssistantMessage = context.AppendAssistantMessage,
            EntryStore = context.EntryStore
        };
    }

    private void AppendDisplay(string text)
    {
        Raise(new AgentUpdate
        {
            Kind = AgentUpdateKind.DisplayText,
            Text = text
        });
    }

    // ------------------------------------------------------------------
    // 持久化
    // ------------------------------------------------------------------

    private async Task PersistAsync()
    {
        if (!_options.AutoSaveConversations || _options.ConversationStore == null)
        {
            return;
        }

        IReadOnlyList<TranscriptMessage> snapshot;
        lock (_history)
        {
            snapshot = _history.ToArray();
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        try
        {
            var record = _conversation ?? new ConversationRecord();
            record.ExtensionState = _pipeline?.ExportState();
            record.Messages = snapshot.Select(entry =>
            {
                var message = ConversationMessage.FromChatMessage(entry.Message);
                message.DisplayText = entry.DisplayText;
                return message;
            }).ToList();
            record.ProviderId = CurrentProvider?.Id ?? record.ProviderId;
            record.ModelId = CurrentModel?.Id ?? record.ModelId;
            record.Title = string.IsNullOrWhiteSpace(record.Title) || record.Title == "新对话"
                ? ConversationRecord.DeriveTitle(record.Messages)
                : record.Title;

            await _options.ConversationStore.SaveAsync(record).ConfigureAwait(false);
            _conversation = record;
            Title = record.Title;
            _pipeline?.Session.UpdateSessionId(record.Id);
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            SetStatus("保存对话失败：" + ex.Message);
        }
    }

    private async Task PersistSettingsAsync(AppSettings settings)
    {
        try
        {
            await _options.SettingsStore!.SaveAsync(settings).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 选择保存失败不影响当前会话。
        }
    }

    private void PersistExtensionState()
    {
        if (_conversation == null || _pipeline == null)
        {
            return;
        }

        _conversation.ExtensionState = _pipeline.ExportState();
    }

    private void ApplyConversation(ConversationRecord record)
    {
        lock (_history)
        {
            _history.Clear();
            foreach (var message in record.Messages)
            {
                if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _history.Add(new TranscriptMessage(message.ToChatMessage(), message.DisplayText));
            }
        }

        _conversation = record;
        Title = string.IsNullOrWhiteSpace(record.Title) ? "新对话" : record.Title;
        _pipeline?.ImportState(record.ExtensionState);
        _pipeline?.Session.UpdateSessionId(record.Id);

        if (!string.IsNullOrWhiteSpace(record.ProviderId))
        {
            var provider = _settings?.FindProvider(record.ProviderId);
            if (provider != null)
            {
                var model = string.IsNullOrWhiteSpace(record.ModelId) ? null : provider.FindModel(record.ModelId);
                CurrentProvider = provider;
                CurrentModel = model ?? provider.Models.FirstOrDefault(item => item.Enabled) ?? model;
            }
        }
    }

    private void RemoveLastUserTurn()
    {
        for (var index = _history.Count - 1; index >= 0; index--)
        {
            if (_history[index].Message.Role == ChatRole.User)
            {
                _history.RemoveRange(index, _history.Count - index);
                return;
            }
        }
    }

    /// <summary>重新读取设置（<c>/reload</c>）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ReloadSettingsAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _options.SettingsStore!.LoadAsync(cancellationToken).ConfigureAwait(false);
        AvailableModels = BuildModelOptions();
        SelectInitialModel(keepCurrent: true);
        SetStatus("设置已重新载入");
        RaiseStateChanged();
    }

    private List<ModelOption> BuildModelOptions()
    {
        var options = new List<ModelOption>();
        foreach (var provider in _settings?.Providers ?? new List<ProviderConfig>())
        {
            if (!provider.Enabled)
            {
                continue;
            }

            foreach (var model in provider.Models.Where(model => model.Enabled && !string.IsNullOrWhiteSpace(model.Id)))
            {
                options.Add(new ModelOption(provider, model));
            }
        }

        foreach (var virtualModel in _pipeline?.Runtime.VirtualModels ?? Array.Empty<VirtualModelRegistration>())
        {
            // 虚拟模型挂在第一条可用服务商下，便于在选择器里显示。
            var host = options.FirstOrDefault()?.Provider ?? _settings?.Providers.FirstOrDefault();
            if (host != null && !string.IsNullOrWhiteSpace(virtualModel.VirtualModelName))
            {
                options.Insert(0, new ModelOption(
                    host,
                    new ModelConfig { Id = virtualModel.VirtualModelName, DisplayName = virtualModel.Description ?? virtualModel.VirtualModelName, Enabled = true }));
            }
        }

        return options;
    }

    private void SelectInitialModel(bool keepCurrent = false)
    {
        if (keepCurrent && CurrentProvider != null && CurrentModel != null)
        {
            var stillThere = AvailableModels.FirstOrDefault(item =>
                string.Equals(item.Provider.Id, CurrentProvider.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Model.Id, CurrentModel.Id, StringComparison.OrdinalIgnoreCase));
            if (stillThere != null)
            {
                return;
            }
        }

        var preferredProvider = _options.ProviderId ?? _settings?.SelectedProviderId;
        var preferredModel = _options.ModelId ?? _settings?.SelectedModelId;

        var selection = AvailableModels.FirstOrDefault(item =>
                            string.Equals(item.Provider.Id, preferredProvider, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.Model.Id, preferredModel, StringComparison.OrdinalIgnoreCase))
                        ?? AvailableModels.FirstOrDefault(item =>
                            string.Equals(item.Provider.Id, preferredProvider, StringComparison.OrdinalIgnoreCase))
                        ?? AvailableModels.FirstOrDefault();

        if (selection == null)
        {
            TryApplyEnvironmentFallback();
            return;
        }

        CurrentProvider = selection.Provider;
        CurrentModel = selection.Model;
    }

    /// <summary>
    /// 没有配置文件时，按环境变量构造一个内存中的服务商，保证 <c>OPENAI_API_KEY</c> 这类常见用法可用。
    /// </summary>
    private void TryApplyEnvironmentFallback()
    {
        foreach (var descriptor in _options.Providers ?? ProviderDescriptor.BuiltIns)
        {
            var variable = descriptor.ApiKeyEnvironmentVariable;
            if (string.IsNullOrWhiteSpace(variable))
            {
                continue;
            }

            var key = Environment.GetEnvironmentVariable(variable!);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var modelId = Environment.GetEnvironmentVariable(variable!.Replace("_API_KEY", "_CHAT_MODEL"));
            if (string.IsNullOrWhiteSpace(modelId))
            {
                modelId = descriptor.DefaultModels.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            var provider = new ProviderConfig
            {
                Id = descriptor.Id,
                DisplayName = descriptor.DisplayName,
                Protocol = descriptor.Protocol,
                BaseUrl = descriptor.DefaultBaseUrl,
                ApiKey = key!,
                Enabled = true,
                Models = new List<ModelConfig> { new() { Id = modelId!, Enabled = true } }
            };

            _settings ??= new AppSettings();
            _settings.Providers.Add(provider);
            _settings.SelectedProviderId = provider.Id;
            _settings.SelectedModelId = modelId;

            AvailableModels = BuildModelOptions();
            CurrentProvider = provider;
            CurrentModel = provider.Models[0];
            SetStatus($"已从环境变量 {variable} 启用 {descriptor.DisplayName} · {modelId}");
            return;
        }
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private void Raise(AgentUpdate update) => Updates?.Invoke(update);

    private void SetStatus(string text)
    {
        Status = text;
        Raise(new AgentUpdate { Kind = AgentUpdateKind.Status, Text = text });
    }

    private void RaiseStateChanged()
        => Raise(new AgentUpdate
        {
            Kind = AgentUpdateKind.StateChanged,
            Payload = new SessionState
            {
                Title = Title,
                ConversationId = _conversation?.Id,
                MessageCount = History.Count,
                Provider = CurrentProvider,
                Model = CurrentModel,
                OutputStyle = SelectedStyle.Style?.Name,
                IsBusy = IsBusy,
                Models = AvailableModels
            }
        });

    private static string? FormatUsage(Usage usage)
    {
        if (usage.InputTokens.HasValue && usage.OutputTokens.HasValue)
        {
            return $"{usage.InputTokens.Value}→{usage.OutputTokens.Value} tokens";
        }

        if (usage.InputTokens.HasValue)
        {
            return $"输入 {usage.InputTokens.Value} tokens";
        }

        if (usage.OutputTokens.HasValue)
        {
            return $"输出 {usage.OutputTokens.Value} tokens";
        }

        return usage.TotalTokens.HasValue ? $"共 {usage.TotalTokens.Value} tokens" : null;
    }

    private static string DescribeServiceException(AiServiceException exception)
    {
        var details = new List<string>();
        if (exception.StatusCode.HasValue)
        {
            details.Add($"HTTP {exception.StatusCode.Value}");
        }

        if (!string.IsNullOrWhiteSpace(exception.ErrorCode))
        {
            details.Add($"错误码 {exception.ErrorCode}");
        }

        if (exception is AiRateLimitException rateLimit && rateLimit.RetryAfter.HasValue)
        {
            details.Add($"建议 {Math.Max(1, (int)Math.Ceiling(rateLimit.RetryAfter.Value.TotalSeconds))} 秒后重试");
        }

        var message = string.IsNullOrWhiteSpace(exception.Message) ? "服务商返回错误。" : exception.Message;
        if (details.Count > 0)
        {
            message += $"（{string.Join("，", details)}）";
        }

        return string.IsNullOrWhiteSpace(exception.ProviderName)
            ? message
            : $"{exception.ProviderName}：{message}";
    }

    private sealed class ToolRegistration
    {
        public ToolRegistration(ToolDefinition definition, IChatToolProvider? provider)
        {
            Definition = definition;
            Provider = provider;
        }

        public ToolDefinition Definition { get; }

        /// <summary>宿主注册的 provider；扩展工具为 null，执行时走扩展运行时。</summary>
        public IChatToolProvider? Provider { get; }

        public static ToolRegistration FromProvider(ToolDefinition definition, IChatToolProvider provider)
            => new(definition, provider);

        public static ToolRegistration FromExtension(ToolRegistration registration)
            => new(registration.Definition, null);
    }
}
