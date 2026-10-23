using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiTools.Samples.McpAgents;

/// <summary>
/// 读取并修改受支持 agent 的 MCP 配置文件。
/// 每次修改都会在原文件旁创建带时间戳的备份，并以原子方式写入。
/// </summary>
public sealed class AgentConfigEditor
{
    private readonly string _backupDirectory;

    /// <summary>创建将备份存储在指定目录下的编辑器。</summary>
    public AgentConfigEditor(string backupDirectory)
    {
        _backupDirectory = backupDirectory ?? throw new ArgumentNullException(nameof(backupDirectory));
    }

    /// <summary>读取某个 agent 已安装的 MCP 服务 ID。</summary>
    public AgentMcpState Read(AgentAdapter adapter, string configPath)
    {
        if (adapter == null)
        {
            throw new ArgumentNullException(nameof(adapter));
        }

        try
        {
            if (adapter.Kind == AgentKind.Unsupported)
            {
                return new AgentMcpState
                {
                    AgentId = adapter.Id,
                    ConfigExists = false,
                    ConfigPath = configPath,
                    InstalledServers = Array.Empty<string>()
                };
            }

            if (!File.Exists(configPath))
            {
                return new AgentMcpState
                {
                    AgentId = adapter.Id,
                    ConfigExists = false,
                    ConfigPath = configPath,
                    InstalledServers = Array.Empty<string>()
                };
            }

            var servers = adapter.Kind switch
            {
                AgentKind.JsonMcpServers => ReadJson(adapter, configPath, "mcpServers"),
                AgentKind.JsonMcpObject => ReadJson(adapter, configPath, "mcp"),
                AgentKind.TomlMcpServers => ReadToml(adapter, configPath),
                AgentKind.YamlMcpServers => ReadYaml(adapter, configPath),
                _ => Array.Empty<string>()
            };

            return new AgentMcpState
            {
                AgentId = adapter.Id,
                ConfigExists = true,
                ConfigPath = configPath,
                InstalledServers = servers
            };
        }
        catch (Exception ex)
        {
            return new AgentMcpState
            {
                AgentId = adapter.Id,
                ConfigExists = File.Exists(configPath),
                ConfigPath = configPath,
                Error = "读取配置失败：" + ex.Message
            };
        }
    }

    /// <summary>将预设安装到 agent 配置中。</summary>
    public AgentOperationResult Install(AgentAdapter adapter, string configPath, McpPreset preset, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (adapter == null)
        {
            throw new ArgumentNullException(nameof(adapter));
        }

        if (preset == null)
        {
            throw new ArgumentNullException(nameof(preset));
        }

        if (adapter.Kind == AgentKind.Unsupported)
        {
            return AgentOperationResult.Fail($"「{adapter.DisplayName}」不支持自动写入配置，请使用导出的 JSON。");
        }

        try
        {
            switch (adapter.Kind)
            {
                case AgentKind.JsonMcpServers:
                    return UpsertJson(adapter, configPath, "mcpServers", preset, environment);
                case AgentKind.JsonMcpObject:
                    return UpsertJson(adapter, configPath, "mcp", preset, environment);
                case AgentKind.TomlMcpServers:
                    return UpsertToml(adapter, configPath, preset, environment);
                case AgentKind.YamlMcpServers:
                    return UpsertYaml(adapter, configPath, preset, environment);
                default:
                    return AgentOperationResult.Fail($"未知配置类型：{adapter.Kind}");
            }
        }
        catch (Exception ex)
        {
            return AgentOperationResult.Fail($"写入配置失败：{ex.Message}", configPath);
        }
    }

