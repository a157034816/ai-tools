using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace AiTools.Abstractions;

/// <summary>聊天客户端的便捷扩展：流式聚合与自动工具调用循环。</summary>
public static class ChatClientExtensions
{
    /// <summary>
    /// 发起聊天请求并在模型请求工具调用时自动执行 <paramref name="toolHandler"/>，
    /// 将结果回传模型，直到模型给出最终答复。
    /// </summary>
    /// <param name="client">聊天客户端。</param>
    /// <param name="request">初始请求；其 Messages 不会被修改。</param>
    /// <param name="toolHandler">工具执行器：接收工具调用，返回结果文本（建议为 JSON 字符串）。</param>
    /// <param name="maxToolRounds">最大工具调用轮数，防止死循环。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<ChatResponse> CompleteWithToolsAsync(
        this IChatClient client,
        ChatRequest request,
        Func<ToolCall, CancellationToken, Task<string>> toolHandler,
        int maxToolRounds = 8,
        CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (toolHandler == null)
        {
            throw new ArgumentNullException(nameof(toolHandler));
        }

        var messages = new List<ChatMessage>(request.Messages);
        for (var round = 0; round <= maxToolRounds; round++)
        {
            var response = await client.CompleteAsync(request.WithMessages(messages), cancellationToken).ConfigureAwait(false);

            if (response.FinishReason != ChatFinishReason.ToolCalls || response.ToolCalls.Count == 0)
            {
                return response;
            }

            messages.Add(response.Message);
            if (round == maxToolRounds)
            {
                throw new InvalidOperationException($"The model exceeded the maximum of {maxToolRounds} tool-calling rounds.");
            }

            foreach (var call in response.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await toolHandler(call, cancellationToken).ConfigureAwait(false);
                messages.Add(ChatMessage.ToolResult(call.Id, result ?? string.Empty));
            }
        }

        throw new InvalidOperationException($"The model exceeded the maximum of {maxToolRounds} tool-calling rounds.");
    }

    /// <summary>
    /// 流式聊天并在模型请求工具调用时自动执行 <paramref name="toolHandler"/>。
    /// 工具调用过程对调用方透明：只收到面向用户的文本增量事件；工具调用本身不会以增量事件暴露。
    /// </summary>
    public static async IAsyncEnumerable<ChatStreamUpdate> StreamWithToolsAsync(
        this IChatClient client,
        ChatRequest request,
        Func<ToolCall, CancellationToken, Task<string>> toolHandler,
        int maxToolRounds = 8,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (toolHandler == null)
        {
            throw new ArgumentNullException(nameof(toolHandler));
        }

        var messages = new List<ChatMessage>(request.Messages);
        for (var round = 0; round <= maxToolRounds; round++)
        {
            var accumulator = new ToolCallAccumulator();
            ChatFinishReason? finishReason = null;

            await foreach (var update in client.StreamAsync(request.WithMessages(messages), cancellationToken))
            {
                if (update.ToolCallDelta != null)
                {
                    accumulator.Add(update.ToolCallDelta);
                    continue;
                }

                if (update.FinishReason.HasValue)
                {
                    finishReason = update.FinishReason;
                }

                if (update.FinishReason == ChatFinishReason.ToolCalls)
                {
                    continue;
                }

                if (update.TextDelta != null || update.ReasoningDelta != null)
                {
                    yield return update;
                }
            }

            var calls = accumulator.Build();
            if (finishReason != ChatFinishReason.ToolCalls || calls.Count == 0)
            {
                yield break;
            }

            if (round == maxToolRounds)
            {
                throw new InvalidOperationException($"The model exceeded the maximum of {maxToolRounds} tool-calling rounds.");
            }

            messages.Add(ChatMessage.AssistantWithToolCalls(calls));
            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await toolHandler(call, cancellationToken).ConfigureAwait(false);
                messages.Add(ChatMessage.ToolResult(call.Id, result ?? string.Empty));
            }
        }
    }

    /// <summary>将流式更新聚合为一个完整响应。工具调用片段会按序号自动拼接。</summary>
    public static async Task<ChatResponse> AggregateAsync(
        this IAsyncEnumerable<ChatStreamUpdate> stream,
        CancellationToken cancellationToken = default)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var text = new StringBuilder();
        var accumulator = new ToolCallAccumulator();
        var finishReason = ChatFinishReason.None;
        Usage? usage = null;
        string? id = null;
        string? model = null;

        await foreach (var update in stream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (update.TextDelta != null)
            {
                text.Append(update.TextDelta);
            }

            if (update.ToolCallDelta != null)
            {
                accumulator.Add(update.ToolCallDelta);
            }

            if (update.FinishReason.HasValue)
            {
                finishReason = update.FinishReason.Value;
            }

            if (update.Usage != null)
            {
                usage = update.Usage;
            }

            if (update.ResponseId != null)
            {
                id = update.ResponseId;
            }

            if (update.Model != null)
            {
                model = update.Model;
            }
        }

        var parts = new List<ContentPart>();
        if (text.Length > 0)
        {
            parts.Add(ContentPart.FromText(text.ToString()));
        }

        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = parts,
            ToolCalls = accumulator.Build()
        };

        return new ChatResponse
        {
            Id = id,
            Model = model ?? string.Empty,
            Message = message,
            FinishReason = finishReason,
            Usage = usage
        };
    }

    /// <summary>按序号拼接流式工具调用片段。</summary>
    private sealed class ToolCallAccumulator
    {
        private readonly SortedDictionary<int, Entry> _entries = new();

        public void Add(ToolCallDelta delta)
        {
            if (!_entries.TryGetValue(delta.Index, out var entry))
            {
                entry = new Entry();
                _entries[delta.Index] = entry;
            }

            if (!string.IsNullOrEmpty(delta.Id))
            {
                entry.Id = delta.Id;
            }

            if (!string.IsNullOrEmpty(delta.Name))
            {
                entry.Name = delta.Name;
            }

            if (!string.IsNullOrEmpty(delta.ArgumentsDelta))
            {
                entry.Arguments.Append(delta.ArgumentsDelta);
            }
        }

        public IReadOnlyList<ToolCall> Build()
        {
            if (_entries.Count == 0)
            {
                return Array.Empty<ToolCall>();
            }

            var calls = new List<ToolCall>(_entries.Count);
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                var arguments = entry.Arguments.Length == 0 ? "{}" : entry.Arguments.ToString();
                calls.Add(new ToolCall(
                    entry.Id ?? $"call_{pair.Key}",
                    entry.Name ?? string.Empty,
                    arguments));
            }

            return calls;
        }

        private sealed class Entry
        {
            public string? Id;

            public string? Name;

            public StringBuilder Arguments { get; } = new StringBuilder();
        }
    }
}
