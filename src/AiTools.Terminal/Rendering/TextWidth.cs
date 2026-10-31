using System;
using System.Collections.Generic;

namespace AiTools.Terminal.Rendering;

/// <summary>
/// 终端字符宽度计算（东亚宽度）。渲染器按"显示列"而不是 UTF-16 码元排布，
/// 否则中文、Emoji 与组合字符会让整行错位。
/// </summary>
public static class TextWidth
{
    /// <summary>获取单个码点的显示宽度：0（组合符）、1（半角）或 2（全角）。</summary>
    /// <param name="codePoint">Unicode 码点。</param>
    public static int Of(int codePoint)
    {
        if (codePoint <= 0)
        {
            return 0;
        }

        if (codePoint < 32 || (codePoint >= 0x7F && codePoint < 0xA0))
        {
            // 控制字符不占位；调用方负责把它们替换成可见符号。
            return 0;
        }

        if (IsZeroWidth(codePoint))
        {
            return 0;
        }

        return IsWide(codePoint) ? 2 : 1;
    }

    /// <summary>获取一个字符串的显示宽度（忽略换行，换行按 0 计算）。</summary>
    /// <param name="text">文本。</param>
    public static int Of(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        for (var index = 0; index < text!.Length;)
        {
            var codePoint = char.ConvertToUtf32(text, index);
            width += Of(codePoint);
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
        }

        return width;
    }

    /// <summary>
    /// 测量字符串从 <paramref name="start"/> 到末尾中，在给定列宽内最多能显示的字符数。
    /// 返回时可选择不拆分宽字符：放不下的宽字符会让 <paramref name="width"/> 保持未占用。
    /// </summary>
    /// <param name="text">文本。</param>
    /// <param name="start">起始字符索引。</param>
    /// <param name="maxWidth">允许的最大显示列数。</param>
    /// <param name="width">实际占用的列数。</param>
    /// <param name="truncated">是否还有未显示的字符。</param>
    public static int CountFit(string text, int start, int maxWidth, out int width, out bool truncated)
    {
        width = 0;
        truncated = false;
        var index = start;
        while (index < text.Length)
        {
            var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
            var codePoint = char.ConvertToUtf32(text, index);
            var cell = Of(codePoint);
            if (width + cell > maxWidth)
            {
                truncated = true;
                break;
            }

            width += cell;
            index += length;
        }

        return index - start;
    }

    /// <summary>把字符串按显示宽度截断，超出部分以 <paramref name="ellipsis"/> 结尾。</summary>
    /// <param name="text">文本。</param>
    /// <param name="maxWidth">最大显示列数。</param>
    /// <param name="ellipsis">省略号；默认 <c>…</c>。</param>
    public static string Truncate(string? text, int maxWidth, string ellipsis = "…")
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return string.Empty;
        }

        if (Of(text) <= maxWidth)
        {
            return text!;
        }

        var ellipsisWidth = Of(ellipsis);
        if (ellipsisWidth >= maxWidth)
        {
            return ellipsis.Length > 0 ? ellipsis.Substring(0, Math.Min(ellipsis.Length, maxWidth)) : string.Empty;
        }

        var take = CountFit(text!, 0, maxWidth - ellipsisWidth, out _, out _);
        return text!.Substring(0, take) + ellipsis;
    }

    /// <summary>把字符串右侧补空格到指定显示宽度；已经更宽时原样返回。</summary>
    /// <param name="text">文本。</param>
    /// <param name="width">目标显示宽度。</param>
    public static string PadRight(string text, int width)
    {
        var current = Of(text);
        return current >= width ? text : text + new string(' ', width - current);
    }

    /// <summary>
    /// 把字符串左侧补空格到指定显示宽度。
    /// </summary>
    /// <param name="text">文本。</param>
    /// <param name="width">目标显示宽度。</param>
    public static string PadLeft(string text, int width)
    {
        var current = Of(text);
        return current >= width ? text : new string(' ', width - current) + text;
    }

    private static bool IsZeroWidth(int codePoint)
    {
        // 组合附加符号、变体选择符、零宽连接符等不占显示列。
        return (codePoint >= 0x0300 && codePoint <= 0x036F)
               || (codePoint >= 0x1AB0 && codePoint <= 0x1AFF)
               || (codePoint >= 0x1DC0 && codePoint <= 0x1DFF)
               || (codePoint >= 0x20D0 && codePoint <= 0x20FF)
               || (codePoint >= 0xFE00 && codePoint <= 0xFE0F)
               || (codePoint >= 0xFE20 && codePoint <= 0xFE2F)
               || codePoint == 0x200B
               || codePoint == 0x200C
               || codePoint == 0x200D
               || codePoint == 0xFEFF
               || (codePoint >= 0xE0100 && codePoint <= 0xE01EF);
    }

    private static bool IsWide(int codePoint)
    {
        if (codePoint < 0x1100)
        {
            return false;
        }

        return (codePoint <= 0x115F)                       // Hangul Jamo
               || codePoint == 0x2329 || codePoint == 0x232A
               || (codePoint >= 0x2E80 && codePoint <= 0xA4CF && codePoint != 0x303F) // CJK 部首、汉字、假名等
               || (codePoint >= 0xAC00 && codePoint <= 0xD7A3) // Hangul 音节
               || (codePoint >= 0xF900 && codePoint <= 0xFAFF) // CJK 兼容表意文字
               || (codePoint >= 0xFE10 && codePoint <= 0xFE19)
               || (codePoint >= 0xFE30 && codePoint <= 0xFE6F)
               || (codePoint >= 0xFF00 && codePoint <= 0xFF60) // 全角形式
               || (codePoint >= 0xFFE0 && codePoint <= 0xFFE6)
               || (codePoint >= 0x1F300 && codePoint <= 0x1F64F) // 表情符号
               || (codePoint >= 0x1F900 && codePoint <= 0x1F9FF)
               || (codePoint >= 0x1FA70 && codePoint <= 0x1FAFF)
               || (codePoint >= 0x20000 && codePoint <= 0x3FFFD);
    }

    /// <summary>把不可打印字符替换成可见符号，避免破坏输出流。</summary>
    /// <param name="text">原始文本。</param>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var needsWork = false;
        foreach (var character in text)
        {
            if (char.IsControl(character) && character != '\n' && character != '\r' && character != '\t')
            {
                needsWork = true;
                break;
            }
        }

        if (!needsWork)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsControl(character) && character != '\n' && character != '\r' && character != '\t')
            {
                builder.Append('�');
            }
            else if (character == '\t')
            {
                builder.Append("    ");
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
