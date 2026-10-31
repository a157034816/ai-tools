using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AiTools.Terminal;

/// <summary>把文本写入系统剪贴板（尽力而为，失败时抛异常由调用方提示）。</summary>
public static class ClipboardWriter
{
    /// <summary>写入剪贴板。</summary>
    /// <param name="text">要复制的文本。</param>
    /// <exception cref="InvalidOperationException">当前平台没有可用的剪贴板命令。</exception>
    public static void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RunWithInput("clip.exe", Array.Empty<string>(), text);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            RunWithInput("pbcopy", Array.Empty<string>(), text);
            return;
        }

        if (TryRun("wl-copy", Array.Empty<string>(), text) || TryRun("xclip", new[] { "-selection", "clipboard" }, text))
        {
            return;
        }

        throw new InvalidOperationException("未找到可用的剪贴板命令（wl-copy 或 xclip）。");
    }

    private static bool TryRun(string command, string[] arguments, string text)
    {
        try
        {
            RunWithInput(command, arguments, text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RunWithInput(string command, string[] arguments, string text)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动剪贴板命令：" + command);
        process.StandardInput.Write(text);
        process.StandardInput.Close();

        if (!process.WaitForExit(3000))
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
            }

            throw new TimeoutException("剪贴板命令超时：" + command);
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("剪贴板命令返回 " + process.ExitCode + "：" + command);
        }
    }
}
