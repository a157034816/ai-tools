using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace AiTools.Core.Http;

/// <summary>按配置创建或复用 HttpClient，避免每次 new 造成的端口耗尽。</summary>
public static class HttpClientProvider
{
    private static readonly ConcurrentDictionary<string, HttpClient> Cache =
        new ConcurrentDictionary<string, HttpClient>(StringComparer.Ordinal);

    public static HttpClient Resolve(AiHttpOptions options, string providerName)
    {
        if (options.HttpClient != null)
        {
            return options.HttpClient;
        }

        var key = providerName + "|" + (options.BaseAddress?.ToString() ?? string.Empty);
        return Cache.GetOrAdd(key, _ =>
        {
            var handler = CreateHandler();
            var client = new HttpClient(handler, disposeHandler: true)
            {
                // 超时由每个请求的 CancellationToken 控制，避免长连接被 HttpClient 提前掐断。
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };

            if (options.BaseAddress != null)
            {
                client.BaseAddress = options.BaseAddress;
            }

            return client;
        });
    }

    private static HttpMessageHandler CreateHandler()
    {
#if NETSTANDARD2_0
        return new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };
#else
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        return handler;
#endif
    }
}
