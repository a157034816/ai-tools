using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>
/// 宿主的交互界面能力（对应 Pi 的 <c>ctx.ui</c>）。无 UI 的宿主可以提供 null，
/// 扩展必须处理"宿主没有 UI"的情况。
/// </summary>
public interface IExtensionUi
{
    /// <summary>获取宿主是否支持交互式对话框。</summary>
    bool HasDialogs { get; }

    /// <summary>显示一条通知。</summary>
    void Notify(string message, ExtensionNoticeKind kind = ExtensionNoticeKind.Info);

    /// <summary>更新状态栏文本；传 null 清除。</summary>
    void SetStatus(string? text);

    /// <summary>让用户确认；不支持对话框时返回 <paramref name="defaultValue"/>。</summary>
    Task<bool> ConfirmAsync(string title, string message, bool defaultValue = false, CancellationToken cancellationToken = default);

    /// <summary>让用户输入文本；取消时返回 null。</summary>
    Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default);

    /// <summary>让用户从选项中选择；取消时返回 null。</summary>
    Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default);
}

/// <summary>把扩展当作 provider 观察器接入 HTTP 层。</summary>
internal sealed class ExtensionHttpObserver : AiTools.Core.Http.IAiHttpObserver
{
    private readonly ExtensionRuntime _runtime;
    private readonly Func<CommandContext> _contextFactory;

    public ExtensionHttpObserver(ExtensionRuntime runtime, Func<CommandContext> contextFactory)
    {
        _runtime = runtime;
        _contextFactory = contextFactory;
    }

    public void OnBeforeHeaders(AiTools.Core.Http.AiRequestContext context, IDictionary<string, string> headers)
    {
        var payload = new ProviderHeadersEvent
        {
            ProviderName = context.ProviderName,
            Headers = headers
        };

        _runtime.DispatchAsync(ExtensionEvent.BeforeProviderHeaders, payload, _contextFactory())
            .GetAwaiter()
            .GetResult();
    }

    public string? OnBeforeRequest(AiTools.Core.Http.AiRequestContext context, string bodyJson)
    {
        var payload = new ProviderRequestEvent
        {
            ProviderName = context.ProviderName,
            Operation = context.Operation,
            BodyJson = bodyJson,
            IsStreaming = context.IsStreaming
        };

        var result = _runtime.DispatchAsync(
                ExtensionEvent.BeforeProviderRequest,
                payload,
                _contextFactory(),
                failClosed: payload)
            .GetAwaiter()
            .GetResult();

        var resolved = result as ProviderRequestEvent ?? payload;
        if (!string.IsNullOrEmpty(resolved.BlockReason))
        {
            throw new AiTools.Core.Http.AiRequestBlockedException(resolved.BlockReason!);
        }

        return string.Equals(resolved.BodyJson, bodyJson, StringComparison.Ordinal) ? null : resolved.BodyJson;
    }

    public void OnAfterResponse(AiTools.Core.Http.AiRequestContext context, System.Net.Http.HttpResponseMessage response, TimeSpan duration)
    {
        var payload = new ProviderResponseEvent
        {
            ProviderName = context.ProviderName,
            StatusCode = (int)response.StatusCode,
            ContentLength = response.Content?.Headers.ContentLength ?? 0,
            Duration = duration
        };

        _runtime.PublishAsync(ExtensionEvent.AfterProviderResponse, payload, _contextFactory())
            .GetAwaiter()
            .GetResult();
    }

    public void OnStreamEvent(AiTools.Core.Http.AiRequestContext context, string? eventType, string data)
    {
        var payload = new ProviderStreamEvent
        {
            ProviderName = context.ProviderName,
            EventType = eventType,
            Data = data
        };

        _runtime.PublishAsync(ExtensionEvent.ProviderStreamEvent, payload, _contextFactory())
            .GetAwaiter()
            .GetResult();
    }
}
