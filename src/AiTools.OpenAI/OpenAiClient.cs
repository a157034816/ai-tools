using System;
using System.Net.Http;
using AiTools.Abstractions;
using AiTools.Core.Http;

namespace AiTools.OpenAI;

/// <summary>
/// 便捷的聚合客户端，公开共享同一份 <see cref="OpenAiOptions"/> 和 <see cref="HttpClient"/> 的
/// 聊天、嵌入、图像与音频客户端。
/// </summary>
public sealed class OpenAiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 <see cref="OpenAiClient"/> 类的新实例，并从提供的选项解析
    /// <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    public OpenAiClient(OpenAiOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// 使用显式指定的 <see cref="HttpClient"/> 初始化 <see cref="OpenAiClient"/> 类的新实例。
    /// 所有子客户端共享提供的实例。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">
    /// 要使用的 HTTP 客户端；为 null 时由选项（或共享的服务商）提供一个。注入的客户端
    /// 绝不会由本实例释放。
    /// </param>
    public OpenAiClient(OpenAiOptions options, HttpClient? httpClient)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        Options = options;
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, options.ProviderName);

        Chat = new OpenAiChatClient(options, _httpClient);
        Embeddings = new OpenAiEmbeddingClient(options, _httpClient);
        Images = new OpenAiImageClient(options, _httpClient);
        Audio = new OpenAiAudioClient(options, _httpClient);
    }

    /// <summary>获取所有子客户端使用的选项。</summary>
    public OpenAiOptions Options { get; }

    /// <summary>获取服务商标识符，例如 <c>openai</c> 或 <c>deepseek</c>。</summary>
    public string ProviderName => Options.ProviderName;

    /// <summary>获取聊天补全客户端。</summary>
    public OpenAiChatClient Chat { get; }

    /// <summary>获取嵌入客户端。</summary>
    public OpenAiEmbeddingClient Embeddings { get; }

    /// <summary>获取图像生成客户端。</summary>
    public OpenAiImageClient Images { get; }

    /// <summary>获取音频（语音合成与转写）客户端。</summary>
    public OpenAiAudioClient Audio { get; }

    /// <summary>为提供的选项创建新的聚合客户端。</summary>
    /// <param name="options">服务商选项。</param>
    /// <returns>新的 <see cref="OpenAiClient"/>。</returns>
    public static OpenAiClient Create(OpenAiOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return new OpenAiClient(options);
    }

    /// <summary>
    /// 释放此聚合实例持有的资源。底层的 <see cref="HttpClient"/> 绝不在此释放：
    /// 它要么由调用方注入，要么通过 <see cref="HttpClientProvider"/> 共享，
    /// 因此释放它会破坏其他仍在使用
    /// 同一服务商端点的客户端。
    /// </summary>
    public void Dispose()
    {
    }
}
