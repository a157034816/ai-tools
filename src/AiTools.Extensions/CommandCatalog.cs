using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>统一后的斜杠命令：来源既可以是扩展代码，也可以是 Claude Code 插件的 Markdown 文件。</summary>
public sealed class ResolvedCommand
{
    /// <summary>获取命令名（不带 <c>/</c>）。扩展命令不带插件前缀；插件命令带 <c>plugin:</c> 前缀。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取用于输入的短名。</summary>
    public string ShortName { get; init; } = string.Empty;

    /// <summary>获取参数提示。</summary>
    public string? ArgumentHint { get; init; }

    /// <summary>获取描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>获取来源标签（扩展名或插件名）。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>获取扩展命令定义；插件命令时为 null。</summary>
    public ExtensionCommand? ExtensionCommand { get; init; }

    /// <summary>获取插件命令定义；扩展命令时为 null。</summary>
    public PluginCommand? PluginCommand { get; init; }

    /// <summary>获取是否为代码实现的扩展命令。</summary>
    public bool IsCodeCommand => ExtensionCommand != null;

    /// <summary>获取短名是否与其它命令冲突。</summary>
    public bool IsAmbiguous { get; set; }

    /// <summary>获取输入时应使用的命令名：冲突时用全名。</summary>
    public string InvocationName => IsAmbiguous ? Name : ShortName;

    /// <inheritdoc />
    public override string ToString() => "/" + Name;
}

/// <summary>
/// 命令目录：合并扩展命令与插件命令，解析短名/限定名，并统一渲染成要发送给模型的提示词。
/// </summary>
/// <remarks>
/// 优先级与 Pi 的"后注册胜出"一致：扩展命令先登记、插件命令随后登记，
/// 因此同名短命令会被插件命令覆盖；反过来，用限定名总是精确命中插件命令。
/// 命令目录只读，可在 UI 线程安全地重复查询。
/// </remarks>
public sealed class CommandCatalog
{
    private readonly List<ResolvedCommand> _commands = new();

    private CommandCatalog(IReadOnlyList<ResolvedCommand> commands, PluginCatalog? plugins, ExtensionRuntime? runtime)
    {
        _commands.AddRange(commands);
        Plugins = plugins;
        Runtime = runtime;
        // 冲突只影响"需要缩写的命令"：若命令的全名与短名相同（扩展命令），
        // 输入短名本来就精确命中它，不应被标记为歧义。
        var conflicts = commands
            .Where(command => command.ShortName.Length > 0)
            .GroupBy(command => command.ShortName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        var conflictSet = new HashSet<string>(conflicts, StringComparer.OrdinalIgnoreCase);

        foreach (var command in _commands)
        {
            command.IsAmbiguous = !string.Equals(command.Name, command.ShortName, StringComparison.OrdinalIgnoreCase) &&
                                  conflictSet.Contains(command.ShortName);
        }
    }

    /// <summary>获取插件目录；未配置插件时为 null。</summary>
    public PluginCatalog? Plugins { get; }

    /// <summary>获取扩展运行时；未配置扩展时为 null。</summary>
    public ExtensionRuntime? Runtime { get; }

    /// <summary>获取全部命令（扩展在前，插件在后；同名已去重）。</summary>
    public IReadOnlyList<ResolvedCommand> Commands => _commands;

    /// <summary>获取是否没有任何命令。</summary>
    public int Count => _commands.Count;

    /// <summary>解析命令名（可带 <c>/</c> 前缀）。精确匹配全名优先，其次唯一短名。</summary>
    /// <param name="name">命令名。</param>
    /// <param name="command">解析结果。</param>
    public bool TryResolve(string? name, out ResolvedCommand? command)
    {
        command = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name!.Trim().TrimStart('/');
        if (trimmed.Length == 0)
        {
            return false;
        }

        var exact = _commands.LastOrDefault(item => string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            command = exact;
            return true;
        }

        var shortMatches = _commands
            .Where(item => string.Equals(item.ShortName, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (shortMatches.Length == 1)
        {
            command = shortMatches[0];
            return true;
        }

        return false;
    }

    /// <summary>获取短名冲突的命令组。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ResolvedCommand>> GetShortNameConflicts()
        => _commands
            .GroupBy(command => command.ShortName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ResolvedCommand>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>构建命令目录。</summary>
    /// <param name="runtime">扩展运行时（可为 null）。</param>
    /// <param name="plugins">插件目录（可为 null）。</param>
    public static CommandCatalog Create(ExtensionRuntime? runtime, PluginCatalog? plugins)
    {
        var commands = new List<ResolvedCommand>();

        if (runtime != null)
        {
            foreach (var command in runtime.Commands)
            {
                commands.Add(new ResolvedCommand
                {
                    Name = command.Name,
                    ShortName = command.Name,
                    ArgumentHint = command.ArgumentHint,
                    Description = command.Description,
                    Source = command.ExtensionName,
                    ExtensionCommand = command
                });
            }
        }

        if (plugins != null)
        {
            foreach (var command in plugins.Commands.Where(item => item.UserInvocable))
            {
                commands.Add(new ResolvedCommand
                {
                    Name = command.Name,
                    ShortName = command.ShortName,
                    ArgumentHint = command.ArgumentHint,
                    Description = command.Description,
                    Source = command.PluginName,
                    PluginCommand = command
                });
            }
        }

        return new CommandCatalog(commands, plugins, runtime);
    }
}

/// <summary>命令执行结果（统一扩展命令与插件命令）。</summary>
public sealed class CommandExecution
{
    /// <summary>获取命中的命令。</summary>
    public ResolvedCommand Command { get; init; } = null!;

    /// <summary>获取参数字符串。</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>获取要发送给模型的提示词；<see cref="CommandExecutionKind.Noop"/> 时为 null。</summary>
    public string? Prompt { get; init; }

    /// <summary>获取要直接显示的助手文本。</summary>
    public string? DisplayText { get; init; }

    /// <summary>获取结果种类。</summary>
    public CommandExecutionKind Kind { get; init; }

    /// <summary>获取错误信息（渲染或执行失败时）。</summary>
    public string? Error { get; init; }

    /// <summary>获取执行过程中运行过的 shell 命令（仅插件命令）。</summary>
    public IReadOnlyList<string> ShellCommands { get; init; } = Array.Empty<string>();
}

/// <summary><see cref="CommandExecution"/> 的种类。</summary>
public enum CommandExecutionKind
{
    /// <summary>不产生动作。</summary>
    Noop,

    /// <summary>发送提示词。</summary>
    Prompt,

    /// <summary>直接显示文本。</summary>
    Display,

    /// <summary>失败。</summary>
    Error
}
