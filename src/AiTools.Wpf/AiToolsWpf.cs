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
    private static AppSettings? _settings;

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

    /// <summary>获取已注册 Tool provider 的快照。</summary>
    public static IReadOnlyList<IChatToolProvider> GetToolProviders()
    {
        lock (ToolProviders)
        {
            return ToolProviders.ToArray();
        }
    }

    /// <summary>获取对话页的恢复/分叉功能所使用的会话存储。</summary>
    public static Core.Sessions.IConversationStore ConversationStore => Options.ConversationStore!;

    /// <summary>创建默认的设置页。</summary>
    public static Views.SettingsPage CreateSettingsPage() => new();

    /// <summary>创建默认的对话页。</summary>
    public static Views.ChatPage CreateChatPage() => new();

    internal static void Configure(AiToolsWpfOptions options)
    {
        OptionsAccessor = options;
        _settings = null;
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
        lock (ToolProviders)
        {
            ToolProviders.Clear();
        }
    }

    private static AiToolsWpfOptions? OptionsAccessor { get; set; }

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
