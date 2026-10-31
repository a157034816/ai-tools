using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Terminal;
using AiTools.Terminal.Agent;

namespace AiTools.Cli;

/// <summary>命令行参数解析结果。</summary>
internal sealed class CommandLine
{
    public bool ShowHelp { get; private set; }

    public bool ShowVersion { get; private set; }

    public bool ListModels { get; private set; }

    public bool ListSessions { get; private set; }

    public bool ListCommands { get; private set; }

    public bool PrintMode { get; private set; }

    public string? InitialPrompt { get; private set; }

    public string? ProviderId { get; private set; }

    public string? ModelId { get; private set; }

    public string? WorkingDirectory { get; private set; }

    public string? ResumeId { get; private set; }

    public bool ContinueLast { get; private set; }

    public bool NoPlugins { get; private set; }

    public bool NoExtensions { get; private set; }

    public bool NoTools { get; private set; }

    public bool PlainScreen { get; private set; }

    public bool ShowReasoning { get; private set; } = true;

    public string? SystemPrompt { get; private set; }

    public string? OutputStyle { get; private set; }

    public int? MaxToolRounds { get; private set; }

    public string? DumpPath { get; private set; }

    public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();

    public bool HasErrors => Errors.Count > 0;

    /// <summary>解析参数。未知的 <c>--</c> 选项记为错误；位置参数合并为提示词。</summary>
    /// <param name="args">命令行参数。</param>
    public static CommandLine Parse(string[] args)
    {
        var line = new CommandLine();
        var errors = new List<string>();
        var promptParts = new List<string>();
        var positionalOnly = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            string? NextValue(string name)
            {
                if (index + 1 >= args.Length)
                {
                    errors.Add($"参数 {name} 缺少值。");
                    return null;
                }

                return args[++index];
            }

            if (positionalOnly)
            {
                promptParts.Add(arg);
                continue;
            }

            switch (arg)
            {
                case "--":
                    positionalOnly = true;
                    break;
                case "-h":
                case "--help":
                    line.ShowHelp = true;
                    break;
                case "-v":
                case "--version":
                    line.ShowVersion = true;
                    break;
                case "--list-models":
                    line.ListModels = true;
                    break;
                case "--list-sessions":
                    line.ListSessions = true;
                    break;
                case "--list-commands":
                    line.ListCommands = true;
                    break;
                case "-p":
                case "--print":
                    line.PrintMode = true;
                    break;
                case "--provider":
                case "--provider-id":
                    line.ProviderId = NextValue(arg) ?? line.ProviderId;
                    break;
                case "--model":
                case "-m":
                    line.ModelId = NextValue(arg) ?? line.ModelId;
                    break;
                case "-C":
                case "--cwd":
                    line.WorkingDirectory = NextValue(arg) ?? line.WorkingDirectory;
                    break;
                case "-r":
                case "--resume":
                    line.ResumeId = NextValue(arg) ?? line.ResumeId;
                    break;
                case "-c":
                case "--continue":
                    line.ContinueLast = true;
                    break;
                case "--no-plugins":
                    line.NoPlugins = true;
                    break;
                case "--no-extensions":
                    line.NoExtensions = true;
                    break;
                case "--no-tools":
                    line.NoTools = true;
                    break;
                case "--no-reasoning":
                    line.ShowReasoning = false;
                    break;
                case "--plain":
                    line.PlainScreen = true;
                    break;
                case "--system-prompt":
                    line.SystemPrompt = NextValue(arg) ?? line.SystemPrompt;
                    break;
                case "--output-style":
                    line.OutputStyle = NextValue(arg) ?? line.OutputStyle;
                    break;
                case "--max-tool-rounds":
                    var rounds = NextValue(arg);
                    if (int.TryParse(rounds, out var value))
                    {
                        line.MaxToolRounds = value;
                    }
                    else
                    {
                        errors.Add("--max-tool-rounds 需要一个整数。");
                    }

                    break;
                case "--dump-settings":
                    line.DumpPath = NextValue(arg) ?? line.DumpPath;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1)
                    {
                        // 未知选项按错误处理，避免静默忽略导致误解。
                        errors.Add($"未知参数：{arg}");
                        break;
                    }

                    promptParts.Add(arg);
                    break;
            }
        }

        if (promptParts.Count > 0)
        {
            line.InitialPrompt = string.Join(" ", promptParts);
        }

        line.Errors = errors;
        return line;
    }
}

/// <summary>CLI 入口：解析参数，选择全屏 TUI、行式 REPL 或一次性输出。</summary>
internal static class Program
{
    private const string Version = "0.1.0";

    private const string DefaultSystemPrompt =
        "你是一个运行在终端里的编码助手。回答保持简洁、可执行，优先给出改动方案与命令；" +
        "需要读取或修改文件时使用可用工具，不要凭空猜测文件内容。";

