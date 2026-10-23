using System;
using AiTools.Extensions;

namespace AiTools.Wpf.ViewModels;

/// <summary>命令面板（输入 <c>/</c> 时出现）中的一项。</summary>
public sealed class SlashCommandViewModel
{
    /// <summary>创建命令项。</summary>
    /// <param name="command">扩展或插件命令。</param>
    public SlashCommandViewModel(ResolvedCommand command)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
    }

    /// <summary>获取底层命令。</summary>
    public ResolvedCommand Command { get; }

    /// <summary>获取短名是否与其它命令冲突。</summary>
    public bool IsAmbiguous => Command.IsAmbiguous;

    /// <summary>获取不带前缀的命令名。</summary>
    public string ShortName => Command.ShortName;

    /// <summary>获取全名（插件命令带 <c>plugin:</c> 前缀）。</summary>
    public string Name => Command.Name;

    /// <summary>获取用于插入输入框的文本；短名冲突时使用全名。</summary>
    public string Insertion => "/" + Command.InvocationName + " ";

    /// <summary>获取实际输入的命令名：唯一时用短名，冲突时用全名。</summary>
    public string InvocationName => Command.InvocationName;

    /// <summary>获取面板中显示的标题，例如 <c>/goal &lt;目标描述&gt;</c>。</summary>
    public string Title => string.IsNullOrWhiteSpace(Command.ArgumentHint)
        ? "/" + InvocationName
        : "/" + InvocationName + " " + Command.ArgumentHint;

    /// <summary>获取说明文本。</summary>
    public string Description => Command.Description;

    /// <summary>获取来源标签（扩展名或插件名）。</summary>
    public string Source => Command.Source;

    /// <summary>获取命令是否由扩展代码实现。</summary>
    public bool IsCodeCommand => Command.IsCodeCommand;

    /// <summary>获取全名，例如 <c>/workflows:goal</c>。</summary>
    public string QualifiedName => "/" + Command.Name;
}
