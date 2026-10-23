using System;
using System.Collections.Generic;

namespace AiTools.Extensions;

/// <summary>一个已加载插件的完整内容。</summary>
public sealed class AiToolsPlugin
{
    /// <summary>获取插件标识（manifest <c>name</c>，缺失时取目录名）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取 UI 显示名。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>获取版本。</summary>
    public string? Version { get; init; }

    /// <summary>获取描述。</summary>
    public string? Description { get; init; }

    /// <summary>获取许可证标识。</summary>
    public string? License { get; init; }

    /// <summary>获取主页 URL。</summary>
    public string? Homepage { get; init; }

    /// <summary>获取源代码仓库 URL。</summary>
    public string? Repository { get; init; }

    /// <summary>获取作者名称。</summary>
    public string? Author { get; init; }

    /// <summary>获取默认是否启用。</summary>
    public bool DefaultEnabled { get; init; } = true;

    /// <summary>获取自由元数据。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>获取插件根目录的绝对路径。</summary>
    public string RootPath { get; init; } = string.Empty;

    /// <summary>获取 <c>.claude-plugin/plugin.json</c> 的路径；无清单时为 null。</summary>
    public string? ManifestPath { get; init; }

    /// <summary>获取插件提供的命令（含由 skill 生成的命令），按名称排序。</summary>
    public IReadOnlyList<PluginCommand> Commands { get; init; } = Array.Empty<PluginCommand>();

    /// <summary>获取插件提供的 skill。</summary>
    public IReadOnlyList<PluginSkill> Skills { get; init; } = Array.Empty<PluginSkill>();

    /// <summary>获取插件声明的 MCP 服务器。</summary>
    public IReadOnlyList<PluginMcpServer> McpServers { get; init; } = Array.Empty<PluginMcpServer>();

    /// <summary>获取加载该插件时产生的诊断。</summary>
    public IReadOnlyList<ExtensionDiagnostic> Diagnostics { get; init; } = Array.Empty<ExtensionDiagnostic>();

    /// <inheritdoc />
    public override string ToString() => Name;
}
