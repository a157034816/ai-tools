using System;
using System.Collections.Generic;

namespace AiTools.Skills;

/// <summary>
/// 解析后的 <c>SKILL.md</c> 文件 YAML frontmatter。
/// 仅支持 Agent Skills 使用的子集：顶层标量键、内联列表
/// （<c>[a, b]</c>）、块列表（<c>- item</c>），以及一层嵌套键（例如 <c>metadata.author</c>）。
/// </summary>
public sealed class SkillFrontmatter
{
    private static readonly IReadOnlyList<string> EmptyList = Array.Empty<string>();
    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private SkillFrontmatter(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<string>> lists,
        string body)
    {
        Values = values;
        Lists = lists;
        Body = body;
    }

    /// <summary>获取按名称索引的标量值（不区分大小写）。嵌套键使用点号表示法，例如 <c>metadata.author</c>。</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>获取按名称索引的列表值（不区分大小写）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Lists { get; }

    /// <summary>获取 frontmatter 之后的 Markdown 正文。</summary>
    public string Body { get; }

    /// <summary>获取标量值，不存在时返回 null。</summary>
    public string? GetString(string key) => Values.TryGetValue(key, out var value) ? value : null;

    /// <summary>获取列表值，不存在时返回空列表。</summary>
    public IReadOnlyList<string> GetList(string key) => Lists.TryGetValue(key, out var value) ? value : EmptyList;

    /// <summary>
    /// 解析 <c>SKILL.md</c> 文档。当文档没有 frontmatter 或
    /// frontmatter 无法解析时返回 false。
    /// </summary>
    public static bool TryParse(string content, out SkillFrontmatter? frontmatter, out string? error)
    {
        frontmatter = null;
        error = null;

        if (content == null)
        {
            error = "The skill file is empty.";
            return false;
        }

        var text = content.TrimStart('﻿');
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var index = 0;

        // 跳过起始围栏前的空行。
        while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index]))
        {
            index++;
        }

        if (index >= lines.Length || !string.Equals(lines[index].Trim(), "---", StringComparison.Ordinal))
        {
            error = "Missing YAML frontmatter: the file must start with '---'.";
            return false;
        }

        var start = ++index;
        var end = -1;
        for (var i = start; i < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), "---", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            error = "Unterminated YAML frontmatter: no closing '---' was found.";
            return false;
        }

        Dictionary<string, string> values;
        Dictionary<string, IReadOnlyList<string>> lists;
        try
        {
            if (!TryParseBlock(lines, start, end, out values, out lists, out error))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            error = "Invalid frontmatter: " + ex.Message;
            return false;
        }

        var body = string.Join("\n", lines, end + 1, lines.Length - end - 1).Trim();
        frontmatter = new SkillFrontmatter(values, lists, body);
        return true;
    }

    private static bool TryParseBlock(
        string[] lines,
        int start,
        int end,
        out Dictionary<string, string> values,
        out Dictionary<string, IReadOnlyList<string>> lists,
        out string? error)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var listResults = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        lists = listResults;
        error = null;

        var nestedPrefix = (string?)null;
        string? pendingListKey = null;
        var pendingList = new List<string>();
        var pendingListIndent = -1;

        void FlushList()
        {
            if (pendingListKey != null)
            {
                listResults[pendingListKey] = pendingList.ToArray();
                pendingListKey = null;
                pendingList.Clear();
                pendingListIndent = -1;
            }
        }

        for (var i = start; i < end; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var indent = CountIndent(raw);

            // 块列表项。
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || string.Equals(trimmed, "-", StringComparison.Ordinal))
            {
                var item = trimmed.Length > 2 ? trimmed.Substring(2).Trim() : string.Empty;
                if (pendingListKey != null && indent >= pendingListIndent)
                {
                    pendingList.Add(Unquote(item));
                    continue;
                }

                FlushList();
                error = $"List item without a key on line {i + 1}.";
                return false;
            }

            FlushList();

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                error = $"Expected 'key: value' on line {i + 1}.";
                return false;
            }

            var key = trimmed.Substring(0, separator).Trim();
            var value = trimmed.Substring(separator + 1).Trim();

            if (indent > 0 && nestedPrefix != null)
            {
                key = nestedPrefix + "." + key;
            }
            else if (indent == 0)
            {
                nestedPrefix = null;
            }

            if (value.Length == 0)
            {
                // 可能是嵌套块或块列表；根据下一个非空行来决定。
                var next = FindNextContentLine(lines, i + 1, end);
                if (next >= 0 && CountIndent(lines[next]) > indent &&
                    lines[next].Trim().StartsWith("- ", StringComparison.Ordinal))
                {
                    pendingListKey = key;
                    pendingListIndent = CountIndent(lines[next]);
                    continue;
                }

                if (indent == 0)
                {
                    nestedPrefix = key;
                    continue;
                }

                values[key] = string.Empty;
                continue;
            }

            if (value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal))
            {
                lists[key] = SplitInlineList(value);
                continue;
            }

            values[key] = Unquote(value);
        }

        FlushList();
        return true;
    }

    private static int FindNextContentLine(string[] lines, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) && !lines[i].TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                count++;
            }
            else if (c == '\t')
            {
                count += 2;
            }
            else
            {
                break;
            }
        }

        return count;
    }

    private static IReadOnlyList<string> SplitInlineList(string value)
    {
        var inner = value.Substring(1, value.Length - 2);
        var items = new List<string>();
        foreach (var part in inner.Split(','))
        {
            var item = Unquote(part.Trim());
            if (item.Length > 0)
            {
                items.Add(item);
            }
        }

        return items.ToArray();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[value.Length - 1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return value.Substring(1, value.Length - 2);
            }
        }

        return value;
    }
}
