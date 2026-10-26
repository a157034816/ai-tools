using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Anthropic;
using AiTools.Gemini;
using AiTools.OpenAI;

namespace AiTools.Wpf.Configuration;

/// <summary>
/// 默认的 <see cref="IProviderFactory"/> 实现，将服务商配置映射到内置的 AiTools 客户端。
/// 同时提供模型列表客户端，供设置页直接从服务商接口拉取可用模型。
/// </summary>
public sealed class BuiltInProviderFactory : IProviderFactory, IProviderModelCatalogFactory
{
    private readonly Dictionary<string, IChatClient> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <inheritdoc />
    public IChatClient CreateChatClient(ProviderConfig provider)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(provider.Protocol))
        {
            throw new AiToolsWpfException($"服务商 {provider.DisplayName} 未指定协议。");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new AiToolsWpfException($"服务商 {provider.DisplayName} 未配置 Base URL。");
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

    private static IChatClient Create(ProviderConfig provider)
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
                    BaseAddress = new Uri(provider.BaseUrl, UriKind.Absolute)
                });

            case ProviderProtocols.Gemini:
                return new GeminiChatClient(new GeminiOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = new Uri(provider.BaseUrl, UriKind.Absolute)
                });

            default:
                throw new AiToolsWpfException($"不支持的服务商协议：{provider.Protocol}。可用协议：{string.Join(", ", ProviderProtocols.All)}。");
        }
    }

    /// <inheritdoc />
    public IModelCatalog CreateModelCatalog(ProviderConfig provider)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(provider.Protocol))
        {
            throw new AiToolsWpfException($"服务商 {provider.DisplayName} 未指定协议。");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new AiToolsWpfException($"服务商 {provider.DisplayName} 未配置 Base URL。");
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
                throw new AiToolsWpfException($"不支持的服务商协议：{provider.Protocol}。可用协议：{string.Join(", ", ProviderProtocols.All)}。");
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
