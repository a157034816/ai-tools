using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiTools.Extensions;

/// <summary>替换命令正文中的占位符时所需的上下文。</summary>
public sealed class CommandRenderContext
{
    /// <summary>获取调用参数。</summary>
    public CommandArguments Arguments { get; init; } = CommandArguments.Parse(string.Empty);

    /// <summary>获取项目根目录（<c>${CLAUDE_PROJECT_DIR}</c>）。</summary>
    public string ProjectDirectory { get; init; } = string.Empty;

    /// <summary>获取当前会话 ID（<c>${CLAUDE_SESSION_ID}</c>）。</summary>
    public string? SessionId { get; init; }

    /// <summary>获取插件数据目录（<c>${CLAUDE_PLUGIN_DATA}</c>）。</summary>
    public string? PluginDataDirectory { get; init; }

    /// <summary>获取额外的自定义变量，例如宿主注入的值。</summary>
    public IReadOnlyDictionary<string, string>? Variables { get; init; }

    /// <summary>获取动态上下文注入（<c>!`cmd`</c>）的执行器；为 null 时占位符原样保留。</summary>
    public Func<string, string?>? ShellExecutor { get; init; }

    /// <summary>获取是否禁用 shell 注入（对应 Claude Code 的 <c>disableSkillShellExecution</c>）。</summary>
    public bool DisableShellExecution { get; init; }
}

/// <summary>命令正文的渲染结果。</summary>
public sealed class CommandRenderResult
{
    /// <summary>获取渲染后的正文。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>获取是否至少替换了一个参数占位符。为 false 且有参数时，宿主可追加 <c>ARGUMENTS:</c> 块。</summary>
    public bool ArgumentsConsumed { get; init; }

