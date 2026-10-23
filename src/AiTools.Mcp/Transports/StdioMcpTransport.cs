using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Mcp;

/// <summary>
/// 启动子进程并通过标准 I/O 交换以换行符分隔的 JSON-RPC 消息的 MCP transport。
/// 标准错误输出通过 <see cref="ErrorDataReceived"/> 暴露，且绝不会阻塞协议流。
/// </summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    private readonly StdioTransportOptions _options;
    private readonly AsyncMessageQueue<JsonElement> _incoming = new AsyncMessageQueue<JsonElement>();
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly object _gate = new object();
    private readonly TaskCompletionSource<bool> _exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private bool _disposed;

    /// <summary>使用提供的进程配置创建 stdio transport。</summary>
    /// <param name="options">进程启动选项。</param>
    public StdioMcpTransport(StdioTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>子进程标准错误流每写入一行时触发。</summary>
    public event Action<string>? ErrorDataReceived;

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && _process != null && !HasExited(_process);
            }
        }
    }

    /// <inheritdoc />
    public string? SessionId => null;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(StdioMcpTransport));
            }

            if (_process != null)
            {
                return Task.CompletedTask;
            }

            if (string.IsNullOrWhiteSpace(_options.Command))
            {
                throw new ArgumentException("A command must be configured on StdioTransportOptions.", nameof(_options));
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _options.Command,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

#if NET8_0_OR_GREATER
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
#endif

            if (!string.IsNullOrEmpty(_options.WorkingDirectory))
            {
                startInfo.WorkingDirectory = _options.WorkingDirectory!;
            }

            if (_options.Arguments != null)
            {
#if NET8_0_OR_GREATER
                foreach (var argument in _options.Arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }
#else
                startInfo.Arguments = BuildArgumentsString(_options.Arguments);
#endif
            }

            if (_options.EnvironmentVariables != null)
            {
                foreach (var variable in _options.EnvironmentVariables)
                {
                    startInfo.Environment[variable.Key] = variable.Value;
                }
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += OnProcessExited;
            try
            {
                process.Start();
            }
            catch
            {
                process.Exited -= OnProcessExited;
                process.Dispose();
                throw;
            }

            _process = process;
            _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            _stdoutPump = Task.Run(() => PumpStdoutAsync(process.StandardOutput, _incoming));
            _stderrPump = Task.Run(() => PumpStderrAsync(process.StandardError));
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(JsonElement message, CancellationToken ct = default)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("An MCP message must be a JSON object.", nameof(message));
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StreamWriter? stdin;
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(StdioMcpTransport));
                }

                stdin = _stdin;
            }

            if (stdin == null)
            {
                throw new InvalidOperationException("The stdio transport has not been started.");
            }

            await stdin.WriteLineAsync(message.GetRawText()).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("The MCP server process has closed its standard input.", ex);
        }
        finally
        {
            ReleaseSendLock();
        }
    }

    private void ReleaseSendLock()
    {
        try
        {
            _sendLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // 请求进行期间 transport 被释放。
        }
    }

    /// <inheritdoc />
    /// <remarks>同一时间只允许一个消费者枚举此序列。</remarks>
    public IAsyncEnumerable<JsonElement> ReceiveAsync(CancellationToken ct = default) => _incoming.ReadAllAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Process? process;
        StreamWriter? stdin;
        Task? stdoutPump;
        Task? stderrPump;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            process = _process;
            stdin = _stdin;
            stdoutPump = _stdoutPump;
            stderrPump = _stderrPump;
        }

        _incoming.Complete();

        if (stdin != null)
        {
            try
            {
                stdin.Dispose();
            }
            catch (IOException)
            {
                // 进程可能已终止。
            }
            catch (ObjectDisposedException)
            {
                // 已释放。
            }
        }

        if (process != null)
        {
            try
            {
                if (!HasExited(process))
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // 进程从未启动或已经退出。
            }
            catch (NotSupportedException)
            {
                // 此平台不支持终止进程；进程会随其父进程一起退出。
            }

            try
            {
                await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 忽略关闭过程中的竞态。
            }

            process.Exited -= OnProcessExited;
            process.Dispose();
        }

        var pumps = new List<Task>(2);
        if (stdoutPump != null)
        {
            pumps.Add(stdoutPump);
        }

        if (stderrPump != null)
        {
            pumps.Add(stderrPump);
        }

        if (pumps.Count > 0)
        {
            try
            {
                await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 流拆除时读取循环可能出现故障。
            }
        }

        _sendLock.Dispose();
    }

    private async Task PumpStdoutAsync(StreamReader reader, AsyncMessageQueue<JsonElement> queue)
    {
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                var trimmed = line.Trim().TrimStart('﻿');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                JsonElement message;
                try
                {
                    using (var document = JsonDocument.Parse(trimmed))
                    {
                        message = document.RootElement.Clone();
                    }
                }
                catch (JsonException)
                {
                    // 忽略非 JSON 输出；MCP 服务器只能向 stdout 写入 JSON-RPC。
                    continue;
                }

                queue.Enqueue(message);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            queue.Complete(ex);
            return;
        }
        catch (Exception ex)
        {
            queue.Complete(ex);
            return;
        }

        queue.Complete();
    }

    private async Task PumpStderrAsync(StreamReader reader)
    {
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    return;
                }

                try
                {
                    ErrorDataReceived?.Invoke(line);
                }
                catch (Exception)
                {
                    // 有故障的日志订阅者不能终止接收泵。
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
        {
            // 读取 stderr 期间进程已退出。
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _exited.TrySetResult(true);
    }

#if !NET8_0_OR_GREATER
    private static string BuildArgumentsString(IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(QuoteArgument(argument));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(character);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
#endif

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
