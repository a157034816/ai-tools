using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Configuration;
using AiTools.Terminal;
using AiTools.Terminal.Agent;
using Xunit;

namespace AiTools.Tests.Terminal;

/// <summary>返回预置回复的聊天客户端。</summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<ChatRequest, ChatResponse>> _responses = new();
    private Func<ChatRequest, ChatResponse>? _replay;

    public string ProviderName => "scripted";

    public List<ChatRequest> Requests { get; } = new();

    public ScriptedChatClient Reply(string text)
    {
        _responses.Enqueue(_ => new ChatResponse
        {
            Message = ChatMessage.Assistant(text),
            FinishReason = ChatFinishReason.Stop,
            Usage = new Usage { InputTokens = 3, OutputTokens = 5 }
        });
        return this;
    }

    public ScriptedChatClient ToolCall(string id, string name, string argumentsJson)
    {
        _responses.Enqueue(_ => new ChatResponse
        {
            Message = ChatMessage.AssistantWithToolCalls(new[] { new ToolCall(id, name, argumentsJson) }),
            FinishReason = ChatFinishReason.ToolCalls
        });
        return this;
    }

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(Next(request));
    }

    public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response;
        lock (Requests)
        {
            Requests.Add(request);
            response = Next(request);
            if (response.ToolCalls.Count > 0)
            {
                // 工具循环会以非流式方式重发同一轮以取回 ProviderData；缓存响应让它保持一致。
                _replay = _responsesForReplay;
            }
        }

        var text = response.GetText();
        if (text.Length > 0)
        {
            yield return new ChatStreamUpdate { TextDelta = text };
        }

        if (response.ToolCalls.Count > 0)
        {
            for (var index = 0; index < response.ToolCalls.Count; index++)
            {
                var call = response.ToolCalls[index];
                yield return new ChatStreamUpdate
                {
                    ToolCallDelta = new ToolCallDelta
                    {
                        Index = index,
                        Id = call.Id,
                        Name = call.Name,
                        ArgumentsDelta = call.ArgumentsJson
                    }
                };
            }

            yield return new ChatStreamUpdate { FinishReason = ChatFinishReason.ToolCalls };
            yield break;
        }

        yield return new ChatStreamUpdate { FinishReason = ChatFinishReason.Stop, Usage = response.Usage };
    }

    private Func<ChatRequest, ChatResponse>? _responsesForReplay;

    private ChatResponse Next(ChatRequest request)
    {
        if (_replay != null)
        {
            var replay = _replay;
            _replay = null;
            return replay(request);
        }

        if (_responses.Count == 0)
        {
            return new ChatResponse
            {
                Message = ChatMessage.Assistant(string.Empty),
                FinishReason = ChatFinishReason.Stop
            };
        }

        var factory = _responses.Dequeue();
        _responsesForReplay = factory;
        return factory(request);
    }
}

/// <summary>测试用工厂：返回共享的脚本客户端，并记录收到的配置。</summary>
internal sealed class ScriptedProviderFactory : IProviderFactory
{
    private readonly IChatClient _client;

    public ScriptedProviderFactory(IChatClient client) => _client = client;

    public IChatClient CreateChatClient(ProviderConfig provider) => _client;
}

internal sealed class StaticSettingsStore : ISettingsStore
{
    public StaticSettingsStore(AppSettings settings) => Settings = settings;

    public AppSettings Settings { get; }

    public int SaveCount { get; private set; }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

public class AgentSessionTests : IDisposable
{
    private readonly string _root;

    public AgentSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aitools-term-" + Guid.NewGuid().ToString("N"));
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

