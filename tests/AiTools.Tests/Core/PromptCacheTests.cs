using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Chat;
using Xunit;

namespace AiTools.Tests.Core;

/// <summary>提示缓存策略、经济性估算与预热器的行为。</summary>
public class PromptCacheTests
{
    private static Usage Usage(int input, int? cacheRead = null, int? cacheWrite = null)
        => new() { InputTokens = input, CacheReadInputTokens = cacheRead, CacheCreationInputTokens = cacheWrite, OutputTokens = 1 };

    private static ChatRequest Request() => ChatRequest.FromUser("claude-sonnet-4-5", "hello");

    private static PromptCachePolicy WarmablePolicy(TimeSpan? lifetime = null) => new()
    {
        Kind = PromptCacheKind.Explicit,
        Lifetime = lifetime ?? TimeSpan.FromMinutes(5),
        Pricing = new PromptCachePricing
        {
            InputPerMillion = 3,
            CachedInputPerMillion = 0.3,
            CacheWritePerMillion = 3.75,
            OutputPerMillion = 15
        }
    };

    // ------------------------------------------------------------------
    // PromptCachePolicyCatalog
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_Anthropic_IsExplicitWithFiveMinuteLifetime()
    {
        var policy = PromptCachePolicyCatalog.Resolve("anthropic", "claude-sonnet-4-5");

        Assert.Equal(PromptCacheKind.Explicit, policy.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.Lifetime);
        Assert.True(policy.IsWarmable);
    }

    [Theory]
    [InlineData("openai", "gpt-4.1", PromptCacheKind.Implicit)]
    [InlineData("openai", "gpt-4o-mini", PromptCacheKind.Implicit)]
    [InlineData("openai", "deepseek-chat", PromptCacheKind.Implicit)]
    [InlineData("openai", "kimi-k2", PromptCacheKind.Implicit)]
    [InlineData("gemini", "gemini-2.5-pro", PromptCacheKind.Implicit)]
    public void Resolve_OpenAiAndGemini_UseImplicitPrefixCache(string protocol, string model, PromptCacheKind expected)
    {
        var policy = PromptCachePolicyCatalog.Resolve(protocol, model);

        Assert.Equal(expected, policy.Kind);
        Assert.True(policy.IsWarmable);
    }

    [Fact]
    public void Resolve_Anthropic_HandlesPrefixedModelIds()
    {
        var policy = PromptCachePolicyCatalog.Resolve("anthropic", "anthropic/claude-opus-4-1");

        Assert.Equal(PromptCacheKind.Explicit, policy.Kind);
    }

    [Fact]
    public void Resolve_Gemini_HandlesModelsPrefix()
    {
        var policy = PromptCachePolicyCatalog.Resolve("gemini", "models/gemini-2.5-flash");

        Assert.Equal(PromptCacheKind.Implicit, policy.Kind);
        Assert.Equal(PromptCachePolicyCatalog.GeminiImplicitLifetime, policy.Lifetime);
    }

    [Fact]
    public void Resolve_AnthropicLongRetention_UsesOneHourLifetime()
    {
        var shortLived = PromptCachePolicyCatalog.Resolve("anthropic", "claude-sonnet-4-5");
        var longLived = PromptCachePolicyCatalog.Resolve("anthropic", "claude-sonnet-4-5", longRetention: true);

        Assert.Equal(PromptCachePolicyCatalog.AnthropicShortLifetime, shortLived.Lifetime);
        Assert.Equal(PromptCachePolicyCatalog.AnthropicLongLifetime, longLived.Lifetime);
    }

    [Fact]
    public void Resolve_Anthropic_ExposesZeroWarmOutputTokensAndMinimum()
    {
        var policy = PromptCachePolicyCatalog.Resolve("anthropic", "claude-sonnet-4-5");

        // 官方推荐的预热写法：max_tokens 为 0，不产生输出 token 计费。
        Assert.Equal(0, policy.WarmOutputTokens);
        Assert.Equal(PromptCachePolicyCatalog.AnthropicConservativeMinimumTokens, policy.MinimumCacheableTokens);
    }

