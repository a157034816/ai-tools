using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AiTools.Terminal.Input;
using AiTools.Terminal.Rendering;

namespace AiTools.Terminal;

/// <summary>
/// 基于控制台的标准终端宿主。进入时切换到备用屏幕并切换到原始输入模式，
/// 退出时（包括异常）恢复光标、屏幕与终端模式。
/// </summary>
/// <remarks>
/// 输入在后台线程读取：Windows 上把控制台切到虚拟终端输入模式，从而获得
/// 方向键、括号粘贴等序列；Unix 上通过 <c>stty raw -echo</c> 关闭行缓冲。
/// 读取失败或平台不支持时退化为 <see cref="Console.ReadKey(bool)"/>。
/// </remarks>
public sealed class ConsoleTerminalHost : ITerminalHost
{
    private readonly FrameComposer _composer = new();
    private readonly KeyParser _parser = new();
    private readonly Channel<TerminalKey> _keys = Channel.CreateUnbounded<TerminalKey>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _parserGate = new();
    private readonly Timer _escapeTimer;
    private readonly Timer _resizeTimer;
    private readonly TextWriter _output;
    private readonly bool _useAlternateScreen;
    private volatile bool _disposed;
    private volatile bool _redrawRequested;
    private long _pendingEscapeTimestamp;
    private int _width;
    private int _height;
    private (int Width, int Height)? _pendingResize;
    private string? _savedStty;
    private uint _savedConsoleMode;
    private IntPtr _stdinHandle;
    private bool _rawModeActive;
    private bool _consoleModeSaved;

    /// <summary>创建终端宿主。</summary>
    /// <param name="useAlternateScreen">
    /// 是否使用备用屏幕。false 时原地绘制并保留退出后的终端内容（适合在 IDE 内嵌终端中运行）。
    /// </param>
    /// <param name="output">输出目标；默认 <see cref="Console.Out"/>。</param>
    public ConsoleTerminalHost(bool useAlternateScreen = true, TextWriter? output = null)
    {
        _useAlternateScreen = useAlternateScreen;
        _output = output ?? Console.Out;
        SupportsAnsi = TerminalCapabilities.SupportsAnsi;
        (_width, _height) = TerminalCapabilities.GetSize();
        _escapeTimer = new Timer(FlushPendingEscape, null, 25, 25);
        _resizeTimer = new Timer(PollResize, null, 120, 120);
    }

    /// <inheritdoc />
    public int Width => Volatile.Read(ref _width);

    /// <inheritdoc />
    public int Height => Volatile.Read(ref _height);

    /// <inheritdoc />
    public bool SupportsAnsi { get; }

    /// <summary>获取输入会话是否已关闭。</summary>
    public bool IsClosed => _disposed;

    /// <summary>进入全屏模式并启动输入线程。</summary>
    public void Start()
    {
        if (!SupportsAnsi)
        {
            return;
        }

        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception)
        {
            // 某些宿主（重定向、IDE）不允许修改编码；输入读取会按替换字符降级。
        }

        EnableRawInput();

        if (_useAlternateScreen)
        {
            WriteRaw("\u001b[?1049h");
        }

