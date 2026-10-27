using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Extensions;
using AiTools.Skills;
using AiTools.Wpf.Configuration;

namespace AiTools.Wpf.Skills;

/// <summary>
/// 把多个来源的 skill 合并成一个 <see cref="IChatToolProvider"/>：
/// 插件自带技能、共享 <c>~/.agents/skills</c> 与项目 <c>.agents/skills</c>。
/// </summary>
/// <remarks>
/// 优先级与 Pi/Codex 的共用约定一致：项目级 &gt; 用户级 &gt; 插件。
/// 模型仍然通过 <c>load_skill</c> / <c>read_skill_file</c> 按需加载，
/// 合并后的清单同时进入系统提示，用户也能用 <c>/skill:&lt;名称&gt;</c> 直接调用。
/// </remarks>
public sealed class SkillCatalogChatToolProvider : IChatToolProvider
{
    private readonly SkillToolset _toolset;
    private readonly Func<ToolCall, CancellationToken, Task<string>> _handler;

    /// <summary>从已合并的 skill 目录创建 provider。</summary>
    /// <param name="catalog">合并后的 skill 目录。</param>
    /// <param name="options">skill 工具选项。</param>
    public SkillCatalogChatToolProvider(SkillCatalog catalog, SkillToolOptions? options = null)
    {
        if (catalog == null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        Catalog = catalog;
        _toolset = new SkillToolset(catalog, options);
        _handler = _toolset.CreateToolHandler();
    }

    /// <summary>
    /// 合并插件技能与共享 <c>.agents</c> 技能，优先级从低到高：插件 → 用户级 → 项目级。
    /// </summary>
    /// <param name="plugins">插件目录；为 null 时只用共享目录。</param>
    /// <param name="shared">共享 <c>.agents</c> 发现结果；为 null 时只用插件。</param>
    /// <param name="options">skill 工具选项。</param>
    public static SkillCatalogChatToolProvider Create(
        PluginCatalog? plugins,
        SharedAgentsCatalog? shared,
        SkillToolOptions? options = null)
    {
        var sources = new List<SkillDefinition>();

        if (plugins != null)
        {
            sources.AddRange(plugins.ToSkillCatalog().Skills);
        }

        if (shared != null)
        {
            sources.AddRange(shared.Skills.Skills);
        }

        return new SkillCatalogChatToolProvider(SkillCatalog.FromSkills(sources), options);
    }

    /// <summary>获取合并来源后的 skill 目录。</summary>
    public SkillCatalog Catalog { get; }

    /// <summary>获取底层工具集。</summary>
    public SkillToolset Toolset => _toolset;

    /// <summary>获取可用的 skill 数量。</summary>
    public int SkillCount => _toolset.Skills.Count;

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetTools() => _toolset.GetToolDefinitions();

    /// <inheritdoc />
    public Task<string> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
        => _handler(call, cancellationToken);
}
