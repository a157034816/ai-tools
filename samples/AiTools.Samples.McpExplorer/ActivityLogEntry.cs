using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using AiTools.Samples.McpExplorer.ViewModels;

namespace AiTools.Samples.McpExplorer;

/// <summary>活动日志条目的严重级别。</summary>
public enum ActivityKind
{
    /// <summary>信息性消息。</summary>
    Info,

    /// <summary>操作成功。</summary>
    Success,

    /// <summary>操作失败或引发了警告。</summary>
    Error
}

/// <summary>活动日志中的一条记录。</summary>
public sealed class ActivityLogEntry
{
    /// <summary>创建日志条目。</summary>
    /// <param name="kind">严重级别。</param>
    /// <param name="operation">简短的操作名称。</param>
    /// <param name="detail">结果详情。</param>
    public ActivityLogEntry(ActivityKind kind, string operation, string detail)
    {
        Kind = kind;
        Operation = operation;
        Detail = detail;
        Timestamp = DateTime.Now;
    }

    /// <summary>获取本地时间戳。</summary>
    public DateTime Timestamp { get; }

    /// <summary>获取时间戳显示文本。</summary>
    public string TimestampText => Timestamp.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>获取严重级别。</summary>
    public ActivityKind Kind { get; }

    /// <summary>获取简短的操作名称。</summary>
    public string Operation { get; }

    /// <summary>获取结果详情。</summary>
    public string Detail { get; }

    /// <summary>获取状态符号。</summary>
    public string StatusGlyph => Kind switch
    {
        ActivityKind.Success => "✓",
        ActivityKind.Error => "✗",
        _ => "•"
    };

    /// <summary>获取用于状态符号的画刷。</summary>
    public Brush StatusBrush => Kind switch
    {
        ActivityKind.Success => UiBrushes.Success,
        ActivityKind.Error => UiBrushes.Danger,
        _ => UiBrushes.Accent
    };
}

/// <summary>调用结果中一个已渲染的 MCP 内容块。</summary>
public sealed class ResultBlockViewModel
{
    /// <summary>创建显示块。</summary>
    /// <param name="typeLabel">本地化的内容类型。</param>
    /// <param name="summary">单行摘要。</param>
    /// <param name="text">可选的正文文本。</param>
    /// <param name="accent">用于类型标签的画刷。</param>
    public ResultBlockViewModel(string typeLabel, string summary, string text, Brush accent)
    {
        TypeLabel = typeLabel;
        Summary = summary;
        Text = text;
        Accent = accent;
    }

    /// <summary>获取本地化的内容类型。</summary>
    public string TypeLabel { get; }

    /// <summary>获取单行摘要。</summary>
    public string Summary { get; }

    /// <summary>获取可选的正文文本。</summary>
    public string Text { get; }

    /// <summary>获取是否存在正文文本。</summary>
    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>获取用于类型标签的画刷。</summary>
    public Brush Accent { get; }
}
