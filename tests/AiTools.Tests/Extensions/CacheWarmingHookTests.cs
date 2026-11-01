using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Chat;
using AiTools.Extensions;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>
/// 覆盖 <c>cache_warming_decision</c> 事件：扩展可以改写预热器的结论，
/// 以及宿主侧把决策接进管道的方式。
/// </summary>
public sealed class CacheWarmingHookTests
{
    private static ExtensionPipeline Pipeline(IExtension extension)
        => new(new ExtensionPipelineOptions
        {
            Extensions = new[] { extension },
            WorkingDirectory = Environment.CurrentDirectory
        });

    [Fact]
    public async Task PublishCacheWarmingDecision_ExposesEconomicsAndAllowsOverride()
    {
        CacheWarmingDecisionEvent? seen = null;
        var pipeline = Pipeline(new ProbeExtension("warm", api =>
            api.On<CacheWarmingDecisionEvent>(ExtensionEvent.CacheWarmingDecision, (e, _, _) =>
            {
                seen = e;
                e.Stop();
                return Task.FromResult<CacheWarmingDecisionEvent?>(e);
            })));

        var result = await pipeline.PublishCacheWarmingDecisionAsync(
            pipeline.Session.CreateContext(null, null),
            new CacheWarmingDecisionEvent
            {
                Phase = "streaming",
                Model = "claude-sonnet-4-5",
                PromptTokens = 200_000,
                WarmCost = 0.06,
                MissCost = 0.54,
                ContinuationProbability = 1,
                ExpectedSavings = 0.48,
                EconomicsAvailable = true,
                Action = "warm"
            });

        Assert.NotNull(seen);
        Assert.Equal(200_000, seen!.PromptTokens);
        Assert.Equal("stop", result.Action);
        Assert.False(result.IsWarm);
    }

    [Fact]
    public async Task PublishCacheWarmingDecision_NoHandler_KeepsBuiltInConclusion()
    {
        var pipeline = Pipeline(new ProbeExtension("noop", _ => { }));

        var result = await pipeline.PublishCacheWarmingDecisionAsync(
            pipeline.Session.CreateContext(null, null),
            new CacheWarmingDecisionEvent { Action = "warm" });

        Assert.True(result.IsWarm);
    }

    [Fact]
    public async Task PublishCacheWarmingDecision_HandlerFailure_FallsBackToOriginal()
    {
        var pipeline = Pipeline(new ProbeExtension("boom", api =>
            api.On<CacheWarmingDecisionEvent>(ExtensionEvent.CacheWarmingDecision, (_, _, _) =>
                throw new InvalidOperationException("boom"))));

        var result = await pipeline.PublishCacheWarmingDecisionAsync(
            pipeline.Session.CreateContext(null, null),
            new CacheWarmingDecisionEvent { Action = "warm" });

        // fail-closed：异常时事件保持原样，不因扩展故障阻断预热。
        Assert.True(result.IsWarm);
    }

    [Fact]
    public void DecisionEvent_HelpersToggleAction()
    {
        var e = new CacheWarmingDecisionEvent { Action = "warm" };

        Assert.True(e.IsWarm);
        e.Stop();
        Assert.False(e.IsWarm);
        e.Warm();
        Assert.True(e.IsWarm);
    }

    [Fact]
    public async Task Warmer_ConsultsPipeline_AndHonoursStop()
    {
        var called = 0;
        var pipeline = Pipeline(new ProbeExtension("warm", api =>
            api.On<CacheWarmingDecisionEvent>(ExtensionEvent.CacheWarmingDecision, (e, _, _) =>
            {
                Interlocked.Increment(ref called);
                e.Stop();
                return Task.FromResult<CacheWarmingDecisionEvent?>(e);
            })));

        var client = new RecordingClient();
        using var warmer = new PromptCacheWarmer(
            new PromptCacheWarmingOptions { Mode = PromptCacheWarmingMode.Streaming },
            async decision =>
            {
                var dispatched = await pipeline.PublishCacheWarmingDecisionAsync(
                    pipeline.Session.CreateContext(null, null),
                    new CacheWarmingDecisionEvent
                    {
                        Phase = decision.Phase,
                        Model = decision.Model,
                        PromptTokens = decision.PromptTokens,
                        Action = decision.Action == CacheWarmingAction.Warm ? "warm" : "stop"
                    });

                return dispatched.IsWarm ? CacheWarmingAction.Warm : CacheWarmingAction.Stop;
            },
            (_, _) => Task.CompletedTask);

        warmer.OnRunStarted();
        warmer.Track(new PromptCacheWarmRequest
        {
            Client = client,
            Request = ChatRequest.FromUser("claude-sonnet-4-5", "hi"),
            Policy = new PromptCachePolicy
            {
                Kind = PromptCacheKind.Explicit,
                Lifetime = TimeSpan.FromMinutes(5),
                Pricing = new PromptCachePricing
                {
                    InputPerMillion = 3,
                    CachedInputPerMillion = 0.3,
                    OutputPerMillion = 15
                }
            },
            PromptTokens = 200_000
        }, () => true);

        Assert.True(SpinWait.SpinUntil(
            () => warmer.Status.State == "inactive",
            TimeSpan.FromSeconds(2)));

        Assert.Equal(1, Volatile.Read(ref called));
        Assert.Empty(client.Requests);
        Assert.Equal("预期收益低于阈值", warmer.Status.Reason);
    }

    /// <summary>记录请求的聊天客户端。</summary>
    private sealed class RecordingClient : IChatClient
    {
        public string ProviderName => "anthropic";

        public List<ChatRequest> Requests { get; } = new();

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatResponse { Message = ChatMessage.Assistant("ok") });
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
