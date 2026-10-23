using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Samples.McpToolServer;

/// <summary>
/// 直接基于 <see cref="HttpListener"/> 实现的最小 MCP Streamable HTTP 服务器。
/// </summary>
/// <remarks>
/// <para>
/// 端点通过 <c>POST /mcp</c> 接收 JSON-RPC 请求。请求会收到单个 <c>application/json</c> 响应；
/// 通知则收到空响应体的 <c>202 Accepted</c>。<c>GET /mcp</c> 始终返回 <c>405 Method Not Allowed</c>，
/// 因为本示例未实现服务器主动发起的 SSE 流。<c>DELETE /mcp</c> 结束会话并返回 <c>204 No Content</c>。
/// </para>
/// <para>会话在内存中跟踪；携带未知 <c>Mcp-Session-Id</c> 的请求会收到 404。</para>
/// </remarks>
internal sealed class HttpMcpServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly McpProtocol _protocol;
    private readonly ConsoleLog _log;
    private readonly Action<RequestLogEntry>? _requestObserver;
    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);
    private int _unsupportedVersionWarned;
    private bool _disposed;

    private HttpMcpServer(
        HttpListener listener,
        string host,
        int port,
        string serverName,
        ConsoleLog log,
        Action<RequestLogEntry>? requestObserver)
    {
        _listener = listener;
        _protocol = new McpProtocol(
            serverName,
            new ResourceCatalog(serverName, "streamable-http", () => Endpoint));
        _log = log;
        _requestObserver = requestObserver;
        Endpoint = "http://" + host + ":" + port + ServerMetadata.McpPath;
        Host = host;
        Port = port;
    }

    /// <summary>获取完整的 MCP 端点 URL。</summary>
    internal string Endpoint { get; }

    /// <summary>获取绑定的主机。</summary>
    internal string Host { get; }

    /// <summary>获取绑定的端口。</summary>
    internal int Port { get; }

    /// <summary>
    /// 使用请求的端口绑定 <see cref="HttpListener"/>。端口为 0 时通过
    /// <see cref="FindFreePort"/> 探测空闲端口（HttpListener 无法绑定端口 0）。端口被占用时尝试后续 20 个端口。
    /// </summary>
    /// <param name="host">要绑定的网络接口，通常为 <c>127.0.0.1</c>。</param>
    /// <param name="requestedPort">请求的端口，0 表示由系统选择。</param>
    /// <param name="serverName">向客户端报告的服务器名称。</param>
    /// <param name="log">控制台日志记录器。</param>
    /// <param name="server">成功时返回绑定的服务器。</param>
    /// <param name="error">便于阅读的失败描述。</param>
    /// <returns>成功绑定监听器时返回 <see langword="true"/>。</returns>
    internal static bool TryCreate(
        string host,
        int requestedPort,
        string serverName,
        ConsoleLog log,
        out HttpMcpServer? server,
        out string? error) =>
        TryCreate(host, requestedPort, serverName, log, requestObserver: null, out server, out error);

    /// <summary>
    /// 绑定 <see cref="HttpListener"/>，并可选地将已完成的请求转发给 UI。
    /// </summary>
    /// <param name="host">要绑定的网络接口，通常为 <c>127.0.0.1</c>。</param>
    /// <param name="requestedPort">请求的端口，0 表示由系统选择。</param>
    /// <param name="serverName">向客户端报告的服务器名称。</param>
    /// <param name="log">控制台日志记录器；WPF 模式下传入 writer 为 null 的日志记录器。</param>
    /// <param name="requestObserver">可选回调，每个已完成的请求都会调用。</param>
    /// <param name="server">成功时返回绑定的服务器。</param>
    /// <param name="error">便于阅读的失败描述。</param>
    /// <returns>成功绑定监听器时返回 <see langword="true"/>。</returns>
    internal static bool TryCreate(
        string host,
        int requestedPort,
        string serverName,
        ConsoleLog log,
        Action<RequestLogEntry>? requestObserver,
        out HttpMcpServer? server,
        out string? error)
    {
        var attempts = requestedPort == 0 ? 20 : 21;
        string? lastError = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var candidate = requestedPort == 0
                ? FindFreePort()
                : requestedPort + attempt;

            if (candidate < 1 || candidate > 65535)
            {
                lastError = "端口号超出有效范围。";
                break;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add("http://" + host + ":" + candidate + "/");
            try
            {
                listener.Start();
                server = new HttpMcpServer(listener, host, candidate, serverName, log, requestObserver);
                error = null;
                return true;
            }
            catch (Exception ex) when (
                ex is HttpListenerException or UnauthorizedAccessException or InvalidOperationException)
            {
                lastError = ex.Message;
                try
                {
                    listener.Close();
                }
                catch (Exception)
                {
                    // 监听器启动失败；关闭操作是尽力而为。
                }
            }
        }

        server = null;
        error = lastError ?? "无法绑定监听端口。";
        return false;
    }

    /// <summary>查找当前空闲的回环 TCP 端口。</summary>
    /// <returns>空闲端口号。</returns>
    internal static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>接受并处理请求，直到取消 Token 被触发或监听器停止。</summary>
    /// <param name="ct">关闭 Token。</param>
    internal async Task RunAsync(CancellationToken ct)
    {
        var activeRequests = new ConcurrentDictionary<Task, byte>();

        using (ct.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
                // 已经停止。
            }
        }))
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    try
                    {
                        context.Response.Abort();
                    }
                    catch (Exception)
                    {
                        // 客户端连接已经断开。
                    }

                    break;
                }

                var handler = Task.Run(() => HandleContextAsync(context));
                activeRequests.TryAdd(handler, 0);
                _ = handler.ContinueWith(
                    completed =>
                    {
                        activeRequests.TryRemove(completed, out _);
                        _ = completed.Exception;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        // 给正在执行的请求一点时间收尾，避免关闭时客户端只看到连接被重置。
        var pending = Task.WhenAll(activeRequests.Keys);
        await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessions.Clear();

        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // 已经停止。
        }

        _listener.Close();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath ?? "/";
            if (!string.Equals(path.TrimEnd('/'), ServerMetadata.McpPath, StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(response, 404, "未找到该路径。MCP 端点为 /mcp。").ConfigureAwait(false);
                Report(request.HttpMethod + " " + path, null, stopwatch.Elapsed, "404 路径不存在");
                return;
            }

            var protocolHeader = request.Headers["MCP-Protocol-Version"];
            if (!string.IsNullOrEmpty(protocolHeader)
                && !ServerMetadata.IsSupportedProtocolVersion(protocolHeader)
                && Interlocked.Exchange(ref _unsupportedVersionWarned, 1) == 0)
            {
                // 未知版本不拒绝，避免与较新或较旧的客户端完全不兼容；仅提示一次。
                _log.Warn("收到未声明的 MCP 协议版本 " + protocolHeader + "，将按兼容模式继续。");
            }

            switch (request.HttpMethod)
            {
                case "POST":
                    await HandlePostAsync(context, protocolHeader, stopwatch).ConfigureAwait(false);
                    break;

                case "GET":
                    response.AddHeader("Allow", "POST, DELETE");
                    await WriteTextAsync(
                        response,
                        405,
                        "本示例未实现服务器主动推送的 SSE 流，请使用 POST /mcp 发送 JSON-RPC 请求。")
                        .ConfigureAwait(false);
                    Report("GET /mcp", null, stopwatch.Elapsed, "405 不支持 SSE");
                    break;

                case "DELETE":
                    var sessionId = request.Headers["Mcp-Session-Id"];
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        _sessions.TryRemove(sessionId, out _);
                    }

                    response.StatusCode = 204;
                    response.ContentLength64 = 0;
                    Report("DELETE /mcp", null, stopwatch.Elapsed, "204 会话已结束");
                    break;

                default:
                    response.AddHeader("Allow", "POST, DELETE");
                    await WriteTextAsync(response, 405, "仅支持 POST 与 DELETE。").ConfigureAwait(false);
                    Report(request.HttpMethod + " /mcp", null, stopwatch.Elapsed, "405 方法不支持");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("处理 HTTP 请求失败：" + ex.Message);
            try
            {
                if (!response.OutputStream.CanWrite)
                {
                    return;
                }

                await WriteTextAsync(response, 500, "服务器内部错误。").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 响应可能已开始发送，或客户端已断开连接。
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception)
            {
                // 关闭已中止的响应不会造成影响。
            }
        }
    }

    private async Task HandlePostAsync(
        HttpListenerContext context,
        string? protocolHeader,
        Stopwatch stopwatch)
    {
        var request = context.Request;
        var response = context.Response;

        string body;
        using (var reader = new StreamReader(
                   request.InputStream,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                   detectEncodingFromByteOrderMarks: false,
                   bufferSize: 4096,
                   leaveOpen: true))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            await WriteTextAsync(response, 400, "请求体为空，需要 JSON-RPC 2.0 消息。").ConfigureAwait(false);
            Report("POST /mcp", null, stopwatch.Elapsed, "400 空请求体");
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // MCP Streamable HTTP 规范要求无效 JSON 返回 HTTP 400。
            await WriteTextAsync(response, 400, "请求体不是有效的 JSON。").ConfigureAwait(false);
            Report("POST /mcp", null, stopwatch.Elapsed, "400 JSON 无效");
            return;
        }

        using (document)
        {
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object)
            {
                await WriteTextAsync(response, 400, "JSON-RPC 消息必须是单个 JSON 对象（不支持批量请求）。")
                    .ConfigureAwait(false);
                Report("POST /mcp", null, stopwatch.Elapsed, "400 非对象消息");
                return;
            }

            var method = message.TryGetProperty("method", out var methodElement)
                         && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()
                : null;
            var hasId = message.TryGetProperty("id", out var idElement)
                        && idElement.ValueKind != JsonValueKind.Null;
            var isInitialize = hasId && string.Equals(method, "initialize", StringComparison.Ordinal);

            var sessionHeader = request.Headers["Mcp-Session-Id"];
            if (!string.IsNullOrEmpty(sessionHeader)
                && !isInitialize
                && !_sessions.ContainsKey(sessionHeader))
            {
                // MCP 规范：会话已过期或不存在时返回 404，客户端应重新 initialize。
                await WriteTextAsync(response, 404, "MCP 会话不存在或已过期，请重新初始化。").ConfigureAwait(false);
                Report(method ?? "POST /mcp", null, stopwatch.Elapsed, "404 会话无效");
                return;
            }

            var reply = _protocol.Handle(message);

            if (reply.IsNotification)
            {
                response.StatusCode = 202;
                response.ContentLength64 = 0;
                if (!string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
                {
                    Report(method ?? "通知", null, stopwatch.Elapsed, "202 通知");
                }

                return;
            }

            response.StatusCode = 200;
            response.ContentType = "application/json; charset=utf-8";

            if (isInitialize)
            {
                var sessionId = Guid.NewGuid().ToString("N");
                _sessions.TryAdd(sessionId, 0);
                response.AddHeader("Mcp-Session-Id", sessionId);
            }

            var responseVersion = !string.IsNullOrWhiteSpace(protocolHeader)
                ? protocolHeader!
                : reply.ProtocolVersion ?? ServerMetadata.DefaultProtocolVersion;
            response.AddHeader("MCP-Protocol-Version", responseVersion);

            var bytes = Encoding.UTF8.GetBytes(reply.Json ?? "{}");
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);

            var outcome = reply.IsError ? "200 工具错误" : "200";
            Report(reply.Method ?? method ?? "unknown", reply.ToolName, stopwatch.Elapsed, outcome);
        }
    }

    /// <summary>将已完成的请求报告到控制台日志以及可选的 UI 观察器。</summary>
    private void Report(string method, string? toolName, TimeSpan elapsed, string outcome)
    {
        _log.Request(method, toolName, elapsed, outcome);
        if (_requestObserver == null)
        {
            return;
        }

        try
        {
            _requestObserver(new RequestLogEntry(DateTime.Now, method, toolName, elapsed, outcome));
        }
        catch (Exception)
        {
            // UI 回调出错绝不能影响请求处理。
        }
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, int statusCode, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }
}
