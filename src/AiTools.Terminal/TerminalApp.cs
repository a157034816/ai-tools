using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Core.Sessions;
using AiTools.Extensions;
using AiTools.Terminal.Agent;
using AiTools.Terminal.Input;
using AiTools.Terminal.Rendering;
using AiTools.Terminal.Views;

namespace AiTools.Terminal;

/// <summary>
/// 全屏终端 Agent 界面：头部、记录区、输入框、状态栏，以及斜杠命令面板与浮层。
/// </summary>
/// <remarks>
/// 主循环在调用线程上串行运行：读取按键 → 处理 → 重绘。Agent 在后台任务中运行，
/// 其更新通过 Channel 汇入主循环，因此界面状态本身不需要加锁。
/// </remarks>
public sealed class TerminalApp : IAsyncDisposable
{
    private readonly AgentSession _session;
    private readonly ITerminalHost _host;
    private readonly TerminalTheme _theme;
    private readonly TerminalPrompt _prompt = new();
    private readonly Transcript _transcript = new();
    private readonly TranscriptRenderer _renderer;
    private readonly LineEditor _editor = new();
    private readonly ScreenBuffer _screen = new();
    private readonly Channel<AgentUpdate> _updates = Channel.CreateUnbounded<AgentUpdate>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly List<SlashCommand> _commands = new();
    private readonly List<TranscriptEntry> _pendingToolEntries = new();
    private Overlay? _overlay;
    private TranscriptEntry? _streamingEntry;
    private CancellationTokenSource? _turnCancellation;
    private int _spinnerFrame;
    private bool _exitRequested;
    private bool _showReasoning = true;
    private string _statusOverride = string.Empty;
    private SessionState _state = new();
    private int _scrollLines;

    /// <summary>创建界面。</summary>
    /// <param name="session">Agent 会话。</param>
    /// <param name="host">终端宿主。</param>
    /// <param name="theme">主题；null 时创建默认主题。</param>
    public TerminalApp(AgentSession session, ITerminalHost host, TerminalTheme? theme = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _theme = theme ?? TerminalTheme.CreateDefault();
        _renderer = new TranscriptRenderer(_theme);
        _showReasoning = session.Options.ShowReasoning;
        _session.Updates += OnAgentUpdate;
        _session.Prompt = _prompt;
        BuildCommands();
    }

    /// <summary>获取是否已请求退出。</summary>
    public bool ExitRequested => _exitRequested;

    /// <summary>获取或设置启动时放入输入框的文本（来自命令行位置参数）。</summary>
    public string? InitialInput { get; set; }

