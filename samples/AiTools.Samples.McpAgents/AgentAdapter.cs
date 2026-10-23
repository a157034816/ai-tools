using System;
using System.Collections.Generic;

namespace AiTools.Samples.McpAgents;

/// <summary>可承载 MCP 配置的 agent，以及其解析后的配置文件路径。</summary>
public sealed class AgentAdapter
{
    /// <summary>创建适配器。应用程序中优先使用 <see cref="AgentCatalog.CreateAdapters()"/>。</summary>
    /// <param name="id">稳定标识符。</param>
    /// <param name="displayName">显示名称。</param>
    /// <param name="cliCommand">用于检测的 CLI 命令，或为 null。</param>
    /// <param name="docsUrl">可选的文档 URL。</param>
    /// <param name="kind">配置类型。</param>
    /// <param name="configPath">配置文件路径。</param>
    public AgentAdapter(
        string id,
        string displayName,
        string? cliCommand,
        string? docsUrl,
        AgentKind kind,
        string configPath)
    {
        Id = id;
        DisplayName = displayName;
        CliCommand = cliCommand;
        DocsUrl = docsUrl;
        Kind = kind;
        ConfigPath = configPath;
    }

    /// <summary>获取稳定的 agent 标识符（小写）。</summary>
    public string Id { get; }

    /// <summary>获取显示名称。</summary>
    public string DisplayName { get; }

    /// <summary>获取用于检测 agent 是否已安装的 CLI 命令。</summary>
    public string? CliCommand { get; }

    /// <summary>获取作为提示显示的可选文档 URL。</summary>
    public string? DocsUrl { get; }

    /// <summary>获取配置类型；参见 <see cref="AgentKind"/>。</summary>
    public AgentKind Kind { get; }

    /// <summary>获取解析后的配置文件路径（文件缺失时为预期位置）。</summary>
    public string ConfigPath { get; }

    /// <summary>
    /// 返回指向其他配置文件的此适配器副本。
    /// 当宿主允许用户覆盖检测到的位置时使用。
    /// </summary>
    public AgentAdapter WithConfigPath(string path)
        => new(Id, DisplayName, CliCommand, DocsUrl, Kind, path);
}

/// <summary>某个 agent 已配置的 MCP 服务快照。</summary>
public sealed class AgentMcpState
{
    /// <summary>获取 agent 标识符。</summary>
    public string AgentId { get; init; } = string.Empty;

    /// <summary>获取 agent 的配置文件是否存在。</summary>
    public bool ConfigExists { get; init; }

    /// <summary>获取配置文件路径（文件缺失时为预期路径）。</summary>
    public string ConfigPath { get; init; } = string.Empty;

    /// <summary>获取已安装的服务 ID 列表。</summary>
    public IReadOnlyList<string> InstalledServers { get; init; } = Array.Empty<string>();

    /// <summary>获取读取时遇到的错误（如有）。</summary>
    public string? Error { get; init; }

    /// <summary>获取是否已安装某个预设。</summary>
    public bool Has(string presetId)
    {
        foreach (var server in InstalledServers)
        {
            if (string.Equals(server, presetId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
