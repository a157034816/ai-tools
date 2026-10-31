using System;
using System.Collections.Generic;
using AiTools.Core.Sessions;
using AiTools.Configuration;

namespace AiTools.Wpf;

/// <summary>
/// AiTools WPF 组件的全局配置。请在应用启动期间向 <c>AiToolsWpf.Initialize</c> 传入一个实例；
/// 每个页面和 ViewModel 都从中读取设置。
/// </summary>
public sealed class AiToolsWpfOptions
{
    /// <summary>设置存储。默认为 <c>~/.ai-tools</c> 下的 <see cref="JsonFileSettingsStore"/>。</summary>
    public ISettingsStore? SettingsStore { get; set; }

    /// <summary>服务商工厂。默认为 <see cref="BuiltInProviderFactory"/>。</summary>
    public IProviderFactory? ProviderFactory { get; set; }

    /// <summary>
    /// 可选的模型列表工厂。为 null 时，若 <see cref="ProviderFactory"/> 实现了
    /// <see cref="IProviderModelCatalogFactory"/> 则复用它；内置工厂默认支持。
    /// </summary>
    public IProviderModelCatalogFactory? ProviderModelCatalogFactory { get; set; }

    /// <summary>默认 JSON 存储所使用的密钥保护器。默认为 Windows DPAPI。</summary>
    public ISecretProtector? SecretProtector { get; set; }

    /// <summary>设置页上提供的服务商模板。默认为 <see cref="ProviderDescriptor.BuiltIns"/>。</summary>
    public IReadOnlyList<ProviderDescriptor>? Providers { get; set; }

    /// <summary>新会话的默认 system prompt。</summary>
    public string? DefaultSystemPrompt { get; set; }

    /// <summary>是否将已注册 <see cref="IChatToolProvider"/> 实例提供的 Tool 定义发送给 model。</summary>
    public bool EnableTools { get; set; } = true;

    /// <summary>每条用户消息最多允许的自动 Tool 调用轮数。</summary>
    public int MaxToolRounds { get; set; } = 8;

    /// <summary>可选钩子，用于构建 chat 请求；返回 null 表示使用默认构建器。</summary>
    public Func<ChatRequestContext, AiTools.Abstractions.ChatRequest?>? RequestBuilder { get; set; }

    /// <summary>可选钩子，在每次助手回复后调用，用于日志记录或遥测。</summary>
    public Action<ChatTurnCompletedContext>? OnTurnCompleted { get; set; }

    /// <summary>
    /// 对话页的恢复/分叉功能所使用的会话存储。默认为
    /// <c>~/.ai-tools/conversations</c> 下的 <see cref="JsonConversationStore"/>。
    /// 如需临时会话，请设置为 <see cref="InMemoryConversationStore"/>。
    /// </summary>
    public IConversationStore? ConversationStore { get; set; }

    /// <summary>对话页是否在每轮结束后自动保存会话。默认为 true。</summary>
    public bool AutoSaveConversations { get; set; } = true;

    /// <summary>是否启用可编程扩展（<c>/goal</c>、<c>/plan</c> 等）。默认为 true。</summary>
    public bool EnableExtensions { get; set; } = true;

    /// <summary>
    /// 要激活的扩展。为 null 时使用内置的 <c>AskExtension</c>、<c>GoalExtension</c> 与 <c>PlanExtension</c>；
    /// 设为空数组可只保留插件命令。
    /// </summary>
    public IReadOnlyList<AiTools.Extensions.IExtension>? Extensions { get; set; }

    /// <summary>
    /// 提问（<c>ask</c> 工具与 <c>ctx.Ui.ChooseAsync</c>）的呈现方式。
    /// 为 null 时使用对话页输入框上方的内联面板。
    /// 换成自定义实现即可改成模态对话框、命令行提示或固定答案。
    /// </summary>
    public Asking.IAskPresenter? AskPresenter { get; set; }

    /// <summary>
    /// 是否读取多个 agent 工具共用的 <c>.agents</c> 目录（项目级 <c>&lt;project&gt;/.agents</c>
    /// 与用户级 <c>~/.agents</c>）。默认 true。
    /// </summary>
    /// <remarks>
    /// 共享目录遵循 Pi / Codex 的约定：<c>skills/&lt;name&gt;/SKILL.md</c> 是技能，
    /// 根部的 <c>AGENTS.md</c> 是 agent 指令。项目级定义覆盖同名用户级定义。
    /// 用户级技能与 <c>AGENTS.md</c> 会被合并进 <see cref="DefaultSystemPrompt"/> 之前注入的清单。
    /// 此外还会读取 AiTools 专属的 <c>~/.ai-tools/skills</c> 与 <c>~/.ai-tools/AGENTS.md</c>，
    /// 它们比 <c>~/.agents</c> 更具体，同名时优先。
    /// </remarks>
    public bool EnableSharedAgents { get; set; } = true;

