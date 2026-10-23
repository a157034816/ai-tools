using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using AiTools.Samples.McpExplorer.ViewModels;

namespace AiTools.Samples.McpExplorer;

/// <summary>为 JSON Schema 属性生成的输入控件类型。</summary>
public enum ToolParameterKind
{
    /// <summary>自由格式的单行文本。</summary>
    Text,

    /// <summary>多行文本。</summary>
    MultilineText,

    /// <summary>数值输入（整数或数字）。</summary>
    Number,

    /// <summary>布尔复选框。</summary>
    Boolean,

    /// <summary>以组合框呈现的枚举。</summary>
    Enum
}

/// <summary>由架构驱动的工具调用可编辑参数。</summary>
public sealed class ToolParameterViewModel : ObservableObject
{
    private readonly string? _defaultValue;
    private string _textValue = string.Empty;
    private bool _boolValue;
    private string? _selectedEnumValue;
    private string? _validationError;

    /// <summary>从 JSON Schema 属性创建参数 ViewModel。</summary>
    /// <param name="name">属性名称。</param>
    /// <param name="schema">属性架构。</param>
    /// <param name="required">父架构是否将该属性列为必填。</param>
    public ToolParameterViewModel(string name, JsonElement schema, bool required)
    {
        Name = name;
        Schema = schema;
        IsRequired = required;
        TypeLabel = ToolInvocation.DescribeSchemaType(schema);
        Description = ToolInvocation.ReadString(schema, "description") ?? string.Empty;
        Label = required ? Name + " *" : Name;
        Minimum = ReadNumber(schema, "minimum");
        Maximum = ReadNumber(schema, "maximum");

        EnumValues = ToolInvocation.ReadEnumValues(schema);
        _defaultValue = ToolInvocation.ReadDefaultText(schema);

        if (EnumValues.Count > 0)
        {
            Kind = ToolParameterKind.Enum;
            _selectedEnumValue = _defaultValue ?? EnumValues[0];
        }
        else if (string.Equals(TypeLabel, "boolean", StringComparison.OrdinalIgnoreCase))
        {
            Kind = ToolParameterKind.Boolean;
            _boolValue = string.Equals(_defaultValue, "true", StringComparison.OrdinalIgnoreCase);
        }
        else if (string.Equals(TypeLabel, "integer", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(TypeLabel, "number", StringComparison.OrdinalIgnoreCase))
        {
            Kind = ToolParameterKind.Number;
            _textValue = _defaultValue ?? string.Empty;
        }
        else if (string.Equals(TypeLabel, "string", StringComparison.OrdinalIgnoreCase))
        {
            Kind = ToolParameterKind.Text;
            _textValue = _defaultValue ?? string.Empty;
        }
        else
        {
            // 对象、数组和未知类型以 JSON 文本形式编辑。
            Kind = ToolParameterKind.MultilineText;
            _textValue = _defaultValue ?? string.Empty;
        }

        IsJsonText = !string.Equals(TypeLabel, "string", StringComparison.OrdinalIgnoreCase)
                     && Kind is ToolParameterKind.MultilineText;
    }

    /// <summary>获取属性名称。</summary>
    public string Name { get; }

    /// <summary>获取表单中显示的标签（必填字段带星号）。</summary>
    public string Label { get; }

    /// <summary>获取属性的原始 JSON Schema。</summary>
    public JsonElement Schema { get; }

    /// <summary>获取属性是否为必填。</summary>
    public bool IsRequired { get; }

    /// <summary>获取架构类型标签。</summary>
    public string TypeLabel { get; }

    /// <summary>获取用作提示文本的架构描述。</summary>
    public string Description { get; }

    /// <summary>获取是否应显示描述。</summary>
    public bool HasDescription => Description.Length > 0;

    /// <summary>获取为此参数生成的控件类型。</summary>
    public ToolParameterKind Kind { get; }

    /// <summary>获取值是否为自由格式的 JSON 文本（对象/数组/未知类型）。</summary>
    public bool IsJsonText { get; }

    /// <summary>获取是否应使用数值控件。</summary>
    public bool IsBoolean => Kind == ToolParameterKind.Boolean;

    /// <summary>获取是否应使用组合框控件。</summary>
    public bool IsEnum => Kind == ToolParameterKind.Enum;

    /// <summary>获取是否应使用文本控件。</summary>
    public bool IsText => Kind is ToolParameterKind.Text or ToolParameterKind.Number;

    /// <summary>获取是否应使用多行控件。</summary>
    public bool IsMultiline => Kind == ToolParameterKind.MultilineText;

    /// <summary>获取允许的枚举值。</summary>
    public List<string> EnumValues { get; }

    /// <summary>获取架构声明的独占最小值（当为数值时）。</summary>
    public double? Minimum { get; }

    /// <summary>获取架构声明的独占最大值（当为数值时）。</summary>
    public double? Maximum { get; }

    /// <summary>获取是否应显示架构边界。</summary>
    public bool HasBounds => Kind == ToolParameterKind.Number && (Minimum.HasValue || Maximum.HasValue);

    /// <summary>获取架构范围提示。</summary>
    public string BoundsText
    {
        get
        {
            if (!HasBounds)
            {
                return string.Empty;
            }

            if (Minimum.HasValue && Maximum.HasValue)
            {
                return $"范围 {FormatNumber(Minimum.Value)} ~ {FormatNumber(Maximum.Value)}";
            }

            return Minimum.HasValue ? $"最小值 {FormatNumber(Minimum.Value)}" : $"最大值 {FormatNumber(Maximum!.Value)}";
        }
    }

    /// <summary>获取结合描述、范围和默认值的提示。</summary>
    public string HintText
    {
        get
        {
            var parts = new List<string>();
            if (HasDescription)
            {
                parts.Add(Description);
            }

            if (HasBounds)
            {
                parts.Add(BoundsText);
            }

            if (_defaultValue != null)
            {
                parts.Add("默认 " + _defaultValue);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>获取字段名旁显示的输入类型标签。</summary>
    public string TypeHint => TypeLabel;

    /// <summary>获取数值/文本输入值。</summary>
    public string TextValue
    {
        get => _textValue;
        set
        {
            if (SetProperty(ref _textValue, value ?? string.Empty))
            {
                Validate();
            }
        }
    }

    /// <summary>获取或设置布尔值。</summary>
    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (SetProperty(ref _boolValue, value))
            {
                Validate();
            }
        }
    }

    /// <summary>获取或设置选中的枚举值。</summary>
    public string? SelectedEnumValue
    {
        get => _selectedEnumValue;
        set
        {
            if (SetProperty(ref _selectedEnumValue, value))
            {
                Validate();
            }
        }
    }

    /// <summary>获取当前的校验错误；值可接受时为 null。</summary>
    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ErrorBrush));
            }
        }
    }

    /// <summary>获取是否存在校验错误。</summary>
    public bool HasError => !string.IsNullOrEmpty(ValidationError);

    /// <summary>获取用于显示校验错误的画刷。</summary>
    public Brush ErrorBrush => UiBrushes.Danger;

    /// <summary>获取参数当前是否带有可用值。</summary>
    public bool HasValue => Kind switch
    {
        ToolParameterKind.Boolean => true,
        ToolParameterKind.Enum => !string.IsNullOrEmpty(SelectedEnumValue),
        _ => !string.IsNullOrWhiteSpace(TextValue)
    };

    /// <summary>校验当前值并更新 <see cref="ValidationError"/>。</summary>
    /// <returns>值有效时返回 <see langword="true"/>。</returns>
    public bool Validate()
    {
        if (Kind == ToolParameterKind.Number && !string.IsNullOrWhiteSpace(TextValue))
        {
            var isInteger = string.Equals(TypeLabel, "integer", StringComparison.OrdinalIgnoreCase);
            var valid = isInteger
                ? long.TryParse(TextValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                : double.TryParse(TextValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            if (!valid)
            {
                ValidationError = isInteger ? "请输入整数。" : "请输入数字。";
                return false;
            }
        }

        if (Kind == ToolParameterKind.MultilineText && IsJsonText && !string.IsNullOrWhiteSpace(TextValue))
        {
            try
            {
                using var _ = JsonDocument.Parse(TextValue);
            }
            catch (JsonException)
            {
                ValidationError = "请输入有效的 JSON。";
                return false;
            }
        }

        if (IsRequired && !HasValue)
        {
            ValidationError = "必填参数不能为空。";
            return false;
        }

        ValidationError = null;
        return true;
    }

    /// <summary>获取转换为 JSON 节点后的值；当参数应被省略时返回 null。</summary>
    /// <returns>适合放入参数对象的 JSON 值。</returns>
    public System.Text.Json.Nodes.JsonNode? ToJsonNode()
    {
        switch (Kind)
        {
            case ToolParameterKind.Boolean:
                return System.Text.Json.Nodes.JsonValue.Create(BoolValue);

            case ToolParameterKind.Enum:
                return string.IsNullOrEmpty(SelectedEnumValue)
                    ? null
                    : System.Text.Json.Nodes.JsonValue.Create(SelectedEnumValue);

            case ToolParameterKind.Number:
                if (string.IsNullOrWhiteSpace(TextValue))
                {
                    return null;
                }

                if (string.Equals(TypeLabel, "integer", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(TextValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    return System.Text.Json.Nodes.JsonValue.Create(integer);
                }

                return double.TryParse(TextValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? System.Text.Json.Nodes.JsonValue.Create(number)
                    : null;

            default:
                if (string.IsNullOrEmpty(TextValue))
                {
                    return null;
                }

                if (IsJsonText)
                {
                    try
                    {
                        return System.Text.Json.Nodes.JsonNode.Parse(TextValue);
                    }
                    catch (JsonException)
                    {
                        return System.Text.Json.Nodes.JsonValue.Create(TextValue);
                    }
                }

                return System.Text.Json.Nodes.JsonValue.Create(TextValue);
        }
    }

    /// <summary>从 JSON 节点设置值，用于提交原始 JSON 编辑器时。</summary>
    /// <param name="node">JSON 值；为 null 时清空字段。</param>
    public void SetFromJson(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (Kind)
        {
            case ToolParameterKind.Boolean:
                BoolValue = node is System.Text.Json.Nodes.JsonValue value
                            && value.TryGetValue<bool>(out var flag)
                            && flag;
                break;

            case ToolParameterKind.Enum:
                if (node is System.Text.Json.Nodes.JsonValue enumValue)
                {
                    var enumText = enumValue.ToString();
                    if (EnumValues.Contains(enumText))
                    {
                        SelectedEnumValue = enumText;
                    }
                }

                break;

            case ToolParameterKind.Number:
                TextValue = node switch
                {
                    null => string.Empty,
                    System.Text.Json.Nodes.JsonValue numeric => numeric.ToString(),
                    _ => node.ToJsonString()
                };
                break;

            default:
                if (node is System.Text.Json.Nodes.JsonValue stringValue
                    && stringValue.TryGetValue<string>(out var text))
                {
                    TextValue = text;
                }
                else
                {
                    TextValue = node?.ToJsonString() ?? string.Empty;
                }

                break;
        }
    }

    private static double? ReadNumber(JsonElement schema, string name)
    {
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number))
        {
            return number;
        }

        return null;
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
