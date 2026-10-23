using System.Net;
using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Gemini;
using AiTools.Tests.Support;
using Xunit;

namespace AiTools.Tests.Providers;

public class GeminiChatClientTests
{
    private static GeminiOptions Options() => new() { ApiKey = "gemini-key" };

    [Fact]
    public async Task CompleteAsync_MapsRequestAndResponse()
    {
        const string responseJson = """
        {
          "candidates": [{
            "content": {"role": "model", "parts": [{"text": "Hello!"}]},
            "finishReason": "STOP"
          }],
          "usageMetadata": {"promptTokenCount": 7, "candidatesTokenCount": 2, "totalTokenCount": 9, "cachedContentTokenCount": 3, "thoughtsTokenCount": 1},
          "modelVersion": "gemini-2.5-flash"
        }
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new GeminiChatClient(Options(), handler.CreateClient());

        var response = await client.CompleteAsync(new ChatRequest
        {
            Model = "gemini-2.5-flash",
            SystemPrompt = "Be concise.",
            Messages = new[] { ChatMessage.User("Hi") },
            MaxOutputTokens = 128,
            Temperature = 0.5,
            TopP = 0.9,
            TopK = 40,
            StopSequences = new[] { "END" }
        });

        Assert.Equal("Hello!", response.GetText());
        Assert.Equal("gemini-2.5-flash", response.Model);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(7, response.Usage!.InputTokens);
        Assert.Equal(2, response.Usage.OutputTokens);
        Assert.Equal(9, response.Usage.TotalTokens);
        Assert.Equal(3, response.Usage.CacheReadInputTokens);
        Assert.Equal(1, response.Usage.ReasoningTokens);

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", handler.LastRequestUri!.ToString());
        Assert.Equal("gemini-key", handler.LastHeaders["x-goog-api-key"][0]);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("Be concise.", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("user", root.GetProperty("contents")[0].GetProperty("role").GetString());
        Assert.Equal("Hi", root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());

        var generation = root.GetProperty("generationConfig");
        Assert.Equal(128, generation.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(0.5, generation.GetProperty("temperature").GetDouble(), 5);
        Assert.Equal(0.9, generation.GetProperty("topP").GetDouble(), 5);
        Assert.Equal(40, generation.GetProperty("topK").GetInt32());
        Assert.Equal("END", generation.GetProperty("stopSequences")[0].GetString());
    }

    [Fact]
    public async Task CompleteAsync_NormalizesModelPrefixAndMapsToolCalls()
    {
        const string responseJson = """
        {
          "candidates": [{
            "content": {"role": "model", "parts": [
              {"text": "Checking."},
              {"functionCall": {"name": "get_weather", "args": {"city": "Paris"}}, "thoughtSignature": "sig-123"}
            ]},
            "finishReason": "STOP"
          }]
        }
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new GeminiChatClient(Options(), handler.CreateClient());

        var response = await client.CompleteAsync(new ChatRequest
        {
            Model = "models/gemini-2.5-flash",
            Messages = new[] { ChatMessage.User("weather?") },
            Tools = new[] { ToolDefinition.FromJsonSchema("get_weather", "weather", """{"type":"object","properties":{"city":{"type":"string"}}}""") },
            ToolChoice = ToolChoice.Function("get_weather")
        });

        Assert.Equal("Checking.", response.GetText());
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("city").GetString());
        Assert.NotNull(call.ProviderData);
        Assert.Equal("sig-123", call.ProviderData!.Value.GetProperty("thoughtSignature").GetString());

