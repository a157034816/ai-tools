using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Core.Sse;
using AiTools.Gemini.Internal;

namespace AiTools.Gemini;

/// <summary>Google Gemini 聊天客户端，实现 <see cref="IChatClient"/>。</summary>
/// <remarks>
/// <para>
/// 请求发送到 <c>{base}/models/{model}:generateContent</c>（或 <c>:streamGenerateContent?alt=sse</c>）。
/// 模型名称可以带或不带 <c>models/</c> 前缀。
/// </para>
/// <para>
/// Gemini 的 function calling 没有 ID。本客户端会为统一的 <see cref="ToolCall"/> 模型合成形如
/// <c>call_{index}</c> 的 ID，并在将工具结果映射回去时，从产生该调用的助手消息中解析出
/// function 名称，因为 <see cref="ChatMessage.ToolResult"/> 只携带 <see cref="ChatMessage.ToolCallId"/>。
/// </para>
/// <para>
/// 对于 gemini-2.5 的多轮 function calling，与 <c>functionCall</c> 部分一同返回的
/// <c>thoughtSignature</c> 会保存在 <see cref="ToolCall.ProviderData"/> 中，并在下一次请求时原样回传。
/// 省略它会导致 API 拒绝该对话。流式工具调用增量无法携带服务商数据，因为
/// <see cref="ToolCallDelta"/> 没有此字段；需要继续 gemini-2.5 工具调用循环时，请使用非流式
/// 补全。
/// </para>
/// </remarks>
public sealed class GeminiChatClient : IChatClient
{
    private readonly GeminiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>使用提供的选项初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    public GeminiChatClient(GeminiOptions options)
        : this(options, null)
    {
    }

    /// <summary>使用提供的选项和 HTTP 客户端初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">可选的 HTTP 客户端；为 null 时创建共享客户端。</param>
    public GeminiChatClient(GeminiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, GeminiInfrastructure.ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => GeminiInfrastructure.ProviderName;

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var body = GeminiChatRequestBuilder.Build(request);
        var uri = GeminiInfrastructure.BuildUri(_options, request.Model, "generateContent", stream: false);

        using var timeout = GeminiInfrastructure.CreateTimeoutSource(_options, cancellationToken);
        var token = timeout?.Token ?? cancellationToken;

        using var response = await _httpClient
            .SendWithRetryAsync(
                () => GeminiInfrastructure.CreateRequest(HttpMethod.Post, uri, _options, body, stream: false),
                ProviderName,
                _options,
                token,
                new AiRequestContext { ProviderName = ProviderName, Operation = "chat", Model = request.Model })
            .ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return GeminiChatResponseParser.ParseComplete(responseBody);
    }

    private AiRequestContext StreamContext(string? model) => new()
    {
        ProviderName = ProviderName,
        Operation = "chat",
        Model = model,
        IsStreaming = true
    };

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var body = GeminiChatRequestBuilder.Build(request);
        var uri = GeminiInfrastructure.BuildUri(_options, request.Model, "streamGenerateContent", stream: true);

        using var timeout = GeminiInfrastructure.CreateTimeoutSource(_options, cancellationToken);
        var token = timeout?.Token ?? cancellationToken;

        using var response = await _httpClient
            .SendWithRetryAsync(
                () => GeminiInfrastructure.CreateRequest(HttpMethod.Post, uri, _options, body, stream: true),
                ProviderName,
                _options,
                token,
                new AiRequestContext { ProviderName = ProviderName, Operation = "chat", Model = request.Model, IsStreaming = true })
            .ConfigureAwait(false);

        var state = new GeminiChatResponseParser.GeminiStreamState();
        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using (stream)
        {
            if (UsesEventStream(response))
            {
                await foreach (var evt in ObservedSseParser.ParseObservedAsync(stream, _options.Observer, StreamContext(request.Model), token).ConfigureAwait(false))
                {
                    if (!SseParser.TryParseJson(evt, out var element))
                    {
                        continue;
                    }

                    foreach (var update in GeminiChatResponseParser.ParseChunk(element, state))
                    {
                        yield return update;
                    }
                }
            }
            else
            {
                // 忽略 alt=sse 的代理可能返回单个 JSON 对象、由 Chunk 组成的 JSON 数组，
                // 或按行分隔的 JSON。缓冲这段（通常较小的）回退响应体并处理这三种情况。
                string buffered;
                using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                {
                    buffered = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                System.Text.Json.JsonDocument? whole = null;
                var looksLikeSse = buffered.TrimStart().StartsWith("data:", StringComparison.Ordinal) ||
                                   buffered.TrimStart().StartsWith("event:", StringComparison.Ordinal);

                if (!looksLikeSse)
                {
                    try
                    {
                        whole = System.Text.Json.JsonDocument.Parse(buffered);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // 不是单个 JSON 文档；继续尝试 NDJSON 解析。
                    }
                }

                if (whole != null)
                {
                    using (whole)
                    {
                        foreach (var update in GeminiChatResponseParser.ParseChunk(whole.RootElement, state))
                        {
                            yield return update;
                        }
                    }
                }
                else if (looksLikeSse)
                {
                    using var sseStream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(buffered));
                    await foreach (var evt in ObservedSseParser.ParseObservedAsync(sseStream, _options.Observer, StreamContext(request.Model), token).ConfigureAwait(false))
                    {
                        if (!SseParser.TryParseJson(evt, out var element))
                        {
                            continue;
                        }

                        foreach (var update in GeminiChatResponseParser.ParseChunk(element, state))
                        {
                            yield return update;
                        }
                    }
                }
                else
                {
                    using var buffer = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(buffered));
                    await foreach (var element in ObservedSseParser.ParseNdJsonObservedAsync(buffer, _options.Observer, StreamContext(request.Model), token).ConfigureAwait(false))
                    {
                        foreach (var update in GeminiChatResponseParser.ParseChunk(element, state))
                        {
                            yield return update;
                        }
                    }
                }
            }
        }
    }

    private static bool UsesEventStream(HttpResponseMessage response)
    {
        var mediaType = response.Content?.Headers.ContentType?.MediaType;
        return mediaType != null &&
               mediaType.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
