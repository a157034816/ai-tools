using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Json;

namespace AiTools.Mcp;

/// <summary>
/// 高层 Model Context Protocol 客户端。执行 initialization 握手，并通过任意 <see cref="IMcpTransport"/>
/// 暴露 Tool、Resource、Prompt、logging 与 progress notifications。
/// </summary>
/// <remarks>
/// 单个后台接收循环负责将响应分发给待处理请求，并转发 notifications。
/// 公共方法可安全地并发调用。<see cref="ReceiveServerNotificationsAsync"/> 支持多个
/// 并发订阅者。
/// </remarks>
public sealed class McpClient : IAsyncDisposable
{
    private readonly IMcpTransport _transport;
    private readonly McpClientOptions _options;
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new ConcurrentDictionary<long, PendingRequest>();
    private readonly ConcurrentDictionary<string, IProgress<McpProgress>> _progressHandlers =
        new ConcurrentDictionary<string, IProgress<McpProgress>>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AsyncMessageQueue<JsonElement>, byte> _notificationSubscribers =
        new ConcurrentDictionary<AsyncMessageQueue<JsonElement>, byte>();
    private readonly SemaphoreSlim _initializeLock = new SemaphoreSlim(1, 1);
    private readonly TaskCompletionSource<bool> _pumpCompleted =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;
    private long _nextRequestId;
    private int _nextProgressToken;
    private int _initialized;
    private int _disposed;

    /// <summary>基于提供的 transport 创建客户端。</summary>
    /// <param name="transport">用于交换 JSON-RPC 消息的 transport。</param>
    /// <param name="options">可选的客户端选项。</param>
    public McpClient(IMcpTransport transport, McpClientOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new McpClientOptions();
    }

    /// <summary>获取 initialization 握手是否已成功完成。</summary>
    public bool IsInitialized => Volatile.Read(ref _initialized) == 1;

    /// <summary>获取 initialization 期间服务器报告的名称；未初始化时为 null。</summary>
    public string? ServerName { get; private set; }

    /// <summary>获取 initialization 期间服务器报告的版本；未初始化时为 null。</summary>
    public string? ServerVersion { get; private set; }

    /// <summary>获取原始服务器 capabilities 对象；未初始化时为 null。</summary>
    public JsonElement? ServerCapabilities { get; private set; }

    /// <summary>获取服务器报告的可选使用说明；服务器未提供时为 null。</summary>
    public string? ServerInstructions { get; private set; }

    /// <summary>获取与服务器协商的协议版本；未初始化时为 null。</summary>
    public string? NegotiatedProtocolVersion { get; private set; }

    /// <summary>
    /// 启动 transport 并执行 MCP initialization 握手，然后发送
    /// <c>notifications/initialized</c>。在成功初始化之后调用此方法不会产生任何效果。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (IsInitialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInitialized)
            {
                return;
            }

            ThrowIfDisposed();

            if (_pump == null)
            {
                await _transport.StartAsync(ct).ConfigureAwait(false);
                StartReceivePump();
            }

            var parameters = JsonRpc.CreateObject(writer =>
            {
                writer.WriteString("protocolVersion", _options.ProtocolVersion);
                writer.WritePropertyName("capabilities");
                writer.WriteStartObject();
                writer.WriteEndObject();
                writer.WritePropertyName("clientInfo");
                writer.WriteStartObject();
                writer.WriteString("name", _options.ClientName);
                writer.WriteString("version", _options.ClientVersion);
                writer.WriteEndObject();
            });

            var result = await SendRequestAsync(McpMethods.Initialize, parameters, ct).ConfigureAwait(false);

            var negotiatedVersion = JsonHelpers.GetString(result, "protocolVersion");
            var protocolVersion = string.IsNullOrEmpty(negotiatedVersion) ? _options.ProtocolVersion : negotiatedVersion;

            ServerName = null;
            ServerVersion = null;
            ServerCapabilities = null;
            ServerInstructions = null;
            NegotiatedProtocolVersion = protocolVersion;

            if (JsonHelpers.GetObject(result, "serverInfo") is { } serverInfo)
            {
                ServerName = JsonHelpers.GetString(serverInfo, "name");
                ServerVersion = JsonHelpers.GetString(serverInfo, "version");
            }

