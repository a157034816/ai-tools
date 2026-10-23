using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;

namespace AiTools.Samples.McpToolServer.ViewModels;

/// <summary>工具目录列表中显示的一张工具卡片。</summary>
public sealed class ToolCardViewModel
{
    /// <summary>根据服务器工具定义创建卡片。</summary>
    /// <param name="tool">其定义提供名称、描述与 JSON Schema 的工具。</param>
    internal ToolCardViewModel(McpServerTool tool)
    {
        Name = tool.Definition.Name;
        Title = tool.Title;
        Description = tool.Definition.Description ?? string.Empty;
        Parameters = BuildParameters(tool.Definition.ParametersSchema);
    }

    /// <summary>获取工具名称。</summary>
    public string Name { get; }

    /// <summary>获取简短的显示标题。</summary>
    public string Title { get; }

    /// <summary>获取面向 AI 的描述。</summary>
    public string Description { get; }

    /// <summary>获取从工具 JSON Schema 解析出的参数。</summary>
    public IReadOnlyList<ToolParameterViewModel> Parameters { get; }

    /// <summary>获取工具是否不接受任何参数。</summary>
    public bool HasNoParameters => Parameters.Count == 0;

    /// <summary>获取参数列表的可见性。</summary>
    public Visibility ParametersVisibility => HasNoParameters ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>获取“无参数”提示的可见性。</summary>
    public Visibility NoParametersVisibility => HasNoParameters ? Visibility.Visible : Visibility.Collapsed;

    private static IReadOnlyList<ToolParameterViewModel> BuildParameters(JsonElement? schema)
    {
        var parameters = new List<ToolParameterViewModel>();
        if (schema is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return parameters;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        required.Add(name!);
                    }
                }
            }
        }

        foreach (var property in properties.EnumerateObject())
        {
            var definition = property.Value;
            var type = definition.TryGetProperty("type", out var typeElement)
                       && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() ?? "string"
                : "string";

            var description = definition.TryGetProperty("description", out var descriptionElement)
                              && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;

            var options = new List<string>();
            if (definition.TryGetProperty("enum", out var enumElement)
                && enumElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in enumElement.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        options.Add(value.GetString() ?? string.Empty);
                    }
                    else
                    {
                        options.Add(value.GetRawText());
                    }
                }
            }

            parameters.Add(new ToolParameterViewModel(
                property.Name,
                description,
                type,
                required.Contains(property.Name),
                options));
        }

        return parameters;
    }
}
