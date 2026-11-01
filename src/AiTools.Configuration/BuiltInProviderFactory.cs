using System;
using System.Collections.Generic;
using System.Linq;
using AiTools.Abstractions;
using AiTools.Anthropic;
using AiTools.Gemini;
using AiTools.OpenAI;

namespace AiTools.Configuration;

/// <summary>
/// 默认的 <see cref="IProviderFactory"/> 实现，将服务商配置映射到内置的 AiTools 客户端。
/// 同时提供模型列表客户端，供设置页直接从服务商接口拉取可用模型。
/// </summary>
public sealed class BuiltInProviderFactory : IProviderFactory, IProviderModelCatalogFactory
{
    private readonly Dictionary<string, IChatClient> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>创建工厂。</summary>
    /// <param name="promptCache">提示缓存策略；null 时使用默认值（启用缓存与预热）。</param>
    public BuiltInProviderFactory(PromptCacheOptions? promptCache = null)
        => PromptCache = promptCache ?? new PromptCacheOptions();

    /// <summary>获取提示缓存策略。修改后需要清空会话/重载设置才对新建的客户端生效。</summary>
    public PromptCacheOptions PromptCache { get; }

    /// <inheritdoc />
    public IChatClient CreateChatClient(ProviderConfig provider)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(provider.Protocol))
        {
            throw new AiToolsConfigurationException($"服务商 {provider.DisplayName} 未指定协议。");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new AiToolsConfigurationException($"服务商 {provider.DisplayName} 未配置 Base URL。");
        }

        var cacheKey = BuildCacheKey(provider);
        IChatClient? cached;
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }
        }

        var client = Create(provider);
        lock (_gate)
        {
            _cache[cacheKey] = client;
        }

        return client;
    }

    private IChatClient Create(ProviderConfig provider)
    {
        switch (provider.Protocol)
        {
            case ProviderProtocols.OpenAi:
                return new OpenAiChatClient(new OpenAiOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = new Uri(provider.BaseUrl, UriKind.Absolute),
                    ProviderName = string.IsNullOrWhiteSpace(provider.Id) ? "openai" : provider.Id
                });

            case ProviderProtocols.Anthropic:
                return new AnthropicChatClient(new AnthropicOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = new Uri(provider.BaseUrl, UriKind.Absolute),
                    PromptCaching = PromptCache.Enabled ? ToAnthropicCaching(PromptCache) : null
                });

            case ProviderProtocols.Gemini:
                return new GeminiChatClient(new GeminiOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = new Uri(provider.BaseUrl, UriKind.Absolute)
                });

            default:
                throw new AiToolsConfigurationException($"不支持的服务商协议：{provider.Protocol}。可用协议：{string.Join(", ", ProviderProtocols.All)}。");
        }
    }

    /// <summary>把共享策略映射成 Anthropic 专属的缓存写入配置。</summary>
    public static AnthropicPromptCachingOptions ToAnthropicCaching(PromptCacheOptions options) => new()
    {
        UseLongRetention = options.LongRetention,
        CacheTools = options.CacheTools,
        CacheSystem = options.CacheSystem,
        CacheMessages = options.CacheMessages,
        MessageCacheOffset = options.MessageCacheOffset
    };

    /// <inheritdoc />
    public IModelCatalog CreateModelCatalog(ProviderConfig provider)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(provider.Protocol))
        {
            throw new AiToolsConfigurationException($"服务商 {provider.DisplayName} 未指定协议。");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new AiToolsConfigurationException($"服务商 {provider.DisplayName} 未配置 Base URL。");
        }

        var baseAddress = new Uri(provider.BaseUrl, UriKind.Absolute);
        switch (provider.Protocol)
        {
            case ProviderProtocols.OpenAi:
                return new OpenAiModelClient(new OpenAiOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = baseAddress,
                    ProviderName = string.IsNullOrWhiteSpace(provider.Id) ? "openai" : provider.Id
                });

            case ProviderProtocols.Anthropic:
                return new AnthropicModelClient(new AnthropicOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = baseAddress
                });

            case ProviderProtocols.Gemini:
                return new GeminiModelClient(new GeminiOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = baseAddress
                });

            default:
                throw new AiToolsConfigurationException($"不支持的服务商协议：{provider.Protocol}。可用协议：{string.Join(", ", ProviderProtocols.All)}。");
        }
    }

    private static string BuildCacheKey(ProviderConfig provider)
    {
        return string.Join(
            "|",
            provider.Protocol, provider.BaseUrl, provider.ApiKey,
            provider.Id);
    }
}
