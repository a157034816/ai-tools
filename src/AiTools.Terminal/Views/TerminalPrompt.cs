using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Terminal.Agent;
using AiTools.Terminal.Input;
using AiTools.Terminal.Views;

namespace AiTools.Terminal.Views;

/// <summary>一次待用户回答的交互请求。</summary>
internal sealed class PromptRequest
{
    public PromptRequest(PromptRequestKind kind, string title, string? message)
    {
        Kind = kind;
        Title = title;
        Message = message;
        Completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public PromptRequestKind Kind { get; }

    public string Title { get; }

    public string? Message { get; }

    public IReadOnlyList<PromptChoice> Choices { get; init; } = Array.Empty<PromptChoice>();

    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    public string? Placeholder { get; init; }

    public bool DefaultValue { get; init; }

    public TaskCompletionSource<object?> Completion { get; }
}

internal enum PromptRequestKind
{
    Confirm,
    Input,
    Select,
    Choose
}

/// <summary>
/// 终端交互宿主：把确认/输入/选择请求排入队列，由 TUI 主循环以浮层形式呈现，
/// 并把结果回填给等待的调用方（扩展工具、权限闸门等）。
/// </summary>
public sealed class TerminalPrompt : IInteractivePrompt
{
    private readonly ConcurrentQueue<PromptRequest> _pending = new();
    private readonly object _gate = new();
    private PromptRequest? _active;

    /// <summary>获取是否处于交互式 TUI 中。</summary>
    public bool IsInteractive { get; internal set; }

    /// <summary>获取当前是否有待处理的交互请求。</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _active != null || !_pending.IsEmpty;
            }
        }
    }

    /// <summary>取出下一个待展示的请求；没有时返回 null。</summary>
    internal PromptRequest? TryTake()
    {
        lock (_gate)
        {
            if (_active != null)
            {
                return null;
            }

            if (_pending.TryDequeue(out var request))
            {
                _active = request;
            }

            return _active;
        }
    }

    /// <summary>完成当前请求。</summary>
    /// <param name="request">请求。</param>
    /// <param name="result">结果。</param>
    internal void Complete(PromptRequest request, object? result)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, request))
            {
                return;
            }

            _active = null;
        }

        request.Completion.TrySetResult(result);
    }

    /// <summary>取消全部挂起的请求（会话退出时）。</summary>
    internal void CancelAll()
    {
        lock (_gate)
        {
            if (_active != null)
            {
                _active.Completion.TrySetResult(null);
                _active = null;
            }
        }

        while (_pending.TryDequeue(out var request))
        {
            request.Completion.TrySetResult(null);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, bool defaultValue, CancellationToken cancellationToken = default)
    {
        if (!IsInteractive)
        {
            return defaultValue;
        }

        var request = new PromptRequest(PromptRequestKind.Confirm, title, message)
        {
            DefaultValue = defaultValue
        };

        var result = await EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        return result is bool approved && approved;
    }

    /// <inheritdoc />
    public async Task<string?> InputAsync(string title, string? placeholder, CancellationToken cancellationToken = default)
    {
        if (!IsInteractive)
        {
            return null;
        }

        var request = new PromptRequest(PromptRequestKind.Input, title, null)
        {
            Placeholder = placeholder
        };

        var result = await EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    /// <inheritdoc />
    public async Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
    {
        if (!IsInteractive)
        {
            return null;
        }

        var request = new PromptRequest(PromptRequestKind.Select, title, null)
        {
            Options = options
        };

        var result = await EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is int index && index >= 0 && index < options.Count)
        {
            return options[index];
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<int?> ChooseAsync(string title, string? message, IReadOnlyList<PromptChoice> choices, CancellationToken cancellationToken = default)
    {
        if (!IsInteractive)
        {
            return null;
        }

        var request = new PromptRequest(PromptRequestKind.Choose, title, message)
        {
            Choices = choices
        };

        var result = await EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        return result as int?;
    }

    private Task<object?> EnqueueAsync(PromptRequest request, CancellationToken cancellationToken)
    {
        _pending.Enqueue(request);
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                if (request.Completion.TrySetResult(null))
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_active, request))
                        {
                            _active = null;
                        }
                    }
                }
            });
        }

        return request.Completion.Task;
    }
}
