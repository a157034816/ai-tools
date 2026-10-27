using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Wpf.Asking;

/// <summary>
/// 一次待用户回答的提问：标题、可选说明，以及若干带说明的选项。
/// 这是纯展示模型，视图可以直接绑定它；与扩展层的 <c>ExtensionChoice</c> 解耦。
/// </summary>
public sealed class AskPrompt
{
    /// <summary>创建提问。</summary>
    /// <param name="title">问题标题。</param>
    /// <param name="message">选项上方的补充说明；可为 null。</param>
    /// <param name="options">选项列表。</param>
    public AskPrompt(string title, string? message, IReadOnlyList<AskPromptOption> options)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A prompt title is required.", nameof(title));
        }

        Title = title;
        Message = message;
        Options = options ?? Array.Empty<AskPromptOption>();
    }

    /// <summary>获取问题标题。</summary>
    public string Title { get; }

    /// <summary>获取选项上方的补充说明。</summary>
    public string? Message { get; }

    /// <summary>获取选项列表。</summary>
    public IReadOnlyList<AskPromptOption> Options { get; }
}

/// <summary>提问中的一个选项：标签 + 说明 + 在列表中的索引。</summary>
public sealed class AskPromptOption
{
    /// <summary>创建选项。</summary>
    /// <param name="label">短标签。</param>
    /// <param name="description">该选项的含义或后果；可为 null。</param>
    /// <param name="index">该选项在列表中的索引。</param>
    public AskPromptOption(string label, string? description = null, int index = 0)
    {
        Label = label ?? string.Empty;
        Description = description;
        Index = index;
    }

    /// <summary>获取短标签。</summary>
    public string Label { get; }

    /// <summary>获取说明文本。</summary>
    public string? Description { get; }

    /// <summary>获取该选项在列表中的索引；视图可直接把它作为命令参数。</summary>
    public int Index { get; }
}

/// <summary>一次提问的结果：被选中的索引，或取消。</summary>
public sealed class AskResponse
{
    private AskResponse(int? selectedIndex) => SelectedIndex = selectedIndex;

    /// <summary>获取被选中的选项索引；取消时为 null。</summary>
    public int? SelectedIndex { get; }

    /// <summary>获取用户是否取消了提问。</summary>
    public bool IsCancelled => SelectedIndex == null;

    /// <summary>用户选择了某个选项。</summary>
    /// <param name="index">选项索引。</param>
    public static AskResponse Select(int index) => new(index);

    /// <summary>用户取消了提问。</summary>
    public static AskResponse Cancel() => new(null);
}

/// <summary>
/// 提问的呈现策略：决定一次 <c>ask</c> 如何展示给用户、以及如何得到答案。
/// </summary>
/// <remarks>
/// 默认实现是输入框上方的内联面板（<see cref="InlineAskPresenter"/>）；宿主可以换成模态对话框、
/// 控制台提示，或直接返回固定答案（自动化测试）。实现必须保证 <see cref="AskAsync"/> 返回后，
/// 自己弹出的界面已经关闭。
/// </remarks>
public interface IAskPresenter
{
    /// <summary>展示一次提问并等待用户回答；用户取消时返回 <see cref="AskResponse.Cancel"/>。</summary>
    /// <param name="prompt">提问内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AskResponse> AskAsync(AskPrompt prompt, CancellationToken cancellationToken = default);
}

/// <summary>
/// 内联呈现：把提问交给 <see cref="InlineAskHost"/>，由视图在聊天输入框上方绘制面板。
/// </summary>
/// <remarks>
/// 本类型不含任何 UI，只做"提问 → 宿主状态 → 用户选择 → 结果"的转接；
/// 面板长什么样完全由视图与宿主决定。
/// </remarks>
public sealed class InlineAskPresenter : IAskPresenter
{
    private readonly InlineAskHost _host;

    /// <summary>创建内联呈现器。</summary>
    /// <param name="host">宿主持有的面板状态。</param>
    public InlineAskPresenter(InlineAskHost host)
        => _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <inheritdoc />
    public Task<AskResponse> AskAsync(AskPrompt prompt, CancellationToken cancellationToken = default)
        => _host.ShowAsync(prompt, cancellationToken);
}

/// <summary>
/// 内联提问面板的宿主状态：维护"当前是否有提问"以及等待中的任务。
/// 视图把面板按钮绑定到 <see cref="Select"/> / <see cref="Cancel"/> 即可。
/// </summary>
public sealed class InlineAskHost
{
    private readonly object _gate = new();
    private PendingRequest? _pending;

    /// <summary>提问出现或消失时触发。可能来自任意线程，视图应自行封送到 UI 线程。</summary>
    public event EventHandler? Changed;

    /// <summary>获取当前是否有待回答的提问。</summary>
    public bool HasPrompt => Current != null;

    /// <summary>获取当前提问；没有时为 null。</summary>
    public AskPrompt? Current
    {
        get
        {
            lock (_gate)
            {
                return _pending?.Prompt;
            }
        }
    }

    /// <summary>
    /// 展示提问并等待答案。同一时刻只允许一个提问：新提问会把旧的那个按取消处理，
    /// 避免旧调用方永久等待。
    /// </summary>
    /// <param name="prompt">提问内容。</param>
    /// <param name="cancellationToken">取消令牌；取消时按用户取消处理。</param>
    public Task<AskResponse> ShowAsync(AskPrompt prompt, CancellationToken cancellationToken = default)
    {
        if (prompt == null)
        {
            throw new ArgumentNullException(nameof(prompt));
        }

        var request = new PendingRequest(prompt);
        PendingRequest? displaced;
        lock (_gate)
        {
            displaced = _pending;
            _pending = request;
        }

        displaced?.Completion.TrySetResult(AskResponse.Cancel());

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => Complete(request, AskResponse.Cancel()));
        }

        RaiseChanged();
        return request.Completion.Task;
    }

    /// <summary>用户选择了某个选项。</summary>
    /// <param name="index">选项索引。</param>
    public void Select(int index)
    {
        PendingRequest? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        if (pending != null)
        {
            Complete(pending, AskResponse.Select(index));
        }
    }

    /// <summary>用户取消了提问。</summary>
    public void Cancel()
    {
        PendingRequest? pending;
        lock (_gate)
        {
            pending = _pending;
        }

        if (pending != null)
        {
            Complete(pending, AskResponse.Cancel());
        }
    }

    private void Complete(PendingRequest request, AskResponse response)
    {
        lock (_gate)
        {
            // 只有仍是当前请求时才生效，避免迟到的取消覆盖新的提问。
            if (!ReferenceEquals(_pending, request))
            {
                return;
            }

            _pending = null;
        }

        RaiseChanged();
        request.Completion.TrySetResult(response);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class PendingRequest
    {
        public PendingRequest(AskPrompt prompt) => Prompt = prompt;

        public AskPrompt Prompt { get; }

        public TaskCompletionSource<AskResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
