using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AiTools.Skills;

namespace AiTools.Extensions;

/// <summary>
/// 一个输出风格：Markdown 正文追加到系统提示，改变模型的措辞与回复结构。
/// 与 Claude Code/Pi 的 <c>output-styles/*.md</c> 对应。
/// </summary>
public sealed class OutputStyle
{
    /// <summary>获取风格名称（frontmatter <c>name</c>，缺失时取文件名）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取风格描述（frontmatter <c>description</c>，缺失时退化为正文首个非空行）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// 获取应用本风格时是否保留宿主的编码指令（frontmatter <c>keep-coding-instructions</c>，默认 false）。
    /// </summary>
    public bool KeepCodingInstructions { get; init; }

    /// <summary>获取追加到系统提示的 Markdown 正文（已去掉 frontmatter）。</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>获取来源：插件名，或宿主注册时的 <c>host</c>。</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>获取来源文件的绝对路径；宿主注册时为空字符串。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>解析 <c>output-styles/*.md</c>。frontmatter 复用 <see cref="SkillFrontmatter"/> 的子集语法。</summary>
internal static class OutputStyleParser
{
    /// <summary>
    /// 解析一个风格文件。frontmatter 缺失时整个文件作为正文；frontmatter 非法时同样退化为正文并给出警告。
    /// </summary>
    /// <param name="content">文件内容。</param>
    /// <param name="fallbackName">frontmatter 无 <c>name</c> 时使用的名称（通常是文件名）。</param>
    /// <param name="source">来源（插件名或 <c>host</c>）。</param>
    /// <param name="sourcePath">来源文件路径。</param>
    /// <param name="warning">解析过程中的可恢复问题；无问题时为 null。</param>
    /// <returns>解析结果；名称为空时返回 null。</returns>
    public static OutputStyle? Parse(
        string content,
        string? fallbackName,
        string source,
        string sourcePath,
        out string? warning)
    {
        warning = null;
        SkillFrontmatter? frontmatter = null;
        var parsed = false;
        if (LooksLikeFrontmatter(content))
        {
            parsed = SkillFrontmatter.TryParse(content, out frontmatter, out var error);
            if (!parsed)
            {
                warning = (error ?? "Invalid frontmatter.") + " The whole file is treated as the style body.";
            }
        }

        var body = parsed ? frontmatter!.Body : content;
        var name = (frontmatter?.GetString("name") ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = (fallbackName ?? string.Empty).Trim();
        }

        if (name.Length == 0)
        {
            warning = "Output style has no usable name and was skipped.";
            return null;
        }

        var description = FrontmatterHelper.Describe(frontmatter?.GetString("description"), body);
        return new OutputStyle
        {
            Name = name,
            Description = description.Length == 0 ? name : description,
            KeepCodingInstructions = FrontmatterHelper.GetBool(frontmatter, "keep-coding-instructions") ?? false,
            Body = body.Trim(),
            Source = source,
            SourcePath = sourcePath
        };
    }

    private static bool LooksLikeFrontmatter(string content)
    {
        var text = content.TrimStart('\uFEFF').TrimStart();
        return text.StartsWith("---", StringComparison.Ordinal) &&
               (text.Length == 3 || text[3] == '\n' || text[3] == '\r');
    }
}

/// <summary>
/// 按名称索引的输出风格集合。插件提供的风格经 <see cref="LoadFromPlugins"/> 汇总，
/// 宿主注册的风格经 <see cref="FromStyles"/> 汇总；重名时先出现的定义获胜并记录诊断。
/// </summary>
public sealed class OutputStyleCatalog
{
    private readonly Dictionary<string, OutputStyle> _styles;

    private OutputStyleCatalog(IReadOnlyList<OutputStyle> styles, IReadOnlyList<PluginDiagnostic> diagnostics)
    {
        Styles = styles;
        Diagnostics = diagnostics;
        _styles = new Dictionary<string, OutputStyle>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in styles)
        {
            _styles[style.Name] = style;
        }
    }

    /// <summary>获取全部风格，按名称排序。</summary>
    public IReadOnlyList<OutputStyle> Styles { get; }

    /// <summary>获取汇总过程中发现的问题（重名、无效名称）；插件自身的诊断见 <see cref="PluginCatalog.Diagnostics"/>。</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; }

    /// <summary>获取风格数量。</summary>
    public int Count => Styles.Count;

    /// <summary>按名称（不区分大小写）获取风格；不存在时返回 null。</summary>
    public OutputStyle? TryGet(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _styles.TryGetValue(name!.Trim(), out var style) ? style : null;
    }

