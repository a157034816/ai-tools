using System;
using System.Collections.Generic;
using System.Text;
using AiTools.Terminal.Rendering;

namespace AiTools.Terminal;

/// <summary>Markdown 渲染后的行：一组带样式的片段。</summary>
public sealed class MarkdownLine
{
    /// <summary>创建行。</summary>
    /// <param name="segments">片段。</param>
    public MarkdownLine(IReadOnlyList<StyledText> segments)
    {
        Segments = segments ?? Array.Empty<StyledText>();
    }

    /// <summary>获取片段。</summary>
    public IReadOnlyList<StyledText> Segments { get; }

    /// <summary>创建空行。</summary>
    public static MarkdownLine Empty { get; } = new(Array.Empty<StyledText>());

    /// <summary>获取行的纯文本。</summary>
    public string Text
    {
        get
        {
            var builder = new StringBuilder();
            foreach (var segment in Segments)
            {
                builder.Append(segment.Text);
            }

            return builder.ToString();
        }
    }
}

/// <summary>
/// 面向终端的 Markdown 渲染器。覆盖模型回复里真正常见的子集：
/// 段落、标题、列表、引用、围栏代码、行内代码、粗体/斜体/删除线、链接与分隔线。
/// 不追求完整的 CommonMark，目标是稳定、可读、按显示宽度换行。
/// </summary>
public sealed class MarkdownRenderer
{
    private readonly TerminalTheme _theme;