    [Fact]
    public void Resolve_OpenAiAndGemini_RequireAtLeastOneOutputToken()
    {
        var openAi = PromptCachePolicyCatalog.Resolve("openai", "gpt-4.1");
        var gemini = PromptCachePolicyCatalog.Resolve("gemini", "gemini-2.5-pro");

        Assert.Equal(1, openAi.WarmOutputTokens);
        Assert.Equal(1, gemini.WarmOutputTokens);
        Assert.Equal(PromptCachePolicyCatalog.OpenAiMinimumTokens, openAi.MinimumCacheableTokens);
        Assert.Equal(PromptCachePolicyCatalog.GeminiMinimumTokens, gemini.MinimumCacheableTokens);
    }

    [Theory]
    [InlineData("openai", "o1-mini")]
    [InlineData("openai", "some-unknown-model")]
    [InlineData("openai", "text-embedding-3-large")]
    [InlineData("openai", "dall-e-3")]
    [InlineData("openai", "whisper-1")]
    [InlineData("anthropic", "text-embedding-3-large")]
    [InlineData("gemini", "text-embedding-004")]
    [InlineData("", "gpt-4.1")]
    [InlineData("openai", "")]
    [InlineData(null, null)]
    public void Resolve_UnsupportedCombinations_ReturnNone(string? protocol, string? model)
    {
        var policy = PromptCachePolicyCatalog.Resolve(protocol, model);

        Assert.Equal(PromptCacheKind.None, policy.Kind);
        Assert.False(policy.IsWarmable);
    }

    // ------------------------------------------------------------------
    // PromptCacheAccounting
    // ------------------------------------------------------------------

    [Fact]
    public void GetPromptTokens_Anthropic_SumsInputCacheReadAndWrite()
    {
        var total = PromptCacheAccounting.GetPromptTokens(
            "anthropic",
            Usage(input: 100, cacheRead: 900, cacheWrite: 200));

        Assert.Equal(1_200, total);
    }

    [Fact]
    public void GetPromptTokens_OpenAi_DoesNotDoubleCountCacheRead()
    {
        // OpenAI 的 prompt_tokens 已包含 cached_tokens。
        var total = PromptCacheAccounting.GetPromptTokens("openai", Usage(input: 1_000, cacheRead: 800));

        Assert.Equal(1_000, total);
    }

    [Fact]
    public void GetPromptTokens_FallsBackToTotalTokens()
    {
        var usage = new Usage { TotalTokens = 42 };

        Assert.Equal(42, PromptCacheAccounting.GetPromptTokens("gemini", usage));
        Assert.Equal(0, PromptCacheAccounting.GetPromptTokens("gemini", null));
    }

    [Fact]
    public void GetCacheHitRate_ReturnsNullWhenProviderReportsNoCachedTokens()
    {
        Assert.Null(PromptCacheAccounting.GetCacheHitRate("openai", Usage(1_000)));
        Assert.Null(PromptCacheAccounting.GetCacheHitRate("openai", null));
    }

    [Fact]
    public void GetCacheHitRate_ComputesShareOfTotalPrompt()
    {
        var rate = PromptCacheAccounting.GetCacheHitRate(
            "anthropic",
            Usage(input: 100, cacheRead: 900, cacheWrite: 0));

        Assert.NotNull(rate);
        Assert.Equal(0.9, rate!.Value, 3);
    }

    // ------------------------------------------------------------------
    // PromptCacheWarmer：策略与经济性
    // ------------------------------------------------------------------

