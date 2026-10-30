using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiTools.Skills;

namespace AiTools.Extensions.Packages;

/// <summary>
/// 从一个扩展包目录读取随包技能。技能布局与 Agent Skills 一致：
/// 每个包含 <c>SKILL.md</c> 的目录是一个技能。
/// </summary>
/// <remarks>
/// 搜索顺序：清单 <c>skills</c> 声明的路径（相对包根）→ 包根的 <c>skills/</c> 目录。
/// 两者都没有时不报错，包仍然可以只提供代码。
/// 无效技能被跳过并通过诊断报告，发现过程永不抛出异常。
/// </remarks>
public static class ExtensionSkillLoader
{
    /// <summary>默认技能目录名（位于包根）。</summary>
    public const string DefaultSkillsDirectoryName = "skills";

    /// <summary>技能定义文件名。</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>
    /// 读取包内技能。
    /// </summary>
    /// <param name="manifest">包清单（<see cref="ExtensionPackageManifest.RootPath"/> 必须已由加载器填入）。</param>
    /// <param name="report">诊断回调；为 null 时丢弃诊断。</param>
    /// <returns>按名称排序的技能；包根不可用时为空集合。</returns>
    public static IReadOnlyList<SkillDefinition> Load(
        ExtensionPackageManifest manifest,
        Action<PluginDiagnostic>? report = null)
    {
        if (manifest == null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var root = manifest.RootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Array.Empty<SkillDefinition>();
        }

        var roots = ResolveSkillRoots(manifest, report);
        if (roots.Count == 0)
        {
            return Array.Empty<SkillDefinition>();
        }

        var skills = new List<SkillDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in roots)
        {
            foreach (var skill in new SkillCatalogReader(directory, manifest.Name, report).Read())
            {
                if (!seen.Add(skill.Name))
                {
                    report?.Invoke(new PluginDiagnostic
                    {
                        Kind = PluginDiagnosticKind.Warning,
                        PluginName = manifest.Name,
                        Path = skill.SkillFilePath,
                        Message = $"Duplicate skill name '{skill.Name}' in extension package '{manifest.Name}'. The first definition wins."
                    });
                    continue;
                }

                skills.Add(skill);
            }
        }

        skills.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return skills;
    }

    private static List<string> ResolveSkillRoots(ExtensionPackageManifest manifest, Action<PluginDiagnostic>? report)
    {
        var root = Path.GetFullPath(manifest.RootPath!);
        var roots = new List<string>();

        void Add(string path, string declared)
        {
            var resolved = ExtensionPackagePath.Resolve(root, declared);
            if (resolved == null)
            {
                report?.Invoke(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    PluginName = manifest.Name,
                    Path = declared,
                    Message = "Skill path is outside the extension package and was ignored."
                });
                return;
            }

            if (!Directory.Exists(resolved) && !File.Exists(resolved))
            {
                report?.Invoke(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    PluginName = manifest.Name,
                    Path = resolved,
                    Message = "Skill path does not exist."
                });
                return;
            }

            if (!roots.Contains(resolved, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(resolved);
            }
        }

        foreach (var declared in manifest.SkillPaths)
        {
            if (!string.IsNullOrWhiteSpace(declared))
            {
                Add(root, declared.Trim());
            }
        }

        // 清单未声明 skills 时回退到默认目录；显式声明后只加载声明的位置。
        if (manifest.SkillPaths.Count == 0)
        {
            var defaultDirectory = Path.Combine(root, DefaultSkillsDirectoryName);
            if (Directory.Exists(defaultDirectory))
            {
                Add(root, DefaultSkillsDirectoryName);
            }
        }

        return roots;
    }
}

/// <summary>读取单个技能根下的技能目录；被 <see cref="ExtensionSkillLoader"/> 复用。</summary>
internal sealed class SkillCatalogReader
{
    private readonly string _root;
    private readonly string _pluginName;
    private readonly Action<PluginDiagnostic>? _report;

    public SkillCatalogReader(string root, string pluginName, Action<PluginDiagnostic>? report)
    {
        _root = root;
        _pluginName = pluginName;
        _report = report;
    }

