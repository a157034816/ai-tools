using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Core.Chat;

/// <summary>
/// 流式工具循环的中间结果：除对外的文本增量外，还保留真实的工具调用（含服务商私有数据）。
/// </summary>
public sealed class ChatTurnResult
{
    /// <summary>本轮的助手消息（文本 + 工具调用）。</summary>
    public ChatMessage Message { get; init; } = ChatMessage.Assistant(string.Empty);

    /// <summary>本轮结束时上报的用量。</summary>
    public Usage? Usage { get; init; }

    /// <summary>本轮结束原因。</summary>
    public ChatFinishReason FinishReason { get; init; }

    /// <summary>本轮产生的真实工具调用（含 ProviderData），可直接回传给下一轮。</summary>
    public IReadOnlyList<ToolCall> ToolCalls => Message.ToolCalls;

    /// <summary>本轮的文本内容。</summary>
    public string Text => Message.GetText();
}

/// <summary>需要 ProviderData 保真时使用的工具轮次辅助方法。</summary>
public static class ChatTurnExtensions
{
    /// <summary>
    /// 流式请求并对每个文本/推理增量调用 <paramref name="onUpdate"/>，同时返回包含真实工具调用的轮次结果。
    /// 与 <c>AggregateAsync</c> 不同，调用方可以拿到聚合后的助手消息（含 Provider 填充的工具调用）。
    /// </summary>
    public static async Task<ChatTurnResult> StreamTurnAsync(
        this IChatClient client,
        ChatRequest request,
        Action<ChatStreamUpdate>? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        var accumulator = new ChatStreamAccumulator();
        await foreach (var update in client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (update.TextDelta != null || update.ReasoningDelta != null)
            {
                onUpdate?.Invoke(update);
            }

            accumulator.Add(update);
        }

        var aggregated = accumulator.Build();
        return new ChatTurnResult
        {
            Message = aggregated.Message,
            Usage = aggregated.Usage,
            FinishReason = aggregated.FinishReason
        };
    }

    /// <summary>
    /// 在流式模式下执行自动工具调用循环，并保证工具调用携带的 ProviderData（如 Gemini thoughtSignature）不丢失。
    /// </summary>
    /// <remarks>
    /// 流式增量不包含 ProviderData，因此当流式回合结束时检测到工具调用，会以同一消息历史重新发起一次非流式请求，
    /// 用其返回的完整工具调用执行工具；最终答复仍以流式方式呈现。
    /// </remarks>
    public static async Task<ChatResponse> StreamWithToolsExactAsync(
        this IChatClient client,
        ChatRequest request,
        Func<ToolCall, CancellationToken, Task<string>> toolHandler,
        Action<string>? onText = null,
        Action<string>? onReasoning = null,
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
            var turn = await client.StreamTurnAsync(
                request.WithMessages(messages),
                update =>
                {
                    if (update.TextDelta != null)
                    {
                        onText?.Invoke(update.TextDelta);
                    }

                    if (update.ReasoningDelta != null)
                    {
                        onReasoning?.Invoke(update.ReasoningDelta);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (turn.FinishReason != ChatFinishReason.ToolCalls || turn.ToolCalls.Count == 0)
            {
                return new ChatResponse
                {
                    Message = turn.Message,
                    FinishReason = turn.FinishReason,
                    Usage = turn.Usage
                };
            }

            if (round == maxToolRounds)
            {
                throw new InvalidOperationException($"The model exceeded the maximum of {maxToolRounds} tool-calling rounds.");
            }

            // 流式增量不含 ProviderData（例如 Gemini thoughtSignature）。重新以非流式方式请求同一轮，
            // 拿到带私有数据的完整工具调用后再执行工具，避免多轮工具调用在部分服务商上失败。
            var toolTurn = await client.CompleteAsync(request.WithMessages(messages), cancellationToken).ConfigureAwait(false);
            var calls = toolTurn.ToolCalls.Count > 0 ? toolTurn.ToolCalls : turn.ToolCalls;

            messages.Add(toolTurn.ToolCalls.Count > 0 ? toolTurn.Message : turn.Message);
            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await toolHandler(call, cancellationToken).ConfigureAwait(false);
                messages.Add(ChatMessage.ToolResult(call.Id, result ?? string.Empty));
            }
        }

        throw new InvalidOperationException($"The model exceeded the maximum of {maxToolRounds} tool-calling rounds.");
    }
}
