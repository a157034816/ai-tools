using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Mcp;

namespace AiTools.Extensions;

/// <summary>控制 <see cref="PluginHost"/> 行为的选项。</summary>
public sealed class PluginHostOptions
{
    /// <summary>获取或设置项目根目录（<c>${CLAUDE_PROJECT_DIR}</c>）。默认为进程当前目录。</summary>
    public string? ProjectDirectory { get; set; }

    /// <summary>获取或设置插件数据根目录（每个插件一个子目录，对应 <c>${CLAUDE_PLUGIN_DATA}</c>）。</summary>
    public string? PluginDataDirectory { get; set; }

    /// <summary>获取或设置是否允许执行命令正文中的 <c>!`command`</c> 动态上下文注入。默认为 true。</summary>
    public bool EnableShellExecution { get; set; } = true;

    /// <summary>获取或设置单条注入命令的超时。默认 60 秒。</summary>
    public TimeSpan ShellTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>获取或设置当短名重复时是否直接使用第一个匹配（false 时要求使用限定名）。默认 false。</summary>
    public bool AllowAmbiguousShortNames { get; set; }
}

/// <summary>一次斜杠命令调用的解析结果。</summary>
public sealed class SlashCommandInvocation
{
    /// <summary>获取被调用的命令。</summary>
    public PluginCommand Command { get; init; } = new();

    /// <summary>获取参数。</summary>
    public CommandArguments Arguments { get; init; } = CommandArguments.Parse(string.Empty);

    /// <summary>获取渲染后的正文（可被多条消息共享）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>获取渲染过程中执行的 shell 命令。</summary>
    public IReadOnlyList<string> ShellCommands { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 斜杠命令宿主：负责把插件定义的命令渲染为发送给模型的用户消息，
/// 并把插件的 skill 与 MCP 服务器接入聊天请求。
/// </summary>
/// <remarks>
/// 该类型不依赖任何 UI 框架，可在控制台、WPF 或服务端复用。
/// </remarks>
public sealed class PluginHost
{
    private readonly Dictionary<string, McpClient> _mcpClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mcpGate = new();
    private readonly PluginHostOptions _options;

    /// <summary>使用给定目录与选项创建宿主。</summary>
    /// <param name="catalog">已加载的插件。</param>
    /// <param name="options">宿主选项。</param>
    public PluginHost(PluginCatalog catalog, PluginHostOptions? options = null)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _options = options ?? new PluginHostOptions();
    }

    /// <summary>获取已加载的插件集合。</summary>
    public PluginCatalog Catalog { get; }

    /// <summary>获取宿主选项。</summary>
    public PluginHostOptions Options => _options;

    /// <summary>判断输入是否是斜杠命令（以 <c>/</c> 开头且不是纯路径）。</summary>
    public static bool LooksLikeCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text!.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/')
        {
            return false;
        }

        // "/goal" 是命令；"/usr/bin" 这类路径不是。
        var firstToken = trimmed.Substring(1).Split(new[] { ' ', '\t', '\r', '\n' }, 2)[0];
        return firstToken.Length > 0 && firstToken.IndexOf('/') < 0 && firstToken.IndexOf('\\') < 0;
    }

    /// <summary>把 "<c>/name args</c>" 拆分为命令名与参数。</summary>
    public static (string Name, string Arguments) SplitCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        var trimmed = text.Trim().TrimStart('/');
        var index = trimmed.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        if (index < 0)
        {
            return (trimmed, string.Empty);
        }

