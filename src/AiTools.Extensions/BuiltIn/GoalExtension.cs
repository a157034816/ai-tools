using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Tools;

namespace AiTools.Extensions.BuiltIn;

/// <summary>目标的一条成功标准。</summary>
public sealed class GoalCriterion
{
    /// <summary>获取或设置标准描述。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>获取或设置该标准是否已被可观察证据证明。</summary>
    public bool Done { get; set; }

    /// <summary>获取或设置证明该标准的证据（命令、文件或测试名）。</summary>
    public string? Evidence { get; set; }
}

/// <summary>目标跟踪状态。</summary>
public sealed class GoalState
{
    /// <summary>获取或设置目标（一句结果导向的陈述）。</summary>
    public string Objective { get; set; } = string.Empty;

    /// <summary>获取或设置成功标准。</summary>
    public List<GoalCriterion> Criteria { get; set; } = new();

    /// <summary>获取或设置计划步骤。</summary>
    public List<string> Steps { get; set; } = new();

    /// <summary>获取或设置已完成的步骤索引（从 0 开始）。</summary>
    public List<int> CompletedSteps { get; set; } = new();

    /// <summary>获取或设置已知假设。</summary>
    public List<string> Assumptions { get; set; } = new();

    /// <summary>获取或设置创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>获取是否所有标准都已完成。</summary>
    public bool IsComplete => Criteria.Count > 0 && Criteria.All(criterion => criterion.Done);
}

/// <summary>
/// <c>/goal</c>：把目标转成可验证的成功标准与步骤清单，并在后续每一轮把进度注入上下文。
/// </summary>
/// <remarks>
/// 这是 Pi 式扩展的参考实现：命令负责建立状态，工具负责让模型推进状态，
/// <c>BeforeAgentStart</c> 负责把状态回灌到提示词。状态只存于 <see cref="ExtensionStateStore"/>，
/// 不占用对话历史。
/// </remarks>
public sealed class GoalExtension : IExtension
{
    /// <summary>扩展名，同时作为状态桶名。</summary>
    public const string ExtensionId = "goal";

    /// <summary>记录/更新目标计划的工具名。</summary>
    public const string PlanToolName = "goal_plan";

    /// <summary>推进目标状态的工具名。</summary>
    public const string UpdateToolName = "goal_update";

    private const string StateKey = "current";

    /// <inheritdoc />
    public string Name => ExtensionId;

    /// <inheritdoc />
    public string? Description => "Sets and tracks a session goal with verifiable criteria.";

    /// <inheritdoc />
    public void Activate(IExtensionApi api)
    {
        api.RegisterCommand("goal", "设定并跟踪本次会话的目标", HandleGoalAsync, "<目标描述>");

        api.RegisterTool(
            ToolDefinition.FromJsonSchema(
                PlanToolName,
                "Records the goal, its verifiable success criteria, and the ordered steps to reach them. Call this once right after /goal before doing any work.",
                """
                {
                  "type": "object",
                  "properties": {
                    "objective": { "type": "string", "description": "One sentence stating the outcome, not the activity." },
                    "criteria": {
                      "type": "array",
                      "minItems": 1,
                      "items": {
                        "type": "object",
                        "properties": { "text": { "type": "string", "description": "A checkable statement." } },
                        "required": ["text"]
                      }
                    },
                    "steps": { "type": "array", "items": { "type": "string" } },
                    "assumptions": { "type": "array", "items": { "type": "string" } }
                  },
                  "required": ["objective", "criteria", "steps"]
                }
                """),
            ExecutePlanAsync);

        api.RegisterTool(
            ToolDefinition.FromJsonSchema(
                UpdateToolName,
                "Updates goal progress: marks a step complete, marks a criterion as proven with evidence, or records a blocker. Call it whenever progress changes.",
                """
                {
                  "type": "object",
                  "properties": {
                    "completeStep": { "type": "integer", "description": "0-based index of the step that is now done." },
                    "proveCriterion": { "type": "integer", "description": "0-based index of the criterion now proven." },
                    "evidence": { "type": "string", "description": "The command, file, or test that proves the criterion." },
                    "blocker": { "type": "string", "description": "What is blocking progress." }
                  }
                }
                """),
            ExecuteUpdateAsync);

        api.On<BeforeAgentStartEvent>(ExtensionEvent.BeforeAgentStart, AppendGoalSectionAsync);
    }

