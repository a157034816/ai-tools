using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>
/// 一组相关工具（例如一个 MCP 服务器的工具）。工具清单按命名空间分组，
/// 查找时 <c>my-docs</c> 与 <c>my_docs</c> 视为同一个命名空间。
/// </summary>
public sealed record ToolNamespace
{
    /// <summary>获取命名空间名，例如 <c>mcp__docs</c>。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取别名；查找时与 <see cref="Name"/> 等价，分隔符差异被忽略。</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>获取短描述；在模型可见的工具清单里随分组显示一次。</summary>
    public string? Description { get; init; }

    /// <summary>获取更长的使用说明；不进入工具清单，供按需查询使用。</summary>
    public string? Instructions { get; init; }
}

/// <summary>
/// 工具可达范围：<see cref="All"/>，或一组词。词匹配工具名、或工具所属命名空间的名称/别名
/// （包含关系，大小写与 <c>-</c>/<c>_</c> 差异不敏感）。
/// </summary>
public sealed class ToolReach
{
    private static readonly ToolReach AllReach = new(isAll: true, Array.Empty<string>());

    private ToolReach(bool isAll, IReadOnlyList<string> items)
    {
        IsAll = isAll;
        Items = items;
    }

    /// <summary>获取是否不限制可达工具。</summary>
    public bool IsAll { get; }

    /// <summary>获取匹配词；<see cref="IsAll"/> 时为空。</summary>
    public IReadOnlyList<string> Items { get; }

    /// <summary>不限制可达工具。</summary>
    public static ToolReach All() => AllReach;

    /// <summary>只允许命中所给词的工具。</summary>
    /// <param name="words">匹配词（空词被忽略）。</param>
    public static ToolReach Words(params string[] words)
    {
        if (words == null)
        {
            throw new ArgumentNullException(nameof(words));
        }

        var items = new List<string>(words.Length);
        foreach (var word in words)
        {
            if (!string.IsNullOrWhiteSpace(word))
            {
                items.Add(word.Trim());
            }
        }

        return new ToolReach(isAll: false, items);
    }
}

/// <summary>
/// 一次请求的工具装载结果：声明给模型的工具、可被其它工具调用的工具，以及全部已登记工具。
/// 编排型工具（<c>codemode</c>、<c>tool_search</c> 等）可通过 <see cref="IExtensionApi.RegisterLoadoutPreparer"/>
/// 改写 <see cref="Descriptions"/> 与 <see cref="HiddenDeclarations"/>。
/// </summary>
public sealed class ToolLoadout
{
    /// <summary>获取或设置声明给模型的工具（已应用描述替换并去掉隐藏声明）。</summary>
    public IReadOnlyList<ExtensionTool> Declared { get; set; } = Array.Empty<ExtensionTool>();

    /// <summary>获取或设置可被 <see cref="ExtensionRuntime.ExecuteToolAsync(string, string, CommandContext, string?, System.Threading.CancellationToken)"/> 调用的工具。</summary>
    public IReadOnlyList<ExtensionTool> Callable { get; set; } = Array.Empty<ExtensionTool>();

    /// <summary>获取或设置全部已登记工具（含内置编排工具与不可达工具）。</summary>
    public IReadOnlyList<ExtensionTool> All { get; set; } = Array.Empty<ExtensionTool>();

    /// <summary>获取或设置声明用的替换描述（工具名 → 描述）。</summary>
    public Dictionary<string, string> Descriptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取或设置从请求声明中隐藏的已激活工具。它们仍保持激活且可被其它工具调用，
    /// 只是不进入模型请求的工具清单。
    /// </summary>
    public HashSet<string> HiddenDeclarations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>复制一份装载结果，供准备器改写时与当前结果隔离。</summary>
    internal static ToolLoadout Copy(ToolLoadout source) => new()
    {
        Declared = source.Declared.ToArray(),
        Callable = source.Callable.ToArray(),
        All = source.All.ToArray(),
        Descriptions = new Dictionary<string, string>(source.Descriptions, StringComparer.OrdinalIgnoreCase),
        HiddenDeclarations = new HashSet<string>(source.HiddenDeclarations, StringComparer.OrdinalIgnoreCase)
    };

    /// <summary>替换工具定义中的描述，其它字段保持不变。</summary>
    internal static ExtensionTool WithDescription(ExtensionTool tool, string? description)
    {
        if (string.Equals(tool.Definition.Description, description, StringComparison.Ordinal))
        {
            return tool;
        }

        return new ExtensionTool
        {
            Definition = new ToolDefinition
            {
                Name = tool.Definition.Name,
                Description = description,
                ParametersSchema = tool.Definition.ParametersSchema
            },
            ExtensionName = tool.ExtensionName,
            Exposure = tool.Exposure,
            Execute = tool.Execute,
            ReadOnlyHint = tool.ReadOnlyHint,
            Namespace = tool.Namespace,
            ReachesTools = tool.ReachesTools
        };
    }
}

/// <summary>工具名与命名空间名的归一化比较。</summary>
internal static class ToolNames
{
    /// <summary>转为小写、分隔符统一为 <c>_</c> 的规范形式（<c>my-docs</c> 与 <c>my_docs</c> 相同）。</summary>
    public static string Canonicalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value!.Trim();
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[builder.Length - 1] != '_')
            {
                builder.Append('_');
            }
        }

        while (builder.Length > 0 && builder[builder.Length - 1] == '_')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}

/// <summary>按命名空间分组的工具清单；<see cref="Namespace"/> 为 null 表示未分组。</summary>
public sealed class ToolGroup
{
    /// <summary>获取命名空间；未分组时为 null。</summary>
    public ToolNamespace? Namespace { get; init; }

    /// <summary>获取该组内的工具。</summary>
    public IReadOnlyList<ExtensionTool> Tools { get; init; } = Array.Empty<ExtensionTool>();
}

/// <summary>命名空间的注册项：记录归属扩展，便于激活失败时回滚。</summary>
internal sealed class NamespaceRegistration
{
    public NamespaceRegistration(string owner, ToolNamespace value)
    {
        Owner = owner;
        Value = value;
    }

    public string Owner { get; }

    public ToolNamespace Value { get; }
}

/// <summary>装载准备器的注册项：记录归属扩展，便于激活失败时回滚。</summary>
internal sealed class LoadoutPreparerRegistration
{
    public LoadoutPreparerRegistration(string owner, Func<ToolLoadout, ToolLoadout> prepare)
    {
        Owner = owner;
        Prepare = prepare;
    }

    public string Owner { get; }

    public Func<ToolLoadout, ToolLoadout> Prepare { get; }
}
