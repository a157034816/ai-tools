using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Terminal.Agent;
using AiTools.Terminal.Rendering;

namespace AiTools.Cli;

/// <summary>
/// 无 ANSI 环境下的行式 REPL。与全屏 TUI 共用 <see cref="AgentSession"/>，
/// 只把更新折叠成普通文本行输出，因此重定向、IDE 内嵌终端与 CI 里都能用。
/// </summary>
internal sealed class LineRepl
{
    private readonly AgentSession _session;
    private readonly TextWriter _output;
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private bool _reasoningHeaderPrinted;
    private readonly bool _showReasoning;

    public LineRepl(AgentSession session, TextWriter? output = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _output = output ?? Console.Out;
        _showReasoning = session.Options.ShowReasoning;
        _session.Updates += OnUpdate;
    }

    /// <summary>运行 REPL。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _output.WriteLine($"aitools {_session.Options.Version}（行式模式，终端不支持全屏或已指定 --plain）");
        _output.WriteLine("输入消息开始，/help 查看命令，/exit 退出。");
        foreach (var line in _session.DescribeMissingConfiguration())
        {
            _output.WriteLine("  " + line);
        }

        _output.WriteLine();

        while (!cancellationToken.IsCancellationRequested)
        {
            _output.Write(_session.Options.ApplicationName + " > ");
            _output.Flush();
            var line = Console.ReadLine();
            if (line == null)
            {
                break;
            }

            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (text is "/exit" or "/quit" or ":q")
            {
                break;
            }

            ResetBuffers();
            try
            {
                var result = await _session.SubmitAsync(text, cancellationToken).ConfigureAwait(false);
                if (result != null && string.IsNullOrEmpty(_text.ToString()) && !string.IsNullOrEmpty(result.Text))
                {
                    // 命令结果或非流式兜底：直接输出完整文本。
                    _output.WriteLine();
                    _output.WriteLine(result.Text);
                }

                if (result?.Error != null)
                {
                    _output.WriteLine("错误：" + result.Error);
                }

                _output.WriteLine();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _output.WriteLine("请求失败：" + ex.Message);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void ResetBuffers()
    {
        lock (_gate)
        {
            _text.Clear();
            _reasoning.Clear();
            _reasoningHeaderPrinted = false;
        }
    }

    private void OnUpdate(AgentUpdate update)
    {
        lock (_gate)
        {
            switch (update.Kind)
            {
                case AgentUpdateKind.TextDelta:
                    if (_reasoning.Length > 0 && !_text.ToString().Contains('\n'))
                    {
                        EndReasoningBlock();
                    }

                    _text.Append(update.Text);
                    _output.Write(update.Text);
                    _output.Flush();
                    break;

                case AgentUpdateKind.ReasoningDelta:
                    if (_showReasoning)
                    {
                        if (!_reasoningHeaderPrinted)
                        {
                            _output.Write("思考：");
                            _reasoningHeaderPrinted = true;
                        }

                        _reasoning.Append(update.Text);
                        _output.Write(update.Text);
                        _output.Flush();
                    }

                    break;

                case AgentUpdateKind.TurnStarted:
                    _output.WriteLine();
                    break;

                case AgentUpdateKind.ToolStarted when update.ToolCall != null:
                    EndReasoningBlock();
                    _output.WriteLine();
                    _output.WriteLine("  · 调用 " + update.ToolCall.Name);
                    break;

                case AgentUpdateKind.ToolFinished:
                    EndReasoningBlock();
                    var mark = update.IsError ? "  ✗ " : "  ✓ ";
                    var summary = update.ToolResult ?? string.Empty;
                    summary = summary.Replace("\r", " ").Replace("\n", " ");
                    if (summary.Length > 160)
                    {
                        summary = summary.Substring(0, 160) + "…";
                    }

                    _output.WriteLine(mark + (update.ToolCall?.Name ?? "tool")
                                            + "  " + update.Duration.TotalSeconds.ToString("0.0") + "s  " + summary);
                    break;

                case AgentUpdateKind.TurnCompleted:
                    EndReasoningBlock();
                    _output.WriteLine();
                    if (update.Usage != null)
                    {
                        _output.WriteLine($"  {update.Usage.InputTokens}→{update.Usage.OutputTokens} tokens");
                    }

                    break;

                case AgentUpdateKind.TurnFailed:
                    EndReasoningBlock();
                    _output.WriteLine();
                    break;

                case AgentUpdateKind.Notice:
                    _output.WriteLine("  · " + update.Text);
                    break;

                case AgentUpdateKind.DisplayText:
                    if (!string.IsNullOrWhiteSpace(update.Text))
                    {
                        _output.WriteLine();
                        _output.WriteLine(update.Text);
                    }

                    break;

                case AgentUpdateKind.Status:
                    break;
            }
        }
    }

    private void EndReasoningBlock()
    {
        if (_reasoningHeaderPrinted)
        {
            _output.WriteLine();
            _reasoningHeaderPrinted = false;
        }
    }
}
