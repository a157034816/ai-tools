using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AiTools.Skills;

/// <summary>一份被发现的 agent 指令文件（例如 <c>AGENTS.md</c>）。</summary>
public sealed class AgentInstructions
{
    /// <summary>获取指令来源（文件绝对路径或内置说明）。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>获取作用范围。</summary>
    public AgentInstructionsScope Scope { get; init; }

    /// <summary>获取 Markdown 正文（已去掉 frontmatter 之外的原文）。</summary>
    public string Content { get; init; } = string.Empty;
}

/// <summary>agent 指令的作用范围。</summary>
public enum AgentInstructionsScope
{
    /// <summary>用户级：对这台机器上的所有项目生效。</summary>
    User,

    /// <summary>项目级：只对当前工作目录生效。</summary>
    Project
}

/// <summary>共享 <c>.agents</c> 目录的发现结果。</summary>
public sealed class SharedAgentsCatalog
{
    /// <summary>获取合并后的 skill（项目级覆盖用户级）。</summary>
    public SkillCatalog Skills { get; init; } = SkillCatalog.FromSkills(Array.Empty<SkillDefinition>());

    /// <summary>获取按优先级从低到高排列的 agent 指令（用户级在前，项目级在后）。</summary>
    public IReadOnlyList<AgentInstructions> Instructions { get; init; } = Array.Empty<AgentInstructions>();

    /// <summary>获取实际参与合并的目录（按优先级从低到高）。</summary>
    public IReadOnlyList<string> SkillRoots { get; init; } = Array.Empty<string>();

    /// <summary>获取发现过程中的诊断（含被覆盖的重复 skill）。</summary>
    public IReadOnlyList<SkillDiagnostic> Diagnostics { get; init; } = Array.Empty<SkillDiagnostic>();