    private static async Task<int> Main(string[] args)
    {
        // 统一用 UTF-8 输出：Windows 控制台默认代码页会让中文在管道与 CI 中变成乱码。
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception)
        {
            // 部分宿主不允许修改编码；继续运行。
        }

        var commandLine = CommandLine.Parse(args);

        if (commandLine.ShowHelp)
        {
            Console.Out.Write(HelpText);
            return 0;
        }

        if (commandLine.ShowVersion)
        {
            Console.Out.WriteLine("aitools " + Version);
            return 0;
        }

        if (commandLine.HasErrors)
        {
            foreach (var error in commandLine.Errors)
            {
                Console.Error.WriteLine("错误：" + error);
            }

            Console.Error.WriteLine("运行 aitools --help 查看用法。");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(commandLine.WorkingDirectory))
        {
            try
            {
                Environment.CurrentDirectory = Path.GetFullPath(commandLine.WorkingDirectory!);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("无法切换工作目录：" + ex.Message);
                return 2;
            }
        }

        if (commandLine.DumpPath != null)
        {
            return DumpSettings(commandLine.DumpPath);
        }

        var options = new TerminalOptions
        {
            ProviderId = commandLine.ProviderId,
            ModelId = commandLine.ModelId,
            Version = Version,
            DefaultSystemPrompt = commandLine.SystemPrompt ?? DefaultSystemPrompt,
            EnableTools = !commandLine.NoTools,
            EnableExtensions = !commandLine.NoExtensions,
            EnableExtensionPackages = !commandLine.NoExtensions,
            AutoDiscoverPlugins = !commandLine.NoPlugins,
            ShowReasoning = commandLine.ShowReasoning,
            OutputStyle = commandLine.OutputStyle,
            UseAlternateScreen = !commandLine.PlainScreen && !commandLine.PrintMode,
            MaxToolRounds = commandLine.MaxToolRounds ?? 24
        };

        var fullScreenEntered = false;
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            // 首次 Ctrl+C 交给会话取消；连按第二次直接终止进程。
            if (shutdown.IsCancellationRequested)
            {
                return;
            }

            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += handler;

        try
        {
            await using var session = new AgentSession(options);
            await session.InitializeAsync(shutdown.Token).ConfigureAwait(false);

            if (commandLine.ListModels)
            {
                return ListModels(session);
            }

            if (commandLine.ListSessions)
            {
                return await ListSessionsAsync(session, shutdown.Token).ConfigureAwait(false);
            }

            if (commandLine.ListCommands)
            {
                return ListCommands(session);
            }

            if (commandLine.ResumeId != null)
            {
                await session.ResumeAsync(commandLine.ResumeId, shutdown.Token).ConfigureAwait(false);
            }
            else if (commandLine.ContinueLast)
            {
                var summaries = await session.ListConversationsAsync(shutdown.Token).ConfigureAwait(false);
                var latest = summaries.FirstOrDefault();
                if (latest != null)
                {
                    await session.ResumeAsync(latest.Id, shutdown.Token).ConfigureAwait(false);
                }
            }

            if (commandLine.PrintMode)
            {
                return await RunOnceAsync(session, commandLine.InitialPrompt, shutdown.Token).ConfigureAwait(false);
            }

            if (!CanUseFullScreen())
            {
                var repl = new LineRepl(session);
                await repl.RunAsync(shutdown.Token).ConfigureAwait(false);
                return 0;
            }

            using var host = new ConsoleTerminalHost(options.UseAlternateScreen);
            fullScreenEntered = true;
            host.Start();
            await using var app = new TerminalApp(session, host)
            {
                InitialInput = commandLine.InitialPrompt
            };

            await app.RunAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (AiToolsConfigurationException ex)
        {
            Console.Error.WriteLine("配置错误：" + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("运行失败：" + ex);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            if (fullScreenEntered)
            {
                TerminalCapabilities.ResetModes();
            }
        }
    }

    private static bool CanUseFullScreen()
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return TerminalCapabilities.SupportsAnsi;
    }

    private static int ListModels(AgentSession session)
    {
        var models = session.AvailableModels;
        if (models.Count == 0)
        {
            Console.Out.WriteLine("没有可用的模型配置。");
            foreach (var line in session.DescribeMissingConfiguration())
            {
                Console.Out.WriteLine("  " + line);
            }

            return 1;
        }

        foreach (var model in models)
        {
            var current = string.Equals(model.Provider.Id, session.CurrentProvider?.Id, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(model.Model.Id, session.CurrentModel?.Id, StringComparison.OrdinalIgnoreCase);
            Console.Out.WriteLine((current ? "* " : "  ") + model.Provider.Id + "  " + model.Model.Id
                                  + (string.IsNullOrWhiteSpace(model.Model.DisplayName) ? string.Empty : "  (" + model.Model.DisplayName + ")"));
        }

        return 0;
    }

    private static async Task<int> ListSessionsAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var summaries = await session.ListConversationsAsync(cancellationToken).ConfigureAwait(false);
        if (summaries.Count == 0)
        {
            Console.Out.WriteLine("还没有已保存的会话。");
            return 0;
        }

        foreach (var summary in summaries)
        {
            Console.Out.WriteLine(summary.Id + "  " + summary.GetRelativeTime() + "  " + summary.MessageCount + " 条  " + summary.Title);
        }

        return 0;
    }

