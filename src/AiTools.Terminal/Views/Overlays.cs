using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AiTools.Terminal.Input;
using AiTools.Terminal.Rendering;

namespace AiTools.Terminal.Views;

/// <summary>屏幕上的矩形区域。</summary>
public readonly struct Rect
{
    /// <summary>创建矩形。</summary>
    /// <param name="x">左边界（列）。</param>
    /// <param name="y">上边界（行）。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    public Rect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>获取左边界。</summary>
    public int X { get; }

    /// <summary>获取上边界。</summary>
    public int Y { get; }

    /// <summary>获取宽度。</summary>
    public int Width { get; }

    /// <summary>获取高度。</summary>
    public int Height { get; }

    /// <summary>获取右边界（不含）。</summary>
    public int Right => X + Width;

    /// <summary>获取下边界（不含）。</summary>
    public int Bottom => Y + Height;
}

/// <summary>覆盖在记录区之上的浮层。</summary>
public abstract class Overlay
{
    /// <summary>获取浮层标题。</summary>
    public abstract string Title { get; }

    /// <summary>获取补充说明。</summary>
    public virtual string? Message => null;

    /// <summary>获取浮层希望的高度（不含边框）。</summary>
    /// <param name="maxHeight">可用高度。</param>
    public abstract int DesiredHeight(int maxHeight);

    /// <summary>绘制浮层内容（不含边框）。</summary>
    /// <param name="buffer">目标屏幕。</param>
    /// <param name="rect">内容区域。</param>
    /// <param name="theme">主题。</param>
    public abstract void Draw(ScreenBuffer buffer, Rect rect, TerminalTheme theme);

    /// <summary>处理按键。</summary>
    /// <param name="key">按键。</param>
    /// <returns>浮层是否应当关闭。</returns>
    public abstract bool HandleKey(TerminalKey key);

    /// <summary>获取底部提示。</summary>
    public virtual string Hint => "↑↓ 选择   Enter 确认   Esc 取消";
}

/// <summary>一个可筛选的列表浮层。选择结果通过 <see cref="Result"/> 返回，取消时结果为 null。</summary>
public sealed class ListOverlay : Overlay
{
    private readonly List<(string Label, string? Description)> _items;
    private readonly List<int> _filtered = new();
    private readonly TaskCompletionSource<int?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _filter = string.Empty;
    private int _selected;

    /// <summary>创建列表浮层。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">补充说明。</param>
    /// <param name="items">选项。</param>
    /// <param name="initialSelection">初始选中项。</param>
    /// <param name="allowFilter">是否允许输入筛选。</param>
    public ListOverlay(
        string title,
        string? message,
        IReadOnlyList<(string Label, string? Description)> items,
        int initialSelection = 0,
        bool allowFilter = true)
    {
        Title = title ?? string.Empty;
        Message = message;
        _items = new List<(string, string?)>(items);
        AllowFilter = allowFilter;
        ApplyFilter(initialSelection);
    }

    /// <summary>获取选择结果；取消时为 null。</summary>
    public Task<int?> Result => _completion.Task;

    /// <inheritdoc />
    public override string Title { get; }

    /// <inheritdoc />
    public override string? Message { get; }

    /// <summary>获取是否允许输入筛选。</summary>
    public bool AllowFilter { get; }

    /// <summary>获取当前筛选文本。</summary>
    public string Filter => _filter;

    /// <summary>以程序方式确认选择。</summary>
    /// <param name="index">要选择的原始索引。</param>
    public void Select(int index)
    {
        _completion.TrySetResult(index);
    }

    /// <summary>以程序方式取消。</summary>
    public void Cancel() => _completion.TrySetResult(null);

    /// <inheritdoc />
    public override int DesiredHeight(int maxHeight)
    {
        var lines = Math.Max(1, _filtered.Count);
        if (AllowFilter)
        {
            lines++;
        }

        if (!string.IsNullOrEmpty(Message))
        {
            lines++;
        }

        return Math.Min(maxHeight, lines);
    }