    [Fact]
    public void Track_NonWarmablePolicy_LeavesWarmerInactive()
    {
        using var warmer = new PromptCacheWarmer(delay: NeverDelay);
        var client = new RecordingChatClient();

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = client,
            Request = Request(),
            Policy = PromptCachePolicy.None,
            PromptTokens = 100_000
        });

        Assert.Equal("inactive", warmer.Status.State);
    }

    [Fact]
    public async Task Track_PromptTooSmallToPayOff_StopsWithoutRefreshing()
    {
        var decisions = new List<CacheWarmingDecision>();
        using var warmer = new PromptCacheWarmer(
            new PromptCacheWarmingOptions { Mode = PromptCacheWarmingMode.Idle },
            decision =>
            {
                decisions.Add(decision);
                return Task.FromResult(decision.Action);
            },
            ImmediateDelay);
        var client = new RecordingChatClient();
        warmer.OnRunStarted();

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = client,
            Request = Request(),
            Policy = WarmablePolicy(),
            // 只有 1k token：省下的钱远低于 $0.05 阈值。
            PromptTokens = 1_000
        }, () => true);

        await WaitForAsync(() => decisions.Count > 0).ConfigureAwait(true);

        Assert.Single(decisions);
        Assert.Equal(CacheWarmingAction.Stop, decisions[0].Action);
        Assert.True(decisions[0].EconomicsAvailable);
        Assert.True(decisions[0].ExpectedSavings < warmer.Options.MinimumExpectedSavings);
        Assert.Empty(client.Completions);
    }

    [Fact]
    public async Task Track_LargePrompt_SendsOneTokenReplay()
    {
        var warmed = new List<CacheWarmingResult>();
        var options = new PromptCacheWarmingOptions { Mode = PromptCacheWarmingMode.Streaming, MaxRefreshes = 1 };
        using var warmer = new PromptCacheWarmer(options, delay: ImmediateDelay);
        warmer.Warmed += warmed.Add;
        var client = new RecordingChatClient();
        warmer.OnRunStarted();

        var request = new ChatRequest
        {
            Model = "claude-sonnet-4-5",
            SystemPrompt = "You are helpful.",
            Messages = new[] { ChatMessage.User("hi") },
            MaxOutputTokens = 4096
        };

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = client,
            Request = request,
            Policy = WarmablePolicy(),
            PromptTokens = 200_000
        }, () => true);

        await WaitForAsync(() => warmed.Count > 0).ConfigureAwait(true);

        var replay = Assert.Single(client.Completions);
        // 重放必须把输出压到最小，其余字段与真实请求一致。
        Assert.Equal(1, replay.MaxOutputTokens);
        Assert.Equal(request.Model, replay.Model);
        Assert.Equal(request.SystemPrompt, replay.SystemPrompt);
        Assert.Equal("hi", replay.Messages[0].GetText());
        Assert.Equal(1, warmed[0].RefreshCount);
    }

    [Fact]
    public async Task Track_AnthropicPolicy_ReplaysWithZeroOutputTokensAndNoToolChoice()
    {
        var warmed = new List<CacheWarmingResult>();
        using var warmer = new PromptCacheWarmer(
            new PromptCacheWarmingOptions { MaxRefreshes = 1 },
            delay: ImmediateDelay);
        warmer.Warmed += warmed.Add;
        var client = new RecordingChatClient();
        warmer.OnRunStarted();

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = client,
            Request = new ChatRequest
            {
                Model = "claude-opus-5",
                SystemPrompt = "sys",
                Messages = new[] { ChatMessage.User("hi") },
                MaxOutputTokens = 64000,
                ToolChoice = ToolChoice.Function("lookup"),
                ResponseFormat = ResponseFormat.JsonSchema("s", "{\"type\":\"object\"}")
            },
            // Anthropic 策略：WarmOutputTokens = 0。
            Policy = PromptCachePolicyCatalog.Resolve("anthropic", "claude-opus-5"),
            PromptTokens = 200_000
        }, () => true);

        await WaitForAsync(() => warmed.Count > 0).ConfigureAwait(true);

        var replay = Assert.Single(client.Completions);
        // max_tokens: 0 与强制工具选择 / JSON Schema 组合会被 Anthropic 拒绝，因此必须清空。
        Assert.Equal(0, replay.MaxOutputTokens);
        Assert.Null(replay.ToolChoice);
        Assert.Null(replay.ResponseFormat);
    }

    [Fact]
    public async Task Track_BelowMinimumCacheableLength_StopsWithoutRefreshing()
    {
        var decisions = new List<CacheWarmingDecision>();
        var options = new PromptCacheWarmingOptions { Mode = PromptCacheWarmingMode.Idle };
        using var warmer = new PromptCacheWarmer(
            options,
            decision =>
            {
                decisions.Add(decision);
                return Task.FromResult(decision.Action);
            },
            ImmediateDelay);
        var client = new RecordingChatClient();
        warmer.OnRunStarted();

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = client,
            Request = Request(),
            Policy = new PromptCachePolicy
            {
                Kind = PromptCacheKind.Explicit,
                Lifetime = TimeSpan.FromMinutes(5),
                Pricing = WarmablePolicy().Pricing,
                // Haiku 4.5 级别的下限：提示远大于经济阈值但低于最小可缓存长度。
                MinimumCacheableTokens = 4_096
            },
            PromptTokens = 2_000
        }, () => true);

        await WaitForAsync(() => decisions.Count > 0).ConfigureAwait(true);

        Assert.Single(decisions);
        Assert.True(decisions[0].BelowCacheableMinimum);
        Assert.False(decisions[0].EconomicsAvailable);
        Assert.Equal(CacheWarmingAction.Stop, decisions[0].Action);
        Assert.Equal(4_096, decisions[0].MinimumCacheableTokens);
        Assert.Empty(client.Completions);
    }

    [Fact]
    public async Task OnRunSettled_InStreamingMode_StopsWarming()
    {
        var decisions = 0;
        var options = new PromptCacheWarmingOptions
        {
            Mode = PromptCacheWarmingMode.Streaming,
            RefreshFraction = 0.9
        };

        var delay = new BlockingDelay();
        using var warmer = new PromptCacheWarmer(
            options,
            decision =>
            {
                Interlocked.Increment(ref decisions);
                return Task.FromResult(decision.Action);
            },
            delay.WaitAsync);
        warmer.OnRunStarted();
        var client = new RecordingChatClient();

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = client,
            Request = Request(),
            Policy = WarmablePolicy(TimeSpan.FromMinutes(5)),
            PromptTokens = 200_000
        }, () => true);

        // 第一次等待开始后模拟本轮结束：Streaming 模式应直接停止，而不是进入重放。
        Assert.True(SpinWait.SpinUntil(() => delay.WaitCount > 0, TimeSpan.FromSeconds(2)));
        warmer.OnRunSettled();

        Assert.True(SpinWait.SpinUntil(
            () => warmer.Status.State == "inactive",
            TimeSpan.FromSeconds(2)));

        Assert.Equal("本轮已结束", warmer.Status.Reason);
        Assert.Equal(0, Volatile.Read(ref decisions));
        Assert.Empty(client.Completions);
    }

    [Fact]
    public async Task DecisionCallback_CanOverrideBuiltInConclusion()
    {
        var options = new PromptCacheWarmingOptions { Mode = PromptCacheWarmingMode.Streaming, MaxRefreshes = 1 };
        var overridden = false;
        using var warmer = new PromptCacheWarmer(
            options,
            decision =>
            {
                // 内置结论是"不刷新"（提示太小），扩展强行要求刷新。
                overridden = true;
                return Task.FromResult(CacheWarmingAction.Warm);
            },
            ImmediateDelay);
        var client = new RecordingChatClient();
        warmer.OnRunStarted();

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = client,
            Request = Request(),
            Policy = WarmablePolicy(),
            PromptTokens = 1_000
        }, () => true);

        await WaitForAsync(() => client.Completions.Count > 0).ConfigureAwait(true);

        Assert.True(overridden);
        Assert.Single(client.Completions);
        Assert.True(warmer.Status.ExtensionOverride);
    }

    [Fact]
    public async Task RefreshFailure_StopsWarmingWithoutThrowing()
    {
        using var warmer = new PromptCacheWarmer(
            new PromptCacheWarmingOptions { Mode = PromptCacheWarmingMode.Streaming },
            delay: ImmediateDelay);
        var client = new RecordingChatClient { ThrowOnComplete = true };
        warmer.OnRunStarted();

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = client,
            Request = Request(),
            Policy = WarmablePolicy(),
            PromptTokens = 200_000
        }, () => true);

        await WaitForAsync(() => warmer.Status.State == "inactive").ConfigureAwait(true);

        Assert.Equal("刷新请求失败", warmer.Status.Reason);
    }

    [Fact]
    public void Cancel_ClearsTrackedRequest()
    {
        using var warmer = new PromptCacheWarmer(delay: NeverDelay);
        warmer.OnRunStarted();
        warmer.Track(new PromptCacheWarmRequest
        {
            Client = new RecordingChatClient(),
            Request = Request(),
            Policy = WarmablePolicy(),
            PromptTokens = 200_000
        }, () => true);

        Assert.NotEqual("inactive", warmer.Status.State);

        warmer.Cancel();

        Assert.Equal("inactive", warmer.Status.State);
    }

    [Fact]
    public void IsCurrentFalse_StopsWarming()
    {
        using var warmer = new PromptCacheWarmer(delay: NeverDelay);
        warmer.OnRunStarted();
        warmer.Track(new PromptCacheWarmRequest
        {
            Client = new RecordingChatClient(),
            Request = Request(),
            Policy = WarmablePolicy(),
            PromptTokens = 200_000
        }, () => false);

        // 循环尚未开始时就已失效：状态会很快落到 inactive。
        Assert.True(SpinWait.SpinUntil(
            () => warmer.Status.State == "inactive",
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Track_ReplacesPreviousRequest()
    {
        using var warmer = new PromptCacheWarmer(delay: NeverDelay);
        warmer.OnRunStarted();

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = new RecordingChatClient(),
            Request = ChatRequest.FromUser("m1", "first"),
            Policy = WarmablePolicy(),
            PromptTokens = 200_000
        }, () => true);
        var first = warmer.Status;

        warmer.Track(new PromptCacheWarmRequest
        {
            Client = new RecordingChatClient(),
            Request = ChatRequest.FromUser("m2", "second"),
            Policy = WarmablePolicy(),
            PromptTokens = 200_000
        }, () => true);

        // 新请求已接管预热状态，旧循环被取消。
        Assert.NotNull(warmer.Status);
        Assert.NotEqual("inactive", warmer.Status.State);
        _ = first;
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private static Task ImmediateDelay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;

    private static Task NeverDelay(TimeSpan duration, CancellationToken cancellationToken)
        => Task.Delay(Timeout.Infinite, cancellationToken);

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(true);
        }

        Assert.Fail("条件在超时前未满足。");
    }

    /// <summary>可观测的等待实现：记录被调用次数并一直阻塞到取消。</summary>
    private sealed class BlockingDelay
    {
        private int _waitCount;

        public int WaitCount => Volatile.Read(ref _waitCount);

        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waitCount);
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>记录请求并返回固定响应的聊天客户端。</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public string ProviderName => "anthropic";

        public bool ThrowOnComplete { get; set; }

        public List<ChatRequest> Completions { get; } = new();

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnComplete)
            {
                throw new InvalidOperationException("boom");
            }

            Completions.Add(request);
            return Task.FromResult(new ChatResponse
            {
                Message = ChatMessage.Assistant("ok"),
                Usage = new Usage
                {
                    InputTokens = 200_000,
                    CacheReadInputTokens = 199_000,
                    OutputTokens = 1
                }
            });
        }

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatStreamUpdate { TextDelta = "ok" };
            await Task.CompletedTask.ConfigureAwait(true);
        }
    }
}
