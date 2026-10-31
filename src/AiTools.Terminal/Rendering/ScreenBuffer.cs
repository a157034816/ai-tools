using System;
using System.Collections.Generic;
using System.Text;

namespace AiTools.Terminal.Rendering;

/// <summary>虚拟屏幕中的一个单元格。</summary>
internal struct ScreenCell : IEquatable<ScreenCell>
{
    public char Character;

    public TextStyle Style;

    /// <summary>宽字符右半部分占位：渲染时跳过，避免重复写入。</summary>
    public bool Continuation;

    public static ScreenCell Blank(TextStyle style) => new() { Character = ' ', Style = style };

    public bool Equals(ScreenCell other)
        => Character == other.Character && Continuation == other.Continuation && Style.Equals(other.Style);

    public override bool Equals(object? obj) => obj is ScreenCell other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Character, Style, Continuation);
}

/// <summary>
/// 一份虚拟屏幕：按单元格记录字符与样式，渲染时与上一帧做逐行 diff，
/// 只重绘发生变化的行。这样流式输出时不会整屏闪烁，长会话也不必每帧重排。
/// </summary>
public sealed class ScreenBuffer
{
    private readonly List<ScreenCell[]> _rows = new();

    /// <summary>获取屏幕宽度（列）。</summary>
    public int Width { get; private set; }

    /// <summary>获取屏幕高度（行）。</summary>
    public int Height { get; private set; }

    /// <summary>获取或设置光标位置（列、行）；null 表示隐藏光标。</summary>
    public (int X, int Y)? LastCursor { get; set; }

    /// <summary>调整尺寸；已有内容被裁剪或补白。</summary>
    /// <param name="width">列数。</param>
    /// <param name="height">行数。</param>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height)
        {
            return;
        }

        while (_rows.Count < height)
        {
            var row = new ScreenCell[width];
            Array.Fill(row, ScreenCell.Blank(TextStyle.Default));
            _rows.Add(row);
        }

        if (_rows.Count > height)
        {
            _rows.RemoveRange(height, _rows.Count - height);
        }

        for (var index = 0; index < _rows.Count; index++)
        {
            if (_rows[index].Length != width)
            {
                var row = new ScreenCell[width];
                Array.Fill(row, ScreenCell.Blank(TextStyle.Default));
                Array.Copy(_rows[index], row, Math.Min(_rows[index].Length, width));
                _rows[index] = row;
            }
        }

        Width = width;
        Height = height;
    }

    /// <summary>清空全部单元格（保留尺寸）。</summary>
    public void Clear()
    {
        Clear(TextStyle.Default);
    }

    /// <summary>用给定样式清空全部单元格。</summary>
    /// <param name="style">单元格样式。</param>
    public void Clear(TextStyle style)
    {
        foreach (var row in _rows)
        {
            Array.Fill(row, ScreenCell.Blank(style));
        }
    }

    /// <summary>清空一行。</summary>
    /// <param name="row">行号。</param>
    /// <param name="style">单元格样式。</param>
    public void ClearRow(int row, TextStyle style)
    {
        if (row >= 0 && row < _rows.Count)
        {
            Array.Fill(_rows[row], ScreenCell.Blank(style));
        }
    }

    /// <summary>在指定位置写入单个字符；越界或宽字符右半格会被忽略。</summary>
    /// <param name="x">列。</param>
    /// <param name="y">行。</param>
    /// <param name="character">字符。</param>
    /// <param name="style">样式。</param>
    public void Set(int x, int y, char character, TextStyle style)
    {
        if (x < 0 || y < 0 || y >= _rows.Count || x >= Width)
        {
            return;
        }

        var row = _rows[y];
        if (row[x].Continuation)
        {
            return;
        }

        row[x] = new ScreenCell { Character = character, Style = style };
    }

    /// <summary>把一段带样式的文本写入一行，自动处理宽字符与截断。</summary>
    /// <param name="x">起始列。</param>
    /// <param name="y">行号。</param>
    /// <param name="segments">文本片段。</param>
    /// <param name="maxWidth">可用宽度；-1 表示到行尾。</param>
    /// <param name="fillStyle">
    /// 非 null 时先用它铺满可用宽度的空白，保证背景色连续（用于输入框、状态栏）。
    /// </param>
    /// <returns>实际写入的列数。</returns>
    public int Write(int x, int y, IEnumerable<StyledText> segments, int maxWidth = -1, TextStyle? fillStyle = null)
    {
        if (y < 0 || y >= _rows.Count)
        {
            return 0;
        }

        var limit = maxWidth < 0 ? Width - x : Math.Min(maxWidth, Width - x);
        if (limit <= 0)
        {
            return 0;
        }

        if (fillStyle.HasValue)
        {
            for (var offset = 0; offset < limit; offset++)
            {
                Set(x + offset, y, ' ', fillStyle.Value);
            }
        }

        var column = 0;
        foreach (var segment in segments)
        {
            if (column >= limit)
            {
                break;
            }

            var text = segment.Text;
            for (var index = 0; index < text.Length && column < limit;)
            {
                if (text[index] == '\n' || text[index] == '\r')
                {
                    index++;
                    continue;
                }

                var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
                var codePoint = char.ConvertToUtf32(text, index);
                var cellWidth = TextWidth.Of(codePoint);
                if (cellWidth == 0)
                {
                    index += length;
                    continue;
                }

                if (column + cellWidth > limit)
                {
                    // 宽字符放不下时截断，避免半个汉字。
                    break;
                }

                if (cellWidth == 1)
                {
                    Set(x + column, y, text[index], segment.Style);
                }
                else
                {
                    // 宽字符写入前两格：首格存字符，次格标记为占位。
                    Set(x + column, y, text[index], segment.Style);
                    if (x + column + 1 < Width && y < _rows.Count)
                    {
                        _rows[y][x + column + 1] = new ScreenCell
                        {
                            Character = ' ',
                            Style = segment.Style,
                            Continuation = true
                        };
                    }
                }

                column += cellWidth;
                index += length;
            }
        }

        return column;
    }

    /// <summary>获取一行中指定区间的内容，用于 diff 与测试。</summary>
    /// <param name="row">行号。</param>
    internal ReadOnlySpan<ScreenCell> GetRow(int row) => _rows[row];

    internal int RowCount => _rows.Count;

    /// <summary>把一行复制到另一个缓冲区；仅在两者宽度一致时复制。</summary>
    /// <param name="row">源行号。</param>
    /// <param name="target">目标缓冲区。</param>
    /// <param name="targetRow">目标行号。</param>
    internal void CopyRowTo(int row, ScreenBuffer target, int targetRow)
    {
        if (row < 0 || row >= _rows.Count || targetRow < 0 || targetRow >= target._rows.Count)
        {
            return;
        }

        Array.Copy(_rows[row], target._rows[targetRow], Math.Min(_rows[row].Length, target._rows[targetRow].Length));
    }
}

