using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>
/// 内置的 <c>codemode</c> 工具：按步执行其它工具，步与步之间可用 <c>argumentsFrom</c> 取上一步结果。
/// 无 JS 引擎——这里只保留 Pi <c>codemode</c> 的编排语义：可达范围、串联、失败中止与结果汇总。
/// </summary>
public static class CodemodeTool
{
    /// <summary>工具名。</summary>
    public const string Name = "codemode";

    /// <summary>一次调用允许的最大步数。</summary>
    public const int MaxSteps = 32;

    private const string Description =
        "Runs a fixed sequence of tool calls: each step names a tool and its arguments, and may map " +
        "parameters from the previous step's JSON result with argumentsFrom. Stops at the first failure.";

    /// <summary>构建工具定义。</summary>
    /// <param name="runtime">所属运行时。</param>
    /// <param name="reachesTools">本次编排允许触达的工具范围；null 表示不限制。</param>
    /// <param name="name">工具名；默认为 <c>codemode</c>。</param>
    public static ExtensionTool Create(ExtensionRuntime runtime, ToolReach? reachesTools = null, string name = Name)
    {
        if (runtime == null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A tool name is required.", nameof(name));
        }

        var reach = reachesTools ?? ToolReach.All();
        return new ExtensionTool
        {
            Definition = ToolDefinition.FromJsonSchema(
                name,
                Description,
                """
                {
                  "type": "object",
                  "properties": {
                    "steps": {
                      "type": "array",
                      "maxItems": 32,
                      "items": {
                        "type": "object",
                        "properties": {
                          "tool": { "type": "string", "description": "Tool to call." },
                          "arguments": { "type": "object", "description": "Arguments passed to the tool." },
                          "argumentsFrom": {
                            "type": "object",
                            "description": "Parameter name to dot path in the previous step's result, e.g. {\"id\":\"result.items.0.id\"}.",
                            "additionalProperties": { "type": "string" }
                          }
                        },
                        "required": ["tool"]
                      }
                    }
                  },
                  "required": ["steps"]
                }
                """),
            ExtensionName = ExtensionRuntime.RuntimeOwner,
            Exposure = ToolExposure.ModelOnly,
            ReachesTools = reach,
            Execute = (call, context, token) => ExecuteAsync(runtime, name, reach, context, call.ArgumentsJson, token)
        };
    }

