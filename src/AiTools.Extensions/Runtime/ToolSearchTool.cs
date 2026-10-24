using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>
/// 内置的 <c>tool_search</c> 工具：在未声明的工具（<see cref="ToolExposure.Codemode"/> 与
/// <see cref="ToolExposure.Deferred"/>）里按子串查找，并激活命中项，使其在下一轮声明给模型。
/// 只在存在这类工具时由运行时提供。
/// </summary>
internal static class ToolSearchTool
{
    /// <summary>工具名。</summary>
    public const string Name = "tool_search";

    /// <summary>一次调用最多激活的工具数。</summary>
    public const int ActivateLimit = 10;

    private const string Description =
        "Searches tools that are not declared yet (codemode/deferred) by name, namespace, or description, " +
        "and activates the matches so they are declared from the next model call on.";

    /// <summary>构建工具定义。</summary>
    /// <param name="runtime">所属运行时。</param>
    public static ExtensionTool Create(ExtensionRuntime runtime)
        => new()
        {
            Definition = ToolDefinition.FromJsonSchema(
                Name,
                Description,
                """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Case-insensitive text matched against tool name, namespace name/alias, and description." },
                    "activate": { "type": "boolean", "description": "Activate the matches so they are declared to the model. Defaults to true." }
                  },
                  "required": ["query"]
                }
                """),
            ExtensionName = ExtensionRuntime.RuntimeOwner,
            Exposure = ToolExposure.ModelOnly,
            ReachesTools = ToolReach.All(),
            Execute = (call, _, token) => ExecuteAsync(runtime, call.ArgumentsJson, token)
        };

    private static Task<string> ExecuteAsync(ExtensionRuntime runtime, string argumentsJson, CancellationToken cancellationToken)
    {
        string query;
        var activate = true;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            query = root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("query", out var queryElement) &&
                    queryElement.ValueKind == JsonValueKind.String
                ? queryElement.GetString() ?? string.Empty
                : string.Empty;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("activate", out var activateElement) &&
                activateElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                activate = activateElement.GetBoolean();
            }
        }
        catch (JsonException ex)
        {
            return Task.FromResult(Error("Invalid JSON arguments: " + ex.Message));
        }

        var matches = Search(runtime, query);
        if (activate)
        {
            foreach (var match in matches.Take(ActivateLimit))
            {
                runtime.ActivateTool(match.Definition.Name);
            }
        }

        var results = matches.Select(match => new
        {
            name = match.Definition.Name,
            @namespace = match.Namespace,
            exposure = match.Exposure.ToString().ToLowerInvariant(),
            description = match.Definition.Description ?? string.Empty,
            activated = runtime.IsActiveTool(match.Definition.Name)
        });

        return Task.FromResult(JsonSerializer.Serialize(results));
    }

    /// <summary>
    /// 在可发现工具的候选中按子串匹配：工具名、命名空间名/别名、描述，均大小写不敏感。
    /// 空查询匹配全部候选。
    /// </summary>
    private static IReadOnlyList<ExtensionTool> Search(ExtensionRuntime runtime, string query)
    {
        var needle = query.Trim();
        var matches = new List<ExtensionTool>();
        foreach (var tool in runtime.BuildLoadout().All)
        {
            if (tool.Exposure is not (ToolExposure.Codemode or ToolExposure.Deferred))
            {
                continue;
            }

            if (needle.Length == 0 || Matches(runtime, tool, needle))
            {
                matches.Add(tool);
            }
        }

        return matches;
    }

    private static bool Matches(ExtensionRuntime runtime, ExtensionTool tool, string needle)
    {
        if (Contains(tool.Definition.Name, needle) || Contains(tool.Definition.Description, needle))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(tool.Namespace))
        {
            return false;
        }

        if (Contains(tool.Namespace, needle))
        {
            return true;
        }

        var declared = runtime.FindNamespace(tool.Namespace);
        if (declared == null)
        {
            return false;
        }

        return Contains(declared.Name, needle) || declared.Aliases.Any(alias => Contains(alias, needle));
    }

    /// <summary>大小写不敏感的子串匹配，同时接受分隔符差异（<c>my-docs</c> 与 <c>my_docs</c>）。</summary>
    private static bool Contains(string? value, string needle)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var text = value!;
        return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
               ToolNames.Canonicalize(text).Contains(ToolNames.Canonicalize(needle));
    }

    private static string Error(string message)
        => "{\"error\":" + JsonSerializer.Serialize(message) + "}";
}
