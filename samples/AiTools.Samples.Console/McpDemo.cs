using AiTools.Abstractions;
using AiTools.Mcp;

namespace AiTools.Samples.Console;

/// <summary>MCP 客户端示例：连接 stdio MCP 服务器、列出工具、把工具交给模型调用。</summary>
internal static class McpDemo
{
    /// <summary>
    /// 运行示例。需要设置环境变量 MCP_SERVER_COMMAND（例如 "npx"），
    /// 以及可选 MCP_SERVER_ARGS（以空格分隔的参数）。
    /// </summary>
    public static async Task<int> RunAsync()
    {
        var command = Demo.Required("MCP_SERVER_COMMAND", "例如 npx");
        if (command == null)
        {
            Demo.Section("MCP 客户端");
            Demo.Note("设置 MCP_SERVER_COMMAND / MCP_SERVER_ARGS 可运行 MCP 示例；演示代码见 McpDemo.cs。");
            return 0;
        }

        var args = (Demo.Env("MCP_SERVER_ARGS") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Demo.Section("MCP 客户端");
        try
        {
            await using var transport = new StdioMcpTransport(new StdioTransportOptions
            {
                Command = command,
                Arguments = args
            });

            await using var client = new McpClient(transport);
            await client.InitializeAsync();

            Demo.Ok($"已连接 MCP 服务器：{client.ServerName} {client.ServerVersion}");

            var tools = await client.ListToolsAsync();
            Demo.Ok($"发现 {tools.Count} 个工具");
            foreach (var tool in tools.Take(10))
            {
                Demo.Note($"  - {tool.Name}: {Demo.Truncate(tool.Description ?? "(无描述)", 80)}");
            }

            var resources = await client.ListResourcesAsync();
            if (resources.Count > 0)
            {
                Demo.Ok($"发现 {resources.Count} 个资源");
            }

            // 把 MCP 工具交给模型（需要聊天 Provider 的环境变量）。
            var session = DemoSession.TryCreate(requireChat: false);
            if (session?.Chat == null || tools.Count == 0)
            {
                return 1;
            }

            var response = await session.Chat.CompleteWithToolsAsync(
                new ChatRequest
                {
                    Model = session.ChatModel,
                    SystemPrompt = "你可以使用 MCP 提供的工具。优先使用工具获取真实信息。",
                    Messages = new[] { ChatMessage.User("请调用一个可用工具，并简要说明结果。") },
                    Tools = McpToolBridge.ToToolDefinitions(tools),
                    MaxOutputTokens = 1024
                },
                McpToolBridge.CreateToolHandler(client, tools));

            Demo.Ok(Demo.Describe(response));
            return 1;
        }
        catch (Exception ex)
        {
            Demo.Fail(ex);
            return 1;
        }
    }
}
