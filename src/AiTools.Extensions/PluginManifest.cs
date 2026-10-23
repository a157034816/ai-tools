using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AiTools.Extensions;

/// <summary>插件清单（<c>.claude-plugin/plugin.json</c>）的解析结果。</summary>
internal sealed class PluginManifest
{
    public string? Name { get; private set; }

    public string? DisplayName { get; private set; }

    public string? Version { get; private set; }

    public string? Description { get; private set; }

    public string? License { get; private set; }

    public string? Homepage { get; private set; }

    public string? Repository { get; private set; }

    public string? AuthorName { get; private set; }

    public bool DefaultEnabled { get; private set; } = true;

    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>manifest <c>skills</c> 声明的目录；null 表示未声明（仍扫描默认 <c>skills/</c>）。</summary>
    public List<string>? SkillPaths { get; private set; }

    /// <summary>manifest <c>commands</c> 声明的文件或目录；null 表示未声明（扫描默认 <c>commands/</c>）。</summary>
    public List<string>? CommandPaths { get; private set; }

    /// <summary>manifest <c>commands</c> 的对象映射形式（命令名 → 内联或文件）。</summary>
    public List<ManifestCommandEntry> InlineCommands { get; } = new();

    /// <summary>manifest <c>mcpServers</c> 声明的 JSON 文件路径。</summary>
    public List<string> McpServerPaths { get; } = new();

    /// <summary>manifest 内联的 mcpServers 对象。</summary>
    public List<JsonElement> InlineMcpServers { get; } = new();

    /// <summary>manifest 中声明但本框架未实现的组件键（agents、hooks、lspServers 等）。</summary>
    public List<string> UnsupportedComponents { get; } = new();

    /// <summary>解析过程中发现的问题。</summary>
    public List<ExtensionDiagnostic> Diagnostics { get; } = new();

    public static PluginManifest Load(string manifestPath)
    {
        var manifest = new PluginManifest();
        string json;
        try
        {
            json = File.ReadAllText(manifestPath);
        }
        catch (Exception ex)
        {
            manifest.Diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Error,
                Path = manifestPath,
                Message = "Failed to read the plugin manifest: " + ex.Message
            });
            return manifest;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            manifest.Diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Error,
                Path = manifestPath,
                Message = "Invalid JSON in the plugin manifest: " + ex.Message
            });
            return manifest;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                manifest.Diagnostics.Add(new ExtensionDiagnostic
                {
                    Kind = ExtensionDiagnosticKind.Error,
                    Path = manifestPath,
                    Message = "The plugin manifest must be a JSON object."
                });
                return manifest;
            }

            manifest.Name = ReadString(root, "name");
            manifest.DisplayName = ReadString(root, "displayName");
            manifest.Version = ReadString(root, "version");
            manifest.Description = ReadString(root, "description");
            manifest.License = ReadString(root, "license");
            manifest.Homepage = ReadString(root, "homepage");
            manifest.Repository = ReadString(root, "repository");
            manifest.DefaultEnabled = ReadBool(root, "defaultEnabled") ?? true;

            if (root.TryGetProperty("author", out var author))
            {
                if (author.ValueKind == JsonValueKind.Object)
                {
                    manifest.AuthorName = ReadString(author, "name");
                }
                else if (author.ValueKind == JsonValueKind.String)
                {
                    manifest.AuthorName = author.GetString();
                }
            }

            if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in metadata.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    {
                        manifest.Metadata[property.Name] = property.Value.ToString();
                    }
                }
            }

            ReadPathList(root, "skills", manifest, target =>
            {
                manifest.SkillPaths ??= new List<string>();
                manifest.SkillPaths.Add(target);
            });

            if (root.TryGetProperty("commands", out var commands))
            {
                ReadCommands(commands, manifest, manifestPath);
            }

            if (root.TryGetProperty("mcpServers", out var mcpServers))
            {
                ReadMcpServers(mcpServers, manifest, manifestPath);
            }

            foreach (var key in new[] { "agents", "hooks", "lspServers", "outputStyles", "workflows", "themes", "monitors", "channels", "experimental", "settings", "bin", "userConfig", "dependencies" })
            {
                if (root.TryGetProperty(key, out _))
                {
                    manifest.UnsupportedComponents.Add(key);
                }
            }
        }

        return manifest;
    }

    private static void ReadCommands(JsonElement commands, PluginManifest manifest, string manifestPath)
    {
        switch (commands.ValueKind)
        {
            case JsonValueKind.String:
                manifest.CommandPaths ??= new List<string>();
                manifest.CommandPaths.Add(commands.GetString() ?? string.Empty);
                break;

            case JsonValueKind.Array:
                manifest.CommandPaths ??= new List<string>();
                foreach (var item in commands.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        manifest.CommandPaths.Add(item.GetString() ?? string.Empty);
                    }
                }

                break;

            case JsonValueKind.Object:
                foreach (var property in commands.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        manifest.Diagnostics.Add(new ExtensionDiagnostic
                        {
                            Kind = ExtensionDiagnosticKind.Warning,
                            Path = manifestPath,
                            Message = $"Command '{property.Name}' must be an object with 'source' or 'content'."
                        });
                        continue;
                    }

                    var source = ReadString(property.Value, "source");
                    var content = ReadString(property.Value, "content");
                    if ((source == null) == (content == null))
                    {
                        manifest.Diagnostics.Add(new ExtensionDiagnostic
                        {
                            Kind = ExtensionDiagnosticKind.Warning,
                            Path = manifestPath,
                            Message = $"Command '{property.Name}' must set exactly one of 'source' or 'content'."
                        });
                        continue;
                    }

                    manifest.InlineCommands.Add(new ManifestCommandEntry
                    {
                        Name = property.Name,
                        Source = source,
                        Content = content,
                        Description = ReadString(property.Value, "description"),
                        ArgumentHint = ReadString(property.Value, "argumentHint"),
                        Model = ReadString(property.Value, "model"),
                        AllowedTools = ReadStringList(property.Value, "allowedTools")
                    });
                }

                break;
        }
    }

    private static void ReadMcpServers(JsonElement element, PluginManifest manifest, string manifestPath)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var path = element.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    manifest.McpServerPaths.Add(path!);
                }

                break;

            case JsonValueKind.Object:
                manifest.InlineMcpServers.Add(element.Clone());
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var itemPath = item.GetString();
                        if (!string.IsNullOrWhiteSpace(itemPath))
                        {
                            manifest.McpServerPaths.Add(itemPath!);
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        manifest.InlineMcpServers.Add(item.Clone());
                    }
                    else
                    {
                        manifest.Diagnostics.Add(new ExtensionDiagnostic
                        {
                            Kind = ExtensionDiagnosticKind.Warning,
                            Path = manifestPath,
                            Message = "Unsupported mcpServers entry: expected a path or an inline server object."
                        });
                    }
                }

                break;
        }
    }

    private static void ReadPathList(JsonElement root, string property, PluginManifest manifest, Action<string> add)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                add(value!);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            manifest.Diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                Message = $"'{property}' must be a path or an array of paths."
            });
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    add(value!);
                }
            }
        }
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static IReadOnlyList<string> ReadStringList(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }
}

/// <summary>manifest 中 <c>commands</c> 对象映射里的一项。</summary>
internal sealed class ManifestCommandEntry
{
    public string Name { get; init; } = string.Empty;

    public string? Source { get; init; }

    public string? Content { get; init; }

    public string? Description { get; init; }

    public string? ArgumentHint { get; init; }

    public string? Model { get; init; }

    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
}
