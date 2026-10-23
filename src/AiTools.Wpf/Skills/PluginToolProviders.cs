using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Extensions;
using AiTools.Skills;
using AiTools.Wpf.Configuration;

namespace AiTools.Wpf.Skills;

/// <summary>
/// 把已加载插件的 skill 聚合为一个 <see cref="IChatToolProvider"/>，
/// 使模型可以通过 <c>load_skill</c> 按需加载插件技能（渐进式披露）。
/// </summary>
public sealed class PluginSkillChatToolProvider : IChatToolProvider
{
    private readonly SkillToolset _toolset;
    private readonly Func<ToolCall, CancellationToken, Task<string>> _handler;

    /// <summary>从插件目录创建 provider。</summary>
    /// <param name="catalog">已加载的插件。</param>
    /// <param name="options">skill 工具选项。</param>
    public PluginSkillChatToolProvider(ExtensionCatalog catalog, SkillToolOptions? options = null)
    {
        if (catalog == null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        _toolset = new SkillToolset(catalog.ToSkillCatalog(), options);
        _handler = _toolset.CreateToolHandler();
    }

    /// <summary>获取底层工具集。</summary>
    public SkillToolset Toolset => _toolset;

    /// <summary>获取插件提供的 skill 数量。</summary>
    public int SkillCount => _toolset.Skills.Count;

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetTools() => _toolset.GetToolDefinitions();

    /// <inheritdoc />
    public Task<string> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
        => _handler(call, cancellationToken);
}

/// <summary>
/// 把插件声明的 MCP 服务器接入对话页：首次 <see cref="ConnectAsync"/> 时连接所有服务器，
/// 之后 <see cref="GetTools"/> 返回已发现的工具。
/// </summary>
/// <remarks>
/// 工具名统一加 <c>plugin_server_</c> 前缀以避免跨插件冲突；调用时剥回原名。
/// 服务器不可用时不会抛异常，错误通过 <see cref="LastErrors"/> 暴露。
/// </remarks>
public sealed class PluginMcpChatToolProvider : IChatToolProvider
{
    private readonly ExtensionHost _host;
    private readonly Dictionary<string, ServerTools> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly List<string> _errors = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private bool _connected;

    /// <summary>创建 provider。</summary>
    public PluginMcpChatToolProvider(ExtensionHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>获取连接过程中遇到的问题（服务器名 + 原因）。</summary>
    public IReadOnlyList<string> LastErrors
    {
        get
        {
            lock (_gate)
            {
                return _errors.ToArray();
            }
        }
    }

    /// <summary>获取是否已完成一次连接尝试。</summary>
    public bool IsConnected => _connected;

    /// <summary>连接所有插件声明的 MCP 服务器并发现工具。可安全地重复调用。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected)
            {
                return;
            }

            foreach (var server in _host.Catalog.McpServers)
            {
                try
                {
                    var client = await ConnectServerAsync(server, cancellationToken).ConfigureAwait(false);
                    var tools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _servers[server.QualifiedName] = new ServerTools(server, client, tools);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _errors.Add($"{server.QualifiedName}: {ex.Message}");
                    }
                }
            }

            _connected = true;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetTools()
    {
        ServerTools[] servers;
        lock (_gate)
        {
            servers = _servers.Values.ToArray();
        }

        var definitions = new List<ToolDefinition>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in servers)
        {
            foreach (var tool in entry.Tools)
            {
                var name = MakeToolName(entry.Server, tool.Name);
                while (!used.Add(name))
                {
                    name += "_2";
                }

                definitions.Add(ToolDefinition.Create(name, tool.Description, tool.InputSchema));
            }
        }

        return definitions;
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
    {
        if (call == null)
        {
            throw new ArgumentNullException(nameof(call));
        }

        ServerTools? target = null;
        string? originalName = null;
        lock (_gate)
        {
            foreach (var entry in _servers.Values)
            {
                foreach (var tool in entry.Tools)
                {
                    if (string.Equals(MakeToolName(entry.Server, tool.Name), call.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        target = entry;
                        originalName = tool.Name;
                        break;
                    }
                }

                if (target != null)
                {
                    break;
                }
            }
        }

        if (target == null || originalName == null)
        {
            return "{\"error\":\"Unknown MCP tool: " + call.Name + "\"}";
        }

        System.Text.Json.JsonElement? arguments = null;
        var json = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                arguments = document.RootElement.Clone();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return "{\"error\":\"Invalid JSON arguments for " + call.Name + "\"}";
        }

        var result = await target.Client.CallToolAsync(originalName, arguments, cancellationToken).ConfigureAwait(false);
        var text = result.GetText();
        if (text.Length == 0 && result.StructuredContent.HasValue)
        {
            text = result.StructuredContent.Value.GetRawText();
        }

        if (text.Length == 0 && result.IsError)
        {
            text = "The MCP tool reported an error without a message.";
        }

        return text;
    }

    private static string MakeToolName(PluginMcpServer server, string toolName)
    {
        var prefix = server.Name.Replace('-', '_');
        var sanitized = new string(toolName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return "plugin_" + prefix + "_" + sanitized;
    }

    private async Task<AiTools.Mcp.McpClient> ConnectServerAsync(PluginMcpServer server, CancellationToken cancellationToken)
    {
        // ExtensionHost 已经缓存了每个服务器的客户端与握手状态。
        await _host.ConnectMcpServerAsync(server, cancellationToken).ConfigureAwait(false);
        var client = _host.TryGetMcpClient(server.QualifiedName);
        if (client == null)
        {
            throw new InvalidOperationException("The MCP client for '" + server.QualifiedName + "' was not created.");
        }

        return client;
    }

    private sealed class ServerTools
    {
        public ServerTools(PluginMcpServer server, AiTools.Mcp.McpClient client, IReadOnlyList<AiTools.Mcp.McpTool> tools)
        {
            Server = server;
            Client = client;
            Tools = tools;
        }

        public PluginMcpServer Server { get; }

        public AiTools.Mcp.McpClient Client { get; }

        public IReadOnlyList<AiTools.Mcp.McpTool> Tools { get; }
    }
}
