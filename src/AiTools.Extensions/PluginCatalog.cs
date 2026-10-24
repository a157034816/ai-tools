using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiTools.Skills;

namespace AiTools.Extensions;

/// <summary>控制插件发现行为的选项。</summary>
public sealed class PluginCatalogOptions
{
    /// <summary>获取或设置是否递归搜索子目录中的插件根。默认 true。</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>获取或设置额外忽略的插件目录名（不区分大小写）。</summary>
    public IReadOnlyCollection<string>? IgnoredDirectories { get; set; }

    /// <summary>获取或设置额外显式加载的插件目录（可为插件根或包含多个插件的目录）。</summary>
    public IReadOnlyCollection<string>? AdditionalDirectories { get; set; }

    /// <summary>获取或设置插件名允许列表；非空时只加载其中的插件。</summary>
    public IReadOnlyCollection<string>? EnabledPlugins { get; set; }

    /// <summary>获取或设置插件名拒绝列表（优先级高于 <see cref="EnabledPlugins"/>）。</summary>
    public IReadOnlyCollection<string>? DisabledPlugins { get; set; }
}

/// <summary>
/// 已加载插件的不可变集合，按名称索引。通过
/// <see cref="LoadFromDirectory(string, PluginCatalogOptions?)"/> 或
/// <see cref="FromPlugins(IEnumerable{AiToolsPlugin})"/> 构建。
/// </summary>
public sealed class PluginCatalog
{
    private readonly Dictionary<string, AiToolsPlugin> _plugins;

