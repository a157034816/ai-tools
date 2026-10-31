using System;
using System.Collections.Generic;
using System.Text;
using AiTools.Terminal.Agent;

namespace AiTools.Terminal.Views;

/// <summary>记录区中的一条条目种类。</summary>
public enum TranscriptEntryKind
{
    /// <summary>用户输入。</summary>
    User,

    /// <summary>助手回复。</summary>
    Assistant,

    /// <summary>工具调用与结果。</summary>
    Tool,

    /// <summary>系统通知。</summary>
    Notice,

    /// <summary>错误。</summary>
    Error,

    /// <summary>直接显示的命令输出。</summary>
    Display,

    /// <summary>分隔线（例如新会话开始）。</summary>
    Divider
}

/// <summary>
/// 记录区的一条条目。条目本身只保存原始文本，排版在渲染时按当前宽度进行，
/// 因此窗口缩放后不需要重建内容。
/// </summary>
public sealed class TranscriptEntry
{
    /// <summary>创建条目。</summary>
    /// <param name="kind">种类。</param>
    /// <param name="text">正文。</param>
    public TranscriptEntry(TranscriptEntryKind kind, string text)
    {
        Kind = kind;
        Text = text ?? string.Empty;
        Id = ++_counter;
    }

    private static int _counter;

    /// <summary>获取条目的稳定标识。</summary>
    public int Id { get; }

    /// <summary>获取条目种类。</summary>
    public TranscriptEntryKind Kind { get; }

    /// <summary>获取正文。</summary>
    public string Text { get; set; }

    /// <summary>获取工具名（仅 <see cref="TranscriptEntryKind.Tool"/>）。</summary>
    public string? ToolName { get; init; }

    /// <summary>获取工具执行耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>获取工具是否失败。</summary>
    public bool IsError { get; init; }

    /// <summary>获取助手条目的推理文本。</summary>
    public string? Reasoning { get; set; }

    /// <summary>获取是否正在流式生成。</summary>
    public bool IsStreaming { get; set; }

    /// <summary>获取用量文本。</summary>
    public string? UsageText { get; set; }

    /// <summary>获取条目修订号；内容变化时递增，供渲染缓存失效。</summary>
    public int Revision { get; private set; }

    /// <summary>标记条目内容已变化（渲染缓存据此重新排版）。</summary>
    public void Touch() => Revision++;

    /// <inheritdoc />
    public override string ToString() => Kind + ": " + Text;
}

/// <summary>
/// 记录区模型：维护条目列表、滚动位置与"是否跟随尾部"。
/// 渲染器按可用宽度把条目展开成行，视口只取其中一段。
/// </summary>
public sealed class Transcript
{
    private readonly List<TranscriptEntry> _entries = new();
    private readonly object _gate = new();
    private int _scrollOffset;
    private bool _followTail = true;
    private int _lastRenderedLineCount;

    /// <summary>获取条目快照。</summary>
    public IReadOnlyList<TranscriptEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>获取条目数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>获取是否自动跟随尾部。</summary>
    public bool FollowTail => _followTail;

    /// <summary>获取当前滚动偏移（0 表示最新）。</summary>
    public int ScrollOffset => _scrollOffset;

    /// <summary>添加条目并（默认）滚动到尾部。</summary>
    /// <param name="entry">条目。</param>
    public void Add(TranscriptEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_followTail)
            {
                _scrollOffset = 0;
            }
        }
    }

    /// <summary>移除最后一条匹配的条目。</summary>
    /// <param name="entry">要移除的条目。</param>
    public void Remove(TranscriptEntry entry)
    {
        lock (_gate)
        {
            _entries.Remove(entry);
        }
    }

    /// <summary>整体替换条目（用于按索引插入或重排）。</summary>
    /// <param name="entries">新条目列表。</param>
    public void ReplaceAll(IEnumerable<TranscriptEntry> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(entries);
            if (_followTail)
            {
                _scrollOffset = 0;
            }
        }
    }

    /// <summary>清空全部条目。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _scrollOffset = 0;
            _followTail = true;
        }
    }

    /// <summary>向上滚动若干行。</summary>
    /// <param name="lines">行数。</param>
    public void ScrollUp(int lines)
    {
        lock (_gate)
        {
            var max = Math.Max(0, _lastRenderedLineCount - 1);
            _scrollOffset = Math.Min(max, _scrollOffset + Math.Max(1, lines));
            _followTail = _scrollOffset == 0;
        }
    }

    /// <summary>向下滚动若干行。</summary>
    /// <param name="lines">行数。</param>
    public void ScrollDown(int lines)
    {
        lock (_gate)
        {
            _scrollOffset = Math.Max(0, _scrollOffset - Math.Max(1, lines));
            _followTail = _scrollOffset == 0;
        }
    }

    /// <summary>滚动到最顶部。</summary>
    public void ScrollToTop()
    {
        lock (_gate)
        {
            _scrollOffset = Math.Max(0, _lastRenderedLineCount - 1);
            _followTail = false;
        }
    }

    /// <summary>滚动到最底部并恢复跟随。</summary>
    public void ScrollToBottom()
    {
        lock (_gate)
        {
            _scrollOffset = 0;
            _followTail = true;
        }
    }

    /// <summary>记录一次渲染产生的总行数，用于限制滚动上限。</summary>
    /// <param name="lineCount">总行数。</param>
    public void ReportLineCount(int lineCount)
    {
        lock (_gate)
        {
            _lastRenderedLineCount = Math.Max(0, lineCount);
            if (_scrollOffset > Math.Max(0, lineCount - 1))
            {
                _scrollOffset = Math.Max(0, lineCount - 1);
            }
        }
    }

    /// <summary>把条目的文本转成可复制到剪贴板的纯文本。</summary>
    /// <param name="includeTools">是否包含工具条目。</param>
    public string ToPlainText(bool includeTools = true)
    {
        var builder = new StringBuilder();
        foreach (var entry in Entries)
        {
            if (!includeTools && entry.Kind == TranscriptEntryKind.Tool)
            {
                continue;
            }

            switch (entry.Kind)
            {
                case TranscriptEntryKind.User:
                    builder.Append("> ").AppendLine(entry.Text);
                    break;
                case TranscriptEntryKind.Assistant:
                    builder.AppendLine(entry.Text);
                    break;
                case TranscriptEntryKind.Tool:
                    builder.Append("[").Append(entry.ToolName).Append("] ").AppendLine(entry.Text);
                    break;
                case TranscriptEntryKind.Divider:
                    builder.AppendLine("---");
                    break;
                default:
                    builder.AppendLine(entry.Text);
                    break;
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
