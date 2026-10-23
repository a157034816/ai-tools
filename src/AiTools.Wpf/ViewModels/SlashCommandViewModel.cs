using System;
using AiTools.Extensions;

namespace AiTools.Wpf.ViewModels;

/// <summary>命令面板（输入 <c>/</c> 时出现）中的一项。</summary>
public sealed class SlashCommandViewModel
{
    /// <summary>创建命令项。</summary>
    /// <param name="command">插件命令。</param>
    /// <param name="isAmbiguous">短名与其它插件命令冲突时为 true，插入文本会使用限定名。</param>
    public SlashCommandViewModel(PluginCommand command, bool isAmbiguous = false)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        IsAmbiguous = isAmbiguous;
    }

    /// <summary>获取底层命令定义。</summary>
    public PluginCommand Command { get; }

    /// <summary>获取短名是否与其它插件命令冲突。</summary>
    public bool IsAmbiguous { get; }

    /// <summary>获取不带插件前缀的命令名。</summary>
    public string ShortName => Command.ShortName;

    /// <summary>获取带插件前缀的命令名。</summary>
    public string Name => Command.Name;

    /// <summary>获取用于插入输入框的文本；短名冲突时使用限定名。</summary>
    public string Insertion => "/" + InvocationName + " ";

    /// <summary>获取实际输入的命令名：唯一时用短名，冲突时用限定名。</summary>
    public string InvocationName => IsAmbiguous ? Command.Name : Command.ShortName;

    /// <summary>获取面板中显示的标题，例如 <c>/goal &lt;目标描述&gt;</c>。</summary>
    public string Title => string.IsNullOrWhiteSpace(Command.ArgumentHint)
        ? "/" + InvocationName
        : "/" + InvocationName + " " + Command.ArgumentHint;

    /// <summary>获取说明文本。</summary>
    public string Description => Command.Description;

    /// <summary>获取来源标签，例如 <c>workflows</c>。</summary>
    public string Source => Command.PluginName;

    /// <summary>获取限定名，例如 <c>/workflows:goal</c>。</summary>
    public string QualifiedName => "/" + Command.Name;
}
