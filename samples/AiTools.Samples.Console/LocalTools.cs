using System.Text.Json;

namespace AiTools.Samples.Console;

/// <summary>示例用的本地工具函数（天气、数学、时间）。</summary>
internal static class LocalTools
{
    public static Abstractions.ToolDefinition WeatherTool { get; } = Abstractions.ToolDefinition.FromJsonSchema(
        "get_weather",
        "查询指定城市的当前天气",
        """
        {"type":"object","properties":{"city":{"type":"string","description":"城市名称，例如 北京"}},"required":["city"]}
        """);

    public static Abstractions.ToolDefinition TimeTool { get; } = Abstractions.ToolDefinition.FromJsonSchema(
        "get_time",
        "获取指定时区的当前时间",
        """
        {"type":"object","properties":{"timezone":{"type":"string","description":"IANA 时区，例如 Asia/Shanghai"}},"required":["timezone"]}
        """);

    /// <summary>模拟工具执行器。真实项目里这里会调用实际业务 API。</summary>
    public static Task<string> ExecuteAsync(Abstractions.ToolCall call, CancellationToken cancellationToken)
    {
        using var args = call.ParseArguments();
        var root = args.RootElement;

        string result = call.Name switch
        {
            "get_weather" => JsonSerializer.Serialize(new
            {
                city = root.GetProperty("city").GetString(),
                temperature_c = 24,
                condition = "晴",
                humidity = 45
            }),
            "get_time" => JsonSerializer.Serialize(new
            {
                timezone = root.GetProperty("timezone").GetString(),
                now = DateTimeOffset.UtcNow.ToString("O")
            }),
            _ => JsonSerializer.Serialize(new { error = $"unknown tool: {call.Name}" })
        };

        return Task.FromResult(result);
    }
}
