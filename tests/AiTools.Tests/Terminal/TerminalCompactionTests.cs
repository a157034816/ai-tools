using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Core.Chat;
using AiTools.Terminal.Agent;
using Xunit;

namespace AiTools.Tests.Terminal;

/// <summary>按脚本回复、并记录请求的聊天客户端；用于观察压缩前后的消息列表。</summary>
internal sealed class CompactionAwareClient : IChatClient
{
    public string ProviderName => "compaction-aware";

    public List<ChatRequest> Requests { get; } = new();

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        var last = request.Messages.LastOrDefault()?.GetText() ?? string.Empty;
        var isSummaryRequest = request.SystemPrompt?.Contains("对话压缩助手", StringComparison.Ordinal) == true;
        var text = isSummaryRequest ? "（摘要）用户询问了若干问题，助手逐一作答。" : "回复：" + Trim(last);

        return Task.FromResult(new ChatResponse
        {
            Message = ChatMessage.Assistant(text),
            FinishReason = ChatFinishReason.Stop,
            Usage = new Usage { InputTokens = 10, OutputTokens = 10 }
        });
    }

    public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        yield return new ChatStreamUpdate { TextDelta = response.GetText() };
        yield return new ChatStreamUpdate { FinishReason = ChatFinishReason.Stop, Usage = response.Usage };
    }

    private static string Trim(string text)
        => text.Length <= 20 ? text : text.Substring(0, 20) + "…";
}

public class TerminalCompactionTests : IDisposable
{
    private readonly string _root;
    private readonly CompactionAwareClient _client = new();

    public TerminalCompactionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aitools-compact-" + Guid.NewGuid().ToString("N"));
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

    private TerminalOptions Options(CompactionOptions? compaction = null, string modelId = "scripted-model")
    {
        var settings = new AppSettings
        {
            Version = 1,
            Providers = new List<ProviderConfig>
            {
                new()
                {
                    Id = "test",
                    DisplayName = "Test",
                    Protocol = ProviderProtocols.OpenAi,
                    BaseUrl = "https://example.test/v1",
                    ApiKey = "key",
                    Models = new List<ModelConfig> { new() { Id = modelId } }
                }
            },
            SelectedProviderId = "test",
            SelectedModelId = modelId
        };

        return new TerminalOptions
        {
            SettingsStore = new StaticSettingsStore(settings),
            ProviderFactory = new ScriptedProviderFactory(_client),
            ConversationStore = new AiTools.Core.Sessions.JsonConversationStore(_root),
            WorkingDirectory = _root,
            EnableSharedAgents = false,
            EnableExtensions = false,
            EnableExtensionPackages = false,
            AutoDiscoverPlugins = false,
            DefaultSystemPrompt = "base-prompt",
            Compaction = compaction ?? new CompactionOptions()
        };
    }

    private static string LongText(int length) => new string('x', length);

    [Fact]
    public async Task SendTurn_CompactsWhenReachingNinetyPercentOfWindow()
    {
        // 模型条目显式声明 2000 token 窗口：阈值 1800，触发后在目标 1000 以内。
        var options = Options();
        options.Compaction = new CompactionOptions { MinRecentTurns = 1 };
        options.ProviderId = "test";
        options.ModelId = "scripted-model";
        options.SettingsStore = new StaticSettingsStore(new AppSettings
        {
            Providers = new List<ProviderConfig>
            {
                new()
                {
                    Id = "test",
                    DisplayName = "Test",
                    Protocol = ProviderProtocols.OpenAi,
                    BaseUrl = "https://example.test/v1",
                    ApiKey = "key",
                    Models = new List<ModelConfig> { new() { Id = "scripted-model", ContextWindow = 2_000 } }
                }
            },
            SelectedProviderId = "test",
            SelectedModelId = "scripted-model"
        });

        await using var session = new AgentSession(options);
        await session.InitializeAsync();

        // 每轮 ~1600 字符 ≈ 400 tokens；6 轮后累计超过 1800。
        for (var turn = 0; turn < 6; turn++)
        {
            await session.SubmitAsync("第" + turn + "轮：" + LongText(1_600));
        }

        Assert.NotNull(session.Compaction);
        Assert.True(session.Compaction!.CoveredMessageCount > 0);

        // 压缩后发给模型的请求里，被覆盖的原文不再出现，取而代之是摘要消息。
        var lastRequest = _client.Requests.Last(item => item.SystemPrompt?.Contains("对话压缩助手", StringComparison.Ordinal) != true);
        var allText = string.Join("\n", lastRequest.Messages.Select(message => message.GetText()));
        Assert.Contains("（摘要）", allText);
        Assert.DoesNotContain("第0轮", allText);
    }

