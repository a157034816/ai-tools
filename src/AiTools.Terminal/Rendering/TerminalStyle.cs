using System;
using System.Collections.Generic;
using System.Globalization;

namespace AiTools.Terminal.Rendering;

/// <summary>终端颜色：默认色、16 色、256 色或真彩色。</summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    private TerminalColor(TerminalColorKind kind, byte r, byte g, byte b, byte index)
    {
        Kind = kind;
        R = r;
        G = g;
        B = b;
        Index = index;
    }

    /// <summary>获取颜色类型。</summary>
    public TerminalColorKind Kind { get; }

    /// <summary>获取红色分量（仅 <see cref="TerminalColorKind.Rgb"/>）。</summary>
    public byte R { get; }

    /// <summary>获取绿色分量（仅 <see cref="TerminalColorKind.Rgb"/>）。</summary>
    public byte G { get; }

    /// <summary>获取蓝色分量（仅 <see cref="TerminalColorKind.Rgb"/>）。</summary>
    public byte B { get; }

    /// <summary>获取调色板索引（<see cref="TerminalColorKind.Indexed"/>）。</summary>
    public byte Index { get; }

    /// <summary>使用终端默认前景/背景色。</summary>
    public static TerminalColor Default { get; } = new(TerminalColorKind.Default, 0, 0, 0, 0);

    /// <summary>使用 ANSI 16 色调色板。</summary>
    /// <param name="index">0-15 的调色板索引。</param>
    public static TerminalColor Basic(byte index) => new(TerminalColorKind.Indexed, 0, 0, 0, index);

    /// <summary>使用 256 色索引。</summary>
    /// <param name="index">调色板索引。</param>
    public static TerminalColor Indexed(byte index) => new(TerminalColorKind.Indexed, 0, 0, 0, index);

    /// <summary>使用真彩色。</summary>
    /// <param name="r">红色分量。</param>
    /// <param name="g">绿色分量。</param>
    /// <param name="b">蓝色分量。</param>
    public static TerminalColor Rgb(byte r, byte g, byte b) => new(TerminalColorKind.Rgb, r, g, b, 0);

    /// <summary>从 <c>#RRGGBB</c> 十六进制字符串创建真彩色；无效输入返回 <see cref="Default"/>。</summary>
    /// <param name="hex">十六进制颜色，可带前导 <c>#</c>。</param>
    public static TerminalColor Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Default;
        }

        var text = hex!.Trim().TrimStart('#');
        if (text.Length != 6 ||
            !byte.TryParse(text.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return Default;
        }

        return Rgb(r, g, b);
    }

    /// <summary>追加前景色 SGR 参数；默认色时返回 false。</summary>
    /// <param name="target">接收参数的列表。</param>
    public bool TryAppendForeground(List<string> target)
    {
        switch (Kind)
        {
            case TerminalColorKind.Default:
                return false;
            case TerminalColorKind.Rgb:
                target.Add("38;2;" + R.ToString(CultureInfo.InvariantCulture) + ";" +
                           G.ToString(CultureInfo.InvariantCulture) + ";" + B.ToString(CultureInfo.InvariantCulture));
                return true;
            default:
                target.Add(Index < 8
                    ? (30 + Index).ToString(CultureInfo.InvariantCulture)
                    : Index < 16
                        ? (90 + Index - 8).ToString(CultureInfo.InvariantCulture)
                        : "38;5;" + Index.ToString(CultureInfo.InvariantCulture));
                return true;
        }
    }

    /// <summary>追加背景色 SGR 参数；默认色时返回 false。</summary>
    /// <param name="target">接收参数的列表。</param>
    public bool TryAppendBackground(List<string> target)
    {
        switch (Kind)
        {
            case TerminalColorKind.Default:
                return false;
            case TerminalColorKind.Rgb:
                target.Add("48;2;" + R.ToString(CultureInfo.InvariantCulture) + ";" +
                           G.ToString(CultureInfo.InvariantCulture) + ";" + B.ToString(CultureInfo.InvariantCulture));
                return true;
            default:
                target.Add(Index < 8
                    ? (40 + Index).ToString(CultureInfo.InvariantCulture)
                    : Index < 16
                        ? (100 + Index - 8).ToString(CultureInfo.InvariantCulture)
                        : "48;5;" + Index.ToString(CultureInfo.InvariantCulture));
                return true;
        }
    }

    /// <inheritdoc />
    public bool Equals(TerminalColor other)
        => Kind == other.Kind && R == other.R && G == other.G && B == other.B && Index == other.Index;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TerminalColor other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine((int)Kind, R, G, B, Index);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        TerminalColorKind.Rgb => "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2"),
        TerminalColorKind.Indexed => "idx:" + Index,
        _ => "default"
    };

    /// <summary>比较两个颜色是否相等。</summary>
    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);

    /// <summary>比较两个颜色是否不相等。</summary>
    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);
}

/// <summary>颜色类型。</summary>
public enum TerminalColorKind
{
    /// <summary>终端默认色。</summary>
    Default,

    /// <summary>256 色索引（前 16 个映射到标准 SGR 30-37/90-97）。</summary>
    Indexed,