    /// <inheritdoc />
    public override void Draw(ScreenBuffer buffer, Rect rect, TerminalTheme theme)
    {
        var row = rect.Y;
        var width = rect.Width;

        if (!string.IsNullOrEmpty(Message) && row < rect.Bottom)
        {
            buffer.Write(rect.X, row, new[] { new StyledText(TextWidth.Truncate(Message!, width), theme.OverlayMutedStyle) }, width);
            row++;
        }

        if (AllowFilter && row < rect.Bottom)
        {
            var filterText = _filter.Length == 0 ? "输入以筛选…" : _filter;
            var style = _filter.Length == 0 ? theme.OverlayMutedStyle.WithItalic() : theme.OverlayStyle;
            buffer.Write(rect.X, row, new[]
            {
                new StyledText("筛选  ", theme.OverlayMutedStyle),
                new StyledText(TextWidth.Truncate(filterText, Math.Max(1, width - 6)), style)
            }, width);
            row++;
        }

        for (var index = 0; index < _filtered.Count && row < rect.Bottom; index++)
        {
            var itemIndex = _filtered[index];
            var item = _items[itemIndex];
            var selected = index == _selected;
            var style = selected ? theme.OverlaySelectionStyle : theme.OverlayStyle;
            var marker = selected ? (theme.Unicode ? "› " : "> ") : "  ";
            var labelWidth = Math.Max(1, width - 2);

            var segments = new List<StyledText>
            {
                new(marker, style),
                new(TextWidth.Truncate(item.Label, labelWidth), style)
            };

            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                var used = TextWidth.Of(marker) + TextWidth.Of(item.Label);
                var remaining = width - used - 2;
                if (remaining > 8)
                {
                    segments.Add(new StyledText(
                        "  " + TextWidth.Truncate(item.Description, remaining),
                        selected ? theme.OverlaySelectionStyle : theme.OverlayMutedStyle));
                }
            }

            buffer.Write(rect.X, row, segments, width, selected ? theme.OverlaySelectionStyle : (TextStyle?)null);
            row++;
        }

        if (_filtered.Count == 0 && row < rect.Bottom)
        {
            buffer.Write(rect.X, row, new[] { new StyledText("没有匹配项", theme.OverlayMutedStyle) }, width);
        }
    }

    /// <inheritdoc />
    public override bool HandleKey(TerminalKey key)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.ArrowUp:
                Move(-1);
                return false;
            case TerminalKeyKind.ArrowDown:
                Move(1);
                return false;
            case TerminalKeyKind.PageUp:
                Move(-8);
                return false;
            case TerminalKeyKind.PageDown:
                Move(8);
                return false;
            case TerminalKeyKind.Enter:
                if (_filtered.Count > 0)
                {
                    _completion.TrySetResult(_filtered[_selected]);
                }

                return true;
            case TerminalKeyKind.Escape:
                _completion.TrySetResult(null);
                return true;
            case TerminalKeyKind.Backspace:
                if (AllowFilter && _filter.Length > 0)
                {
                    _filter = _filter.Substring(0, _filter.Length - 1);
                    ApplyFilter(0);
                }

                return false;
            case TerminalKeyKind.Character when key.IsCtrl:
                if (key.Text == "c")
                {
                    _completion.TrySetResult(null);
                    return true;
                }

                return false;
            case TerminalKeyKind.Character:
                if (AllowFilter)
                {
                    _filter += key.Text;
                    ApplyFilter(0);
                }

                return false;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    public override string Hint => AllowFilter
        ? "↑↓ 选择   Enter 确认   Esc 取消   输入筛选"
        : "↑↓ 选择   Enter 确认   Esc 取消";

    private void Move(int delta)
    {
        if (_filtered.Count == 0)
        {
            return;
        }

        _selected = (_selected + delta) % _filtered.Count;
        if (_selected < 0)
        {
            _selected += _filtered.Count;
        }
    }

    private void ApplyFilter(int selection)
    {
        _filtered.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            if (_filter.Length == 0 ||
                _items[index].Label.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (_items[index].Description?.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            {
                _filtered.Add(index);
            }
        }

        _selected = _filtered.Count == 0 ? 0 : Math.Clamp(selection, 0, _filtered.Count - 1);
    }
}

