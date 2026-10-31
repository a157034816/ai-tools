using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Extensions;

/// <summary>
/// 扩展会话：把运行时、状态与插件命令表绑到一次对话上，并负责
/// <c>session_start</c> / <c>session_shutdown</c> 的分发。
/// </summary>
/// <remarks>
/// 与 Pi 一致：扩展在工厂（<see cref="IExtension.Activate"/>）里不启动长生命周期资源，
/// 而在 <c>session_start</c> 启动、在 <c>session_shutdown</c> 幂等释放。
/// 会话可被替换（<c>/new</c>、<c>/resume</c>）；关闭后旧会话的上下文不再可用。
/// </remarks>
public sealed class ExtensionSession : IAsyncDisposable
{
    private readonly List<Func<ValueTask>> _cleanup = new();
    private readonly object _gate = new();
    private int _disposed;

    private ExtensionSession(ExtensionRuntime runtime, string workingDirectory)
    {
        Runtime = runtime;
        WorkingDirectory = workingDirectory;
        State = runtime.State;
    }

    /// <summary>获取扩展运行时。</summary>
    public ExtensionRuntime Runtime { get; }

    /// <summary>获取会话状态。</summary>
    public ExtensionStateStore State { get; }

    /// <summary>获取工作目录。</summary>
    public string WorkingDirectory { get; }

    /// <summary>获取会话 ID；尚未持久化的会话为 null。</summary>
    public string? SessionId { get; private set; }

    /// <summary>获取会话是否已启动。</summary>
    public bool IsStarted { get; private set; }

    /// <summary>获取会话是否已关闭。</summary>
    public bool IsShutdown => Volatile.Read(ref _disposed) != 0;

    /// <summary>创建一个会话（尚未启动）。</summary>
    /// <param name="runtime">扩展运行时。</param>
    /// <param name="workingDirectory">工作目录。</param>
    public static ExtensionSession Create(ExtensionRuntime runtime, string? workingDirectory = null)
    {
        if (runtime == null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        return new ExtensionSession(runtime, string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory!);
    }

    /// <summary>注册一个会话级清理回调。回调可能被执行多次检查，因此必须幂等。</summary>
    public void RegisterCleanup(Func<ValueTask> cleanup)
    {
        if (cleanup == null)
        {
            throw new ArgumentNullException(nameof(cleanup));
        }

        lock (_gate)
        {
            _cleanup.Add(cleanup);
        }
    }

    /// <summary>启动会话并分发 <c>session_start</c>。重复调用无副作用。</summary>
    /// <param name="sessionId">会话 ID（可为 null）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task StartAsync(string? sessionId, CancellationToken cancellationToken = default)
        => StartAsync(sessionId, "startup", cancellationToken);

    /// <summary>启动会话并分发 <c>session_start</c>。重复调用无副作用。</summary>
    /// <param name="sessionId">会话 ID（可为 null）。</param>
    /// <param name="reason">启动原因：<c>startup</c>、<c>reload</c> 或 <c>resume</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StartAsync(string? sessionId, string reason, CancellationToken cancellationToken = default)
    {
        if (IsShutdown)
        {
            throw new InvalidOperationException("The extension session has already been shut down.");
        }

        if (IsStarted)
        {
            return;
        }

        IsStarted = true;
        SessionId = sessionId;

        var context = CreateContext(notify: null, appendAssistantMessage: null);
        await Runtime.PublishAsync(
            ExtensionEvent.SessionStart,
            new SessionStartEvent
            {
                SessionId = sessionId,
                WorkingDirectory = WorkingDirectory,
                Reason = string.IsNullOrWhiteSpace(reason) ? "startup" : reason!
            },
            context,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 会话替换前分发 <c>session_before_switch</c>，让扩展有机会取消迁移（例如有未保存的草稿）。
    /// </summary>
    /// <param name="reason">迁移原因：<c>new</c>、<c>resume</c>、<c>fork</c> 或 <c>clear</c>。</param>
    /// <param name="targetSessionId">目标会话 ID；<c>new</c> / <c>clear</c> 为 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>取消原因；未被取消时为 null。</returns>
    public async Task<string?> PublishBeforeSwitchAsync(
        string reason,
        string? targetSessionId,
        CancellationToken cancellationToken = default)
    {
        var payload = new SessionBeforeSwitchEvent { Reason = reason, TargetSessionId = targetSessionId };
        var dispatched = await Runtime
            .DispatchAsync(ExtensionEvent.SessionBeforeSwitch, payload, CreateContext(null, null), failClosed: payload, cancellationToken)
            .ConfigureAwait(false);

        return (dispatched as SessionBeforeSwitchEvent ?? payload).CancelReason;
    }

    /// <summary>会话替换后分发 <c>session_switched</c>，让扩展按新分支重建状态。</summary>
    /// <param name="reason">迁移原因：<c>new</c>、<c>resume</c>、<c>fork</c> 或 <c>clear</c>。</param>
    /// <param name="previousSessionId">上一个会话 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task PublishSwitchedAsync(
        string reason,
        string? previousSessionId,
        CancellationToken cancellationToken = default)
        => Runtime.PublishAsync(
            ExtensionEvent.SessionSwitched,
            new SessionSwitchedEvent
            {
                Reason = reason,
                PreviousSessionId = previousSessionId,
                SessionId = SessionId
            },
            CreateContext(null, null),
            cancellationToken);

    /// <summary>更新会话 ID（会话首次持久化后调用，让扩展能读到稳定标识）。</summary>
    public void UpdateSessionId(string? sessionId) => SessionId = sessionId;

    /// <summary>
    /// 关闭会话：按与注册相反的顺序执行清理回调，并分发 <c>session_shutdown</c>。
    /// 幂等；多次调用只有第一次生效。
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var context = CreateContext(notify: null, appendAssistantMessage: null);
        try
        {
            await Runtime.PublishAsync(
                ExtensionEvent.SessionShutdown,
                new SessionStartEvent { SessionId = SessionId, WorkingDirectory = WorkingDirectory },
                context,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 关闭阶段不应抛出。
        }

        Func<ValueTask>[] cleanup;
        lock (_gate)
        {
            cleanup = _cleanup.ToArray();
            _cleanup.Clear();
        }

        for (var index = cleanup.Length - 1; index >= 0; index--)
        {
            try
            {
                await cleanup[index]().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 单个清理失败不应阻止其它清理。
            }
        }
    }

    /// <summary>获取或设置宿主的交互界面（对应 Pi 的 <c>ctx.ui</c>）。</summary>
    public IExtensionUi? Ui { get; set; }

    /// <summary>为当前会话构建命令上下文。</summary>
    public CommandContext CreateContext(
        Action<string, ExtensionNoticeKind>? notify,
        Action<string>? appendAssistantMessage)
        => new()
        {
            Runtime = Runtime,
            State = State,
            SessionId = SessionId,
            WorkingDirectory = WorkingDirectory,
            Ui = Ui,
            Notify = notify,
            AppendAssistantMessage = appendAssistantMessage,
            EntryStore = EntryStore
        };

    /// <summary>
    /// 自定义条目存储：读取时 <paramref name="value"/> 为 null，写入时非 null。
    /// 条目保存在 <see cref="ExtensionStateStore"/> 的 <c>entries.</c> 命名空间下，因此会随会话持久化。
    /// </summary>
    private object? EntryStore(string kind, object? value)
    {
        const string extensionBucket = "__session_entries__";
        var key = "entries." + kind;

        if (value == null)
        {
            return State.Get(extensionBucket, key);
        }

        State.Set(extensionBucket, key, value);
        return value;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(ShutdownAsync());
}
