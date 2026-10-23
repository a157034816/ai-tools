using System.Runtime.CompilerServices;
using AiTools.Abstractions;
using Xunit;

namespace AiTools.Tests.Core;

public class ChatClientExtensionsTests
{
    [Fact]
    public async Task CompleteWithToolsAsync_ExecutesToolCallsUntilFinalAnswer()
    {
        var client = new ScriptedChatClient(
            new ChatResponse
            {
                Message = ChatMessage.AssistantWithToolCalls(new[]
                {
                    new ToolCall("call_1", "get_weather", "{\"city\":\"Paris\"}")
                }),
                FinishReason = ChatFinishReason.ToolCalls
            },
            new ChatResponse
            {
                Message = ChatMessage.Assistant("It is sunny in Paris."),
                FinishReason = ChatFinishReason.Stop
            });

        var seen = new List<ToolCall>();
        var response = await client.CompleteWithToolsAsync(
            new ChatRequest { Model = "m", Messages = new[] { ChatMessage.User("weather?") } },
            (call, _) =>
            {
                seen.Add(call);
                return Task.FromResult("{\"temp\":25}");
            });

        Assert.Equal("It is sunny in Paris.", response.GetText());
        var call = Assert.Single(seen);
        Assert.Equal("get_weather", call.Name);

        // 第二轮请求应包含：用户消息、助手工具调用、工具结果。
        var secondRequest = client.Requests[1];
        Assert.Equal(3, secondRequest.Messages.Count);
        Assert.Equal(ChatRole.Tool, secondRequest.Messages[2].Role);
        Assert.Equal("call_1", secondRequest.Messages[2].ToolCallId);
        Assert.Equal("{\"temp\":25}", secondRequest.Messages[2].GetText());    }

    [Fact]
    public async Task CompleteWithToolsAsync_DoesNotMutateOriginalRequest()
    {
        var client = new ScriptedChatClient(
            new ChatResponse { Message = ChatMessage.Assistant("done"), FinishReason = ChatFinishReason.Stop });
        var request = new ChatRequest { Model = "m", Messages = new[] { ChatMessage.User("hi") } };

        await client.CompleteWithToolsAsync(request, (_, _) => Task.FromResult("x"));

        Assert.Single(request.Messages);
        Assert.Single(client.Requests[0].Messages);    }

    [Fact]
    public async Task CompleteWithToolsAsync_ThrowsWhenRoundsExceeded()
    {
        var client = new ScriptedChatClient(Enumerable.Range(0, 10)
            .Select(_ => new ChatResponse
            {
                Message = ChatMessage.AssistantWithToolCalls(new[] { new ToolCall("id", "loop") }),
                FinishReason = ChatFinishReason.ToolCalls
            })
            .ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteWithToolsAsync(
                new ChatRequest { Model = "m", Messages = new[] { ChatMessage.User("hi") } },
                (_, _) => Task.FromResult("x"),
                maxToolRounds: 2));
    }

    [Fact]
    public async Task StreamWithToolsAsync_HidesToolCallDeltasAndResumesAfterTool()
    {
        var client = new StreamingScriptedChatClient(
            new ChatStreamUpdate[]
            {
                new() { TextDelta = "Let me check. " },
                new() { ToolCallDelta = new ToolCallDelta { Index = 0, Id = "c1", Name = "lookup" } },
                new() { ToolCallDelta = new ToolCallDelta { Index = 0, ArgumentsDelta = "{}" } },
                new() { FinishReason = ChatFinishReason.ToolCalls }
            },
            new ChatStreamUpdate[]
            {
                new() { TextDelta = "The answer is 42." },
                new() { FinishReason = ChatFinishReason.Stop }
            });

        var updates = new List<string>();
        await foreach (var update in client.StreamWithToolsAsync(
            new ChatRequest { Model = "m", Messages = new[] { ChatMessage.User("q") } },
            (_, _) => Task.FromResult("42")))
        {
            if (update.TextDelta != null)
            {
                updates.Add(update.TextDelta);
            }
        }

        Assert.Equal(new[] { "Let me check. ", "The answer is 42." }, updates);
    }

    [Fact]
    public async Task AggregateAsync_ProducesCompleteResponse()
    {
        var stream = ToAsync(new[]
        {
            new ChatStreamUpdate { TextDelta = "Hel", ResponseId = "r1", Model = "m" },
            new ChatStreamUpdate { TextDelta = "lo" },
            new ChatStreamUpdate
            {
                ToolCallDelta = new ToolCallDelta { Index = 0, Id = "c1", Name = "f", ArgumentsDelta = "{\"a\":1}" }
            },
            new ChatStreamUpdate { FinishReason = ChatFinishReason.ToolCalls, Usage = new Usage { TotalTokens = 7 } }
        });

        var response = await stream.AggregateAsync();

        Assert.Equal("Hello", response.GetText());
        Assert.Equal("r1", response.Id);
        Assert.Equal("m", response.Model);
        Assert.Equal(7, response.Usage!.TotalTokens);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("{\"a\":1}", call.ArgumentsJson);
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> items, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses;

        public ScriptedChatClient(params ChatResponse[] responses)
        {
            _responses = new Queue<ChatResponse>(responses);
        }

        public string ProviderName => "scripted";

        public List<ChatRequest> Requests { get; } = new();

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : new ChatResponse { Message = ChatMessage.Assistant(string.Empty) });
        }

        public IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StreamingScriptedChatClient : IChatClient
    {
        private readonly Queue<ChatStreamUpdate[]> _rounds;

        public StreamingScriptedChatClient(params ChatStreamUpdate[][] rounds)
        {
            _rounds = new Queue<ChatStreamUpdate[]>(rounds);
        }

        public string ProviderName => "scripted-stream";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var round = _rounds.Dequeue();
            foreach (var update in round)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }
    }
}
