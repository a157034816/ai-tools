using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AiTools.Samples.McpAgents;

/// <summary>
/// 受支持的 agent 目录。每个条目包含在实际安装环境中观察到的配置位置和格式。
/// 不支持的 agent 仍会列出，以便应用导出可移植的 JSON 片段。
/// </summary>
public static class AgentCatalog
{
    /// <summary>使用当前用户主目录构建适配器。</summary>
    public static IReadOnlyList<AgentAdapter> CreateAdapters()
        => CreateAdapters(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>使用显式根目录构建适配器（便于测试）。</summary>
    public static IReadOnlyList<AgentAdapter> CreateAdapters(string home, string? localAppData)
    {
        if (home == null)
        {
            throw new ArgumentNullException(nameof(home));
        }

        return new[]
        {
            // Claude Code：~/.claude.json 顶层 mcpServers（用户级）。
            Create("claude-code", "Claude Code", "claude", "https://code.claude.com/docs/en/mcp", AgentKind.JsonMcpServers,
                Path.Combine(home, ".claude.json")),

            // Codex：~/.codex/config.toml 的 [mcp_servers.*]。
            Create("codex", "Codex CLI", "codex", null, AgentKind.TomlMcpServers,
                Path.Combine(home, ".codex", "config.toml")),

            // Grok：~/.grok/config.toml 的 [mcp_servers.*]。
            Create("grok", "Grok CLI", "grok", null, AgentKind.TomlMcpServers,
                Path.Combine(home, ".grok", "config.toml")),

            // Gemini CLI：~/.gemini/settings.json 的 mcpServers。
            Create("gemini", "Gemini CLI", "gemini", null, AgentKind.JsonMcpServers,
                Path.Combine(home, ".gemini", "settings.json")),

            // Kimi CLI：~/.kimi/mcp.json 的 mcpServers。
            Create("kimi", "Kimi CLI", "kimi", null, AgentKind.JsonMcpServers,
                Path.Combine(home, ".kimi", "mcp.json")),

            // OpenCode：~/.config/opencode/opencode.json 的 mcp 对象。
            Create("opencode", "OpenCode", "opencode", "https://opencode.ai/docs/mcp-servers", AgentKind.JsonMcpObject,
                Path.Combine(home, ".config", "opencode", "opencode.json")),

            // OpenClaw：未检测到本机安装，按 mcpServers 结构处理。
            Create("openclaw", "OpenClaw", "openclaw", null, AgentKind.JsonMcpServers,
                Path.Combine(home, ".openclaw", "mcp.json")),

            // Pi：~/.pi/agent/mcp.json 的 mcpServers（本机实测）。
            Create("pi", "Pi", "pi", null, AgentKind.JsonMcpServers,
                Path.Combine(home, ".pi", "agent", "mcp.json")),

            // OMP（oh-my-pi）：~/.omp/agent/mcp.json 的 mcpServers（本机实测）。
            Create("omp", "OMP (oh-my-pi)", "omp", null, AgentKind.JsonMcpServers,
                Path.Combine(home, ".omp", "agent", "mcp.json")),

            // Hermes：~/.hermes/config.yaml 的 mcp_servers 映射；Windows 上位于 %LOCALAPPDATA%\hermes。
            Create("hermes", "Hermes", "hermes", null, AgentKind.YamlMcpServers,
                ResolveHermesConfig(home, localAppData)),

            // dsh：未检测到本机安装，仅提供 JSON 导出。
            CreateUnsupported("dsh", "dsh", "dsh", Path.Combine(home, ".dsh", "mcp.json"))
        };
    }

    private static string ResolveHermesConfig(string home, string? localAppData)
    {
        if (!string.IsNullOrEmpty(localAppData))
        {
            var windowsPath = Path.Combine(localAppData, "hermes", "config.yaml");
            if (File.Exists(windowsPath))
            {
                return windowsPath;
            }
        }

        return Path.Combine(home, ".hermes", "config.yaml");
    }

    private static AgentAdapter Create(
        string id,
        string displayName,
        string cliCommand,
        string? docsUrl,
        AgentKind kind,
        string configPath)
        => new(id, displayName, cliCommand, docsUrl, kind, configPath);

    private static AgentAdapter CreateUnsupported(string id, string displayName, string cliCommand, string intendedPath)
        => new(id, displayName, cliCommand, null, AgentKind.Unsupported, intendedPath);
}

/// <summary>探测本机是否安装某个 agent 的结果。</summary>
public sealed class AgentProbe
{
    /// <summary>获取 agent 适配器。</summary>
    public AgentAdapter Adapter { get; init; } = null!;

    /// <summary>获取配置文件是否存在。</summary>
    public bool ConfigExists { get; init; }

    /// <summary>获取是否在 PATH 中找到了 agent 的 CLI。</summary>
    public bool CliInstalled { get; init; }

    /// <summary>获取是否可以通过编程方式配置该 agent。</summary>
    public bool Managed => Adapter.Kind != AgentKind.Unsupported;

    /// <summary>获取解析后的配置路径。</summary>
    public string ConfigPath => Adapter.ConfigPath;

    /// <summary>构建状态标签，例如 <c>已配置 · 3 个 MCP</c>。</summary>
    public string GetStatusLabel(int installedCount)
    {
        if (!Managed)
        {
            return "不支持自动配置 · 可导出 JSON";
        }

        if (!ConfigExists)
        {
            return CliInstalled ? "已检测到 CLI · 尚无配置" : "未检测到";
        }

        return $"已配置 · {installedCount} 个 MCP";
    }
}

/// <summary>检测哪些 agent 已安装以及其配置文件的位置。</summary>
public sealed class AgentProbeService
{
    private readonly IReadOnlyList<string> _pathDirectories;

    /// <summary>使用当前 PATH 创建探测器。</summary>
    public AgentProbeService()
        : this((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
    }

    /// <summary>使用显式 PATH 条目创建探测器（便于测试）。</summary>
    public AgentProbeService(IEnumerable<string> pathDirectories)
    {
        _pathDirectories = pathDirectories?.ToArray() ?? Array.Empty<string>();
    }

    /// <summary>探测所有 agent。</summary>
    public IReadOnlyList<AgentProbe> ProbeAll(IReadOnlyList<AgentAdapter> adapters)
    {
        var results = new List<AgentProbe>(adapters.Count);
        foreach (var adapter in adapters)
        {
            results.Add(new AgentProbe
            {
                Adapter = adapter,
                ConfigExists = adapter.Kind != AgentKind.Unsupported && File.Exists(adapter.ConfigPath),
                CliInstalled = IsCliInstalled(adapter.CliCommand)
            });
        }

        return results;
    }

    /// <summary>返回某个 CLI 命令是否存在于 PATH 中。</summary>
    public bool IsCliInstalled(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        foreach (var directory in _pathDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                if (File.Exists(Path.Combine(directory, command)) ||
                    File.Exists(Path.Combine(directory, command + ".exe")) ||
                    File.Exists(Path.Combine(directory, command + ".cmd")) ||
                    File.Exists(Path.Combine(directory, command + ".bat")))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 忽略非法 PATH 项。
            }
        }

        return false;
    }
}
