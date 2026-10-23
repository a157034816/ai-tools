using System;
using System.Collections.Generic;
using System.Linq;

namespace AiTools.Wpf.Configuration;

/// <summary>内置服务商工厂支持的通信协议。</summary>
public static class ProviderProtocols
{
    /// <summary>OpenAI Chat Completions 协议，也适用于 OpenAI 兼容 endpoint。</summary>
    public const string OpenAi = "openai";

    /// <summary>Anthropic Messages 协议。</summary>
    public const string Anthropic = "anthropic";

    /// <summary>Google Gemini generateContent 协议。</summary>
    public const string Gemini = "gemini";

    /// <summary>内置工厂支持的所有协议。</summary>
    public static IReadOnlyList<string> All { get; } = new[] { OpenAi, Anthropic, Gemini };
}

/// <summary>为服务商配置的一个 model 条目。</summary>
public sealed class ModelConfig
{
    /// <summary>发送给服务商 API 的 model 标识符，例如 <c>gpt-4.1</c>。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>在 UI 中显示的可选名称。为空时回退到 <see cref="Id"/>。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>是否在对话页的 model 选择器中提供。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>创建深拷贝。</summary>
    public ModelConfig Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Enabled = Enabled
    };

    /// <summary>获取用于在 UI 中显示的名称。</summary>
    public string GetDisplayName() => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
}

/// <summary>设置页中编辑的服务商 endpoint 配置。</summary>
public sealed class ProviderConfig
{
    /// <summary>用于匹配描述符和选择的稳定标识符。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>面向用户的服务商名称。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>通信协议；参见 <see cref="ProviderProtocols"/>。</summary>
    public string Protocol { get; set; } = ProviderProtocols.OpenAi;

    /// <summary>API base URL，例如 <c>https://api.openai.com/v1</c>。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API Key。默认设置存储会以加密形式持久化。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>服务商是否可供对话页使用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>已配置的模型。</summary>
    public List<ModelConfig> Models { get; set; } = new();

    /// <summary>按标识符查找 model。</summary>
    public ModelConfig? FindModel(string? modelId)
        => modelId == null ? null : Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 服务商是否可用：已启用、配置了 base URL，并且有 API Key 或指向本地 endpoint。
    /// </summary>
    public bool IsUsable()
    {
        if (!Enabled || string.IsNullOrWhiteSpace(BaseUrl))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(ApiKey) || IsLocalEndpoint(BaseUrl);
    }

    /// <summary>创建深拷贝。</summary>
    public ProviderConfig Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Protocol = Protocol,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Enabled = Enabled,
        Models = Models.Select(m => m.Clone()).ToList()
    };

    internal static bool IsLocalEndpoint(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>通过 <see cref="ISettingsStore"/> 加载和保存的根设置对象。</summary>
public sealed class AppSettings
{
    /// <summary>架构版本。零表示设置从未保存过。</summary>
    public int Version { get; set; }

    /// <summary>已配置的服务商。</summary>
    public List<ProviderConfig> Providers { get; set; } = new();

    /// <summary>对话页中所选服务商的标识符。</summary>
    public string? SelectedProviderId { get; set; }

    /// <summary>对话页中所选 model 的标识符。</summary>
    public string? SelectedModelId { get; set; }

    /// <summary>新会话使用的 system prompt。为空时回退到全局默认值。</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>按标识符查找服务商。</summary>
    public ProviderConfig? FindProvider(string? providerId)
        => providerId == null ? null : Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>创建深拷贝。</summary>
    public AppSettings Clone() => new()
    {
        Version = Version,
        Providers = Providers.Select(p => p.Clone()).ToList(),
        SelectedProviderId = SelectedProviderId,
        SelectedModelId = SelectedModelId,
        SystemPrompt = SystemPrompt
    };
}
