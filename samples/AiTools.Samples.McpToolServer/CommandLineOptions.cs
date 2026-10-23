using System;
using System.Collections.Generic;
using System.Globalization;

namespace AiTools.Samples.McpToolServer;

/// <summary>命令行参数无效时引发。</summary>
internal sealed class CommandLineException : Exception
{
    /// <summary>使用面向用户的消息创建参数异常。</summary>
    /// <param name="message">描述问题的消息。</param>
    internal CommandLineException(string message)
        : base(message)
    {
    }
}

/// <summary>示例服务器的已解析命令行选项。</summary>
public sealed class CommandLineOptions
{
    /// <summary>监听端口；0 表示由操作系统选择空闲端口。默认为 5220。</summary>
    internal int Port { get; private set; } = 5220;

    /// <summary>要绑定的网络接口。默认为回环地址。</summary>
    internal string Host { get; private set; } = "127.0.0.1";

    /// <summary>向 MCP 客户端报告并写入注册表的服务器名称。</summary>
    internal string Name { get; private set; } = ServerMetadata.DefaultName;

    /// <summary>获取服务器是否应通过标准输入/输出使用按行分隔的 JSON-RPC。</summary>
    internal bool Stdio { get; private set; }

    /// <summary>获取 HTTP 服务器是否应在无窗口模式下运行（不创建 WPF 应用程序）。</summary>
    internal bool Headless { get; private set; }

    /// <summary>获取是否以 JSON 输出工具目录后退出。</summary>
    internal bool ListTools { get; private set; }

    /// <summary>获取是否请求了帮助。</summary>
    internal bool ShowHelp { get; private set; }

    /// <summary>解析命令行参数。</summary>
    /// <param name="args">不含可执行文件名的参数。</param>
    /// <returns>解析后的选项。</returns>
    /// <exception cref="CommandLineException">参数未知或取值无效。</exception>
    internal static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--port=", StringComparison.Ordinal))
            {
                options.Port = ParsePort(argument.Substring("--port=".Length));
                continue;
            }

            if (argument.StartsWith("--host=", StringComparison.Ordinal))
            {
                options.Host = ValidateHost(argument.Substring("--host=".Length));
                continue;
            }

            if (argument.StartsWith("--name=", StringComparison.Ordinal))
            {
                options.Name = ValidateName(argument.Substring("--name=".Length));
                continue;
            }

            switch (argument)
            {
                case "--port":
                    options.Port = ParsePort(RequireValue(args, ref index, "--port"));
                    break;

                case "--host":
                    options.Host = ValidateHost(RequireValue(args, ref index, "--host"));
                    break;

                case "--name":
                    options.Name = ValidateName(RequireValue(args, ref index, "--name"));
                    break;

                case "--stdio":
                    options.Stdio = true;
                    break;

                case "--headless":
                    options.Headless = true;
                    break;

                case "--list-tools":
                    options.ListTools = true;
                    break;

                case "-h":
                case "--help":
                case "-?":
                    options.ShowHelp = true;
                    break;

                default:
                    throw new CommandLineException("未知参数: " + argument);
            }
        }

        return options;
    }

    /// <summary>获取由 <c>--help</c> 以及参数错误后显示的用法文本。</summary>
    /// <returns>简体中文的多行用法文本。</returns>
    internal static string GetUsage() =>
        """
        AiTools 示例 MCP 工具服务（WPF 桌面版）

        用法:
          AiTools.Samples.McpToolServer [选项]
          dotnet AiTools.Samples.McpToolServer.dll [选项]

        不带参数时启动 WPF 界面，并自动启动 HTTP 服务、写入本地注册表。

        选项:
          --port <n>       监听端口，默认 5220；0 表示由系统选择空闲端口。
                           端口被占用时自动尝试后续 20 个端口，并输出最终端口。
          --host <host>    监听地址，默认 127.0.0.1（仅本机）。仅在确需外部访问时修改。
          --name <name>    服务器名称，默认 “AiTools 示例工具服务”。
          --headless       无窗口运行 HTTP 服务，阻塞到进程结束；适合自动化测试。
          --stdio          使用标准输入/输出运行（每行一个 JSON-RPC 消息），不创建窗口，
                           不注册 HTTP 端点，日志写入 stderr。
          --list-tools     以 JSON 数组打印全部工具的 MCP 描述后退出。
          -h, --help       显示本帮助。

        示例:
          dotnet run --project samples/AiTools.Samples.McpToolServer
          dotnet run --project samples/AiTools.Samples.McpToolServer -- --port 0 --headless
          dotnet run --project samples/AiTools.Samples.McpToolServer -- --list-tools
          dotnet run --project samples/AiTools.Samples.McpToolServer -- --stdio

        说明:
          GET /mcp 返回 405，因为本示例不实现服务器主动推送的 SSE 流；
          DELETE /mcp 结束会话并返回 204。stdio 模式不写入本地注册表。
        """;

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new CommandLineException("参数 " + option + " 缺少取值。");
        }

        var value = args[index + 1];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandLineException("参数 " + option + " 缺少取值。");
        }

        index++;
        return value.Trim();
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port < 0
            || port > 65535)
        {
            throw new CommandLineException("端口必须是 0 到 65535 之间的整数。");
        }

        return port;
    }

    private static string ValidateHost(string value)
    {
        var host = value.Trim();
        if (host.Length == 0)
        {
            throw new CommandLineException("--host 不能为空。");
        }

        if (host.Contains('/') || host.Contains("://", StringComparison.Ordinal) || host.Any(char.IsWhiteSpace))
        {
            throw new CommandLineException("--host 只填写主机名或 IP 地址，例如 127.0.0.1 或 localhost。");
        }

        return host;
    }

    private static string ValidateName(string value)
    {
        var name = value.Trim();
        if (name.Length == 0)
        {
            throw new CommandLineException("--name 不能为空。");
        }

        return name;
    }
}
