using System.Net;
using System.Net.Http;
using System.Text;

namespace AiTools.Tests.Support;

/// <summary>可编程的假 HTTP 处理器，用于验证 Provider 的请求映射与响应解析。</summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _responder;

    public ScriptedHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    /// <summary>最近一次请求的 URL。</summary>
    public Uri? LastRequestUri { get; private set; }

    /// <summary>最近一次请求的方法。</summary>
    public HttpMethod? LastMethod { get; private set; }

    /// <summary>最近一次请求的请求头快照。</summary>
    public Dictionary<string, string[]> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次请求的正文。</summary>
    public string? LastRequestBody { get; private set; }

    /// <summary>所有已发送请求的正文。</summary>
    public List<string> RequestBodies { get; } = new();

    /// <summary>所有已发送请求的 URL。</summary>
    public List<Uri> RequestUris { get; } = new();

    public HttpClient CreateClient() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastMethod = request.RequestMethodOrNull();
        RequestUris.Add(request.RequestUri!);

        LastHeaders.Clear();
        foreach (var header in request.Headers)
        {
            LastHeaders[header.Key] = header.Value.ToArray();
        }

        string? body = null;
        if (request.Content != null)
        {
            // 内容头（如 multipart 的 Content-Type）挂在 Content.Headers 上，需要单独收集。
            foreach (var header in request.Content.Headers)
            {
                LastHeaders[header.Key] = header.Value.ToArray();
            }

            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            LastRequestBody = body;
            RequestBodies.Add(body);
        }

        return _responder(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Sse(string events, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };

    public static HttpResponseMessage Bytes(byte[] data, string contentType = "audio/mpeg")
        => new() { Content = new ByteArrayContent(data) };

    public static HttpResponseMessage Empty(HttpStatusCode status = HttpStatusCode.OK)
        => new(status);
}

internal static class HttpRequestMessageExtensions
{
    public static HttpMethod RequestMethodOrNull(this HttpRequestMessage request) => request.Method;
}