    private PluginCatalog(IReadOnlyList<AiToolsPlugin> plugins, IReadOnlyList<PluginDiagnostic> diagnostics)
    {
        Plugins = plugins;
        Diagnostics = diagnostics;
        _plugins = plugins.ToDictionary(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>获取已加载的插件，按名称排序。</summary>
    public IReadOnlyList<AiToolsPlugin> Plugins { get; }

    /// <summary>获取插件数量。</summary>
    public int Count => Plugins.Count;

    /// <summary>获取发现/加载过程中遇到的问题。</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; }

    /// <summary>获取所有插件提供的命令，按限定名排序。</summary>
    public IReadOnlyList<PluginCommand> Commands => Plugins
        .SelectMany(plugin => plugin.Commands)
        .OrderBy(command => command.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>获取所有插件提供的 skill。</summary>
    public IReadOnlyList<PluginSkill> Skills => Plugins
        .SelectMany(plugin => plugin.Skills)
        .OrderBy(skill => skill.QualifiedName, StringComparer.Ordinal)
        .ToArray();

    /// <summary>获取所有插件声明的 MCP 服务器。</summary>
    public IReadOnlyList<PluginMcpServer> McpServers => Plugins
        .SelectMany(plugin => plugin.McpServers)
        .OrderBy(server => server.QualifiedName, StringComparer.Ordinal)
        .ToArray();

    /// <summary>获取所有插件提供的输出风格。</summary>
    public IReadOnlyList<OutputStyle> OutputStyles => Plugins
        .SelectMany(plugin => plugin.OutputStyles)
        .OrderBy(style => style.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>获取所有插件提供的工作流，按限定名排序。</summary>
    public IReadOnlyList<Workflows.WorkflowDefinition> Workflows => Plugins
        .SelectMany(plugin => plugin.Workflows)
        .OrderBy(workflow => workflow.Name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>获取是否存在任何命令。</summary>
    public bool HasCommands => Plugins.Any(plugin => plugin.Commands.Count > 0);

    /// <summary>按名称获取插件，不存在时返回 null。</summary>
    public AiToolsPlugin? TryGet(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return _plugins.TryGetValue(name, out var plugin) ? plugin : null;
    }

    /// <summary>按限定名或短名解析命令。<paramref name="allowAmbiguousShortName"/> 为 true 时，重名短命令返回第一个。</summary>
    /// <param name="name">命令名，可带或不带插件前缀。</param>
    /// <param name="command">解析成功的命令。</param>
    /// <param name="allowAmbiguousShortName">短名冲突时是否容许回退到第一个匹配。</param>
    /// <remarks>
    /// 只解析 <see cref="PluginCommand.UserInvocable"/> 为 true 的命令：<c>user-invocable: false</c>
    /// 的命令仅供模型经 <c>load_skill</c> 使用，不接受用户直接调用。
    /// </remarks>
    public bool TryResolveCommand(string? name, out PluginCommand? command, bool allowAmbiguousShortName = false)
    {
        command = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name!.Trim().TrimStart('/');

        // 1) 限定名（plugin:command）。
        foreach (var plugin in Plugins)
        {
            var match = plugin.Commands.FirstOrDefault(item =>
                item.UserInvocable && string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                command = match;
                return true;
            }
        }

        // 2) 短名。
        var shortMatches = Commands
            .Where(item => string.Equals(item.ShortName, trimmed, StringComparison.OrdinalIgnoreCase) && item.UserInvocable)
            .ToArray();
        if (shortMatches.Length == 1)
        {
            command = shortMatches[0];
            return true;
        }

        if (shortMatches.Length > 1 && allowAmbiguousShortName)
        {
            command = shortMatches[0];
            return true;
        }

        return false;
    }

    /// <summary>获取与短名冲突的命令组（用于诊断提示）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PluginCommand>> GetShortNameConflicts()
    {
        return Commands
            .Where(command => command.UserInvocable)
            .GroupBy(command => command.ShortName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PluginCommand>)group.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>从预先构建的插件集合创建目录。</summary>
    public static PluginCatalog FromPlugins(IEnumerable<AiToolsPlugin> plugins)
    {
        if (plugins == null)
        {
            throw new ArgumentNullException(nameof(plugins));
        }

        var ordered = plugins
            .Where(plugin => plugin != null)
            .GroupBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(plugin => plugin.Name, StringComparer.Ordinal)
            .ToArray();

        return new PluginCatalog(ordered, Array.Empty<PluginDiagnostic>());
    }

    /// <summary>
    /// 在 <paramref name="rootPath"/> 下发现插件。目录本身是插件时按单个插件加载；
    /// 否则把每个子目录（递归时可更深）中"含清单/组件"的目录视为插件根。
    /// 无效插件会被跳过并在 <see cref="Diagnostics"/> 中报告。永不抛出异常。
    /// </summary>
    /// <param name="rootPath">插件根目录或多个插件的父目录。</param>
    /// <param name="options">发现选项。</param>
    public static PluginCatalog LoadFromDirectory(string rootPath, PluginCatalogOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A plugins directory is required.", nameof(rootPath));
        }

        options ??= new PluginCatalogOptions();
        var diagnostics = new List<PluginDiagnostic>();
        var plugins = new List<AiToolsPlugin>();
        var fullRoot = Path.GetFullPath(rootPath);
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void LoadOne(string directory)
        {
            var full = Path.GetFullPath(directory);
            if (!seenRoots.Add(full))
            {
                return;
            }

            var plugin = PluginLoader.Load(full);
            diagnostics.AddRange(plugin.Diagnostics);

            if (options.DisabledPlugins != null &&
                options.DisabledPlugins.Any(name => string.Equals(name, plugin.Name, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Info,
                    PluginName = plugin.Name,
                    Path = full,
                    Message = "Plugin is disabled and was not loaded."
                });
                return;
            }

            if (options.EnabledPlugins != null &&
                options.EnabledPlugins.Count > 0 &&
                !options.EnabledPlugins.Any(name => string.Equals(name, plugin.Name, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Info,
                    PluginName = plugin.Name,
                    Path = full,
                    Message = "Plugin is not in the enabled list and was not loaded."
                });
                return;
            }

            var error = plugin.Diagnostics.FirstOrDefault(item => item.Kind == PluginDiagnosticKind.Error);
            if (error != null)
            {
                return;
            }

            if (plugin.Commands.Count == 0 && plugin.Skills.Count == 0 && plugin.McpServers.Count == 0 && plugin.OutputStyles.Count == 0 && plugin.Workflows.Count == 0)
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    PluginName = plugin.Name,
                    Path = full,
                    Message = "Plugin declares no supported components (commands, skills, MCP servers, output styles or workflows)."
                });
                return;
            }

            plugins.Add(plugin);
        }

        void Scan(string directory, int depth)
        {
            if (!options.Recursive && depth > 1)
            {
                return;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    Path = directory,
                    Message = "Failed to enumerate directories: " + ex.Message
                });
                return;
            }

            foreach (var child in children.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, ".claude-plugin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (options.IgnoredDirectories != null &&
                    options.IgnoredDirectories.Any(ignored => string.Equals(ignored, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (PluginLoader.IsPluginDirectory(child))
                {
                    LoadOne(child);
                    continue;
                }

                if (options.Recursive)
                {
                    Scan(child, depth + 1);
                }
            }
        }

        try
        {
            if (Directory.Exists(fullRoot) && PluginLoader.IsPluginDirectory(fullRoot))
            {
                LoadOne(fullRoot);
            }
            else if (Directory.Exists(fullRoot))
            {
                Scan(fullRoot, 1);
            }
            else
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    Path = fullRoot,
                    Message = "The plugins directory does not exist."
                });
            }

            if (options.AdditionalDirectories != null)
            {
                foreach (var additional in options.AdditionalDirectories)
                {
                    if (string.IsNullOrWhiteSpace(additional))
                    {
                        continue;
                    }

                    if (!Directory.Exists(additional))
                    {
                        diagnostics.Add(new PluginDiagnostic
                        {
                            Kind = PluginDiagnosticKind.Warning,
                            Path = additional,
                            Message = "Additional plugin directory does not exist."
                        });
                        continue;
                    }

                    if (PluginLoader.IsPluginDirectory(additional))
                    {
                        LoadOne(additional);
                    }
                    else
                    {
                        Scan(Path.GetFullPath(additional), 1);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Error,
                Path = fullRoot,
                Message = "Failed to discover plugins: " + ex.Message
            });
        }

        var ordered = plugins
            .GroupBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToArray();
                if (items.Length > 1)
                {
                    diagnostics.Add(new PluginDiagnostic
                    {
                        Kind = PluginDiagnosticKind.Warning,
                        PluginName = group.Key,
                        Message = "Duplicate plugin name discovered; the last loaded copy wins."
                    });
                }

                return items[items.Length - 1];
            })
            .OrderBy(plugin => plugin.Name, StringComparer.Ordinal)
            .ToList();

        var catalog = new PluginCatalog(ordered, diagnostics);

        foreach (var conflict in catalog.GetShortNameConflicts())
        {
            diagnostics.Add(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Info,
                Path = string.Join(", ", conflict.Value.Select(item => "/" + item.Name)),
                Message = $"Short command name '{conflict.Key}' is ambiguous; use the qualified /plugin:name form."
            });
        }

        return catalog;
    }

    /// <summary>
    /// 把所有插件的 skill 合并为 <see cref="SkillCatalog"/>，供 <see cref="SkillToolset"/> 使用。
    /// skill 名称在无冲突时保留短名，冲突时改用 <c>plugin:name</c> 限定名。
    /// </summary>
    public SkillCatalog ToSkillCatalog()
    {
        var skills = Skills;
        var shortNameCounts = skills
            .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var definitions = skills.Select(skill =>
        {
            var unique = shortNameCounts[skill.Name] == 1;
            return new SkillDefinition
            {
                Name = unique ? skill.Name : skill.QualifiedName,
                Description = skill.Description,
                Instructions = skill.Instructions,
                DirectoryPath = skill.DirectoryPath,
                SkillFilePath = skill.SkillFilePath,
                AllowedTools = skill.AllowedTools
            };
        });

        return SkillCatalog.FromSkills(definitions);
    }
}
