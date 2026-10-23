using System.Net;
using System.Text.Json;
using AiTools.Abstractions;
using AiTools.OpenAI;
using AiTools.Tests.Support;
using Xunit;

namespace AiTools.Tests.Providers;

public class OpenAiChatClientTests
{
    private static OpenAiOptions Options() => new() { ApiKey = "sk-test" };

    [Fact]
    public async Task CompleteAsync_MapsRequestAndResponse()
    {
        const string responseJson = """
        {
          "id": "chatcmpl-1",
          "object": "chat.completion",
          "model": "gpt-4.1",
          "choices": [{"index": 0, "message": {"role": "assistant", "content": "Hello!"}, "finish_reason": "stop"}],
          "usage": {"prompt_tokens": 9, "completion_tokens": 2, "total_tokens": 11,
                    "prompt_tokens_details": {"cached_tokens": 4},
                    "completion_tokens_details": {"reasoning_tokens": 1}}
        }
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new OpenAiChatClient(Options(), handler.CreateClient());

        var response = await client.CompleteAsync(ChatRequest.FromUser("gpt-4.1", "Hi"));

        Assert.Equal("Hello!", response.GetText());
        Assert.Equal("chatcmpl-1", response.Id);
        Assert.Equal("gpt-4.1", response.Model);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(9, response.Usage!.InputTokens);
        Assert.Equal(2, response.Usage.OutputTokens);
        Assert.Equal(11, response.Usage.TotalTokens);
        Assert.Equal(4, response.Usage.CacheReadInputTokens);
        Assert.Equal(1, response.Usage.ReasoningTokens);

        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.LastRequestUri!.ToString());
        Assert.Equal("Bearer sk-test", handler.LastHeaders["Authorization"][0]);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("gpt-4.1", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task CompleteAsync_CompatibleProvider_UsesCustomBaseAddressAndName()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleTextResponse));
        var options = OpenAiOptions.ForCompatibleProvider("http://localhost:11434/v1/", "ollama", "ollama");
        var client = new OpenAiChatClient(options, handler.CreateClient());

        await client.CompleteAsync(ChatRequest.FromUser("qwen2.5", "hi"));

        Assert.Equal("ollama", client.ProviderName);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.LastRequestUri!.ToString());
        Assert.Equal("Bearer ollama", handler.LastHeaders["Authorization"][0]);
    }

    [Fact]
    public async Task CompleteAsync_MapsMultimodalAndTools()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleTextResponse));
        var client = new OpenAiChatClient(Options(), handler.CreateClient());

        var request = new ChatRequest
        {
            Model = "gpt-4o",
            SystemPrompt = "be nice",
            Messages = new[]
            {
                ChatMessage.UserParts(
                    ContentPart.FromText("What is this?"),
                    ContentPart.FromImageUri("https://example.com/a.png"))
            },
            Tools = new[] { ToolDefinition.FromJsonSchema("look", "lookup", """{"type":"object","properties":{"q":{"type":"string"}}}""") },
            ToolChoice = ToolChoice.Function("look"),
            MaxOutputTokens = 128,
            StopSequences = new[] { "STOP" },
            ResponseFormat = ResponseFormat.JsonObject
        };

        await client.CompleteAsync(request);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;

        Assert.Equal("be nice", root.GetProperty("messages")[0].GetProperty("content").GetString());

        var userContent = root.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("text", userContent[0].GetProperty("type").GetString());
        Assert.Equal("image_url", userContent[1].GetProperty("type").GetString());
        Assert.Equal("https://example.com/a.png", userContent[1].GetProperty("image_url").GetProperty("url").GetString());

        Assert.Equal("look", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("look", root.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(128, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("STOP", root.GetProperty("stop").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CompleteAsync_MapsToolCallsInResponseAndRoundTripsAssistantToolMessage()
    {
        const string responseJson = """
        {
          "id": "c1", "model": "gpt-4.1",
          "choices": [{"index":0,"message":{"role":"assistant","content":null,
            "tool_calls":[{"id":"call_a","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}]},
            "finish_reason":"tool_calls"}],
          "usage": {"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}
        }
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new OpenAiChatClient(Options(), handler.CreateClient());

        var response = await client.CompleteAsync(ChatRequest.FromUser("gpt-4.1", "weather"));

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_a", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("city").GetString());

        // 回传助手工具消息 + 工具结果时应生成规范结构。
        await client.CompleteAsync(new ChatRequest
        {
            Model = "gpt-4.1",
            Messages = new[]
            {
                ChatMessage.User("weather"),
                ChatMessage.AssistantWithToolCalls(new[] { call }),
                ChatMessage.ToolResult("call_a", "{\"temp\":25}")
            }
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(JsonValueKind.Null, messages[1].GetProperty("content").ValueKind);
        Assert.Equal("call_a", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_a", messages[2].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task CompleteAsync_MergesAdditionalParametersAndSupportsMaxCompletionTokens()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleTextResponse));
        var client = new OpenAiChatClient(Options(), handler.CreateClient());

        await client.CompleteAsync(new ChatRequest
        {
            Model = "o3",
            Messages = new[] { ChatMessage.User("hi") },
            MaxOutputTokens = 999,
            AdditionalParameters = JsonDocument.Parse("""{"max_completion_tokens":256,"reasoning_effort":"high"}""").RootElement.Clone()
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(256, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task CompleteAsync_ThrowsMappedExceptionOnHttpError()
    {
        var handler = new ScriptedHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"message":"rate limited","type":"requests","code":"rate_limit_exceeded"}}""")
        });
        var client = new OpenAiChatClient(Options(), handler.CreateClient());

        var ex = await Assert.ThrowsAsync<AiRateLimitException>(() =>
            client.CompleteAsync(ChatRequest.FromUser("gpt-4.1", "hi")));

        Assert.Equal("rate_limit_exceeded", ex.ErrorCode);
        Assert.Equal("openai", ex.ProviderName);
    }

    [Fact]
    public async Task StreamAsync_EmitsTextReasoningToolsUsageAndFinishReason()
    {
        const string sse = """
        data: {"id":"c1","model":"deepseek-chat","choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}

        data: {"id":"c1","model":"deepseek-chat","choices":[{"index":0,"delta":{"reasoning_content":"thinking..."}}]}

        data: {"id":"c1","model":"deepseek-chat","choices":[{"index":0,"delta":{"content":"Hello"}}]}

        data: {"id":"c1","model":"deepseek-chat","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"lookup","arguments":"{\"q\":"}}]}}]}

        data: {"id":"c1","model":"deepseek-chat","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"x\"}"}}]}}]}

