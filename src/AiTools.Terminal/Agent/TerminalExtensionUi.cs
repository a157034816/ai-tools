using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Extensions;

namespace AiTools.Terminal.Agent;

/// <summary>通知的严重级别（与扩展层的 <see cref="ExtensionNoticeKind"/> 一一对应）。</summary>
public enum NoticeKind
{
    /// <summary>中性信息。</summary>
    Info,

    /// <summary>成功。</summary>
    Success,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error
}

/// <summary>
/// 交互式提问的宿主接缝。TUI 实现它把问题画成浮层；无界面宿主返回默认值。
/// </summary>
public interface IInteractivePrompt
{
    /// <summary>获取宿主是否支持交互。</summary>
    bool IsInteractive { get; }

    /// <summary>让用户确认；不支持交互时返回 <paramref name="defaultValue"/>。</summary>
    Task<bool> ConfirmAsync(string title, string message, bool defaultValue, CancellationToken cancellationToken = default);

    /// <summary>让用户输入文本；取消或不支持交互时返回 null。</summary>
    Task<string?> InputAsync(string title, string? placeholder, CancellationToken cancellationToken = default);

    /// <summary>让用户从选项中选择；取消或不支持交互时返回 null。</summary>
    Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default);

    /// <summary>让用户从带说明的选项中选择；取消或不支持交互时返回选项索引 null。</summary>
    Task<int?> ChooseAsync(
        string title,
        string? message,
        IReadOnlyList<PromptChoice> choices,
        CancellationToken cancellationToken = default);
}

/// <summary>一个带说明的选项。</summary>
public sealed class PromptChoice
{
    /// <summary>创建选项。</summary>
    /// <param name="label">短标签。</param>
    /// <param name="description">说明。</param>
    public PromptChoice(string label, string? description)
    {
        Label = label ?? string.Empty;
        Description = description;
    }

    /// <summary>获取短标签。</summary>
    public string Label { get; }

    /// <summary>获取说明。</summary>
    public string? Description { get; }
}

/// <summary>无界面宿主：所有交互返回默认值，通知直接输出到控制台。</summary>
public sealed class HeadlessPrompt : IInteractivePrompt
{
    /// <summary>获取单例。</summary>
    public static HeadlessPrompt Instance { get; } = new();

    /// <inheritdoc />
    public bool IsInteractive => false;

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, bool defaultValue, CancellationToken cancellationToken = default)
        => Task.FromResult(defaultValue);

    /// <inheritdoc />
    public Task<string?> InputAsync(string title, string? placeholder, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<int?> ChooseAsync(string title, string? message, IReadOnlyList<PromptChoice> choices, CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);
}

/// <summary>
/// 把扩展的 UI 接缝桥接到终端宿主：通知与状态栏走会话更新，
/// 确认/输入/选择转发给 <see cref="IInteractivePrompt"/>。
/// </summary>
public sealed class TerminalExtensionUi : IExtensionUi, IExtensionChoiceUi
{
    private readonly Action<string, NoticeKind> _notify;
    private readonly Action<string?> _setStatus;
    private readonly IInteractivePrompt _prompt;

    /// <summary>创建桥。</summary>
    /// <param name="notify">显示通知。</param>
    /// <param name="setStatus">更新状态栏；null 清除。</param>
    /// <param name="prompt">交互式提问宿主。</param>
    public TerminalExtensionUi(Action<string, NoticeKind> notify, Action<string?> setStatus, IInteractivePrompt prompt)
    {
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
        _setStatus = setStatus;
        _prompt = prompt ?? HeadlessPrompt.Instance;
    }

    /// <inheritdoc />
    public bool HasDialogs => _prompt.IsInteractive;

    /// <inheritdoc />
    public void Notify(string message, ExtensionNoticeKind kind = ExtensionNoticeKind.Info)
        => _notify(message, Map(kind));

    /// <inheritdoc />
    public void SetStatus(string? text) => _setStatus?.Invoke(text);

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, bool defaultValue = false, CancellationToken cancellationToken = default)
        => _prompt.ConfirmAsync(title, message, defaultValue, cancellationToken);

    /// <inheritdoc />
    public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default)
        => _prompt.InputAsync(title, placeholder, cancellationToken);

    /// <inheritdoc />
    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
        => _prompt.SelectAsync(title, options, cancellationToken);

    /// <inheritdoc />
    public async Task<ExtensionChoiceResult?> ChooseAsync(
        string title,
        string? message,
        IReadOnlyList<ExtensionChoice> choices,
        CancellationToken cancellationToken = default)
    {
        if (choices == null || choices.Count == 0)
        {
            return null;
        }

        var promptChoices = new List<PromptChoice>(choices.Count);
        foreach (var choice in choices)
        {
            promptChoices.Add(new PromptChoice(choice.Label, choice.Description));
        }

        var index = await _prompt.ChooseAsync(title, message, promptChoices, cancellationToken).ConfigureAwait(false);
        if (index is not { } selected || selected < 0 || selected >= choices.Count)
        {
            return null;
        }

        return new ExtensionChoiceResult
        {
            Index = selected,
            Label = choices[selected].Label,
            Description = choices[selected].Description
        };
    }

    private static NoticeKind Map(ExtensionNoticeKind kind)
        => kind switch
        {
            ExtensionNoticeKind.Success => NoticeKind.Success,
            ExtensionNoticeKind.Warning => NoticeKind.Warning,
            ExtensionNoticeKind.Error => NoticeKind.Error,
            _ => NoticeKind.Info
        };
}