    /// <summary>汇总所有插件的输出风格。</summary>
    /// <param name="catalog">已加载的插件目录。</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> 为 null。</exception>
    public static OutputStyleCatalog LoadFromPlugins(PluginCatalog catalog)
    {
        if (catalog == null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        return Build(catalog.Plugins.SelectMany(plugin => plugin.OutputStyles));
    }

    /// <summary>从宿主注册的风格创建目录。</summary>
    /// <param name="styles">风格集合。</param>
    /// <exception cref="ArgumentNullException"><paramref name="styles"/> 为 null。</exception>
    public static OutputStyleCatalog FromStyles(IEnumerable<OutputStyle> styles)
    {
        if (styles == null)
        {
            throw new ArgumentNullException(nameof(styles));
        }

        return Build(styles);
    }

    private static OutputStyleCatalog Build(IEnumerable<OutputStyle> styles)
    {
        var ordered = new List<OutputStyle>();
        var diagnostics = new List<PluginDiagnostic>();
        var seen = new Dictionary<string, OutputStyle>(StringComparer.OrdinalIgnoreCase);

        foreach (var style in styles)
        {
            if (style == null || string.IsNullOrWhiteSpace(style.Name))
            {
                continue;
            }

            if (seen.TryGetValue(style.Name, out var existing))
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    PluginName = string.IsNullOrEmpty(style.Source) ? null : style.Source,
                    Path = style.SourcePath,
                    Message = $"Duplicate output style name '{style.Name}'; the definition from '{existing.Source}' wins."
                });
                continue;
            }

            seen.Add(style.Name, style);
            ordered.Add(style);
        }

        ordered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return new OutputStyleCatalog(ordered, diagnostics);
    }
}

/// <summary>
/// 把输出风格正文追加到系统提示。宿主决定是否以及如何丢弃自己的编码指令：
/// 当 <see cref="OutputStyle.KeepCodingInstructions"/> 为 false 时，这里只写入
/// <c>Coding instructions: suspended</c> 标记，不静默删除宿主提示。
/// </summary>
public static class OutputStylePrompt
{
    /// <summary>生成追加风格后的系统提示；<paramref name="baseSystemPrompt"/> 原样保留在最前面。</summary>
    /// <param name="style">要应用的风格。</param>
    /// <param name="baseSystemPrompt">宿主的系统提示，可为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="style"/> 为 null。</exception>
    public static string Apply(OutputStyle style, string? baseSystemPrompt)
    {
        if (style == null)
        {
            throw new ArgumentNullException(nameof(style));
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(baseSystemPrompt))
        {
            builder.Append(baseSystemPrompt!.TrimEnd());
            builder.Append('\n').Append('\n');
        }

        builder.Append("## Output style: ").Append(style.Name).Append('\n').Append('\n');
        if (!style.KeepCodingInstructions)
        {
            builder.Append("Coding instructions: suspended").Append('\n').Append('\n');
        }

        builder.Append(style.Body.Trim());
        return builder.ToString();
    }
}

/// <summary>
/// 一次会话选定的输出风格。宿主把它放进管道选项，在组装系统提示时调用 <see cref="ApplyTo"/>。
/// 未选择风格时 <see cref="None"/> 原样返回宿主提示。
/// </summary>
public sealed class OutputStyleSelection
{
    private OutputStyleSelection(OutputStyle? style)
    {
        Style = style;
    }

    /// <summary>获取未选择任何风格的实例。</summary>
    public static OutputStyleSelection None { get; } = new(null);

    /// <summary>获取选中的风格；未选择时为 null。</summary>
    public OutputStyle? Style { get; }

    /// <summary>获取是否选中了风格。</summary>
    public bool HasStyle => Style != null;

    /// <summary>按名称从目录解析；名称为空或目录中不存在时返回 <see cref="None"/>。</summary>
    public static OutputStyleSelection Resolve(OutputStyleCatalog? catalog, string? name)
        => catalog?.TryGet(name) is { } style ? new OutputStyleSelection(style) : None;

    /// <summary>
    /// 把选中的风格应用到系统提示；未选择风格时原样返回，
    /// 传入 null 且未选择风格时返回空字符串（系统提示段落永不为 null）。
    /// </summary>
    public string ApplyTo(string? baseSystemPrompt)
        => Style == null ? baseSystemPrompt ?? string.Empty : OutputStylePrompt.Apply(Style, baseSystemPrompt);
}
