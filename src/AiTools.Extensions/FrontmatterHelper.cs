using System;
using System.Collections.Generic;
using System.Linq;
using AiTools.Skills;

namespace AiTools.Extensions;

/// <summary>读取 <see cref="SkillFrontmatter"/> 的便捷方法：frontmatter 的标量、内联列表与块列表统一处理。</summary>
internal static class FrontmatterHelper
{
    /// <summary>读取字符串列表：优先块列表/内联列表，其次按空白与逗号拆分标量。</summary>
    public static IReadOnlyList<string> GetListOrSplit(SkillFrontmatter? frontmatter, string key)
    {
        if (frontmatter == null)
        {
            return Array.Empty<string>();
        }

        var list = frontmatter.GetList(key);
        if (list.Count > 0)
        {
            return list;
        }

        return Split(frontmatter.GetString(key));
    }

    /// <summary>按逗号、空白拆分工具/参数列表。</summary>
    public static IReadOnlyList<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value!
            .Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();
    }

    /// <summary>读取布尔值，接受 true/false/yes/no/on/off/1/0。</summary>
    public static bool? GetBool(SkillFrontmatter? frontmatter, string key)
    {
        var raw = frontmatter?.GetString(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        switch (raw!.Trim().ToLowerInvariant())
        {
            case "true":
            case "yes":
            case "on":
            case "1":
                return true;
            case "false":
            case "no":
            case "off":
            case "0":
                return false;
            default:
                return null;
        }
    }

    /// <summary>读取 frontmatter 正文之外的描述，回退到正文首个非空行。</summary>
    public static string Describe(string? description, string body)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description!.Trim();
        }

        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('#').Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            return trimmed.Length <= 200 ? trimmed : trimmed.Substring(0, 200) + "…";
        }

        return string.Empty;
    }
}
