using System;
using System.Net.Http;

namespace AiTools.Anthropic;

/// <summary>
/// Anthropic 服务商的便捷聚合客户端，在其各客户端之间共享同一组选项。
/// </summary>
/// <remarks>
/// Anthropic 通过统一抽象仅公开聊天（Messages API），因此此类型
/// 目前只公开一个 <see cref="Chat"/> 客户端。该聚合实例不拥有也不释放
/// 底层的 <see cref="HttpClient"/>；注入的客户端和缓存的服务商客户端
/// 仍由调用方管理。
/// </remarks>
public sealed class AnthropicClient : IDisposable
{
    /// <summary>
    /// 初始化 <see cref="AnthropicClient"/> 类的新实例。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <see langword="null"/>。</exception>
    public AnthropicClient(AnthropicOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Chat = new AnthropicChatClient(options);
        Models = new AnthropicModelClient(options);
    }

    /// <summary>
    /// 使用自定义 HTTP 客户端初始化 <see cref="AnthropicClient"/> 类的新实例。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">用于所有请求的 HTTP 客户端。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 或 <paramref name="httpClient"/> 为 <see langword="null"/>。
    /// </exception>
    public AnthropicClient(AnthropicOptions options, HttpClient httpClient)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        if (httpClient == null)
        {
            throw new ArgumentNullException(nameof(httpClient));
        }

        Chat = new AnthropicChatClient(options, httpClient);
        Models = new AnthropicModelClient(options, httpClient);
    }

    /// <summary>获取此聚合实例使用的服务商选项。</summary>
    public AnthropicOptions Options { get; }

    /// <summary>获取 Anthropic 聊天客户端。</summary>
    public AnthropicChatClient Chat { get; }

    /// <summary>获取 Anthropic 模型列表客户端。</summary>
    public AnthropicModelClient Models { get; }

    /// <summary>从选项创建聚合客户端。</summary>
    public static AnthropicClient Create(AnthropicOptions options) => new(options);

    /// <summary>从选项和自定义 HTTP 客户端创建聚合客户端。</summary>
    public static AnthropicClient Create(AnthropicOptions options, HttpClient httpClient) => new(options, httpClient);

    /// <summary>
    /// 释放此聚合实例持有的资源。共享的 HTTP 客户端故意不释放。
    /// </summary>
    public void Dispose()
    {
        // HTTP 客户端由 HttpClientProvider 缓存，或由调用方拥有。
    }
}