        data: {"id":"c1","model":"deepseek-chat","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

        data: {"id":"c1","model":"deepseek-chat","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}

        data: [DONE]

        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Sse(sse));
        var client = new OpenAiChatClient(Options(), handler.CreateClient());

        var response = await client.StreamAsync(ChatRequest.FromUser("deepseek-chat", "hi")).AggregateAsync();

        Assert.Equal("Hello", response.GetText());
        Assert.Equal("c1", response.Id);
        Assert.Equal("deepseek-chat", response.Model);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal(15, response.Usage!.TotalTokens);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("{\"q\":\"x\"}", call.ArgumentsJson);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task StreamAsync_EmitsReasoningDeltaSeparately()
    {
        const string sse = """
        data: {"choices":[{"index":0,"delta":{"reasoning_content":"step 1"}}]}

        data: {"choices":[{"index":0,"delta":{"content":"answer"},"finish_reason":"stop"}]}

        data: [DONE]

        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Sse(sse));
        var client = new OpenAiChatClient(Options(), handler.CreateClient());

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in client.StreamAsync(ChatRequest.FromUser("reasoner", "hi")))
        {
            updates.Add(update);
        }

        Assert.Equal("step 1", updates.First(u => u.ReasoningDelta != null).ReasoningDelta);
        Assert.Equal("answer", updates.First(u => u.TextDelta != null).TextDelta);
        Assert.Equal(ChatFinishReason.Stop, updates.Last(u => u.FinishReason != null).FinishReason);
    }

    private const string SimpleTextResponse = """
    {"id":"x","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
    """;
}
