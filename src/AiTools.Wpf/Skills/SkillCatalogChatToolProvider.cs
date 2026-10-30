using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Extensions;
using AiTools.Skills;
using AiTools.Wpf.Configuration;

namespace AiTools.Wpf.Skills;

/// <summary>
/// 把多个来源的 skill 合并成一个 <see cref="IChatToolProvider"/>：
/// 插件自带技能、扩展运行时登记的技能、共享 <c>~/.agents/skills</c> 与项目 <c>.agents/skills</c>。
/// </summary>
/// <remarks>
/// 优先级与 Pi/Codex 的共用约定一致：项目级 &gt; 用户级 &gt; 扩展 &gt; 插件。
/// 模型仍然通过 <c>load_skill</c> / <c>read_skill_file</c> 按需加载，
/// 合并后的清单同时进入系统提示，用户也能用 <c>/skill:&lt;名称&gt;</c> 直接调用。
/// 传入目录工厂时，每次访问都会重新读取目录；只有技能内容变化时才重建工具集。
/// 因此扩展在会话期间新登记技能后，宿主无需重新注册 provider。
/// </remarks>
public sealed class SkillCatalogChatToolProvider : IChatToolProvider
{
    private readonly Func<SkillCatalog> _catalogFactory;
    private readonly SkillToolOptions? _options;
    private readonly object _gate = new();
    private SkillCatalog? _catalog;
    private SkillToolset? _toolset;
    private Func<ToolCall, CancellationToken, Task<string>>? _handler;
    private string? _fingerprint;

    /// <summary>从已合并的 skill 目录创建 provider。</summary>
    /// <param name="catalog">合并后的 skill 目录。</param>
    /// <param name="options">skill 工具选项。</param>
    public SkillCatalogChatToolProvider(SkillCatalog catalog, SkillToolOptions? options = null)
        : this(() => catalog ?? throw new ArgumentNullException(nameof(catalog)), options)
    {
    }

    /// <summary>
    /// 从目录工厂创建 provider。每次 <see cref="GetTools"/> / <see cref="InvokeAsync"/> 都会重新读取目录，
    /// 内容（名称、描述、指令、目录）发生变化时才重建 <see cref="SkillToolset"/>。
    /// </summary>
    /// <param name="catalogFactory">返回当前 skill 目录的工厂。</param>
    /// <param name="options">skill 工具选项。</param>
    public SkillCatalogChatToolProvider(Func<SkillCatalog> catalogFactory, SkillToolOptions? options = null)
    {
        _catalogFactory = catalogFactory ?? throw new ArgumentNullException(nameof(catalogFactory));
        _options = options;
        Refresh();
    }

    /// <summary>
    /// 合并插件技能、扩展技能与共享 <c>.agents</c> 技能，优先级从低到高：插件 → 扩展 → 共享。
    /// </summary>
    /// <param name="plugins">插件目录；为 null 时跳过插件。</param>
    /// <param name="shared">共享 <c>.agents</c> 发现结果；为 null 时跳过共享目录。</param>
    /// <param name="extensionSkills">扩展运行时登记的技能；为 null 时跳过。</param>
    /// <param name="options">skill 工具选项。</param>
    public static SkillCatalogChatToolProvider Create(
        PluginCatalog? plugins,
        SharedAgentsCatalog? shared,
        IEnumerable<SkillDefinition>? extensionSkills = null,
        SkillToolOptions? options = null)
    {
        var sources = new List<SkillDefinition>();

        if (plugins != null)
        {
            sources.AddRange(plugins.ToSkillCatalog().Skills);
        }

        if (extensionSkills != null)
        {
            sources.AddRange(extensionSkills);
        }

        if (shared != null)
        {
            sources.AddRange(shared.Skills.Skills);
        }

        return new SkillCatalogChatToolProvider(SkillCatalog.FromSkills(sources), options);
    }

    /// <summary>获取合并来源后的 skill 目录。</summary>
    public SkillCatalog Catalog
    {
        get
        {
            lock (_gate)
            {
                return _catalog!;
            }
        }
    }

    /// <summary>获取底层工具集。</summary>
    public SkillToolset Toolset
    {
        get
        {
            lock (_gate)
            {
                return _toolset!;
            }
        }
    }

    /// <summary>获取可用的 skill 数量。</summary>
    public int SkillCount => Toolset.Skills.Count;

    /// <summary>重新解析目录；技能内容变化时重建工具集。返回是否发生了变化。</summary>
    public bool Refresh()
    {
        SkillCatalog next;
        try
        {
            next = _catalogFactory();
        }
        catch (Exception)
        {
            return false;
        }

        if (next == null)
        {
            return false;
        }

        var fingerprint = ComputeFingerprint(next);
        lock (_gate)
        {
            if (string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            _fingerprint = fingerprint;
            _catalog = next;
            _toolset = new SkillToolset(next, _options);
            _handler = _toolset.CreateToolHandler();
            return true;
        }
    }

    private const char FingerprintSeparator = '\u001F';
    private const char FingerprintRecordSeparator = '\u001E';

    /// <summary>
    /// 计算技能内容的指纹。宿主通常每次重新合并都产生新目录对象，
    /// 只有内容真的变化时才需要重建 <see cref="SkillToolset"/>。
    /// </summary>
    private static string ComputeFingerprint(SkillCatalog catalog)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var skill in catalog.Skills)
        {
            builder
                .Append(skill.Name).Append(FingerprintSeparator)
                .Append(skill.Description).Append(FingerprintSeparator)
                .Append(skill.DirectoryPath).Append(FingerprintSeparator)
                .Append(skill.Instructions).Append(FingerprintRecordSeparator);
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetTools()
    {
        Refresh();
        return Toolset.GetToolDefinitions();
    }

    /// <inheritdoc />
    public Task<string> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
    {
        Refresh();
        Func<ToolCall, CancellationToken, Task<string>>? handler;
        lock (_gate)
        {
            handler = _handler;
        }

        return handler == null
            ? Task.FromResult("{\"error\":\"No skills are loaded.\"}")
            : handler(call, cancellationToken);
    }
}