    private static Task<CommandResult> HandleGoalAsync(CommandContext context, CommandArguments arguments, CancellationToken cancellationToken)
    {
        var objective = arguments.Raw.Trim();

        if (objective.Length == 0)
        {
            var existing = context.State.Get<GoalState>(ExtensionId, StateKey);
            if (existing != null && !string.IsNullOrWhiteSpace(existing.Objective))
            {
                return Task.FromResult(CommandResult.Display(Describe(existing, prefix: "当前目标")));
            }

            return Task.FromResult(CommandResult.Display(
                "用法：`/goal <目标>`。\n\n把目标写成一句结果导向的陈述，例如 `/goal 把 /goal 与 /plan 做成可持久化的扩展`。"));
        }

        // 新目标覆盖旧目标：只保留目标本身，等待模型调用 goal_plan 补全标准。
        context.State.Set(ExtensionId, StateKey, new GoalState { Objective = objective });
        context.NotifyUser($"目标已设定：{objective}", ExtensionNoticeKind.Success);

        var builder = new StringBuilder();
        builder.AppendLine("The user set a session goal:");
        builder.AppendLine();
        builder.Append("> ").AppendLine(objective);
        builder.AppendLine();
        builder.AppendLine("Before doing any work, call the `" + PlanToolName + "` tool to record:");
        builder.AppendLine("- `objective`: the goal restated as one outcome-oriented sentence.");
        builder.AppendLine("- `criteria`: 2–5 statements that a person could verify by running a command, reading a diff, or opening a file. No \"works well\" or \"clean code\".");
        builder.AppendLine("- `steps`: concrete steps in dependency order; each must leave the tree in a working state.");
        builder.AppendLine("- `assumptions`: anything you had to decide to make the plan concrete.");
        builder.AppendLine();
        builder.AppendLine("If an ambiguity would change the implementation, ask up to three questions instead of guessing. Otherwise state the assumption and proceed.");
        builder.AppendLine();
        builder.AppendLine("Then work the steps. After each meaningful step, call `" + UpdateToolName + "` with `completeStep` and, when a criterion is proven, `proveCriterion` plus the `evidence` (the exact command, test, or file). The goal is complete only when every criterion is proven by evidence you can name.");

        return Task.FromResult(CommandResult.Prompt(builder.ToString()));
    }

    private static Task<string> ExecutePlanAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var root = document.RootElement;

        var objective = ReadString(root, "objective");
        if (string.IsNullOrWhiteSpace(objective))
        {
            return Task.FromResult("{\"error\":\"The 'objective' argument is required.\"}");
        }

        var criteria = ReadStringArray(root, "criteria");
        if (criteria.Count == 0)
        {
            return Task.FromResult("{\"error\":\"At least one verifiable criterion is required.\"}");
        }

        var state = new GoalState
        {
            Objective = objective!,
            Criteria = criteria.Select(text => new GoalCriterion { Text = text }).ToList(),
            Steps = ReadStringArray(root, "steps"),
            Assumptions = ReadStringArray(root, "assumptions")
        };

        context.State.Set(ExtensionId, StateKey, state);

        var builder = new StringBuilder();
        builder.AppendLine("Goal recorded.");
        builder.Append("Objective: ").AppendLine(state.Objective);
        builder.Append("Criteria: ").AppendLine(state.Criteria.Count.ToString());
        builder.Append("Steps: ").AppendLine(state.Steps.Count.ToString());
        builder.AppendLine();
        builder.AppendLine("This state is injected into every following turn. Use `" + UpdateToolName + "` to mark steps and criteria complete as you go.");

