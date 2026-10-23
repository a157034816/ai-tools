using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Wpf.Configuration;

namespace AiTools.Wpf;

/// <summary>
/// AiTools WPF 组件的静态服务定位器。请在应用启动期间、创建任何页面之前
/// 调用一次 <see cref="Initialize(AiToolsWpfOptions?)"/>。
/// </summary>
/// <example>
/// <code>
/// protected override void OnStartup(StartupEventArgs e)
/// {
///     AiToolsWpf.Initialize(new AiToolsWpfOptions
///     {
///         DefaultSystemPrompt = "你是简洁的中文助手。"
///     });
///     base.OnStartup(e);
/// }
/// </code>
/// </example>
public static class AiToolsWpf
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly List<IChatToolProvider> ToolProviders = new();
    private static readonly object ExtensionGate = new();
    private static AppSettings? _settings;
    private static AiTools.Extensions.PluginHost? _pluginHost;
    private static AiTools.Extensions.ExtensionPipeline? _pipeline;
    private static Skills.PluginSkillChatToolProvider? _pluginSkillProvider;
    private static Skills.PluginMcpChatToolProvider? _pluginMcpProvider;

    /// <summary>获取 <see cref="Initialize(AiToolsWpfOptions?)"/> 是否已完成。</summary>
    public static bool IsInitialized => OptionsAccessor != null;

    /// <summary>
    /// 初始化各组件。必须在应用启动期间于 UI 线程上、创建任何页面之前调用一次。
    /// </summary>
    /// <param name="options">全局选项。未指定的成员将应用默认值。</param>
    /// <exception cref="AiToolsWpfException">组件已经初始化。</exception>
    public static void Initialize(AiToolsWpfOptions? options = null)
    {
        if (IsInitialized)
        {
            throw new AiToolsWpfException("AiToolsWpf 已经初始化，请勿重复调用 Initialize。");
        }

        options ??= new AiToolsWpfOptions();
        Configure(options.Normalize());
    }

    /// <summary>
    /// 初始化各组件。必须在应用启动期间于 UI 线程上、创建任何页面之前调用一次。
    /// </summary>
    /// <param name="configure">用于配置全局选项的回调。</param>
    public static void Initialize(Action<AiToolsWpfOptions> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new AiToolsWpfOptions();
        configure(options);
        Initialize(options);
    }

    /// <summary>获取当前生效的选项。组件未初始化时会抛出异常。</summary>
    public static AiToolsWpfOptions Options
    {
        get
        {
            var options = OptionsAccessor;
            if (options == null)
            {
                throw new AiToolsWpfException("AiToolsWpf 尚未初始化。请在应用启动时调用 AiToolsWpf.Initialize(options)。");
            }

            return options;
        }
    }

    /// <summary>获取设置页上提供的服务商模板。</summary>
    public static IReadOnlyList<ProviderDescriptor> ProviderDescriptors => Options.Providers!;

    /// <summary>获取当前已加载的设置。首次访问时会加载。</summary>
    public static async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var options = Options;
        if (_settings != null)
        {
            return _settings;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settings != null)
            {
                return _settings;
            }

            var loaded = await options.SettingsStore!.LoadAsync(cancellationToken).ConfigureAwait(false);
            Normalize(loaded);
            _settings = loaded;
            return loaded;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>替换内存中的设置，并通过已配置的存储保存。</summary>
    public static async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var options = Options;
        Normalize(settings);
        settings.Version = 1;
        await options.SettingsStore!.SaveAsync(settings, cancellationToken).ConfigureAwait(false);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings = settings;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>为给定的服务商配置获取一个已初始化的 chat 客户端。</summary>
    public static Abstractions.IChatClient CreateChatClient(ProviderConfig provider)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        return Options.ProviderFactory!.CreateChatClient(provider);
    }

    /// <summary>注册一个 Tool provider，由对话页向 model 暴露。</summary>
    public static void RegisterToolProvider(IChatToolProvider provider)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        lock (ToolProviders)
        {
            ToolProviders.Add(provider);
        }
    }

    /// <summary>注册一个 Tool provider，若同一实例已注册则忽略。用于按页面重复初始化的场景。</summary>
    public static void RegisterToolProviderOnce(IChatToolProvider provider)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        lock (ToolProviders)
        {
            if (!ToolProviders.Contains(provider))
            {
                ToolProviders.Add(provider);
            }
        }
    }

    /// <summary>获取已注册 Tool provider 的快照。</summary>
    public static IReadOnlyList<IChatToolProvider> GetToolProviders()
    {
        lock (ToolProviders)
        {
            return ToolProviders.ToArray();
        }
    }

    /// <summary>
    /// 获取插件 skill 的 Tool provider（模型可调用 <c>load_skill</c>）。
    /// 未配置插件或插件没有 skill 时返回 null；实例在整个进程中复用。
    /// </summary>
    public static Skills.PluginSkillChatToolProvider? PluginSkillProvider
    {
        get
        {
            var host = PluginHost;
            if (host == null || host.Catalog.Skills.Count == 0)
            {
                return null;
            }

            lock (ExtensionGate)
            {
                return _pluginSkillProvider ??= new Skills.PluginSkillChatToolProvider(host.Catalog);
            }
        }
    }

    /// <summary>
    /// 获取插件 MCP 服务器的 Tool provider。首次访问时在后台连接全部服务器；
    /// 未配置插件或插件没有 MCP 服务器时返回 null。
    /// </summary>
    public static Skills.PluginMcpChatToolProvider? PluginMcpProvider
    {
        get
        {
            var host = PluginHost;
            if (host == null || host.Catalog.McpServers.Count == 0)
            {
                return null;
            }

            lock (ExtensionGate)
            {
                if (_pluginMcpProvider == null)
                {
                    _pluginMcpProvider = new Skills.PluginMcpChatToolProvider(host);
                    _ = ConnectPluginMcpAsync(_pluginMcpProvider);
                }

                return _pluginMcpProvider;
            }
        }
    }

    private static async Task ConnectPluginMcpAsync(Skills.PluginMcpChatToolProvider provider)
    {
        try
        {
            await provider.ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 连接失败记录在 provider.LastErrors 中，不影响对话。
        }
    }

    /// <summary>获取对话页的恢复/分叉功能所使用的会话存储。</summary>
    public static Core.Sessions.IConversationStore ConversationStore => Options.ConversationStore!;

    /// <summary>
    /// 获取插件宿主。未配置插件目录/<see cref="AiToolsWpfOptions.Extensions"/> 时返回 null。
    /// 首次访问时发现插件并合并插件 skill 到系统提示词所需的目录。
    /// </summary>
    public static AiTools.Extensions.PluginHost? PluginHost
    {
        get
        {
            var options = Options;
            if (options.Plugins == null && options.PluginsDirectory != null)
            {
                options.Plugins = AiTools.Extensions.PluginCatalog.LoadFromDirectory(options.PluginsDirectory);
            }

            if (options.Plugins == null)
            {
                return null;
            }

            var hostOptions = options.PluginHostOptions ?? new AiTools.Extensions.PluginHostOptions();
            hostOptions.EnableShellExecution = options.AllowPluginShellExecution;

            return _pluginHost != null && ReferenceEquals(_pluginHost.Catalog, options.Plugins)
                ? _pluginHost
                : _pluginHost = new AiTools.Extensions.PluginHost(options.Plugins, hostOptions);
        }
    }

    /// <summary>获取插件提供、可直接调用的斜杠命令。</summary>
    public static IReadOnlyList<AiTools.Extensions.PluginCommand> GetPluginCommands()
        => PluginHost?.Catalog.Commands ?? Array.Empty<AiTools.Extensions.PluginCommand>();

    /// <summary>
    /// 获取扩展管道：合并扩展命令、插件命令、扩展工具与插件 MCP 服务器。
    /// 扩展（如 <c>/goal</c>、<c>/plan</c>）默认启用，因此该属性在配置插件之前也不为 null。
    /// </summary>
    public static AiTools.Extensions.ExtensionPipeline? Pipeline
    {
        get
        {
            var options = Options;
            if (options.EnableExtensions == false)
            {
                return null;
            }

            lock (ExtensionGate)
            {
                if (_pipeline != null)
                {
                    return _pipeline;
                }

                var pluginHost = PluginHost;
                var hostOptions = options.PluginHostOptions ?? new AiTools.Extensions.PluginHostOptions();
                hostOptions.EnableShellExecution = options.AllowPluginShellExecution;

                _pipeline = new AiTools.Extensions.ExtensionPipeline(new AiTools.Extensions.ExtensionPipelineOptions
                {
                    Extensions = options.Extensions ?? CreateDefaultExtensions(),
                    Plugins = pluginHost?.Catalog,
                    PluginHostOptions = hostOptions,
                    WorkingDirectory = hostOptions.ProjectDirectory ?? Environment.CurrentDirectory
                });

                _ = _pipeline.StartAsync();
                return _pipeline;
            }
        }
    }

    private static IReadOnlyList<AiTools.Extensions.IExtension> CreateDefaultExtensions()
        => new AiTools.Extensions.IExtension[]
        {
            new AiTools.Extensions.BuiltIn.GoalExtension(),
            new AiTools.Extensions.BuiltIn.PlanExtension()
        };

    /// <summary>创建默认的设置页。</summary>
    public static Views.SettingsPage CreateSettingsPage() => new();

    /// <summary>创建默认的对话页。</summary>
    public static Views.ChatPage CreateChatPage() => new();

    internal static void Configure(AiToolsWpfOptions options)
    {
        OptionsAccessor = options;
        _settings = null;
        ResetExtensions();
        lock (ToolProviders)
        {
            ToolProviders.Clear();
        }
    }

    /// <summary>丢弃所有缓存状态。用于测试和宿主关闭场景。</summary>
    public static void Reset()
    {
        OptionsAccessor = null;
        _settings = null;
        ResetExtensions();
        lock (ToolProviders)
        {
            ToolProviders.Clear();
        }
    }

    private static AiToolsWpfOptions? OptionsAccessor { get; set; }

    private static void ResetExtensions()
    {
        AiTools.Extensions.PluginHost? host;
        AiTools.Extensions.ExtensionPipeline? pipeline;
        lock (ExtensionGate)
        {
            host = _pluginHost;
            pipeline = _pipeline;
            _pluginHost = null;
            _pipeline = null;
            _pluginSkillProvider = null;
            _pluginMcpProvider = null;
        }

        // MCP 子进程与扩展会话需要在重置时关闭；这里是同步路径，交给后台任务完成。
        if (pipeline != null)
        {
            _ = pipeline.ShutdownAsync();
        }
        else if (host != null)
        {
            _ = host.DisposeAsync();
        }
    }

    private static void Normalize(AppSettings settings)
    {
        settings.Providers ??= new List<ProviderConfig>();
        foreach (var provider in settings.Providers)
        {
            provider.Models ??= new List<ModelConfig>();
            if (string.IsNullOrWhiteSpace(provider.Protocol))
            {
                provider.Protocol = ProviderProtocols.OpenAi;
            }
        }
    }
}
