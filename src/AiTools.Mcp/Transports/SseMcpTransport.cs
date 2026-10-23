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
/// 旧版 MCP HTTP+SSE transport。通过一个长连接的 GET 请求接收 server-sent events；第一个 <c>endpoint</c> 事件
/// 提供用于 POST 客户端消息的 URL。响应则通过 SSE 流到达。
/// </summary>
/// <remarks>
/// 并发发送会被串行化。同一时间只允许一个消费者枚举 <see cref="ReceiveAsync"/>。
/// </remarks>
public sealed class SseMcpTransport : IMcpTransport, IProtocolVersionTransport
{
    private readonly SseMcpTransportOptions _options;
    private readonly AsyncMessageQueue<JsonElement> _incoming = new AsyncMessageQueue<JsonElement>();
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly HttpClient _httpClient;
    private readonly object _gate = new object();
    private readonly string _providerName = McpConstants.ProviderName;
    private TaskCompletionSource<Uri> _messageEndpoint = CreateEndpointSource();
    private CancellationTokenSource _streamCts = new CancellationTokenSource();
    private CancellationTokenSource? _startCts;
    private HttpResponseMessage? _streamResponse;
    private Task? _streamPump;
    private string? _protocolVersion;
    private bool _started;
    private bool _disposed;

    /// <summary>创建旧版 HTTP+SSE transport。</summary>
    /// <param name="options">SSE 端点及 HTTP 配置。</param>
    public SseMcpTransport(SseMcpTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = HttpClientProvider.Resolve(options, _providerName);
    }

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return !_disposed
                    && _started
                    && _streamPump != null
                    && !_streamPump.IsCompleted
                    && _messageEndpoint.Task.Status == TaskStatus.RanToCompletion;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>旧版 HTTP+SSE transport 不协商会话标识符。</remarks>
    public string? SessionId => null;