    /// <summary>24 位真彩色。</summary>
    Rgb
}

/// <summary>
/// 带样式的文本片段。渲染器把一行拆成若干片段，逐格写入虚拟屏幕；
/// <see cref="Style"/> 相同的相邻片段在输出时会被合并。
/// </summary>
public readonly struct StyledText : IEquatable<StyledText>
{
    /// <summary>创建片段。</summary>
    /// <param name="text">文本；不应包含换行。</param>
    /// <param name="style">样式。</param>
    public StyledText(string text, TextStyle style)
    {
        Text = text ?? string.Empty;
        Style = style;
    }

    /// <summary>获取文本。</summary>
    public string Text { get; }

    /// <summary>获取样式。</summary>
    public TextStyle Style { get; }

    /// <inheritdoc />
    public bool Equals(StyledText other) => string.Equals(Text, other.Text, StringComparison.Ordinal) && Style.Equals(other.Style);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StyledText other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Text, Style);

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>单元格样式：前景色、背景色、粗体、斜体、下划线与反色。</summary>
public readonly struct TextStyle : IEquatable<TextStyle>
{
    /// <summary>创建样式。</summary>
    public TextStyle(
        TerminalColor foreground,
        TerminalColor background,
        bool bold = false,
        bool italic = false,
        bool underline = false,
        bool reverse = false)
    {
        Foreground = foreground;
        Background = background;
        Bold = bold;
        Italic = italic;
        Underline = underline;
        Reverse = reverse;
    }

    /// <summary>获取前景色。</summary>
    public TerminalColor Foreground { get; }

    /// <summary>获取背景色。</summary>
    public TerminalColor Background { get; }

    /// <summary>获取是否粗体。</summary>
    public bool Bold { get; }

    /// <summary>获取是否斜体。</summary>
    public bool Italic { get; }

    /// <summary>获取是否下划线。</summary>
    public bool Underline { get; }

    /// <summary>获取是否反色。</summary>
    public bool Reverse { get; }

    /// <summary>默认样式。</summary>
    public static TextStyle Default { get; } = new(TerminalColor.Default, TerminalColor.Default);

    /// <summary>返回仅替换前景色的样式。</summary>
    /// <param name="color">新的前景色。</param>
    public TextStyle WithForeground(TerminalColor color) => new(color, Background, Bold, Italic, Underline, Reverse);

    /// <summary>返回仅替换背景色的样式。</summary>
    /// <param name="color">新的背景色。</param>
    public TextStyle WithBackground(TerminalColor color) => new(Foreground, color, Bold, Italic, Underline, Reverse);

    /// <summary>返回加粗样式。</summary>
    /// <param name="bold">是否加粗。</param>
    public TextStyle WithBold(bool bold = true) => new(Foreground, Background, bold, Italic, Underline, Reverse);

    /// <summary>返回斜体样式。</summary>
    /// <param name="italic">是否斜体。</param>
    public TextStyle WithItalic(bool italic = true) => new(Foreground, Background, Bold, italic, Underline, Reverse);

    /// <summary>返回下划线样式。</summary>
    /// <param name="underline">是否下划线。</param>
    public TextStyle WithUnderline(bool underline = true) => new(Foreground, Background, Bold, Italic, underline, Reverse);

    /// <summary>返回反色样式。</summary>
    /// <param name="reverse">是否反色。</param>
    public TextStyle WithReverse(bool reverse = true) => new(Foreground, Background, Bold, Italic, Underline, reverse);

    /// <summary>返回不带任何修饰、只保留颜色的样式。</summary>
    public TextStyle Plain() => new(Foreground, Background);

    /// <summary>把样式转换成 SGR 转义序列；默认样式返回空字符串。</summary>
    public string ToSgr()
    {
        var parts = new List<string>(6);
        if (Bold)
        {
            parts.Add("1");
        }

        if (Italic)
        {
            parts.Add("3");
        }

        if (Underline)
        {
            parts.Add("4");
        }

        if (Reverse)
        {
            parts.Add("7");
        }

        try
        {
            Foreground.TryAppendForeground(parts);
            Background.TryAppendBackground(parts);
        }
        catch (Exception)
        {
            // 调色板参数构造失败时退化为默认样式，避免渲染循环抛出。
            return string.Empty;
        }

        return parts.Count == 0 ? string.Empty : "\u001b[" + string.Join(";", parts) + "m";
    }

    /// <inheritdoc />
    public bool Equals(TextStyle other)
        => Foreground.Equals(other.Foreground)
           && Background.Equals(other.Background)
           && Bold == other.Bold
           && Italic == other.Italic
           && Underline == other.Underline
           && Reverse == other.Reverse;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TextStyle other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Foreground, Background, Bold, Italic, Underline, Reverse);

    /// <summary>比较两个样式是否相等。</summary>
    public static bool operator ==(TextStyle left, TextStyle right) => left.Equals(right);

    /// <summary>比较两个样式是否不相等。</summary>
    public static bool operator !=(TextStyle left, TextStyle right) => !left.Equals(right);
}
