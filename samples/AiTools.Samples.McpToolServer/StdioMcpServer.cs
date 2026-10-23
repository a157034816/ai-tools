using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Samples.McpToolServer;

/// <summary>
/// MCP stdio 传输：从标准输入逐行读取 JSON-RPC 消息，并向标准输出逐行写入紧凑的 JSON-RPC
/// 响应。标准输出绝不接收日志或横幅信息。
/// </summary>
internal sealed class StdioMcpServer
{
    private readonly McpProtocol _protocol;
    private readonly ConsoleLog _log;
    private readonly Stream _input;
    private readonly Stream _output;

    /// <summary>创建 stdio 服务器。</summary>
    /// <param name="protocol">协议路由器。</param>
    /// <param name="log">写入标准错误的日志记录器。</param>
    /// <param name="input">标准输入流。</param>
    /// <param name="output">标准输出流。</param>
    internal StdioMcpServer(McpProtocol protocol, ConsoleLog log, Stream input, Stream output)
    {
        _protocol = protocol;
        _log = log;
        _input = input;
        _output = output;
    }

    /// <summary>运行按行分隔的消息循环，直到输入结束或取消。</summary>
    /// <param name="ct">CancellationToken。</param>
    internal async Task RunAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(
            _input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        // UTF-8 without BOM，按行写入，绝不缩进输出。
        using var writer = new StreamWriter(
            _output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _log.Error("读取标准输入失败：" + ex.Message);
                break;
            }

            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            JsonDocument? document = null;
            try
            {
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    WriteLine(writer, McpJson.Error(null, McpJson.ParseError, "无法解析 JSON-RPC 消息。"));
                    _log.Request("JSON", null, stopwatch.Elapsed, "解析失败");
                    continue;
                }

                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object)
                {
                    WriteLine(writer, McpJson.Error(null, McpJson.InvalidRequest, "JSON-RPC 消息必须是 JSON 对象。"));
                    _log.Request("JSON", null, stopwatch.Elapsed, "无效请求");
                    continue;
                }

                var method = message.TryGetProperty("method", out var methodElement)
                             && methodElement.ValueKind == JsonValueKind.String
                    ? methodElement.GetString()
                    : null;

                var reply = _protocol.Handle(message);
                if (!reply.IsNotification && reply.Json != null)
                {
                    WriteLine(writer, reply.Json);
                }

                if (!(reply.IsNotification
                      && string.Equals(method, "notifications/initialized", StringComparison.Ordinal)))
                {
                    var outcome = reply.IsNotification ? "通知" : reply.IsError ? "工具错误" : "完成";
                    _log.Request(reply.Method ?? method ?? "unknown", reply.ToolName, stopwatch.Elapsed, outcome);
                }
            }
            catch (Exception ex)
            {
                WriteLine(writer, McpJson.Error(null, -32603, "服务器内部错误：" + ex.Message));
                _log.Error("处理消息失败：" + ex.Message);
            }
            finally
            {
                document?.Dispose();
            }
        }

        writer.Flush();
    }

    private void WriteLine(StreamWriter writer, string json)
    {
        try
        {
            writer.WriteLine(json);
        }
        catch (IOException ex)
        {
            _log.Error("写入标准输出失败：" + ex.Message);
        }
        catch (ObjectDisposedException)
        {
            // 标准输出在关闭过程中已关闭。
        }
    }
}
