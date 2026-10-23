using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiTools.Skills;

namespace AiTools.Extensions;

/// <summary>
/// 按 Claude Code 的插件目录约定加载单个插件：
/// <c>.claude-plugin/plugin.json</c>、<c>skills/&lt;name&gt;/SKILL.md</c>、
/// <c>commands/*.md</c>、<c>.mcp.json</c>。加载过程永不抛出异常，
/// 问题通过 <see cref="AiToolsPlugin.Diagnostics"/> 返回。
/// </summary>
public static class PluginLoader
{
    private static readonly Regex NamePattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InvalidCommandChars = new("[^a-zA-Z0-9_.:-]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>判断目录是否像一个插件根（含清单、根 SKILL.md 或组件目录）。</summary>
    public static bool IsPluginDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, ".claude-plugin", "plugin.json"))
               || File.Exists(Path.Combine(directory, "SKILL.md"))
               || Directory.Exists(Path.Combine(directory, "skills"))
               || Directory.Exists(Path.Combine(directory, "commands"));
    }

    /// <summary>加载单个插件目录。永不抛出异常。</summary>
    /// <param name="directory">插件根目录。</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> 为空。</exception>
    public static AiToolsPlugin Load(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A plugin directory is required.", nameof(directory));
        }

        var root = Path.GetFullPath(directory);
        var diagnostics = new List<ExtensionDiagnostic>();

        if (!Directory.Exists(root))
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Error,
                Path = root,
                Message = "The plugin directory does not exist."
            });
            return Build(root, null, null, diagnostics, Array.Empty<PluginCommand>(), Array.Empty<PluginSkill>(), Array.Empty<PluginMcpServer>());
        }

        var manifestPath = Path.Combine(root, ".claude-plugin", "plugin.json");
        PluginManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            manifest = PluginManifest.Load(manifestPath);
            diagnostics.AddRange(manifest.Diagnostics);
        }
        else
        {
            manifestPath = null;
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Info,
                Path = root,
                Message = "No .claude-plugin/plugin.json found; loading by directory convention."
            });
        }

        var pluginName = SanitizePluginName(manifest?.Name ?? new DirectoryInfo(root).Name);
        if (!NamePattern.IsMatch(pluginName))
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = manifestPath ?? root,
                Message = "Plugin names should be kebab-case (lowercase letters, digits and single hyphens)."
            });
        }

        if (manifest != null)
        {
            foreach (var unsupported in manifest.UnsupportedComponents.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.Ordinal))
            {
                diagnostics.Add(new ExtensionDiagnostic
                {
                    Kind = ExtensionDiagnosticKind.Info,
                    PluginName = pluginName,
                    Path = manifestPath,
                    Message = $"Component '{unsupported}' is not supported by AiTools and was ignored."
                });
            }
        }

        var skills = LoadSkills(root, pluginName, manifest, diagnostics);
        var commands = LoadCommands(root, pluginName, manifest, skills, diagnostics);
        var mcpServers = LoadMcpServers(root, pluginName, manifest, diagnostics);

        return Build(root, manifestPath, manifest, diagnostics, commands, skills, mcpServers, pluginName);
    }

    /// <summary>把任意字符串规范化为可用的插件标识（小写、连字符）。</summary>
    public static string SanitizePluginName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "plugin";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim())
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

        var value = builder.ToString().Trim('-');
        while (value.Contains("--"))
        {
            value = value.Replace("--", "-");
        }

        return value.Length == 0 ? "plugin" : value;
    }

    /// <summary>把 manifest 中的相对路径解析到插件目录内；越界返回 null。</summary>
    internal static string? ResolvePath(string root, string declared)
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

    private static AiToolsPlugin Build(
        string root,
        string? manifestPath,
        PluginManifest? manifest,
        List<ExtensionDiagnostic> diagnostics,
        IReadOnlyList<PluginCommand> commands,
        IReadOnlyList<PluginSkill> skills,
        IReadOnlyList<PluginMcpServer> mcpServers,
        string? nameOverride = null)
    {
        var name = nameOverride ?? SanitizePluginName(manifest?.Name ?? new DirectoryInfo(root).Name);

        return new AiToolsPlugin
        {
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(manifest?.DisplayName) ? name : manifest!.DisplayName!,
            Version = manifest?.Version,
            Description = manifest?.Description,
            License = manifest?.License,
            Homepage = manifest?.Homepage,
            Repository = manifest?.Repository,
            Author = manifest?.AuthorName,
            DefaultEnabled = manifest?.DefaultEnabled ?? true,
            Metadata = manifest?.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            RootPath = root,
            ManifestPath = manifestPath,
            Commands = commands,
            Skills = skills,
            McpServers = mcpServers,
            Diagnostics = diagnostics
        };
    }

    private static List<PluginSkill> LoadSkills(
        string root,
        string pluginName,
        PluginManifest? manifest,
        List<ExtensionDiagnostic> diagnostics)
    {
        var skills = new List<PluginSkill>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();

        void AddDirectory(string path)
        {
            if (!directories.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(path);
            }
        }

        var defaultSkills = Path.Combine(root, "skills");
        if (Directory.Exists(defaultSkills))
        {
            AddDirectory(defaultSkills);
        }

        if (manifest?.SkillPaths != null)
        {
            foreach (var declared in manifest.SkillPaths)
            {
                var resolved = ResolvePath(root, declared);
                if (resolved == null)
                {
                    diagnostics.Add(new ExtensionDiagnostic
                    {
                        Kind = ExtensionDiagnosticKind.Warning,
                        PluginName = pluginName,
                        Path = declared,
                        Message = "Skill path is outside the plugin directory and was ignored."
                    });
                    continue;
                }

                if (!Directory.Exists(resolved) && !File.Exists(resolved))
                {
                    diagnostics.Add(new ExtensionDiagnostic
                    {
                        Kind = ExtensionDiagnosticKind.Warning,
                        PluginName = pluginName,
                        Path = resolved,
                        Message = "Skill path does not exist."
                    });
                    continue;
                }

                AddDirectory(resolved);
            }
        }

        foreach (var directory in directories)
        {
            // 目录本身就是一个 skill（含 SKILL.md）。
            var directSkill = Path.Combine(directory, "SKILL.md");
            if (File.Exists(directSkill))
            {
                var skill = TryLoadSkill(directSkill, root, pluginName, diagnostics, isPluginRootSkill: string.Equals(directory, root, StringComparison.OrdinalIgnoreCase));
                if (skill != null && seen.Add(skill.Name))
                {
                    skills.Add(skill);
                }

                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var skillFile = Path.Combine(child, "SKILL.md");
                if (!File.Exists(skillFile))
                {
                    continue;
                }

                var skill = TryLoadSkill(skillFile, root, pluginName, diagnostics, isPluginRootSkill: false);
                if (skill == null)
                {
                    continue;
                }

                if (!seen.Add(skill.Name))
                {
                    diagnostics.Add(new ExtensionDiagnostic
                    {
                        Kind = ExtensionDiagnosticKind.Warning,
                        PluginName = pluginName,
                        Path = skillFile,
                        Message = $"Duplicate skill name '{skill.Name}'. The first definition wins."
                    });
                    continue;
                }

                skills.Add(skill);
            }
        }

        // 插件根目录的单个 SKILL.md：没有 skills/ 目录也没有 manifest skills 键时生效。
        if (skills.Count == 0 && manifest?.SkillPaths == null && !Directory.Exists(defaultSkills))
        {
            var rootSkill = Path.Combine(root, "SKILL.md");
            if (File.Exists(rootSkill))
            {
                var skill = TryLoadSkill(rootSkill, root, pluginName, diagnostics, isPluginRootSkill: true);
                if (skill != null && seen.Add(skill.Name))
                {
                    skills.Add(skill);
                }
            }
        }

        skills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return skills;
    }

    private static PluginSkill? TryLoadSkill(
        string skillFile,
        string pluginRoot,
        string pluginName,
        List<ExtensionDiagnostic> diagnostics,
        bool isPluginRootSkill)
    {
        string content;
        try
        {
            content = File.ReadAllText(skillFile, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = skillFile,
                Message = "Failed to read SKILL.md: " + ex.Message
            });
            return null;
        }

        var parsed = SkillFrontmatter.TryParse(content, out var frontmatter, out var error);
        if (!parsed)
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = skillFile,
                Message = (error ?? "Invalid frontmatter.") + " The whole file is treated as instructions."
            });
        }

        var directory = Path.GetDirectoryName(skillFile) ?? pluginRoot;
        var fallbackName = isPluginRootSkill ? pluginName : Path.GetFileName(directory);
        var name = SanitizeCommandSegment(frontmatter?.GetString("name") ?? fallbackName);
        if (name.Length == 0)
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = skillFile,
                Message = "Skill has no usable name and was skipped."
            });
            return null;
        }

        var body = parsed ? frontmatter!.Body : content;
        var description = FrontmatterHelper.Describe(frontmatter?.GetString("description"), body);
        if (description.Length == 0)
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = skillFile,
                Message = "Skill has no description; the model will not know when to use it."
            });
            description = name;
        }

        var allowedTools = FrontmatterHelper.GetListOrSplit(frontmatter, "allowed-tools");
        var userInvocable = FrontmatterHelper.GetBool(frontmatter, "user-invocable") ?? true;
        var disableModelInvocation = FrontmatterHelper.GetBool(frontmatter, "disable-model-invocation") ?? false;
        var qualified = pluginName + ":" + name;

        PluginCommand? command = null;
        if (userInvocable)
        {
            command = BuildCommand(
                qualified,
                name,
                pluginName,
                description,
                body,
                skillFile,
                pluginRoot,
                directory,
                frontmatter,
                allowedTools,
                disableModelInvocation);
        }

        return new PluginSkill
        {
            Name = name,
            QualifiedName = qualified,
            PluginName = pluginName,
            Description = description,
            Instructions = body,
            DirectoryPath = directory,
            SkillFilePath = skillFile,
            PluginRoot = pluginRoot,
            AllowedTools = allowedTools,
            Command = command
        };
    }

    private static PluginCommand BuildCommand(
        string qualifiedName,
        string shortName,
        string pluginName,
        string description,
        string body,
        string sourcePath,
        string pluginRoot,
        string? skillDirectory,
        SkillFrontmatter? frontmatter,
        IReadOnlyList<string> allowedTools,
        bool disableModelInvocation)
        => new()
        {
            Name = qualifiedName,
            ShortName = shortName,
            PluginName = pluginName,
            Description = description,
            ArgumentHint = frontmatter?.GetString("argument-hint"),
            Body = body,
            SourcePath = sourcePath,
            PluginRoot = pluginRoot,
            SkillDirectory = skillDirectory,
            AllowedTools = allowedTools,
            DisallowedTools = FrontmatterHelper.GetListOrSplit(frontmatter, "disallowed-tools"),
            ArgumentNames = FrontmatterHelper.GetListOrSplit(frontmatter, "arguments"),
            Model = frontmatter?.GetString("model"),
            DisableModelInvocation = disableModelInvocation,
            UserInvocable = true
        };

    private static List<PluginCommand> LoadCommands(
        string root,
        string pluginName,
        PluginManifest? manifest,
        IReadOnlyList<PluginSkill> skills,
        List<ExtensionDiagnostic> diagnostics)
    {
        var commands = new List<PluginCommand>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in skills)
        {
            if (skill.Command != null && names.Add(skill.Command.Name))
            {
                commands.Add(skill.Command);
            }
        }

        var sources = manifest?.CommandPaths;
        if (sources == null && Directory.Exists(Path.Combine(root, "commands")))
        {
            sources = new List<string> { "./commands" };
        }

        if (sources != null)
        {
            foreach (var declared in sources)
            {
                var resolved = ResolvePath(root, declared);
                if (resolved == null)
                {
                    diagnostics.Add(new ExtensionDiagnostic
                    {
                        Kind = ExtensionDiagnosticKind.Warning,
                        PluginName = pluginName,
                        Path = declared,
                        Message = "Command path is outside the plugin directory and was ignored."
                    });
                    continue;
                }

                if (File.Exists(resolved))
                {
                    var command = TryLoadCommandFile(resolved, Path.GetFileNameWithoutExtension(resolved), root, pluginName, diagnostics);
                    if (command != null && names.Add(command.Name))
                    {
                        commands.Add(command);
                    }

                    continue;
                }

                if (Directory.Exists(resolved))
                {
                    foreach (var file in Directory.EnumerateFiles(resolved, "*.md", SearchOption.AllDirectories)
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        var relative = file.Substring(resolved.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        var segments = relative.Replace(Path.DirectorySeparatorChar, '/').Split('/')
                            .Where(segment => segment.Length > 0)
                            .ToArray();
                        if (segments.Length == 0)
                        {
                            continue;
                        }

                        segments[segments.Length - 1] = Path.GetFileNameWithoutExtension(segments[segments.Length - 1]);
                        var shortName = SanitizeCommandSegment(string.Join(":", segments));
                        var command = TryLoadCommandFile(file, shortName, root, pluginName, diagnostics);
                        if (command != null && names.Add(command.Name))
                        {
                            commands.Add(command);
                        }
                    }

                    continue;
                }

                diagnostics.Add(new ExtensionDiagnostic
                {
                    Kind = ExtensionDiagnosticKind.Warning,
                    PluginName = pluginName,
                    Path = resolved,
                    Message = "Command path does not exist."
                });
            }
        }

        if (manifest != null)
        {
            foreach (var entry in manifest.InlineCommands)
            {
                var shortName = SanitizeCommandSegment(entry.Name);
                if (shortName.Length == 0)
                {
                    continue;
                }

                string? body = entry.Content;
                var sourcePath = string.Empty;
                if (body == null && entry.Source != null)
                {
                    var resolved = ResolvePath(root, entry.Source);
                    if (resolved == null || !File.Exists(resolved))
                    {
                        diagnostics.Add(new ExtensionDiagnostic
                        {
                            Kind = ExtensionDiagnosticKind.Warning,
                            PluginName = pluginName,
                            Path = entry.Source,
                            Message = $"Inline command '{entry.Name}' points at a missing file."
                        });
                        continue;
                    }

                    try
                    {
                        body = File.ReadAllText(resolved, Encoding.UTF8);
                        sourcePath = resolved;
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add(new ExtensionDiagnostic
                        {
                            Kind = ExtensionDiagnosticKind.Warning,
                            PluginName = pluginName,
                            Path = resolved,
                            Message = "Failed to read command file: " + ex.Message
                        });
                        continue;
                    }
                }

                var qualified = pluginName + ":" + shortName;
                if (!names.Add(qualified))
                {
                    continue;
                }

                commands.Add(new PluginCommand
                {
                    Name = qualified,
                    ShortName = shortName,
                    PluginName = pluginName,
                    Description = FrontmatterHelper.Describe(entry.Description, body ?? string.Empty),
                    ArgumentHint = entry.ArgumentHint,
                    Body = body ?? string.Empty,
                    SourcePath = sourcePath,
                    PluginRoot = root,
                    AllowedTools = entry.AllowedTools,
                    Model = entry.Model
                });
            }
        }

        commands.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return commands;
    }

    private static PluginCommand? TryLoadCommandFile(
        string filePath,
        string shortName,
        string pluginRoot,
        string pluginName,
        List<ExtensionDiagnostic> diagnostics)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = filePath,
                Message = "Failed to read the command file: " + ex.Message
            });
            return null;
        }

        var normalized = shortName.Trim(':').Trim();
        if (normalized.Length == 0)
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = filePath,
                Message = "Command has an empty name and was skipped."
            });
            return null;
        }

        // frontmatter 可选：没有就以整个文件作为正文。
        SkillFrontmatter? frontmatter = null;
        var body = content;
        if (LooksLikeFrontmatter(content))
        {
            if (SkillFrontmatter.TryParse(content, out var parsed, out var error))
            {
                frontmatter = parsed;
                body = parsed!.Body;
            }
            else
            {
                diagnostics.Add(new ExtensionDiagnostic
                {
                    Kind = ExtensionDiagnosticKind.Warning,
                    PluginName = pluginName,
                    Path = filePath,
                    Message = (error ?? "Invalid frontmatter.") + " The whole file is treated as the command body."
                });
            }
        }

        var description = FrontmatterHelper.Describe(frontmatter?.GetString("description"), body);

        return new PluginCommand
        {
            Name = pluginName + ":" + normalized,
            ShortName = normalized,
            PluginName = pluginName,
            Description = description,
            ArgumentHint = frontmatter?.GetString("argument-hint"),
            Body = body,
            SourcePath = filePath,
            PluginRoot = pluginRoot,
            AllowedTools = FrontmatterHelper.GetListOrSplit(frontmatter, "allowed-tools"),
            DisallowedTools = FrontmatterHelper.GetListOrSplit(frontmatter, "disallowed-tools"),
            ArgumentNames = FrontmatterHelper.GetListOrSplit(frontmatter, "arguments"),
            Model = frontmatter?.GetString("model"),
            DisableModelInvocation = FrontmatterHelper.GetBool(frontmatter, "disable-model-invocation") ?? false,
            UserInvocable = FrontmatterHelper.GetBool(frontmatter, "user-invocable") ?? true
        };
    }

    private static List<PluginMcpServer> LoadMcpServers(
        string root,
        string pluginName,
        PluginManifest? manifest,
        List<ExtensionDiagnostic> diagnostics)
    {
        var servers = new List<PluginMcpServer>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddMap(JsonElement map, string? source)
        {
            if (map.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new ExtensionDiagnostic
                {
                    Kind = ExtensionDiagnosticKind.Warning,
                    PluginName = pluginName,
                    Path = source,
                    Message = "MCP configuration must be a JSON object."
                });
                return;
            }

            var serversElement = map;
            if (map.TryGetProperty("mcpServers", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                serversElement = nested;
            }

            foreach (var property in serversElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "mcpServers", StringComparison.Ordinal) ||
                    string.Equals(property.Name, "$schema", StringComparison.Ordinal))
                {
                    continue;
                }

                var server = TryParseServer(property.Name, property.Value, root, pluginName, source, diagnostics);
                if (server == null)
                {
                    continue;
                }

                // 后声明的服务器覆盖先声明的同名服务器。
                servers.RemoveAll(existing => string.Equals(existing.QualifiedName, server.QualifiedName, StringComparison.OrdinalIgnoreCase));
                names.Add(server.Name);
                servers.Add(server);
            }
        }

        var defaultMcp = Path.Combine(root, ".mcp.json");
        if (File.Exists(defaultMcp))
        {
            var element = TryReadJson(defaultMcp, pluginName, diagnostics);
            if (element.HasValue)
            {
                AddMap(element.Value, defaultMcp);
            }
        }

        if (manifest != null)
        {
            foreach (var declared in manifest.McpServerPaths)
            {
                var resolved = ResolvePath(root, declared);
                if (resolved == null || !File.Exists(resolved))
                {
                    diagnostics.Add(new ExtensionDiagnostic
                    {
                        Kind = ExtensionDiagnosticKind.Warning,
                        PluginName = pluginName,
                        Path = declared,
                        Message = "MCP server config file was not found."
                    });
                    continue;
                }

                var element = TryReadJson(resolved, pluginName, diagnostics);
                if (element.HasValue)
                {
                    AddMap(element.Value, resolved);
                }
            }

            foreach (var inline in manifest.InlineMcpServers)
            {
                AddMap(inline, null);
            }
        }

        servers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return servers;
    }

    private static PluginMcpServer? TryParseServer(
        string name,
        JsonElement element,
        string root,
        string pluginName,
        string? source,
        List<ExtensionDiagnostic> diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = source,
                Message = $"MCP server '{name}' must be an object."
            });
            return null;
        }

        var type = GetOptionalString(element, "type")?.ToLowerInvariant();
        var url = GetOptionalString(element, "url");
        var command = GetOptionalString(element, "command");

        PluginMcpTransportKind transport;
        switch (type)
        {
            case "sse":
                transport = PluginMcpTransportKind.Sse;
                break;
            case "http":
            case "streamable-http":
                transport = PluginMcpTransportKind.Http;
                break;
            case "stdio":
                transport = PluginMcpTransportKind.Stdio;
                break;
            default:
                transport = url != null ? PluginMcpTransportKind.Http : PluginMcpTransportKind.Stdio;
                break;
        }

        var arguments = new List<string>();
        if (element.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var argument in argsElement.EnumerateArray())
            {
                if (argument.ValueKind == JsonValueKind.String)
                {
                    arguments.Add(Expand(argument.GetString()!, root));
                }
            }
        }

        var environment = ReadStringMap(element, "env", root);
        var headers = ReadStringMap(element, "headers", root);

        Uri? endpoint = null;
        if (url != null && !Uri.TryCreate(Expand(url, root), UriKind.Absolute, out endpoint))
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = source,
                Message = $"MCP server '{name}' has an invalid URL '{url}'."
            });
            return null;
        }

        if (transport == PluginMcpTransportKind.Stdio && string.IsNullOrWhiteSpace(command))
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = source,
                Message = $"MCP server '{name}' is missing 'command' and was skipped."
            });
            return null;
        }

        if (transport != PluginMcpTransportKind.Stdio && endpoint == null)
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = source,
                Message = $"MCP server '{name}' is missing 'url' and was skipped."
            });
            return null;
        }

        return new PluginMcpServer
        {
            Name = name,
            QualifiedName = pluginName + ":" + name,
            PluginName = pluginName,
            Transport = transport,
            Command = command == null ? string.Empty : Expand(command, root),
            Arguments = arguments,
            Environment = environment,
            Url = endpoint,
            Headers = headers,
            SourcePath = source
        };
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement element, string property, string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!element.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
            {
                result[entry.Name] = Expand(entry.Value.GetString()!, root);
            }
        }

        return result;
    }

    private static JsonElement? TryReadJson(string path, string pluginName, List<ExtensionDiagnostic> diagnostics)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            diagnostics.Add(new ExtensionDiagnostic
            {
                Kind = ExtensionDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = path,
                Message = "Failed to read MCP configuration: " + ex.Message
            });
            return null;
        }
    }

    private static string? GetOptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Expand(string value, string pluginRoot)
    {
        if (value.IndexOf("${CLAUDE_PLUGIN_ROOT}", StringComparison.Ordinal) < 0)
        {
            return value;
        }

        // 与 Claude Code 一致：替换后的路径使用正斜杠，避免 shell 把反斜杠当转义。
        return value.Replace("${CLAUDE_PLUGIN_ROOT}", pluginRoot.Replace('\\', '/'));
    }

    private static bool LooksLikeFrontmatter(string content)
    {
        var text = content.TrimStart('\uFEFF').TrimStart();
        return text.StartsWith("---", StringComparison.Ordinal) &&
               (text.Length == 3 || text[3] == '\n' || text[3] == '\r');
    }

    private static string SanitizeCommandSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = InvalidCommandChars.Replace(value.Trim(), "-");
        while (cleaned.Contains("--"))
        {
            cleaned = cleaned.Replace("--", "-");
        }

        return cleaned.Trim('-', ':');
    }
}
