using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Sessions;
using AiTools.Extensions;
using AiTools.Extensions.Workflows;
using AiTools.Wpf;
using AiTools.Configuration;
using AiTools.Wpf.ViewModels;
using Xunit;

namespace AiTools.Tests.Extensions;

[Collection(AiTools.Tests.Wpf.AiToolsWpfGlobalCollection.Name)]
public sealed class IntegrationTests : IDisposable
{
    private readonly TempPluginTree _tree = new();

    public IntegrationTests()
    {
        AiToolsWpf.Reset();
    }

    public void Dispose()
    {
        AiToolsWpf.Reset();
        _tree.Dispose();
    }

    [Fact]
    public void PluginLoader_LoadsDefaultManifestAndArrayWorkflowPaths()
    {
        _tree.Write(Path.Combine("default", ".claude-plugin", "plugin.json"), "{\"name\":\"default\"}");
        _tree.Write(Path.Combine("default", "workflows", "a.js"), "export const meta = { name: 'alpha' }; return 'a';");
        _tree.Write(Path.Combine("declared", ".claude-plugin", "plugin.json"), "{\"name\":\"declared\",\"workflows\":\"./scripts\"}");
        _tree.Write(Path.Combine("declared", "scripts", "b.js"), "export const meta = { name: 'beta' }; return 'b';");
        _tree.Write(Path.Combine("array", ".claude-plugin", "plugin.json"), "{\"name\":\"array\",\"workflows\":[\"./one.js\",\"./two.js\"]}");
        _tree.Write(Path.Combine("array", "one.js"), "export const meta = { name: 'one' }; return 'one';");
        _tree.Write(Path.Combine("array", "two.js"), "export const meta = { name: 'two' }; return 'two';");

        var defaultPlugin = PluginLoader.Load(Path.Combine(_tree.Root, "default"));
        Assert.Equal("default:alpha", Assert.Single(defaultPlugin.Workflows).Name);

        var declaredPlugin = PluginLoader.Load(Path.Combine(_tree.Root, "declared"));
        Assert.Equal("declared:beta", Assert.Single(declaredPlugin.Workflows).Name);

        var arrayPlugin = PluginLoader.Load(Path.Combine(_tree.Root, "array"));
        Assert.Equal(new[] { "array:one", "array:two" }, arrayPlugin.Workflows.Select(workflow => workflow.Name));
        Assert.DoesNotContain(arrayPlugin.Diagnostics, item => item.Message.Contains("workflows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PluginCatalog_AcceptsWorkflowOnlyPlugin()
    {
        var pluginRoot = Path.Combine("workflow-only", "workflows");
        _tree.Write(Path.Combine(pluginRoot, "a.js"), "export const meta = { name: 'alpha' }; return 'a';");

        var catalog = PluginCatalog.LoadFromDirectory(Path.Combine(_tree.Root, "workflow-only"));

        Assert.Equal("workflow-only:alpha", Assert.Single(catalog.Workflows).Name);
        Assert.DoesNotContain(catalog.Diagnostics, item => item.Message.Contains("no supported components", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pipeline_AggregatesAndSelectsStylesForTurnPrompt()
    {
        _tree.Write(Path.Combine("styles", ".claude-plugin", "plugin.json"), "{\"name\":\"styles\"}");
        _tree.Write(Path.Combine("styles", "output-styles", "brief.md"), "---\nname: brief\nkeep-coding-instructions: true\n---\nUse short paragraphs.");
        var catalog = PluginCatalog.LoadFromDirectory(Path.Combine(_tree.Root, "styles"));
        await using var pipeline = CreatePipeline(catalog);

        Assert.Equal("brief", Assert.Single(pipeline.OutputStyles.Styles).Name);
        Assert.False(pipeline.SelectedStyle.HasStyle);
        Assert.True(pipeline.SelectOutputStyle("brief"));
        Assert.Equal("brief", pipeline.SelectedStyle.Style?.Name);
        Assert.False(pipeline.SelectOutputStyle("brief"));

        var preparation = await pipeline.PrepareTurnAsync(
            pipeline.Session.CreateContext(null, null),
            "test-model",
            CancellationToken.None,
            "host base prompt");

        Assert.NotNull(preparation.SystemPromptOverride);
        Assert.Contains("host base prompt", preparation.SystemPromptOverride);
        Assert.Contains("## Output style: brief", preparation.SystemPromptOverride);
        Assert.Contains("Use short paragraphs.", preparation.SystemPromptOverride);
        Assert.StartsWith(preparation.SystemPromptOverride!, preparation.SystemPromptSections, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuardToolAsync_OptInPoliciesAndNoPolicyBehavior()
    {
        var noPolicy = CreatePipeline();
        await using (noPolicy)
        {
            Assert.False(noPolicy.Runtime.HasHandlers(ExtensionEvent.ToolCall));
            Assert.Null(noPolicy.GuardToolAsync("tool", "{}", () => Task.FromResult("ran"), null));
        }

        var executions = 0;
        await using (var denied = CreatePipeline(permissions: Policy("tool", PermissionDecision.Deny)))
        {
            var result = await denied.GuardToolAsync("tool", "{}", () =>
            {
                executions++;
                return Task.FromResult("ran");
            }, null)!;
            Assert.Contains("\"error\":\"denied\"", result);
            Assert.Equal(0, executions);
        }

        await using (var ask = CreatePipeline(permissions: PermissionPolicy.Default))
        {
            var result = await ask.GuardToolAsync("tool", "{}", () =>
            {
                executions++;
                return Task.FromResult("ran");
            }, null)!;
            Assert.Contains("\"error\":\"not approved\"", result);
            Assert.Equal(0, executions);
        }

        await using (var allow = CreatePipeline(permissions: Policy("tool", PermissionDecision.Allow)))
        {
            var result = await allow.GuardToolAsync("tool", "{}", () =>
            {
                executions++;
                return Task.FromResult("ran");
            }, null)!;
            Assert.Equal("ran", result);
            Assert.Equal(1, executions);
        }
    }

    [Fact]
    public async Task GuardToolAsync_AskUsesHostUiForConfirmation()
    {
        var ui = new FakeExtensionUi(approved: true);
        await using var pipeline = CreatePipeline(permissions: PermissionPolicy.Default);
        pipeline.Ui = ui;
        var preflight = await pipeline.DispatchHostToolCallAsync(
            new ToolCallEvent { ToolName = "tool", ArgumentsJson = "{}" },
            pipeline.Session.CreateContext(null, null));
        Assert.Null(preflight.BlockReason);

        var result = await pipeline.GuardToolAsync(
            "tool",
            "{}",
            () => Task.FromResult("ran"),
            prompt => ui.ConfirmAsync("Tool permission", prompt, defaultValue: false))!;
        Assert.Equal("ran", result);
        Assert.Contains("Allow tool 'tool'", Assert.Single(ui.Confirmations));
    }

    [Fact]
    public async Task PermissionBackstop_BlocksDeniedCodemodeScripts()
    {
        var executions = 0;
        var extension = ToolExtensionWithCodemode("secret", () =>
        {
            executions++;
            return "secret";
        });
        await using var pipeline = CreatePipeline(
            permissions: Policy("secret", PermissionDecision.Deny),
            extensions: new[] { extension });
        var codemode = pipeline.Runtime.TryGetTool("codemode");
        Assert.NotNull(codemode);

        var result = await codemode!.Execute(
            new ToolCall("call-1", "codemode", "{\"script\":\"await executeTool('secret', {});\"}"),
            new ToolContext { Runtime = pipeline.Runtime, State = pipeline.Runtime.State },
            CancellationToken.None);

        Assert.Contains("denied by policy", result);
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task PermissionBackstop_BlocksContextToolCalls()
    {
        var executions = 0;
        await using var pipeline = CreatePipeline(
            permissions: Policy("secret", PermissionDecision.Deny),
            extensions: new[] { ToolExtension("secret", () =>
            {
                executions++;
                return "secret";
            }, ToolExposure.Direct) });
        var context = pipeline.Session.CreateContext(null, null);

        var commandResult = await context.ExecuteToolAsync("secret", "{}");
        var toolResult = await new ToolContext
        {
            Runtime = pipeline.Runtime,
            State = pipeline.Runtime.State
        }.ExecuteToolAsync("secret", "{}");

        Assert.Contains("denied by policy", commandResult);
        Assert.Contains("denied by policy", toolResult);
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task PermissionBackstop_HidesDeniedToolsFromDeclaredLoadout()
    {
        await using var pipeline = CreatePipeline(
            permissions: Policy("secret", PermissionDecision.Deny),
            extensions: new[] { ToolExtensionWithCodemode("secret", () => "secret", ToolExposure.Deferred) });
        var toolSearch = pipeline.Runtime.TryGetTool("tool_search");
        Assert.NotNull(toolSearch);

        await toolSearch!.Execute(
            new ToolCall("search", "tool_search", "{\"query\":\"secret\"}"),
            new ToolContext { Runtime = pipeline.Runtime, State = pipeline.Runtime.State },
            CancellationToken.None);

        Assert.DoesNotContain(pipeline.Runtime.BuildLoadout().Declared, tool => tool.Definition.Name == "secret");
        Assert.DoesNotContain(pipeline.BuildToolRegistrations(), registration => registration.Definition.Name == "secret");
    }

    [Fact]
    public async Task Pipeline_ActivatesPluginWorkflowsAsDisplayCommands()
    {
        _tree.Write(Path.Combine("workflow", ".claude-plugin", "plugin.json"), "{\"name\":\"workflow\"}");
        _tree.Write(Path.Combine("workflow", "workflows", "answer.js"), "export const meta = { name: 'answer' }; return await agent('answer');");
        var catalog = PluginCatalog.LoadFromDirectory(Path.Combine(_tree.Root, "workflow"));
        await using var pipeline = CreatePipeline(
            catalog,
            workflowOptionsFactory: () => new WorkflowRuntimeOptions { Chat = new FakeChatClient() });

        var execution = await pipeline.ExecuteCommandAsync(
            "/workflow:answer",
            pipeline.Session.CreateContext(null, null));

        Assert.NotNull(execution);
        Assert.Equal(CommandExecutionKind.Display, execution!.Kind);
        Assert.Equal("workflow output", execution.DisplayText);
    }

    [Fact]
    public void WpfPipeline_RejectsUntrustedPluginDirectoryButRemainsAvailable()
    {
        WriteWorkflowPlugin("plugins");
        var projectDirectory = Path.Combine(_tree.Root, "project");
        Directory.CreateDirectory(projectDirectory);
        AiToolsWpf.Initialize(new AiToolsWpfOptions
        {
            PluginsDirectory = Path.Combine(_tree.Root, "plugins"),
            RequireProjectTrust = true,
            TrustedRoots = new[] { Path.Combine(_tree.Root, "trusted") },
            PluginHostOptions = new PluginHostOptions { ProjectDirectory = projectDirectory }
        });

        var pipeline = AiToolsWpf.Pipeline;

        Assert.NotNull(pipeline);
        Assert.Null(pipeline!.Plugins);
        Assert.NotNull(AiToolsWpf.LastTrustDecision);
        Assert.Equal(TrustLevel.Untrusted, AiToolsWpf.LastTrustDecision!.Level);
        Assert.Contains(projectDirectory, AiToolsWpf.LastTrustDecision.SkippedSources);
    }

    [Fact]
    public void WpfPipeline_LoadsPluginsUnderTrustedRoot()
    {
        WriteWorkflowPlugin("plugins");
        var projectDirectory = Path.Combine(_tree.Root, "trusted", "project");
        Directory.CreateDirectory(projectDirectory);
        AiToolsWpf.Initialize(new AiToolsWpfOptions
        {
            PluginsDirectory = Path.Combine(_tree.Root, "plugins"),
            RequireProjectTrust = true,
            TrustedRoots = new[] { Path.Combine(_tree.Root, "trusted") },
            PluginHostOptions = new PluginHostOptions { ProjectDirectory = projectDirectory }
        });

        var pipeline = AiToolsWpf.Pipeline;

        Assert.NotNull(pipeline);
        Assert.Equal(1, pipeline!.Plugins?.Count);
        Assert.Equal(TrustLevel.Trusted, AiToolsWpf.LastTrustDecision?.Level);
    }

    [Fact]
    public async Task ChatViewModel_ExecutesWorkflowDisplayCommand()
    {
        _tree.Write(Path.Combine("workflow-display", ".claude-plugin", "plugin.json"), "{\"name\":\"workflow-display\"}");
        _tree.Write(Path.Combine("workflow-display", "workflows", "answer.js"), "export const meta = { name: 'answer' }; return 'workflow displayed';");
        AiToolsWpf.Initialize(new AiToolsWpfOptions
        {
            PluginsDirectory = Path.Combine(_tree.Root, "workflow-display"),
            SettingsStore = new FixedSettingsStore(),
            ConversationStore = new InMemoryConversationStore()
        });
        var viewModel = new ChatViewModel();
        await viewModel.InitializeAsync();

        Assert.True(await viewModel.TryExecuteCommandAsync("/workflow-display:answer"));
        Assert.Equal("workflow displayed", viewModel.Messages.Last().Text);
    }

    [Fact]
    public async Task OutputStyleHostCommand_ListsSelectsAndReportsUnknownNames()
    {
        var pluginRoot = Path.Combine("styles", ".claude-plugin", "plugin.json");
        _tree.Write(pluginRoot, "{\"name\":\"styles\"}");
        _tree.Write(Path.Combine("styles", "output-styles", "brief.md"), "---\nname: brief\n---\nUse short paragraphs.");
        AiToolsWpf.Initialize(new AiToolsWpfOptions
        {
            PluginsDirectory = Path.Combine(_tree.Root, "styles"),
            SettingsStore = new FixedSettingsStore(),
            ConversationStore = new InMemoryConversationStore()
        });
        var viewModel = new ChatViewModel();
        await viewModel.InitializeAsync();

        Assert.True(await viewModel.TryInvokeBuiltInCommandAsync("/output-style"));
        Assert.Contains("brief", viewModel.Messages.Last().Text);
        Assert.Contains("当前输出风格：无", viewModel.Messages.Last().Text);

        Assert.True(await viewModel.TryInvokeBuiltInCommandAsync("/output-style brief"));
        Assert.Equal("brief", viewModel.Pipeline?.SelectedStyle.Style?.Name);
        Assert.Contains("已选择输出风格：brief", viewModel.Messages.Last().Text);

        Assert.True(await viewModel.TryInvokeBuiltInCommandAsync("/output-style missing"));
        Assert.Contains("未知输出风格：missing", viewModel.Messages.Last().Text);
        Assert.Equal("brief", viewModel.Pipeline?.SelectedStyle.Style?.Name);
    }

    private void WriteWorkflowPlugin(string directory)
    {
        _tree.Write(Path.Combine(directory, "workflow", ".claude-plugin", "plugin.json"), "{\"name\":\"workflow\"}");
        _tree.Write(Path.Combine(directory, "workflow", "workflows", "a.js"), "export const meta = { name: 'alpha' }; return 'a';");
    }

    private static ExtensionPipeline CreatePipeline(
        PluginCatalog? plugins = null,
        PermissionPolicy? permissions = null,
        Func<WorkflowRuntimeOptions>? workflowOptionsFactory = null,
        IReadOnlyList<IExtension>? extensions = null)
        => new(new ExtensionPipelineOptions
        {
            Extensions = extensions ?? Array.Empty<IExtension>(),
            Plugins = plugins,
            Permissions = permissions,
            WorkflowOptionsFactory = workflowOptionsFactory,
            WorkingDirectory = Environment.CurrentDirectory
        });

    private static PermissionPolicy Policy(string tool, PermissionDecision decision)
        => new(new[] { new PermissionRule { Tool = tool, Decision = decision } });

    private static IExtension ToolExtension(string name, Func<string> execute, ToolExposure exposure)
        => new TestExtension("test-tools", api => api.RegisterTool(
            ToolDefinition.Create(name),
            (_, _, _) => Task.FromResult(execute()),
            exposure));

    private static IExtension ToolExtensionWithCodemode(
        string name,
        Func<string> execute,
        ToolExposure targetExposure = ToolExposure.Direct)
        => new TestExtension("test-tools", api =>
        {
            api.RegisterTool(
                ToolDefinition.Create(name),
                (_, _, _) => Task.FromResult(execute()),
                targetExposure);
            api.RegisterTool(
                ToolDefinition.Create("codemode_bridge"),
                (_, _, _) => Task.FromResult(string.Empty),
                ToolExposure.Codemode);
        });

    private sealed class TestExtension : IExtension
    {
        private readonly Action<IExtensionApi> _activate;

        public TestExtension(string name, Action<IExtensionApi> activate)
        {
            Name = name;
            _activate = activate;
        }

        public string Name { get; }

        public string? Description => null;

        public void Activate(IExtensionApi api) => _activate(api);
    }

    private sealed class FakeExtensionUi : IExtensionUi
    {
        private readonly bool _approved;

        public FakeExtensionUi(bool approved) => _approved = approved;

        public List<string> Confirmations { get; } = new();

        public bool HasDialogs => true;

        public void Notify(string message, ExtensionNoticeKind kind = ExtensionNoticeKind.Info) { }

        public void SetStatus(string? text) { }

        public Task<bool> ConfirmAsync(string title, string message, bool defaultValue = false, CancellationToken cancellationToken = default)
        {
            Confirmations.Add(message);
            return Task.FromResult(_approved);
        }

        public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeChatClient : IChatClient
    {
        public string ProviderName => "integration-test";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse
            {
                Message = ChatMessage.Assistant("workflow output"),
                FinishReason = ChatFinishReason.Stop
            });

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatStreamUpdate { TextDelta = "workflow output", FinishReason = ChatFinishReason.Stop };
        }
    }

    private sealed class FixedSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppSettings { Providers = new List<ProviderConfig>() });

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
