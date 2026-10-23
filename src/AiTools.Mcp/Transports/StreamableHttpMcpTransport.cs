using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Core.Sse;

namespace AiTools.Mcp;

/// <summary>
/// MCP Streamable HTTP transport。每条出站消息都以 POST 发送到端点；JSON 响应会直接返回，
/// 而 <c>text/event-stream</c> 响应则会被读取直到流关闭。<c>Mcp-Session-Id</c> 标头在
/// initialization 期间被捕获，并在后续请求中回传。
/// </summary>
/// <remarks>
/// 并发发送会被串行化。一次 POST 的响应可能以一个或多个 JSON-RPC 消息的形式通过
/// <see cref="ReceiveAsync"/> 交付；同一时间只允许一个消费者枚举该序列。
/// </remarks>
public sealed class StreamableHttpMcpTransport : IMcpTransport, IProtocolVersionTransport
{
    private readonly StreamableHttpTransportOptions _options;
    private readonly AsyncMessageQueue<JsonElement> _incoming = new AsyncMessageQueue<JsonElement>();
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly HttpClient _httpClient;
    private readonly string _providerName = McpConstants.ProviderName;
    private readonly CancellationTokenSource _disposeCts = new CancellationTokenSource();
    private string? _sessionId;
    private string? _protocolVersion;
    private bool _started;
    private bool _disposed;

    /// <summary>创建 Streamable HTTP transport。</summary>
    /// <param name="options">端点和 HTTP 配置。</param>
    public StreamableHttpMcpTransport(StreamableHttpTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = HttpClientProvider.Resolve(options, _providerName);
    }

    /// <inheritdoc />
    public bool IsConnected => !_disposed && _started;

    /// <inheritdoc />
    public string? SessionId => _sessionId;

    /// <summary>在 <c>MCP-Protocol-Version</c> 标头中发送的 MCP 协议版本（已知时）。</summary>
    string? IProtocolVersionTransport.ProtocolVersion
    {
        get => _protocolVersion;
        set => _protocolVersion = value;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StreamableHttpMcpTransport));
        }

        if (_options.Endpoint == null)
        {
            throw new ArgumentException("An endpoint must be configured on StreamableHttpTransportOptions.", nameof(_options));
        }

        if (!_options.Endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The Streamable HTTP endpoint must be an absolute URI.", nameof(_options));
        }

        _started = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SendAsync(JsonElement message, CancellationToken ct = default)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("An MCP message must be a JSON object.", nameof(message));
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StreamableHttpMcpTransport));
        }

        var endpoint = _options.Endpoint;
        if (!_started || endpoint == null)
        {
            throw new InvalidOperationException("The transport has not been started.");
        }

        var payload = message.GetRawText();
        using (var requestCts = CreateTimeoutTokenSource(ct))
        {
            await _sendLock.WaitAsync(requestCts.Token).ConfigureAwait(false);
            var lockHeld = true;
            try
            {
                HttpResponseMessage response;
                try
                {
                    // MCP 请求可能产生副作用，因此绝不自动重试 POST。
                    response = await _httpClient.SendAndEnsureAsync(
                        () => CreateRequest(endpoint, payload),
                        _providerName,
                        requestCts.Token).ConfigureAwait(false);
                }
                catch (AiInvalidRequestException ex) when (ex.StatusCode == 404)
                {
                    throw McpHttp.CreateNotFoundException(_sessionId != null, ex);
                }

                using (response)
                {
                    CaptureSessionHeaders(response);

                    // 响应体可能是长时间存活的 event stream。读取之前先释放发送锁，
                    // 以便服务器发起的请求（例如 sampling）和 cancellation notifications 仍可由
                    // 其他调用 POST 出去。
                    ReleaseSendLock();
                    lockHeld = false;

                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                        {
                            await ReadEventStreamAsync(stream, requestCts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            await ReadJsonBodyAsync(stream, response, requestCts.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new AiServiceException(
                    "The MCP request timed out.",
                    _providerName,
                    innerException: ex);
            }
            catch (HttpRequestException ex)
            {
                throw new AiServiceException(
                    "Failed to reach the MCP endpoint: " + ex.Message,
                    _providerName,
                    innerException: ex);
            }
            finally
            {
                if (lockHeld)
                {
                    ReleaseSendLock();
                }
            }
        }
    }

    private void ReleaseSendLock()
    {
        try
        {
            _sendLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // 请求进行期间 transport 被释放。
        }
    }

    /// <inheritdoc />
    /// <remarks>同一时间只允许一个消费者枚举此序列。</remarks>
    public IAsyncEnumerable<JsonElement> ReceiveAsync(CancellationToken ct = default) => _incoming.ReadAllAsync(ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _started = false;
            try
            {
                _disposeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已取消。
            }

            _incoming.Complete();
            _sendLock.Dispose();
            _disposeCts.Dispose();
        }

        return default;
    }

    private HttpRequestMessage CreateRequest(Uri endpoint, string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = AiContent.Json(payload)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        McpHttp.ApplyHeaders(request, _options, _sessionId, _protocolVersion);
        return request;
    }

    private async Task ReadJsonBodyAsync(Stream stream, HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            // 202 Accepted / 204 No Content 响应不会产生任何消息。
            return;
        }

        JsonElement parsed;
        try
        {
            using (var document = JsonDocument.Parse(body))
            {
                parsed = document.RootElement.Clone();
            }
        }
        catch (JsonException ex)
        {
            throw new AiServiceException(
                "The MCP server returned a response that is not valid JSON.",
                _providerName,
                (int)response.StatusCode,
                responseBody: body,
                innerException: ex);
        }

        if (parsed.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in parsed.EnumerateArray())
            {
                _incoming.Enqueue(item.Clone());
            }
        }
        else if (parsed.ValueKind == JsonValueKind.Object)
        {
            _incoming.Enqueue(parsed);
        }
    }

    private async Task ReadEventStreamAsync(Stream stream, CancellationToken ct)
    {
        await foreach (var evt in SseParser.ParseAsync(stream, ct).ConfigureAwait(false))
        {
            if (evt.IsDone)
            {
                break;
            }

            if (!SseParser.TryParseJson(evt, out var message))
            {
                continue;
            }

            _incoming.Enqueue(message);
        }
    }

    private void CaptureSessionHeaders(HttpResponseMessage response)
    {
        var sessionId = McpHttp.GetSessionId(response);
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessionId = sessionId;
        }

        var protocolVersion = McpHttp.GetProtocolVersion(response);
        if (!string.IsNullOrEmpty(protocolVersion))
        {
            _protocolVersion = protocolVersion;
        }
    }

    private CancellationTokenSource CreateTimeoutTokenSource(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var timeout = _options.Timeout;
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(timeout);
        }

        return source;
    }
}
