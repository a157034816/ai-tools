using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Sessions;
using AiTools.Extensions;
using AiTools.Extensions.BuiltIn;
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
            options.AutoSaveConversations = true;
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

        // 内置扩展提供 /goal 与 /plan；测试插件另提供 /workflows:goal。
        var goal = Assert.Single(vm.AvailableCommands, item => item.Name == "goal");
        Assert.Equal("/goal <目标描述>", goal.Title);
        Assert.True(goal.IsCodeCommand);

        var pluginCommand = Assert.Single(vm.AvailableCommands, item => item.Name == "workflows:goal");
        Assert.False(pluginCommand.IsCodeCommand);
        // 插件命令的短名与扩展命令冲突，必须用限定名插入。
        Assert.True(pluginCommand.IsAmbiguous);
        Assert.Equal("/workflows:goal ", pluginCommand.Insertion);
        Assert.True(vm.HasPluginCommands);

        // 会话命令（/fork、/resume 等）不依赖任何扩展，必须同样出现在候选面板里。
        Assert.Contains(vm.AvailableCommands, item => item.Name == "fork" && item.IsBuiltIn);
        Assert.Contains(vm.AvailableCommands, item => item.Name == "resume" && item.IsBuiltIn);
        Assert.All(vm.AvailableCommands.Where(item => item.IsBuiltIn), item => Assert.Equal("内置", item.Source));
    }

    [Fact]
    public async Task TypingSlash_OpensCommandPalette()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        vm.InputText = "/g";
        Assert.True(vm.IsCommandPaletteOpen);
        Assert.Contains(vm.AvailableCommands, item => item.Name == "goal");

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

        // 面板第一项是内置会话命令 /resume。
        var first = vm.AvailableCommands[0];
        Assert.True(first.IsBuiltIn);
        vm.ApplyCommandCommand.Execute(first);

        Assert.Equal("/resume ", vm.InputText);
        Assert.False(vm.IsCommandPaletteOpen);
    }

    [Fact]
    public async Task SendingExtensionCommand_ExpandsPromptInsteadOfRawText()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        vm.InputText = "/goal ship the plugin framework";
        await vm.SendAsync();

        var request = Assert.Single(_client.Requests);
        var userMessage = request.Messages[request.Messages.Count - 1];
        Assert.Equal(ChatRole.User, userMessage.Role);
        var text = userMessage.GetText();
        Assert.Contains("ship the plugin framework", text);
        Assert.Contains(GoalExtension.PlanToolName, text);
        Assert.DoesNotContain("/goal ship", text);

        // 气泡显示命令名，完整提示词只进入请求历史。
        Assert.Contains(vm.Messages, message => message.IsUser && message.Text == "/goal");
    }

    [Fact]
    public async Task GoalCommand_InjectsGoalStateOnNextTurn()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        // 第一轮：/goal 建立状态并发出指令。
        vm.InputText = "/goal ship it";
        await vm.SendAsync();

        // 模型通过 goal_plan 记录目标与标准（相当于一次工具调用）。
        var pipeline = vm.Pipeline!;
        var recorded = await pipeline.Runtime.ExecuteToolAsync(
            GoalExtension.PlanToolName,
            """{"objective":"ship it","criteria":[{"text":"tests pass"}],"steps":["wire it"]}""",
            pipeline.Session.CreateContext(null, null));

        Assert.Contains("Goal recorded", recorded.Text);

        // 第二轮：状态应作为系统提示段落注入。
        vm.InputText = "继续";
        await vm.SendAsync();

        var second = _client.Requests[^1];
        Assert.Contains("## Session goal", second.SystemPrompt);
        Assert.Contains("tests pass", second.SystemPrompt);
        Assert.Contains("0/1 steps", second.SystemPrompt);
    }

    [Fact]
    public async Task PlanCommand_TracksApprovalAndSteps()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        vm.InputText = "/plan refactor sessions";
        await vm.SendAsync();

        var pipeline = vm.Pipeline!;
        var context = pipeline.Session.CreateContext(null, null);

        var saved = await pipeline.Runtime.ExecuteToolAsync(
            PlanExtension.SaveToolName,
            """{"task":"refactor sessions","steps":[{"title":"extract store","files":["A.cs"],"verify":"dotnet test"}]}""",
            context);
        Assert.Contains("Plan recorded", saved.Text);

        // 未确认时应提示不得开工。
        await pipeline.Runtime.ExecuteToolAsync(PlanExtension.StepToolName, """{"completeStep":0}""", context);
        vm.InputText = "继续";
        await vm.SendAsync();

        var second = _client.Requests[^1];
        Assert.Contains("## Implementation plan", second.SystemPrompt);
        Assert.Contains("NOT approved", second.SystemPrompt);
        Assert.Contains("[x] 0. extract store", second.SystemPrompt);

        // 确认后改为允许实施。
        await pipeline.Runtime.ExecuteToolAsync(PlanExtension.StepToolName, """{"approve":true}""", context);
        vm.InputText = "继续";
        await vm.SendAsync();

        var third = _client.Requests[^1];
        Assert.Contains("approved this plan", third.SystemPrompt);
    }

    [Fact]
    public async Task AskTool_ShowsTheInlinePanelInsteadOfADialog()
    {
        _client.NextToolCall = new ToolCall(
            "call-inline",
            AskExtension.ToolName,
            """{"title":"Which scope?","message":"决定改动范围。","options":[{"label":"Small","description":"Only the parser."},{"label":"Large","description":"Parser and callers."}]}""");

        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        // 默认配置下提问不弹窗，而是让对话页在输入框上方显示面板。
        vm.InputText = "随便问一句";
        var send = vm.SendAsync();
        await WaitForAsync(() => vm.IsAskPanelOpen);

        Assert.True(vm.IsAskPanelOpen, "ask 应打开内联面板；status=" + vm.StatusMessage);
        var prompt = vm.CurrentAskPrompt!;
        Assert.Equal("Which scope?", prompt.Title);
        Assert.Equal("决定改动范围。", prompt.Message);
        Assert.Equal(2, prompt.Options.Count);
        Assert.Equal(1, prompt.Options[1].Index);

        // 用户点击第二个选项。
        vm.SelectAskOptionCommand.Execute(prompt.Options[1].Index);
        await send;

        Assert.False(vm.IsAskPanelOpen);

        // 选择结果回传给模型的下一轮请求。
        Assert.True(_client.Requests.Count >= 2);
        var toolMessage = _client.Requests[^1].Messages
            .Last(message => message.Role == ChatRole.Tool);
        Assert.Contains("Parser and callers", toolMessage.GetText());
    }

    [Fact]
    public async Task AskTool_CancelFromThePanel_ReportsCancellation()
    {
        _client.NextToolCall = new ToolCall(
            "call-cancel",
            AskExtension.ToolName,
            """{"title":"Which scope?","options":[{"label":"Small","description":"Only the parser."},{"label":"Large","description":"Parser and callers."}]}""");

        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        vm.InputText = "随便问一句";
        var send = vm.SendAsync();
        await WaitForAsync(() => vm.IsAskPanelOpen);

        vm.CloseAskCommand.Execute(null);
        await send;

        Assert.False(vm.IsAskPanelOpen);
        var toolMessage = _client.Requests[^1].Messages
            .Last(message => message.Role == ChatRole.Tool);
        Assert.Contains("cancelled", toolMessage.GetText());
    }

    [Fact]
    public async Task AskTool_ReceivesTheHostUiWhenTheModelCallsIt()
    {
        // 模型调用工具时宿主必须把 Ui 传进 ToolContext，否则 ask 在真实对话里永远只能返回 unavailable。
        var ui = new RecordingChoiceUi();
        _client.NextToolCall = new ToolCall(
            "call-1",
            AskExtension.ToolName,
            """{"title":"Which scope?","options":[{"label":"Small","description":"Only the parser."},{"label":"Large","description":"Parser and callers."}]}""");

        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        // InitializeAsync 会安装宿主自己的 Ui；这里替换成可观察的实现。
        AiToolsWpf.Pipeline!.Ui = ui;

        vm.InputText = "随便问一句";
        await vm.SendAsync();

        var asked = Assert.Single(ui.Asked);
        Assert.Equal("Which scope?", asked.Title);
        Assert.Equal(2, asked.Choices.Count);

        // 工具结果必须回传给模型的下一轮请求，否则模型看不到用户的选择。
        Assert.True(_client.Requests.Count >= 2);
        var toolMessage = _client.Requests[^1].Messages
            .Last(message => message.Role == ChatRole.Tool);
        Assert.Contains("\"selected\"", toolMessage.GetText());
        Assert.Contains("Only the parser.", toolMessage.GetText());
    }

    [Fact]
    public async Task ExtensionState_SurvivesSaveAndRestore()
    {
        var vm = new ChatViewModel();
        await vm.InitializeAsync();
        vm.InputText = "/goal persist me";
        await vm.SendAsync();

        var pipeline = vm.Pipeline!;
        await pipeline.Runtime.ExecuteToolAsync(
            GoalExtension.PlanToolName,
            """{"objective":"persist me","criteria":[{"text":"restored"}],"steps":["a"]}""",
            pipeline.Session.CreateContext(null, null));

        // 强制持久化，再从存储里恢复。
        vm.InputText = "继续";
        await vm.SendAsync();
        var record = await AiToolsWpf.ConversationStore.LoadAsync(vm.CurrentConversationId!);
        Assert.NotNull(record);
        Assert.False(string.IsNullOrWhiteSpace(record!.ExtensionState));

        pipeline.Runtime.State.ClearAll();
        pipeline.ImportState(record.ExtensionState);
        var restored = pipeline.Runtime.State.Get<GoalExtensionStateProbe>(GoalExtension.ExtensionId, "current");
        Assert.NotNull(restored);
        Assert.Equal("persist me", restored!.Objective);
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
    public async Task SkillCommands_AppearInThePaletteAndRunFromTheInputBox()
    {
        // 技能只在插件加载时被发现；单独初始化一份带技能的宿主。
        var skillRoot = Path.Combine(Path.GetTempPath(), "aitools-vm-skills-" + Guid.NewGuid().ToString("N"));
        var pluginRoot = Path.Combine(skillRoot, "skills-plugin");
        Directory.CreateDirectory(Path.Combine(pluginRoot, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(pluginRoot, "skills", "review"));
        File.WriteAllText(
            Path.Combine(pluginRoot, ".claude-plugin", "plugin.json"),
            """{"name":"skills-plugin"}""");
        File.WriteAllText(
            Path.Combine(pluginRoot, "skills", "review", "SKILL.md"),
            "---\ndescription: Reviews a diff.\n---\nReview the changed files carefully.");

        AiToolsWpf.Reset();
        AiToolsWpf.Initialize(options =>
        {
            options.ConversationStore = new InMemoryConversationStore();
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
            options.PluginsDirectory = skillRoot;
            options.AllowPluginShellExecution = false;
        });

        try
        {
            var vm = new ChatViewModel();
            await vm.InitializeAsync();

            // 每个 skill 都列出 /skill:<name>，且原本的插件命令名保留（同一技能两条入口）。
            var skillCommand = Assert.Single(
                vm.AvailableCommands,
                item => item.Source == "skill" && item.ShortName.StartsWith("skill:", StringComparison.Ordinal));
            Assert.Equal("skill:skills-plugin:review", skillCommand.Name);
            Assert.Equal("skill:review", skillCommand.ShortName);
            Assert.Contains("Reviews a diff.", skillCommand.Description);
            Assert.Contains(
                vm.AvailableCommands,
                item => item.Source == "skill" && item.Name == "skills-plugin:review");

            // 不带前缀的技能名也能在面板里搜到。
            vm.InputText = "/review";
            Assert.True(vm.IsCommandPaletteOpen, "输入 /review 应命中 /skill:review");
            Assert.NotNull(vm.SelectedCommand);
            Assert.True(
                vm.SelectedCommand!.ShortName == "skill:review" || vm.SelectedCommand.Name == "skills-plugin:review",
                "选中项应是该技能的某条入口：" + vm.SelectedCommand.Name);

            // 直接执行：技能正文成为提示词。
            vm.InputText = "/skill:review 关注错误处理";
            await vm.SendAsync();

            var request = Assert.Single(_client.Requests);
            var userMessage = request.Messages[^1];
            Assert.Equal(ChatRole.User, userMessage.Role);
            Assert.Contains("Review the changed files carefully.", userMessage.GetText());
            Assert.Contains("关注错误处理", userMessage.GetText());
        }
        finally
        {
            try
            {
                Directory.Delete(skillRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
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
        Assert.Contains("keep it", userMessage.GetText());
        Assert.DoesNotContain("/goal", userMessage.GetText());
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

    /// <summary>用于断言恢复结果的最小状态投影。</summary>
    private sealed class GoalExtensionStateProbe
    {
        public string Objective { get; set; } = string.Empty;
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
        private bool _toolCallConsumed;

        public List<ChatRequest> Requests { get; } = new();

        /// <summary>下一个非流式/流式响应要发出的工具调用；每次只发一次。</summary>
        public ToolCall? NextToolCall { get; set; }

        public string ProviderName => "capturing";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(BuildResponse());
        }

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;

            // 流式回合只负责"宣告有工具调用"；真正的工具参数由随后的非流式请求提供
            // （StreamWithToolsExactAsync 的保真路径），因此这里不消费调用。
            if (!_toolCallConsumed && NextToolCall != null)
            {
                yield return new ChatStreamUpdate
                {
                    ToolCallDelta = new ToolCallDelta
                    {
                        Index = 0,
                        Id = NextToolCall.Id,
                        Name = NextToolCall.Name,
                        ArgumentsDelta = NextToolCall.ArgumentsJson
                    },
                    FinishReason = ChatFinishReason.ToolCalls
                };
                yield break;
            }

            yield return new ChatStreamUpdate { TextDelta = "ok", FinishReason = ChatFinishReason.Stop };
        }

        private ChatResponse BuildResponse()
        {
            var call = TakeToolCall();
            return call == null
                ? new ChatResponse
                {
                    Message = ChatMessage.Assistant("ok"),
                    FinishReason = ChatFinishReason.Stop
                }
                : new ChatResponse
                {
                    Message = ChatMessage.AssistantWithToolCalls(new[] { call }),
                    FinishReason = ChatFinishReason.ToolCalls
                };
        }

        private ToolCall? TakeToolCall()
        {
            if (_toolCallConsumed || NextToolCall == null)
            {
                return null;
            }

            _toolCallConsumed = true;
            return NextToolCall;
        }
    }

    private sealed class RecordingChoiceUi : IExtensionUi, IExtensionChoiceUi
    {
        public List<(string Title, IReadOnlyList<ExtensionChoice> Choices)> Asked { get; } = new();

        public bool HasDialogs => true;

        public void Notify(string message, ExtensionNoticeKind kind = ExtensionNoticeKind.Info) { }

        public void SetStatus(string? text) { }

        public Task<bool> ConfirmAsync(string title, string message, bool defaultValue = false, CancellationToken cancellationToken = default)
            => Task.FromResult(defaultValue);

        public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<ExtensionChoiceResult?> ChooseAsync(
            string title,
            string? message,
            IReadOnlyList<ExtensionChoice> choices,
            CancellationToken cancellationToken = default)
        {
            Asked.Add((title, choices));
            return Task.FromResult<ExtensionChoiceResult?>(new ExtensionChoiceResult
            {
                Index = 0,
                Label = choices[0].Label,
                Description = choices[0].Description
            });
        }
    }
}
