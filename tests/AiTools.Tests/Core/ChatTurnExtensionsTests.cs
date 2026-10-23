using System.Runtime.CompilerServices;
using AiTools.Abstractions;
using AiTools.Core.Chat;
using Xunit;

namespace AiTools.Tests.Core;

public class ChatTurnExtensionsTests
{
    [Fact]
    public async Task StreamTurnAsync_CollectsUpdatesAndAggregatesMessage()
    {
        var client = new ToolAwareClient(
            streamRounds: new[]
            {
                new[]
                {
                    new ChatStreamUpdate { TextDelta = "Let me " },
                    new ChatStreamUpdate { TextDelta = "check." },
                    new ChatStreamUpdate
                    {
                        ToolCallDelta = new ToolCallDelta { Index = 0, Id = "c1", Name = "lookup", ArgumentsDelta = "{}" }
                    },
                    new ChatStreamUpdate { FinishReason = ChatFinishReason.ToolCalls }
                }
            },
            completions: Array.Empty<ChatResponse>());

        var deltas = new List<string>();
        var turn = await client.StreamTurnAsync(
            ChatRequest.FromUser("m", "q"),
            update => deltas.Add(update.TextDelta ?? "<none>"));

        Assert.Equal(new[] { "Let me ", "check." }, deltas);
        Assert.Equal("Let me check.", turn.Text);
        Assert.Equal(ChatFinishReason.ToolCalls, turn.FinishReason);
        Assert.Single(turn.ToolCalls);
    }

    [Fact]
    public async Task StreamWithToolsExactAsync_UsesNonStreamingFallbackForToolRound()
    {
        var client = new ToolAwareClient(
            streamRounds: new[]
            {
                new[]
                {
                    new ChatStreamUpdate { TextDelta = "Checking..." },
                    new ChatStreamUpdate
                    {
                        ToolCallDelta = new ToolCallDelta { Index = 0, Id = "c1", Name = "lookup", ArgumentsDelta = "{\"q\":\"x\"}" }
                    },
                    new ChatStreamUpdate { FinishReason = ChatFinishReason.ToolCalls }
                },
                new[]
                {
                    new ChatStreamUpdate { TextDelta = "Done: 42" },
                    new ChatStreamUpdate { FinishReason = ChatFinishReason.Stop }
                }
            },
            completions: new[]
            {
                // 非流式回退：带有 ProviderData 的工具调用。
                new ChatResponse
                {
                    Message = ChatMessage.AssistantWithToolCalls(new[]
                    {
                        new ToolCall("c1", "lookup", "{\"q\":\"x\"}")
                        {
                            ProviderData = System.Text.Json.JsonDocument.Parse("{\"sig\":\"abc\"}").RootElement.Clone()
                        }
                    }),
                    FinishReason = ChatFinishReason.ToolCalls
                }
            });

        var text = new List<string>();
        var seen = new List<ToolCall>();
        var result = await client.StreamWithToolsExactAsync(
            ChatRequest.FromUser("m", "q"),
            (call, _) =>
            {
                seen.Add(call);
                return Task.FromResult("42");
            },
            onText: text.Add);

        Assert.Equal("Done: 42", result.GetText());
        Assert.Equal(new[] { "Checking...", "Done: 42" }, text);

        var call = Assert.Single(seen);
        Assert.Equal("lookup", call.Name);
        Assert.NotNull(call.ProviderData);
        Assert.Equal("abc", call.ProviderData!.Value.GetProperty("sig").GetString());

        // 第二轮请求应包含：用户消息、助手工具调用、工具结果。
        Assert.Equal(3, client.CompleteRequests[0].Messages.Count);
        Assert.Equal(ChatRole.Tool, client.CompleteRequests[0].Messages[2].Role);
    }

    [Fact]
    public async Task StreamWithToolsExactAsync_ReturnsDirectlyWhenNoToolCalls()
    {
        var client = new ToolAwareClient(
            streamRounds: new[]
            {
                new[]
                {
                    new ChatStreamUpdate { TextDelta = "Answer" },
                    new ChatStreamUpdate { FinishReason = ChatFinishReason.Stop }
                }
            },
            completions: Array.Empty<ChatResponse>());

        var text = new List<string>();
        var result = await client.StreamWithToolsExactAsync(
            ChatRequest.FromUser("m", "q"),
            (_, _) => Task.FromResult("unused"),
            onText: text.Add);

        Assert.Equal("Answer", result.GetText());
        Assert.Equal(new[] { "Answer" }, text);
        Assert.Empty(client.CompleteRequests);
    }

    private sealed class ToolAwareClient : IChatClient
    {
        private readonly Queue<ChatStreamUpdate[]> _streamRounds;
        private readonly Queue<ChatResponse> _completions;

        public ToolAwareClient(IEnumerable<ChatStreamUpdate[]> streamRounds, IEnumerable<ChatResponse> completions)
        {
            _streamRounds = new Queue<ChatStreamUpdate[]>(streamRounds);
            _completions = new Queue<ChatResponse>(completions);
        }

        public string ProviderName => "tool-aware";

        public List<ChatRequest> CompleteRequests { get; } = new();

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            CompleteRequests.Add(request);
            return Task.FromResult(_completions.Count > 0
                ? _completions.Dequeue()
                : new ChatResponse { Message = ChatMessage.Assistant(string.Empty) });
        }

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var round = _streamRounds.Count > 0 ? _streamRounds.Dequeue() : Array.Empty<ChatStreamUpdate>();
            foreach (var update in round)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }
    }
}
