using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Skills;
using AiTools.Wpf.Configuration;

namespace AiTools.Wpf.Skills;

/// <summary>
/// 将 <see cref="AiTools.Skills.SkillToolset"/> 桥接到 WPF 对话页。
/// 通过 <see cref="AiToolsWpf.RegisterToolProvider"/> 注册实例，即可在对话中使用技能。
/// </summary>
/// <remarks>
/// <see cref="IChatToolProvider"/> 只暴露 Tool，因此此适配器提供两个技能
/// 元 Tool（<c>load_skill</c> / <c>read_skill_file</c>）。若还要把技能目录注入
/// system prompt，请将 <see cref="AiToolsWpfOptions.DefaultSystemPrompt"/> 设置为 <see cref="SkillToolset.BuildSystemPrompt"/>，
/// 或与自己的 prompt 组合使用。
/// </remarks>
public sealed class SkillChatToolProvider : IChatToolProvider
{
    private readonly SkillToolset _toolset;
    private readonly Func<ToolCall, CancellationToken, Task<string>> _handler;

    /// <summary>从技能工具集创建 provider。</summary>
    public SkillChatToolProvider(SkillToolset toolset)
    {
        _toolset = toolset ?? throw new ArgumentNullException(nameof(toolset));
        _handler = toolset.CreateToolHandler();
    }

    /// <summary>创建一个从目录中发现技能的 provider。</summary>
    /// <param name="skillsDirectory">包含技能文件夹（内含 <c>SKILL.md</c>）的目录。</param>
    /// <param name="options">可选的 Tool 选项。</param>
    public static SkillChatToolProvider FromDirectory(string skillsDirectory, SkillToolOptions? options = null)
        => new(new SkillToolset(SkillCatalog.LoadFromDirectory(skillsDirectory), options));

    /// <summary>获取底层工具集。</summary>
    public SkillToolset Toolset => _toolset;

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetTools() => _toolset.GetToolDefinitions();

    /// <inheritdoc />
    public Task<string> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
        => _handler(call, cancellationToken);
}
