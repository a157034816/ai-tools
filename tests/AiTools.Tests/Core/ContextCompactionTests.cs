using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Chat;
using AiTools.Core.Sessions;
using Xunit;

namespace AiTools.Tests.Core;

public class ContextCompactionTests
{
    private static ChatMessage Msg(string role, string text) => role switch
    {
        "user" => ChatMessage.User(text),
        "assistant" => ChatMessage.Assistant(text),
        "tool" => ChatMessage.ToolResult("call_1", text),
        _ => ChatMessage.System(text)
    };

    /// <summary>构造 N 个用户轮次，每轮 user + assistant，正文长度使 token 估算可控。</summary>
    private static List<ChatMessage> BuildTurns(int turns, int charsPerMessage)
    {
        var messages = new List<ChatMessage>();
        for (var turn = 0; turn < turns; turn++)
        {
            messages.Add(ChatMessage.User(new string('u', charsPerMessage) + turn));
            messages.Add(ChatMessage.Assistant(new string('a', charsPerMessage) + turn));
        }

        return messages;
    }

    // ------------------------------------------------------------------
    // ContextWindows
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("claude-sonnet-4-5", 200_000)]
    [InlineData("anthropic/claude-opus-4", 200_000)]
    [InlineData("gpt-4.1", 1_047_576)]
    [InlineData("gpt-4o-mini", 128_000)]
    [InlineData("gpt-5-turbo", 400_000)]
    [InlineData("models/gemini-2.5-pro", 1_048_576)]
    [InlineData("deepseek-chat", 64_000)]
    [InlineData("qwen-max", 32_768)]
    [InlineData("qwen-plus", 131_072)]
    [InlineData("totally-unknown-model", 128_000)]
    [InlineData("", 128_000)]
    [InlineData(null, 128_000)]
    public void Resolve_ReturnsKnownWindowOrFallback(string? modelId, int expected)
        => Assert.Equal(expected, ContextWindows.Resolve(modelId));

    [Fact]
    public void Resolve_OverrideTakesPrecedence()
    {
        Assert.Equal(8_192, ContextWindows.Resolve("claude-sonnet-4-5", 8_192));
        Assert.Equal(8_192, ContextWindows.Resolve("unknown-model", 8_192));
        Assert.Equal(200_000, ContextWindows.Resolve("claude-sonnet-4-5", 0));
        Assert.Equal(200_000, ContextWindows.Resolve("claude-sonnet-4-5", -1));
    }

    // ------------------------------------------------------------------
    // TokenEstimator
    // ------------------------------------------------------------------

    [Fact]
    public void EstimateText_CountsAsciiAndWideCharacters()
    {
        var ascii = TokenEstimator.EstimateText(new string('a', 400));
        Assert.InRange(ascii, 95, 105);

        // 中文按 ~1.4 字符/token，密度高于英文。
        var wide = TokenEstimator.EstimateText(new string('中', 140));
        Assert.InRange(wide, 95, 105);

        Assert.Equal(0, TokenEstimator.EstimateText(null));
        Assert.Equal(0, TokenEstimator.EstimateText(string.Empty));
    }

    [Fact]
    public void EstimateText_WideTextCostsMoreThanSameLengthAscii()
    {
        var ascii = TokenEstimator.EstimateText(new string('a', 1000));
        var wide = TokenEstimator.EstimateText(new string('中', 1000));
        Assert.True(wide > ascii * 2);
    }

    [Fact]
    public void EstimateMessage_IncludesToolCallsAndMultimodalOverhead()
    {
        var plain = TokenEstimator.EstimateMessage(Msg("user", "hello"));
        var withToolCall = TokenEstimator.EstimateMessage(
            ChatMessage.AssistantWithToolCalls(
                new[] { new ToolCall("call_1", "write_file", "{\"path\":\"a.txt\"}") },
                "hello"));
        Assert.True(withToolCall > plain);

        var withImage = TokenEstimator.EstimateMessage(
            ChatMessage.UserParts(ContentPart.FromImageBytes(new byte[] { 1, 2, 3 }, "image/png")));
        Assert.True(withImage >= 1_000);
    }

    [Fact]
    public void EstimateRange_OnlyCountsTheRequestedRange()
    {
        var messages = BuildTurns(3, 400);
        var total = TokenEstimator.EstimateMessages(messages);
        var firstTurn = TokenEstimator.EstimateRange(messages, 0, 2);
        var rest = TokenEstimator.EstimateRange(messages, 2, messages.Count);

        Assert.Equal(total, firstTurn + rest);
        Assert.Equal(0, TokenEstimator.EstimateRange(messages, 5, 3));
    }

    // ------------------------------------------------------------------
    // CompactionView
    // ------------------------------------------------------------------

    [Fact]
    public void Apply_ReplacesCoveredMessagesWithSummary()
    {
        var messages = BuildTurns(4, 100);
        var compaction = new ConversationCompaction
        {
            Summary = "早期对话摘要",
            CoveredMessageCount = 4
        };

        var view = CompactionView.Apply(messages, compaction);

        Assert.Equal(messages.Count - 4 + 1, view.Count);
        Assert.Equal(ChatRole.User, view[0].Role);
        Assert.Contains(CompactionView.SummaryPreamble, view[0].GetText());
        Assert.Contains("早期对话摘要", view[0].GetText());
        // 保留部分与原消息一致。
        for (var index = 4; index < messages.Count; index++)
        {
        }

        for (var viewIndex = 1; viewIndex < view.Count; viewIndex++)
        {
            Assert.Same(messages[4 + viewIndex - 1], view[viewIndex]);
        }
    }

    [Fact]
    public void Apply_ReturnsOriginalWhenCompactionInvalid()
    {
        var messages = BuildTurns(2, 100);

        Assert.Same(messages, CompactionView.Apply(messages, null));

        var stale = new ConversationCompaction { Summary = "过期", CoveredMessageCount = 99 };
        Assert.Same(messages, CompactionView.Apply(messages, stale));

        var coversEverything = new ConversationCompaction { Summary = "全部覆盖", CoveredMessageCount = messages.Count };
        Assert.Same(messages, CompactionView.Apply(messages, coversEverything));
    }

    [Fact]
    public void BuildSource_IncludesExistingSummaryAndSkipsEmptyText()
    {
        var messages = new List<ChatMessage>
        {
            Msg("user", "帮我修登录 bug"),
            Msg("assistant", "已修复，见 src/Auth.cs"),
            ChatMessage.Assistant(string.Empty)
        };

        var source = CompactionView.BuildSource(messages, 0, messages.Count, "此前摘要内容");

        Assert.Contains("此前生成的摘要", source);
        Assert.Contains("此前摘要内容", source);
        Assert.Contains("[用户] 帮我修登录 bug", source);
        Assert.Contains("[助手] 已修复，见 src/Auth.cs", source);
    }

    // ------------------------------------------------------------------
    // CompactionPlanner
    // ------------------------------------------------------------------

    [Fact]
    public void Plan_ReturnsNullWhenBelowThreshold()
    {
        var messages = BuildTurns(5, 400);
        var plan = CompactionPlanner.Plan(messages, 0, null, fixedTokens: 100, contextWindow: 1_000_000, options: new CompactionOptions());

        Assert.Null(plan);
    }

    [Fact]
    public void Plan_TriggerAtThresholdAndKeepsRecentTurns()
    {
        // 10 轮 × 每条 800 字符 ≈ 10 × 2 × 204 tokens ≈ 4080；窗口 4000 × 90% = 3600 触发。
        var messages = BuildTurns(10, 800);
        var options = new CompactionOptions { Threshold = 0.9, Target = 0.5, MinRecentTurns = 2 };

        var plan = CompactionPlanner.Plan(messages, 0, null, fixedTokens: 0, contextWindow: 4_000, options: options);

        Assert.NotNull(plan);
        // 切分点必然是某个用户轮次的起点（不会把 user/assistant 拆开）。
        Assert.Equal(ChatRole.User, messages[plan!.SplitIndex].Role);
        // 至少保留最近 2 轮：SplitIndex ≤ messages.Count - 4。
        Assert.True(plan.SplitIndex <= messages.Count - 4, $"split={plan.SplitIndex}");
        Assert.True(plan.SplitIndex > 0);
        // 折叠到目标以内（窗口 50% = 2000 tokens）。
        Assert.True(plan.ProjectedTokens <= 2_000, $"projected={plan.ProjectedTokens}");
        Assert.Equal(plan.SplitIndex, plan.FoldedMessageCount);
    }

    [Fact]
    public void Plan_NeverFoldsRecentTurnsEvenIfStillOverBudget()
    {
        // 巨大消息：即使全部折叠也压不到目标，但最近轮次仍不可折叠。
        var messages = BuildTurns(4, 4_000);
        var options = new CompactionOptions { Threshold = 0.9, Target = 0.5, MinRecentTurns = 2 };

        var plan = CompactionPlanner.Plan(messages, 0, null, fixedTokens: 0, contextWindow: 4_000, options: options);

        Assert.NotNull(plan);
        Assert.Equal(messages.Count - 4, plan!.SplitIndex);
    }

    [Fact]
    public void Plan_ForcedSkipsThresholdButStillRequiresFoldableTurns()
    {
        var messages = BuildTurns(3, 400);
        var options = new CompactionOptions { MinRecentTurns = 2 };

        // 手动压缩：低于目标（窗口 50%）时不压缩。
        var plan = CompactionPlanner.Plan(messages, 0, null, fixedTokens: 0, contextWindow: 1_000_000, options: options, force: true);
        Assert.Null(plan);

        // 只有 3 轮、保留 2 轮 → 可折叠 1 轮。
        var smallWindowPlan = CompactionPlanner.Plan(messages, 0, null, fixedTokens: 0, contextWindow: 800, options: options, force: true);
        Assert.NotNull(smallWindowPlan);
        Assert.Equal(2, smallWindowPlan!.FoldedMessageCount);
    }

    [Fact]
    public void Plan_ExistingCompactionCountsSummaryAndOnlyUncoveredMessages()
    {
        var messages = BuildTurns(10, 800);
        var options = new CompactionOptions { MinRecentTurns = 2 };
        var existing = new ConversationCompaction { Summary = "已有摘要", CoveredMessageCount = 4 };

        // 已覆盖 4 条：只有其后的 16 条计费（≈3264 tokens），窗口 100k 远不触发。
        var noPlan = CompactionPlanner.Plan(messages, 4, existing.Summary, fixedTokens: 0, contextWindow: 100_000, options: options);
        Assert.Null(noPlan);

        // 窗口 3000 → 阈值 2700，剩余部分触发；切分点必须越过已覆盖区间。
        var plan = CompactionPlanner.Plan(messages, 4, existing.Summary, fixedTokens: 0, contextWindow: 3_000, options: options);
        Assert.NotNull(plan);
        Assert.True(plan!.SplitIndex > 4);
        Assert.True(plan.FoldedMessageCount < messages.Count - 4);
    }

    [Fact]
    public void Plan_AccountsForFixedTokensFromToolsAndSystemPrompt()
    {
        var messages = BuildTurns(5, 400);
        var options = new CompactionOptions();

        // 无固定开销：不触发。
        Assert.Null(CompactionPlanner.Plan(messages, 0, null, fixedTokens: 0, contextWindow: 2_000, options: options));

        // 工具与系统提示把总估算推过阈值：触发。
        var plan = CompactionPlanner.Plan(messages, 0, null, fixedTokens: 1_500, contextWindow: 2_000, options: options);
        Assert.NotNull(plan);
    }

    // ------------------------------------------------------------------
    // ConversationCompaction 持久化
    // ------------------------------------------------------------------

    [Fact]
    public void Compaction_RoundTripsThroughConversationJson()
    {
        var record = new ConversationRecord();
        record.Compaction = new ConversationCompaction
        {
            Summary = "摘要正文",
            CoveredMessageCount = 12,
            CreatedAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            OriginalTokenCount = 123_456,
            CompactedTokenCount = 1_234
        };

        var json = JsonSerializer.Serialize(record, ConversationJson.Options);
        var restored = JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);

        Assert.NotNull(restored!.Compaction);
        Assert.Equal("摘要正文", restored.Compaction!.Summary);
        Assert.Equal(12, restored.Compaction.CoveredMessageCount);
        Assert.Equal(123_456, restored.Compaction.OriginalTokenCount);
        Assert.Equal(1_234, restored.Compaction.CompactedTokenCount);
    }

    [Fact]
    public void Fork_CopiesCompactionState()
    {
        var record = new ConversationRecord();
        record.Compaction = new ConversationCompaction { Summary = "父会话摘要", CoveredMessageCount = 4 };
        record.Messages.AddRange(BuildTurns(3, 50).Select(m => ConversationMessage.FromChatMessage(m)));

        var fork = record.CreateFork();

        Assert.NotNull(fork.Compaction);
        Assert.Equal("父会话摘要", fork.Compaction!.Summary);
        Assert.Equal(4, fork.Compaction.CoveredMessageCount);
        Assert.NotSame(record.Compaction, fork.Compaction);
    }

    // ------------------------------------------------------------------
    // ContextCompactionService
    // ------------------------------------------------------------------

    /// <summary>可脚本化的摘要器：记录调用并返回预设文本。</summary>
    private sealed class ScriptedSummarizer : IContextSummarizer
    {
        public List<string> Sources { get; } = new();
        public List<string?> ExistingSummaries { get; } = new();
        public string Reply { get; set; } = "脚本化摘要";
        public bool Throw { get; set; }

        public Task<string> SummarizeAsync(
            IChatClient client,
            string model,
            string source,
            string? existingSummary,
            int maxTokens,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw)
            {
                throw new InvalidOperationException("摘要服务故障");
            }

            Sources.Add(source);
            ExistingSummaries.Add(existingSummary);
            return Task.FromResult(Reply);
        }
    }

    private sealed class RecordingClient : IChatClient
    {
        public string ProviderName => "recording";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse
            {
                Message = ChatMessage.Assistant("客户端摘要"),
                FinishReason = ChatFinishReason.Stop
            });

        public IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static CompactionRequest BuildRequest(
        IReadOnlyList<ChatMessage> messages,
        int contextWindow,
        CompactionOptions? options = null,
        bool force = false,
        string reason = "threshold",
        Func<CompactionContext, Task<CompactionDecision?>>? onBefore = null,
        Func<CompactionResult, Task>? onAfter = null,
        Func<CompactionFailure, Task>? onFailed = null,
        ConversationCompaction? current = null)
        => new()
        {
            Client = new RecordingClient(),
            Model = "test-model",
            Messages = messages,
            Current = current,
            FixedTokens = 0,
            ContextWindow = contextWindow,
            Options = options ?? new CompactionOptions(),
            Force = force,
            Reason = reason,
            OnBefore = onBefore,
            OnAfter = onAfter,
            OnFailed = onFailed
        };

    [Fact]
    public async Task CompactAsync_DoesNothingBelowThreshold()
    {
        var summarizer = new ScriptedSummarizer();
        var service = new ContextCompactionService(summarizer);
        var messages = BuildTurns(3, 100);

        var result = await service.CompactAsync(BuildRequest(messages, 1_000_000));

        Assert.Null(result);
        Assert.Empty(summarizer.Sources);
    }

    [Fact]
    public async Task CompactAsync_SummarizesAndFoldsWhenTriggered()
    {
        var summarizer = new ScriptedSummarizer { Reply = "这是摘要" };
        var service = new ContextCompactionService(summarizer);
        var messages = BuildTurns(10, 800);

        var result = await service.CompactAsync(BuildRequest(messages, 4_000));

        Assert.NotNull(result);
        Assert.Equal("这是摘要", result!.Summary);
        // 切分点落在用户轮次边界，且不折叠最近 2 轮。
        Assert.True(result.CoveredMessageCount > 0 && result.CoveredMessageCount <= messages.Count - 4);
        Assert.Equal(ChatRole.User, messages[result.CoveredMessageCount].Role);
        Assert.True(result.CompactedTokens < result.OriginalTokens);
        // 摘要器收到的是被折叠区间的转录。
        Assert.Contains("[用户]", summarizer.Sources.Single());
        Assert.Equal("这是摘要", result.ToRecord().Summary);
        Assert.Equal(result.CoveredMessageCount, result.ToRecord().CoveredMessageCount);
    }

    [Fact]
    public async Task CompactAsync_ManualForcesCompaction()
    {
        var service = new ContextCompactionService(new ScriptedSummarizer());
        // 5 轮×800 字符在 4000 窗口下远低于目标，需要更小的窗口才能观察手动压缩。
        var messages = BuildTurns(5, 800);

        var result = await service.CompactAsync(BuildRequest(messages, 1_500, force: true, reason: "manual"));

        Assert.NotNull(result);
        Assert.Equal("manual", result!.Reason);
    }

    [Fact]
    public async Task CompactAsync_OnBeforeCanCancel()
    {
        var summarizer = new ScriptedSummarizer();
        var service = new ContextCompactionService(summarizer);
        var messages = BuildTurns(10, 800);

        var result = await service.CompactAsync(BuildRequest(
            messages,
            4_000,
            onBefore: context =>
            {
                Assert.Equal(4_000, context.ContextWindow);
                Assert.Equal("threshold", context.Reason);
                return Task.FromResult<CompactionDecision?>(CompactionDecision.Cancel("扩展不允许"));
            }));

        Assert.Null(result);
        Assert.Empty(summarizer.Sources);
    }

    [Fact]
    public async Task CompactAsync_OnBeforeCanProvideSummaryDirectly()
    {
        var summarizer = new ScriptedSummarizer();
        var service = new ContextCompactionService(summarizer);
        var messages = BuildTurns(10, 800);

        var result = await service.CompactAsync(BuildRequest(
            messages,
            4_000,
            onBefore: context =>
            {
                Assert.Equal(4_000, context.ContextWindow);
                Assert.Equal("threshold", context.Reason);
                Assert.True(context.MessageCount > 0);
                return Task.FromResult<CompactionDecision?>(CompactionDecision.WithSummary("扩展自带摘要"));
            }));

        Assert.NotNull(result);
        Assert.Equal("扩展自带摘要", result!.Summary);
        Assert.Empty(summarizer.Sources);
    }

    [Fact]
    public async Task CompactAsync_SummaryFailureInvokesOnFailedAndReturnsNull()
    {
        var failures = new List<CompactionFailure>();
        var service = new ContextCompactionService(new ScriptedSummarizer { Throw = true });
        var messages = BuildTurns(10, 800);

        var result = await service.CompactAsync(BuildRequest(messages, 4_000, onFailed: failure => { failures.Add(failure); return Task.CompletedTask; }));

        Assert.Null(result);
        var failure = Assert.Single(failures);
        Assert.Contains("摘要服务故障", failure.Error);
    }

    [Fact]
    public async Task CompactAsync_EmptySummaryIsReportedAsFailure()
    {
        var failures = new List<CompactionFailure>();
        var service = new ContextCompactionService(new ScriptedSummarizer { Reply = "   " });
        var messages = BuildTurns(10, 800);

        var result = await service.CompactAsync(BuildRequest(messages, 4_000, onFailed: failure => { failures.Add(failure); return Task.CompletedTask; }));

        Assert.Null(result);
        Assert.Single(failures);
    }

    [Fact]
    public async Task CompactAsync_OnAfterReceivesResult()
    {
        var received = new List<CompactionResult>();
        var service = new ContextCompactionService(new ScriptedSummarizer());
        var messages = BuildTurns(10, 800);

        var result = await service.CompactAsync(BuildRequest(messages, 4_000, onAfter: r => { received.Add(r); return Task.CompletedTask; }));

        Assert.NotNull(result);
        Assert.Same(result, Assert.Single(received));
    }

    [Fact]
    public async Task CompactAsync_RollsExistingSummaryForward()
    {
        var summarizer = new ScriptedSummarizer();
        var service = new ContextCompactionService(summarizer);
        var messages = BuildTurns(10, 800);
        var current = new ConversationCompaction { Summary = "第一轮摘要", CoveredMessageCount = 4 };

        var result = await service.CompactAsync(BuildRequest(messages, 3_000, current: current));

        Assert.NotNull(result);
        Assert.True(result!.CoveredMessageCount > 4);
        // 旧摘要作为 existingSummary 传给摘要器，实现滚动合并。
        Assert.Equal("第一轮摘要", Assert.Single(summarizer.ExistingSummaries));
        Assert.Contains("第一轮摘要", Assert.Single(summarizer.Sources));
    }

    [Fact]
    public async Task CompactAsync_DisabledOptionsShortCircuit()
    {
        var summarizer = new ScriptedSummarizer();
        var service = new ContextCompactionService(summarizer);
        var messages = BuildTurns(10, 800);

        var result = await service.CompactAsync(BuildRequest(messages, 4_000, options: new CompactionOptions { Enabled = false }));

        Assert.Null(result);
        Assert.Empty(summarizer.Sources);
    }

    [Fact]
    public async Task CompactAsync_PropagatesCancellation()
    {
        var service = new ContextCompactionService(new ScriptedSummarizer());
        var messages = BuildTurns(10, 800);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CompactAsync(BuildRequest(messages, 4_000), cts.Token));
    }

    [Fact]
    public void CompactionResult_DefaultsAreConsistent()
    {
        var result = new CompactionResult();

        Assert.Equal("threshold", result.Reason);
        Assert.Equal(0, result.CoveredMessageCount);
        var record = result.ToRecord();
        Assert.Equal(0, record.CoveredMessageCount);
        Assert.False(record.IsValidFor(0));
    }

    [Fact]
    public void CompactionDecision_Helpers()
    {
        Assert.False(CompactionDecision.Proceed().Cancelled);
        Assert.True(CompactionDecision.Cancel("原因").Cancelled);
        Assert.Equal("原因", CompactionDecision.Cancel("原因").CancelReason);
        Assert.Equal("文本", CompactionDecision.WithSummary("文本").Summary);
    }
}
