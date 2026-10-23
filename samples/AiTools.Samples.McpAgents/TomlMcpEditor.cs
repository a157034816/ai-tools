using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AiTools.Samples.McpAgents;

/// <summary>
/// 针对 <c>[mcp_servers.&lt;name&gt;]</c> 表的最小 TOML 编辑器。它原样保留其他所有内容，
/// 仅重写属于目标服务的那一段。处理 Codex/Grok 使用的子集：字符串、字符串数组、布尔值、
/// 数字以及嵌套的 <c>[mcp_servers.name.env]</c> 表。
/// </summary>
internal static class TomlMcpEditor
{
    private static readonly Regex HeaderPattern = new(
        @"^\s*\[\s*(?<table>[A-Za-z0-9_\-\.]+)\s*(?:\.(?<name>""[^""]*""|'[^']*'|[A-Za-z0-9_\-]+))?\s*\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>列出已配置的服务 ID。</summary>
    public static string[] ListServers(string text)
    {
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(text))
        {
            var match = HeaderPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var table = match.Groups["table"].Value;
            var name = match.Groups["name"].Success ? Unquote(match.Groups["name"].Value) : null;

            if (string.Equals(table, "mcp_servers", StringComparison.Ordinal) && !string.IsNullOrEmpty(name))
            {
                ids.Add(name!);
            }
            else if (table.StartsWith("mcp_servers.", StringComparison.Ordinal))
            {
                var remainder = table.Substring("mcp_servers.".Length);
                var first = remainder.Split('.')[0];
                if (first.Length > 0)
                {
                    ids.Add(Unquote(first));
                }
            }
        }

        return ids.ToArray();
    }

    /// <summary>插入或替换一个服务表，返回更新后的文档。</summary>
    public static string Upsert(string text, McpPreset preset, IReadOnlyDictionary<string, string>? environment)
    {
        var (withoutExisting, _) = Remove(text, preset.Id);
        var builder = new StringBuilder(withoutExisting.TrimEnd());
        if (builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        var entry = McpEntryFormatter.ToTomlEntry(preset, environment);
        builder.Append("[mcp_servers.").Append(QuoteIfNeeded(preset.Id)).AppendLine("]");

        WriteScalar(builder, "type", preset.Transport);
        if (preset.IsStdio)
        {
            WriteScalar(builder, "command", preset.Command);
            WriteArray(builder, "args", preset.Arguments);
        }
        else
        {
            WriteScalar(builder, "url", preset.Url);
        }

        var env = preset.IsStdio
            ? MergeEnvironment(preset.Environment, environment)
            : MergeEnvironment(preset.Headers, null);
        if (env.Count > 0)
        {
            builder.AppendLine();
            builder.Append("[mcp_servers.").Append(QuoteIfNeeded(preset.Id)).AppendLine(".env]");
            foreach (var pair in env)
            {
                WriteScalar(builder, pair.Key, pair.Value);
            }
        }

        _ = entry; // entry 仅用于参考/校验。
        return builder.ToString();
    }

    /// <summary>移除一个服务表及其嵌套子表。</summary>
    public static (string Text, bool Removed) Remove(string text, string presetId)
    {
        var lines = SplitLines(text).ToList();
        var kept = new List<string>();
        var removed = false;
        var skipping = false;

        foreach (var line in lines)
        {
            var match = HeaderPattern.Match(line);
            if (match.Success)
            {
                var table = match.Groups["table"].Value;
                var name = match.Groups["name"].Success ? Unquote(match.Groups["name"].Value) : null;
                var belongsToTarget = TableBelongsTo(table, name, presetId);
                skipping = belongsToTarget;
                if (belongsToTarget)
                {
                    removed = true;
                    continue;
                }
            }

            if (!skipping)
            {
                kept.Add(line);
            }
        }

        // 清理文件尾部多余空行，但保留一个换行（TOML 惯例）。
        while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[kept.Count - 1]))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        var result = string.Join(Environment.NewLine, kept);
        return (result.Length == 0 ? string.Empty : result + Environment.NewLine, removed);
    }

    private static bool TableBelongsTo(string table, string? name, string presetId)
    {
        if (string.Equals(table, "mcp_servers", StringComparison.Ordinal))
        {
            return string.Equals(name, presetId, StringComparison.Ordinal);
        }

        if (table.StartsWith("mcp_servers.", StringComparison.Ordinal))
        {
            var remainder = table.Substring("mcp_servers.".Length);
            var first = remainder.Split('.')[0];
            return string.Equals(Unquote(first), presetId, StringComparison.Ordinal);
        }

        return false;
    }

    private static IEnumerable<string> SplitLines(string text)
        => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => line.TrimEnd());

    private static void WriteScalar(StringBuilder builder, string key, string? value)
    {
        if (value == null)
        {
            return;
        }

        builder.Append(QuoteIfNeeded(key)).Append(" = ").AppendLine(Quote(value));
    }

    private static void WriteArray(StringBuilder builder, string key, IReadOnlyList<string> values)
    {
        if (values == null || values.Count == 0)
        {
            return;
        }

        builder.Append(QuoteIfNeeded(key)).Append(" = [ ");
        builder.Append(string.Join(", ", values.Select(Quote)));
        builder.AppendLine(" ]");
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string QuoteIfNeeded(string key)
        => key.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-') ? key : Quote(key);

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') || (value[0] == '\'' && value[value.Length - 1] == '\'')))
        {
            return value.Substring(1, value.Length - 2);
        }

        return value;
    }

    private static Dictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string> baseValues,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in baseValues)
        {
            result[pair.Key] = pair.Value;
        }

        if (overrides != null)
        {
            foreach (var pair in overrides)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    result[pair.Key] = pair.Value;
                }
            }
        }

        return result;
    }
}