    private TerminalOptions Options(IChatClient client, bool extensions = false)
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
                    Models = new List<ModelConfig> { new() { Id = "scripted-model" } }
                }
            },
            SelectedProviderId = "test",
            SelectedModelId = "scripted-model"
        };

        return new TerminalOptions
        {
            SettingsStore = new StaticSettingsStore(settings),
            ProviderFactory = new ScriptedProviderFactory(client),
            ConversationStore = new AiTools.Core.Sessions.JsonConversationStore(_root),
            WorkingDirectory = _root,
            EnableSharedAgents = false,
            EnableExtensions = extensions,
            EnableExtensionPackages = false,
            AutoDiscoverPlugins = false,
            DefaultSystemPrompt = "base-prompt"
        };
    }

    [Fact]
    public async Task SubmitAsync_StreamsTextAndRecordsHistory()
    {
        var client = new ScriptedChatClient().Reply("你好，我是模型。");
        await using var session = new AgentSession(Options(client));
        await session.InitializeAsync();

        var updates = new List<AgentUpdate>();
        session.Updates += update => updates.Add(update);

        var result = await session.SubmitAsync("你好");

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        Assert.Equal("你好，我是模型。", result.Text);
        Assert.Contains(updates, update => update.Kind == AgentUpdateKind.TextDelta && update.Text == "你好，我是模型。");
        Assert.Contains(updates, update => update.Kind == AgentUpdateKind.TurnCompleted);

        var history = session.History;
        Assert.Equal(2, history.Count);
        Assert.Equal("你好", history[0].Message.GetText());
        Assert.Equal("你好，我是模型。", history[1].Message.GetText());
        Assert.Contains("base-prompt", client.Requests[0].SystemPrompt ?? string.Empty);
    }

    [Fact]
    public async Task SubmitAsync_PersistsConversation()
    {
        var client = new ScriptedChatClient().Reply("ok");
        await using var session = new AgentSession(Options(client));
        await session.InitializeAsync();

        await session.SubmitAsync("保存这条消息");

        var summaries = await session.ListConversationsAsync();
        Assert.Single(summaries);
        Assert.Contains("保存这条消息", summaries[0].Title);

        var record = await session.Options.ConversationStore!.LoadAsync(summaries[0].Id);
        Assert.NotNull(record);
        Assert.Equal(2, record!.Messages.Count);
        Assert.Equal("test", record.ProviderId);
    }

    [Fact]
    public async Task SubmitAsync_ExecutesToolThenContinues()
    {
        var client = new ScriptedChatClient()
            .ToolCall("call-1", "echo", "{\"text\":\"hi\"}")
            .Reply("工具结果已收到。");

        var provider = new RecordingToolProvider();
        var options = Options(client);

        await using var session = new AgentSession(options);
        await session.InitializeAsync();

        var updates = new List<AgentUpdate>();
        session.Updates += update => updates.Add(update);

        // 通过宿主注册的工具 provider 暴露工具。
        var runSession = new AgentSession(new TerminalOptions
        {
            SettingsStore = options.SettingsStore,
            ProviderFactory = options.ProviderFactory,
            ConversationStore = options.ConversationStore,
            WorkingDirectory = options.WorkingDirectory,
            EnableSharedAgents = false,
            EnableExtensions = false,
            EnableExtensionPackages = false,
            AutoDiscoverPlugins = false,
            ToolProviders = new IChatToolProvider[] { provider }
        });

        await using var active = runSession;
        await active.InitializeAsync();
        active.Updates += update => updates.Add(update);

        var result = await active.SubmitAsync("用工具");

        Assert.NotNull(result);
        Assert.Equal(1, provider.InvocationCount);
        Assert.Contains(updates, update => update.Kind == AgentUpdateKind.ToolStarted && update.ToolCall?.Name == "echo");
        Assert.Contains(updates, update => update.Kind == AgentUpdateKind.ToolFinished && update.ToolResult == "echo:hi");
        Assert.Equal("工具结果已收到。", result!.Text);
    }

    [Fact]
    public async Task SubmitAsync_WithoutProvider_ReturnsNotice()
    {
        var client = new ScriptedChatClient();
        var options = Options(client);
        options.SettingsStore = new StaticSettingsStore(new AppSettings());
        options.ProviderId = "missing";

        await using var session = new AgentSession(options);
        await session.InitializeAsync();

        var updates = new List<AgentUpdate>();
        session.Updates += update => updates.Add(update);

        var result = await session.SubmitAsync("你好");

        Assert.Null(result);
        Assert.Contains(updates, update => update.Kind == AgentUpdateKind.Notice && update.NoticeKind == NoticeKind.Warning);
    }

    [Fact]
    public async Task EnvironmentFallback_EnablesProviderWhenNoSettings()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-fallback");
        try
        {
            var options = new TerminalOptions
            {
                SettingsStore = new StaticSettingsStore(new AppSettings()),
                ProviderFactory = new ScriptedProviderFactory(new ScriptedChatClient()),
                ConversationStore = new AiTools.Core.Sessions.JsonConversationStore(_root),
                WorkingDirectory = _root,
                EnableSharedAgents = false,
                EnableExtensions = false,
                EnableExtensionPackages = false,
                AutoDiscoverPlugins = false
            };

            await using var session = new AgentSession(options);
            await session.InitializeAsync();

            Assert.NotNull(session.CurrentProvider);
            Assert.Equal("openai", session.CurrentProvider!.Id);
            Assert.NotEmpty(session.AvailableModels);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public async Task NewConversation_ClearsHistoryAndState()
    {
        var client = new ScriptedChatClient().Reply("first").Reply("second");
        await using var session = new AgentSession(Options(client));
        await session.InitializeAsync();

        await session.SubmitAsync("第一条");
        Assert.NotEmpty(session.History);

        await session.NewConversationAsync();

        Assert.Empty(session.History);
        Assert.Equal("新对话", session.Title);
    }

    [Fact]
    public async Task ResumeAsync_RestoresMessagesAndModel()
    {
        var client = new ScriptedChatClient().Reply("answer");
        await using var session = new AgentSession(Options(client));
        await session.InitializeAsync();

        await session.SubmitAsync("恢复我");
        var summaries = await session.ListConversationsAsync();
        var id = summaries[0].Id;

        await session.NewConversationAsync();
        Assert.Empty(session.History);

        Assert.True(await session.ResumeAsync(id));
        Assert.Equal(2, session.History.Count);
        Assert.Equal("test", session.CurrentProvider?.Id);
        Assert.Equal("scripted-model", session.CurrentModel?.Id);
    }

    [Fact]
    public async Task ForkAsync_CreatesIndependentConversation()
    {
        var client = new ScriptedChatClient().Reply("answer");
        await using var session = new AgentSession(Options(client));
        await session.InitializeAsync();
        await session.SubmitAsync("原始消息");

        Assert.True(await session.ForkAsync());
        Assert.NotEqual("新对话", session.Title);
        Assert.Contains("分叉", session.Title, StringComparison.Ordinal);
        Assert.Equal(2, (await session.ListConversationsAsync()).Count);
    }

    [Fact]
    public async Task SelectModel_UpdatesSelectionAndSettings()
    {
        var client = new ScriptedChatClient();
        var store = new StaticSettingsStore(new AppSettings
        {
            Providers = new List<ProviderConfig>
            {
                new()
                {
                    Id = "a",
                    DisplayName = "A",
                    BaseUrl = "https://a.test/v1",
                    ApiKey = "k",
                    Models = new List<ModelConfig> { new() { Id = "m1" }, new() { Id = "m2" } }
                }
            }
        });

        var options = new TerminalOptions
        {
            SettingsStore = store,
            ProviderFactory = new ScriptedProviderFactory(client),
            ConversationStore = new AiTools.Core.Sessions.JsonConversationStore(_root),
            WorkingDirectory = _root,
            EnableSharedAgents = false,
            EnableExtensions = false,
            EnableExtensionPackages = false,
            AutoDiscoverPlugins = false
        };

        await using var session = new AgentSession(options);
        await session.InitializeAsync();
        session.SelectModel("a", "m2");

        Assert.Equal("m2", session.CurrentModel?.Id);
        Assert.Equal("m2", store.Settings.SelectedModelId);
    }
}

internal sealed class RecordingToolProvider : IChatToolProvider
{
    public int InvocationCount { get; private set; }

    public IReadOnlyList<ToolDefinition> GetTools() => new[]
    {
        ToolDefinition.FromJsonSchema("echo", "回显文本", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}")
    };

    public Task<string> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
    {
        InvocationCount++;
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var text = document.RootElement.TryGetProperty("text", out var element) ? element.GetString() : string.Empty;
        return Task.FromResult("echo:" + text);
    }
}