        // 模型前缀不应重复。
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent", handler.LastRequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("get_weather", root.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        Assert.Equal("ANY", root.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        Assert.Equal("get_weather", root.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("allowedFunctionNames")[0].GetString());
    }

    [Fact]
    public async Task CompleteAsync_RoundTripsFunctionResponseWithResolvedName()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var client = new GeminiChatClient(Options(), handler.CreateClient());

        var toolCall = new ToolCall("call_0", "lookup", "{\"q\":\"x\"}");
        await client.CompleteAsync(new ChatRequest
        {
            Model = "gemini-2.5-flash",
            Messages = new[]
            {
                ChatMessage.User("find"),
                ChatMessage.AssistantWithToolCalls(new[] { toolCall }),
                ChatMessage.ToolResult("call_0", "{\"result\":42}")
            }
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var contents = body.RootElement.GetProperty("contents");

        var modelParts = contents[1].GetProperty("parts");
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal("lookup", modelParts[0].GetProperty("functionCall").GetProperty("name").GetString());

        var userParts = contents[2].GetProperty("parts");
        Assert.Equal("user", contents[2].GetProperty("role").GetString());
        Assert.Equal("lookup", userParts[0].GetProperty("functionResponse").GetProperty("name").GetString());
        Assert.Equal(42, userParts[0].GetProperty("functionResponse").GetProperty("response").GetProperty("result").GetProperty("result").GetInt32());
    }

    [Fact]
    public async Task CompleteAsync_MapsJsonSchemaAndNormalizesTypes()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var client = new GeminiChatClient(Options(), handler.CreateClient());

        await client.CompleteAsync(new ChatRequest
        {
            Model = "gemini-2.5-flash",
            Messages = new[] { ChatMessage.User("give me a person") },
            ResponseFormat = ResponseFormat.JsonSchema("person", """
            {"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"},"tags":{"type":"array","items":{"type":"string"}}},"required":["name"]}
            """)
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var generation = body.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", generation.GetProperty("responseMimeType").GetString());

        var schema = generation.GetProperty("responseSchema");
        Assert.Equal("OBJECT", schema.GetProperty("type").GetString());
        Assert.Equal("STRING", schema.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("INTEGER", schema.GetProperty("properties").GetProperty("age").GetProperty("type").GetString());
        Assert.Equal("ARRAY", schema.GetProperty("properties").GetProperty("tags").GetProperty("type").GetString());
        Assert.Equal("STRING", schema.GetProperty("properties").GetProperty("tags").GetProperty("items").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CompleteAsync_MapsBlockedPromptToContentFilter()
    {
        const string responseJson = """{"promptFeedback":{"blockReason":"SAFETY"}}""";

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new GeminiChatClient(Options(), handler.CreateClient());

        var response = await client.CompleteAsync(ChatRequest.FromUser("gemini-2.5-flash", "..."));

        Assert.Equal(ChatFinishReason.ContentFilter, response.FinishReason);
        Assert.Equal(string.Empty, response.GetText());
    }

    [Fact]
    public async Task CompleteAsync_UseQueryStringApiKey()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var options = new GeminiOptions { ApiKey = "k 1", UseQueryStringApiKey = true };
        var client = new GeminiChatClient(options, handler.CreateClient());

        await client.CompleteAsync(ChatRequest.FromUser("gemini-2.5-flash", "hi"));

        Assert.Contains("key=k%201", handler.LastRequestUri!.Query);
        Assert.False(handler.LastHeaders.ContainsKey("x-goog-api-key"));
    }

    [Fact]
    public async Task StreamAsync_ParsesSseChunks()
    {
        const string sse = """
        data: {"candidates":[{"content":{"role":"model","parts":[{"text":"Hel"}]}}]}

        data: {"candidates":[{"content":{"role":"model","parts":[{"text":"lo"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5},"modelVersion":"gemini-2.5-flash"}

        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Sse(sse));
        var client = new GeminiChatClient(Options(), handler.CreateClient());

        var response = await client.StreamAsync(ChatRequest.FromUser("gemini-2.5-flash", "hi")).AggregateAsync();

        Assert.Equal("Hello", response.GetText());
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(5, response.Usage!.TotalTokens);
        Assert.Equal("gemini-2.5-flash", response.Model);
        Assert.Contains(":streamGenerateContent?alt=sse", handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task StreamAsync_EmitsFunctionCallAndThoughtReasoning()
    {
        const string sse = """
        data: {"candidates":[{"content":{"role":"model","parts":[{"text":"thinking","thought":true}]}}]}

        data: {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"lookup","args":{"q":"x"}}}]},"finishReason":"STOP"}]}

        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Sse(sse));
        var client = new GeminiChatClient(Options(), handler.CreateClient());

        var updates = new List<ChatStreamUpdate>();
        await foreach (var update in client.StreamAsync(ChatRequest.FromUser("gemini-2.5-flash", "hi")))
        {
            updates.Add(update);
        }

        Assert.Equal("thinking", updates.First(u => u.ReasoningDelta != null).ReasoningDelta);
        var toolDelta = updates.First(u => u.ToolCallDelta?.Name != null).ToolCallDelta!;
        Assert.Equal("lookup", toolDelta.Name);
        Assert.Equal("{\"q\":\"x\"}", toolDelta.ArgumentsDelta);
        // 带有 functionCall 时即使服务端报告 STOP，也应归一化为 ToolCalls，便于下游工具循环识别。
        Assert.Equal(ChatFinishReason.ToolCalls, updates.Last(u => u.FinishReason != null).FinishReason);
    }

    [Fact]
    public async Task CompleteAsync_ThrowsMappedException()
    {
        var handler = new ScriptedHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":403,"message":"API key not valid","status":"PERMISSION_DENIED"}}""")
        });
        var client = new GeminiChatClient(Options(), handler.CreateClient());

        var ex = await Assert.ThrowsAsync<AiAuthenticationException>(() =>
            client.CompleteAsync(ChatRequest.FromUser("gemini-2.5-flash", "hi")));

        Assert.Equal("PERMISSION_DENIED", ex.ErrorCode);
        Assert.Equal("gemini", ex.ProviderName);
    }

    private const string SimpleResponse = """
    {"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}
    """;
}
