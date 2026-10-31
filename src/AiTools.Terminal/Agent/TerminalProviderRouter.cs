using System;
using System.Collections.Generic;
using System.Linq;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Core.Http;
using AiTools.Extensions;

namespace AiTools.Terminal.Agent;

/// <summary>
/// 把扩展注册的 provider 与虚拟模型接入终端的服务商工厂：
/// 虚拟模型把请求路由到实际模型，扩展 provider 用内置协议客户端实现。
/// </summary>
public sealed class TerminalProviderRouter
{
    private readonly BuiltInProviderFactory _inner = new();
    private readonly ExtensionRuntime _runtime;
    private readonly IProviderFactory? _fallback;

    /// <summary>创建路由。</summary>
    /// <param name="runtime">扩展运行时。</param>
    /// <param name="fallback">扩展未处理时使用的工厂；为 null 时使用内置工厂。</param>
    public TerminalProviderRouter(ExtensionRuntime runtime, IProviderFactory? fallback = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _fallback = fallback;
    }

    /// <summary>创建聊天客户端：优先匹配扩展 provider，其次匹配虚拟模型路由。</summary>
    /// <param name="provider">设置中的服务商。</param>
    /// <param name="model">请求的模型名（可能为虚拟模型）。</param>
    /// <param name="observer">可选的 HTTP 观察器。</param>
    public IChatClient CreateChatClient(ProviderConfig provider, string? model, IAiHttpObserver? observer = null)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        var virtualModel = _runtime.ResolveVirtualModel(model ?? provider.Models.FirstOrDefault()?.Id);
        if (virtualModel != null)
        {
            var targetProvider = provider;
            if (!string.IsNullOrWhiteSpace(virtualModel.Provider) &&
                !string.Equals(virtualModel.Provider, provider.Id, StringComparison.OrdinalIgnoreCase))
            {
                var registered = _runtime.Providers.FirstOrDefault(item =>
                    string.Equals(item.Name, virtualModel.Provider, StringComparison.OrdinalIgnoreCase));
                if (registered != null)
                {
                    targetProvider = new ProviderConfig
                    {
                        Id = registered.Name,
                        DisplayName = registered.Name,
                        Protocol = registered.Protocol,
                        BaseUrl = registered.BaseAddress?.ToString() ?? provider.BaseUrl,
                        ApiKey = registered.ApiKey ?? provider.ApiKey,
                        Models = new List<ModelConfig> { new() { Id = virtualModel.TargetModel, Enabled = true } }
                    };
                }
            }

            var client = CreateClientFor(targetProvider, observer);
            return new VirtualModelChatClient(client, virtualModel);
        }

        var registeredProvider = _runtime.Providers.FirstOrDefault(item =>
            string.Equals(item.Name, provider.Id, StringComparison.OrdinalIgnoreCase));
        if (registeredProvider != null)
        {
            var config = new ProviderConfig
            {
                Id = registeredProvider.Name,
                DisplayName = registeredProvider.Name,
                Protocol = registeredProvider.Protocol,
                BaseUrl = registeredProvider.BaseAddress?.ToString() ?? provider.BaseUrl,
                ApiKey = registeredProvider.ApiKey ?? provider.ApiKey,
                Models = new List<ModelConfig>
                {
                    new()
                    {
                        Id = registeredProvider.DefaultModel ?? provider.Models.FirstOrDefault()?.Id ?? string.Empty,
                        Enabled = true
                    }
                }
            };
            return CreateClientFor(config, observer);
        }

        return CreateClientFor(provider, observer);
    }

    private IChatClient CreateClientFor(ProviderConfig provider, IAiHttpObserver? observer)
    {
        if (_fallback != null)
        {
            return _fallback.CreateChatClient(provider);
        }

        return observer == null
            ? _inner.CreateChatClient(provider)
            : BuiltInProviderFactoryWithObserver.Create(provider, observer);
    }

    /// <summary>可注入 HTTP 观察器的客户端构造器。</summary>
    internal static class BuiltInProviderFactoryWithObserver
    {
        public static IChatClient Create(ProviderConfig provider, IAiHttpObserver observer)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                throw new AiToolsConfigurationException($"服务商 {provider.DisplayName} 未配置 Base URL。");
            }

            var baseAddress = new Uri(provider.BaseUrl, UriKind.Absolute);
            switch (provider.Protocol)
            {
                case ProviderProtocols.Anthropic:
                    return new AiTools.Anthropic.AnthropicChatClient(new AiTools.Anthropic.AnthropicOptions
                    {
                        ApiKey = provider.ApiKey,
                        BaseAddress = baseAddress,
                        Observer = observer
                    });

                case ProviderProtocols.Gemini:
                    return new AiTools.Gemini.GeminiChatClient(new AiTools.Gemini.GeminiOptions
                    {
                        ApiKey = provider.ApiKey,
                        BaseAddress = baseAddress,
                        Observer = observer
                    });

                default:
                    return new AiTools.OpenAI.OpenAiChatClient(new AiTools.OpenAI.OpenAiOptions
                    {
                        ApiKey = provider.ApiKey,
                        BaseAddress = baseAddress,
                        ProviderName = string.IsNullOrWhiteSpace(provider.Id) ? "openai" : provider.Id,
                        Observer = observer
                    });
            }
        }
    }

    /// <summary>把对虚拟模型的请求改写成真实模型，并让响应看起来仍来自虚拟模型。</summary>
    private sealed class VirtualModelChatClient : IChatClient
    {
        private readonly IChatClient _inner;
        private readonly VirtualModelRegistration _registration;

        public VirtualModelChatClient(IChatClient inner, VirtualModelRegistration registration)
        {
            _inner = inner;
            _registration = registration;
        }

        public string ProviderName => _inner.ProviderName;

        public System.Threading.Tasks.Task<ChatResponse> CompleteAsync(ChatRequest request, System.Threading.CancellationToken cancellationToken = default)
            => _inner.CompleteAsync(Rewrite(request), cancellationToken);

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken cancellationToken = default)
        {
            await foreach (var update in _inner.StreamAsync(Rewrite(request), cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private ChatRequest Rewrite(ChatRequest request)
        {
            var rewritten = new ChatRequest
            {
                Model = _registration.TargetModel,
                SystemPrompt = request.SystemPrompt,
                Messages = request.Messages,
                Tools = request.Tools,
                ToolChoice = request.ToolChoice,
                Temperature = request.Temperature,
                TopP = request.TopP,
                TopK = request.TopK,
                MaxOutputTokens = request.MaxOutputTokens,
                StopSequences = request.StopSequences,
                ResponseFormat = request.ResponseFormat,
                AdditionalParameters = request.AdditionalParameters
            };

            return _registration.Rewrite?.Invoke(rewritten) ?? rewritten;
        }
    }
}
