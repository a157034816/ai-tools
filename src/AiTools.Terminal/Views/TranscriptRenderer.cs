using System;
using System.Collections.Generic;
using AiTools.Terminal.Rendering;

namespace AiTools.Terminal.Views;

/// <summary>渲染后的记录行：片段 + 是否带背景（用于代码块）。</summary>
public sealed class RenderedLine
{
    /// <summary>创建行。</summary>
    /// <param name="segments">片段。</param>
    /// <param name="background">整行背景色；null 表示默认。</param>
    public RenderedLine(IReadOnlyList<StyledText> segments, TerminalColor? background = null)
    {
        Segments = segments;
        Background = background;
    }

    /// <summary>获取片段。</summary>
    public IReadOnlyList<StyledText> Segments { get; }

    /// <summary>获取整行背景。</summary>
    public TerminalColor? Background { get; }
}

/// <summary>
/// 把 <see cref="Transcript"/> 展开为行，并按宽度缓存：
/// 已完成的条目只在宽度变化时重新排版，流式条目按修订号增量刷新。
/// </summary>
public sealed class TranscriptRenderer
{
    private readonly MarkdownRenderer _markdown;
    private readonly TerminalTheme _theme;
    private readonly Dictionary<int, CacheEntry> _cache = new();
    private int _cacheWidth = -1;