    private static int ListCommands(AgentSession session)
    {
        var commands = session.Commands;
        if (commands == null || commands.Count == 0)
        {
            Console.Out.WriteLine("没有扩展、插件或技能命令（内置命令见 --help）。");
        }
        else
        {
            foreach (var command in commands.Commands.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                var hint = string.IsNullOrWhiteSpace(command.ArgumentHint) ? string.Empty : " " + command.ArgumentHint;
                Console.Out.WriteLine("/" + command.InvocationName + hint + "  —  " + command.Description + "  [" + command.Source + "]");
            }
        }

        if (session.SkillProvider != null)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("技能：" + session.SkillProvider.SkillCount + " 个");
            foreach (var skill in session.SkillProvider.Toolset.Skills)
            {
                Console.Out.WriteLine("  " + skill.Name + " — " + skill.Description);
            }
        }

        var errors = session.Diagnostics.Where(item => item.Kind == AiTools.Extensions.PluginDiagnosticKind.Error).ToArray();
        if (errors.Length > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("诊断：");
            foreach (var error in errors)
            {
                Console.Error.WriteLine("  " + error);
            }
        }

        return 0;
    }

    private static int DumpSettings(string path)
    {
        var store = new JsonFileSettingsStore(AiTools.Core.AiToolsPaths.GetSettingsFile(), new DpapiSecretProtector());
        var settings = store.LoadAsync().GetAwaiter().GetResult();
        var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(path, json, new UTF8Encoding(false));
        Console.Out.WriteLine("已写出设置（API Key 为明文，请勿提交）：" + Path.GetFullPath(path));
        return 0;
    }

    private static async Task<int> RunOnceAsync(AgentSession session, string? prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("--print 需要一段提示词，例如：aitools -p \"总结这个仓库\"");
            return 2;
        }

        var result = await session.SubmitAsync(prompt!, cancellationToken).ConfigureAwait(false);
        if (result == null)
        {
            return 0;
        }

        if (!string.IsNullOrEmpty(result.Text))
        {
            Console.Out.WriteLine(result.Text);
        }

        if (result.Error != null)
        {
            Console.Error.WriteLine("错误：" + result.Error);
            return 1;
        }

        return result.Cancelled ? 130 : 0;
    }

    private const string HelpText = """
aitools — AiTools 官方终端 Agent

用法:
  aitools                      启动全屏交互界面
  aitools "提示词"             带初始提示词进入交互界面
  aitools -p "提示词"          一次性输出回答到 stdout（脚本友好）
  aitools --list-models        列出已配置的模型
  aitools --list-sessions      列出已保存的会话
  aitools --list-commands      列出扩展/插件/技能命令
  aitools -c, --continue       继续最近一次会话
  aitools -r <id>, --resume    恢复指定会话

选项:
  -m, --model <id>             使用指定模型
      --provider <id>          使用指定服务商
  -C, --cwd <目录>             设置工作目录
      --no-plugins             不加载 Claude Code 插件
      --no-extensions          不启用扩展与扩展包
      --no-tools               不向模型发送工具
      --no-reasoning           不显示推理过程
      --plain                  不使用备用屏幕（适合 IDE 内嵌终端）
      --output-style <名称>    初始输出风格
      --max-tool-rounds <n>    单轮最大工具调用轮数（默认 24）
      --dump-settings <路径>   把当前设置（明文 Key）导出为 JSON，便于迁移
  -h, --help                   显示本帮助
  -v, --version                显示版本

环境变量:
  OPENAI_API_KEY / OPENAI_BASE_URL / OPENAI_CHAT_MODEL
  ANTHROPIC_API_KEY / ANTHROPIC_CHAT_MODEL
  GEMINI_API_KEY / GEMINI_CHAT_MODEL
  AITOOLS_HOME                 覆盖 ~/.ai-tools 数据根目录

配置（与 WPF 组件共用同一份）:
  ~/.ai-tools/settings.json    服务商与模型
  ~/.ai-tools/conversations/   会话存储
  ~/.ai-tools/plugins/         Claude Code 插件
  ~/.ai-tools/extensions/      扩展包
  ~/.agents/ 与 <project>/.agents/  共享技能与 AGENTS.md

全屏界面快捷键: ? 查看帮助，/ 查看命令，Esc 停止生成，Ctrl+C 退出。

扩展与非交互模式:
  AITOOLS_EXTENSIONS=<目录>     加载磁盘扩展包（冒号/分号分隔多个根目录）
  aitools --list-commands       查看扩展、插件与技能提供的命令
  aitools -p "/goal 完成迁移"   一次性模式下也会执行扩展命令
""";
}
