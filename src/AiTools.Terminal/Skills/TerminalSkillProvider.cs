using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Extensions;
using AiTools.Skills;

namespace AiTools.Terminal.Skills;

/// <summary>
/// 把多个来源的 skill 合并成一个 <see cref="IChatToolProvider"/>：
/// 插件自带技能、扩展运行时登记的技能、共享 <c>~/.agents/skills</c> 与项目 <c>.agents/skills</c>。
/// </summary>
/// <remarks>
/// 优先级与 Pi/Codex 的共用约定一致：项目级 &gt; 用户级 &gt; 扩展 &gt; 插件。
/// 模型通过 <c>load_skill</c> / <c>read_skill_file</c> 按需加载，
/// 合并后的清单同时进入系统提示，用户也能用 <c>/skill:&lt;名称&gt;</c> 直接调用。
/// 传入目录工厂时，每次访问都会重新读取目录；只有技能内容变化时才重建工具集。
/// </remarks>
public sealed class TerminalSkillProvider : IChatToolProvider
{
    private readonly Func<SkillCatalog> _catalogFactory;
    private readonly SkillToolOptions? _options;
    private readonly object _gate = new();
    private SkillCatalog? _catalog;
    private SkillToolset? _toolset;
    private Func<ToolCall, CancellationToken, Task<string>>? _handler;
    private string? _fingerprint;

    /// <summary>从目录工厂创建 provider。</summary>
    /// <param name="catalogFactory">返回当前 skill 目录的工厂。</param>
    /// <param name="options">skill 工具选项。</param>
    public TerminalSkillProvider(Func<SkillCatalog> catalogFactory, SkillToolOptions? options = null)
    {
        _catalogFactory = catalogFactory ?? throw new ArgumentNullException(nameof(catalogFactory));
        _options = options;
        Refresh();
    }

    /// <summary>获取合并来源后的 skill 目录。</summary>
    public SkillCatalog Catalog
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return _catalog!;
            }
        }
    }

    /// <summary>获取底层工具集。</summary>
    public SkillToolset Toolset
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return _toolset!;
            }
        }
    }

    /// <summary>获取可用的 skill 数量。</summary>
    public int SkillCount => Toolset.Skills.Count;

    /// <summary>重新解析目录；技能内容变化时重建工具集。返回是否发生了变化。</summary>
    public bool Refresh()
    {
        SkillCatalog next;
        try
        {
            next = _catalogFactory();
        }
        catch (Exception)
        {
            return false;
        }

        if (next == null)
        {
            return false;
        }

        var fingerprint = ComputeFingerprint(next);
        lock (_gate)
        {
            if (string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            _fingerprint = fingerprint;
            _catalog = next;
            _toolset = new SkillToolset(next, _options);
            _handler = _toolset.CreateToolHandler();
            return true;
        }
    }

    /// <summary>
    /// 合并插件技能、扩展技能与共享 <c>.agents</c> 技能，优先级从低到高：插件 → 扩展 → 共享。
    /// </summary>
    /// <param name="plugins">插件目录。</param>
    /// <param name="shared">共享目录发现结果。</param>
    /// <param name="extensionSkills">扩展运行时登记的技能。</param>
    /// <param name="options">skill 工具选项。</param>
    public static SkillCatalog Merge(
        PluginCatalog? plugins,
        SharedAgentsCatalog? shared,
        IEnumerable<SkillDefinition>? extensionSkills,
        SkillToolOptions? options = null)
    {
        var sources = new List<SkillDefinition>();
        if (plugins != null)
        {
            sources.AddRange(plugins.ToSkillCatalog().Skills);
        }

        if (extensionSkills != null)
        {
            sources.AddRange(extensionSkills);
        }

        if (shared != null)
        {
            sources.AddRange(shared.Skills.Skills);
        }

        // 同名时靠后的来源更具体，因此按顺序覆盖。
        var winners = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var skill in sources)
        {
            if (string.IsNullOrWhiteSpace(skill.Name))
            {
                continue;
            }

            if (!winners.ContainsKey(skill.Name))
            {
                order.Add(skill.Name);
            }

            winners[skill.Name] = skill;
        }

        _ = options;
        return SkillCatalog.FromSkills(order.Select(name => winners[name]));
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetTools()
    {
        Refresh();
        return Toolset.GetToolDefinitions();
    }

    /// <inheritdoc />
    public Task<string> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
    {
        Refresh();
        Func<ToolCall, CancellationToken, Task<string>>? handler;
        lock (_gate)
        {
            handler = _handler;
        }

        return handler == null
            ? Task.FromResult("{\"error\":\"No skills are loaded.\"}")
            : handler(call, cancellationToken);
    }

    private const char FingerprintSeparator = '\u001f';
    private const char FingerprintRecordSeparator = '\u001e';

    private static string ComputeFingerprint(SkillCatalog catalog)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var skill in catalog.Skills)
        {
            builder
                .Append(skill.Name).Append(FingerprintSeparator)
                .Append(skill.Description).Append(FingerprintSeparator)
                .Append(skill.DirectoryPath).Append(FingerprintSeparator)
                .Append(skill.Instructions).Append(FingerprintRecordSeparator);
        }

        return builder.ToString();
    }
}

/// <summary>
/// 把插件声明的 MCP 服务器接入终端会话：首次连接时建立全部服务器，
/// 之后 <see cref="GetTools"/> 返回已发现的工具（名称前缀 <c>plugin_</c>）。
/// </summary>
public sealed class PluginMcpToolProvider : IChatToolProvider
{
    private readonly PluginHost _host;
    private readonly Dictionary<string, ServerTools> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly List<string> _errors = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private bool _connected;

    /// <summary>创建 provider。</summary>
    /// <param name="host">插件宿主。</param>
    public PluginMcpToolProvider(PluginHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>获取连接过程中的错误。</summary>
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