    [Fact]
    public async Task CompactAsync_Manual_SummarizesHistory()
    {
        var options = Options();
        var settings = (StaticSettingsStore)options.SettingsStore!;
        settings.Settings.Providers[0].Models[0].ContextWindow = 1_000;
        options.Compaction = new CompactionOptions { MinRecentTurns = 1 };

        await using var session = new AgentSession(options);
        await session.InitializeAsync();

        await session.SubmitAsync("第一轮：" + LongText(1_200));
        await session.SubmitAsync("第二轮：" + LongText(1_200));

        var compacted = await session.CompactAsync();

        Assert.True(compacted);
        Assert.NotNull(session.Compaction);
        Assert.Contains("摘要", session.Compaction!.Summary);
    }

    [Fact]
    public async Task CompactAsync_NoHistory_IsNoop()
    {
        await using var session = new AgentSession(Options());
        await session.InitializeAsync();

        Assert.False(await session.CompactAsync());
        Assert.Null(session.Compaction);
    }

    [Fact]
    public async Task Compaction_SurvivesResume()
    {
        var options = Options();
        ((StaticSettingsStore)options.SettingsStore!).Settings.Providers[0].Models[0].ContextWindow = 1_000;
        options.Compaction = new CompactionOptions { MinRecentTurns = 1 };

        await using var session = new AgentSession(options);
        await session.InitializeAsync();
        await session.SubmitAsync("第一轮：" + LongText(1_200));
        await session.SubmitAsync("第二轮：" + LongText(1_200));
        Assert.True(await session.CompactAsync());
        var conversationId = session.ConversationId;
        Assert.NotNull(conversationId);

        await session.NewConversationAsync();
        Assert.Null(session.Compaction);

        Assert.True(await session.ResumeAsync(conversationId!));
        Assert.NotNull(session.Compaction);
        Assert.Contains("摘要", session.Compaction!.Summary);
    }

    [Fact]
    public async Task DescribeContextUsage_ReportsUsageAndWindow()
    {
        var options = Options();
        ((StaticSettingsStore)options.SettingsStore!).Settings.Providers[0].Models[0].ContextWindow = 50_000;

        await using var session = new AgentSession(options);
        await session.InitializeAsync();
        await session.SubmitAsync("你好");

        var (used, limit) = session.DescribeContextUsage();
        Assert.True(used > 0);
        Assert.Equal(50_000, limit);
    }

    [Fact]
    public async Task DisabledCompaction_LeavesHistoryIntact()
    {
        var options = Options(new CompactionOptions { Enabled = false });
        ((StaticSettingsStore)options.SettingsStore!).Settings.Providers[0].Models[0].ContextWindow = 1_000;

        await using var session = new AgentSession(options);
        await session.InitializeAsync();

        for (var turn = 0; turn < 3; turn++)
        {
            await session.SubmitAsync("第" + turn + "轮：" + LongText(1_200));
        }

        Assert.Null(session.Compaction);
        Assert.False(await session.CompactAsync());
        var lastRequest = _client.Requests.Last();
        Assert.DoesNotContain("（摘要）", string.Join("\n", lastRequest.Messages.Select(message => message.GetText())));
    }
}
