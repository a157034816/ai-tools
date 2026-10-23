using System;
using System.Collections.Generic;
using AiTools.Skills;

namespace AiTools.Extensions;

/// <summary>
/// 插件提供的一个斜杠命令。Claude Code 中命令与 skill 是同一概念的两种载体：
/// <c>commands/&lt;name&gt;.md</c> 与 <c>skills/&lt;name&gt;/SKILL.md</c> 都生成一个
/// <c>/&lt;plugin&gt;:&lt;name&gt;</c> 命令，因此本框架把它们统一建模为 <see cref="PluginCommand"/>。
/// </summary>
public sealed class PluginCommand
{
    /// <summary>获取带插件前缀的命令名，例如 <c>workflows:goal</c>。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取不带前缀的命令名，例如 <c>goal</c>。无冲突时可直接用 <c>/goal</c> 调用。</summary>
    public string ShortName { get; init; } = string.Empty;

    /// <summary>获取拥有该命令的插件名。</summary>
    public string PluginName { get; init; } = string.Empty;

    /// <summary>获取命令描述，用于命令面板与模型提示。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>获取参数提示，例如 <c>&lt;目标描述&gt;</c>。</summary>
    public string? ArgumentHint { get; init; }

    /// <summary>获取 Markdown 指令正文（已去掉 frontmatter，未做变量替换）。</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>获取命令来源文件；内联定义时为空字符串。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>获取插件根目录（<c>${CLAUDE_PLUGIN_ROOT}</c>）。</summary>
    public string PluginRoot { get; init; } = string.Empty;

    /// <summary>获取 skill 目录（<c>${CLAUDE_SKILL_DIR}</c>）；命令文件定义时为 null。</summary>
    public string? SkillDirectory { get; init; }

    /// <summary>获取 <c>allowed-tools</c> 声明的工具，调用该命令的轮次内免确认。</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>获取 <c>disallowed-tools</c> 声明的工具，在本命令激活期间从工具池移除。</summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = Array.Empty<string>();

    /// <summary>获取 <c>arguments</c> 声明的具名位置参数，用于 <c>$name</c> 替换。</summary>
    public IReadOnlyList<string> ArgumentNames { get; init; } = Array.Empty<string>();

    /// <summary>获取命令指定的模型；为空时沿用会话模型。</summary>
    public string? Model { get; init; }

    /// <summary>获取模型是否不可自行调用该命令（<c>disable-model-invocation</c>）。</summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>获取用户是否可以调用该命令（<c>user-invocable</c>，默认 true）。</summary>
    public bool UserInvocable { get; init; } = true;

    /// <inheritdoc />
    public override string ToString() => "/" + Name;
}

/// <summary>插件提供的一个 Agent Skill（<c>skills/&lt;name&gt;/SKILL.md</c>）。</summary>
public sealed class PluginSkill
{
    /// <summary>获取 skill 名称（小写短横线形式）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取带插件前缀的名称，例如 <c>workflows:goal</c>。</summary>
    public string QualifiedName { get; init; } = string.Empty;

    /// <summary>获取拥有该 skill 的插件名。</summary>
    public string PluginName { get; init; } = string.Empty;

    /// <summary>获取 skill 描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>获取 skill 的 Markdown 指令正文。</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>获取 skill 目录的绝对路径。</summary>
    public string DirectoryPath { get; init; } = string.Empty;

    /// <summary>获取 <c>SKILL.md</c> 的绝对路径。</summary>
    public string SkillFilePath { get; init; } = string.Empty;

    /// <summary>获取插件根目录。</summary>
    public string PluginRoot { get; init; } = string.Empty;

    /// <summary>获取 <c>allowed-tools</c> 声明。</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>获取该 skill 生成的命令定义。</summary>
    public PluginCommand? Command { get; init; }

    /// <summary>转换为 <see cref="AiTools.Skills.SkillDefinition"/>，可直接送入 <see cref="SkillCatalog"/>。</summary>
    public SkillDefinition ToSkillDefinition() => new()
    {
        Name = Name,
        Description = Description,
        Instructions = Instructions,
        DirectoryPath = DirectoryPath,
        SkillFilePath = SkillFilePath,
        AllowedTools = AllowedTools
    };
}

/// <summary>插件声明的 MCP 传输类型。</summary>
public enum PluginMcpTransportKind
{
    /// <summary>启动本地子进程并通过 stdin/stdout 通信。</summary>
    Stdio,

    /// <summary>Streamable HTTP（POST + SSE 响应）。</summary>
    Http,

    /// <summary>旧版 HTTP + SSE。</summary>
    Sse
}

/// <summary>插件声明的 MCP 服务。连接由 <see cref="PluginHost.ConnectMcpServerAsync"/> 建立。</summary>
public sealed class PluginMcpServer
{
    /// <summary>获取服务器在插件内的名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取带插件前缀的服务器名，例如 <c>workflows:db</c>。</summary>
    public string QualifiedName { get; init; } = string.Empty;

    /// <summary>获取拥有该服务器的插件名。</summary>
    public string PluginName { get; init; } = string.Empty;

    /// <summary>获取传输类型。</summary>
    public PluginMcpTransportKind Transport { get; init; }

    /// <summary>获取 stdio 传输要启动的可执行文件。</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>获取 stdio 传输的命令行参数（已替换 <c>${CLAUDE_PLUGIN_ROOT}</c> 等变量）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>获取 stdio 子进程的额外环境变量。</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>获取 HTTP/SSE 传输的端点。</summary>
    public Uri? Url { get; init; }

    /// <summary>获取 HTTP/SSE 传输的请求头。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>获取声明来源文件；内联声明时为 null。</summary>
    public string? SourcePath { get; init; }

    /// <inheritdoc />
    public override string ToString() => QualifiedName + " (" + Transport + ")";
}
