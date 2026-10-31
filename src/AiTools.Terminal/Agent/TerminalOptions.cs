using System;
using System.Collections.Generic;
using AiTools.Abstractions;
using AiTools.Configuration;

namespace AiTools.Terminal.Agent;

/// <summary>终端宿主的运行选项。与 <c>AiToolsWpfOptions</c> 对应，但完全跨平台、无 UI 框架依赖。</summary>
public sealed class TerminalOptions
{
    /// <summary>设置存储。默认为 <c>~/.ai-tools/settings.json</c>。</summary>
    public ISettingsStore? SettingsStore { get; set; }

    /// <summary>密钥保护器。默认为 Windows DPAPI（其他平台退化为明文）。</summary>
    public ISecretProtector? SecretProtector { get; set; }

    /// <summary>服务商工厂。默认为 <see cref="BuiltInProviderFactory"/>。</summary>
    public IProviderFactory? ProviderFactory { get; set; }

    /// <summary>服务商模板。默认为 <see cref="ProviderDescriptor.BuiltIns"/>。</summary>
    public IReadOnlyList<ProviderDescriptor>? Providers { get; set; }

    /// <summary>新会话的默认 system prompt。</summary>
    public string? DefaultSystemPrompt { get; set; }

    /// <summary>强制使用的服务商 ID；null 时使用设置里的选择。</summary>
    public string? ProviderId { get; set; }

    /// <summary>强制使用的模型 ID；null 时使用设置里的选择。</summary>
    public string? ModelId { get; set; }

    /// <summary>是否向模型发送工具。默认 true。</summary>
    public bool EnableTools { get; set; } = true;

    /// <summary>单轮请求内允许的自动工具调用轮数。默认 24。</summary>
    public int MaxToolRounds { get; set; } = 24;

    /// <summary>是否启用可编程扩展（<c>/goal</c>、<c>/plan</c>、<c>/ask</c> 等）。默认 true。</summary>
    public bool EnableExtensions { get; set; } = true;

    /// <summary>要激活的扩展；null 时使用内置扩展。</summary>
    public IReadOnlyList<AiTools.Extensions.IExtension>? Extensions { get; set; }

    /// <summary>插件目录（Claude Code 兼容）。默认读取 <c>~/.ai-tools/plugins</c> 与 <c>&lt;project&gt;/.claude/plugins</c>。</summary>
    public string? PluginsDirectory { get; set; }

    /// <summary>已加载的插件目录；优先于 <see cref="PluginsDirectory"/>。</summary>
    public AiTools.Extensions.PluginCatalog? Plugins { get; set; }

    /// <summary>是否自动发现插件。默认 true；配合 <see cref="PluginsDirectory"/> 为 null 时读取默认位置。</summary>
    public bool AutoDiscoverPlugins { get; set; } = true;

    /// <summary>扩展包工作区根目录；null 时使用 <c>~/.ai-tools/extensions</c>。</summary>
    public IReadOnlyList<string>? ExtensionDirectories { get; set; }

    /// <summary>是否加载磁盘上的扩展包。默认 true。</summary>
    public bool EnableExtensionPackages { get; set; } = true;

    /// <summary>是否监视扩展包目录并热重载。默认 false。</summary>
    public bool WatchExtensions { get; set; }

    /// <summary>插件与工具的工作目录。默认进程当前目录。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>是否读取共享 <c>.agents</c> 目录（技能 + <c>AGENTS.md</c>）。默认 true。</summary>
    public bool EnableSharedAgents { get; set; } = true;

    /// <summary>是否包含用户级 <c>~/.agents</c>。默认 true。</summary>
    public bool EnableUserSharedAgents { get; set; } = true;

    /// <summary>覆盖用户级共享目录（主要用于测试）。</summary>
    public string? SharedAgentsUserDirectory { get; set; }

    /// <summary>是否读取 AiTools 专属技能目录 <c>~/.ai-tools/skills</c>。默认 true。</summary>
    public bool EnableNativeSkills { get; set; } = true;

    /// <summary>工具调用权限策略；null 时不启用权限闸门。</summary>
    public AiTools.Extensions.PermissionPolicy? Permissions { get; set; }

    /// <summary>是否要求项目目录位于受信任根下才加载项目级插件。默认 false。</summary>
    public bool RequireProjectTrust { get; set; }

    /// <summary>受信任的项目根目录。</summary>
    public IReadOnlyList<string>? TrustedRoots { get; set; }

    /// <summary>是否允许插件命令执行 <c>!`cmd`</c> 动态注入。默认 true（CLI 用户已在本机终端中）。</summary>
    public bool AllowPluginShellExecution { get; set; } = true;

    /// <summary>初始输出风格名。</summary>
    public string? OutputStyle { get; set; }

    /// <summary>是否启用工作流。默认 true。</summary>
    public bool EnableWorkflows { get; set; } = true;

    /// <summary>会话存储；默认为 <c>~/.ai-tools/conversations</c>。</summary>
    public AiTools.Core.Sessions.IConversationStore? ConversationStore { get; set; }

    /// <summary>是否自动保存会话。默认 true。</summary>
    public bool AutoSaveConversations { get; set; } = true;

    /// <summary>宿主注册的工具 provider。</summary>
    public IReadOnlyList<IChatToolProvider> ToolProviders { get; set; } = Array.Empty<IChatToolProvider>();

    /// <summary>是否使用备用屏幕。默认 true；在 IDE 内嵌终端中可关闭。</summary>
    public bool UseAlternateScreen { get; set; } = true;

    /// <summary>是否显示推理过程（reasoning）。默认 true。</summary>
    public bool ShowReasoning { get; set; } = true;

    /// <summary>是否使用 ANSI 全屏界面。默认根据终端能力自动判断；关闭时退化为行式 REPL。</summary>
    public bool? UseFullScreen { get; set; }

    /// <summary>应用的显示名称（头部标题）。默认 <c>AiTools</c>。</summary>
    public string ApplicationName { get; set; } = "AiTools";

    /// <summary>版本号文本（头部与 <c>--version</c> 使用）。</summary>
    public string? Version { get; set; }

    /// <summary>启动时额外注入的系统提示段落。</summary>
    public string? ExtraSystemPrompt { get; set; }

    /// <summary>获取环境变量兜底时使用的服务商标识（例如 <c>openai</c>）。</summary>
    public string? PreferredEnvProvider { get; set; }

    /// <summary>校验选项并填充默认值。</summary>
    internal TerminalOptions Normalize()
    {
        SecretProtector ??= new DpapiSecretProtector();
        SettingsStore ??= new JsonFileSettingsStore(SecretProtector);
        ProviderFactory ??= new BuiltInProviderFactory();
        Providers ??= ProviderDescriptor.BuiltIns;
        ConversationStore ??= new AiTools.Core.Sessions.JsonConversationStore(AiTools.Core.AiToolsPaths.GetConversationsRoot());
        MaxToolRounds = Math.Max(0, MaxToolRounds);
        return this;
    }
}
