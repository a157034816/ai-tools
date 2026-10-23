using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Anthropic;
using AiTools.Tests.Support;
using Xunit;

namespace AiTools.Tests.Providers;

public class AnthropicMultimodalTests
{
    private static AnthropicOptions Options() => new() { ApiKey = "key" };

    [Fact]
    public async Task CompleteAsync_SendsImageAndDocumentBlocks()
    {
        var pdf = new byte[] { 37, 80, 68, 70 };

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        await client.CompleteAsync(new ChatRequest
        {
            Model = "claude-sonnet-4-5",
            Messages = new[]
            {
                ChatMessage.UserParts(
                    ContentPart.FromText("Summarize these."),
                    ContentPart.FromImageUri("https://example.com/a.png"),
                    ContentPart.FromImageBytes(new byte[] { 1, 2, 3 }, "image/jpeg"),
                    ContentPart.FromFileBytes(pdf, "application/pdf"))
            }
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("url", content[1].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("https://example.com/a.png", content[1].GetProperty("source").GetProperty("url").GetString());

        Assert.Equal("base64", content[2].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/jpeg", content[2].GetProperty("source").GetProperty("media_type").GetString());

        Assert.Equal("document", content[3].GetProperty("type").GetString());
        Assert.Equal("application/pdf", content[3].GetProperty("source").GetProperty("media_type").GetString());
    }

    [Fact]
    public async Task CompleteAsync_RejectsAudioInput()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        await Assert.ThrowsAsync<AiInvalidRequestException>(() => client.CompleteAsync(new ChatRequest
        {
            Model = "claude",
            Messages = new[]
            {
                ChatMessage.UserParts(ContentPart.FromAudioBytes(new byte[] { 1 }, "audio/wav"))
            }
        }));
    }

    [Fact]
    public async Task CompleteAsync_SendsToolsAndToolChoiceModes()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());
        var tool = ToolDefinition.FromJsonSchema("lookup", "Look up", """{"type":"object","properties":{"q":{"type":"string"}}}""");

        await client.CompleteAsync(new ChatRequest
        {
            Model = "claude",
            Messages = new[] { ChatMessage.User("hi") },
            Tools = new[] { tool },
            ToolChoice = ToolChoice.Required
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("lookup", body.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("any", body.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());

        await client.CompleteAsync(new ChatRequest
        {
            Model = "claude",
            Messages = new[] { ChatMessage.User("hi") },
            Tools = new[] { tool },
            ToolChoice = ToolChoice.None
        });

        using var body2 = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("none", body2.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CompleteAsync_CustomHeadersOverrideDefaults()
    {
        var options = new AnthropicOptions
        {
            ApiKey = "key",
            Beta = "interleaved-thinking-2025-05-14",
            DefaultHeaders = new Dictionary<string, string> { ["X-Trace"] = "abc" }
        };

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(SimpleResponse));
        var client = new AnthropicChatClient(options, handler.CreateClient());

        await client.CompleteAsync(ChatRequest.FromUser("claude", "hi"));

        Assert.Equal("interleaved-thinking-2025-05-14", handler.LastHeaders["anthropic-beta"][0]);
        Assert.Equal("abc", handler.LastHeaders["X-Trace"][0]);
    }

    [Fact]
    public async Task StreamAsync_StopsCleanlyOnCancellation()
    {
        const string sse = """
        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"a"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"b"}}

        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Sse(sse));
        var client = new AnthropicChatClient(Options(), handler.CreateClient());

        using var cts = new CancellationTokenSource();
        var received = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.StreamAsync(ChatRequest.FromUser("m", "hi"), cts.Token))
            {
                if (update.TextDelta != null)
                {
                    received.Add(update.TextDelta);
                    cts.Cancel();
                }
            }
        });

        Assert.Single(received);
    }

    private const string SimpleResponse = """
    {"id":"m1","model":"claude","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}
    """;
}
