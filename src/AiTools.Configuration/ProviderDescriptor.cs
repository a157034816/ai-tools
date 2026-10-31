using System;
using System.Collections.Generic;

namespace AiTools.Configuration;

/// <summary>
/// 描述宿主可在设置页展示的服务商模板。
/// 内置模板涵盖 OpenAI、Anthropic、Gemini 以及常见的 OpenAI 兼容 endpoint。
/// </summary>
public sealed class ProviderDescriptor
{
    /// <summary>稳定标识符，例如 <c>openai</c> 或 <c>deepseek</c>。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>添加服务商时显示的展示名称。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>通信协议；参见 <see cref="ProviderProtocols"/>。</summary>
    public string Protocol { get; init; } = ProviderProtocols.OpenAi;

    /// <summary>默认 API base URL。</summary>
    public string DefaultBaseUrl { get; init; } = string.Empty;

    /// <summary>添加服务商时预填充的模型。</summary>
    public IReadOnlyList<string> DefaultModels { get; init; } = Array.Empty<string>();

    /// <summary>在设置页中显示的可选描述。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// 读取 API Key 的环境变量名。CLI 等非交互宿主在配置缺失时用它兜底；
    /// 为空表示该模板没有约定环境变量。
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; init; }

    /// <summary>默认的服务商模板。宿主可通过自己的选项替换此列表。</summary>
    public static IReadOnlyList<ProviderDescriptor> BuiltIns { get; } = new ProviderDescriptor[]
    {
        new()
        {
            Id = "openai",
            DisplayName = "OpenAI",
            Protocol = ProviderProtocols.OpenAi,
            DefaultBaseUrl = "https://api.openai.com/v1",
            DefaultModels = new[] { "gpt-4.1", "gpt-4.1-mini", "gpt-4o", "o3" },
            Description = "OpenAI 官方 API",
            ApiKeyEnvironmentVariable = "OPENAI_API_KEY"
        },
        new()
        {
            Id = "anthropic",
            DisplayName = "Anthropic Claude",
            Protocol = ProviderProtocols.Anthropic,
            DefaultBaseUrl = "https://api.anthropic.com/v1",
            DefaultModels = new[] { "claude-sonnet-4-5", "claude-opus-4-1", "claude-haiku-4-5" },
            Description = "Claude Messages API",
            ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY"
        },
        new()
        {
            Id = "gemini",
            DisplayName = "Google Gemini",
            Protocol = ProviderProtocols.Gemini,
            DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            DefaultModels = new[] { "gemini-2.5-flash", "gemini-2.5-pro" },
            Description = "Google Gemini API",
            ApiKeyEnvironmentVariable = "GEMINI_API_KEY"
        },
        new()
        {
            Id = "deepseek",
            DisplayName = "DeepSeek",
            Protocol = ProviderProtocols.OpenAi,
            DefaultBaseUrl = "https://api.deepseek.com/v1",
            DefaultModels = new[] { "deepseek-chat", "deepseek-reasoner" },
            Description = "OpenAI 兼容端点，支持 reasoning_content",
            ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY"
        },
        new()
        {
            Id = "moonshot",
            DisplayName = "Moonshot Kimi",
            Protocol = ProviderProtocols.OpenAi,
            DefaultBaseUrl = "https://api.moonshot.cn/v1",
            DefaultModels = new[] { "kimi-k2-0711-preview", "moonshot-v1-8k" },
            Description = "OpenAI 兼容端点",
            ApiKeyEnvironmentVariable = "MOONSHOT_API_KEY"
        },
        new()
        {
            Id = "qwen",
            DisplayName = "通义千问",
            Protocol = ProviderProtocols.OpenAi,
            DefaultBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            DefaultModels = new[] { "qwen-max", "qwen-plus", "qwen-turbo" },
            Description = "DashScope OpenAI 兼容模式",
            ApiKeyEnvironmentVariable = "DASHSCOPE_API_KEY"
        },
        new()
        {
            Id = "ollama",
            DisplayName = "Ollama 本地",
            Protocol = ProviderProtocols.OpenAi,
            DefaultBaseUrl = "http://localhost:11434/v1",
            DefaultModels = new[] { "llama3.3", "qwen2.5", "deepseek-r1" },
            Description = "本地模型，无需 API Key"
        },
        new()
        {
            Id = "custom",
            DisplayName = "自定义服务商",
            Protocol = ProviderProtocols.OpenAi,
            DefaultBaseUrl = string.Empty,
            DefaultModels = Array.Empty<string>(),
            Description = "任意 OpenAI 兼容网关"
        }
    };
}