    public IEnumerable<SkillDefinition> Read()
    {
        // 目录本身就是一个技能（根下直接有 SKILL.md）。
        if (File.Exists(Path.Combine(_root, ExtensionSkillLoader.SkillFileName)))
        {
            var single = TryLoad(_root);
            if (single != null)
            {
                yield return single;
            }

            yield break;
        }

        if (!Directory.Exists(_root))
        {
            yield break;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(_root);
        }
        catch (Exception ex)
        {
            _report?.Invoke(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Warning,
                PluginName = _pluginName,
                Path = _root,
                Message = "Failed to enumerate skills: " + ex.Message
            });
            yield break;
        }

        Array.Sort(children, StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            if (!File.Exists(Path.Combine(child, ExtensionSkillLoader.SkillFileName)))
            {
                continue;
            }

            var skill = TryLoad(child);
            if (skill != null)
            {
                yield return skill;
            }
        }
    }

    private SkillDefinition? TryLoad(string directory)
    {
        var file = Path.Combine(directory, ExtensionSkillLoader.SkillFileName);
        string content;
        try
        {
            content = File.ReadAllText(file, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _report?.Invoke(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Warning,
                PluginName = _pluginName,
                Path = file,
                Message = "Failed to read SKILL.md: " + ex.Message
            });
            return null;
        }

        var parsed = SkillFrontmatter.TryParse(content, out var frontmatter, out var error);
        if (!parsed || frontmatter == null)
        {
            _report?.Invoke(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Warning,
                PluginName = _pluginName,
                Path = file,
                Message = (error ?? "Invalid frontmatter.") + " The skill was skipped."
            });
            return null;
        }

        var name = NormalizeName(frontmatter.GetString("name") ?? Path.GetFileName(directory));
        if (name.Length == 0)
        {
            _report?.Invoke(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Warning,
                PluginName = _pluginName,
                Path = file,
                Message = "Skill has no usable name and was skipped."
            });
            return null;
        }

        if (!string.Equals(Path.GetFileName(directory), name, StringComparison.OrdinalIgnoreCase))
        {
            _report?.Invoke(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Warning,
                PluginName = _pluginName,
                Path = file,
                Message = $"Skill directory '{Path.GetFileName(directory)}' does not match the skill name '{name}'. The skill was loaded anyway."
            });
        }

        var description = frontmatter.GetString("description")?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            _report?.Invoke(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Warning,
                PluginName = _pluginName,
                Path = file,
                Message = "Skill has no description; the model will not know when to use it."
            });
            description = name;
        }
        else
        {
            description = description!.Trim();
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
            DirectoryPath = Path.GetFullPath(directory),
            SkillFilePath = Path.GetFullPath(file),
            License = frontmatter.GetString("license"),
            Compatibility = frontmatter.GetString("compatibility"),
            Version = frontmatter.GetString("version"),
            AllowedTools = frontmatter.GetList("allowed-tools"),
            Metadata = metadata
        };
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (character is '-' or '_' or ' ')
            {
                builder.Append('-');
            }
        }

        var normalized = builder.ToString().Trim('-');
        while (normalized.Contains("--"))
        {
            normalized = normalized.Replace("--", "-");
        }

        return normalized.Length > 64 ? normalized.Substring(0, 64).TrimEnd('-') : normalized;
    }
}

/// <summary>扩展包内的相对路径解析：拒绝越界路径。</summary>
internal static class ExtensionPackagePath
{
    /// <summary>把声明路径解析到包根内；越界或为空时返回 null。</summary>
    public static string? Resolve(string root, string declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        var normalized = declared.Trim();
        if (normalized is "." or "./")
        {
            return root;
        }

        if (normalized.Contains(".."))
        {
            return null;
        }

        var trimmed = normalized.TrimStart('.', '/', '\\');
        if (trimmed.Length == 0)
        {
            return root;
        }

        var combined = Path.GetFullPath(Path.Combine(root, trimmed));
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(combined, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return combined;
    }
}