    /// <summary>在 <c>MCP-Protocol-Version</c> 标头中发送的 MCP 协议版本（已知时）。</summary>
    string? IProtocolVersionTransport.ProtocolVersion
    {
        get => _protocolVersion;
        set => _protocolVersion = value;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        TaskCompletionSource<Uri> endpointSource;
        CancellationTokenSource streamCts;
        CancellationTokenSource startCts;
        HttpRequestMessage request;
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SseMcpTransport));
            }

            if (_started)
            {
                return;
            }

            var endpoint = _options.SseEndpoint;
            if (endpoint == null)
            {
                throw new ArgumentException("An SSE endpoint must be configured on SseMcpTransportOptions.", nameof(_options));
            }

            if (!endpoint.IsAbsoluteUri)
            {
                throw new ArgumentException("The SSE endpoint must be an absolute URI.", nameof(_options));
            }

            _started = true;
            _messageEndpoint = CreateEndpointSource();
            _streamCts = new CancellationTokenSource();
            endpointSource = _messageEndpoint;
            streamCts = _streamCts;
            startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_options.Timeout != Timeout.InfiniteTimeSpan)
            {
                startCts.CancelAfter(_options.Timeout);
            }

            _startCts = startCts;

            request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            McpHttp.ApplyHeaders(request, _options, null, _protocolVersion);
        }

        HttpResponseMessage response;
        using (request)
        {
            try
            {
                response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, startCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                ResetAfterStartFailure();
                throw new AiServiceException("Timed out while opening the MCP SSE stream.", _providerName, innerException: ex);
            }
            catch (HttpRequestException ex)
            {
                ResetAfterStartFailure();
                throw new AiServiceException("Failed to open the MCP SSE stream: " + ex.Message, _providerName, innerException: ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = string.Empty;
            try
            {
                body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 响应体仅用于诊断。
            }

            var status = response.StatusCode;
            response.Dispose();
            ResetAfterStartFailure();
            throw new AiServiceException(
                $"The MCP SSE endpoint returned HTTP {(int)status} ({status}).",
                _providerName,
                (int)status,
                responseBody: body);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                response.Dispose();
                throw new ObjectDisposedException(nameof(SseMcpTransport));
            }

            _streamResponse = response;
            _streamPump = Task.Run(() => PumpStreamAsync(response, streamCts));
        }

        try
        {
            await WaitForMessageEndpointAsync(endpointSource, startCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            ResetAfterStartFailure();
            throw new AiServiceException(
                "Timed out waiting for the MCP SSE endpoint event.",
                _providerName,
                innerException: ex);
        }
        catch (AiServiceException)
        {
            ResetAfterStartFailure();
            throw;
        }
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
            throw new ObjectDisposedException(nameof(SseMcpTransport));
        }

        if (!_started)
        {
            throw new InvalidOperationException("The transport has not been started.");
        }

        Uri messageEndpoint;
        try
        {
            messageEndpoint = await _messageEndpoint.Task.WithCancellationAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("The MCP SSE stream did not provide a message endpoint.", ex);
        }

        var payload = message.GetRawText();

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using (var timeout = CreateTimeoutTokenSource(ct))
            {
                var response = await _httpClient.SendAndEnsureAsync(
                    () => CreatePostRequest(messageEndpoint, payload),
                    _providerName,
                    timeout.Token).ConfigureAwait(false);

                using (response)
                {
                    // 部分服务器会在 HTTP 响应中直接返回 JSON-RPC 响应，而不是通过 SSE 流返回。
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (mediaType != null
                        && mediaType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        EnqueueJsonBody(body);
                    }
                }
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AiServiceException("The MCP request timed out.", _providerName, innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiServiceException("Failed to reach the MCP message endpoint: " + ex.Message, _providerName, innerException: ex);
        }
        finally
        {
            ReleaseSendLock();
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
    public async ValueTask DisposeAsync()
    {
        HttpResponseMessage? response;
        Task? pump;
        CancellationTokenSource? startCts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _started = false;
            response = _streamResponse;
            pump = _streamPump;
            startCts = _startCts;
            _streamResponse = null;
            _streamPump = null;
        }

        _incoming.Complete();
        _messageEndpoint.TrySetCanceled();

        try
        {
            _streamCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        startCts?.Cancel();
        response?.Dispose();

        if (pump != null)
        {
            try
            {
                await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 流拆除时接收泵可能出现故障。
            }
        }
    }

    private static TaskCompletionSource<Uri> CreateEndpointSource() =>
        new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ResetAfterStartFailure()
    {
        HttpResponseMessage? response;
        lock (_gate)
        {
            _started = false;
            response = _streamResponse;
            _streamResponse = null;
            _streamPump = null;
        }

        try
        {
            _streamCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }

        response?.Dispose();
        _messageEndpoint.TrySetCanceled();
    }

    private async Task PumpStreamAsync(HttpResponseMessage response, CancellationTokenSource streamCts)
    {
        try
        {
            using (response)
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                await foreach (var evt in SseParser.ParseAsync(stream, streamCts.Token).ConfigureAwait(false))
                {
                    if (evt.IsDone)
                    {
                        break;
                    }

                    if (string.Equals(evt.EventType, "endpoint", StringComparison.Ordinal))
                    {
                        var endpointValue = evt.Data.Trim();
                        if (endpointValue.Length > 0)
                        {
                            _messageEndpoint.TrySetResult(ResolveMessageEndpoint(endpointValue));
                        }

                        continue;
                    }

                    if (!SseParser.TryParseJson(evt, out var message))
                    {
                        continue;
                    }

                    _incoming.Enqueue(message);
                }
            }

            CompleteStream();
        }
        catch (OperationCanceledException)
        {
            CompleteStream();
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is HttpRequestException)
        {
            CompleteStream(ex);
        }
        catch (Exception ex)
        {
            CompleteStream(ex);
        }
    }

    private void CompleteStream(Exception? error = null)
    {
        if (!_messageEndpoint.Task.IsCompleted)
        {
            _messageEndpoint.TrySetException(
                error ?? new AiServiceException("The MCP SSE stream closed before providing a message endpoint.", _providerName));
        }

        _incoming.Complete(error);
    }

    private Uri ResolveMessageEndpoint(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        // 按旧版 transport 的要求，相对端点基于 SSE 端点解析。
        var baseUri = _options.SseEndpoint ?? _options.BaseAddress;
        if (baseUri == null || !baseUri.IsAbsoluteUri)
        {
            throw new AiServiceException("The MCP server returned a relative message endpoint but no absolute SSE endpoint is configured.", _providerName);
        }

        return new Uri(baseUri, value);
    }

    private HttpRequestMessage CreatePostRequest(Uri endpoint, string payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = AiContent.Json(payload)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        McpHttp.ApplyHeaders(request, _options, null, _protocolVersion);
        return request;
    }

    private void EnqueueJsonBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using (var document = JsonDocument.Parse(body))
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        _incoming.Enqueue(item.Clone());
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    _incoming.Enqueue(root.Clone());
                }
            }
        }
        catch (JsonException)
        {
            // 忽略非 JSON 响应体；响应通常通过 SSE 流传递。
        }
    }

    private static async Task WaitForMessageEndpointAsync(TaskCompletionSource<Uri> endpointSource, CancellationToken ct)
    {
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancellation.TrySetResult(true)))
        {
            if (await Task.WhenAny(endpointSource.Task, cancellation.Task).ConfigureAwait(false) != endpointSource.Task)
            {
                throw new OperationCanceledException(ct);
            }
        }

        await endpointSource.Task.ConfigureAwait(false);
    }

    private CancellationTokenSource CreateTimeoutTokenSource(CancellationToken ct)
    {
        var timeout = _options.Timeout;
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(timeout);
        return source;
    }
}
