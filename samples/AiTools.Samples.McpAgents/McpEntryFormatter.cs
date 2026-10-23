using System;
using System.Collections.Generic;
using System.Linq;

namespace AiTools.Samples.McpAgents;

/// <summary>
/// 将预设转换为各 agent 所需的 MCP 服务条目结构，并生成可移植的 JSON 导出。
/// </summary>
public static class McpEntryFormatter
{
    /// <summary>构建大多数 agent（Claude/Gemini/OMP/Pi 风格）使用的标准 MCP 服务条目。</summary>
    public static Dictionary<string, object?> ToStandardEntry(McpPreset preset, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (preset == null)
        {
            throw new ArgumentNullException(nameof(preset));
        }

        var entry = new Dictionary<string, object?>();

        if (preset.IsStdio)
        {
            entry["type"] = "stdio";
            entry["command"] = preset.Command;
            if (preset.Arguments.Count > 0)
            {
                entry["args"] = preset.Arguments.ToArray();
            }

            var env = MergeEnvironment(preset.Environment, environment);
            if (env.Count > 0)
            {
                entry["env"] = env;
            }
        }
        else
        {
            entry["type"] = preset.Transport;
            entry["url"] = preset.Url;
            var headers = MergeEnvironment(preset.Headers, null);
            if (headers.Count > 0)
            {
                entry["headers"] = headers;
            }
        }

        return entry;
    }

    /// <summary>构建基于 TOML 的 agent（Codex/Grok）使用的最小条目：不含 <c>type</c> 键。</summary>
    public static Dictionary<string, object?> ToTomlEntry(McpPreset preset, IReadOnlyDictionary<string, string>? environment = null)
    {
        var entry = ToStandardEntry(preset, environment);
        entry.Remove("type");
        return entry;
    }

    /// <summary>为不支持的 agent 构建顶层 <c>{ "mcpServers": { ... } }</c> 文档。</summary>
    public static string BuildPortableJson(McpPreset preset, IReadOnlyDictionary<string, string>? environment = null)
    {
        var document = new Dictionary<string, object?>
        {
            ["mcpServers"] = new Dictionary<string, object?>
            {
                [preset.Id] = ToStandardEntry(preset, environment)
            }
        };

        return System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>构建包含多个预设的可移植 JSON 文档。</summary>
    public static string BuildPortableJson(IEnumerable<McpPreset> presets)
    {
        var servers = new Dictionary<string, object?>();
        foreach (var preset in presets)
        {
            servers[preset.Id] = ToStandardEntry(preset);
        }

        var document = new Dictionary<string, object?> { ["mcpServers"] = servers };
        return System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    /// <summary>判断是否已提供所有必需的环境变量。</summary>
    public static bool HasRequiredEnvironment(McpPreset preset, IReadOnlyDictionary<string, string>? environment)
    {
        foreach (var key in preset.RequiredEnvironment.Keys)
        {
            if (environment == null ||
                !environment.TryGetValue(key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>列出缺失的必需环境变量名称。</summary>
    public static IReadOnlyList<string> GetMissingEnvironment(McpPreset preset, IReadOnlyDictionary<string, string>? environment)
    {
        var missing = new List<string>();
        foreach (var key in preset.RequiredEnvironment.Keys)
        {
            if (environment == null ||
                !environment.TryGetValue(key, out var value) ||
                string.IsNullOrWhiteSpace(value))
            {
                missing.Add(key);
            }
        }

        return missing;
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