        return (trimmed.Substring(0, index), trimmed.Substring(index + 1).Trim());
    }

    /// <summary>尝试解析斜杠命令；未找到插件命令时返回 false（调用方回退到普通消息）。</summary>
    public bool TryResolve(string text, out PluginCommand? command, out CommandArguments arguments)
    {
        command = null;
        arguments = CommandArguments.Parse(string.Empty);
        if (!LooksLikeCommand(text))
        {
            return false;
        }

        var (name, raw) = SplitCommand(text);
        if (!Catalog.TryResolveCommand(name, out command, _options.AllowAmbiguousShortNames) || command == null)
        {
            return false;
        }

        arguments = CommandArguments.Parse(raw);
        return true;
    }

    /// <summary>渲染命令调用的正文。</summary>
    /// <param name="command">命令。</param>
    /// <param name="arguments">参数。</param>
    /// <param name="sessionId">会话标识，用于 <c>${CLAUDE_SESSION_ID}</c>。</param>
    /// <exception cref="CommandExecutionException">动态上下文注入命令失败。</exception>
    public SlashCommandInvocation Render(PluginCommand command, CommandArguments arguments, string? sessionId = null)
    {
        if (command == null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        arguments ??= CommandArguments.Parse(string.Empty);

        Func<string, string?>? executor = null;
        if (_options.EnableShellExecution && CommandTemplateRenderer.ContainsShellInjection(command.Body))
        {
            var workingDirectory = _options.ProjectDirectory ?? Environment.CurrentDirectory;
            var timeout = _options.ShellTimeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(2) : _options.ShellTimeout;
            executor = script => ShellCommandRunner.Run(script, workingDirectory, timeout);
        }

        var context = new CommandRenderContext
        {
            Arguments = arguments,
            ProjectDirectory = _options.ProjectDirectory ?? Environment.CurrentDirectory,
            SessionId = sessionId,
            PluginDataDirectory = GetPluginDataDirectory(command.PluginName),
            ShellExecutor = executor,
            DisableShellExecution = !_options.EnableShellExecution
        };

        var result = CommandTemplateRenderer.RenderResult(command.Body, command, context);
        var text = CommandTemplateRenderer.AppendArgumentsIfUnused(result.Text, arguments, result.ArgumentsConsumed);

        return new SlashCommandInvocation
        {
            Command = command,
            Arguments = arguments,
            Text = text,
            ShellCommands = result.ShellCommands
        };
    }

    /// <summary>获取某插件的持久化数据目录（必要时创建）。</summary>
    public string? GetPluginDataDirectory(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(_options.PluginDataDirectory) || string.IsNullOrWhiteSpace(pluginName))
        {
            return null;
        }

        var safe = new string(pluginName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray());
        var directory = Path.Combine(_options.PluginDataDirectory!, safe);
        try
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception)
        {
            return directory;
        }
    }

    /// <summary>构建可注入系统提示的插件清单，帮助模型理解有哪些插件命令可用。</summary>
    public string BuildSystemPrompt()
    {
        var commands = Catalog.Commands.Where(command => !command.DisableModelInvocation).ToArray();
        if (commands.Length == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("## Plugin commands");
        builder.AppendLine();
        builder.AppendLine("The user can run these slash commands; each expands to a detailed instruction prompt:");
        foreach (var command in commands)
        {
            builder.Append("- `").Append('/').Append(command.Name).Append('`');
            if (!string.IsNullOrWhiteSpace(command.ArgumentHint))
            {
                builder.Append(' ').Append(command.ArgumentHint);
            }

            builder.Append(": ").AppendLine(command.Description);
        }

        if (Catalog.Commands.Any(command => command.DisableModelInvocation))
        {
            builder.AppendLine();
            builder.AppendLine("Commands marked as user-invocable only must not be simulated by the model.");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>连接插件声明的 MCP 服务器并返回其工具定义。</summary>
    /// <param name="server">服务器声明。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<ToolDefinition>> ConnectMcpServerAsync(
        PluginMcpServer server,
        CancellationToken cancellationToken = default)
    {
        if (server == null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        McpClient client;
        lock (_mcpGate)
        {
            if (!_mcpClients.TryGetValue(server.QualifiedName, out client!))
            {
                client = CreateClient(server);
                _mcpClients[server.QualifiedName] = client;
            }
        }

        if (!client.IsInitialized)
        {
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        return McpToolBridge.ToToolDefinitions(tools);
    }

    /// <summary>获取已连接的 MCP 客户端。</summary>
    public McpClient? TryGetMcpClient(string qualifiedName)
    {
        lock (_mcpGate)
        {
            return _mcpClients.TryGetValue(qualifiedName, out var client) ? client : null;
        }
    }

    /// <summary>关闭所有已连接的 MCP 客户端。</summary>
    public async Task DisposeAsync()
    {
        McpClient[] clients;
        lock (_mcpGate)
        {
            clients = _mcpClients.Values.ToArray();
            _mcpClients.Clear();
        }

        foreach (var client in clients)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭失败不应影响其它服务器。
            }
        }
    }

    private static McpClient CreateClient(PluginMcpServer server)
    {
        switch (server.Transport)
        {
            case PluginMcpTransportKind.Http:
                return new McpClient(new StreamableHttpMcpTransport(new StreamableHttpTransportOptions
                {
                    Endpoint = server.Url,
                    DefaultHeaders = server.Headers.Count == 0 ? null : server.Headers
                }));

            case PluginMcpTransportKind.Sse:
                return new McpClient(new SseMcpTransport(new SseMcpTransportOptions
                {
                    SseEndpoint = server.Url,
                    DefaultHeaders = server.Headers.Count == 0 ? null : server.Headers
                }));

            default:
                return new McpClient(new StdioMcpTransport(new StdioTransportOptions
                {
                    Command = server.Command,
                    Arguments = server.Arguments,
                    EnvironmentVariables = server.Environment.Count == 0 ? null : server.Environment
                }));
        }
    }
}