    private static async Task<string> ExecuteAsync(
        ExtensionRuntime runtime,
        string name,
        ToolReach reach,
        ToolContext toolContext,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
        catch (JsonException ex)
        {
            return Failure(error: "Invalid JSON arguments: " + ex.Message, failedStep: 0);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("steps", out var stepsElement) ||
                stepsElement.ValueKind != JsonValueKind.Array)
            {
                return Failure(error: "steps must be an array", failedStep: 0);
            }

            var steps = stepsElement.EnumerateArray().ToArray();
            if (steps.Length > MaxSteps)
            {
                return Failure(error: $"At most {MaxSteps} steps are allowed", failedStep: 0);
            }

            var callerReach = string.IsNullOrWhiteSpace(toolContext.ParentToolName)
                ? null
                : runtime.ToolReachOf(toolContext.ParentToolName);

            var commandContext = new CommandContext
            {
                Runtime = runtime,
                State = runtime.State,
                SessionId = toolContext.SessionId,
                Ui = toolContext.Ui,
                EntryStore = toolContext.EntryStore,
                WorkingDirectory = Environment.CurrentDirectory
            };

            var results = new List<StepResult>(steps.Length);
            for (var index = 0; index < steps.Length; index++)
            {
                var step = steps[index];
                if (step.ValueKind != JsonValueKind.Object ||
                    !step.TryGetProperty("tool", out var toolElement) ||
                    toolElement.ValueKind != JsonValueKind.String)
                {
                    return Failure(error: "Each step needs a tool name", failedStep: index, results: results);
                }

                var toolName = toolElement.GetString() ?? string.Empty;
                var target = runtime.TryGetTool(toolName);
                if (target == null)
                {
                    return Failure(error: $"Unknown tool: {toolName}", failedStep: index, results: results);
                }

                if (!runtime.Allows(reach, target) ||
                    (callerReach != null && !runtime.Allows(callerReach, target)))
                {
                    return Failure(
                        error: $"Tool '{toolName}' is outside the reach of '{toolContext.ParentToolName ?? name}'",
                        failedStep: index,
                        results: results);
                }

                JsonObject arguments;
                try
                {
                    arguments = BuildArguments(step, index == 0 ? null : results[index - 1]);
                }
                catch (ArgumentException ex)
                {
                    return Failure(error: ex.Message, failedStep: index, results: results);
                }

                var result = await runtime.ExecuteToolAsync(
                    toolName,
                    arguments.ToJsonString(),
                    commandContext,
                    parentToolName: name,
                    cancellationToken).ConfigureAwait(false);

                if (result.IsError)
                {
                    return Failure(error: result.Text, failedStep: index, results: results);
                }

                results.Add(new StepResult(toolName, result.Text));
            }

            var payload = new JsonObject
            {
                ["ok"] = true,
                ["results"] = new JsonArray(results.Select(item => (JsonNode)item.ToJson()).ToArray())
            };

            return payload.ToJsonString();
        }
    }

    /// <summary>合并 <c>arguments</c> 与按路径取自上一步结果的 <c>argumentsFrom</c>。</summary>
    private static JsonObject BuildArguments(JsonElement step, StepResult? previous)
    {
        var arguments = new JsonObject();
        if (step.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in argumentsElement.EnumerateObject())
            {
                arguments[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        if (!step.TryGetProperty("argumentsFrom", out var fromElement) || fromElement.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        if (previous == null)
        {
            throw new ArgumentException("argumentsFrom needs a previous step result");
        }

        foreach (var property in fromElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"argumentsFrom.{property.Name} must be a string path");
            }

            var path = property.Value.GetString() ?? string.Empty;
            var resolved = previous.Resolve(path);
            if (resolved == null)
            {
                throw new ArgumentException($"argumentsFrom.{property.Name}: path '{path}' did not resolve");
            }

            arguments[property.Name] = JsonNode.Parse(resolved.Value.GetRawText());
        }

        return arguments;
    }

    private static string Failure(string error, int failedStep, IReadOnlyList<StepResult>? results = null)
    {
        var payload = new JsonObject
        {
            ["ok"] = false,
            ["failedStep"] = failedStep,
            ["error"] = error,
            ["results"] = new JsonArray((results ?? Array.Empty<StepResult>())
                .Select(item => (JsonNode)item.ToJson())
                .ToArray())
        };

        return payload.ToJsonString();
    }

    /// <summary>一步的执行结果；<see cref="ToJson"/> 是 <c>argumentsFrom</c> 的取值根。</summary>
    private sealed class StepResult
    {
        private readonly JsonElement? _value;

        public StepResult(string tool, string text)
        {
            Tool = tool;
            Text = text;
            try
            {
                using var document = JsonDocument.Parse(text);
                _value = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                _value = null;
            }
        }

        public string Tool { get; }

        public string Text { get; }

        public JsonObject ToJson()
        {
            var node = new JsonObject { ["tool"] = Tool, ["ok"] = true };
            node["result"] = _value.HasValue
                ? JsonNode.Parse(_value.Value.GetRawText())
                : JsonValue.Create(Text);
            return node;
        }

        /// <summary>
        /// 按点路径取值（支持数组下标）。根是本步结果对象（<c>result.items.0.id</c>）；
        /// 若首段不存在而结果本身是对象，则退化为直接对结果取值（<c>items.0.id</c>）。
        /// </summary>
        public JsonElement? Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var resolved = ResolveFrom(ToJson().ToJsonString(), path);
            if (resolved == null && _value.HasValue && _value.Value.ValueKind == JsonValueKind.Object)
            {
                resolved = ResolveFrom(_value.Value.GetRawText(), path);
            }

            return resolved;
        }

        private static JsonElement? ResolveFrom(string json, string path)
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;
            foreach (var segment in path.Split('.'))
            {
                if (segment.Length == 0)
                {
                    return null;
                }

                if (current.ValueKind == JsonValueKind.Object)
                {
                    if (!current.TryGetProperty(segment, out current))
                    {
                        return null;
                    }
                }
                else if (current.ValueKind == JsonValueKind.Array &&
                         int.TryParse(segment, out var index) &&
                         index >= 0 && index < current.GetArrayLength())
                {
                    current = current[index];
                }
                else
                {
                    return null;
                }
            }

            return current.Clone();
        }
    }
}