    /// <summary>
    /// 把 agent 指令拼接成一段可直接注入系统提示的文本。没有指令时返回空字符串。
    /// </summary>
    /// <remarks>
    /// 顺序与优先级一致：用户级在前、项目级在后，越靠后的内容越具体。
    /// </remarks>
    public string BuildInstructionsPrompt()
    {
        if (Instructions.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("## Agent instructions");
        builder.AppendLine();

        foreach (var instructions in Instructions)
        {
            builder.Append("### ").AppendLine(instructions.Scope == AgentInstructionsScope.User ? "User" : "Project");
            builder.Append("Source: ").AppendLine(instructions.Source);
            builder.AppendLine();
            builder.AppendLine(instructions.Content.TrimEnd());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// 发现多个 agent 工具（Pi、Codex 等）共用的 <c>.agents</c> 目录。
/// </summary>
/// <remarks>
/// 约定来自 Pi 的共享资源布局，也是 Codex 与 Pi 都能读到的位置：
/// <list type="bullet">
/// <item><c>~/.ai-tools/skills/&lt;name&gt;/SKILL.md</c>：AiTools 专属的用户级 skill（优先级最高）</item>
/// <item><c>~/.agents/skills/&lt;name&gt;/SKILL.md</c>：用户级共享 skill</item>
/// <item><c>&lt;project&gt;/.agents/skills/&lt;name&gt;/SKILL.md</c>：项目级共享 skill</item>
/// <item><c>~/.agents/skills/*/agents/*.yaml</c>：skill 附带的子代理定义（本库只报告，不执行）</item>
/// <item><c>AGENTS.md</c>：用户级与项目级 agent 指令，项目级覆盖/追加在用户级之后</item>
/// </list>
/// 项目级定义总是覆盖同名用户级定义——这是 Pi 的 <c>.pi/agents/</c> 与 <c>.agents/</c>
/// 冲突时的既有规则，沿用它可以避免不同工具读到不同的技能。
/// AiTools 专属目录排在共享目录之后，因此同名时专属定义胜出（越具体越优先）。
/// </remarks>
public static class SharedAgents
{
    /// <summary>共享目录名（用户主目录与项目根下都用它）。</summary>
    public const string DirectoryName = ".agents";

    /// <summary>agent 指令文件名。</summary>
    public const string InstructionsFileName = "AGENTS.md";

    /// <summary>共享 skill 的子目录名。</summary>
    public const string SkillsDirectoryName = "skills";

    /// <summary>获取用户级共享目录（<c>~/.agents</c>）。</summary>
    public static string GetUserDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DirectoryName);

    /// <summary>获取项目级共享目录（<c>&lt;project&gt;/.agents</c>）。</summary>
    /// <param name="projectDirectory">项目根目录。</param>
    public static string GetProjectDirectory(string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new ArgumentException("A project directory is required.", nameof(projectDirectory));
        }

        return Path.Combine(projectDirectory!, DirectoryName);
    }

    /// <summary>
    /// 发现用户级与项目级共享 skill 及 <c>AGENTS.md</c>。
    /// </summary>
    /// <param name="projectDirectory">项目根目录；为 null 时只发现用户级资源。</param>
    /// <param name="includeUser">
    /// 是否包含用户级资源。设为 false 可避免测试或受限宿主读取开发者主目录。
    /// </param>
    /// <param name="userDirectory">覆盖用户级共享目录；为 null 时使用 <see cref="GetUserDirectory"/>。</param>
    /// <param name="includeNativeSkills">
    /// 是否包含 AiTools 专属的 <c>~/.ai-tools/skills</c>（以及同一根下的 <c>AGENTS.md</c>）。
    /// 该目录比共享 <c>.agents</c> 更具体，同名时优先。默认 true。
    /// </param>
    /// <param name="nativeDirectory">
    /// 覆盖 AiTools 专属目录；为 null 时使用 <c>~/.ai-tools</c>。
    /// 注意：显式指定 <paramref name="userDirectory"/> 而不同时指定本参数时，
    /// 视为调用方要求一个沙盒视图，专属目录会被跳过，避免读到开发者真实主目录。
    /// </param>
    public static SharedAgentsCatalog Discover(
        string? projectDirectory = null,
        bool includeUser = true,
        string? userDirectory = null,
        bool includeNativeSkills = true,
        string? nativeDirectory = null)
    {
        var diagnostics = new List<SkillDiagnostic>();
        var skillRoots = new List<string>();
        var instructions = new List<AgentInstructions>();

        if (includeUser)
        {
            var userRoot = userDirectory ?? GetUserDirectory();

            var userSkills = Path.Combine(userRoot, SkillsDirectoryName);
            if (Directory.Exists(userSkills))
            {
                skillRoots.Add(userSkills);
            }

            var userInstructions = Path.Combine(userRoot, InstructionsFileName);
            if (File.Exists(userInstructions))
            {
                instructions.Add(ReadInstructions(userInstructions, AgentInstructionsScope.User, diagnostics));
            }

            // AiTools 专属目录排在共享 .agents 之后：越具体越优先。
            if (includeNativeSkills)
            {
                var nativeRoot = nativeDirectory
                    ?? (userDirectory == null ? AiTools.Core.AiToolsPaths.GetRootDirectory() : null);

                if (nativeRoot != null)
                {
                    var nativeSkills = Path.Combine(nativeRoot, SkillsDirectoryName);
                    if (Directory.Exists(nativeSkills))
                    {
                        skillRoots.Add(nativeSkills);
                    }

                    var nativeInstructions = Path.Combine(nativeRoot, InstructionsFileName);
                    if (File.Exists(nativeInstructions))
                    {
                        instructions.Add(ReadInstructions(nativeInstructions, AgentInstructionsScope.User, diagnostics));
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(projectDirectory))
        {
            var projectRoot = GetProjectDirectory(projectDirectory!);

            var projectSkills = Path.Combine(projectRoot, SkillsDirectoryName);
            if (Directory.Exists(projectSkills))
            {
                skillRoots.Add(projectSkills);
            }

            var projectInstructions = Path.Combine(projectRoot, InstructionsFileName);
            if (File.Exists(projectInstructions))
            {
                instructions.Add(ReadInstructions(projectInstructions, AgentInstructionsScope.Project, diagnostics));
            }
        }

        var skills = skillRoots.Count == 0
            ? SkillCatalog.FromSkills(Array.Empty<SkillDefinition>())
            : SkillCatalog.LoadFromDirectories(skillRoots, recursive: true);

        diagnostics.AddRange(skills.Diagnostics);

        return new SharedAgentsCatalog
        {
            Skills = skills,
            Instructions = instructions,
            SkillRoots = skillRoots,
            Diagnostics = diagnostics
        };
    }

    /// <summary>
    /// 列出某个 skill 目录下附带的子代理定义（<c>agents/*.yaml</c> / <c>agents/*.yml</c>）。
    /// 本库不执行这些定义，只把它们报告给调用方，由宿主决定是否使用。
    /// </summary>
    /// <param name="skillDirectory">skill 目录。</param>
    public static IReadOnlyList<string> FindAgentDefinitions(string skillDirectory)
    {
        if (string.IsNullOrWhiteSpace(skillDirectory))
        {
            return Array.Empty<string>();
        }

        var agentsDirectory = Path.Combine(skillDirectory!, "agents");
        if (!Directory.Exists(agentsDirectory))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory
                .EnumerateFiles(agentsDirectory, "*.y*ml", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            // 目录存在但不可读时不要让发现流程失败。
            return Array.Empty<string>();
        }
    }

    private static AgentInstructions ReadInstructions(
        string path,
        AgentInstructionsScope scope,
        List<SkillDiagnostic> diagnostics)
    {
        try
        {
            return new AgentInstructions
            {
                Source = path,
                Scope = scope,
                Content = File.ReadAllText(path)
            };
        }
        catch (Exception ex)
        {
            diagnostics.Add(new SkillDiagnostic
            {
                Path = path,
                Message = "Failed to read agent instructions: " + ex.Message
            });

            return new AgentInstructions { Source = path, Scope = scope, Content = string.Empty };
        }
    }
}