    /// <summary>获取被执行的 shell 命令，按出现顺序。</summary>
    public IReadOnlyList<string> ShellCommands { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 按 Claude Code 的规则渲染命令正文：<c>$ARGUMENTS</c>、<c>$ARGUMENTS[N]</c>、<c>$N</c>、
/// <c>$name</c>、<c>${CLAUDE_*}</c> 变量，以及 <c>!`command`</c> / <c>```!</c> 动态上下文注入。
/// </summary>
/// <remarks>
/// 参数替换是单遍的：由参数值注入的文本不会被再次扫描（与 Claude Code 一致）。
/// 使用 <c>\$1</c> 可写出字面量。动态上下文命令的输出也不会被再次扫描。
/// </remarks>
public static class CommandTemplateRenderer
{
    private const string ShellDisabledPlaceholder = "[shell command execution disabled by policy]";

    private static readonly Regex ShellPattern = new(
        "```!\\r?\\n(?<block>[\\s\\S]*?)```|(?<prefix>^|\\s)!`(?<inline>[^`]*)`",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex VariablePattern = new(
        @"\$\{(?<name>CLAUDE_[A-Z0-9_]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NamedArgumentPattern = new(
        @"\$(?<named>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>渲染命令正文并返回详细结果。</summary>
    /// <param name="template">命令正文。</param>
    /// <param name="command">命令定义（提供插件根目录、skill 目录与参数名）。</param>
    /// <param name="context">渲染上下文。</param>
    /// <exception cref="ExtensionCommandException">动态上下文注入命令失败。</exception>
    public static CommandRenderResult RenderResult(string template, PluginCommand command, CommandRenderContext context)
    {
        if (template == null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        if (command == null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        context ??= new CommandRenderContext();

        // 1) 变量与参数替换作用于原始正文（包含 shell 命令内的占位符）。
        var withVariables = ApplyVariables(template, command, context);
        var withArguments = ApplyArguments(withVariables, command, context.Arguments, out var consumed);

        // 2) 动态上下文注入最后执行；输出作为纯文本插入，不会再被扫描。
        var executed = new List<string>();
        var text = context.DisableShellExecution || context.ShellExecutor != null
            ? ApplyShellInjection(withArguments, context, executed)
            : withArguments;

        return new CommandRenderResult
        {
            Text = text,
            ArgumentsConsumed = consumed,
            ShellCommands = executed
        };
    }

    /// <summary>渲染命令正文并返回字符串。</summary>
    /// <param name="template">命令正文。</param>
    /// <param name="command">命令定义。</param>
    /// <param name="context">渲染上下文。</param>
    public static string Render(string template, PluginCommand command, CommandRenderContext context)
        => RenderResult(template, command, context).Text;

    private static string ApplyShellInjection(string template, CommandRenderContext context, List<string> executed)
    {
        if (!ShellPattern.IsMatch(template))
        {
            return template;
        }

        string Handler(string rawCommand)
        {
            var command = rawCommand.Trim();
            if (context.DisableShellExecution)
            {
                return ShellDisabledPlaceholder;
            }

            if (command.Length == 0)
            {
                return string.Empty;
            }

            executed.Add(command);
            try
            {
                var output = context.ShellExecutor!(command);
                return (output ?? string.Empty).TrimEnd('\r', '\n');
            }
            catch (ExtensionCommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ExtensionCommandException($"Shell command failed for pattern \"{command}\": {ex.Message}", ex);
            }
        }

        // 单遍替换：输出不会被再次扫描，且执行顺序与文档顺序一致。
        return ShellPattern.Replace(template, match =>
            match.Groups["block"].Success
                ? Handler(match.Groups["block"].Value)
                : match.Groups["prefix"].Value + Handler(match.Groups["inline"].Value));
    }

    private static string ApplyArguments(
        string template,
        PluginCommand command,
        CommandArguments arguments,
        out bool consumed)
    {
        consumed = false;
        if (template.IndexOf('$') < 0)
        {
            return template;
        }

        var builder = new StringBuilder();
        // \$… → 字面量（只去掉一个反斜杠）。
        builder.Append(@"\\(?<escaped>\$(?:ARGUMENTS(?:\[\d+\])?|\d+");
        foreach (var name in command.ArgumentNames)
        {
            if (!string.IsNullOrEmpty(name))
            {
                builder.Append('|').Append(Regex.Escape(name));
            }
        }

        builder.Append("))");
        // $ARGUMENTS[N] 与 $N。
        builder.Append(@"|\$(?:ARGUMENTS\[(?<index1>\d{1,3})\]|(?<index2>\d{1,3})(?![\d\[]))");
        // $ARGUMENTS（整串）。
        builder.Append(@"|\$(?<all>ARGUMENTS)(?![A-Za-z0-9_\[])");
        // $name。
        builder.Append(@"|\$(?<named>[A-Za-z_][A-Za-z0-9_]*)");

        var pattern = new Regex(builder.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var anyConsumed = false;

        var result = pattern.Replace(template, match =>
        {
            if (match.Groups["escaped"].Success)
            {
                return match.Value.Substring(1);
            }

            if (match.Groups["all"].Success)
            {
                anyConsumed = true;
                return arguments.Raw;
            }

            var indexGroup = match.Groups["index1"].Success ? match.Groups["index1"] : match.Groups["index2"];
            if (indexGroup.Success)
            {
                if (!int.TryParse(indexGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    return match.Value;
                }

                var value = arguments.Get(index);
                if (value == null)
                {
                    // 越界的索引占位符保持原样，且不视为消费。
                    return match.Value;
                }

                anyConsumed = true;
                return value;
            }

            if (match.Groups["named"].Success)
            {
                var position = IndexOfName(command.ArgumentNames, match.Groups["named"].Value);
                if (position < 0)
                {
                    // 未声明的 $name 原样保留，交给模型按字面理解。
                    return match.Value;
                }

                anyConsumed = true;
                return arguments.Get(position) ?? string.Empty;
            }

            return match.Value;
        });

        consumed = anyConsumed;
        return result;
    }

    private static int IndexOfName(IReadOnlyList<string> names, string name)
    {
        for (var index = 0; index < names.Count; index++)
        {
            if (string.Equals(names[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string ApplyVariables(string body, PluginCommand command, CommandRenderContext context)
    {
        if (body.IndexOf("${CLAUDE_", StringComparison.Ordinal) < 0)
        {
            return body;
        }

        return VariablePattern.Replace(body, match =>
        {
            var name = match.Groups["name"].Value;
            switch (name)
            {
                case "CLAUDE_PLUGIN_ROOT":
                    return command.PluginRoot;
                case "CLAUDE_SKILL_DIR":
                    return command.SkillDirectory ?? command.PluginRoot;
                case "CLAUDE_PROJECT_DIR":
                    return string.IsNullOrEmpty(context.ProjectDirectory) ? command.PluginRoot : context.ProjectDirectory;
                case "CLAUDE_SESSION_ID":
                    return context.SessionId ?? string.Empty;
                case "CLAUDE_PLUGIN_DATA":
                    return context.PluginDataDirectory ?? string.Empty;
                default:
                    return context.Variables != null && context.Variables.TryGetValue(name, out var value)
                        ? value
                        : match.Value;
            }
        });
    }

    /// <summary>判断给定命令正文是否含有动态上下文注入（无论是否会执行）。</summary>
    public static bool ContainsShellInjection(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return false;
        }

        return ShellPattern.IsMatch(template);
    }

    /// <summary>提取命令正文中的全部动态上下文注入命令，按出现顺序返回（去重）。</summary>
    public static IReadOnlyList<string> ExtractShellCommands(string template)
    {
        var commands = new List<string>();
        if (string.IsNullOrEmpty(template))
        {
            return commands;
        }

        foreach (Match match in ShellPattern.Matches(template))
        {
            var value = match.Groups["block"].Success
                ? match.Groups["block"].Value
                : match.Groups["inline"].Value;
            var command = value.Trim();
            if (command.Length > 0 && !commands.Contains(command))
            {
                commands.Add(command);
            }
        }

        return commands;
    }

    /// <summary>在正文未消费任何参数时追加 <c>ARGUMENTS: &lt;value&gt;</c> 块。</summary>
    public static string AppendArgumentsIfUnused(string body, CommandArguments arguments, bool consumed)
    {
        if (consumed || arguments.IsEmpty)
        {
            return body;
        }

        var builder = new StringBuilder(body.TrimEnd());
        builder.AppendLine();
        builder.AppendLine();
        builder.Append("ARGUMENTS: ").Append(arguments.Raw);
        return builder.ToString();
    }
}
