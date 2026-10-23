using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions.BuiltIn;

/// <summary>计划中的一步。</summary>
public sealed class PlanStep
{
    /// <summary>获取或设置步骤标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>获取或设置要创建或修改的文件路径。</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>获取或设置变更内容。</summary>
    public string? Change { get; set; }

    /// <summary>获取或设置验证方式。</summary>
    public string? Verify { get; set; }

    /// <summary>获取或设置该步骤是否完成。</summary>
    public bool Done { get; set; }
}

/// <summary>计划状态。</summary>
public sealed class PlanState
{
    /// <summary>获取或设置计划针对的任务。</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>获取或设置现状说明。</summary>
    public string? Context { get; set; }

    /// <summary>获取或设置步骤。</summary>
    public List<PlanStep> Steps { get; set; } = new();

    /// <summary>获取或设置风险。</summary>
    public List<string> Risks { get; set; } = new();

    /// <summary>获取或设置范围外事项。</summary>
    public List<string> OutOfScope { get; set; } = new();

    /// <summary>获取或设置待确认问题。</summary>
    public List<string> OpenQuestions { get; set; } = new();

    /// <summary>获取或设置用户是否已确认计划。</summary>
    public bool Approved { get; set; }
}

/// <summary>
/// <c>/plan</c>：产出分步实施计划并跟踪每一步的完成情况。
/// </summary>
/// <remarks>
/// 与 <see cref="GoalExtension"/> 相同的形状：命令建立状态、工具推进状态、
/// <c>BeforeAgentStart</c> 把计划回灌进提示词。计划在用户确认前不得开始实施。
/// </remarks>
public sealed class PlanExtension : IExtension
{
    /// <summary>扩展名，同时作为状态桶名。</summary>
    public const string ExtensionId = "plan";

    /// <summary>保存计划的工具名。</summary>
    public const string SaveToolName = "plan_save";

    /// <summary>更新步骤状态的工具名。</summary>
    public const string StepToolName = "plan_step";

    private const string StateKey = "current";

    /// <inheritdoc />
    public string Name => ExtensionId;

    /// <inheritdoc />
    public string? Description => "Produces a grounded implementation plan and tracks its steps.";

    /// <inheritdoc />
    public void Activate(IExtensionApi api)
    {
        api.RegisterCommand("plan", "为一次改动产出可执行的实施计划", HandlePlanAsync, "<任务描述>");

        api.RegisterTool(
            ToolDefinition.FromJsonSchema(
                SaveToolName,
                "Records the implementation plan after you have confirmed the touched files and callers. Call this once, before implementing.",
                """
                {
                  "type": "object",
                  "properties": {
                    "task": { "type": "string" },
                    "context": { "type": "string", "description": "What exists today and why it does not satisfy the task." },
                    "steps": {
                      "type": "array",
                      "minItems": 1,
                      "items": {
                        "type": "object",
                        "properties": {
                          "title": { "type": "string" },
                          "files": { "type": "array", "items": { "type": "string" } },
                          "change": { "type": "string" },
                          "verify": { "type": "string", "description": "The exact command, test, or scenario that proves the step." }
                        },
                        "required": ["title", "verify"]
                      }
                    },
                    "risks": { "type": "array", "items": { "type": "string" } },
                    "outOfScope": { "type": "array", "items": { "type": "string" } },
                    "openQuestions": { "type": "array", "items": { "type": "string" } }
                  },
                  "required": ["task", "steps"]
                }
                """),
            ExecuteSaveAsync);

        api.RegisterTool(
            ToolDefinition.FromJsonSchema(
                StepToolName,
                "Marks a plan step done, or asks for the plan to be approved before implementation.",
                """
                {
                  "type": "object",
                  "properties": {
                    "completeStep": { "type": "integer", "description": "0-based index of the step now done." },
                    "approve": { "type": "boolean", "description": "Set true once the user confirmed the plan." }
                  }
                }
                """),
            ExecuteStepAsync);

        api.On<BeforeAgentStartEvent>(ExtensionEvent.BeforeAgentStart, AppendPlanSectionAsync);
    }