    /// <summary>创建渲染器。</summary>
    /// <param name="theme">主题。</param>
    public TranscriptRenderer(TerminalTheme theme)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _markdown = new MarkdownRenderer(theme);
    }

    /// <summary>把条目展开为行。</summary>
    /// <param name="entries">条目。</param>
    /// <param name="width">可用宽度。</param>
    public IReadOnlyList<RenderedLine> Render(IReadOnlyList<TranscriptEntry> entries, int width)
    {
        width = Math.Max(12, width);
        if (width != _cacheWidth)
        {
            _cache.Clear();
            _cacheWidth = width;
        }

        var lines = new List<RenderedLine>(entries.Count * 4);
        var live = new HashSet<int>();

        foreach (var entry in entries)
        {
            live.Add(entry.Id);
            if (_cache.TryGetValue(entry.Id, out var cached) &&
                cached.Revision == entry.Revision &&
                cached.Width == width)
            {
                lines.AddRange(cached.Lines);
                continue;
            }

            var rendered = RenderEntry(entry, width);
            _cache[entry.Id] = new CacheEntry(entry.Revision, width, rendered);
            lines.AddRange(rendered);
        }

        // 清除已删除条目的缓存，避免长时间会话持续占用内存。
        if (_cache.Count > live.Count)
        {
            var stale = new List<int>();
            foreach (var key in _cache.Keys)
            {
                if (!live.Contains(key))
                {
                    stale.Add(key);
                }
            }

            foreach (var key in stale)
            {
                _cache.Remove(key);
            }
        }

        return lines;
    }

    /// <summary>丢弃缓存（切换会话或主题时）。</summary>
    public void Invalidate() => _cache.Clear();

    private List<RenderedLine> RenderEntry(TranscriptEntry entry, int width)
    {
        var lines = new List<RenderedLine>();
        switch (entry.Kind)
        {
            case TranscriptEntryKind.User:
                RenderUser(entry, width, lines);
                break;

            case TranscriptEntryKind.Assistant:
                RenderAssistant(entry, width, lines);
                break;

            case TranscriptEntryKind.Tool:
                RenderTool(entry, width, lines);
                break;

            case TranscriptEntryKind.Notice:
                AddPrefixed(width, lines, _theme.Unicode ? "· " : "- ", _theme.MutedStyle, entry.Text, _theme.MutedStyle);
                break;

            case TranscriptEntryKind.Error:
                AddPrefixed(width, lines, _theme.CrossSymbol + " ", _theme.ErrorStyle, entry.Text, _theme.ErrorStyle);
                break;

            case TranscriptEntryKind.Display:
                foreach (var line in _markdown.RenderPlain(entry.Text, width, _theme.MutedStyle))
                {
                    lines.Add(new RenderedLine(line.Segments));
                }

                break;

            case TranscriptEntryKind.Divider:
                lines.Add(new RenderedLine(new[]
                {
                    new StyledText(new string(_theme.Unicode ? '─' : '-', width), _theme.FaintStyle)
                }));
                break;
        }

        return lines;
    }

    private void RenderUser(TranscriptEntry entry, int width, List<RenderedLine> lines)
    {
        var prefix = _theme.PromptSymbol + " ";
        var prefixWidth = TextWidth.Of(prefix);
        var text = entry.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var first = true;
        foreach (var rawLine in text.Split('\n'))
        {
            var wrapped = _markdown.RenderPlain(rawLine, Math.Max(8, width - prefixWidth), _theme.UserStyle);
            if (wrapped.Count == 0)
            {
                lines.Add(new RenderedLine(new[] { new StyledText(prefix, _theme.AccentStyle) }));
                first = false;
                continue;
            }

            foreach (var line in wrapped)
            {
                var segments = new List<StyledText>(line.Segments.Count + 1);
                segments.Add(new StyledText(first ? prefix : new string(' ', prefixWidth), _theme.AccentStyle));
                segments.AddRange(line.Segments);
                lines.Add(new RenderedLine(segments));
                first = false;
            }
        }

        lines.Add(new RenderedLine(Array.Empty<StyledText>()));
    }

    private void RenderAssistant(TranscriptEntry entry, int width, List<RenderedLine> lines)
    {
        var gutter = _theme.AssistantSymbol + " ";
        var gutterWidth = TextWidth.Of(gutter);

        if (!string.IsNullOrWhiteSpace(entry.Reasoning))
        {
            var reasoning = entry.Reasoning!.Trim();
            if (reasoning.Length > 0)
            {
                // 推理只保留末尾几行：它通常很长，完整铺开会淹没正文。
                var reasoningLines = _markdown.RenderPlain(reasoning, Math.Max(8, width - gutterWidth), _theme.ReasoningStyle);
                const int maxReasoningLines = 6;
                var skipped = Math.Max(0, reasoningLines.Count - maxReasoningLines);
                if (skipped > 0)
                {
                    lines.Add(new RenderedLine(new[]
                    {
                        new StyledText(new string(' ', gutterWidth), _theme.ReasoningStyle),
                        new StyledText("… 省略 " + skipped + " 行思考", _theme.FaintStyle)
                    }));
                }

                for (var index = skipped; index < reasoningLines.Count; index++)
                {
                    var segments = new List<StyledText> { new(new string(' ', gutterWidth), _theme.ReasoningStyle) };
                    segments.AddRange(reasoningLines[index].Segments);
                    lines.Add(new RenderedLine(segments));
                }

                lines.Add(new RenderedLine(Array.Empty<StyledText>()));
            }
        }

        if (entry.Text.Length == 0)
        {
            if (entry.IsStreaming)
            {
                lines.Add(new RenderedLine(new[] { new StyledText(gutter + "…", _theme.MutedStyle) }));
            }

            lines.Add(new RenderedLine(Array.Empty<StyledText>()));
            return;
        }

        var markdownLines = _markdown.Render(entry.Text, Math.Max(8, width - gutterWidth), _theme.BaseStyle);
        for (var index = 0; index < markdownLines.Count; index++)
        {
            var line = markdownLines[index];
            var segments = new List<StyledText>(line.Segments.Count + 2);
            segments.Add(new StyledText(index == 0 ? gutter : new string(' ', gutterWidth), _theme.AccentStyle.WithForeground(_theme.Secondary)));

            var isCode = line.Segments.Count > 0 && line.Segments[0].Style.Equals(_theme.CodeStyle);
            segments.AddRange(line.Segments);
            if (isCode)
            {
                var used = gutterWidth + TextWidth.Of(line.Text);
                if (used < width)
                {
                    segments.Add(new StyledText(new string(' ', width - used), _theme.CodeStyle));
                }
            }

            lines.Add(new RenderedLine(segments, isCode ? _theme.CodeBackground : (TerminalColor?)null));
        }

        if (entry.IsStreaming)
        {
            // 流式时在末尾画一个不断闪烁的方块，已有正文时不额外占行。
        }

        if (!string.IsNullOrWhiteSpace(entry.UsageText))
        {
            lines.Add(new RenderedLine(new[]
            {
                new StyledText(new string(' ', gutterWidth), _theme.FaintStyle),
                new StyledText(entry.UsageText!, _theme.FaintStyle)
            }));
        }

        lines.Add(new RenderedLine(Array.Empty<StyledText>()));
    }

    private void RenderTool(TranscriptEntry entry, int width, List<RenderedLine> lines)
    {
        var symbol = entry.IsError ? _theme.CrossSymbol : _theme.CheckSymbol;
        var style = entry.IsError ? _theme.ToolErrorStyle : _theme.ToolOkStyle;
        var gutter = "  " + symbol + " ";
        var gutterWidth = TextWidth.Of(gutter);

        var header = new List<StyledText>
        {
            new(gutter, style),
            new(entry.ToolName ?? "tool", _theme.ToolStyle.WithBold())
        };

        if (entry.Duration > TimeSpan.Zero)
        {
            header.Add(new StyledText("  " + entry.Duration.TotalSeconds.ToString("0.0") + "s", _theme.FaintStyle));
        }

        lines.Add(new RenderedLine(header));

        if (!string.IsNullOrWhiteSpace(entry.Text))
        {
            var summary = SummarizeToolResult(entry.Text!);
            foreach (var line in _markdown.RenderPlain(summary, Math.Max(8, width - gutterWidth - 2), _theme.ToolArgumentStyle))
            {
                var segments = new List<StyledText>(line.Segments.Count + 1)
                {
                    new("  " + new string(' ', gutterWidth - 2), _theme.FaintStyle)
                };
                segments.AddRange(line.Segments);
                lines.Add(new RenderedLine(segments));
            }
        }
    }

    private static string SummarizeToolResult(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return "(empty)";
        }

        // 工具结果通常是 JSON；优先展示 error 字段，其次截断原始文本。
        if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.Length > 2)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return "error: " + error.GetString();
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        var single = trimmed.Replace("\r", " ").Replace("\n", " ");
        return TextWidth.Truncate(single, 300);
    }

    private void AddPrefixed(
        int width,
        List<RenderedLine> lines,
        string prefix,
        TextStyle prefixStyle,
        string text,
        TextStyle textStyle)
    {
        var prefixWidth = TextWidth.Of(prefix);
        var wrapped = _markdown.RenderPlain(text, Math.Max(8, width - prefixWidth), textStyle);
        if (wrapped.Count == 0)
        {
            lines.Add(new RenderedLine(new[] { new StyledText(prefix.TrimEnd(), prefixStyle) }));
            return;
        }

        for (var index = 0; index < wrapped.Count; index++)
        {
            var segments = new List<StyledText>
            {
                new(index == 0 ? prefix : new string(' ', prefixWidth), prefixStyle)
            };
            segments.AddRange(wrapped[index].Segments);
            lines.Add(new RenderedLine(segments));
        }
    }

    private readonly struct CacheEntry
    {
        public CacheEntry(int revision, int width, IReadOnlyList<RenderedLine> lines)
        {
            Revision = revision;
            Width = width;
            Lines = lines;
        }

        public int Revision { get; }

        public int Width { get; }

        public IReadOnlyList<RenderedLine> Lines { get; }
    }
}
