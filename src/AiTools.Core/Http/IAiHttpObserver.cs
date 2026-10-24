using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Core.Http;

/// <summary>
/// Provider 请求/流事件的订阅点。宿主（例如扩展运行时）可挂接它来观察或改写
/// 真正发出的请求与收到的流事件，而 Provider 本身无需知道扩展的存在。
/// </summary>
/// <remarks>
/// 挂接在 <see cref="AiHttpOptions"/> 上，因此只在显式配置时生效；
/// 未挂接时所有方法都是空操作，不产生分支成本。
/// </remarks>
public interface IAiHttpObserver
{
    /// <summary>请求头构造后、发出前调用；实现可就地修改 <paramref name="headers"/>。</summary>
    /// <param name="context">请求上下文。</param>
    /// <param name="headers">可写的请求头集合。</param>
    void OnBeforeHeaders(AiRequestContext context, IDictionary<string, string> headers);

    /// <summary>请求体序列化后、发出前调用；返回非 null 时代替原请求体。</summary>
    /// <param name="context">请求上下文。</param>
    /// <param name="bodyJson">原请求体。</param>
    /// <returns>替换后的请求体，或 null 表示不修改。</returns>
    /// <exception cref="AiRequestBlockedException">实现决定阻止该请求。</exception>
    string? OnBeforeRequest(AiRequestContext context, string bodyJson);

    /// <summary>收到响应头后调用（流式响应此时尚未读取）。</summary>
    /// <param name="context">请求上下文。</param>
    /// <param name="response">响应（只读观察）。</param>
    /// <param name="duration">从发出到收到响应头的耗时。</param>
    void OnAfterResponse(AiRequestContext context, HttpResponseMessage response, TimeSpan duration);

    /// <summary>流式解析出的每个事件。</summary>
    /// <param name="context">请求上下文。</param>
    /// <param name="eventType">SSE <c>event:</c> 字段。</param>
    /// <param name="data">事件数据。</param>
    void OnStreamEvent(AiRequestContext context, string? eventType, string data);
}

/// <summary>一次 provider 请求的元数据，供 <see cref="IAiHttpObserver"/> 使用。</summary>
public sealed class AiRequestContext
{
    /// <summary>获取服务商标识。</summary>
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>获取请求类别：<c>chat</c>、<c>embedding</c>、<c>image</c>、<c>audio</c>。</summary>
    public string Operation { get; init; } = "chat";

    /// <summary>获取模型名。</summary>
    public string? Model { get; init; }

    /// <summary>获取是否流式请求。</summary>
    public bool IsStreaming { get; init; }
}

/// <summary>观察器决定阻止请求时抛出。</summary>
public sealed class AiRequestBlockedException : Exception
{
    /// <summary>使用原因创建异常。</summary>
    public AiRequestBlockedException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    /// <summary>获取阻止原因。</summary>
    public string Reason { get; }
}