    private static Task<CommandResult> HandlePlanAsync(CommandContext context, CommandArguments arguments, CancellationToken cancellationToken)
    {
        var task = arguments.Raw.Trim();

        if (task.Length == 0)
        {
            var existing = context.State.Get<PlanState>(ExtensionId, StateKey);
            if (existing != null && !string.IsNullOrWhiteSpace(existing.Task))
            {
                return Task.FromResult(CommandResult.Display(Describe(existing)));
            }

            return Task.FromResult(CommandResult.Display(
                "用法：`/plan <任务描述>`。\n\n例如 `/plan 把扩展状态持久化到会话记录`。"));
        }

        context.State.Set(ExtensionId, StateKey, new PlanState { Task = task });
        context.NotifyUser($"开始制定计划：{task}", ExtensionNoticeKind.Info);

        var builder = new StringBuilder();
        builder.AppendLine("The user asked for an implementation plan:");
        builder.AppendLine();
        builder.Append("> ").AppendLine(task);
        builder.AppendLine();
        builder.AppendLine("Ground the plan in the real codebase before writing it:");
        builder.AppendLine("- Locate the files, types, and entry points the task touches. Never plan against files you have not confirmed exist.");
        builder.AppendLine("- Read the current implementation of the code you intend to change.");
        builder.AppendLine("- Find every caller of the symbols you will change.");
        builder.AppendLine("- Reuse the pattern the project already uses instead of introducing a second one.");
        builder.AppendLine();
        builder.AppendLine("Then call `" + SaveToolName + "` with:");
        builder.AppendLine("- `context`: what exists today and why it does not satisfy the task.");
        builder.AppendLine("- `steps`: ordered steps, each with `files`, `change`, and a `verify` that names an exact command, test, or scenario. Shared foundations land first; callers migrate in the same step that removes the old path.");
        builder.AppendLine("- `risks`: each with the safe option. `outOfScope`, `openQuestions` when they exist.");
        builder.AppendLine();
        builder.AppendLine("Do not plan tests that only re-assert wiring or copy implementation details. Do not pad the plan with refactors the task did not ask for.");
        builder.AppendLine();
        builder.AppendLine("After recording the plan, present it and stop. Do not start implementing until the user confirms; when they do, call `" + StepToolName + "` with `approve: true`.");

        return Task.FromResult(CommandResult.Prompt(builder.ToString()));
    }

    private static Task<string> ExecuteSaveAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var root = document.RootElement;

        var task = ReadString(root, "task");
        if (string.IsNullOrWhiteSpace(task))
        {
            return Task.FromResult("{\"error\":\"The 'task' argument is required.\"}");
        }

        var steps = new List<PlanStep>();
        if (root.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stepsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = ReadString(item, "title");
                var verify = ReadString(item, "verify");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(verify))
                {
                    return Task.FromResult("{\"error\":\"Every step needs 'title' and 'verify'.\"}");
                }

