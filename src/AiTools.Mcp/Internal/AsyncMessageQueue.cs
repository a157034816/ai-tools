using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Mcp;

/// <summary>
/// 简单的单消费者异步队列。用于将推送式生产者（进程读取器、SSE 流）桥接到
/// transport 的接收循环。
/// </summary>
internal sealed class AsyncMessageQueue<T>
{
    private readonly ConcurrentQueue<T> _items = new ConcurrentQueue<T>();
    private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
    private readonly object _gate = new object();
    private bool _completed;
    private Exception? _error;

    internal void Enqueue(T item)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _items.Enqueue(item);
        }

        ReleaseSignal();
    }

    internal void Complete(Exception? error = null)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _error = error;
        }

        ReleaseSignal();
    }

    internal async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            while (_items.TryDequeue(out var item))
            {
                yield return item;
            }

            bool completed;
            Exception? error;
            lock (_gate)
            {
                completed = _completed;
                error = _error;
            }

            if (!completed)
            {
                continue;
            }

            if (error != null)
            {
                throw error;
            }

            yield break;
        }
    }

    private void ReleaseSignal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 该队列仅供库内部使用；信号量已满意味着达到了默认最大值。
        }
        catch (ObjectDisposedException)
        {
            // 所属 transport 已被释放。
        }
    }
}

/// <summary>netstandard2.0 中未提供的 Task 辅助方法。</summary>
internal static class TaskExtensions
{
    internal static async Task WithCancellationAsync(this Task task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancellation.TrySetResult(true)))
        {
            if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await task.ConfigureAwait(false);
        }
    }

    internal static async Task<T> WithCancellationAsync<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancellation.TrySetResult(true)))
        {
            if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return await task.ConfigureAwait(false);
        }
    }
}
