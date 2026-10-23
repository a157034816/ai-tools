using System;
using System.Globalization;
using System.IO;

namespace AiTools.Samples.McpToolServer;

/// <summary>
/// 线程安全且紧凑的日志记录器。无窗口模式下写入标准输出；WPF 模式下 writer 为 null，
/// 仅使用内存中的请求日志（由 <see cref="HttpMcpServer"/> 提供）。stdio 模式写入标准错误，
/// 以确保标准输出只包含 JSON-RPC 消息。
/// </summary>
internal sealed class ConsoleLog
{
    private readonly TextWriter? _writer;
    private readonly string _transport;
    private readonly object _gate = new();

    /// <summary>为指定的传输方式创建日志记录器。</summary>
    /// <param name="writer">目标写入器；为 null 时禁用控制台输出（WPF 模式）。</param>
    /// <param name="transport">显示在每行中的简短传输标签，例如 <c>HTTP</c>。</param>
    internal ConsoleLog(TextWriter? writer, string transport)
    {
        _writer = writer;
        _transport = transport;
    }

    /// <summary>写入一条信息级日志。</summary>
    /// <param name="message">要写入的消息。</param>
    internal void Info(string message) => Write("信息", message);

    /// <summary>写入一条警告级日志。</summary>
    /// <param name="message">要写入的消息。</param>
    internal void Warn(string message) => Write("警告", message);

    /// <summary>写入一条错误级日志。</summary>
    /// <param name="message">要写入的消息。</param>
    internal void Error(string message) => Write("错误", message);

    /// <summary>写入紧凑的请求日志行，包含 MCP 方法、可选的工具名称、耗时与结果。</summary>
    /// <param name="method">JSON-RPC 方法或 HTTP 动词。</param>
    /// <param name="toolName"><c>tools/call</c> 请求的工具名称；其他请求为 null。</param>
    /// <param name="elapsed">处理请求所花费的时间。</param>
    /// <param name="outcome">简短状态，例如 <c>200</c> 或 <c>202</c>。</param>
    internal void Request(string method, string? toolName, TimeSpan elapsed, string outcome)
    {
        var label = string.IsNullOrEmpty(toolName) ? method : method + " " + toolName;
        var milliseconds = elapsed.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture);
        Write("请求", label + " · " + milliseconds + " ms · " + outcome);
    }

    private void Write(string level, string message)
    {
        if (_writer == null)
        {
            return;
        }

        var line = "[" + _transport + " " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
                   "] " + level + ": " + message;

        lock (_gate)
        {
            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (IOException)
            {
                // 日志记录绝不能导致服务器崩溃。
            }
            catch (ObjectDisposedException)
            {
                // 输出流已经关闭。
            }
        }
    }
}