    /// <summary>从 agent 配置中移除预设。</summary>
    public AgentOperationResult Remove(AgentAdapter adapter, string configPath, string presetId)
    {
        if (adapter == null)
        {
            throw new ArgumentNullException(nameof(adapter));
        }

        if (adapter.Kind == AgentKind.Unsupported)
        {
            return AgentOperationResult.Fail($"「{adapter.DisplayName}」不支持自动修改配置。");
        }

        if (!File.Exists(configPath))
        {
            return AgentOperationResult.Fail("配置文件不存在。", configPath);
        }

        try
        {
            switch (adapter.Kind)
            {
                case AgentKind.JsonMcpServers:
                    return RemoveJson(adapter, configPath, "mcpServers", presetId);
                case AgentKind.JsonMcpObject:
                    return RemoveJson(adapter, configPath, "mcp", presetId);
                case AgentKind.TomlMcpServers:
                    return RemoveToml(adapter, configPath, presetId);
                case AgentKind.YamlMcpServers:
                    return RemoveYaml(adapter, configPath, presetId);
                default:
                    return AgentOperationResult.Fail($"未知配置类型：{adapter.Kind}");
            }
        }
        catch (Exception ex)
        {
            return AgentOperationResult.Fail($"修改配置失败：{ex.Message}", configPath);
        }
    }

    // ---------- JSON ----------

