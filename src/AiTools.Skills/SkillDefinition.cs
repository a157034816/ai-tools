using System;
using System.Collections.Generic;

namespace AiTools.Skills;

/// <summary>
/// 从 <c>SKILL.md</c> 包中发现的 skill。skill 是一个目录，其中包含一个
/// 带有 YAML frontmatter 的 <c>SKILL.md</c> 文件（至少包含 <c>name</c> 和 <c>description</c>），
/// 后面是 Markdown 指令，以及可选的资源和脚本。
/// </summary>
public sealed class SkillDefinition
{
    /// <summary>获取 skill 名称（小写字母、数字和单个连字符，最多 64 个字符）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取在模型决定是否加载 skill 时向其展示的描述（最多 1024 个字符）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>获取 frontmatter 之后的 Markdown 指令。</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>获取 skill 目录的绝对路径。</summary>
    public string DirectoryPath { get; init; } = string.Empty;

    /// <summary>获取 <c>SKILL.md</c> 文件的绝对路径。</summary>
    public string SkillFilePath { get; init; } = string.Empty;

    /// <summary>获取 frontmatter 中可选的许可证标识符。</summary>
    public string? License { get; init; }

    /// <summary>获取 frontmatter 中可选的兼容性说明。</summary>
    public string? Compatibility { get; init; }

    /// <summary>获取 frontmatter 中可选的版本字符串。</summary>
    public string? Version { get; init; }

    /// <summary>获取 skill 通过 <c>allowed-tools</c> 声明其需要的工具。</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>获取 frontmatter 中可选的元数据映射。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