    /// <summary>运行主循环，直到用户退出或取消。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _prompt.IsInteractive = true;
        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Notice, WelcomeText()));

        // 恢复的会话先回放历史，初始提示词随后填入输入框。
        ReplayHistory();

        if (!string.IsNullOrWhiteSpace(InitialInput))
        {
            _editor.Insert(InitialInput!);
            InitialInput = null;
        }

        foreach (var line in _session.DescribeMissingConfiguration())
        {
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Notice, line));
        }

        Render();

        while (!_exitRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resized = _host.TryConsumeResize();
            if (resized.HasValue)
            {
                Render(force: true);
            }

            var wait = _host.ReadKeyAsync(cancellationToken);
            var key = await wait.ConfigureAwait(false);

            if (key.HasValue)
            {
                await HandleKeyAsync(key.Value, cancellationToken).ConfigureAwait(false);
                DrainUpdates();
                Render();
                continue;
            }

            // 没有按键：处理后台更新与动画。
            var hadUpdates = DrainUpdates();
            var animate = _session.IsBusy || (_overlay is null && _prompt.HasPending);
            if (animate)
            {
                _spinnerFrame++;
            }

            if (hadUpdates || animate || _host is ConsoleTerminalHost console && console.ConsumeRedrawRequest())
            {
                Render();
            }

            }

        _prompt.IsInteractive = false;
        _prompt.CancelAll();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _session.Updates -= OnAgentUpdate;
        _prompt.CancelAll();
        _prompt.IsInteractive = false;
        try
        {
            _turnCancellation?.Cancel();
            _turnCancellation?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        await _session.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // 主循环
    // ------------------------------------------------------------------

    private async Task HandleKeyAsync(TerminalKey key, CancellationToken cancellationToken)
    {
        if (_overlay != null)
        {
            if (_overlay.HandleKey(key))
            {
                _overlay = null;
            }

            return;
        }

        var pending = _prompt.TryTake();
        if (pending != null)
        {
            HandlePromptKey(pending, key);
            return;
        }

        switch (key.Kind)
        {
            case TerminalKeyKind.Enter:
                // 单行输入框：Enter 提交。多数终端无法区分 Shift+Enter，
                // 需要多行文本时直接粘贴，编辑器会把换行折叠为空格。
                await SubmitAsync(cancellationToken).ConfigureAwait(false);
                return;

            case TerminalKeyKind.Escape:
                if (_session.IsBusy)
                {
                    _session.CancelTurn();
                    _statusOverride = "正在停止…";
                }
                else if (!_editor.IsEmpty)
                {
                    _editor.Clear();
                }

                return;

            case TerminalKeyKind.Tab:
                CompleteCommand();
                return;

            case TerminalKeyKind.Backspace:
                _editor.Backspace();
                return;

            case TerminalKeyKind.Delete:
                _editor.Delete();
                return;

            case TerminalKeyKind.ArrowLeft:
                if (key.IsCtrl)
                {
                    _editor.MoveWordLeft();
                }
                else
                {
                    _editor.MoveLeft();
                }

                return;

            case TerminalKeyKind.ArrowRight:
                if (key.IsCtrl)
                {
                    _editor.MoveWordRight();
                }
                else
                {
                    _editor.MoveRight();
                }

                return;

            case TerminalKeyKind.Home:
                _editor.MoveHome();
                return;

            case TerminalKeyKind.End:
                _editor.MoveEnd();
                return;

            case TerminalKeyKind.ArrowUp:
                if (key.IsCtrl)
                {
                    ScrollTranscript(1);
                }
                else if (!_editor.HistoryPrevious())
                {
                    ScrollTranscript(1);
                }

                return;

            case TerminalKeyKind.ArrowDown:
                if (key.IsCtrl)
                {
                    ScrollTranscript(-1);
                }
                else if (!_editor.HistoryNext())
                {
                    ScrollTranscript(-1);
                }

                return;

            case TerminalKeyKind.PageUp:
                ScrollTranscript(10);
                return;

            case TerminalKeyKind.PageDown:
                ScrollTranscript(-10);
                return;

            case TerminalKeyKind.Paste:
                _editor.Insert(key.Text);
                return;

            case TerminalKeyKind.Character when key.Text == " " && _editor.IsEmpty && _scrollLines > 0:
                // 输入为空且处于回滚状态时，空格用于翻页浏览历史。
                ScrollTranscript(-8);
                return;

            case TerminalKeyKind.Character when key.IsCtrl:
                await HandleControlAsync(key, cancellationToken).ConfigureAwait(false);
                return;

            case TerminalKeyKind.Character:
                _editor.Insert(key.Text);
                return;
        }
    }

    private async Task HandleControlAsync(TerminalKey key, CancellationToken cancellationToken)
    {
        switch (key.Text)
        {
            case "c":
                if (_session.IsBusy)
                {
                    _session.CancelTurn();
                    _statusOverride = "已取消当前请求";
                }
                else if (_editor.IsEmpty)
                {
                    _exitRequested = true;
                }
                else
                {
                    _editor.Clear();
                }

                return;

            case "d":
                if (_editor.IsEmpty)
                {
                    _exitRequested = true;
                }

                return;

            case "l":
                Render(force: true);
                return;

            case "u":
                _editor.DeleteToStart();
                return;

            case "k":
                _editor.DeleteToEnd();
                return;

            case "w":
                _editor.DeleteWordBackward();
                return;

            case "a":
                _editor.MoveHome();
                return;

            case "e":
                _editor.MoveEnd();
                return;

            case "o":
                _showReasoning = !_showReasoning;
                _statusOverride = _showReasoning ? "显示推理过程" : "隐藏推理过程";
                return;

            case "r":
                await _session.ReloadSettingsAsync(cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task SubmitAsync(CancellationToken cancellationToken)
    {
        var text = _editor.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (_session.IsBusy)
        {
            _statusOverride = "正在生成，请先按 Esc 停止";
            return;
        }

        _editor.CommitHistory();
        _editor.Clear();

        if (await TryHandleLocalCommandAsync(text, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _turnCancellation?.Dispose();
        _turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _turnCancellation.Token;

        var task = Task.Run(async () =>
        {
            try
            {
                await _session.SubmitAsync(text, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _updates.Writer.TryWrite(new AgentUpdate
                {
                    Kind = AgentUpdateKind.Notice,
                    Text = "请求失败：" + ex.Message,
                    NoticeKind = NoticeKind.Error
                });
            }
        }, CancellationToken.None);

        _ = task;
    }

    // ------------------------------------------------------------------
    // Agent 更新
    // ------------------------------------------------------------------

    private void OnAgentUpdate(AgentUpdate update) => _updates.Writer.TryWrite(update);

    private bool DrainUpdates()
    {
        var changed = false;
        while (_updates.Reader.TryRead(out var update))
        {
            changed = true;
            ApplyUpdate(update);
        }

        return changed;
    }

    private void ApplyUpdate(AgentUpdate update)
    {
        switch (update.Kind)
        {
            case AgentUpdateKind.TurnStarted:
            case AgentUpdateKind.TextDelta:
            case AgentUpdateKind.ReasoningDelta:
                ApplyStream(update);
                break;

            case AgentUpdateKind.ToolStarted:
                if (update.ToolCall != null)
                {
                    var entry = new TranscriptEntry(TranscriptEntryKind.Tool, "执行中…")
                    {
                        ToolName = update.ToolCall.Name
                    };
                    _pendingToolEntries.Add(entry);
                    _transcript.Add(entry);
                }

                break;

            case AgentUpdateKind.ToolFinished:
                if (_pendingToolEntries.Count > 0)
                {
                    var entry = _pendingToolEntries[0];
                    _pendingToolEntries.RemoveAt(0);
                    var replacement = new TranscriptEntry(TranscriptEntryKind.Tool, update.ToolResult ?? string.Empty)
                    {
                        ToolName = update.ToolCall?.Name ?? entry.ToolName,
                        Duration = update.Duration,
                        IsError = update.IsError || string.IsNullOrEmpty(update.ToolResult)
                    };
                    var index = IndexOfEntry(entry);
                    _transcript.Remove(entry);
                    InsertEntry(index, replacement);
                }

                break;

            case AgentUpdateKind.TurnCompleted:
                SealStreamingEntry(update.Text, update.Usage);
                break;

            case AgentUpdateKind.TurnFailed:
                SealStreamingEntry(update.Text, null, failed: true, error: update.Error);
                break;

            case AgentUpdateKind.Notice:
                _transcript.Add(new TranscriptEntry(
                    update.NoticeKind == NoticeKind.Error ? TranscriptEntryKind.Error : TranscriptEntryKind.Notice,
                    update.Text ?? string.Empty));
                break;

            case AgentUpdateKind.DisplayText:
                if (!string.IsNullOrWhiteSpace(update.Text))
                {
                    _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Display, update.Text!));
                }

                break;

            case AgentUpdateKind.Status:
                _statusOverride = update.Text ?? string.Empty;
                break;

            case AgentUpdateKind.StateChanged:
                if (update.Payload is SessionState state)
                {
                    _state = state;
                }

                break;
        }
    }

    private void ApplyStream(AgentUpdate update)
    {
        if (_streamingEntry == null)
        {
            _streamingEntry = new TranscriptEntry(TranscriptEntryKind.Assistant, string.Empty)
            {
                IsStreaming = true
            };
            _transcript.Add(_streamingEntry);
        }

        switch (update.Kind)
        {
            case AgentUpdateKind.TextDelta:
                _streamingEntry.Text += update.Text;
                break;
            case AgentUpdateKind.ReasoningDelta:
                if (_showReasoning)
                {
                    _streamingEntry.Reasoning = (_streamingEntry.Reasoning ?? string.Empty) + update.Text;
                }

                break;
        }

        _streamingEntry.Touch();
    }

    private void SealStreamingEntry(string? text, Usage? usage, bool failed = false, string? error = null)
    {
        var entry = _streamingEntry;
        _streamingEntry = null;
        if (entry == null)
        {
            if (failed && !string.IsNullOrWhiteSpace(error))
            {
                _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Error, error!));
            }

            return;
        }

        entry.IsStreaming = false;
        if (!string.IsNullOrWhiteSpace(text))
        {
            entry.Text = text!;
        }

        entry.UsageText = usage == null ? null : FormatUsage(usage);
        entry.Touch();

        if (failed && !string.IsNullOrWhiteSpace(error))
        {
            _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Error, error!));
        }
    }

    private static string? FormatUsage(Usage usage)
    {
        if (usage.InputTokens.HasValue && usage.OutputTokens.HasValue)
        {
            return $"{usage.InputTokens.Value}→{usage.OutputTokens.Value} tokens";
        }

        return usage.TotalTokens.HasValue ? $"共 {usage.TotalTokens.Value} tokens" : null;
    }

    private int IndexOfEntry(TranscriptEntry entry)
    {
        var entries = _transcript.Entries;
        for (var index = 0; index < entries.Count; index++)
        {
            if (ReferenceEquals(entries[index], entry))
            {
                return index;
            }
        }

        return entries.Count;
    }

    private void InsertEntry(int index, TranscriptEntry entry)
    {
        // Transcript 只支持追加；按索引替换时先重建尾部。
        var entries = _transcript.Entries.ToList();
        index = Math.Clamp(index, 0, entries.Count);
        entries.Insert(index, entry);
        _transcript.ReplaceAll(entries);
    }

    // ------------------------------------------------------------------
    // 渲染
    // ------------------------------------------------------------------

    private void Render(bool force = false)
    {
        var width = Math.Max(20, _host.Width);
        var height = Math.Max(6, _host.Height);
        _screen.Resize(width, height);
        _screen.Clear();
        _screen.LastCursor = null;

        DrawHeader(width);
        var inputRow = height - 2;
        var statusRow = height - 1;
        var overlayTop = DrawOverlay(width, inputRow);
        DrawTranscript(width, 1, overlayTop ?? inputRow);
        DrawInput(width, inputRow);
        DrawStatus(width, statusRow);

        _host.Present(_screen, force);
    }

    private void DrawHeader(int width)
    {
        var title = _session.Options.ApplicationName;
        var version = _session.Options.Version;
        var segments = new List<StyledText>
        {
            new(" " + title, _theme.HeaderTitleStyle)
        };

        if (!string.IsNullOrWhiteSpace(version))
        {
            segments.Add(new StyledText(" " + version, _theme.HeaderStyle));
        }

        segments.Add(new StyledText("  ·  ", _theme.FaintStyle));
        segments.Add(new StyledText(TextWidth.Truncate(_state.Title, Math.Max(8, width / 3)), _theme.HeaderStyle));
        segments.Add(new StyledText("  ·  ", _theme.FaintStyle));
        segments.Add(new StyledText(_session.DescribeCurrentModel(), _theme.HeaderStyle));

        if (!string.IsNullOrWhiteSpace(_state.OutputStyle))
        {
            segments.Add(new StyledText("  ·  ", _theme.FaintStyle));
            segments.Add(new StyledText("风格 " + _state.OutputStyle, _theme.HeaderStyle));
        }

        if (_session.IsBusy)
        {
            segments.Add(new StyledText("  ", _theme.HeaderStyle));
            segments.Add(new StyledText(CurrentSpinner(), _theme.StatusAccentStyle));
        }

        _screen.Write(0, 0, segments, width, _theme.HeaderStyle);
    }

    /// <summary>绘制浮层并返回它占用的顶部行；没有浮层时返回 null。</summary>
    private int? DrawOverlay(int width, int inputRow)
    {
        var pending = _overlay == null ? _prompt.TryTake() : null;
        if (pending != null)
        {
            _overlay = CreatePromptOverlay(pending);
        }

        if (_overlay == null)
        {
            return null;
        }

        var available = Math.Max(4, inputRow - 2);
        var maxContent = Math.Max(2, Math.Min(available - 4, 18));
        var contentHeight = Math.Max(1, Math.Min(maxContent, _overlay.DesiredHeight(maxContent)));
        var boxWidth = Math.Clamp(width - 4, 20, 96);
        var boxHeight = contentHeight + 4;
        var x = Math.Max(0, (width - boxWidth) / 2);
        var y = Math.Max(2, inputRow - boxHeight);
        var box = new Rect(x, y, boxWidth, boxHeight);

        DrawBox(box, _overlay.Title, _overlay.Hint);

        var content = new Rect(box.X + 2, box.Y + 2, Math.Max(1, box.Width - 4), Math.Max(1, box.Height - 4));
        _overlay.Draw(_screen, content, _theme);
        return box.Y;
    }

    private void DrawBox(Rect rect, string title, string hint)
    {
        var horizontal = _theme.Unicode ? '─' : '-';
        var vertical = _theme.Unicode ? '│' : '|';
        var topLeft = _theme.Unicode ? '┌' : '+';
        var topRight = _theme.Unicode ? '┐' : '+';
        var bottomLeft = _theme.Unicode ? '└' : '+';
        var bottomRight = _theme.Unicode ? '┘' : '+';

        var innerWidth = rect.Width - 2;
        var titleText = TextWidth.Truncate(" " + title + " ", innerWidth);
        var top = topLeft + titleText + new string(horizontal, Math.Max(0, innerWidth - TextWidth.Of(titleText))) + topRight;
        var bottom = bottomLeft + new string(horizontal, innerWidth) + bottomRight;

        _screen.Write(rect.X, rect.Y, new[] { new StyledText(top, _theme.OverlayBorderStyle) }, rect.Width);

        for (var row = 1; row < rect.Height - 1; row++)
        {
            _screen.Write(rect.X, rect.Y + row, new[]
            {
                new StyledText(vertical.ToString(), _theme.OverlayBorderStyle),
                new StyledText(new string(' ', innerWidth), _theme.OverlayStyle),
                new StyledText(vertical.ToString(), _theme.OverlayBorderStyle)
            }, rect.Width);
        }

        _screen.Write(rect.X, rect.Bottom - 1, new[] { new StyledText(bottom, _theme.OverlayBorderStyle) }, rect.Width);

        if (!string.IsNullOrWhiteSpace(hint) && rect.Height >= 3)
        {
            var hintText = TextWidth.Truncate(" " + hint + " ", innerWidth);
            _screen.Write(rect.X + 1, rect.Bottom - 1, new[]
            {
                new StyledText(hintText, _theme.OverlayMutedStyle),
                new StyledText(new string(' ', Math.Max(0, innerWidth - TextWidth.Of(hintText))), _theme.OverlayMutedStyle)
            }, innerWidth);
        }
    }

    private void DrawTranscript(int width, int top, int bottom)
    {
        var available = Math.Max(1, bottom - top);
        var entries = _transcript.Entries;
        var lines = _renderer.Render(entries, width);
        _transcript.ReportLineCount(lines.Count);
        _scrollLines = _transcript.ScrollOffset;

        var start = Math.Max(0, lines.Count - available - _scrollLines);
        var end = Math.Min(lines.Count, start + available);

        for (var index = start; index < end; index++)
        {
            var line = lines[index];
            var row = top + (index - start);
            if (line.Background.HasValue)
            {
                _screen.Write(0, row, line.Segments, width, _theme.CodeStyle);
            }
            else
            {
                _screen.Write(0, row, line.Segments, width);
            }
        }

        if (_scrollLines > 0)
        {
            var indicator = new[]
            {
                new StyledText(TextWidth.Truncate($" ↑ 已向上滚动 {_scrollLines} 行 · End 或 PageDown 回到最新", width), _theme.WarningStyle)
            };
            _screen.Write(0, bottom - 1, indicator, width);
        }
    }

    private void DrawInput(int width, int row)
    {
        if (_overlay != null || _prompt.HasPending)
        {
            _screen.Write(0, row, new[]
            {
                new StyledText(TextWidth.Truncate(" 浮层处理中，请先完成上方交互", width), _theme.InputPlaceholderStyle)
            }, width, _theme.InputStyle);
            return;
        }

        var segments = new List<StyledText> { new(" ", _theme.InputStyle) };
        _editor.Render(Math.Max(1, width - 2), _theme, _theme.PromptSymbol + " ", out var inner, out var cursorColumn);
        segments.AddRange(inner);
        _screen.Write(0, row, segments, width, _theme.InputStyle);

        // 光标定位：把终端光标移到输入位置，保证输入法候选框位置正确。
        _screen.LastCursor = (1 + cursorColumn, row);
    }

    private void DrawStatus(int width, int row)
    {
        string status;
        TextStyle style;

        if (_overlay != null)
        {
            status = _overlay.Hint;
            style = _theme.StatusStyle;
        }
        else if (_session.IsBusy)
        {
            status = CurrentSpinner() + " " + (_statusOverride.Length > 0 ? _statusOverride : "正在生成…") + "   Esc 停止";
            style = _theme.StatusStyle;
        }
        else if (_prompt.HasPending)
        {
            status = "等待交互…";
            style = _theme.WarningStyle;
        }
        else
        {
            status = _statusOverride.Length > 0 ? _statusOverride : _session.Status;
            style = _theme.StatusStyle;
        }

        var segments = new List<StyledText>
        {
            new(" " + TextWidth.Truncate(status, Math.Max(10, width - 24)), style)
        };

        var right = new List<string>();
        if (_showReasoning)
        {
            right.Add("思考");
        }

        if (_scrollLines > 0)
        {
            right.Add("↑" + _scrollLines);
        }

        if (_session.IsBusy)
        {
            right.Add("Esc 停止");
        }

        var hint = string.Join(" · ", right);
        if (hint.Length > 0)
        {
            var hintWidth = TextWidth.Of(hint) + 1;
            var statusWidth = Math.Max(0, width - hintWidth);
            segments.Clear();
            segments.Add(new StyledText(" " + TextWidth.Truncate(status, Math.Max(10, statusWidth - 1)), style));
            segments.Add(new StyledText(new string(' ', Math.Max(0, statusWidth - TextWidth.Of(segments[0].Text))), style));
            segments.Add(new StyledText(hint + " ", _theme.StatusAccentStyle));
        }

        _screen.Write(0, row, segments, width, _theme.StatusStyle);
    }

    private string CurrentSpinner()
        => _theme.SpinnerFrames[Math.Abs(_spinnerFrame) % _theme.SpinnerFrames.Count];

    private string WelcomeText()
    {
        var builder = new StringBuilder();
        builder.Append("输入消息开始对话，");
        builder.Append("/ 查看命令，? 查看快捷键，Ctrl+C 退出。");
        if (_state.MessageCount > 0)
        {
            builder.Append(" 当前会话：").Append(_state.Title);
        }

        return builder.ToString();
    }

    private void ScrollTranscript(int lines)
    {
        if (lines > 0)
        {
            _transcript.ScrollUp(lines);
        }
        else
        {
            _transcript.ScrollDown(-lines);
        }
    }

    // ------------------------------------------------------------------
    // 交互浮层
    // ------------------------------------------------------------------

    private Overlay CreatePromptOverlay(PromptRequest request)
    {
        switch (request.Kind)
        {
            case PromptRequestKind.Confirm:
                var confirm = new ConfirmOverlay(request.Title, request.Message ?? "是否继续？", request.DefaultValue);
                confirm.Result.ContinueWith(
                    task => _prompt.Complete(request, task.Result),
                    TaskScheduler.Default);
                return confirm;

            case PromptRequestKind.Input:
                var input = new TextInputOverlay(request.Title, request.Placeholder);
                input.Result.ContinueWith(
                    task => _prompt.Complete(request, task.Result),
                    TaskScheduler.Default);
                return input;

            case PromptRequestKind.Select:
                var items = new List<(string, string?)>();
                foreach (var option in request.Options)
                {
                    items.Add((option, null));
                }

                var select = new ListOverlay(request.Title, request.Message, items);
                select.Result.ContinueWith(
                    task => _prompt.Complete(request, task.Result),
                    TaskScheduler.Default);
                return select;

            default:
                var choices = new List<(string, string?)>();
                foreach (var choice in request.Choices)
                {
                    choices.Add((choice.Label, choice.Description));
                }

                var choose = new ListOverlay(request.Title, request.Message, choices);
                choose.Result.ContinueWith(
                    task => _prompt.Complete(request, task.Result),
                    TaskScheduler.Default);
                return choose;
        }
    }

    private void HandlePromptKey(PromptRequest request, TerminalKey key)
    {
        // 浮层通常已由上一帧的 DrawOverlay 创建；这里处理"请求刚到达、尚未绘制"的窗口。
        _overlay ??= CreatePromptOverlay(request);
        if (_overlay.HandleKey(key))
        {
            _overlay = null;
        }
    }

    // ------------------------------------------------------------------
    // 斜杠命令
    // ------------------------------------------------------------------

    private void BuildCommands()
    {
        _commands.Add(SlashCommand.Local("help", "列出命令与快捷键", HelpAsync));
        _commands.Add(SlashCommand.Local("model", "切换服务商与模型", SelectModelAsync));
        _commands.Add(SlashCommand.Local("resume", "恢复已保存的会话", ResumeAsync));
        _commands.Add(SlashCommand.Local("fork", "把当前会话分叉为新会话", ForkAsync));
        _commands.Add(SlashCommand.Local("new", "开始新会话", NewAsync));
        _commands.Add(SlashCommand.Local("clear", "清空当前会话（不保存）", ClearAsync));
        _commands.Add(SlashCommand.Local("copy", "复制当前会话到剪贴板（支持 --last 只复制最后一条回复）", CopyAsync));
        _commands.Add(SlashCommand.Local("status", "显示会话与扩展状态", StatusAsync));
        _commands.Add(SlashCommand.Local("output-style", "查看或切换输出风格", OutputStyleAsync));
        _commands.Add(SlashCommand.Local("extensions", "列出已加载的扩展、扩展包与插件", ExtensionsAsync));
        _commands.Add(SlashCommand.Local("reasoning", "显示或隐藏推理过程", ReasoningAsync));
        _commands.Add(SlashCommand.Local("settings", "显示设置文件位置与配置摘要", SettingsAsync));
        _commands.Add(SlashCommand.Local("exit", "退出", (_, _) => { _exitRequested = true; return Task.CompletedTask; }));
        _commands.Add(SlashCommand.Local("quit", "退出", (_, _) => { _exitRequested = true; return Task.CompletedTask; }));

        var catalog = _session.Commands;
        if (catalog != null)
        {
            foreach (var command in catalog.Commands)
            {
                _commands.Add(SlashCommand.External(command));
            }
        }
    }

    private async Task<bool> TryHandleLocalCommandAsync(string text, CancellationToken cancellationToken)
    {
        if (!PluginHost.LooksLikeCommand(text))
        {
            if (string.Equals(text.Trim(), "?", StringComparison.Ordinal))
            {
                ShowHelp();
                return true;
            }

            return false;
        }

        var (name, raw) = PluginHost.SplitCommand(text);
        var command = _commands.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                      ?? _commands.FirstOrDefault(item =>
                          string.Equals(item.ShortName, name, StringComparison.OrdinalIgnoreCase) && item.IsLocal);

        if (command == null || !command.IsLocal)
        {
            return false;
        }

        var args = CommandArguments.Parse(raw);
        await command.Handler(args, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void ShowHelp()
    {
        var bindings = new List<(string, string)>
        {
            ("Enter", "发送消息（输入框为单行；粘贴的多行文本会折叠为空格）"),
            ("Tab", "补全斜杠命令"),
            ("↑ / ↓", "输入历史（空输入时滚动记录）"),
            ("PgUp / PgDn", "滚动记录（空格在空输入时向下翻页）"),
            ("Ctrl+O", "显示/隐藏推理过程"),
            ("Ctrl+L", "重绘屏幕"),
            ("Ctrl+R", "重新载入设置"),
            ("Esc", "停止生成 / 清空输入 / 关闭浮层"),
            ("Ctrl+C", "停止生成；再次按下退出"),
            ("Ctrl+D / /exit", "退出")
        };

        _overlay = new HelpOverlay("快捷键", bindings, "输入 / 查看全部命令，包括扩展、插件与技能命令。");
    }

    private Task HelpAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        ShowHelp();
        return Task.CompletedTask;
    }

    private async Task SelectModelAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var models = _session.AvailableModels;
        if (models.Count == 0)
        {
            _statusOverride = "没有可用的模型配置";
            return;
        }

        var items = models.Select(item => (item.Label, (string?)(item.Provider.Id + " · " + item.Model.Id))).ToList();
        var current = models.ToList().FindIndex(item =>
            string.Equals(item.Provider.Id, _session.CurrentProvider?.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Model.Id, _session.CurrentModel?.Id, StringComparison.OrdinalIgnoreCase));

        var overlay = new ListOverlay("选择模型", "↑↓ 选择，Enter 确认", items, Math.Max(0, current));
        _overlay = overlay;
        var index = await overlay.Result.ConfigureAwait(false);
        _overlay = null;
        if (index is { } selected && selected >= 0 && selected < models.Count)
        {
            var option = models[selected];
            _session.SelectModel(option.Provider.Id, option.Model.Id);
            _statusOverride = "模型已切换为 " + option.Label;
        }
    }

    private async Task ResumeAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var summaries = await _session.ListConversationsAsync(cancellationToken).ConfigureAwait(false);
        if (summaries.Count == 0)
        {
            _statusOverride = "还没有已保存的会话";
            return;
        }

        var items = summaries
            .Select(summary => (summary.Title, (string?)(summary.GetRelativeTime() + " · " + summary.MessageCount + " 条")))
            .ToList();

        var overlay = new ListOverlay("恢复会话", "↑↓ 选择，Enter 恢复", items);
        _overlay = overlay;
        var index = await overlay.Result.ConfigureAwait(false);
        _overlay = null;
        if (index is not { } selected || selected < 0 || selected >= summaries.Count)
        {
            return;
        }

        var record = summaries[selected];
        if (await _session.ResumeAsync(record.Id, cancellationToken).ConfigureAwait(false))
        {
            _transcript.Clear();
            _streamingEntry = null;
            _pendingToolEntries.Clear();
            ReplayHistory();
            _renderer.Invalidate();
            _statusOverride = "已恢复「" + record.Title + "」";
        }
    }

    /// <summary>把会话历史回放到记录区（恢复会话时使用）。</summary>
    private void ReplayHistory()
    {
        var history = _session.History;
        if (history.Count == 0)
        {
            return;
        }

        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Divider, string.Empty));
        foreach (var message in history)
        {
            var entry = message.IsUser
                ? new TranscriptEntry(TranscriptEntryKind.User, message.DisplayText ?? message.Message.GetText())
                : new TranscriptEntry(TranscriptEntryKind.Assistant, message.Message.GetText());
            _transcript.Add(entry);
        }
    }

    private async Task ForkAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        if (await _session.ForkAsync(null, cancellationToken).ConfigureAwait(false))
        {
            _statusOverride = "已分叉为「" + _session.Title + "」";
        }
    }

    private async Task NewAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        await _session.NewConversationAsync().ConfigureAwait(false);
        _transcript.Clear();
        _renderer.Invalidate();
        _streamingEntry = null;
        _pendingToolEntries.Clear();
        _statusOverride = "新会话";
    }

    private Task ClearAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        _transcript.Clear();
        _renderer.Invalidate();
        _streamingEntry = null;
        _pendingToolEntries.Clear();
        _statusOverride = "已清空记录（会话仍保留在磁盘上）";
        return Task.CompletedTask;
    }

    private Task CopyAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var lastOnly = args.Positional.Any(item => string.Equals(item, "--last", StringComparison.OrdinalIgnoreCase));
        string text;
        if (lastOnly)
        {
            var entry = _transcript.Entries.LastOrDefault(item => item.Kind == TranscriptEntryKind.Assistant);
            text = entry?.Text ?? string.Empty;
        }
        else
        {
            text = _transcript.ToPlainText(includeTools: false);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _statusOverride = "没有可复制的文本";
            return Task.CompletedTask;
        }

        try
        {
            ClipboardWriter.Write(text);
            _statusOverride = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            _statusOverride = "复制失败：" + ex.Message;
        }

        return Task.CompletedTask;
    }

    private Task StatusAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("会话：" + _session.Title);
        builder.AppendLine("模型：" + _session.DescribeCurrentModel());
        builder.AppendLine("消息：" + _session.History.Count + " 条");
        builder.AppendLine("工作目录：" + _session.WorkingDirectory);
        builder.AppendLine("输出风格：" + (_session.SelectedStyle.Style?.Name ?? "无"));
        builder.AppendLine("技能：" + (_session.SkillProvider?.SkillCount ?? 0) + " 个");
        builder.AppendLine("命令：" + (_session.Commands?.Count ?? 0) + " 条（扩展/插件/技能）");
        builder.AppendLine("插件：" + (_session.PluginHost?.Catalog.Count ?? 0) + " 个");
        builder.AppendLine("扩展包：" + (_session.Workspace?.Packages.Count ?? 0) + " 个");
        builder.AppendLine("共享目录：" + (_session.SharedAgents?.SkillRoots.Count ?? 0) + " 个技能根");

        if (_session.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("诊断：");
            foreach (var diagnostic in _session.Diagnostics.Take(12))
            {
                builder.AppendLine("  " + diagnostic);
            }
        }

        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Display, builder.ToString().TrimEnd()));
        _statusOverride = "已显示会话状态";
        return Task.CompletedTask;
    }

    private Task OutputStyleAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var catalog = _session.OutputStyles;
        var name = args.Raw.Trim();
        if (name.Length == 0)
        {
            if (catalog == null || catalog.Count == 0)
            {
                _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Display, "没有可用的输出风格。"));
            }
            else
            {
                var builder = new StringBuilder();
                builder.AppendLine("当前输出风格：" + (_session.SelectedStyle.Style?.Name ?? "无"));
                builder.AppendLine("可用输出风格：");
                foreach (var style in catalog.Styles)
                {
                    builder.AppendLine("  " + style.Name + " — " + style.Description);
                }

                _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Display, builder.ToString().TrimEnd()));
            }

            return Task.CompletedTask;
        }

        var changed = _session.SelectOutputStyle(name);
        _transcript.Add(new TranscriptEntry(
            TranscriptEntryKind.Display,
            changed ? "已切换到输出风格：" + name : "没有名为 " + name + " 的输出风格，或当前已是该风格。"));
        return Task.CompletedTask;
    }

    private Task ExtensionsAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var pipeline = _session.Pipeline;
        if (pipeline == null)
        {
            builder.AppendLine("扩展未启用。");
        }
        else
        {
            builder.AppendLine("扩展：");
            foreach (var extension in pipeline.Runtime.Extensions)
            {
                builder.AppendLine("  " + extension.Name + (string.IsNullOrWhiteSpace(extension.Description) ? string.Empty : " — " + extension.Description));
            }

            if (_session.Workspace != null)
            {
                builder.AppendLine();
                builder.AppendLine("扩展包：");
                foreach (var package in _session.Workspace.Packages)
                {
                    var status = package.IsLoaded ? "已加载" : "失败";
                    builder.AppendLine("  " + package.Manifest.Name + " (" + package.Manifest.Kind + ", " + status + ")");
                }
            }
        }

        if (_session.PluginHost != null)
        {
            builder.AppendLine();
            builder.AppendLine("插件：");
            foreach (var plugin in _session.PluginHost.Catalog.Plugins)
            {
                builder.AppendLine("  " + plugin.Name + " — " + plugin.Commands.Count + " 命令, " + plugin.Skills.Count + " 技能");
            }
        }

        if (_session.SkillProvider != null)
        {
            builder.AppendLine();
            builder.AppendLine("技能：");
            foreach (var skill in _session.SkillProvider.Toolset.Skills)
            {
                builder.AppendLine("  " + skill.Name + " — " + skill.Description);
            }
        }

        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Display, builder.ToString().TrimEnd()));
        return Task.CompletedTask;
    }

    private Task ReasoningAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var value = args.Get(0);
        _showReasoning = value == null
            ? !_showReasoning
            : !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        _statusOverride = _showReasoning ? "显示推理过程" : "隐藏推理过程";
        return Task.CompletedTask;
    }

    private Task SettingsAsync(CommandArguments args, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("设置文件：" + DescribeSettingsLocation());
        builder.AppendLine("数据根目录：" + AiTools.Core.AiToolsPaths.GetRootDirectory());
        builder.AppendLine();
        builder.AppendLine("服务商：");
        foreach (var model in _session.AvailableModels)
        {
            builder.AppendLine("  " + model.Label + (model.Provider == _session.CurrentProvider && model.Model == _session.CurrentModel ? "  ← 当前" : string.Empty));
        }

        _transcript.Add(new TranscriptEntry(TranscriptEntryKind.Display, builder.ToString().TrimEnd()));
        return Task.CompletedTask;
    }

    private string DescribeSettingsLocation()
    {
        var store = _session.Options.SettingsStore;
        return store is JsonFileSettingsStore fileStore
            ? fileStore.FilePath
            : AiTools.Core.AiToolsPaths.GetSettingsFile();
    }

    private void CompleteCommand()
    {
        var text = _editor.Text;
        if (!text.StartsWith("/", StringComparison.Ordinal) || text.Contains(' '))
        {
            return;
        }

        var token = text.Substring(1);
        var match = _commands
            .Where(item => item.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           item.ShortName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (match != null)
        {
            _editor.Text = "/" + match.InvocationName + " ";
            _editor.MoveEnd();
        }
    }

    private sealed class SlashCommand
    {
        private SlashCommand(string name, string description, bool isLocal, Func<CommandArguments, CancellationToken, Task> handler, string? hint)
        {
            Name = name;
            Description = description;
            IsLocal = isLocal;
            Handler = handler;
            Hint = hint;
        }

        public string Name { get; }

        public string ShortName => Name;

        public string Description { get; }

        public bool IsLocal { get; }

        public string? Hint { get; }

        public Func<CommandArguments, CancellationToken, Task> Handler { get; }

        public string InvocationName => Name;

        public static SlashCommand Local(string name, string description, Func<CommandArguments, CancellationToken, Task> handler)
            => new(name, description, true, handler, null);

        public static SlashCommand External(ResolvedCommand command)
            => new(command.ShortName, command.Description, false, (_, _) => Task.CompletedTask, command.ArgumentHint);
    }
}
