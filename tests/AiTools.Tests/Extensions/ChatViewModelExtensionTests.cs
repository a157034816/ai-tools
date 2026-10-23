using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Sessions;
using AiTools.Extensions;
using AiTools.Wpf;
using AiTools.Wpf.Configuration;
using AiTools.Wpf.ViewModels;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>
/// 验证对话页把插件斜杠命令展开为一条提示词用户消息，并且不会把原始 <c>/goal</c>
/// 当作普通文本发给模型。
/// </summary>
[Collection(AiTools.Tests.Wpf.AiToolsWpfGlobalCollection.Name)]
public sealed class ChatViewModelExtensionTests : IDisposable
{
    private readonly CapturingChatClient _client = new();
    private readonly string _pluginsRoot = Path.Combine(Path.GetTempPath(), "aitools-vm-plugins-" + Guid.NewGuid().ToString("N"));

    public ChatViewModelExtensionTests()
    {
        var pluginRoot = Path.Combine(_pluginsRoot, "workflows");
        Directory.CreateDirectory(Path.Combine(pluginRoot, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(pluginRoot, "commands"));
        File.WriteAllText(Path.Combine(pluginRoot, ".claude-plugin", "plugin.json"), """{"name":"workflows"}""");
        File.WriteAllText(Path.Combine(pluginRoot, "commands", "goal.md"), """
            ---
            description: Track a goal
            argument-hint: <goal>
            ---
            Objective: $ARGUMENTS
            Define success criteria.
            """);

        AiToolsWpf.Reset();
        AiToolsWpf.Initialize(options =>
        {
            options.ConversationStore = new InMemoryConversationStore();
            options.AutoSaveConversations = false;
            options.SettingsStore = new FixedSettingsStore(providerConfig: new ProviderConfig
            {
                Id = "test",
                DisplayName = "Test",
                Protocol = ProviderProtocols.OpenAi,
                BaseUrl = "https://example.com/v1",
                ApiKey = "test-key",
                Models = new List<ModelConfig> { new() { Id = "test-model", Enabled = true } }
            });
            options.ProviderFactory = new FixedProviderFactory(_client);
            options.PluginsDirectory = _pluginsRoot;
            options.AllowPluginShellExecution = false;
        });
    }

    public void Dispose()
    {
        AiToolsWpf.Reset();
        try
        {
            if (Directory.Exists(_pluginsRoot))
            {
                Directory.Delete(_pluginsRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task InitializeAsync_ExposesPluginCommands()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        var command = Assert.Single(vm.AvailableCommands);
        Assert.Equal("goal", command.ShortName);
        Assert.Equal("/goal <goal>", command.Title);
        Assert.True(vm.HasPluginCommands);
    }

    [Fact]
    public async Task TypingSlash_OpensCommandPalette()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        vm.InputText = "/g";
        Assert.True(vm.IsCommandPaletteOpen);
        Assert.Equal("goal", vm.SelectedCommand!.ShortName);

        vm.InputText = "/g now with args";
        Assert.False(vm.IsCommandPaletteOpen);

        vm.InputText = "plain text";
        Assert.False(vm.IsCommandPaletteOpen);
    }

    [Fact]
    public async Task ApplyingCommand_InsertsInvocation()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();
        vm.InputText = "/";

        vm.ApplyCommandCommand.Execute(vm.AvailableCommands[0]);

        Assert.Equal("/goal ", vm.InputText);
        Assert.False(vm.IsCommandPaletteOpen);
    }

    [Fact]
    public async Task SendingPluginCommand_ExpandsPromptInsteadOfRawText()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        vm.InputText = "/goal ship the plugin framework";
        await vm.SendAsync();

        var request = Assert.Single(_client.Requests);
        var userMessage = request.Messages[request.Messages.Count - 1];
        Assert.Equal(ChatRole.User, userMessage.Role);
        var text = userMessage.GetText();
        Assert.Contains("Objective: ship the plugin framework", text);
        Assert.DoesNotContain("/goal ship", text);

        // 气泡显示原始命令，完整提示词只进入请求历史。
        Assert.Contains(vm.Messages, message => message.IsUser && message.Text == "/goal");
    }

    [Fact]
    public async Task SendingUnknownSlashCommand_FallsBackToPlainMessage()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        vm.InputText = "/unknown thing";
        await vm.SendAsync();

        var request = Assert.Single(_client.Requests);
        Assert.Equal("/unknown thing", request.Messages[request.Messages.Count - 1].GetText());
    }

    [Fact]
    public async Task PluginSkillListing_IsInjectedIntoSystemPrompt()
    {
        // 插件技能应出现在系统提示里，模型才知道何时 load_skill。
        var skillsRoot = Path.Combine(_pluginsRoot, "workflows", "skills", "review");
        Directory.CreateDirectory(skillsRoot);
        File.WriteAllText(Path.Combine(skillsRoot, "SKILL.md"), "---\ndescription: Reviews a diff.\n---\nReview it.");

        var vm = new ChatViewModel();
        await vm.InitializeAsync();
        vm.InputText = "hello";
        await vm.SendAsync();

        var request = Assert.Single(_client.Requests);
        Assert.Contains("Reviews a diff.", request.SystemPrompt);
        Assert.Contains("load_skill", request.SystemPrompt);
    }

    [Fact]
    public async Task Retry_ResendsExpandedCommandPrompt()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();
        vm.InputText = "/goal keep it";
        await vm.SendAsync();

        vm.RetryCommand.Execute(null);
        await WaitForAsync(() => _client.Requests.Count >= 2);

        var last = _client.Requests[^1];
        var userMessage = last.Messages[last.Messages.Count - 1];
        Assert.Equal(ChatRole.User, userMessage.Role);
        Assert.Contains("Objective: keep it", userMessage.GetText());
        Assert.DoesNotContain("/goal keep it", userMessage.GetText());
    }

    [Fact]
    public async Task BuiltInHelpCommand_DoesNotCallProvider()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        vm.InputText = "/help goal";
        await vm.SendAsync();

        Assert.Empty(_client.Requests);
        Assert.Contains(vm.Messages, message => !message.IsUser && message.Text.Contains("/goal"));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    private sealed class FixedSettingsStore : ISettingsStore
    {
        private readonly AppSettings _settings;

        public FixedSettingsStore(ProviderConfig providerConfig)
        {
            _settings = new AppSettings { Providers = new List<ProviderConfig> { providerConfig } };
            _settings.SelectedProviderId = providerConfig.Id;
            _settings.SelectedModelId = providerConfig.Models[0].Id;
        }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedProviderFactory : IProviderFactory
    {
        private readonly IChatClient _client;

        public FixedProviderFactory(IChatClient client) => _client = client;

        public IChatClient CreateChatClient(ProviderConfig provider) => _client;
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatRequest> Requests { get; } = new();

        public string ProviderName => "capturing";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatResponse
            {
                Message = ChatMessage.Assistant("ok"),
                FinishReason = ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ChatStreamUpdate { TextDelta = "ok", FinishReason = ChatFinishReason.Stop };
        }
    }
}
