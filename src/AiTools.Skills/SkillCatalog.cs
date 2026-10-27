using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AiTools.Skills;

/// <summary>发现或加载 skill 时遇到的问题。发现过程永不抛出异常。</summary>
public sealed class SkillDiagnostic
{
    /// <summary>获取与问题相关的路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>获取人类可读的问题描述。</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 按名称索引的已发现 skill 的不可变集合。可通过
/// <see cref="LoadFromDirectory(string, bool)"/> 或 <see cref="FromSkills"/> 构建。
/// </summary>
public sealed class SkillCatalog
{
    private static readonly Regex NamePattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 1024;

    private readonly Dictionary<string, SkillDefinition> _skills;

    private SkillCatalog(List<SkillDefinition> skills, IReadOnlyList<SkillDiagnostic> diagnostics)
    {
        _skills = skills.ToDictionary(skill => skill.Name, StringComparer.OrdinalIgnoreCase);
        Skills = skills;
        Diagnostics = diagnostics;
    }

    /// <summary>获取已加载的 skill，按名称排序。</summary>
    public IReadOnlyList<SkillDefinition> Skills { get; }

    /// <summary>获取一个值，指示是否至少加载了一个 skill。</summary>
    public int Count => Skills.Count;

    /// <summary>获取发现过程中遇到的问题。</summary>
    public IReadOnlyList<SkillDiagnostic> Diagnostics { get; }

    /// <summary>按名称获取 skill，不存在时返回 null。</summary>
    public SkillDefinition? TryGet(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return _skills.TryGetValue(name, out var skill) ? skill : null;
    }

    /// <summary>从预先构建的 skill 定义创建目录（适用于测试和嵌入式 skill）。</summary>
    public static SkillCatalog FromSkills(IEnumerable<SkillDefinition> skills)
        => new(skills.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToList(), Array.Empty<SkillDiagnostic>());

    /// <summary>
    /// 在 <paramref name="rootPath"/> 下发现 skill。每个 skill 是一个包含
    /// <c>SKILL.md</c> 文件的目录。当 <paramref name="recursive"/> 为 true 时，也会搜索子目录；
    /// 包含 <c>SKILL.md</c> 的最近目录会被视为 skill 根目录。
    /// 无效的 skill 会被跳过，并通过 <see cref="Diagnostics"/> 报告。
    /// </summary>
    public static SkillCatalog LoadFromDirectory(string rootPath, bool recursive = true)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A skills directory is required.", nameof(rootPath));
        }

        var fullRoot = Path.GetFullPath(rootPath);
        var diagnostics = new List<SkillDiagnostic>();
        var skills = new List<SkillDefinition>();

        if (!Directory.Exists(fullRoot))
        {
            diagnostics.Add(new SkillDiagnostic { Path = fullRoot, Message = "The skills directory does not exist." });
            return new SkillCatalog(skills, diagnostics);
        }

        var rootSkillFile = Path.Combine(fullRoot, "SKILL.md");
        if (File.Exists(rootSkillFile))
        {
            // 根目录本身就是一个技能包（常见于单个技能仓库）。
            var rootSkill = TryLoadSkill(rootSkillFile, diagnostics);
            if (rootSkill != null)
            {
                skills.Add(rootSkill);
            }

            if (!recursive)
            {
                return new SkillCatalog(skills, diagnostics);
            }
        }
        else if (!recursive)
        {
            // 非递归模式：只检查直属子目录中的 SKILL.md。
            foreach (var directory in Directory.EnumerateDirectories(fullRoot))
            {
                var candidate = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var skill = TryLoadSkill(candidate, diagnostics);
                if (skill != null)
                {
                    skills.Add(skill);
                }
            }

            return new SkillCatalog(MarkDuplicates(skills, diagnostics), diagnostics);
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(
                fullRoot,
                "SKILL.md",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new SkillDiagnostic { Path = fullRoot, Message = "Failed to enumerate skills: " + ex.Message });
            return new SkillCatalog(skills, diagnostics);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            // 根目录技能已经加载过，避免重复。
            if (string.Equals(file, rootSkillFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var skill = TryLoadSkill(file, diagnostics);
            if (skill == null)
            {
                continue;
            }

            if (!seen.Add(skill.Name))
            {
                diagnostics.Add(new SkillDiagnostic
                {
                    Path = file,
                    Message = $"Duplicate skill name '{skill.Name}'. The first definition wins; rename the skill or remove the duplicate."
                });
                continue;
            }

            skills.Add(skill);
        }

        skills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return new SkillCatalog(skills, diagnostics);
    }

    /// <summary>
    /// 从多个目录合并发现 skill。后列出的目录优先：同名 skill 由 <paramref name="roots"/> 中
    /// 靠后的定义胜出，靠前的定义被记录为诊断。
    /// </summary>
    /// <remarks>
    /// 这与 Pi / Codex 共用的 <c>.agents</c> 约定一致：项目级定义覆盖用户级定义。
    /// 调用方通常按"内置 → 用户共享 → 项目"的顺序传入，最后一项优先级最高。
    /// </remarks>
    /// <param name="roots">按优先级从低到高排列的目录列表；不存在的目录只记录诊断。</param>
    /// <param name="recursive">是否递归搜索子目录。</param>
    public static SkillCatalog LoadFromDirectories(IEnumerable<string> roots, bool recursive = true)
    {
        if (roots == null)
        {
            throw new ArgumentNullException(nameof(roots));
        }

        var diagnostics = new List<SkillDiagnostic>();
        var winners = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var catalog = LoadFromDirectory(root!, recursive);
            diagnostics.AddRange(catalog.Diagnostics);

            foreach (var skill in catalog.Skills)
            {
                if (winners.TryGetValue(skill.Name, out var existing))
                {
                    // 后出现的根目录优先级更高：替换旧定义并说明被覆盖的来源。
                    diagnostics.Add(new SkillDiagnostic
                    {
                        Path = existing.SkillFilePath,
                        Message = $"Skill '{skill.Name}' from '{existing.DirectoryPath}' was overridden by '{skill.DirectoryPath}'."
                    });
                    winners[skill.Name] = skill;
                    continue;
                }

                winners[skill.Name] = skill;
                order.Add(skill.Name);
            }
        }

        var merged = order.Select(name => winners[name]).ToList();
        merged.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return new SkillCatalog(merged, diagnostics);
    }

