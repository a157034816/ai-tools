using System;
using System.Collections.Generic;
using System.Text.Json;
using AiTools.Mcp;
using AiTools.Samples.McpExplorer.ViewModels;

namespace AiTools.Samples.McpExplorer;

/// <summary>已连接的 MCP 服务器提供的一个工具。</summary>
public sealed class ToolItemViewModel : ObservableObject
{
    private int _parameterCount;
    private string _parametersSummary = string.Empty;
    private bool _hasSchema;

    /// <summary>创建工具 ViewModel。</summary>
    /// <param name="tool">服务器上报的工具。</param>
    public ToolItemViewModel(McpTool tool)
    {
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        Parameters = BuildParameters(tool.InputSchema);
        ParametersSummary = Parameters.Count == 0
            ? "无参数"
            : string.Join("、", Parameters.ConvertAll(parameter => parameter.Name));
        _parameterCount = Parameters.Count;
        _hasSchema = tool.InputSchema is { } schema && schema.ValueKind == JsonValueKind.Object;
    }

    /// <summary>获取 MCP 工具。</summary>
    public McpTool Tool { get; }

    /// <summary>获取工具名称。</summary>
    public string Name => Tool.Name;

    /// <summary>获取工具描述。</summary>
    public string Description =>
        string.IsNullOrWhiteSpace(Tool.Description) ? "（无描述）" : Tool.Description!;

    /// <summary>获取由架构驱动的参数列表。</summary>
    public List<ToolParameterViewModel> Parameters { get; }

    /// <summary>获取参数数量。</summary>
    public int ParameterCount
    {
        get => _parameterCount;
        private set => SetProperty(ref _parameterCount, value);
    }

    /// <summary>获取参数名称的紧凑摘要。</summary>
    public string ParametersSummary
    {
        get => _parametersSummary;
        private set => SetProperty(ref _parametersSummary, value);
    }

    /// <summary>获取工具是否声明了 JSON 对象架构。</summary>
    public bool HasSchema
    {
        get => _hasSchema;
        private set => SetProperty(ref _hasSchema, value);
    }

    /// <summary>获取工具是否声明了一个或多个参数。</summary>
    public bool HasParameters => Parameters.Count > 0;

    /// <summary>获取用于检查的格式化输入架构。</summary>
    public string InputSchemaJson =>
        Tool.InputSchema is { } schema
            ? ToolInvocation.PrettyJson(schema)
            : "（服务器未提供参数结构）";

    private static List<ToolParameterViewModel> BuildParameters(JsonElement? inputSchema)
    {
        var parameters = new List<ToolParameterViewModel>();
        if (inputSchema is not { } schema || schema.ValueKind != JsonValueKind.Object)
        {
            return parameters;
        }

        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return parameters;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name)
                {
                    required.Add(name);
                }
            }
        }

        foreach (var property in properties.EnumerateObject())
        {
            parameters.Add(new ToolParameterViewModel(property.Name, property.Value, required.Contains(property.Name)));
        }

        return parameters;
    }
}