                steps.Add(new PlanStep
                {
                    Title = title!,
                    Verify = verify,
                    Change = ReadString(item, "change"),
                    Files = ReadStringArray(item, "files")
                });
            }
        }

        if (steps.Count == 0)
        {
            return Task.FromResult("{\"error\":\"At least one step is required.\"}");
        }

        var state = new PlanState
        {
            Task = task!,
            Context = ReadString(root, "context"),
            Steps = steps,
            Risks = ReadStringArray(root, "risks"),
            OutOfScope = ReadStringArray(root, "outOfScope"),
            OpenQuestions = ReadStringArray(root, "openQuestions"),
            Approved = false
        };

        context.State.Set(ExtensionId, StateKey, state);

        var builder = new StringBuilder();
        builder.AppendLine("Plan recorded: " + steps.Count + " step(s).");
        builder.AppendLine();
        builder.AppendLine(BuildSection(state));
        builder.AppendLine();
        builder.AppendLine("Present this plan to the user and stop. Implementation starts only after they confirm.");

        return Task.FromResult(builder.ToString());
    }

    private static Task<string> ExecuteStepAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken)
    {
        var state = context.State.Get<PlanState>(ExtensionId, StateKey);
        if (state == null || string.IsNullOrWhiteSpace(state.Task))
        {
            return Task.FromResult("{\"error\":\"No plan is recorded. Ask the user to run /plan first.\"}");
        }

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var root = document.RootElement;
        var changes = new List<string>();

        if (root.TryGetProperty("completeStep", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number)
        {
            var index = indexElement.GetInt32();
            if (index < 0 || index >= state.Steps.Count)
            {
                return Task.FromResult("{\"error\":\"completeStep is out of range (0-" + (state.Steps.Count - 1) + ").\"}");
            }

            state.Steps[index].Done = true;
            changes.Add("step " + index + " done: " + state.Steps[index].Title);
        }

        if (root.TryGetProperty("approve", out var approveElement) &&
            approveElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            state.Approved = approveElement.GetBoolean();
            changes.Add(state.Approved ? "plan approved" : "plan no longer approved");
        }

        if (changes.Count == 0)
        {
            return Task.FromResult("{\"error\":\"Nothing to update: pass completeStep or approve.\"}");
        }

        context.State.Set(ExtensionId, StateKey, state);

        var builder = new StringBuilder();
        builder.AppendLine("Plan updated.");
        foreach (var change in changes)
        {
            builder.Append("- ").AppendLine(change);
        }

        builder.AppendLine();
        builder.Append(Describe(state));

        return Task.FromResult(builder.ToString());
    }

    private static Task<BeforeAgentStartEvent?> AppendPlanSectionAsync(BeforeAgentStartEvent @event, CommandContext context, CancellationToken cancellationToken)
    {
        var state = context.State.Get<PlanState>(ExtensionId, StateKey);
        if (state == null || string.IsNullOrWhiteSpace(state.Task) || state.Steps.Count == 0)
        {
            return Task.FromResult<BeforeAgentStartEvent?>(null);
        }

        @event.PromptSections.Add(BuildSection(state));
        return Task.FromResult<BeforeAgentStartEvent?>(null);
    }

    /// <summary>构建注入系统提示的计划段落。独立暴露以便测试与复用。</summary>
    public static string BuildSection(PlanState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Implementation plan");
        builder.AppendLine();
        builder.Append("Task: ").AppendLine(state.Task);

        if (!string.IsNullOrWhiteSpace(state.Context))
        {
            builder.Append("Context: ").AppendLine(state.Context);
        }

        builder.AppendLine("Steps:");
        for (var index = 0; index < state.Steps.Count; index++)
        {
            var step = state.Steps[index];
            builder.Append("- [").Append(step.Done ? 'x' : ' ').Append("] ").Append(index).Append(". ").AppendLine(step.Title);
            if (step.Files.Count > 0)
            {
                builder.Append("  Files: ").AppendLine(string.Join(", ", step.Files));
            }

            if (!string.IsNullOrWhiteSpace(step.Change))
            {
                builder.Append("  Change: ").AppendLine(step.Change);
            }

            if (!string.IsNullOrWhiteSpace(step.Verify))
            {
                builder.Append("  Verify: ").AppendLine(step.Verify);
            }
        }

        if (state.Risks.Count > 0)
        {
            builder.AppendLine("Risks:");
            foreach (var risk in state.Risks)
            {
                builder.Append("- ").AppendLine(risk);
            }
        }

        if (state.OutOfScope.Count > 0)
        {
            builder.AppendLine("Out of scope:");
            foreach (var item in state.OutOfScope)
            {
                builder.Append("- ").AppendLine(item);
            }
        }

        if (state.OpenQuestions.Count > 0)
        {
            builder.AppendLine("Open questions:");
            foreach (var question in state.OpenQuestions)
            {
                builder.Append("- ").AppendLine(question);
            }
        }

        builder.AppendLine();
        builder.AppendLine(state.Approved
            ? "The user approved this plan. Work the steps in order and call `" + StepToolName + "` with `completeStep` after each one is verified."
            : "The plan is NOT approved yet. Do not implement until the user confirms. If they ask for changes, call `" + SaveToolName + "` again.");

        return builder.ToString().TrimEnd();
    }

    private static string Describe(PlanState state)
    {
        var builder = new StringBuilder();
        builder.Append("Plan: ").Append(state.Task).AppendLine();
        builder.Append("Steps: ").Append(state.Steps.Count(step => step.Done)).Append('/').Append(state.Steps.Count);
        if (!state.Approved)
        {
            builder.Append(" · 待确认");
        }

        return builder.ToString();
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> ReadStringArray(JsonElement element, string property)
    {
        var result = new List<string>();
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text!.Trim());
                }
            }
        }

        return result;
    }
}
