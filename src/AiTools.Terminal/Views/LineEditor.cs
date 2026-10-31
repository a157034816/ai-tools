using System;
using System.Collections.Generic;
using System.Text;
using AiTools.Terminal.Input;
using AiTools.Terminal.Rendering;

namespace AiTools.Terminal.Views;

/// <summary>
/// 单行输入编辑器：维护文本、光标、历史与水平滚动。
/// 支持在光标处插入、删除、Home/End、Ctrl+←/→ 按词移动，以及与命令面板配合的前缀查询。
/// </summary>
public sealed class LineEditor
{
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _draft = string.Empty;
    private string _text = string.Empty;
    private int _cursor;
    private int _scroll;

    /// <summary>获取或设置输入文本。</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            _cursor = Math.Min(_cursor, _text.Length);
            _scroll = 0;
        }
    }

    /// <summary>获取光标位置（UTF-16 索引）。</summary>
    public int Cursor => _cursor;

    /// <summary>获取是否为空。</summary>
    public bool IsEmpty => _text.Length == 0;

    /// <summary>获取输入历史（最旧在前）。</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>获取或设置占位符。</summary>
    public string Placeholder { get; set; } = "输入消息，/ 查看命令，? 查看快捷键";

    /// <summary>获取当前光标是否在开头。</summary>
    public bool IsAtStart => _cursor <= 0;

    /// <summary>获取当前光标是否在末尾。</summary>
    public bool IsAtEnd => _cursor >= _text.Length;

    /// <summary>清空输入与历史浏览状态。</summary>
    public void Clear()
    {
        _text = string.Empty;
        _cursor = 0;
        _scroll = 0;
        _historyIndex = -1;
        _draft = string.Empty;
    }

    /// <summary>在光标处插入文本（粘贴或输入法上屏时可能包含多行）。</summary>
    /// <param name="text">要插入的文本。</param>
    public void Insert(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 输入框保持单行：换行折叠成空格，避免历史记录里出现隐式换行。
        var normalized = text!.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        _text = _text.Insert(_cursor, normalized);
        _cursor += normalized.Length;
    }

    /// <summary>退格：删除光标前的一个字符。</summary>
    public void Backspace()
    {
        if (_cursor <= 0)
        {
            return;
        }

        var length = char.IsLowSurrogate(_text[_cursor - 1]) && _cursor >= 2 && char.IsHighSurrogate(_text[_cursor - 2]) ? 2 : 1;
        _text = _text.Remove(_cursor - length, length);
        _cursor -= length;
    }

    /// <summary>删除光标处的一个字符。</summary>
    public void Delete()
    {
        if (_cursor >= _text.Length)
        {
            return;
        }

        var length = char.IsHighSurrogate(_text[_cursor]) && _cursor + 1 < _text.Length ? 2 : 1;
        _text = _text.Remove(_cursor, length);
    }

    /// <summary>删除光标左侧的一个词。</summary>
    public void DeleteWordBackward()
    {
        if (_cursor <= 0)
        {
            return;
        }

        var start = PreviousWordStart();
        _text = _text.Remove(start, _cursor - start);
        _cursor = start;
    }

    /// <summary>删除光标到行尾的内容（Ctrl+K）。</summary>
    public void DeleteToEnd()
    {
        if (_cursor < _text.Length)
        {
            _text = _text.Substring(0, _cursor);
        }
    }

    /// <summary>删除光标到行首的内容（Ctrl+U）。</summary>
    public void DeleteToStart()
    {
        if (_cursor > 0)
        {
            _text = _text.Substring(_cursor);
            _cursor = 0;
        }
    }

    /// <summary>光标左移一个字符。</summary>
    public void MoveLeft()
    {
        if (_cursor <= 0)
        {
            return;
        }

        _cursor -= char.IsLowSurrogate(_text[_cursor - 1]) && _cursor >= 2 && char.IsHighSurrogate(_text[_cursor - 2]) ? 2 : 1;
    }

    /// <summary>光标右移一个字符。</summary>
    public void MoveRight()
    {
        if (_cursor >= _text.Length)
        {
            return;
        }

        _cursor += char.IsHighSurrogate(_text[_cursor]) && _cursor + 1 < _text.Length ? 2 : 1;
    }

    /// <summary>光标移到行首。</summary>
    public void MoveHome() => _cursor = 0;

    /// <summary>光标移到行尾。</summary>
    public void MoveEnd() => _cursor = _text.Length;

    /// <summary>光标左移一个词。</summary>
    public void MoveWordLeft() => _cursor = PreviousWordStart();

    /// <summary>光标右移一个词。</summary>
    public void MoveWordRight()
    {
        var index = _cursor;
        while (index < _text.Length && IsWordSeparator(_text[index]))
        {
            index++;
        }

        while (index < _text.Length && !IsWordSeparator(_text[index]))
        {
            index++;
        }

        _cursor = index;
    }

    /// <summary>取历史中的上一条（ArrowUp）。</summary>
    /// <returns>是否切换了历史。</returns>
    public bool HistoryPrevious()
    {
        if (_history.Count == 0)
        {
            return false;
        }

        if (_historyIndex < 0)
        {
            _draft = _text;
            _historyIndex = _history.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }
        else
        {
            return false;
        }

        _text = _history[_historyIndex];
        _cursor = _text.Length;
        return true;
    }

    /// <summary>取历史中的下一条（ArrowDown）。</summary>
    /// <returns>是否切换了历史。</returns>
    public bool HistoryNext()
    {
        if (_historyIndex < 0)
        {
            return false;
        }

        if (_historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            _text = _history[_historyIndex];
        }
        else
        {
            _historyIndex = -1;
            _text = _draft;
        }

        _cursor = _text.Length;
        return true;
    }

    /// <summary>把当前文本记入历史（去重相邻重复）。</summary>
    public void CommitHistory()
    {
        var trimmed = _text.Trim();
        if (trimmed.Length > 0 && (_history.Count == 0 || !string.Equals(_history[_history.Count - 1], trimmed, StringComparison.Ordinal)))
        {
            _history.Add(trimmed);
            if (_history.Count > 200)
            {
                _history.RemoveAt(0);
            }
        }

        _historyIndex = -1;
        _draft = string.Empty;
    }

    /// <summary>从外部载入历史（例如从文件恢复）。</summary>
    /// <param name="items">历史条目。</param>
    public void SeedHistory(IEnumerable<string> items)
    {
        _history.Clear();
        _history.AddRange(items);
        _historyIndex = -1;
    }

    /// <summary>把输入渲染成一行片段，并计算光标所在的显示列。</summary>
    /// <param name="width">可用列数（不含提示符）。</param>
    /// <param name="theme">主题。</param>
    /// <param name="prompt">提示符文本。</param>
    /// <param name="segments">输出片段。</param>
    /// <param name="cursorColumn">光标列（相对提示符之后）。</param>
    public void Render(int width, TerminalTheme theme, string prompt, out IReadOnlyList<StyledText> segments, out int cursorColumn)
    {
        var list = new List<StyledText>(4);
        var promptStyle = theme.AccentStyle.WithBold();
        list.Add(new StyledText(prompt, promptStyle));

        var prefixWidth = TextWidth.Of(prompt);
        var available = Math.Max(1, width - prefixWidth);

        if (_text.Length == 0)
        {
            list.Add(new StyledText(TextWidth.Truncate(Placeholder, available, "…"), theme.InputPlaceholderStyle));
            cursorColumn = prefixWidth;
            segments = list;
            return;
        }

        var cursorWidth = TextWidth.Of(_text.Substring(0, _cursor));

        // 光标接近右边界时滚动，保证光标始终可见。
        if (cursorWidth - _scroll >= available)
        {
            _scroll = cursorWidth - available + 1;
        }
        else if (cursorWidth < _scroll)
        {
            _scroll = cursorWidth;
        }

        _scroll = Math.Max(0, _scroll);

        var visibleStart = IndexAtWidth(_text, _scroll);
        var (visible, visibleWidth) = Fit(_text, visibleStart, available);

        list.Add(new StyledText(visible, theme.InputStyle));

        // 用空格补齐到可用宽度，让输入框背景连续。
        if (visibleWidth < available)
        {
            list.Add(new StyledText(new string(' ', available - visibleWidth), theme.InputStyle));
        }

        cursorColumn = prefixWidth + Math.Max(0, cursorWidth - _scroll);
        cursorColumn = Math.Min(cursorColumn, prefixWidth + available - 1);
        segments = list;
    }

    private static (string Text, int Width) Fit(string text, int start, int maxWidth)
    {
        var count = TextWidth.CountFit(text, start, maxWidth, out var width, out _);
        return (text.Substring(start, count), width);
    }

    private static int IndexAtWidth(string text, int targetWidth)
    {
        if (targetWidth <= 0)
        {
            return 0;
        }

        var width = 0;
        var index = 0;
        while (index < text.Length)
        {
            var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
            var cell = TextWidth.Of(char.ConvertToUtf32(text, index));
            if (width + cell > targetWidth)
            {
                break;
            }

            width += cell;
            index += length;
        }

        return index;
    }

    private int PreviousWordStart()
    {
        var index = _cursor;
        while (index > 0 && IsWordSeparator(_text[index - 1]))
        {
            index--;
        }

        while (index > 0 && !IsWordSeparator(_text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static bool IsWordSeparator(char character)
        => char.IsWhiteSpace(character) || character is '/' or '\\' or '.' or ',' or ';' or ':' or '(' or ')' or '[' or ']';
}