/// <summary>单行文本输入浮层。</summary>
public sealed class TextInputOverlay : Overlay
{
    private readonly LineEditor _editor = new();
    private readonly TaskCompletionSource<string?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _placeholder;

    /// <summary>创建输入浮层。</summary>
    /// <param name="title">标题。</param>
    /// <param name="placeholder">占位符。</param>
    /// <param name="initial">初始文本。</param>
    public TextInputOverlay(string title, string? placeholder, string? initial = null)
    {
        Title = title ?? string.Empty;
        _placeholder = placeholder;
        if (!string.IsNullOrEmpty(initial))
        {
            _editor.Text = initial!;
            _editor.MoveEnd();
        }
    }

    /// <summary>获取输入结果；取消时为 null。</summary>
    public Task<string?> Result => _completion.Task;

    /// <inheritdoc />
    public override string Title { get; }

    /// <inheritdoc />
    public override int DesiredHeight(int maxHeight) => Math.Min(maxHeight, 1);

    /// <inheritdoc />
    public override void Draw(ScreenBuffer buffer, Rect rect, TerminalTheme theme)
    {
        _editor.Placeholder = _placeholder ?? string.Empty;
        _editor.Render(rect.Width, theme, "> ", out var segments, out _);
        buffer.Write(rect.X, rect.Y, segments, rect.Width, theme.InputStyle.WithBackground(theme.SurfaceAlt));
    }

    /// <inheritdoc />
    public override bool HandleKey(TerminalKey key)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Enter:
                _completion.TrySetResult(_editor.Text);
                return true;
            case TerminalKeyKind.Escape:
                _completion.TrySetResult(null);
                return true;
            case TerminalKeyKind.Backspace:
                _editor.Backspace();
                return false;
            case TerminalKeyKind.Delete:
                _editor.Delete();
                return false;
            case TerminalKeyKind.ArrowLeft:
                if (key.IsCtrl)
                {
                    _editor.MoveWordLeft();
                }
                else
                {
                    _editor.MoveLeft();
                }

                return false;
            case TerminalKeyKind.ArrowRight:
                if (key.IsCtrl)
                {
                    _editor.MoveWordRight();
                }
                else
                {
                    _editor.MoveRight();
                }

                return false;
            case TerminalKeyKind.Home:
                _editor.MoveHome();
                return false;
            case TerminalKeyKind.End:
                _editor.MoveEnd();
                return false;
            case TerminalKeyKind.Paste:
                _editor.Insert(key.Text);
                return false;
            case TerminalKeyKind.Character when key.IsCtrl:
                if (key.Text == "c")
                {
                    _completion.TrySetResult(null);
                    return true;
                }

                if (key.Text == "u")
                {
                    _editor.DeleteToStart();
                }

                return false;
            case TerminalKeyKind.Character:
                _editor.Insert(key.Text);
                return false;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    public override string Hint => "Enter 确认   Esc 取消";
}

/// <summary>只读帮助浮层。</summary>
public sealed class HelpOverlay : Overlay
{
    private readonly IReadOnlyList<(string Key, string Description)> _bindings;
    private readonly string? _message;

    /// <summary>创建帮助浮层。</summary>
    /// <param name="title">标题。</param>
    /// <param name="bindings">按键说明。</param>
    /// <param name="message">额外说明段落。</param>
    public HelpOverlay(string title, IReadOnlyList<(string Key, string Description)> bindings, string? message = null)
    {
        Title = title;
        _bindings = bindings;
        _message = message;
    }

    /// <inheritdoc />
    public override string Title { get; }

    /// <inheritdoc />
    public override int DesiredHeight(int maxHeight)
        => Math.Min(maxHeight, _bindings.Count + (string.IsNullOrEmpty(_message) ? 0 : 2));