        return Task.FromResult(builder.ToString());
    }

    private static Task<string> ExecuteUpdateAsync(ToolCall call, ToolContext context, CancellationToken cancellationToken)
    {
        var state = context.State.Get<GoalState>(ExtensionId, StateKey);
        if (state == null || string.IsNullOrWhiteSpace(state.Objective))
        {
            return Task.FromResult("{\"error\":\"No goal is set. Ask the user to run /goal first.\"}");
        }

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        var root = document.RootElement;
        var changes = new List<string>();

        if (root.TryGetProperty("completeStep", out var stepElement) && stepElement.ValueKind == JsonValueKind.Number)
        {
            var index = stepElement.GetInt32();
            if (index >= 0 && index < state.Steps.Count)
            {
                if (!state.CompletedSteps.Contains(index))
                {
                    state.CompletedSteps.Add(index);
                    state.CompletedSteps.Sort();
                }

                changes.Add("step " + index + " complete");
            }
            else
            {
                return Task.FromResult("{\"error\":\"completeStep is out of range (0-" + (state.Steps.Count - 1) + ").\"}");
            }
        }

        if (root.TryGetProperty("proveCriterion", out var criterionElement) && criterionElement.ValueKind == JsonValueKind.Number)
        {
            var index = criterionElement.GetInt32();
            if (index < 0 || index >= state.Criteria.Count)
            {
                return Task.FromResult("{\"error\":\"proveCriterion is out of range (0-" + (state.Criteria.Count - 1) + ").\"}");
            }

            var evidence = ReadString(root, "evidence");
            if (string.IsNullOrWhiteSpace(evidence))
            {
                return Task.FromResult("{\"error\":\"Proving a criterion requires 'evidence': the command, test, or file that proves it.\"}");
            }

            state.Criteria[index].Done = true;
            state.Criteria[index].Evidence = evidence;
            changes.Add("criterion " + index + " proven by: " + evidence);
        }

        var blocker = ReadString(root, "blocker");
        if (!string.IsNullOrWhiteSpace(blocker))
        {
            changes.Add("blocker recorded: " + blocker);
        }

        if (changes.Count == 0)
        {
            return Task.FromResult("{\"error\":\"Nothing to update: pass completeStep, proveCriterion (+ evidence), or blocker.\"}");
        }

        context.State.Set(ExtensionId, StateKey, state);

        var builder = new StringBuilder();
        builder.AppendLine("Goal updated.");
        foreach (var change in changes)
        {
            builder.Append("- ").AppendLine(change);
        }

        builder.AppendLine();
        builder.Append(Describe(state, prefix: "Status"));

        if (state.IsComplete)
        {
            builder.AppendLine();
            builder.AppendLine("All criteria are proven. Report completion, naming the evidence for each criterion.");
        }

        return Task.FromResult(builder.ToString());
    }

    private static Task<BeforeAgentStartEvent?> AppendGoalSectionAsync(BeforeAgentStartEvent @event, CommandContext context, CancellationToken cancellationToken)
    {
        var state = context.State.Get<GoalState>(ExtensionId, StateKey);
        if (state == null || string.IsNullOrWhiteSpace(state.Objective))
        {
            return Task.FromResult<BeforeAgentStartEvent?>(null);
        }

        @event.PromptSections.Add(BuildSection(state));
        return Task.FromResult<BeforeAgentStartEvent?>(null);
    }

    /// <summary>构建注入系统提示的目标段落。独立暴露以便测试与复用。</summary>
    public static string BuildSection(GoalState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Session goal");
        builder.AppendLine();
        builder.Append("Objective: ").AppendLine(state.Objective);

        if (state.Criteria.Count > 0)
        {
            builder.AppendLine("Success criteria:");
            for (var index = 0; index < state.Criteria.Count; index++)
            {
                var criterion = state.Criteria[index];
                builder.Append("- [").Append(criterion.Done ? 'x' : ' ').Append("] ").Append(criterion.Text);
                if (!string.IsNullOrWhiteSpace(criterion.Evidence))
                {
                    builder.Append(" — evidence: ").Append(criterion.Evidence);
                }

                builder.AppendLine();
            }
        }

        if (state.Steps.Count > 0)
        {
            var done = state.CompletedSteps.Count;
            builder.Append("Progress: ").Append(done).Append('/').Append(state.Steps.Count).AppendLine(" steps");
            for (var index = 0; index < state.Steps.Count; index++)
            {
                var complete = state.CompletedSteps.Contains(index);
                builder.Append("- [").Append(complete ? 'x' : ' ').Append("] ").Append(index).Append(". ").AppendLine(state.Steps[index]);
            }
        }

        if (state.Assumptions.Count > 0)
        {
            builder.AppendLine("Assumptions:");
            foreach (var assumption in state.Assumptions)
            {
                builder.Append("- ").AppendLine(assumption);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Keep the goal as the scope filter: decline work that does not serve it, and say why in one line. " +
                           "Report progress against this checklist as you go, and call `" + UpdateToolName + "` when a step or criterion changes state. " +
                           "Do not declare the goal complete until every criterion is proven by evidence.");

        return builder.ToString().TrimEnd();
    }

    private static string Describe(GoalState state, string prefix)
    {
        var builder = new StringBuilder();
        builder.Append(prefix).Append(": ").Append(state.Objective).AppendLine();
        builder.Append("Criteria: ").Append(state.Criteria.Count(criterion => criterion.Done)).Append('/').Append(state.Criteria.Count).AppendLine();
        builder.Append("Steps: ").Append(state.CompletedSteps.Count).Append('/').Append(state.Steps.Count);
        return builder.ToString();
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>读取字符串数组；元素可以是字符串，也可以是带 <c>text</c> 字段的对象。</summary>
    private static List<string> ReadStringArray(JsonElement element, string property, string itemProperty = "text")
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
            else if (item.ValueKind == JsonValueKind.Object &&
                     item.TryGetProperty(itemProperty, out var inner) &&
                     inner.ValueKind == JsonValueKind.String)
            {
                var text = inner.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text!.Trim());
                }
            }
        }

        return result;
    }
}