            ServerInstructions = JsonHelpers.GetString(result, "instructions");

            if (result.TryGetProperty("capabilities", out var capabilities)
                && capabilities.ValueKind == JsonValueKind.Object)
            {
                ServerCapabilities = capabilities.Clone();
            }

            if (_transport is IProtocolVersionTransport versionTransport)
            {
                versionTransport.ProtocolVersion = protocolVersion;
            }

            await _transport
                .SendAsync(JsonRpc.CreateNotification(McpMethods.Initialized, null), ct)
                .ConfigureAwait(false);

            Volatile.Write(ref _initialized, 1);
        }
        finally
        {
            try
            {
                _initializeLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // 初始化进行期间客户端被释放。
            }
        }
    }

    /// <summary>列出服务器暴露的 Tool，并跟随 pagination cursor。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>列出结果每一页所报告的 Tool。</returns>
    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        return await ListAllAsync(McpMethods.ToolsList, McpJsonParser.ParseTools, ct).ConfigureAwait(false);
    }

    /// <summary>调用服务器暴露的某个 Tool。</summary>
    /// <param name="name">与服务器报告完全一致的 Tool 名称。</param>
    /// <param name="arguments">可选的 JSON 参数对象。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="progress">可选的 progress 接收器，用于接收 <c>notifications/progress</c> 消息。</param>
    /// <returns>Tool 执行结果。</returns>
    public async Task<McpCallToolResult> CallToolAsync(
        string name,
        JsonElement? arguments = null,
        CancellationToken ct = default,
        IProgress<McpProgress>? progress = null)
    {
        EnsureInitialized();
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A tool name must be supplied.", nameof(name));
        }

        string? progressToken = null;
        if (progress != null)
        {
            progressToken = "mcp-" + Interlocked.Increment(ref _nextProgressToken).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _progressHandlers[progressToken] = progress;
        }

        var parameters = JsonRpc.CreateObject(writer =>
        {
            writer.WriteString("name", name);
            if (JsonValue.IsUsable(arguments))
            {
                writer.WritePropertyName("arguments");
                arguments!.Value.WriteTo(writer);
            }

            if (progressToken != null)
            {
                writer.WritePropertyName("_meta");
                writer.WriteStartObject();
                writer.WriteString("progressToken", progressToken);
                writer.WriteEndObject();
            }
        });

        try
        {
            var result = await SendRequestAsync(McpMethods.ToolsCall, parameters, ct).ConfigureAwait(false);
            return McpJsonParser.ParseToolResult(result);
        }
        finally
        {
            if (progressToken != null)
            {
                _progressHandlers.TryRemove(progressToken, out _);
            }
        }
    }

    /// <summary>列出服务器暴露的具体 Resource，并跟随 pagination cursor。</summary>
    /// <param name="ct">取消令牌。</param>
    public async Task<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        return await ListAllAsync(McpMethods.ResourcesList, McpJsonParser.ParseResources, ct).ConfigureAwait(false);
    }

    /// <summary>按 URI 读取 Resource。</summary>
    /// <param name="uri">Resource 的 URI。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<McpReadResourceResult> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }

        var parameters = JsonRpc.CreateObject(writer => writer.WriteString("uri", uri));
        var result = await SendRequestAsync(McpMethods.ResourcesRead, parameters, ct).ConfigureAwait(false);
        return McpJsonParser.ParseReadResourceResult(result);
    }

    /// <summary>列出服务器暴露的 Resource 模板，并跟随 pagination cursor。</summary>
    /// <param name="ct">取消令牌。</param>
    public async Task<IReadOnlyList<McpResourceTemplate>> ListResourceTemplatesAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        return await ListAllAsync(McpMethods.ResourcesTemplatesList, McpJsonParser.ParseResourceTemplates, ct).ConfigureAwait(false);
    }

    /// <summary>列出服务器暴露的 Prompt，并跟随 pagination cursor。</summary>
    /// <param name="ct">取消令牌。</param>
    public async Task<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        return await ListAllAsync(McpMethods.PromptsList, McpJsonParser.ParsePrompts, ct).ConfigureAwait(false);
    }

    /// <summary>获取 Prompt 及其渲染后的消息。</summary>
    /// <param name="name">Prompt 名称。</param>
    /// <param name="arguments">Prompt 接受的可选字符串参数。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<McpGetPromptResult> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("A prompt name must be supplied.", nameof(name));
        }

        var parameters = JsonRpc.CreateObject(writer =>
        {
            writer.WriteString("name", name);
            if (arguments != null)
            {
                writer.WritePropertyName("arguments");
                writer.WriteStartObject();
                foreach (var argument in arguments)
                {
                    writer.WriteString(argument.Key, argument.Value);
                }

                writer.WriteEndObject();
            }
        });

        var result = await SendRequestAsync(McpMethods.PromptsGet, parameters, ct).ConfigureAwait(false);
        return McpJsonParser.ParseGetPromptResult(result);
    }

    /// <summary>发送 <c>ping</c> 请求以验证连接仍然存活。</summary>
    /// <param name="ct">取消令牌。</param>
    public async Task PingAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        await SendRequestAsync(McpMethods.Ping, null, ct).ConfigureAwait(false);
    }

    /// <summary>设置服务器应发送的日志消息的最低严重级别。</summary>
    /// <param name="level">
    /// Logging 级别：<c>debug</c>、<c>info</c>、<c>notice</c>、<c>warning</c>、<c>error</c>、<c>critical</c>、
    /// <c>alert</c> 或 <c>emergency</c>。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    public async Task SetLoggingLevelAsync(string level, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (string.IsNullOrEmpty(level))
        {
            throw new ArgumentException("A logging level must be supplied.", nameof(level));
        }

        var parameters = JsonRpc.CreateObject(writer => writer.WriteString("level", level));
        await SendRequestAsync(McpMethods.LoggingSetLevel, parameters, ct).ConfigureAwait(false);
    }

    /// <summary>流式接收服务器 notifications，例如 <c>notifications/message</c>、<c>tools/list_changed</c> 和 progress 更新。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>支持多个并发消费者；每个消费者接收订阅之后到达的 notifications。</remarks>
    public async IAsyncEnumerable<JsonElement> ReceiveServerNotificationsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var queue = new AsyncMessageQueue<JsonElement>();
        _notificationSubscribers.TryAdd(queue, 0);

        try
        {
            if (_pumpCompleted.Task.IsCompleted || Volatile.Read(ref _disposed) != 0)
            {
                queue.Complete(new AiServiceException("The MCP connection is closed.", McpConstants.ProviderName));
            }

            await foreach (var notification in queue.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return notification;
            }
        }
        finally
        {
            _notificationSubscribers.TryRemove(queue, out _);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var closed = new AiServiceException("The MCP client has been disposed.", McpConstants.ProviderName);
        FailPendingRequests(closed);

        if (_pump != null)
        {
            try
            {
                _pumpCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 接收泵已停止。
            }

            try
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭过程中的错误对调用方没有价值。
            }

            try
            {
                await _pump.WithCancellationAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // transport 拆除期间接收泵可能出现故障。
            }
        }
        else
        {
            try
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭过程中的错误对调用方没有价值。
            }
        }

        foreach (var subscriber in _notificationSubscribers.Keys)
        {
            subscriber.Complete(closed);
        }

        _notificationSubscribers.Clear();
        _progressHandlers.Clear();
        _pumpCts?.Dispose();

        try
        {
            _initializeLock.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 初始化已提前释放了该锁。
        }
    }

    private void StartReceivePump()
    {
        _pumpCts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            await foreach (var message in _transport.ReceiveAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    HandleMessage(message);
                }
                catch (Exception)
                {
                    // 格式错误的消息不能终止接收循环。
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常关闭。
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            var error = failure ?? new AiServiceException(
                "The MCP connection was closed by the peer.",
                McpConstants.ProviderName);

            FailPendingRequests(error);
            foreach (var subscriber in _notificationSubscribers.Keys)
            {
                subscriber.Complete(error);
            }

            _pumpCompleted.TrySetResult(true);
        }
    }

    private void HandleMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var method = JsonRpc.GetMethod(message);
        if (method != null)
        {
            if (JsonRpc.HasId(message))
            {
                // 服务器发起的请求。回复时不要阻塞接收循环；处理该消息期间，Streamable HTTP
                // transport 可能仍持有响应流。
                _ = RespondToServerRequestAsync(message, method);
                return;
            }

            HandleNotification(method, message);
            BroadcastNotification(message);
            return;
        }

        if (!JsonRpc.TryGetId(message, out var id) || !_pending.TryRemove(id, out var pending))
        {
            return;
        }

        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = JsonHelpers.GetInt32(error, "code") ?? -32603;
            var errorMessage = JsonHelpers.GetString(error, "message") ?? "The MCP server returned an unspecified error.";
            string? data = null;
            if (error.TryGetProperty("data", out var dataElement))
            {
                data = dataElement.ValueKind switch
                {
                    JsonValueKind.String => dataElement.GetString(),
                    JsonValueKind.Undefined => null,
                    JsonValueKind.Null => null,
                    _ => dataElement.GetRawText()
                };
            }

            pending.Completion.TrySetException(new McpException(errorMessage, code, data));
            return;
        }

        JsonElement result = default;
        if (message.TryGetProperty("result", out var resultElement)
            && resultElement.ValueKind == JsonValueKind.Object)
        {
            result = resultElement.Clone();
        }
        else
        {
            // MCP 结果均为对象。对于缺失或为 null 的结果（例如来自 "ping" 的结果），将其视为
            // 空对象，以便可选字段解析永不失败。
            result = JsonRpc.CreateObject(_ => { });
        }

        pending.Completion.TrySetResult(result);
    }

    private void HandleNotification(string method, JsonElement message)
    {
        if (string.Equals(method, McpMethods.CancelledNotification, StringComparison.Ordinal))
        {
            HandleServerCancellation(message);
            return;
        }

        if (!string.Equals(method, McpMethods.ProgressNotification, StringComparison.Ordinal))
        {
            return;
        }

        var progress = McpJsonParser.ParseProgress(message);
        if (progress == null || progress.ProgressToken == null)
        {
            return;
        }

        if (_progressHandlers.TryGetValue(progress.ProgressToken, out var handler))
        {
            try
            {
                handler.Report(progress);
            }
            catch (Exception)
            {
                // 有故障的 progress 接收器不能破坏消息分发。
            }
        }
    }

    private void HandleServerCancellation(JsonElement notification)
    {
        if (!notification.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("requestId", out var requestId)
            || requestId.ValueKind != JsonValueKind.Number
            || !requestId.TryGetInt64(out var id))
        {
            return;
        }

        if (_pending.TryRemove(id, out var pending))
        {
            var reason = JsonHelpers.GetString(parameters, "reason") ?? "The MCP server cancelled the request.";
            pending.Completion.TrySetException(new McpException(reason, McpConstants.RequestCancelledCode));
        }
    }

    private void BroadcastNotification(JsonElement message)
    {
        foreach (var subscriber in _notificationSubscribers.Keys)
        {
            subscriber.Enqueue(message.Clone());
        }
    }

    private async Task RespondToServerRequestAsync(JsonElement request, string method)
    {
        try
        {
            JsonElement id = request.TryGetProperty("id", out var idElement) ? idElement : default;
            JsonElement response;
            if (string.Equals(method, "ping", StringComparison.Ordinal))
            {
                response = JsonRpc.CreateResult(id, null);
            }
            else
            {
                response = JsonRpc.CreateError(id, JsonRpc.MethodNotFound, "The method '" + method + "' is not supported by this client.");
            }

            await _transport.SendAsync(response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 连接可能正在关闭；没有可以报告该失败的对象。
        }
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        JsonElement? parameters,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        var id = Interlocked.Increment(ref _nextRequestId);
        var pending = new PendingRequest();
        if (!_pending.TryAdd(id, pending))
        {
            throw new InvalidOperationException("Failed to allocate an MCP request identifier.");
        }

        var message = JsonRpc.CreateRequest(id, method, parameters);
        using (var timeoutCts = CreateTimeoutTokenSource())
        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
        {
            Task? sendTask = null;
            var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (linkedCts.Token.Register(() => cancellation.TrySetResult(true)))
            {
                try
                {
                    // 启动发送但不等待它完成：某些 transport 在 JSON-RPC 响应送达后仍保持响应流打开。
                    // 真正完成请求的是待处理项的完成。
                    sendTask = _transport.SendAsync(message, sendCts.Token);

                    var completed = await Task
                        .WhenAny(pending.Completion.Task, sendTask, cancellation.Task)
                        .ConfigureAwait(false);

                    if (completed == sendTask)
                    {
                        // 直接呈现 transport 失败（例如连接被拒绝），而不是等待超时。
                        await sendTask.ConfigureAwait(false);
                        linkedCts.Token.ThrowIfCancellationRequested();
                    }
                    else if (completed == cancellation.Task)
                    {
                        linkedCts.Token.ThrowIfCancellationRequested();
                    }

                    return await pending.Completion.Task.WithCancellationAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (_pending.TryRemove(id, out _))
                    {
                        SendCancellationNotification(id, "Request timed out.");
                    }

                    throw new TimeoutException($"The MCP request '{method}' timed out after {_options.RequestTimeout}.");
                }
                catch (OperationCanceledException)
                {
                    if (_pending.TryRemove(id, out _))
                    {
                        SendCancellationNotification(id, "Request cancelled by the caller.");
                    }

                    throw;
                }
                finally
                {
                    _pending.TryRemove(id, out _);
                    ReleaseSendToken(sendTask, sendCts);
                }
            }
        }
    }

    private static void ReleaseSendToken(Task? sendTask, CancellationTokenSource sendCts)
    {
        if (sendTask == null)
        {
            sendCts.Dispose();
            return;
        }

        if (!sendTask.IsCompleted)
        {
            // 响应已分发，而 transport 仍在读取其响应流
            // （Streamable HTTP 使用 SSE 响应时很常见）。现在关闭该流；其上的任何后续消息
            // 都不需要用于完成此请求。
            try
            {
                sendCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // transport 已关闭。
            }
        }

        // 观察最终结果，避免由 cancellation 引发的异常被报告为未观察异常。
        _ = sendTask.ContinueWith(
            task =>
            {
                _ = task.Exception;
                sendCts.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SendCancellationNotification(long requestId, string reason)
    {
        try
        {
            var parameters = JsonRpc.CreateObject(writer =>
            {
                writer.WriteNumber("requestId", requestId);
                writer.WriteString("reason", reason);
            });
            var message = JsonRpc.CreateNotification(McpMethods.CancelledNotification, parameters);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _transport.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 尽力而为的 notification。
                }
            });
        }
        catch (Exception)
        {
            // 尽力而为的 notification。
        }
    }

    private CancellationTokenSource CreateTimeoutTokenSource()
    {
        var source = new CancellationTokenSource();
        if (_options.RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(_options.RequestTimeout);
        }

        return source;
    }

    private async Task<IReadOnlyList<T>> ListAllAsync<T>(
        string method,
        Func<JsonElement, IReadOnlyList<T>> parsePage,
        CancellationToken ct)
    {
        var items = new List<T>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        while (true)
        {
            JsonElement? parameters = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                parameters = JsonRpc.CreateObject(writer => writer.WriteString("cursor", cursor));
            }

            var result = await SendRequestAsync(method, parameters, ct).ConfigureAwait(false);
            items.AddRange(parsePage(result));

            var nextCursor = JsonHelpers.GetString(result, "nextCursor");
            if (string.IsNullOrEmpty(nextCursor) || !seenCursors.Add(nextCursor!))
            {
                break;
            }

            cursor = nextCursor;
        }

        return items;
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private void EnsureInitialized()
    {
        ThrowIfDisposed();
        if (!IsInitialized)
        {
            throw new InvalidOperationException("The MCP client has not been initialized. Call InitializeAsync first.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(McpClient));
        }
    }

    private sealed class PendingRequest
    {
        internal TaskCompletionSource<JsonElement> Completion { get; } =
            new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
