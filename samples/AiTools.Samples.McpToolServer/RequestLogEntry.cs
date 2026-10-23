using System;
using System.Windows.Media;

namespace AiTools.Samples.McpToolServer;

/// <summary>用于为某一行请求日志着色的分类。</summary>
public enum RequestStatusKind
{
    /// <summary>信息性消息，例如已接受的通知。</summary>
    Info,

    /// <summary>请求已成功完成。</summary>
    Success,

    /// <summary>工具已运行，但报告了工具级失败。</summary>
    ToolError,

    /// <summary>客户端发送了无效或未授权的请求。</summary>
    ClientError,

    /// <summary>服务器在处理请求时失败。</summary>
    ServerError
}

/// <summary>WPF 窗口显示的内存请求日志中的一条记录。</summary>
public sealed class RequestLogEntry
{
    /// <summary>创建一条请求日志记录。</summary>
    /// <param name="timestamp">请求完成时的本地时间。</param>
    /// <param name="method">JSON-RPC 方法或 HTTP 操作标签。</param>
    /// <param name="toolName"><c>tools/call</c> 请求的工具名称，其他请求为 null。</param>
    /// <param name="duration">处理请求所花费的时间。</param>
    /// <param name="outcome">简短结果文本，例如 <c>200</c> 或 <c>404 会话无效</c>。</param>
    public RequestLogEntry(DateTime timestamp, string method, string? toolName, TimeSpan duration, string outcome)
    {
        Timestamp = timestamp;
        Method = method;
        ToolName = toolName;
        Duration = duration;
        Outcome = outcome;
        Kind = Classify(outcome);
    }

    /// <summary>获取请求完成时的本地时间。</summary>
    public DateTime Timestamp { get; }

    /// <summary>获取日志列表中显示的格式化时间。</summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>获取 JSON-RPC 方法或 HTTP 操作标签。</summary>
    public string Method { get; }

    /// <summary>获取所调用的工具名称（适用时）。</summary>
    public string? ToolName { get; }

    /// <summary>获取格式化后的工具名称；不涉及工具时使用长破折号。</summary>
    public string ToolText => string.IsNullOrEmpty(ToolName) ? "—" : ToolName!;

    /// <summary>获取处理请求所花费的时间。</summary>
    public TimeSpan Duration { get; }

    /// <summary>获取日志列表中显示的格式化耗时。</summary>
    public string DurationText =>
        Duration.TotalMilliseconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " ms";

    /// <summary>获取简短的结果文本。</summary>
    public string Outcome { get; }

    /// <summary>获取用于为该行着色的分类。</summary>
    public RequestStatusKind Kind { get; }

    /// <summary>获取与 <see cref="Kind"/> 匹配的画刷。</summary>
    public Brush StatusBrush => Kind switch
    {
        RequestStatusKind.Success => UiTheme.Success,
        RequestStatusKind.ToolError => UiTheme.Warning,
        RequestStatusKind.ClientError => UiTheme.Warning,
        RequestStatusKind.ServerError => UiTheme.Danger,
        _ => UiTheme.TextSecondary
    };

    private static RequestStatusKind Classify(string outcome)
    {
        if (string.IsNullOrEmpty(outcome))
        {
            return RequestStatusKind.Info;
        }

        if (outcome.StartsWith("2", StringComparison.Ordinal))
        {
            return outcome.Contains("工具错误", StringComparison.Ordinal)
                ? RequestStatusKind.ToolError
                : RequestStatusKind.Success;
        }

        if (outcome.StartsWith("4", StringComparison.Ordinal))
        {
            return RequestStatusKind.ClientError;
        }

        if (outcome.StartsWith("5", StringComparison.Ordinal))
        {
            return RequestStatusKind.ServerError;
        }

        return RequestStatusKind.Info;
    }
}

/// <summary>由 ViewModel 与日志记录共享的冻结画刷。</summary>
internal static class UiTheme
{
    internal static readonly Brush Accent = Create("#FF6EA8FE");
    internal static readonly Brush Success = Create("#FF46A758");
    internal static readonly Brush Warning = Create("#FFF5A524");
    internal static readonly Brush Danger = Create("#FFE5484D");
    internal static readonly Brush TextSecondary = Create("#FF9CA3AF");
    internal static readonly Brush SuccessSubtle = Create("#2646A758");
    internal static readonly Brush WarningSubtle = Create("#26F5A524");
    internal static readonly Brush DangerSubtle = Create("#26E5484D");

    private static Brush Create(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