    /// <summary>创建渲染器。</summary>
    /// <param name="theme">主题。</param>
    public MarkdownRenderer(TerminalTheme theme)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
    }

    /// <summary>把 Markdown 文本渲染为等宽行（自动换行到 <paramref name="width"/>）。</summary>
    /// <param name="markdown">Markdown 文本。</param>
    /// <param name="width">可用列数。</param>
    /// <param name="baseStyle">正文样式；null 使用主题正文样式。</param>
    public IReadOnlyList<MarkdownLine> Render(string? markdown, int width, TextStyle? baseStyle = null)
    {
        var lines = new List<MarkdownLine>();
        if (string.IsNullOrEmpty(markdown))
        {
            return lines;
        }

        width = Math.Max(8, width);
        var style = baseStyle ?? _theme.BaseStyle;
        var source = markdown!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inCode = false;
        var codeLanguage = string.Empty;
        var codeLines = new List<string>();
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            var text = paragraph.ToString().Trim();
            paragraph.Clear();
            if (text.Length > 0)
            {
                AppendWrapped(lines, ParseInline(text, style), width);
            }
        }

        for (var index = 0; index < source.Length; index++)
        {
            var line = source[index];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCode)
                {
                    FlushParagraph();
                    inCode = true;
                    codeLanguage = trimmed.Substring(3).Trim();
                    codeLines.Clear();
                }
                else
                {
                    AppendCodeBlock(lines, codeLines, codeLanguage, width);
                    inCode = false;
                    codeLanguage = string.Empty;
                    codeLines.Clear();
                }

                continue;
            }

            if (inCode)
            {
                codeLines.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                if (lines.Count > 0 && lines[lines.Count - 1].Segments.Count > 0)
                {
                    lines.Add(MarkdownLine.Empty);
                }

                continue;
            }

            // 标题
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#' && level < 6)
                {
                    level++;
                }

                if (level > 0 && (level == trimmed.Length || trimmed[level] == ' '))
                {
                    FlushParagraph();
                    var title = trimmed.Substring(level).Trim();
                    var titleStyle = level <= 2
                        ? _theme.BoldStyle.WithForeground(level == 1 ? _theme.Accent : _theme.Secondary)
                        : _theme.BoldStyle;
                    lines.Add(new MarkdownLine(new[] { new StyledText(title, titleStyle) }));
                    continue;
                }
            }

            // 表格行：整行原样保留，不参与段落合并（模型常用管道表格）。
            if (trimmed.StartsWith("|", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (IsTableSeparator(trimmed))
                {
                    var separator = new string(_theme.Unicode ? '─' : '-', Math.Min(width, Math.Max(4, trimmed.Length)));
                    lines.Add(new MarkdownLine(new[] { new StyledText(separator, _theme.FaintStyle) }));
                    continue;
                }

                var tableLine = TextWidth.Truncate(trimmed, width);
                lines.Add(new MarkdownLine(new[] { new StyledText(tableLine, _theme.CodeStyle) }));
                continue;
            }

            // 分隔线
            if (IsThematicBreak(trimmed))
            {
                FlushParagraph();
                var rule = new string(_theme.Unicode ? '─' : '-', Math.Min(width, 48));
                lines.Add(new MarkdownLine(new[] { new StyledText(rule, _theme.FaintStyle) }));
                continue;
            }

            // 引用
            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                FlushParagraph();
                var content = trimmed.TrimStart('>').TrimStart();
                var bar = _theme.Unicode ? "│ " : "| ";
                var segments = new List<StyledText> { new(bar, _theme.FaintStyle) };
                segments.AddRange(ParseInline(content, _theme.MutedStyle));
                AppendWrapped(lines, segments, width, hangingStyle: _theme.FaintStyle);
                continue;
            }

            // 列表（含任务列表）
            var bullet = TryParseListMarker(trimmed);
            if (bullet != null)
            {
                FlushParagraph();
                var marker = bullet.Value.Marker + " ";
                var contentSegments = new List<StyledText> { new(marker, _theme.AccentStyle) };
                contentSegments.AddRange(ParseInline(bullet.Value.Content, style));
                var indent = new string(' ', TextWidth.Of(marker));
                AppendWrapped(lines, contentSegments, width, hangingIndent: indent);
                continue;
            }

            // 段落续行：同一段落内的换行保留为一个空格（中文不额外加空格）。
            if (paragraph.Length > 0)
            {
                var previous = paragraph[paragraph.Length - 1];
                if (!IsCjk(previous) && text_is_ascii(trimmed))
                {
                    paragraph.Append(' ');
                }
            }

            paragraph.Append(trimmed);
        }

        if (inCode && codeLines.Count > 0)
        {
            AppendCodeBlock(lines, codeLines, codeLanguage, width);
        }

        FlushParagraph();

        // 去掉尾部空行，避免气泡之间出现双倍空白。
        while (lines.Count > 0 && lines[lines.Count - 1].Segments.Count == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>把一段纯文本按宽度换行（不解析 Markdown）。</summary>
    /// <param name="text">文本。</param>
    /// <param name="width">宽度。</param>
    /// <param name="style">样式。</param>
    public IReadOnlyList<MarkdownLine> RenderPlain(string? text, int width, TextStyle? style = null)
    {
        var lines = new List<MarkdownLine>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        foreach (var raw in text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            AppendWrapped(lines, new[] { new StyledText(raw, style ?? _theme.BaseStyle) }, Math.Max(8, width));
        }

        return lines;
    }

    private static bool text_is_ascii(string text)
    {
        foreach (var character in text)
        {
            if (character > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCjk(char character)
        => (character >= 0x2E80 && character <= 0x9FFF)
           || (character >= 0xF900 && character <= 0xFAFF)
           || (character >= 0xFF00 && character <= 0xFFEF);

    /// <summary>判断是否为 Markdown 表格的列分隔行（| --- | :---: |）。</summary>
    private static bool IsTableSeparator(string trimmed)
    {
        var hasDash = false;
        foreach (var character in trimmed)
        {
            if (character == '-')
            {
                hasDash = true;
                continue;
            }

            if (character is '|' or ':' or ' ' or '	')
            {
                continue;
            }

            return false;
        }

        return hasDash;
    }

    private static bool IsThematicBreak(string trimmed)
    {
        if (trimmed.Length < 3)
        {
            return false;
        }

        var marker = trimmed[0];
        if (marker != '-' && marker != '*' && marker != '_')
        {
            return false;
        }

        var count = 0;
        foreach (var character in trimmed)
        {
            if (character == marker)
            {
                count++;
            }
            else if (character != ' ')
            {
                return false;
            }
        }

        return count >= 3;
    }

    private (string Marker, string Content)? TryParseListMarker(string trimmed)
    {
        if (trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
        {
            var content = trimmed.Substring(2);
            if (content.StartsWith("[ ] ", StringComparison.Ordinal))
            {
                return (_theme.Unicode ? "☐" : "[ ]", content.Substring(4));
            }

            if (content.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
            {
                return (_theme.Unicode ? "☑" : "[x]", content.Substring(4));
            }

            return (_theme.Unicode ? "•" : "-", content);
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits < trimmed.Length && (trimmed[digits] == '.' || trimmed[digits] == ')') &&
            digits + 1 < trimmed.Length && trimmed[digits + 1] == ' ')
        {
            return (trimmed.Substring(0, digits + 1), trimmed.Substring(digits + 2));
        }

        return null;
    }

    private void AppendCodeBlock(List<MarkdownLine> lines, List<string> codeLines, string language, int width)
    {
        var label = string.IsNullOrWhiteSpace(language) ? "code" : language.Trim();
        var borderWidth = Math.Max(4, Math.Min(width, 120));
        var ruleWidth = Math.Max(0, borderWidth - TextWidth.Of(label) - 1);
        var rule = new string(_theme.Unicode ? '─' : '-', ruleWidth);
        lines.Add(new MarkdownLine(new[]
        {
            new StyledText(label, _theme.FaintStyle),
            new StyledText(" " + rule, _theme.FaintStyle)
        }));

        foreach (var codeLine in codeLines)
        {
            // 代码不换行，按宽截断并保留缩进（横向滚动留待后续版本）。
            var text = TextWidth.Truncate(TextWidth.Sanitize(codeLine), borderWidth);
            var padded = TextWidth.PadRight(text, borderWidth);
            lines.Add(new MarkdownLine(new[] { new StyledText(padded, _theme.CodeStyle) }));
        }

        lines.Add(new MarkdownLine(new[] { new StyledText(rule, _theme.FaintStyle) }));
    }

    private void AppendWrapped(
        List<MarkdownLine> lines,
        IReadOnlyList<StyledText> segments,
        int width,
        string? hangingIndent = null,
        TextStyle? hangingStyle = null)
    {
        var indent = hangingIndent ?? string.Empty;
        var first = true;
        var current = new List<StyledText>();

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            lines.Add(new MarkdownLine(current.ToArray()));
            current.Clear();
        }

        void StartLine()
        {
            if (!first && indent.Length > 0)
            {
                current.Add(new StyledText(indent, hangingStyle ?? _theme.FaintStyle));
            }
        }

        StartLine();
        var used = first ? 0 : TextWidth.Of(indent);
        var available = Math.Max(4, width);

        foreach (var segment in segments)
        {
            var text = segment.Text;
            var index = 0;
            while (index < text.Length)
            {
                if (text[index] == '\n')
                {
                    Flush();
                    first = false;
                    StartLine();
                    used = indent.Length > 0 ? TextWidth.Of(indent) : 0;
                    index++;
                    continue;
                }

                // 找到下一个可断行点（空格或 CJK 边界）。
                var chunkStart = index;
                var chunkWidth = 0;
                var breakIndex = -1;
                var breakWidth = 0;
                while (index < text.Length && text[index] != '\n')
                {
                    var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
                    var cell = TextWidth.Of(char.ConvertToUtf32(text, index));
                    if (text[index] == ' ')
                    {
                        breakIndex = index;
                        breakWidth = chunkWidth;
                    }

                    if (used + chunkWidth + cell > available && chunkWidth > 0)
                    {
                        break;
                    }

                    // CJK 可以在任意两个字符之间断行。
                    if (IsCjk(text[index]) && used + chunkWidth + cell <= available && chunkWidth > 0)
                    {
                        breakIndex = index;
                        breakWidth = chunkWidth;
                    }

                    chunkWidth += cell;
                    index += length;
                }

                if (index >= text.Length || text[index] == '\n' ||
                    (used + chunkWidth <= available))
                {
                    // 整段放得下
                    if (chunkWidth > 0)
                    {
                        current.Add(new StyledText(text.Substring(chunkStart, index - chunkStart), segment.Style));
                        used += chunkWidth;
                    }

                    if (index < text.Length && text[index] == '\n')
                    {
                        Flush();
                        first = false;
                        StartLine();
                        used = indent.Length > 0 ? TextWidth.Of(indent) : 0;
                        index++;
                    }

                    continue;
                }

                if (breakIndex > chunkStart)
                {
                    current.Add(new StyledText(text.Substring(chunkStart, breakIndex - chunkStart).TrimEnd(), segment.Style));
                    Flush();
                    first = false;
                    StartLine();
                    used = indent.Length > 0 ? TextWidth.Of(indent) : 0;
                    index = breakIndex;
                    while (index < text.Length && text[index] == ' ')
                    {
                        index++;
                    }

                    continue;
                }

                // 没有断点（长单词/长链接）：硬切。
                var fit = TextWidth.CountFit(text, chunkStart, available - used, out var taken, out _);
                if (fit == 0)
                {
                    Flush();
                    first = false;
                    StartLine();
                    used = indent.Length > 0 ? TextWidth.Of(indent) : 0;
                    continue;
                }

                current.Add(new StyledText(text.Substring(chunkStart, fit), segment.Style));
                Flush();
                first = false;
                StartLine();
                used = indent.Length > 0 ? TextWidth.Of(indent) : 0;
                index = chunkStart + fit;
            }
        }

        Flush();
    }

    private IReadOnlyList<StyledText> ParseInline(string text, TextStyle baseStyle)
    {
        var segments = new List<StyledText>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0)
            {
                return;
            }

            segments.Add(new StyledText(plain.ToString(), baseStyle));
            plain.Clear();
        }

        for (var index = 0; index < text.Length;)
        {
            var character = text[index];

            // 行内代码
            if (character == '`')
            {
                var end = text.IndexOf('`', index + 1);
                if (end > index)
                {
                    FlushPlain();
                    segments.Add(new StyledText(text.Substring(index + 1, end - index - 1).Trim(), _theme.InlineCodeStyle));
                    index = end + 1;
                    continue;
                }
            }

            // 粗体与斜体
            if (character == '*' && index + 1 < text.Length)
            {
                var marker = text[index + 1] == '*' ? "**" : "*";
                var end = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
                if (end > index)
                {
                    FlushPlain();
                    var inner = text.Substring(index + marker.Length, end - index - marker.Length);
                    var style = marker == "**" ? baseStyle.WithBold() : baseStyle.WithItalic();
                    if (marker == "**" && IsBoldAccent(inner))
                    {
                        style = style.WithForeground(_theme.Accent);
                    }

                    segments.AddRange(ParseInline(inner, style));
                    index = end + marker.Length;
                    continue;
                }
            }

            if (character == '_' && index + 1 < text.Length)
            {
                var marker = text[index + 1] == '_' ? "__" : "_";
                var end = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
                if (end > index + 1)
                {
                    FlushPlain();
                    var inner = text.Substring(index + marker.Length, end - index - marker.Length);
                    segments.Add(new StyledText(inner, marker == "__" ? baseStyle.WithBold() : baseStyle.WithItalic()));
                    index = end + marker.Length;
                    continue;
                }
            }

            // 删除线
            if (character == '~' && index + 1 < text.Length && text[index + 1] == '~')
            {
                var end = text.IndexOf("~~", index + 2, StringComparison.Ordinal);
                if (end > index)
                {
                    FlushPlain();
                    var inner = text.Substring(index + 2, end - index - 2);
                    segments.Add(new StyledText(inner, baseStyle.WithUnderline()));
                    index = end + 2;
                    continue;
                }
            }

            // 链接
            if (character == '[')
            {
                var close = text.IndexOf(']', index + 1);
                if (close > index && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var urlEnd = text.IndexOf(')', close + 2);
                    if (urlEnd > close)
                    {
                        FlushPlain();
                        var label = text.Substring(index + 1, close - index - 1);
                        var url = text.Substring(close + 2, urlEnd - close - 2);
                        segments.Add(new StyledText(label.Length == 0 ? url : label, _theme.LinkStyle));
                        if (!string.IsNullOrWhiteSpace(url) && !string.Equals(label, url, StringComparison.Ordinal))
                        {
                            segments.Add(new StyledText(" (" + TextWidth.Truncate(url, 60) + ")", _theme.FaintStyle));
                        }

                        index = urlEnd + 1;
                        continue;
                    }
                }
            }

            plain.Append(character);
            index++;
        }

        FlushPlain();
        return segments;
    }

    private static bool IsBoldAccent(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0 && trimmed.Length <= 24 &&
               (trimmed.IndexOf('。') >= 0 || trimmed.IndexOf(':') >= 0 || trimmed.EndsWith("：", StringComparison.Ordinal));
    }
}
