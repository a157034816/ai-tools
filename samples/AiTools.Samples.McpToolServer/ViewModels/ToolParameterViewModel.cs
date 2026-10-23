using System;
using System.Collections.Generic;
using System.Windows;

namespace AiTools.Samples.McpToolServer.ViewModels;

/// <summary>为工具参数生成的输入控件类型。</summary>
public enum ToolParameterKind
{
    /// <summary>自由格式的单行或多行文本。</summary>
    String,

    /// <summary>64 位整数。</summary>
    Integer,

    /// <summary>小数。</summary>
    Number,

    /// <summary>布尔复选框。</summary>
    Boolean,

    /// <summary>固定枚举中的一个值。</summary>
    Enum
}

/// <summary>工具的一个参数，由其 <c>ToolDefinition</c> 的 JSON Schema 生成。</summary>
public sealed class ToolParameterViewModel : ObservableObject
{
    private string _value = string.Empty;
    private bool _boolValue;
    private string? _selectedOption;

    /// <summary>创建参数 ViewModel。</summary>
    /// <param name="name">参数名称。</param>
    /// <param name="description">可选的 Schema 描述。</param>
    /// <param name="type">JSON Schema 类型，例如 <c>string</c> 或 <c>integer</c>。</param>
    /// <param name="required">Schema 是否将此参数列为必填。</param>
    /// <param name="options">枚举型参数允许的取值。</param>
    public ToolParameterViewModel(
        string name,
        string? description,
        string type,
        bool required,
        IReadOnlyList<string> options)
    {
        Name = name;
        Description = description;
        JsonType = type;
        Required = required;
        Options = options;

        Kind = DetermineKind(type, options);
        if (Kind == ToolParameterKind.Enum && options.Count > 0)
        {
            _selectedOption = options[0];
        }
    }

    /// <summary>获取参数名称。</summary>
    public string Name { get; }

    /// <summary>获取可选的 Schema 描述。</summary>
    public string? Description { get; }

    /// <summary>获取工具定义中书写的 JSON Schema 类型。</summary>
    public string JsonType { get; }

    /// <summary>获取显示类型，例如 <c>string</c>、<c>integer</c> 或 <c>enum</c>。</summary>
    public string TypeText => Kind switch
    {
        ToolParameterKind.Integer => "integer",
        ToolParameterKind.Number => "number",
        ToolParameterKind.Boolean => "boolean",
        ToolParameterKind.Enum => "enum",
        _ => "string"
    };

    /// <summary>获取 Schema 是否要求此参数必填。</summary>
    public bool Required { get; }

    /// <summary>获取显示在参数名下方的紧凑摘要，例如 <c>integer · 必填</c>。</summary>
    public string SummaryText => TypeText + (Required ? " · 必填" : " · 可选");

    /// <summary>获取用于选择控件的输入类型。</summary>
    public ToolParameterKind Kind { get; }

    /// <summary>获取枚举参数允许的取值。</summary>
    public IReadOnlyList<string> Options { get; }

    /// <summary>获取或设置字符串与数值参数使用的原始文本值。</summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>获取或设置布尔参数的值。</summary>
    public bool BoolValue
    {
        get => _boolValue;
        set => SetProperty(ref _boolValue, value);
    }

    /// <summary>获取或设置枚举参数的选中值。</summary>
    public string? SelectedOption
    {
        get => _selectedOption;
        set => SetProperty(ref _selectedOption, value);
    }

    /// <summary>获取调用工具时是否应发送此参数。</summary>
    public bool ShouldInclude => Required
                                 || Kind == ToolParameterKind.Enum
                                 || (Kind == ToolParameterKind.Boolean ? BoolValue : !string.IsNullOrWhiteSpace(Value));

    /// <summary>获取单行文本输入的可见性。</summary>
    public Visibility SingleLineInputVisibility =>
        Kind == ToolParameterKind.String && !IsMultiline ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>获取多行文本输入的可见性。</summary>
    public Visibility MultilineInputVisibility =>
        Kind == ToolParameterKind.String && IsMultiline ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>获取枚举下拉框的可见性。</summary>
    public Visibility EnumInputVisibility =>
        Kind == ToolParameterKind.Enum ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>获取布尔复选框的可见性。</summary>
    public Visibility BoolInputVisibility =>
        Kind == ToolParameterKind.Boolean ? Visibility.Visible : Visibility.Collapsed;

    private bool IsMultiline => string.Equals(Name, "text", StringComparison.Ordinal);

    private static ToolParameterKind DetermineKind(string type, IReadOnlyList<string> options)
    {
        if (options.Count > 0)
        {
            return ToolParameterKind.Enum;
        }

        switch (type)
        {
            case "integer":
                return ToolParameterKind.Integer;

            case "number":
                return ToolParameterKind.Number;

            case "boolean":
                return ToolParameterKind.Boolean;

            default:
                return ToolParameterKind.String;
        }
    }
}
