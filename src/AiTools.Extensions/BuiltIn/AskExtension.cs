using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions.BuiltIn;

/// <summary>一个带说明文本的选项。</summary>
public sealed class AskOption
{
    /// <summary>获取或设置选项标签（简短，作为条目标题）。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>获取或设置选项说明（解释该选项的含义或后果）。</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>一次提问的结果，用于在扩展之间传递"用户选择了什么"。</summary>
public sealed class AskResult
{
    /// <summary>获取或设置问题标题。</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>获取或设置用户选中的选项；取消或宿主不可交互时为 null。</summary>
    public AskOption? Selected { get; set; }

    /// <summary>获取或设置用户是否关闭了提问。</summary>
    public bool Cancelled { get; set; }

    /// <summary>获取或设置宿主是否无法显示交互界面。</summary>
    public bool Unavailable { get; set; }

    /// <summary>获取或设置宿主不可交互的原因。</summary>
    public string? Reason { get; set; }

    /// <summary>获取用户是否给出了明确选择。</summary>
    public bool HasAnswer => !Cancelled && !Unavailable && Selected != null && !string.IsNullOrWhiteSpace(Selected.Label);
}

/// <summary>
/// 通用提问工具 <c>ask</c>：向用户展示一个标题与若干选项，每个选项都带说明文本，
/// 返回用户选中的那一项。它不绑定任何具体工作流，任何需要用户拍板的场景都可以调用。
/// </summary>
/// <remarks>
/// 宿主同时实现 <see cref="IExtensionChoiceUi"/> 时使用带说明的富对话框；
/// 只实现 <see cref="IExtensionUi"/> 时把说明拼进选项文本退化处理；
/// 两者都不可用时返回 <c>unavailable</c>，由模型改为在对话里直接提问。
/// </remarks>
public sealed class AskExtension : IExtension
{
    /// <summary>扩展名。</summary>
    public const string ExtensionId = "ask";

    /// <summary>工具名。</summary>
    public const string ToolName = "ask";

    /// <inheritdoc />
    public string Name => ExtensionId;

    /// <inheritdoc />
    public string? Description => "Asks the user to choose between explained options.";

    /// <inheritdoc />
    public void Activate(IExtensionApi api)
    {
        api.RegisterTool(
            ToolDefinition.FromJsonSchema(
                ToolName,
                "Presents a question to the user as a title plus options that each carry an explanation of what they change, and returns the option they pick. " +
                "Use it when a decision belongs to the user (scope, trade-offs, preferences) and the choices are discrete. " +
                "Ask only after reading what the code already answers; do not use it to confirm facts you can verify yourself.",
                """
                {
                  "type": "object",
                  "properties": {
                    "title": { "type": "string", "description": "The question, short enough to serve as a dialog title." },
                    "message": { "type": "string", "description": "Optional context shown above the options; one or two sentences." },
                    "options": {
                      "type": "array",
                      "minItems": 2,
                      "items": {
                        "type": "object",
                        "properties": {
                          "label": { "type": "string", "description": "Short option title." },
                          "description": { "type": "string", "description": "What this option means and what it changes." }
                        },
                        "required": ["label", "description"]
                      }
                    }
                  },
                  "required": ["title", "options"]
                }
                """),
            ExecuteAsync,
            ToolExposure.Direct,
            readOnlyHint: true);
    }

    private static async Task<string> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken)
    {
        string title;
        string? message;
        var options = new List<AskOption>();
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error("The arguments must be a JSON object.");
            }

            title = ReadString(root, "title")?.Trim() ?? string.Empty;
            message = ReadString(root, "message");

            if (root.TryGetProperty("options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in optionsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        return Error("Every option must be an object with 'label' and 'description'.");
                    }

                    var label = ReadString(item, "label")?.Trim();
                    var description = ReadString(item, "description")?.Trim();
                    if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(description))
                    {
                        return Error("Every option needs both 'label' and 'description'.");
                    }

                    options.Add(new AskOption { Label = label!, Description = description! });
                }
            }
        }
        catch (JsonException ex)
        {
            return Error("Invalid JSON arguments: " + ex.Message);
        }

        if (title.Length == 0)
        {
            return Error("The 'title' argument is required.");
        }

        if (options.Count < 2)
        {
            return Error("Provide at least two options; a single option is not a question.");
        }

        var ui = context.Ui;
        if (ui == null || !ui.HasDialogs)
        {
            return Serialize(new
            {
                question = title,
                unavailable = true,
                reason = "Interactive prompts are unavailable in this host. Ask the question in plain text instead."
            });
        }

        ExtensionChoiceResult? choice = null;
        if (ui is IExtensionChoiceUi choiceUi)
        {
            choice = await choiceUi.ChooseAsync(title, message, ToChoices(options), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // 退化为纯文本选项：把说明拼进标签，保证信息不丢。
            var labels = new List<string>(options.Count);
            foreach (var option in options)
            {
                labels.Add(option.Label + " — " + option.Description);
            }

            var selected = await ui.SelectAsync(title, labels, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                var index = labels.IndexOf(selected!);
                choice = index >= 0
                    ? new ExtensionChoiceResult
                    {
                        Index = index,
                        Label = options[index].Label,
                        Description = options[index].Description
                    }
                    : new ExtensionChoiceResult { Index = -1, Label = selected! };
            }
        }

        if (choice == null)
        {
            return Serialize(new
            {
                question = title,
                cancelled = true,
                note = "The user dismissed the question. Stop and wait for their next message instead of guessing."
            });
        }

        return Serialize(new
        {
            question = title,
            selected = new { label = choice.Label, description = choice.Description }
        });
    }

    /// <summary>把提问结果 JSON 解析回对象；格式不符时返回 null。供其它扩展记录"用户被问过什么"。</summary>
    /// <param name="json">工具返回的 JSON 文本。</param>
    public static AskResult? ParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json!);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var question = ReadString(root, "question");
            if (string.IsNullOrWhiteSpace(question))
            {
                return null;
            }

            var result = new AskResult
            {
                Question = question!,
                Cancelled = root.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True,
                Unavailable = root.TryGetProperty("unavailable", out var unavailable) && unavailable.ValueKind == JsonValueKind.True,
                Reason = ReadString(root, "reason")
            };

            if (root.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.Object)
            {
                result.Selected = new AskOption
                {
                    Label = ReadString(selected, "label") ?? string.Empty,
                    Description = ReadString(selected, "description") ?? string.Empty
                };
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
    private static IReadOnlyList<ExtensionChoice> ToChoices(IReadOnlyList<AskOption> options)
    {
        var choices = new List<ExtensionChoice>(options.Count);
        foreach (var option in options)
        {
            choices.Add(new ExtensionChoice { Label = option.Label, Description = option.Description });
        }

        return choices;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Serialize(object value) => JsonSerializer.Serialize(value, ExtensionStateJson.Options);

    private static string Error(string message) => "{\"error\":" + JsonSerializer.Serialize(message) + "}";
}
