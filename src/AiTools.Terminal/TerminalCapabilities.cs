using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Terminal;

/// <summary>终端运行时能力探测。</summary>
public static class TerminalCapabilities
{
    private static bool? _ansiSupported;

    /// <summary>
    /// 获取当前终端是否支持 ANSI 转义序列。Windows 上通过启用虚拟终端处理实现；
    /// 重定向输出或无法启用时返回 false。
    /// </summary>
    public static bool SupportsAnsi => _ansiSupported ??= DetectAnsi();

    /// <summary>获取当前终端是否支持 256 色/真彩色（大多数现代终端都支持）。</summary>
    public static bool SupportsTrueColor
    {
        get
        {
            var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
            return !string.IsNullOrEmpty(colorTerm);
        }
    }

    /// <summary>获取是否运行在 Windows 上。</summary>
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>把终端重置到常规状态（颜色、光标、括号粘贴与鼠标模式）。</summary>
    public static void ResetModes(TextWriter? writer = null)
    {
        try
        {
            var output = writer ?? Console.Out;
            output.Write("\u001b[0m\u001b[?25h\u001b[?2004l\u001b[?1000l\u001b[?1006l");
            output.Flush();
        }
        catch (Exception)
        {
            // 终端已关闭时忽略。
        }
    }

    /// <summary>获取终端窗口尺寸；失败时回退到 80x24。</summary>
    public static (int Width, int Height) GetSize()
    {
        try
        {
            var width = Console.WindowWidth;
            var height = Console.WindowHeight;
            if (width > 0 && height > 0)
            {
                return (width, height);
            }
        }
        catch (Exception)
        {
            // 输出被重定向时 WindowWidth 会抛异常。
        }

        return (80, 24);
    }

    private static bool DetectAnsi()
    {
        if (!IsWindows)
        {
            return true;
        }

        // 重定向输出意味着我们不在真实终端里，输出转义码只会污染日志。
        try
        {
            if (Console.IsOutputRedirected)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            var handle = NativeMethods.GetStdHandle(NativeMethods.StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return false;
            }

            if (!NativeMethods.GetConsoleMode(handle, out var mode))
            {
                return false;
            }

            if ((mode & NativeMethods.EnableVirtualTerminalProcessing) != 0)
            {
                return true;
            }

            return NativeMethods.SetConsoleMode(handle, mode | NativeMethods.EnableVirtualTerminalProcessing);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static class NativeMethods
    {
        public static readonly IntPtr StdOutputHandle = new(-11);

        public const uint EnableVirtualTerminalProcessing = 0x0004;

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

/// <summary>
/// 全屏终端会话：接管标准输入/输出，提供按键事件与整帧绘制。
/// </summary>
/// <remarks>
/// 进入时切换到备用屏幕、隐藏光标、启用括号粘贴；退出（包括异常与 Ctrl+C）时恢复原状。
/// 输入循环与绘制都在调用方的线程上串行执行，因此宿主不需要锁。
/// </remarks>
public interface ITerminalHost : IDisposable
{
    /// <summary>获取当前终端宽度（列）。</summary>
    int Width { get; }

    /// <summary>获取当前终端高度（行）。</summary>
    int Height { get; }

    /// <summary>获取当前终端是否支持 ANSI（否则宿主应退化为行式输出）。</summary>
    bool SupportsAnsi { get; }

    /// <summary>把绘制好的虚拟屏幕推送到终端。</summary>
    /// <param name="screen">当前帧。</param>
    /// <param name="force">是否强制整屏重绘。</param>
    void Present(Rendering.ScreenBuffer screen, bool force = false);

    /// <summary>读取下一个按键；会话结束时返回 false。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask<Input.TerminalKey?> ReadKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>获取当前待处理的终端尺寸变化。</summary>
    (int Width, int Height)? TryConsumeResize();

    /// <summary>请求重绘（用于尺寸变化或外部状态更新）。</summary>
    void RequestRedraw();
}