    private static List<SkillDefinition> MarkDuplicates(List<SkillDefinition> skills, List<SkillDiagnostic> diagnostics)    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SkillDefinition>(skills.Count);
        foreach (var skill in skills)
        {
            if (!seen.Add(skill.Name))
            {
                diagnostics.Add(new SkillDiagnostic
                {
                    Path = skill.SkillFilePath,
                    Message = $"Duplicate skill name '{skill.Name}'. The first definition wins; rename the skill or remove the duplicate."
                });
                continue;
            }

            result.Add(skill);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return result;
    }

    private static SkillDefinition? TryLoadSkill(string filePath, List<SkillDiagnostic> diagnostics)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new SkillDiagnostic { Path = filePath, Message = "Failed to read the skill file: " + ex.Message });
            return null;
        }

        if (!SkillFrontmatter.TryParse(content, out var frontmatter, out var error))
        {
            diagnostics.Add(new SkillDiagnostic { Path = filePath, Message = error ?? "Invalid frontmatter." });
            return null;
        }

        var name = frontmatter!.GetString("name")?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            diagnostics.Add(new SkillDiagnostic { Path = filePath, Message = "Frontmatter is missing the required 'name' field." });
            return null;
        }

        if (name!.Length > MaxNameLength || !NamePattern.IsMatch(name))
        {
            diagnostics.Add(new SkillDiagnostic
            {
                Path = filePath,
                Message = $"Skill name '{name}' is invalid: use lowercase letters, digits and single hyphens (max {MaxNameLength} characters)."
            });
            return null;
        }

        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var directoryName = Path.GetFileName(directory);
        if (!string.Equals(directoryName, name, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new SkillDiagnostic
            {
                Path = filePath,
                Message = $"Skill directory '{directoryName}' does not match the skill name '{name}'. The skill was loaded anyway."
            });
        }

        var description = frontmatter.GetString("description")?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            diagnostics.Add(new SkillDiagnostic { Path = filePath, Message = "Frontmatter is missing the required 'description' field." });
            return null;
        }

        if (description!.Length > MaxDescriptionLength)
        {
            diagnostics.Add(new SkillDiagnostic
            {
                Path = filePath,
                Message = $"Description exceeds {MaxDescriptionLength} characters and was truncated."
            });
            description = description.Substring(0, MaxDescriptionLength);
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in frontmatter.Values)
        {
            if (pair.Key.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
            {
                metadata[pair.Key.Substring("metadata.".Length)] = pair.Value;
            }
        }

        return new SkillDefinition
        {
            Name = name,
            Description = description,
            Instructions = frontmatter.Body,
            DirectoryPath = directory,
            SkillFilePath = filePath,
            License = frontmatter.GetString("license"),
            Compatibility = frontmatter.GetString("compatibility"),
            Version = frontmatter.GetString("version"),
            AllowedTools = frontmatter.GetList("allowed-tools"),
            Metadata = metadata
        };
    }
}
