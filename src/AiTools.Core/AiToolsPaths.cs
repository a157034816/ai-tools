using System;
using System.IO;

namespace AiTools.Core;

/// <summary>
/// AiTools 的用户级数据根目录。设置、会话、扩展等运行时读写都落在 <c>~/.ai-tools</c> 下，
/// 与 Claude Code 的 <c>~/.claude</c>、Codex 的 <c>~/.codex</c> 保持同一种布局约定。
/// </summary>
/// <remarks>
/// 可用 <c>AITOOLS_HOME</c> 环境变量覆盖根目录（测试与便携部署用）。
/// 共享 <c>.agents</c> 目录不在此列——它属于跨工具约定，位置固定为 <c>~/.agents</c>。
/// </remarks>
public static class AiToolsPaths
{
    /// <summary>用户主目录下的数据目录名。</summary>
    public const string DirectoryName = ".ai-tools";

    /// <summary>覆盖数据根目录的环境变量。</summary>
    public const string HomeEnvironmentVariable = "AITOOLS_HOME";

    /// <summary>获取默认数据根目录（<c>~/.ai-tools</c>），忽略环境变量覆盖。</summary>
    public static string GetDefaultRootDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DirectoryName);

    /// <summary>获取数据根目录：有 <c>AITOOLS_HOME</c> 时用它，否则用 <see cref="GetDefaultRootDirectory"/>。</summary>
    public static string GetRootDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? GetDefaultRootDirectory()
            : Path.GetFullPath(overridden!);
    }

    /// <summary>获取默认设置文件路径（<c>&lt;root&gt;/settings.json</c>）。</summary>
    public static string GetSettingsFile()
        => Path.Combine(GetRootDirectory(), "settings.json");

    /// <summary>获取会话目录的根；<c>JsonConversationStore</c> 会在其下创建 <c>conversations</c>。</summary>
    public static string GetConversationsRoot()
        => GetRootDirectory();

    /// <summary>获取扩展目录（<c>&lt;root&gt;/extensions</c>）。</summary>
    public static string GetExtensionsDirectory()
        => Path.Combine(GetRootDirectory(), "extensions");

    /// <summary>获取扩展数据目录（<c>&lt;root&gt;/extensions/data</c>）。</summary>
    public static string GetExtensionDataDirectory()
        => Path.Combine(GetExtensionsDirectory(), "data");

    /// <summary>
    /// 获取 AiTools 原生技能目录（<c>&lt;root&gt;/skills</c>）。
    /// 与跨工具的 <c>~/.agents/skills</c> 并存，优先级更高（更具体者胜出）。
    /// </summary>
    public static string GetSkillsDirectory()
        => Path.Combine(GetRootDirectory(), "skills");

    /// <summary>获取 AiTools 原生 agent 指令文件（<c>&lt;root&gt;/AGENTS.md</c>）。</summary>
    public static string GetInstructionsFile()
        => Path.Combine(GetRootDirectory(), "AGENTS.md");
}
