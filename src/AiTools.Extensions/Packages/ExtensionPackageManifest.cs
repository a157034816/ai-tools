using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AiTools.Extensions.Packages;

/// <summary>扩展包实现类型。</summary>
public enum ExtensionPackageKind
{
    /// <summary>.NET/C# 程序集。</summary>
    CSharp,

    /// <summary>JavaScript 脚本。</summary>
    JavaScript,

    /// <summary>TypeScript 脚本。</summary>
    TypeScript
}

/// <summary>描述一个磁盘扩展包的清单。</summary>
public sealed class ExtensionPackageManifest
{
    /// <summary>获取扩展包稳定标识。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取扩展包显示名称。</summary>
    public string? DisplayName { get; init; }

    /// <summary>获取扩展包版本。</summary>
    public string? Version { get; init; }

    /// <summary>获取扩展包描述。</summary>
    public string? Description { get; init; }

    /// <summary>获取扩展包实现类型。</summary>
    public ExtensionPackageKind Kind { get; init; }

    /// <summary>获取扩展入口文件或程序集的相对路径。</summary>
    public string? Entry { get; init; }

    /// <summary>获取 C# 项目文件的相对路径。</summary>
    public string? Project { get; init; }

    /// <summary>获取 C# 构建输出目录（相对包根目录）；脚本包不使用此字段，其产物路径来自 <see cref="ScriptExtensionPreparation.ArtifactPath"/>。</summary>
    public string? Output { get; init; }

    /// <summary>获取需要先加载的扩展包名称。</summary>
    public IReadOnlyList<string> Require { get; init; } = Array.Empty<string>();

    /// <summary>获取搜索关键词。</summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    /// <summary>获取未解释的 JSON 元数据。</summary>
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; init; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>获取源代码仓库 URL。</summary>
    public string? Repository { get; init; }

    /// <summary>获取作者。</summary>
    public string? Author { get; init; }

    /// <summary>获取许可证。</summary>
    public string? License { get; init; }

    /// <summary>获取主页 URL。</summary>
    public string? Homepage { get; init; }

    /// <summary>获取所需的最低宿主版本。</summary>
    public string? MinHostVersion { get; init; }

    /// <summary>获取由加载器填入的包目录。</summary>
    public string? RootPath { get; init; }

    /// <summary>获取由加载器填入的清单路径。</summary>
    public string? ManifestPath { get; init; }

    /// <summary>从 JSON 解析清单；格式错误通过诊断返回，不会抛出。</summary>
    /// <param name="json">清单 JSON 文本。</param>
    /// <param name="diagnostics">解析期间产生的诊断。</param>
    /// <param name="manifestPath">清单文件路径，用于诊断和类型推断。</param>
    /// <returns>解析成功时返回清单，否则返回 null。</returns>
    public static ExtensionPackageManifest? Parse(string json, out IReadOnlyList<PluginDiagnostic> diagnostics, string? manifestPath = null)
    {
        var issues = new List<PluginDiagnostic>();
        diagnostics = issues;
        if (json == null)
        {
            issues.Add(Diagnostic("Manifest content is null.", manifestPath));
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                issues.Add(Diagnostic("The extension manifest must be a JSON object.", manifestPath));
                return null;
            }

            var root = document.RootElement;
            string? ReadString(string key)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }

                    if (property.Value.ValueKind != JsonValueKind.Null)
                    {
                        issues.Add(Diagnostic("Field '" + key + "' must be a string.", manifestPath));
                    }
                    return null;
                }
                return null;
            }

            IReadOnlyList<string> ReadStrings(string key)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        issues.Add(Diagnostic("Field '" + key + "' must be an array of strings.", manifestPath));
                        return Array.Empty<string>();
                    }
                    var values = new List<string>();
                    foreach (var value in property.Value.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.String)
                        {
                            issues.Add(Diagnostic("Field '" + key + "' contains a non-string value.", manifestPath));
                            continue;
                        }
                        var text = value.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) values.Add(text!.Trim());
                    }
                    return values;
                }
                return Array.Empty<string>();
            }

            var name = ReadString("name") ?? string.Empty;
            var entry = ReadString("entry");
            var project = ReadString("project");
            var kindText = ReadString("kind");
            var kind = ParseKind(kindText);
            if (kind == null)
            {
                kind = InferKind(entry, project, manifestPath);
                if (!string.IsNullOrWhiteSpace(kindText))
                {
                    issues.Add(Diagnostic("Unknown package kind '" + kindText + "'; inferred " + (kind?.ToString() ?? "CSharp") + ".", manifestPath, PluginDiagnosticKind.Warning));
                }
            }

            var metadata = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "metadata", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var item in property.Value.EnumerateObject()) metadata[item.Name] = item.Value.Clone();
                    }
                    else if (property.Value.ValueKind != JsonValueKind.Null)
                    {
                        issues.Add(Diagnostic("Field 'metadata' must be an object.", manifestPath));
                    }
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add(Diagnostic("Required field 'name' is missing.", manifestPath));
            }

            if (string.IsNullOrWhiteSpace(entry) && (kind == ExtensionPackageKind.JavaScript || kind == ExtensionPackageKind.TypeScript))
            {
                issues.Add(Diagnostic("Required field 'entry' is missing for inferred kind " + kind.Value + ".", manifestPath));
            }

            var manifest = new ExtensionPackageManifest
            {
                Name = name.Trim(),
                DisplayName = ReadString("displayName"),
                Version = ReadString("version"),
                Description = ReadString("description"),
                Kind = kind.GetValueOrDefault(ExtensionPackageKind.CSharp),
                Entry = entry,
                Project = project,
                Output = ReadString("output"),
                Require = ReadStrings("require"),
                Keywords = ReadStrings("keywords"),
                Metadata = metadata,
                Repository = ReadString("repository"),
                Author = ReadString("author"),
                License = ReadString("license"),
                Homepage = ReadString("homepage"),
                MinHostVersion = ReadString("minHostVersion"),
                ManifestPath = manifestPath,
                RootPath = manifestPath == null ? null : Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            };
            diagnostics = issues.ToArray();
            return manifest;
        }
        catch (Exception ex)
        {
            issues.Add(Diagnostic("Failed to parse extension manifest: " + ex.Message, manifestPath));
            diagnostics = issues.ToArray();
            return null;
        }
    }

    private static ExtensionPackageKind? ParseKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.Equals(value, "csharp", StringComparison.OrdinalIgnoreCase)) return ExtensionPackageKind.CSharp;
        if (string.Equals(value, "javascript", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "js", StringComparison.OrdinalIgnoreCase)) return ExtensionPackageKind.JavaScript;
        if (string.Equals(value, "typescript", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "ts", StringComparison.OrdinalIgnoreCase)) return ExtensionPackageKind.TypeScript;
        return null;
    }

    private static ExtensionPackageKind? InferKind(string? entry, string? project, string? manifestPath)
    {
        var root = manifestPath == null ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(project) || Directory.Exists(root) && Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
        {
            return ExtensionPackageKind.CSharp;
        }
        var extension = Path.GetExtension(entry ?? string.Empty);
        if (string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase)) return ExtensionPackageKind.TypeScript;
        if (string.Equals(extension, ".js", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".mjs", StringComparison.OrdinalIgnoreCase)) return ExtensionPackageKind.JavaScript;
        return null;
    }

    private static PluginDiagnostic Diagnostic(string message, string? path, PluginDiagnosticKind kind = PluginDiagnosticKind.Error)
        => new PluginDiagnostic { Kind = kind, Path = path, Message = message };
}
