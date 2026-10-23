using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Wpf.Configuration;

namespace AiTools.Wpf.Demo;

/// <summary>演示宿主实现的工具提供者：向模型暴露“当前时间”和“计算器”两个本地工具。</summary>
public sealed class DemoToolProvider : IChatToolProvider
{
    private static readonly ToolDefinition TimeTool = ToolDefinition.FromJsonSchema(
        "get_current_time",
        "获取当前本地时间",
        """{"type":"object","properties":{}}""");

    private static readonly ToolDefinition CalculatorTool = ToolDefinition.FromJsonSchema(
        "calculate",
        "计算两个数字的加、减、乘、除",
        """
        {
          "type": "object",
          "properties": {
            "a": { "type": "number", "description": "左操作数" },
            "op": { "type": "string", "enum": ["+", "-", "*", "/"], "description": "运算符" },
            "b": { "type": "number", "description": "右操作数" }
          },
          "required": ["a", "op", "b"]
        }
        """);

    public IReadOnlyList<ToolDefinition> GetTools() => new[] { TimeTool, CalculatorTool };

    public Task<string> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
    {
        using var arguments = call.ParseArguments();
        var root = arguments.RootElement;

        var result = call.Name switch
        {
            "get_current_time" => JsonSerializer.Serialize(new
            {
                local = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                utc = DateTimeOffset.UtcNow.ToString("O")
            }),
            "calculate" => Calculate(root),
            _ => JsonSerializer.Serialize(new { error = $"unknown tool: {call.Name}" })
        };

        return Task.FromResult(result);
    }

    private static string Calculate(JsonElement root)
    {
        var a = root.GetProperty("a").GetDouble();
        var b = root.GetProperty("b").GetDouble();
        var op = root.GetProperty("op").GetString();

        var value = op switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" when b != 0 => a / b,
            "/" => double.NaN,
            _ => double.NaN
        };

        return double.IsNaN(value)
            ? JsonSerializer.Serialize(new { error = "无法计算（除数为 0 或运算符未知）" })
            : JsonSerializer.Serialize(new { result = value });
    }
}
