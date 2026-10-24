using System.Globalization;
using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Extensions;

namespace HelloCSharp;

/// <summary>
/// 最小可运行示例：一条带会话计数的命令、一个模型可调用的工具，以及一段每轮注入的提示词段落。
/// </summary>
/// <remarks>
/// 三个能力共用同一份状态（<see cref="ExtensionStateStore"/> 中以 <see cref="Name"/> 分桶），
/// 演示了 Pi 式扩展的典型形状：命令建立状态、工具推进状态、<c>BeforeAgentStart</c> 把状态回灌进提示词。
/// </remarks>
public sealed class HelloExtension : IExtension
{
    /// <summary>扩展标识，同时用作状态桶名。</summary>
    public const string ExtensionId = "hello-csharp";

    /// <summary>工具名。</summary>
    public const string CountToolName = "hello_csharp_count";

    private const string CountKey = "count";

    /// <inheritdoc />
    public string Name => ExtensionId;

    /// <inheritdoc />
    public string? Description => "示例扩展：带会话计数的命令、一个工具、一段每轮注入的提示词。";

    /// <inheritdoc />
    public void Activate(IExtensionApi api)
    {
        api.RegisterCommand(
            "hello",
            "向示例扩展问好，并累加本次会话的问候次数",
            HandleHelloAsync,
            "<名字>");

        api.RegisterTool(
            ToolDefinition.FromJsonSchema(
                CountToolName,
                "Reads or advances the hello-csharp greeting counter for the current session.",
                """
                {
                  "type": "object",
                  "properties": {
                    "increment": {
                      "type": "boolean",
                      "description": "Advance the counter by one before returning it."
                    }
                  }
                }
                """),
            ExecuteCountAsync,
            ToolExposure.Direct);

        api.On<BeforeAgentStartEvent>(ExtensionEvent.BeforeAgentStart, AppendCounterSectionAsync);
    }

    private static Task<CommandResult> HandleHelloAsync(
        CommandContext context,
        CommandArguments arguments,
        CancellationToken cancellationToken)
    {
        var count = Bump(context.State, 1);
        var name = arguments.Raw.Trim();

        if (name.Length == 0)
        {
            return Task.FromResult(CommandResult.Display(
                "你好！本次会话已问候 " + count.ToString(CultureInfo.InvariantCulture) + " 次。用法：`/hello <名字>`。"));
        }

        return Task.FromResult(CommandResult.Prompt(
            "请用一句话向 " + name + " 问好。这是本次会话第 " + count.ToString(CultureInfo.InvariantCulture) + " 次问候。"));
    }

    private static Task<string> ExecuteCountAsync(
        ToolCall call,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var increment = false;
        var json = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;
        using (var document = JsonDocument.Parse(json))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("increment", out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                increment = value.GetBoolean();
            }
        }

        var count = increment
            ? Bump(context.State, 1)
            : context.State.Get<int>(ExtensionId, CountKey);

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            count,
            sessionId = context.SessionId ?? string.Empty
        }));
    }

    private static Task<BeforeAgentStartEvent?> AppendCounterSectionAsync(
        BeforeAgentStartEvent @event,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var count = context.State.Get<int>(ExtensionId, CountKey);
        if (count <= 0)
        {
            return Task.FromResult<BeforeAgentStartEvent?>(null);
        }

        // 只在本轮没有该段落时追加，避免同一轮重复注入。
        var section = "## Hello C#\n\n本次会话已问候 **" + count.ToString(CultureInfo.InvariantCulture) + "** 次。";
        if (!@event.PromptSections.Contains(section))
        {
            @event.PromptSections.Add(section);
        }

        return Task.FromResult<BeforeAgentStartEvent?>(null);
    }

    private static int Bump(ExtensionStateStore state, int delta)
    {
        var count = state.Get<int>(ExtensionId, CountKey) + delta;
        state.Set(ExtensionId, CountKey, count);
        return count;
    }
}
