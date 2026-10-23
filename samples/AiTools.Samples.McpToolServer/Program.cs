using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AiTools.Samples.McpToolServer;

/// <summary>
/// 进程入口点。非交互模式（<c>--stdio</c>、<c>--list-tools</c>、<c>--headless</c>）在运行时不会创建
/// WPF <see cref="Application"/> 或任何窗口；只有默认的交互模式才会启动桌面 UI。
/// </summary>
internal static class Program
{
    /// <summary>运行示例服务器。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出代码。</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        var nonInteractive = args.Any(argument =>
            string.Equals(argument, "--stdio", StringComparison.Ordinal)
            || string.Equals(argument, "--headless", StringComparison.Ordinal)
            || string.Equals(argument, "--list-tools", StringComparison.Ordinal));

        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine("参数错误：" + ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLineOptions.GetUsage());

            if (!nonInteractive && Environment.UserInteractive)
            {
                try
                {
                    MessageBox.Show(
                        "参数错误：" + ex.Message + Environment.NewLine + Environment.NewLine + CommandLineOptions.GetUsage(),
                        "MCP 工具服务",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch (Exception)
                {
                    // 消息框属于尽力而为的提示；退出代码仍会报告失败。
                }
            }

            return 2;
        }

        if (options.ShowHelp)
        {
            WriteStandardOutput(CommandLineOptions.GetUsage());
            return 0;
        }

        if (options.ListTools)
        {
            WriteStandardOutput(ToolCatalog.BuildToolsJson(indented: true));
            return 0;
        }

        if (options.Stdio)
        {
            return RunStdioAsync(options).GetAwaiter().GetResult();
        }

        if (options.Headless)
        {
            return RunHeadlessHttpAsync(options).GetAwaiter().GetResult();
        }

        // 交互模式：创建 WPF 应用与主窗口。
        var app = new App { Options = options };
        app.InitializeComponent();
        return app.Run();
    }

    private static async Task<int> RunStdioAsync(CommandLineOptions options)
    {
        // stdio 模式下 stdout 只能包含 JSON-RPC 消息，所有日志写入 stderr。
        // WinExe 在没有控制台（被父进程重定向）时默认使用系统 ANSI 代码页，这里强制 UTF-8。
        var errorWriter = CreateUtf8Writer(Console.OpenStandardError());
        var log = new ConsoleLog(errorWriter, "stdio");
        var protocol = new McpProtocol(
            options.Name,
            new ResourceCatalog(options.Name, "stdio", () => null));

        log.Info("以 stdio 模式启动（" + options.Name + " · v" + ServerMetadata.Version + "），共 " +
                 ToolCatalog.Tools.Count + " 个工具。");

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        Console.CancelKeyPress += onCancel;
        try
        {
            var server = new StdioMcpServer(
                protocol,
                log,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput());

            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error("stdio 服务异常退出：" + ex.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        log.Info("stdin 已关闭，stdio 服务退出。");
        return 0;
    }

    private static async Task<int> RunHeadlessHttpAsync(CommandLineOptions options)
    {
        // 控制台日志显式使用 UTF-8，避免 WinExe 被重定向时按系统 ANSI 代码页输出。
        var log = new ConsoleLog(CreateUtf8Writer(Console.OpenStandardOutput()), "HTTP");
        log.Info("正在绑定 http://" + options.Host + ":" + options.Port + ServerMetadata.McpPath + " ……");

        if (!HttpMcpServer.TryCreate(options.Host, options.Port, options.Name, log, out var server, out var error)
            || server == null)
        {
            log.Error("无法启动 HTTP 监听：" + (error ?? "未知错误"));
            log.Info("提示：--port 0 可由系统自动选择空闲端口；本机回环地址通常无需管理员权限。");
            return 1;
        }

        await using (server)
        {
            using var registration = RegistryRegistration.Register(options.Name, server.Endpoint, log);

            if (options.Port != 0 && server.Port != options.Port)
            {
                log.Warn("端口 " + options.Port + " 已被占用，已改用 " + server.Port + "。");
            }

            log.Info("服务已就绪：" + server.Endpoint);
            log.Info("服务器名称：" + options.Name + " · 版本：" + ServerMetadata.Version +
                     " · 工具数：" + ToolCatalog.Tools.Count + " · PID：" + Environment.ProcessId);
            log.Info("工具：" + string.Join("、", ToolCatalog.ToolNames));
            log.Info("无窗口模式运行中，按 Ctrl+C 停止服务并清理注册表条目。");

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                log.Info("收到 Ctrl+C，正在停止……");
                shutdown.Cancel();
            };

            EventHandler onProcessExit = (_, _) => shutdown.Cancel();
            Console.CancelKeyPress += onCancel;
            AppDomain.CurrentDomain.ProcessExit += onProcessExit;

            try
            {
                await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                // 正常停止。
            }
            catch (Exception ex)
            {
                log.Error("HTTP 服务异常退出：" + ex.Message);
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
            }

            log.Info("HTTP 监听已停止，正在清理注册表。");
        }

        return 0;
    }

    private static void WriteStandardOutput(string text)
    {
        try
        {
            using var writer = CreateUtf8Writer(Console.OpenStandardOutput());
            writer.WriteLine(text);
            writer.Flush();
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine("写入标准输出失败：" + ex.Message);
            }
            catch (Exception)
            {
                // 没有连接控制台，并且标准错误同样不可用。
            }
        }
    }

    /// <summary>在标准流上创建不带 BOM 的 UTF-8 写入器。</summary>
    private static StreamWriter CreateUtf8Writer(Stream stream) =>
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
}
