using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using AiTools.Core.Sse;
using AiTools.Core.Http;

namespace AiTools.Core.Sse;

/// <summary>
/// 把 SSE/NDJSON 解析结果转发给 <see cref="IAiHttpObserver"/> 的辅助方法。
/// Provider 只需替换解析调用，无需了解观察器的存在。
/// </summary>
public static class ObservedSseParser
{
    /// <summary>解析 SSE 并逐个上报给观察器。<paramref name="observer"/> 为 null 时等价于原解析。</summary>
    /// <param name="stream">响应流。</param>
    /// <param name="observer">观察器；可为 null。</param>
    /// <param name="context">请求元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async IAsyncEnumerable<SseEvent> ParseObservedAsync(
        System.IO.Stream stream,
        IAiHttpObserver? observer,
        AiRequestContext? context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in SseParser.ParseAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (observer != null && context != null)
            {
                try
                {
                    observer.OnStreamEvent(context, evt.EventType, evt.Data);
                }
                catch (Exception)
                {
                    // 观察失败不影响流。
                }
            }

            yield return evt;
        }
    }

    /// <summary>解析 NDJSON 并逐个上报给观察器。</summary>
    /// <param name="stream">响应流。</param>
    /// <param name="observer">观察器；可为 null。</param>
    /// <param name="context">请求元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async IAsyncEnumerable<JsonElement> ParseNdJsonObservedAsync(
        System.IO.Stream stream,
        IAiHttpObserver? observer,
        AiRequestContext? context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var element in SseParser.ParseNdJsonAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (observer != null && context != null)
            {
                try
                {
                    observer.OnStreamEvent(context, null, element.GetRawText());
                }
                catch (Exception)
                {
                    // 观察失败不影响流。
                }
            }

            yield return element;
        }
    }
}