/// <summary>
/// 帧合成器：把虚拟屏幕与上一帧对比，生成最小的 ANSI 更新序列。
/// </summary>
public sealed class FrameComposer
{
    private readonly StringBuilder _output = new(16 * 1024);
    private ScreenBuffer? _previous;
    private int _previousWidth;
    private int _previousHeight;

    /// <summary>
    /// 比较 <paramref name="current"/> 与上一帧，返回从左上角开始逐行更新的 ANSI 序列。
    /// 首帧或尺寸变化时返回整屏重绘。
    /// </summary>
    /// <param name="current">当前帧。</param>
    /// <param name="fullRepaint">是否强制整屏重绘。</param>
    public string Compose(ScreenBuffer current, bool fullRepaint = false)
    {
        _output.Clear();

        var sizeChanged = _previous == null || _previousWidth != current.Width || _previousHeight != current.Height;
        if (fullRepaint || sizeChanged)
        {
            _output.Append("\u001b[0m\u001b[H\u001b[2J");
            for (var row = 0; row < current.Height; row++)
            {
                AppendRow(current, row);
            }
        }
        else
        {
            for (var row = 0; row < current.Height; row++)
            {
                if (RowEquals(current, _previous!, row))
                {
                    continue;
                }

                AppendRow(current, row);
            }
        }

        _output.Append("\u001b[0m");
        Snapshot(current);
        return _output.ToString();
    }

    /// <summary>
    /// 生成从某一行的起始位置开始写入的序列（输入框等局部重绘），
    /// 不参与整帧 diff。行号从 0 开始。
    /// </summary>
    /// <param name="current">当前帧。</param>
    /// <param name="row">行号。</param>
    public static string ComposeRow(ScreenBuffer current, int row)
    {
        if (row < 0 || row >= current.Height)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(current.Width * 4 + 32);
        builder.Append("\u001b[").Append(row + 1).Append(";1H\u001b[0m");

        var span = current.GetRow(row);
        var activeStyle = TextStyle.Default;
        for (var column = 0; column < span.Length; column++)
        {
            var cell = span[column];
            if (cell.Continuation)
            {
                continue;
            }

            if (!cell.Style.Equals(activeStyle))
            {
                AppendStyle(builder, cell.Style);
                activeStyle = cell.Style;
            }

            builder.Append(cell.Character);
        }

        builder.Append("\u001b[0m");
        return builder.ToString();
    }

    private void AppendRow(ScreenBuffer current, int row)
    {
        _output.Append("\u001b[").Append(row + 1).Append(";1H");

        var span = current.GetRow(row);
        var activeStyle = TextStyle.Default;
        AppendStyle(_output, activeStyle);

        for (var column = 0; column < span.Length; column++)
        {
            var cell = span[column];
            if (cell.Continuation)
            {
                continue;
            }

            if (!cell.Style.Equals(activeStyle))
            {
                AppendStyle(_output, cell.Style);
                activeStyle = cell.Style;
            }

            _output.Append(cell.Character);
        }
    }

    private static void AppendStyle(StringBuilder builder, TextStyle style)
    {
        var sgr = style.ToSgr();
        builder.Append(sgr.Length == 0 ? "\u001b[0m" : sgr);
    }

    private static bool RowEquals(ScreenBuffer current, ScreenBuffer previous, int row)
    {
        if (row >= previous.Height)
        {
            return false;
        }

        var left = current.GetRow(row);
        var right = previous.GetRow(row);
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (!left[index].Equals(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private void Snapshot(ScreenBuffer current)
    {
        _previous?.Resize(current.Width, current.Height);
        _previous ??= new ScreenBuffer();
        _previous.Resize(current.Width, current.Height);
        for (var row = 0; row < current.Height; row++)
        {
            current.CopyRowTo(row, _previous, row);
        }

        _previousWidth = current.Width;
        _previousHeight = current.Height;
    }
}