        WriteRaw("\u001b[?25l\u001b[?2004h\u001b[0m\u001b[H\u001b[2J");
        StartReading();
    }

    /// <inheritdoc />
    public void Present(ScreenBuffer screen, bool force = false)
    {
        if (!SupportsAnsi || _disposed)
        {
            return;
        }

        var frame = _composer.Compose(screen, force);
        if (frame.Length == 0)
        {
            return;
        }

        var cursor = screen.LastCursor;
        if (cursor.HasValue)
        {
            var (x, y) = cursor.Value;
            x = Math.Clamp(x, 0, Math.Max(0, screen.Width - 1));
            y = Math.Clamp(y, 0, Math.Max(0, screen.Height - 1));
            frame += "\u001b[" + (y + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ";" +
                     (x + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "H\u001b[?25h";
        }
        else
        {
            frame += "\u001b[?25l";
        }

        WriteRaw(frame);
    }

    /// <inheritdoc />
    public async ValueTask<TerminalKey?> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return null;
        }

        _redrawRequested = false;
        if (_keys.Reader.TryRead(out var ready))
        {
            return ready;
        }

        var wait = _keys.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var completed = await Task.WhenAny(wait, Task.Delay(60, cancellationToken)).ConfigureAwait(false);
        if (completed != wait)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        if (!await wait.ConfigureAwait(false))
        {
            return null;
        }

        return _keys.Reader.TryRead(out var key) ? key : null;
    }

    /// <inheritdoc />
    public (int Width, int Height)? TryConsumeResize()
    {
        var resize = _pendingResize;
        _pendingResize = null;
        return resize;
    }

    /// <inheritdoc />
    public void RequestRedraw() => _redrawRequested = true;

    /// <summary>获取自上次调用以来是否请求过重绘。</summary>
    public bool ConsumeRedrawRequest()
    {
        if (!_redrawRequested)
        {
            return false;
        }

        _redrawRequested = false;
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _escapeTimer.Dispose();
            _resizeTimer.Dispose();
            _keys.Writer.TryComplete();
            if (SupportsAnsi)
            {
                WriteRaw("\u001b[?2004l\u001b[?25h\u001b[0m");
                if (_useAlternateScreen)
                {
                    WriteRaw("\u001b[?1049l");
                }
                else
                {
                    WriteRaw("\u001b[H\u001b[2J");
                }
            }

            RestoreRawInput();
        }
        catch (Exception)
        {
            // 关闭阶段的失败不应掩盖真正的异常。
        }
    }

    private void WriteRaw(string text)
    {
        try
        {
            _output.Write(text);
            _output.Flush();
        }
        catch (Exception)
        {
            // 输出管道已断开（例如宿主退出）时忽略。
        }
    }

    private void StartReading()
    {
        var thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "AiTools.Terminal.Input"
        };
        thread.Start();
    }

    private void ReadLoop()
    {
        try
        {
            var stream = Console.OpenStandardInput();
            var buffer = new byte[4096];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[8192];

            while (!_disposed)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    // stdin 关闭（例如被管道接管）时结束输入，让宿主正常退出。
                    _keys.Writer.TryComplete();
                    return;
                }

                var count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count <= 0)
                {
                    continue;
                }

                lock (_parserGate)
                {
                    _parser.Feed(new string(chars, 0, count));
                    while (_parser.TryParse(out var key))
                    {
                        _keys.Writer.TryWrite(key);
                    }

                    _pendingEscapeTimestamp = _parser.HasLoneEscape ? Stopwatch.GetTimestamp() : 0;
                }
            }
        }
        catch (Exception)
        {
            _keys.Writer.TryComplete();
        }
    }

    private void FlushPendingEscape(object? state)
    {
        if (_disposed)
        {
            return;
        }

        lock (_parserGate)
        {
            if (!_parser.HasLoneEscape)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _pendingEscapeTimestamp) / (double)Stopwatch.Frequency;
            if (elapsed < 0.035)
            {
                return;
            }

            _keys.Writer.TryWrite(_parser.FlushEscape());
            _pendingEscapeTimestamp = 0;
        }
    }

    private void PollResize(object? state)
    {
        if (_disposed || !SupportsAnsi)
        {
            return;
        }

        var (width, height) = TerminalCapabilities.GetSize();
        if (width == Volatile.Read(ref _width) && height == Volatile.Read(ref _height))
        {
            return;
        }

        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
        _pendingResize = (width, height);
        _redrawRequested = true;
    }

    private void EnableRawInput()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            EnableWindowsRawInput();
        }
        else
        {
            EnableUnixRawInput();
        }
    }

    private void RestoreRawInput()
    {
        if (!_rawModeActive)
        {
            return;
        }

        _rawModeActive = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (_consoleModeSaved)
            {
                NativeMethods.SetConsoleMode(_stdinHandle, _savedConsoleMode);
            }
        }
        else if (!string.IsNullOrEmpty(_savedStty))
        {
            try
            {
                var startInfo = new ProcessStartInfo("/bin/sh")
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add("stty " + _savedStty);
                using var process = Process.Start(startInfo);
                process?.WaitForExit(2000);
            }
            catch (Exception)
            {
                // 恢复失败时终端仍可用，只是回显可能保持关闭。
            }
        }
    }

    private void EnableWindowsRawInput()
    {
        try
        {
            _stdinHandle = NativeMethods.GetStdHandle(NativeMethods.StdInputHandle);
            if (_stdinHandle == IntPtr.Zero || _stdinHandle == new IntPtr(-1))
            {
                return;
            }

            if (!NativeMethods.GetConsoleMode(_stdinHandle, out var mode))
            {
                return;
            }

            _savedConsoleMode = mode;
            _consoleModeSaved = true;

            const uint enableExtendedFlags = 0x0080;
            const uint disableQuickEdit = 0x0040;
            const uint enableVirtualTerminalInput = 0x0200;
            const uint enableWindowInput = 0x0008;
            const uint enableProcessedInput = 0x0001;
            const uint enableLineInput = 0x0002;
            const uint enableEchoInput = 0x0004;

            var raw = (mode | enableExtendedFlags | enableVirtualTerminalInput | enableWindowInput)
                      & ~(disableQuickEdit | enableProcessedInput | enableLineInput | enableEchoInput);

            _rawModeActive = NativeMethods.SetConsoleMode(_stdinHandle, raw);
        }
        catch (Exception)
        {
            // 非 Windows 控制台或权限不足：回退到 Console.ReadKey。
            _rawModeActive = false;
            StartReadKeyFallback();
        }
    }

    private void EnableUnixRawInput()
    {
        try
        {
            var startInfo = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("stty -g");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return;
            }

            _savedStty = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            if (string.IsNullOrEmpty(_savedStty))
            {
                return;
            }

            var rawInfo = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            rawInfo.ArgumentList.Add("-c");
            rawInfo.ArgumentList.Add("stty raw -echo");
            using var rawProcess = Process.Start(rawInfo);
            rawProcess?.WaitForExit(2000);
            _rawModeActive = true;
        }
        catch (Exception)
        {
            _rawModeActive = false;
        }
    }

    private void StartReadKeyFallback()
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (!_disposed)
                {
                    var info = Console.ReadKey(intercept: true);
                    var key = MapConsoleKey(info);
                    if (key.HasValue)
                    {
                        _keys.Writer.TryWrite(key.Value);
                    }
                }
            }
            catch (Exception)
            {
                _keys.Writer.TryComplete();
            }
        })
        {
            IsBackground = true,
            Name = "AiTools.Terminal.ReadKey"
        };
        thread.Start();
    }

    private static TerminalKey? MapConsoleKey(ConsoleKeyInfo info)
    {
        var modifiers = KeyModifiers.None;
        if ((info.Modifiers & ConsoleModifiers.Shift) != 0)
        {
            modifiers |= KeyModifiers.Shift;
        }

        if ((info.Modifiers & ConsoleModifiers.Alt) != 0)
        {
            modifiers |= KeyModifiers.Alt;
        }

        if ((info.Modifiers & ConsoleModifiers.Control) != 0)
        {
            modifiers |= KeyModifiers.Ctrl;
        }

        var kind = info.Key switch
        {
            ConsoleKey.Enter => TerminalKeyKind.Enter,
            ConsoleKey.Tab => TerminalKeyKind.Tab,
            ConsoleKey.Backspace => TerminalKeyKind.Backspace,
            ConsoleKey.Delete => TerminalKeyKind.Delete,
            ConsoleKey.Escape => TerminalKeyKind.Escape,
            ConsoleKey.UpArrow => TerminalKeyKind.ArrowUp,
            ConsoleKey.DownArrow => TerminalKeyKind.ArrowDown,
            ConsoleKey.LeftArrow => TerminalKeyKind.ArrowLeft,
            ConsoleKey.RightArrow => TerminalKeyKind.ArrowRight,
            ConsoleKey.Home => TerminalKeyKind.Home,
            ConsoleKey.End => TerminalKeyKind.End,
            ConsoleKey.PageUp => TerminalKeyKind.PageUp,
            ConsoleKey.PageDown => TerminalKeyKind.PageDown,
            ConsoleKey.Insert => TerminalKeyKind.Insert,
            ConsoleKey.F1 => TerminalKeyKind.F1,
            ConsoleKey.F2 => TerminalKeyKind.F2,
            ConsoleKey.F3 => TerminalKeyKind.F3,
            ConsoleKey.F4 => TerminalKeyKind.F4,
            ConsoleKey.F5 => TerminalKeyKind.F5,
            ConsoleKey.F6 => TerminalKeyKind.F6,
            ConsoleKey.F7 => TerminalKeyKind.F7,
            ConsoleKey.F8 => TerminalKeyKind.F8,
            ConsoleKey.F9 => TerminalKeyKind.F9,
            ConsoleKey.F10 => TerminalKeyKind.F10,
            ConsoleKey.F11 => TerminalKeyKind.F11,
            ConsoleKey.F12 => TerminalKeyKind.F12,
            _ => (TerminalKeyKind?)null
        };

        if (kind.HasValue)
        {
            return new TerminalKey(kind.Value, modifiers: modifiers);
        }

        return info.KeyChar == ' '
            ? null
            : new TerminalKey(TerminalKeyKind.Character, info.KeyChar.ToString(), modifiers);
    }

    private static class NativeMethods
    {
        public static readonly IntPtr StdInputHandle = new(-10);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(IntPtr nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }
}
