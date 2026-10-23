using System.Net;
using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Anthropic;
using AiTools.Tests.Support;
using Xunit;

namespace AiTools.Tests.Providers;

public class AnthropicChatClientTests
{
    private static AnthropicOptions Options() => new() { ApiKey = "test-key" };

    [Fact]
    public async Task CompleteAsync_MapsRequestAndResponse()
    {
        const string responseJson = """
        {
          "id": "msg_123",
          "type": "message",
          "role": "assistant",
          "model": "claude-sonnet-4-5",
          "content": [{"type": "text", "text": "Hello!"}],
          "stop_reason": "end_turn",
          "usage": {"input_tokens": 12, "output_tokens": 3, "cache_read_input_tokens": 8, "cache_creation_input_tokens": 2}
        }
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        var response = await client.CompleteAsync(new ChatRequest
        {
            Model = "claude-sonnet-4-5",
            SystemPrompt = "You are helpful.",
            Messages = new[] { ChatMessage.User("Hi") },
            MaxOutputTokens = 256,
            Temperature = 0.2,
            StopSequences = new[] { "\n\n" }
        });

        Assert.Equal("Hello!", response.GetText());
        Assert.Equal("msg_123", response.Id);
        Assert.Equal("claude-sonnet-4-5", response.Model);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(12, response.Usage!.InputTokens);
        Assert.Equal(3, response.Usage.OutputTokens);
        Assert.Equal(8, response.Usage.CacheReadInputTokens);
        Assert.Equal(2, response.Usage.CacheCreationInputTokens);

        Assert.Equal("https://api.anthropic.com/v1/messages", handler.LastRequestUri!.ToString());
        Assert.Equal("test-key", handler.LastHeaders["x-api-key"][0]);
        Assert.Equal("2023-06-01", handler.LastHeaders["anthropic-version"][0]);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("claude-sonnet-4-5", root.GetProperty("model").GetString());
        Assert.Equal("You are helpful.", root.GetProperty("system").GetString());
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble(), 5);
        Assert.Equal("\n\n", root.GetProperty("stop_sequences")[0].GetString());
        Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Hi", root.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task CompleteAsync_MergesConsecutiveSameRoleMessages()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        await client.CompleteAsync(new ChatRequest
        {
            Model = "m",
            Messages = new[]
            {
                ChatMessage.User("first"),
                ChatMessage.User("second"),
                ChatMessage.Assistant("ok"),
                ChatMessage.ToolResult("call_1", "result"),
                ChatMessage.User("third")
            }
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = body.RootElement.GetProperty("messages");

        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal(2, messages[0].GetProperty("content").GetArrayLength()); // first + second merged
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal(2, messages[2].GetProperty("content").GetArrayLength()); // tool_result + third merged
        Assert.Equal("tool_result", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task CompleteAsync_MapsToolUseResponse()
    {
        const string responseJson = """
        {
          "id": "msg_tool",
          "model": "claude",
          "content": [
            {"type": "text", "text": "Let me check."},
            {"type": "tool_use", "id": "toolu_1", "name": "get_weather", "input": {"city": "Paris"}}
          ],
          "stop_reason": "tool_use",
          "usage": {"input_tokens": 5, "output_tokens": 2}
        }
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        var response = await client.CompleteAsync(new ChatRequest
        {
            Model = "claude",
            Messages = new[] { ChatMessage.User("weather?") },
            Tools = new[] { ToolDefinition.Create("get_weather", "weather", JsonSchemaBuilderHelper.CitySchema()) }
        });

        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal("Let me check.", response.GetText());
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("city").GetString());
        Assert.NotNull(call.ProviderData);
    }

    [Fact]
    public async Task CompleteAsync_UsesDefaultMaxTokensAndAppendsJsonSchemaInstruction()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        await client.CompleteAsync(new ChatRequest
        {
            Model = "m",
            Messages = new[] { ChatMessage.User("hi") },
            ResponseFormat = ResponseFormat.JsonSchema("person", """{"type":"object","properties":{"name":{"type":"string"}}}""")
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
        var system = body.RootElement.GetProperty("system").GetString();
        Assert.Contains("Respond only with a JSON object", system);
        Assert.Contains("\"name\"", system);
    }

    [Fact]
    public async Task CompleteAsync_MergesAdditionalParameters()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        await client.CompleteAsync(new ChatRequest
        {
            Model = "m",
            Messages = new[] { ChatMessage.User("hi") },
            AdditionalParameters = JsonDocument.Parse("""{"thinking":{"type":"enabled","budget_tokens":1024},"model":"override"}""").RootElement.Clone()
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("override", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StreamAsync_EmitsTextThinkingAndToolDeltas()
    {
        const string sse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","model":"claude-x","usage":{"input_tokens":10,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hmm"}}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_9","name":"lookup"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"q\":\"x\"}"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":20}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Sse(sse));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in client.StreamAsync(new ChatRequest
        {
            Model = "claude-x",
            Messages = new[] { ChatMessage.User("hi") }
        }))
        {
            updates.Add(update);
        }

        Assert.Equal("msg_1", updates.First(u => u.ResponseId != null).ResponseId);
        Assert.Equal("claude-x", updates.First(u => u.Model != null).Model);
        Assert.Equal("Hello", updates.First(u => u.TextDelta != null).TextDelta);
        Assert.Equal("hmm", updates.First(u => u.ReasoningDelta != null).ReasoningDelta);

        var toolStart = updates.First(u => u.ToolCallDelta?.Id != null).ToolCallDelta!;
        Assert.Equal(1, toolStart.Index);
        Assert.Equal("toolu_9", toolStart.Id);
        Assert.Equal("lookup", toolStart.Name);

        var toolArgs = updates.First(u => u.ToolCallDelta?.ArgumentsDelta != null).ToolCallDelta!;
        Assert.Equal("{\"q\":\"x\"}", toolArgs.ArgumentsDelta);

        var final = updates.Last(u => u.FinishReason != null);
        Assert.Equal(ChatFinishReason.ToolCalls, final.FinishReason);
        Assert.Equal(20, final.Usage!.OutputTokens);
        Assert.Equal(10, final.Usage.InputTokens); // carried over from message_start
    }

    [Fact]
    public async Task StreamAsync_ThrowsOnErrorEvent()
    {
        const string sse = """
        event: error
        data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}

        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Sse(sse));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        var ex = await Assert.ThrowsAsync<AiServiceException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(new ChatRequest { Model = "m" }))
            {
            }
        });

        Assert.Equal("overloaded_error", ex.ErrorCode);
        Assert.Contains("Overloaded", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_ThrowsMappedExceptionOnHttpError()
    {
        var handler = new ScriptedHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""")
        });
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        await Assert.ThrowsAsync<AiAuthenticationException>(() =>
            client.CompleteAsync(new ChatRequest { Model = "m", Messages = new[] { ChatMessage.User("hi") } }));
    }

    private const string SimpleResponse = """
    {"id":"m1","model":"claude","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
    """;
}

internal static class JsonSchemaBuilderHelper
{
    public static JsonElement CitySchema() => JsonDocument.Parse(
        """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""").RootElement.Clone();
}
