using System.Text;
using AiTools.Abstractions;

namespace AiTools.Samples.Console;

/// <summary>示例程序的共享工具。</summary>
internal static class Demo
{
    public static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>读取必需的环境变量；缺失时打印提示并返回 null。</summary>
    public static string? Required(string name, string hint)
    {
        var value = Env(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            System.Console.WriteLine($"  [跳过] 未设置环境变量 {name}（{hint}）");
            return null;
        }

        return value;
    }

    public static void Section(string title)
    {
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine($"── {title} ──");
        System.Console.ResetColor();
    }

    public static void Ok(string message) => System.Console.WriteLine($"  [成功] {message}");

    public static void Fail(Exception ex)
    {
        System.Console.ForegroundColor = ConsoleColor.Red;
        System.Console.WriteLine($"  [失败] {ex.GetType().Name}: {ex.Message}");
        System.Console.ResetColor();
    }

    public static void Note(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.WriteLine($"  {message}");
        System.Console.ResetColor();
    }

    /// <summary>打印流式输出（文本 + 推理过程）。</summary>
    public static async Task RenderStreamAsync(IAsyncEnumerable<ChatStreamUpdate> stream)
    {
        await foreach (var update in stream)
        {
            if (update.ReasoningDelta is { Length: > 0 } reasoning)
            {
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                System.Console.Write(reasoning);
                System.Console.ResetColor();
            }

            if (update.TextDelta is { Length: > 0 } text)
            {
                System.Console.Write(text);
            }
        }

        System.Console.WriteLine();
    }

    /// <summary>简单截断长文本，便于控制台展示。</summary>
    public static string Truncate(string text, int max = 200)
    {
        var single = text.Replace("\r", " ").Replace("\n", " ");
        return single.Length <= max ? single : single.Substring(0, max) + "…";
    }

    public static string Describe(ChatResponse response)
    {
        var builder = new StringBuilder();
        builder.Append($"[{response.Model}] {Truncate(response.GetText())}");
        if (response.Usage is { } usage)
        {
            builder.Append($" (tokens {usage.InputTokens ?? 0}→{usage.OutputTokens ?? 0})");
        }

        return builder.ToString();
    }
}
