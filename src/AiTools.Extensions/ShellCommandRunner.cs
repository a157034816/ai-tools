using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Extensions;

/// <summary>默认的 shell 命令执行器，用于命令正文中的 <c>!`command`</c> 动态上下文注入。</summary>
/// <remarks>
/// 命令先写入临时脚本文件再交给 shell 执行，避免命令行引号/转义在不同平台上产生歧义；
/// 临时脚本在命令结束后删除。stdout 与 stderr 合并返回。
/// </remarks>
public static class ShellCommandRunner
{
    /// <summary>执行一条 shell 命令并返回合并后的 stdout/stderr。</summary>
    /// <param name="command">要执行的命令。</param>
    /// <param name="workingDirectory">工作目录；为空或不存在时使用进程当前目录。</param>
    /// <param name="timeout">超时；超时后终止进程并抛出。</param>
    /// <exception cref="CommandExecutionException">命令以非零退出码结束、启动失败或超时。</exception>
    public static string Run(string command, string workingDirectory, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var isWindows = Path.DirectorySeparatorChar == '\\';
        var scriptPath = Path.Combine(Path.GetTempPath(), "aitools-cmd-" + Guid.NewGuid().ToString("N") + (isWindows ? ".cmd" : ".sh"));

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory
        };

        try
        {
            // cmd.exe 默认回显命令与提示符；关闭后只留下命令输出。
            var script = isWindows ? "@echo off\r\n" + command : command;
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            throw new CommandExecutionException($"Failed to prepare the shell command \"{command}\": {ex.Message}", ex);
        }

        if (isWindows)
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/d /s /c \"" + scriptPath + "\"";
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.Arguments = "\"" + scriptPath + "\"";
        }

        try
        {
            return Execute(command, startInfo, timeout);
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (Exception)
            {
                // 临时文件删除失败不影响结果。
            }
        }
    }

    private static string Execute(string command, ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();
        using var outputDone = new ManualResetEventSlim(false);
        using var errorDone = new ManualResetEventSlim(false);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                outputDone.Set();
            }
            else
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                errorDone.Set();
            }
            else
            {
                lock (error)
                {
                    error.AppendLine(e.Data);
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new CommandExecutionException($"Failed to start the shell command \"{command}\": {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var completed = process.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds));
        if (!completed)
        {
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // 进程可能已经退出。
            }

            throw new CommandExecutionException($"Shell command timed out after {timeout.TotalSeconds:F0}s: {command}");
        }

        // 等待异步读取结束，避免丢失尾部输出。
        outputDone.Wait(TimeSpan.FromSeconds(2));
        errorDone.Wait(TimeSpan.FromSeconds(2));

        string text;
        lock (output)
        {
            lock (error)
            {
                text = error.Length == 0 ? output.ToString() : output.Append(error).ToString();
            }
        }

        if (process.ExitCode != 0)
        {
            var detail = text.Trim();
            throw new CommandExecutionException(
                $"Shell command failed for pattern \"{command}\" (exit code {process.ExitCode})." +
                (detail.Length == 0 ? string.Empty : "\n" + detail));
        }

        return text;
    }
}