    /// <inheritdoc />
    public override void Draw(ScreenBuffer buffer, Rect rect, TerminalTheme theme)
    {
        var row = rect.Y;
        var keyWidth = 0;
        foreach (var binding in _bindings)
        {
            keyWidth = Math.Max(keyWidth, TextWidth.Of(binding.Key));
        }

        foreach (var binding in _bindings)
        {
            if (row >= rect.Bottom)
            {
                break;
            }

            buffer.Write(rect.X, row, new[]
            {
                new StyledText(TextWidth.PadRight(binding.Key, keyWidth), theme.OverlayTitleStyle),
                new StyledText("   " + binding.Description, theme.OverlayStyle)
            }, rect.Width);
            row++;
        }

        if (!string.IsNullOrEmpty(_message) && row + 1 < rect.Bottom)
        {
            row++;
            buffer.Write(rect.X, row, new[] { new StyledText(TextWidth.Truncate(_message!, rect.Width), theme.OverlayMutedStyle) }, rect.Width);
        }
    }

    /// <inheritdoc />
    public override bool HandleKey(TerminalKey key)
        => key.Kind is TerminalKeyKind.Escape or TerminalKeyKind.Enter ||
           (key.Kind == TerminalKeyKind.Character && key.IsCtrl && key.Text == "c");
}

/// <summary>确认浮层（是/否）。</summary>
public sealed class ConfirmOverlay : Overlay
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string[] _options = { "允许", "拒绝" };
    private readonly bool _defaultYes;
    private int _selected;

    /// <summary>创建确认浮层。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">说明。</param>
    /// <param name="defaultYes">默认是否选中"允许"。</param>
    public ConfirmOverlay(string title, string message, bool defaultYes)
    {
        Title = title;
        Message = message;
        _defaultYes = defaultYes;
        _selected = defaultYes ? 0 : 1;
    }

    /// <summary>获取确认结果。</summary>
    public Task<bool> Result => _completion.Task;

    /// <inheritdoc />
    public override string Title { get; }

    /// <inheritdoc />
    public override string? Message { get; }

    /// <inheritdoc />
    public override int DesiredHeight(int maxHeight) => Math.Min(maxHeight, 2);

    /// <inheritdoc />
    public override void Draw(ScreenBuffer buffer, Rect rect, TerminalTheme theme)
    {
        for (var index = 0; index < _options.Length && rect.Y + index < rect.Bottom; index++)
        {
            var selected = index == _selected;
            var style = selected ? theme.OverlaySelectionStyle : theme.OverlayStyle;
            buffer.Write(
                rect.X,
                rect.Y + index,
                new[]
                {
                    new StyledText(selected ? "› " : "  ", style),
                    new StyledText(_options[index], style)
                },
                rect.Width,
                selected ? theme.OverlaySelectionStyle : (TextStyle?)null);
        }
    }

    /// <inheritdoc />
    public override bool HandleKey(TerminalKey key)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.ArrowLeft:
            case TerminalKeyKind.ArrowUp:
                _selected = 0;
                return false;
            case TerminalKeyKind.ArrowRight:
            case TerminalKeyKind.ArrowDown:
                _selected = 1;
                return false;
            case TerminalKeyKind.Tab:
                _selected = 1 - _selected;
                return false;
            case TerminalKeyKind.Enter:
                _completion.TrySetResult(_selected == 0);
                return true;
            case TerminalKeyKind.Escape:
                _completion.TrySetResult(false);
                return true;
            case TerminalKeyKind.Character when key.IsCtrl && key.Text == "c":
                _completion.TrySetResult(false);
                return true;
            case TerminalKeyKind.Character:
                if (key.Text is "y" or "Y")
                {
                    _completion.TrySetResult(true);
                    return true;
                }

                if (key.Text is "n" or "N")
                {
                    _completion.TrySetResult(false);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <inheritdoc />
    public override string Hint => "←→ 选择   Enter 确认   Esc 取消";
}
