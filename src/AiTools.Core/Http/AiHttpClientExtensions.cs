using System;
using System.Net.Http;
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

    /// <summary>发送请求并在失败时按重试策略重试，返回成功响应。</summary>
    public static Task<HttpResponseMessage> SendWithRetryAsync(
        this HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        string providerName,
        AiHttpOptions options,
        CancellationToken cancellationToken)
    {
        return RetryPolicy.ExecuteAsync(
            ct => httpClient.SendAndEnsureAsync(requestFactory, providerName, ct),
            providerName,
            options.MaxRetries,
            options.RetryBaseDelay,
            cancellationToken);
    }
}
