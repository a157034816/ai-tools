using AiTools.Abstractions;

namespace AiTools.Samples.Console;

/// <summary>示例程序入口：按环境变量自动选择可用的 Provider 并运行演示。</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Console.WriteLine("AiTools 示例程序");
        System.Console.WriteLine("================");
        Demo.Note("设置环境变量 OPENAI_API_KEY / ANTHROPIC_API_KEY / GEMINI_API_KEY 可启用对应演示。");
        Demo.Note("参数: offline | chat | stream | tools | embeddings | mcp | all（默认 all）");

        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        var ran = 0;
        if (mode is "all" or "offline")
        {
            await OfflineDemo.RunAsync();
            ran++;
        }
        if (mode is "all" or "chat")
        {
            ran += await RunChatDemoAsync();
        }

        if (mode is "all" or "stream")
        {
            ran += await RunStreamDemoAsync();
        }

        if (mode is "all" or "tools")
        {
            ran += await RunToolDemoAsync();
        }

        if (mode is "all" or "embeddings")
        {
            ran += await RunEmbeddingDemoAsync();
        }

        if (mode is "all" or "mcp")
        {
            ran += await RunMcpDemoAsync();
        }

        if (ran == 0)
        {
            Demo.Section("没有可运行的演示");
            Demo.Note("请设置至少一个 API Key 环境变量后重新运行。");
            return 1;
        }
        System.Console.WriteLine();
        Demo.Ok($"完成 {ran} 组演示。");
        return 0;
    }

    private static async Task<int> RunChatDemoAsync()
    {
        var session = DemoSession.TryCreate();
        if (session == null)
        {
            return 0;
        }

        Demo.Section($"聊天 · {session.ProviderName} / {session.ChatModel}");
        try
        {
            var response = await session.Chat.CompleteAsync(new ChatRequest
            {
                Model = session.ChatModel,
                SystemPrompt = "你是简洁的中文技术助手，回答不超过三句话。",
                Messages = new[] { ChatMessage.User("什么是依赖注入？用一句话说明。") },
                MaxOutputTokens = 512
            });

            Demo.Ok(Demo.Describe(response));
        }
        catch (Exception ex)
        {
            Demo.Fail(ex);
        }

        return 1;
    }

    private static async Task<int> RunStreamDemoAsync()
    {
        var session = DemoSession.TryCreate();
        if (session == null)
        {
            return 0;
        }

        Demo.Section($"流式 · {session.ProviderName} / {session.ChatModel}");
        try
        {
            await Demo.RenderStreamAsync(session.Chat.StreamAsync(new ChatRequest
            {
                Model = session.ChatModel,
                Messages = new[] { ChatMessage.User("用三行诗描述编译器的流水线。") },
                MaxOutputTokens = 512
            }));

            Demo.Ok("流式输出完成");
        }
        catch (Exception ex)
        {
            Demo.Fail(ex);
        }

        return 1;
    }

    private static async Task<int> RunToolDemoAsync()
    {
        var session = DemoSession.TryCreate();
        if (session == null)
        {
            return 0;
        }

        Demo.Section($"工具调用 · {session.ProviderName} / {session.ChatModel}");
        try
        {
            var response = await session.Chat.CompleteWithToolsAsync(
                new ChatRequest
                {
                    Model = session.ChatModel,
                    SystemPrompt = "你可以调用工具获取实时信息。回答时不要编造数据。",
                    Messages = new[] { ChatMessage.User("北京现在天气怎么样？适合出门吗？") },
                    Tools = new[] { LocalTools.WeatherTool, LocalTools.TimeTool },
                    MaxOutputTokens = 1024
                },
                LocalTools.ExecuteAsync);

            Demo.Ok(Demo.Describe(response));
        }
        catch (Exception ex)
        {
            Demo.Fail(ex);
        }

        return 1;
    }

    private static async Task<int> RunEmbeddingDemoAsync()
    {
        var session = DemoSession.TryCreate(requireChat: false);
        if (session?.Embeddings == null)
        {
            return 0;
        }

        Demo.Section($"嵌入 · {session.ProviderName} / {session.EmbeddingModel}");
        try
        {
            var response = await session.Embeddings.EmbedAsync(new EmbeddingRequest
            {
                Model = session.EmbeddingModel,
                Inputs = new[] { "编译器前端负责词法与语法分析", "编译器后端负责优化与代码生成" }
            });

            Demo.Ok($"生成 {response.Data.Count} 条向量，维度 {response.Data.FirstOrDefault()?.Vector.Length}");
        }
        catch (Exception ex)
        {
            Demo.Fail(ex);
        }

        return 1;
    }

    private static async Task<int> RunMcpDemoAsync()
    {
        return await McpDemo.RunAsync();
    }
}
