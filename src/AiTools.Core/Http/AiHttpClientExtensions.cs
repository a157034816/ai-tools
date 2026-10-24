using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Core.Http;

/// <summary>发送请求并确保非成功响应转换为统一的异常。</summary>
public static class AiHttpClientExtensions
{
    /// <summary>
    /// 发送请求并校验状态码；失败时读取响应正文并抛出 <see cref="AiTools.Abstractions.AiServiceException"/> 的对应子类。
    /// 注意：返回的响应需要调用方负责释放。
    /// </summary>
    public static async Task<HttpResponseMessage> SendAndEnsureAsync(
        this HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        string providerName,
        CancellationToken cancellationToken)
    {
        if (httpClient == null)
        {
            throw new ArgumentNullException(nameof(httpClient));
        }

        if (requestFactory == null)
        {
            throw new ArgumentNullException(nameof(requestFactory));
        }

        HttpResponseMessage response;
        using (var request = requestFactory())
        {
            response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var retryAfter = AiErrorMapper.ParseRetryAfter(response);
            var statusCode = response.StatusCode;
            response.Dispose();
            throw AiErrorMapper.Create(providerName, statusCode, body, retryAfter);
        }

        return response;
    }

    /// <summary>
    /// 发送请求并校验状态码，同时把请求头、请求体与响应交给
    /// <see cref="AiHttpOptions.Observer"/>。未配置观察器时等价于 <see cref="SendAndEnsureAsync"/>。
    /// </summary>
    /// <param name="httpClient">HTTP 客户端。</param>
    /// <param name="requestFactory">请求工厂（重试时会再次调用）。</param>
    /// <param name="context">请求元数据。</param>
    /// <param name="options">提供观察器的选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="AiRequestBlockedException">观察器阻止了该请求。</exception>
    public static async Task<HttpResponseMessage> SendObservedAsync(
        this HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        AiRequestContext context,
        AiHttpOptions options,
        CancellationToken cancellationToken)
    {
        var observer = options?.Observer;
        if (observer == null)
        {
            return await httpClient.SendAndEnsureAsync(requestFactory, context.ProviderName, cancellationToken).ConfigureAwait(false);
        }

        using var request = PrepareObservedRequest(requestFactory, context, observer);
        var stopwatch = Stopwatch.StartNew();
        var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        observer.OnAfterResponse(context, response, stopwatch.Elapsed);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var retryAfter = AiErrorMapper.ParseRetryAfter(response);
            var statusCode = response.StatusCode;
            response.Dispose();
            throw AiErrorMapper.Create(context.ProviderName, statusCode, body, retryAfter);
        }

        return response;
    }

    /// <summary>带观察器的重试发送。</summary>
    public static Task<HttpResponseMessage> SendObservedWithRetryAsync(
        this HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        AiRequestContext context,
        AiHttpOptions options,
        CancellationToken cancellationToken)
        => RetryPolicy.ExecuteAsync(
            ct => httpClient.SendObservedAsync(requestFactory, context, options, ct),
            context.ProviderName,
            options.MaxRetries,
            options.RetryBaseDelay,
            cancellationToken);

    private static HttpRequestMessage PrepareObservedRequest(
        Func<HttpRequestMessage> requestFactory,
        AiRequestContext context,
        IAiHttpObserver observer)
    {
        var request = requestFactory();
        var headers = request.Headers
            .Where(header => !IsRestrictedHeader(header.Key))
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);

        observer.OnBeforeHeaders(context, headers);

        foreach (var header in headers)
        {
            if (request.Headers.Contains(header.Key))
            {
                request.Headers.Remove(header.Key);
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content != null)
        {
            var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var replacement = observer.OnBeforeRequest(context, body);
            if (replacement != null && !string.Equals(replacement, body, StringComparison.Ordinal))
            {
                var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
                request.Content.Dispose();
                request.Content = new StringContent(replacement, Encoding.UTF8, mediaType);
            }
        }

        return request;
    }

    private static bool IsRestrictedHeader(string name)
        => name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);

    /// <summary>发送请求并在失败时按重试策略重试，返回成功响应。</summary>
    /// <param name="httpClient">HTTP 客户端。</param>
    /// <param name="requestFactory">请求工厂（重试时会再次调用）。</param>
    /// <param name="providerName">服务商标识。</param>
    /// <param name="options">选项；<see cref="AiHttpOptions.Observer"/> 非空时走观察路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="context">可选的请求元数据（模型、操作类别、是否流式）。</param>
    public static Task<HttpResponseMessage> SendWithRetryAsync(
        this HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        string providerName,
        AiHttpOptions options,
        CancellationToken cancellationToken,
        AiRequestContext? context = null)
    {
        if (options?.Observer == null)
        {
            return RetryPolicy.ExecuteAsync(
                ct => httpClient.SendAndEnsureAsync(requestFactory, providerName, ct),
                providerName,
                options?.MaxRetries ?? 3,
                options?.RetryBaseDelay ?? TimeSpan.FromMilliseconds(500),
                cancellationToken);
        }

        var resolved = context ?? new AiRequestContext { ProviderName = providerName };
        if (string.IsNullOrEmpty(resolved.ProviderName))
        {
            resolved = new AiRequestContext
            {
                ProviderName = providerName,
                Operation = resolved.Operation,
                Model = resolved.Model,
                IsStreaming = resolved.IsStreaming
            };
        }

        return httpClient.SendObservedWithRetryAsync(requestFactory, resolved, options!, cancellationToken);
    }
}