    /// <summary>
    /// 是否包含用户级 <c>~/.agents</c>。设为 false 只读取项目级共享目录，
    /// 适合不希望读取开发者主目录的受限宿主。
    /// </summary>
    public bool EnableUserSharedAgents { get; set; } = true;

    /// <summary>覆盖共享目录的用户级根路径；为 null 时使用 <c>~/.agents</c>。主要用于测试。</summary>
    public string? SharedAgentsUserDirectory { get; set; }

    /// <summary>
    /// 插件目录（Claude Code 兼容）。目录本身是插件时按单个插件加载，否则扫描其子目录。
    /// 为 null 时不加载任何插件。
    /// </summary>
    public string? PluginsDirectory { get; set; }

    /// <summary>已加载的插件；为 null 时由 <see cref="PluginsDirectory"/> 发现。宿主可直接传入自建目录。</summary>
    public AiTools.Extensions.PluginCatalog? Plugins { get; set; }

    /// <summary>插件斜杠命令的行为选项。为 null 时使用默认值。</summary>
    public AiTools.Extensions.PluginHostOptions? PluginHostOptions { get; set; }

    /// <summary>是否允许插件命令执行正文中的 <c>!`cmd`</c> 动态上下文注入。默认为 false（WPF 宿主更保守）。</summary>
    public bool AllowPluginShellExecution { get; set; }

    /// <summary>是否要求项目目录位于显式受信任根下后才加载项目插件。默认为 false。</summary>
    public bool RequireProjectTrust { get; set; }

    /// <summary>项目插件加载所允许的受信任目录根。</summary>
    public IReadOnlyList<string>? TrustedRoots { get; set; }

    /// <summary>要初始选择的插件输出风格名称。</summary>
    public string? OutputStyle { get; set; }

    /// <summary>工具调用权限策略；为 null 时不启用权限闸门。</summary>
    public AiTools.Extensions.PermissionPolicy? Permissions { get; set; }

    /// <summary>是否激活插件工作流。默认为 true。</summary>
    public bool EnableWorkflows { get; set; } = true;

    /// <summary>校验选项并填充默认值。</summary>
    internal AiToolsWpfOptions Normalize()
    {
        SecretProtector ??= new DpapiSecretProtector();
        SettingsStore ??= new JsonFileSettingsStore(SecretProtector);
        ProviderFactory ??= new BuiltInProviderFactory();
        Providers ??= ProviderDescriptor.BuiltIns;
        ConversationStore ??= new JsonConversationStore(AiTools.Core.AiToolsPaths.GetConversationsRoot());

        if (MaxToolRounds < 0)
        {
            MaxToolRounds = 0;
        }

        PluginHostOptions ??= new AiTools.Extensions.PluginHostOptions();
        return this;
    }
}

/// <summary>传递给 <see cref="AiToolsWpfOptions.RequestBuilder"/> 的上下文。</summary>
public sealed class ChatRequestContext
{
    /// <summary>处理本轮请求的服务商。</summary>
    public ProviderConfig Provider { get; init; } = new();

    /// <summary>处理本轮请求的 model。</summary>
    public ModelConfig Model { get; init; } = new();

    /// <summary>完整的对话历史，最旧的在前。</summary>
    public IReadOnlyList<Abstractions.ChatMessage> Messages { get; init; } = Array.Empty<Abstractions.ChatMessage>();

    /// <summary>将向 model 提供的 Tool 列表。</summary>
    public IReadOnlyList<Abstractions.ToolDefinition> Tools { get; init; } = Array.Empty<Abstractions.ToolDefinition>();
}

/// <summary>传递给 <see cref="AiToolsWpfOptions.OnTurnCompleted"/> 的上下文。</summary>
public sealed class ChatTurnCompletedContext
{
    /// <summary>处理本轮请求的服务商。</summary>
    public ProviderConfig Provider { get; init; } = new();

    /// <summary>处理本轮请求的 model。</summary>
    public ModelConfig Model { get; init; } = new();

    /// <summary>已完成的助手响应。</summary>
    public Abstractions.ChatResponse Response { get; init; } = new();

    /// <summary>本轮请求的实际耗时。</summary>
    public TimeSpan Duration { get; init; }
}
