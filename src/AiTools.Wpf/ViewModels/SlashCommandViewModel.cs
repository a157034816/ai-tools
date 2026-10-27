using System;
using AiTools.Extensions;

namespace AiTools.Wpf.ViewModels;

/// <summary>命令面板（输入 <c>/</c> 时出现）中的一项。</summary>
public sealed class SlashCommandViewModel
{
    /// <summary>创建命令项。</summary>
    /// <param name="command">扩展、插件或技能命令。</param>
    public SlashCommandViewModel(ResolvedCommand command)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
    }

    /// <summary>创建宿主内置会话命令项（例如 <c>/resume</c>）。</summary>
    /// <param name="name">命令名（不带 <c>/</c>）。</param>
    /// <param name="description">说明文本。</param>
    /// <param name="argumentHint">参数提示。</param>
    public SlashCommandViewModel(string name, string description, string? argumentHint = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A command name is required.", nameof(name));
        }

        BuiltInName = name;
        BuiltInDescription = description ?? string.Empty;
        BuiltInArgumentHint = argumentHint;
    }

    /// <summary>获取底层命令；内置会话命令为 null。</summary>
    public ResolvedCommand? Command { get; }

    /// <summary>获取是否为宿主内置的会话命令。</summary>
    public bool IsBuiltIn => Command == null;

    /// <summary>获取短名是否与其它命令冲突。</summary>
    public bool IsAmbiguous => Command?.IsAmbiguous ?? false;

    /// <summary>获取不带前缀的命令名。</summary>
    public string ShortName => Command?.ShortName ?? BuiltInName!;

    /// <summary>获取全名（插件命令带 <c>plugin:</c> 前缀，技能命令带 <c>skill:</c> 前缀）。</summary>
    public string Name => Command?.Name ?? BuiltInName!;

    /// <summary>获取用于插入输入框的文本；短名冲突时使用全名。</summary>
    public string Insertion => "/" + InvocationName + " ";

    /// <summary>获取实际输入的命令名：唯一时用短名，冲突时用全名。</summary>
    public string InvocationName => Command?.InvocationName ?? BuiltInName!;

    /// <summary>获取面板中显示的标题，例如 <c>/goal &lt;目标描述&gt;</c>。</summary>
    public string Title
    {
        get
        {
            var hint = Command?.ArgumentHint ?? BuiltInArgumentHint;
            return string.IsNullOrWhiteSpace(hint)
                ? "/" + InvocationName
                : "/" + InvocationName + " " + hint;
        }
    }

    /// <summary>获取说明文本。</summary>
    public string Description => Command?.Description ?? BuiltInDescription!;

    /// <summary>获取来源标签（扩展名、插件名、<c>skill</c> 或 <c>内置</c>）。</summary>
    public string Source => Command == null
        ? "内置"
        : Command.Skill != null
            ? "skill"
            : Command.Source;

    /// <summary>获取命令是否由扩展代码实现。</summary>
    public bool IsCodeCommand => Command?.IsCodeCommand ?? false;

    /// <summary>获取全名，例如 <c>/workflows:goal</c>。</summary>
    public string QualifiedName => "/" + Name;

    /// <summary>
    /// 获取用于模糊匹配的额外文本：技能命令同时接受不带 <c>skill:</c> 前缀的技能名，
    /// 因此 <c>/review</c> 也能找到 <c>/skill:review</c>。
    /// </summary>
    public string Keywords => Command?.Skill is { } skill
        ? skill.Name + " " + skill.QualifiedName
        : string.Empty;

    private string? BuiltInName { get; }

    private string? BuiltInDescription { get; }

    private string? BuiltInArgumentHint { get; }
}