    private string[] ReadJson(AgentAdapter adapter, string configPath, string propertyName)
    {
        var root = LoadJson(configPath);
        var container = GetOrCreateObject(root, propertyName);
        return container.Select(pair => pair.Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private AgentOperationResult UpsertJson(AgentAdapter adapter, string configPath, string propertyName, McpPreset preset, IReadOnlyDictionary<string, string>? environment)
    {
        var root = LoadJson(configPath);
        var container = GetOrCreateObject(root, propertyName);
        container[preset.Id] = JsonSerializer.SerializeToNode(McpEntryFormatter.ToStandardEntry(preset, environment));
        return SaveJson(configPath, root, $"已为「{adapter.DisplayName}」添加 MCP「{preset.Id}」。");
    }

    private AgentOperationResult RemoveJson(AgentAdapter adapter, string configPath, string propertyName, string presetId)
    {
        var root = LoadJson(configPath);
        var container = GetOrCreateObject(root, propertyName);
        if (!container.Remove(presetId))
        {
            return AgentOperationResult.Fail($"「{adapter.DisplayName}」中没有名为「{presetId}」的 MCP。", configPath);
        }

        return SaveJson(configPath, root, $"已从「{adapter.DisplayName}」移除 MCP「{presetId}」。");
    }

    // ---------- TOML ----------

    private string[] ReadToml(AgentAdapter adapter, string configPath)
    {
        var text = File.ReadAllText(configPath);
        return TomlMcpEditor.ListServers(text);
    }

    private AgentOperationResult UpsertToml(AgentAdapter adapter, string configPath, McpPreset preset, IReadOnlyDictionary<string, string>? environment)
    {
        var text = File.ReadAllText(configPath);
        var backup = CreateBackup(configPath);
        var updated = TomlMcpEditor.Upsert(text, preset, environment);
        WriteAtomic(configPath, updated);
        return AgentOperationResult.Ok($"已为「{adapter.DisplayName}」添加 MCP「{preset.Id}」。", configPath, backup);
    }

    private AgentOperationResult RemoveToml(AgentAdapter adapter, string configPath, string presetId)
    {
        var text = File.ReadAllText(configPath);
        var (updated, removed) = TomlMcpEditor.Remove(text, presetId);
        if (!removed)
        {
            return AgentOperationResult.Fail($"「{adapter.DisplayName}」中没有名为「{presetId}」的 MCP。", configPath);
        }

        var backup = CreateBackup(configPath);
        WriteAtomic(configPath, updated);
        return AgentOperationResult.Ok($"已从「{adapter.DisplayName}」移除 MCP「{presetId}」。", configPath, backup);
    }

    // ---------- YAML ----------

    private string[] ReadYaml(AgentAdapter adapter, string configPath)
    {
        var root = LoadYaml(configPath);
        if (!root.TryGetValue("mcp_servers", out var value) || !(value is IDictionary<object, object> servers))
        {
            return Array.Empty<string>();
        }

        return servers.Keys.Select(key => key?.ToString() ?? string.Empty)
            .Where(key => key.Length > 0)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private AgentOperationResult UpsertYaml(AgentAdapter adapter, string configPath, McpPreset preset, IReadOnlyDictionary<string, string>? environment)
    {
        var root = LoadYaml(configPath);
        if (!root.TryGetValue("mcp_servers", out var existing) || !(existing is IDictionary<object, object> servers))
        {
            servers = new Dictionary<object, object>();
            root["mcp_servers"] = servers;
        }

        servers[preset.Id] = ToYamlEntry(McpEntryFormatter.ToStandardEntry(preset, environment));
        return SaveYaml(configPath, root, $"已为「{adapter.DisplayName}」添加 MCP「{preset.Id}」。");
    }

    private AgentOperationResult RemoveYaml(AgentAdapter adapter, string configPath, string presetId)
    {
        var root = LoadYaml(configPath);
        if (!root.TryGetValue("mcp_servers", out var existing) || !(existing is IDictionary<object, object> servers) || !servers.Remove(presetId))
        {
            return AgentOperationResult.Fail($"「{adapter.DisplayName}」中没有名为「{presetId}」的 MCP。", configPath);
        }

        return SaveYaml(configPath, root, $"已从「{adapter.DisplayName}」移除 MCP「{presetId}」。");
    }

    // ---------- 辅助方法 ----------

    private static JsonObject LoadJson(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return new JsonObject();
        }

        var text = File.ReadAllText(configPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        return node as JsonObject ?? throw new InvalidOperationException("配置文件顶层必须是 JSON 对象。");
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string propertyName)
    {
        if (root.TryGetPropertyValue(propertyName, out var existing) && existing is JsonObject obj)
        {
            return obj;
        }

        var created = new JsonObject();
        root[propertyName] = created;
        return created;
    }

    private AgentOperationResult SaveJson(string configPath, JsonObject root, string message)
    {
        var backup = File.Exists(configPath) ? CreateBackup(configPath) : null;
        EnsureDirectory(configPath);
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        WriteAtomic(configPath, json);
        return AgentOperationResult.Ok(message, configPath, backup);
    }

    private static Dictionary<object, object> ToYamlEntry(Dictionary<string, object?> entry)
    {
        var result = new Dictionary<object, object>();
        foreach (var pair in entry)
        {
            if (pair.Value is string[] array)
            {
                result[pair.Key] = array.ToList();
            }
            else if (pair.Value is Dictionary<string, string> map)
            {
                var nested = new Dictionary<object, object>();
                foreach (var item in map)
                {
                    nested[item.Key] = item.Value;
                }

                result[pair.Key] = nested;
            }
            else if (pair.Value != null)
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    private static Dictionary<object, object> LoadYaml(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return new Dictionary<object, object>();
        }

        var text = File.ReadAllText(configPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Dictionary<object, object>();
        }

        var deserializer = new DeserializerBuilder().Build();
        var parsed = deserializer.Deserialize<object>(text);
        if (parsed is IDictionary<object, object> map)
        {
            return new Dictionary<object, object>(map);
        }

        return new Dictionary<object, object>();
    }

    private AgentOperationResult SaveYaml(string configPath, Dictionary<object, object> root, string message)
    {
        var backup = File.Exists(configPath) ? CreateBackup(configPath) : null;
        EnsureDirectory(configPath);
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        WriteAtomic(configPath, serializer.Serialize(root));
        return AgentOperationResult.Ok(message, configPath, backup);
    }

    private string? CreateBackup(string configPath)
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            var name = Path.GetFileName(configPath) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".bak";
            var backupPath = Path.Combine(_backupDirectory, name);
            File.Copy(configPath, backupPath, overwrite: true);
            return backupPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void EnsureDirectory(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void WriteAtomic(string configPath, string content)
    {
        var temp = configPath + ".tmp";
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(configPath))
        {
            File.Replace(temp, configPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, configPath);
        }
    }
}
