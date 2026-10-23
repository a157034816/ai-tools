using System.Text;
using AiTools.Abstractions;
using AiTools.Core.Json;
using AiTools.Core.Sse;
using AiTools.Core.Tools;

namespace AiTools.Samples.Console;

/// <summary>无需 API Key 即可运行的离线演示：Schema 构建、SSE 解析、工具循环装配。</summary>
internal static class OfflineDemo
{
    public static async Task RunAsync()
    {
        Demo.Section("离线演示 1 · 用代码构建工具 Schema");
        var schema = JsonSchemaBuilder.Object("天气查询参数")
            .Property("city", JsonSchemaBuilder.String("城市名称"))
            .Property("unit", JsonSchemaBuilder.Enumeration(new[] { "celsius", "fahrenheit" }, "温度单位"))
            .Property("days", JsonSchemaBuilder.Integer("预报天数"))
            .Required("city")
            .BuildJson();

        Demo.Ok("生成的 JSON Schema：");
        Demo.Note(schema);

        var tool = ToolBuilder.Create("get_weather", "查询天气", JsonSchemaBuilder.Object()
            .Property("city", JsonSchemaBuilder.String())
            .Required("city"));
        Demo.Ok($"工具定义：{tool.Name}（schema 字段数：{tool.ParametersSchema!.Value.EnumerateObject().Count()}）");

        Demo.Section("离线演示 2 · 解析 SSE 流");
        const string sse = """
        data: {"choices":[{"delta":{"content":"你"}}]}

        data: {"choices":[{"delta":{"content":"好"}}]}

        data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"get_weather","arguments":"{\"city\":\"北京\"}"}}]}}]}

        data: [DONE]

        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var builder = new StringBuilder();
        var toolJson = string.Empty;
        await foreach (var evt in SseParser.ParseAsync(stream))
        {
            if (!SseParser.TryParseJson(evt, out var chunk))
            {
                continue;
            }

            var delta = JsonHelpers.GetObject(JsonHelpers.GetArray(chunk, "choices")!.Value.First(), "delta");
            if (delta.HasValue)
            {
                builder.Append(JsonHelpers.GetString(delta.Value, "content"));
                var calls = JsonHelpers.GetArray(delta.Value, "tool_calls");
                if (calls.HasValue)
                {
                    toolJson = JsonHelpers.GetString(calls.Value.First().GetProperty("function"), "arguments") ?? string.Empty;
                }
            }
        }

        Demo.Ok($"聚合文本：{builder}");
        Demo.Ok($"工具参数：{toolJson}");

        Demo.Section("离线演示 3 · 把本地工具接入任意 Provider");
        Demo.Note("任意 IChatClient 都可直接使用下面的调用方式：");
        Demo.Note("""
            var answer = await chat.CompleteWithToolsAsync(request, LocalTools.ExecuteAsync);
            await foreach (var delta in chat.StreamWithToolsAsync(request, LocalTools.ExecuteAsync))
                Console.Write(delta.TextDelta);
            """);

        await Task.CompletedTask;
    }
}
