using System.Text.Json;

namespace AiTools.Samples.McpToolServer;

/// <summary>示例服务器对外提供的 Prompt。</summary>
internal static class PromptCatalog
{
    /// <summary>要求模型解释某个已暴露工具的那条 Prompt 的名称。</summary>
    internal const string ExplainToolName = "explain_tool";

    /// <summary>写入 <c>prompts/list</c> 结果中的 <c>prompts</c> 数组。</summary>
    /// <param name="writer">目标写入器。</param>
    internal static void WriteList(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("prompts");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("name", ExplainToolName);
        writer.WriteString("description", "生成一段提示词，让模型解释指定 MCP 工具的用途、参数与适用场景。");
        writer.WritePropertyName("arguments");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("name", "toolName");
        writer.WriteString("description", "要解释的工具名称，例如 calculate。");
        writer.WriteBoolean("required", true);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    /// <summary>构建 <c>explain_tool</c> Prompt 的用户消息。</summary>
    /// <param name="tool">要解释的工具。</param>
    /// <returns>简体中文的提示词文本。</returns>
    internal static string BuildExplainToolPrompt(McpServerTool tool)
    {
        var schema = tool.Definition.ParametersSchema?.GetRawText() ?? "{}";
        return
            "请解释 MCP 工具 “" + tool.Definition.Name + "” 的用途、参数和适用场景。\n\n" +
            "工具说明：" + (tool.Definition.Description ?? "（无）") + "\n\n" +
            "输入参数 Schema：\n" + schema + "\n\n" +
            "请用简洁的中文说明：\n" +
            "1. 这个工具做什么；\n" +
            "2. 每个参数的含义与取值约束；\n" +
            "3. 给出一个典型的调用参数 JSON 示例；\n" +
            "4. 什么情况下不应使用它。";
    }
}
