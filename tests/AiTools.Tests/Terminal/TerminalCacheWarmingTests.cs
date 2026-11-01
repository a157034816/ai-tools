using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Core.Chat;
using AiTools.Terminal.Agent;
using Xunit;

namespace AiTools.Tests.Terminal;

/// <summary>
/// 验证终端宿主把预热器接进了请求周期：请求发出前就按估算值排程，
/// 响应回来用真实用量重新排程，一轮结束后 Streaming 模式停止。
/// </summary>
public sealed class TerminalCacheWarmingTests : IDisposable
{
    private readonly string _root;

    public TerminalCacheWarmingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aitools-warm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static AppSettings Settings(string protocol, string modelId, string baseUrl) => new()
    {
        Version = 1,
        Providers = new List<ProviderConfig>
        {
            new()
            {
                Id = "test",
                DisplayName = "Test",
                Protocol = protocol,
                BaseUrl = baseUrl,
                ApiKey = "key",
                Models = new List<ModelConfig> { new() { Id = modelId } }
            }
        },
        SelectedProviderId = "test",
        SelectedModelId = modelId
    };

    private TerminalOptions Options(
        IChatClient client,
        AppSettings settings,
        PromptCacheOptions? promptCache = null,
        PromptCacheWarmingOptions? warming = null) => new()
        {
            SettingsStore = new StaticSettingsStore(settings),
            ProviderFactory = new ScriptedProviderFactory(client),
            ConversationStore = new AiTools.Core.Sessions.JsonConversationStore(_root),
            WorkingDirectory = _root,
            EnableSharedAgents = false,
            EnableExtensions = false,
            EnableExtensionPackages = false,
            AutoDiscoverPlugins = false,
            DefaultSystemPrompt = "base-prompt",
            PromptCache = promptCache ?? new PromptCacheOptions(),
            PromptCacheWarming = warming ?? new PromptCacheWarmingOptions
            {
                Mode = PromptCacheWarmingMode.Streaming
            }
        };

    [Fact]
    public async Task SubmitAsync_WarmableModel_SchedulesWarmingDuringRequest()
    {
        // 在流式请求进行中观测预热状态：正是"等响应/跑工具"这段需要保活的窗口。
        CacheWarmingStatus? duringRequest = null;
        AgentSession? session = null;
        var client = new ObservingChatClient(() => duringRequest = session?.CacheWarmingStatus);

        var options = Options(
            client,
            Settings(ProviderProtocols.Anthropic, "claude-sonnet-4-5", "https://api.anthropic.com/v1"));

        session = new AgentSession(options);
        await using var _ = session;
        await session.InitializeAsync();

        Assert.NotNull(session.CacheWarmer);

        await session.SubmitAsync("你好");

        // 请求期间已排程；一轮结束（Streaming 模式）后停止。
        Assert.NotNull(duringRequest);
        Assert.Equal("scheduled", duringRequest!.State);
        Assert.Equal("inactive", session.CacheWarmingStatus!.State);
        Assert.Equal("本轮已结束", session.CacheWarmingStatus.Reason);
    }

    /// <summary>在流式请求开始时回调，用于观测宿主状态。</summary>
    private sealed class ObservingChatClient : IChatClient
    {
        private readonly Action _onStreamStart;

        public ObservingChatClient(Action onStreamStart) => _onStreamStart = onStreamStart;

        public string ProviderName => "anthropic";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse
            {
                Message = ChatMessage.Assistant("ok"),
                FinishReason = ChatFinishReason.Stop,
                Usage = new Usage { InputTokens = 100, OutputTokens = 1 }
            });

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _onStreamStart();
            await Task.Yield();
            yield return new ChatStreamUpdate { TextDelta = "ok" };
            yield return new ChatStreamUpdate
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new Usage { InputTokens = 100, OutputTokens = 1 }
            };
        }
    }

    [Fact]
    public async Task SubmitAsync_DisabledWarming_DoesNotCreateWarmer()
    {
        var client = new ScriptedChatClient().Reply("ok");
        var options = Options(
            client,
            Settings(ProviderProtocols.Anthropic, "claude-sonnet-4-5", "https://api.anthropic.com/v1"),
            promptCache: new PromptCacheOptions { WarmingEnabled = false });

        await using var session = new AgentSession(options);
        await session.InitializeAsync();

        Assert.Null(session.CacheWarmer);
        Assert.Null(session.CacheWarmingStatus);

        var result = await session.SubmitAsync("你好");
        Assert.True(result!.Succeeded);
    }

    [Fact]
    public async Task SubmitAsync_NonCacheableModel_LeavesWarmerInactive()
    {
        var client = new ScriptedChatClient().Reply("ok");
        var options = Options(
            client,
            // o1-mini 明确不参与前缀缓存。
            Settings(ProviderProtocols.OpenAi, "o1-mini", "https://example.test/v1"));

        await using var session = new AgentSession(options);
        await session.InitializeAsync();

        await session.SubmitAsync("你好");

        Assert.NotNull(session.CacheWarmingStatus);
        Assert.Equal("inactive", session.CacheWarmingStatus!.State);
    }

    [Fact]
    public async Task SelectModel_CancelsWarming()
    {
        var client = new ScriptedChatClient().Reply("ok");
        var settings = Settings(ProviderProtocols.Anthropic, "claude-sonnet-4-5", "https://api.anthropic.com/v1");
        settings.Providers[0].Models.Add(new ModelConfig { Id = "claude-haiku-4-5" });

        await using var session = new AgentSession(Options(client, settings));
        await session.InitializeAsync();
        await session.SubmitAsync("你好");

        session.SelectModel("test", "claude-haiku-4-5");

        Assert.Equal("claude-haiku-4-5", session.CurrentModel!.Id);
        Assert.Equal("inactive", session.CacheWarmingStatus!.State);
    }

    [Fact]
    public async Task NewConversation_CancelsWarming()
    {
        var client = new ScriptedChatClient().Reply("ok");
        await using var session = new AgentSession(Options(
            client,
            Settings(ProviderProtocols.Anthropic, "claude-sonnet-4-5", "https://api.anthropic.com/v1")));
        await session.InitializeAsync();
        await session.SubmitAsync("你好");

        await session.NewConversationAsync();

        Assert.Equal("inactive", session.CacheWarmingStatus!.State);
    }
}
