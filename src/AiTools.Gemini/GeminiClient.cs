using System;
using System.Net.Http;
using AiTools.Core.Http;
using AiTools.Gemini.Internal;

namespace AiTools.Gemini;

/// <summary>
/// 便捷的聚合客户端，在聊天、嵌入、图像和音频客户端之间共享同一个 <see cref="GeminiOptions"/>
/// 实例和一个 HTTP 客户端。
/// </summary>
public sealed class GeminiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <summary>使用提供的选项初始化新的聚合客户端。</summary>
    /// <param name="options">服务商选项。</param>
    public GeminiClient(GeminiOptions options)
        : this(options, null)
    {
    }

    /// <summary>使用提供的选项和 HTTP 客户端初始化新的聚合客户端。</summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">
    /// 可选的 HTTP 客户端。为 null 时，会从 <see cref="HttpClientProvider"/>
    /// （或 <see cref="AiHttpOptions.HttpClient"/>）解析客户端，且本实例不会释放它。
    /// </param>
    public GeminiClient(GeminiOptions options, HttpClient? httpClient)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (httpClient != null || options.HttpClient != null)
        {
            _httpClient = httpClient ?? options.HttpClient!;
            _ownsHttpClient = false;
        }
        else
        {
            // 自持有一个专用客户端，使聚合实例能在 Dispose 时释放它，
            // 而不会释放 HttpClientProvider 缓存的进程级客户端。
            _httpClient = GeminiInfrastructure.CreateDedicatedHttpClient(options);
            _ownsHttpClient = true;
        }

        Chat = new GeminiChatClient(options, _httpClient);
        Embeddings = new GeminiEmbeddingClient(options, _httpClient);
        Images = new GeminiImageClient(options, _httpClient);
        Audio = new GeminiAudioClient(options, _httpClient);
    }

    /// <summary>聊天补全客户端。</summary>
    public GeminiChatClient Chat { get; }

    /// <summary>文本嵌入客户端。</summary>
    public GeminiEmbeddingClient Embeddings { get; }

    /// <summary>图像生成客户端。</summary>
    public GeminiImageClient Images { get; }

    /// <summary>语音合成与转写客户端。</summary>
    public GeminiAudioClient Audio { get; }

    /// <summary>使用提供的选项创建聚合客户端。</summary>
    /// <param name="options">服务商选项。</param>
    /// <returns>新的 <see cref="GeminiClient"/>。</returns>
    public static GeminiClient Create(GeminiOptions options) => new(options);

    /// <summary>使用提供的选项和 HTTP 客户端创建聚合客户端。</summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">要使用的 HTTP 客户端；本实例绝不会释放它。</param>
    /// <returns>新的 <see cref="GeminiClient"/>。</returns>
    public static GeminiClient Create(GeminiOptions options, HttpClient? httpClient) => new(options, httpClient);

    /// <summary>
    /// 仅当 HTTP 客户端由本实例创建时才释放它。注入的客户端
    /// （构造函数参数或 <see cref="AiHttpOptions.HttpClient"/>）仍由调用方负责释放。
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
