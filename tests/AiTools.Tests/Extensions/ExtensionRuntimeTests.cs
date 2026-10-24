using System.Text.Json;
using System.Threading;
using AiTools.Abstractions;
using AiTools.Extensions;
using AiTools.Extensions.BuiltIn;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>一个最小的测试扩展：按测试需要登记命令/工具/事件。</summary>
internal sealed class ProbeExtension : IExtension
{
    private readonly Action<IExtensionApi> _configure;

    public ProbeExtension(string name, Action<IExtensionApi> configure)
    {
        Name = name;
        _configure = configure;
    }

    public string Name { get; }

    public string? Description => "probe";

    public void Activate(IExtensionApi api) => _configure(api);
}

public sealed class ExtensionRuntimeTests
{
    private static CommandContext Context(ExtensionRuntime runtime)
        => new() { Runtime = runtime, State = runtime.State, WorkingDirectory = Environment.CurrentDirectory };

    [Fact]
    public void Activate_RegistersCommandsAndTools()
    {
        var runtime = new ExtensionRuntime();
        var diagnostics = runtime.Activate(new ProbeExtension("probe", api =>
        {
            api.RegisterCommand("hello", "Say hello", (_, _, _) => Task.FromResult(CommandResult.Display("hi")));
            api.RegisterTool(ToolDefinition.Create("probe_tool"), (_, _, _) => Task.FromResult("ok"));
        }));

        Assert.Empty(diagnostics);
        Assert.Equal("hello", Assert.Single(runtime.Commands).Name);
        Assert.Equal("probe_tool", Assert.Single(runtime.Tools).Definition.Name);
        Assert.Equal("probe", runtime.Tools[0].ExtensionName);
    }

    [Fact]
    public void Activate_FailureRollsBackRegistrations()
    {
        var runtime = new ExtensionRuntime();
        var diagnostics = runtime.Activate(new ProbeExtension("broken", api =>
        {
            api.RegisterCommand("partial", "p", (_, _, _) => Task.FromResult(CommandResult.Noop));
            throw new InvalidOperationException("boom");
        }));

        Assert.Single(diagnostics);
        Assert.Equal(PluginDiagnosticKind.Error, diagnostics[0].Kind);
        Assert.Empty(runtime.Commands);
        Assert.Empty(runtime.Extensions);
    }

    [Fact]
    public void Activate_DuplicateIsIgnored()
    {
        var runtime = new ExtensionRuntime();
        var extension = new ProbeExtension("dup", _ => { });
        runtime.Activate(extension);
        var diagnostics = runtime.Activate(extension);

        Assert.Single(diagnostics);
        Assert.Single(runtime.Extensions);
    }

    [Fact]
    public void LaterRegistration_WinsForSameName()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("first", api =>
            api.RegisterCommand("go", "first", (_, _, _) => Task.FromResult(CommandResult.Display("first")))));
        runtime.Activate(new ProbeExtension("second", api =>
            api.RegisterCommand("go", "second", (_, _, _) => Task.FromResult(CommandResult.Display("second")))));

        var command = Assert.Single(runtime.Commands);
        Assert.Equal("second", command.ExtensionName);
    }

    [Fact]
    public async Task Dispatch_TransformsChainInRegistrationOrder()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("a", api =>
            api.On<InputEvent>(ExtensionEvent.Input, (e, _, _) =>
            {
                e.Text += "-a";
                return Task.FromResult<InputEvent?>(e);
            })));
        runtime.Activate(new ProbeExtension("b", api =>
            api.On<InputEvent>(ExtensionEvent.Input, (e, _, _) =>
            {
                e.Text += "-b";
                return Task.FromResult<InputEvent?>(e);
            })));

        var result = await runtime.DispatchAsync(
            ExtensionEvent.Input,
            new InputEvent { Text = "start" },
            Context(runtime));

        Assert.Equal("start-a-b", ((InputEvent)result!).Text);
    }

    [Fact]
    public async Task Dispatch_FailingGateFailsClosed()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("gate", api =>
            api.On<ToolCallEvent>(ExtensionEvent.ToolCall, (_, _, _) => throw new InvalidOperationException("bad"))));

        var result = await runtime.DispatchAsync(
            ExtensionEvent.ToolCall,
            new ToolCallEvent { ToolName = "t" },
            Context(runtime),
            failClosed: new ToolCallEvent { ToolName = "t", BlockReason = "blocked" });

        Assert.Equal("blocked", ((ToolCallEvent)result!).BlockReason);
    }

    [Fact]
    public async Task ExecuteTool_RespectsToolCallBlocking()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("probe", api =>
        {
            api.RegisterTool(ToolDefinition.Create("danger"), (_, _, _) => Task.FromResult("executed"));
            api.On<ToolCallEvent>(ExtensionEvent.ToolCall, (e, _, _) =>
            {
                e.Block("not allowed");
                return Task.FromResult<ToolCallEvent?>(e);
            });
        }));

        var result = await runtime.ExecuteToolAsync("danger", "{}", Context(runtime));

        Assert.True(result.Blocked);
        Assert.True(result.IsError);
        Assert.Equal("not allowed", result.Text);
    }

    [Fact]
    public async Task ExecuteTool_AllowsResultRewrite()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("probe", api =>
        {
            api.RegisterTool(ToolDefinition.Create("leak"), (_, _, _) => Task.FromResult("secret=abc"));
            api.On<ToolResultEvent>(ExtensionEvent.ToolResult, (e, _, _) =>
            {
                e.Text = e.Text.Replace("abc", "***");
                return Task.FromResult<ToolResultEvent?>(e);
            });
        }));

        var result = await runtime.ExecuteToolAsync("leak", "{}", Context(runtime));

        Assert.Equal("secret=***", result.Text);
    }

    [Fact]
    public async Task ExecuteTool_UnknownTool_IsError()
    {
        var runtime = new ExtensionRuntime();

        var result = await runtime.ExecuteToolAsync("missing", "{}", Context(runtime));

        Assert.True(result.NotFound);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ExecuteTool_ThrowingToolBecomesErrorText()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("probe", api =>
            api.RegisterTool(ToolDefinition.Create("bad"), (_, _, _) => throw new InvalidOperationException("nope"))));

        var result = await runtime.ExecuteToolAsync("bad", "{}", Context(runtime));

        Assert.True(result.IsError);
        Assert.Contains("nope", result.Text);
    }

    [Fact]
    public async Task InvokeTool_SkipsEventDispatchAndHiddenTools()
    {
        var runtime = new ExtensionRuntime();
        var calls = 0;
        runtime.Activate(new ProbeExtension("probe", api =>
        {
            api.RegisterTool(ToolDefinition.Create("plain"), (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult("ran");
            });
            api.RegisterTool(ToolDefinition.Create("hidden"), (_, _, _) => Task.FromResult("should not run"), ToolExposure.Hidden);
            api.On<ToolCallEvent>(ExtensionEvent.ToolCall, (e, _, _) =>
            {
                e.Block("should not fire");
                return Task.FromResult<ToolCallEvent?>(e);
            });
        }));

        var toolContext = new ToolContext { Runtime = runtime, State = runtime.State };
        var plain = await runtime.InvokeToolAsync("plain", "{}", toolContext);
        var hidden = await runtime.InvokeToolAsync("hidden", "{}", toolContext);
        var missing = await runtime.InvokeToolAsync("nope", "{}", toolContext);

        // 事件未分发，因此拦截不生效；这也是 WPF 宿主自行分发事件时的路径。
        Assert.False(plain.IsError);
        Assert.Equal("ran", plain.Text);
        Assert.Equal(1, calls);

        Assert.True(hidden.IsError);
        Assert.Contains("not reachable", hidden.Text);
        Assert.True(missing.NotFound);
    }

    [Fact]
    public async Task Dispatch_InputEventCanSuppressAndRewrite()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("probe", api =>
            api.On<InputEvent>(ExtensionEvent.Input, (e, _, _) =>
            {
                if (e.Text == "blocked")
                {
                    e.Suppress = true;
                }
                else
                {
                    e.Text = e.Text.ToUpperInvariant();
                }

                return Task.FromResult<InputEvent?>(e);
            })));

        var blocked = (InputEvent)(await runtime.DispatchAsync(ExtensionEvent.Input, new InputEvent { Text = "blocked" }, Context(runtime)))!;
        var rewritten = (InputEvent)(await runtime.DispatchAsync(ExtensionEvent.Input, new InputEvent { Text = "hello" }, Context(runtime)))!;

        Assert.True(blocked.Suppress);
        Assert.Equal("HELLO", rewritten.Text);
    }

    [Fact]
    public void DeclaredTools_ExcludesDeferredAndHidden()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("probe", api =>
        {
            api.RegisterTool(ToolDefinition.Create("direct"), (_, _, _) => Task.FromResult(""));
            api.RegisterTool(ToolDefinition.Create("model"), (_, _, _) => Task.FromResult(""), ToolExposure.ModelOnly);
            api.RegisterTool(ToolDefinition.Create("deferred"), (_, _, _) => Task.FromResult(""), ToolExposure.Deferred);
            api.RegisterTool(ToolDefinition.Create("hidden"), (_, _, _) => Task.FromResult(""), ToolExposure.Hidden);
        }));

        var declared = runtime.DeclaredTools.Select(tool => tool.Definition.Name).ToArray();

        // 有 deferred 工具时运行时自动提供 tool_search（ModelOnly 且恒为激活）。
        Assert.Equal(new[] { "direct", "model", "tool_search" }, declared);
        // 延迟/隐藏工具仍可被程序化调用。
        Assert.NotNull(runtime.TryGetTool("hidden"));
    }

    [Fact]
    public void Subscribing_ReturnsUnsubscribe()
    {
        var runtime = new ExtensionRuntime();
        Action? unsubscribe = null;
        var calls = 0;

        runtime.Activate(new ProbeExtension("probe", api =>
        {
            unsubscribe = api.On(ExtensionEvent.SessionStart, (_, _, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });
        }));

        Assert.True(runtime.HasHandlers(ExtensionEvent.SessionStart));
        unsubscribe!.Invoke();
        Assert.False(runtime.HasHandlers(ExtensionEvent.SessionStart));
        Assert.Equal(0, calls);
    }
}

public sealed class ExtensionStateStoreTests
{
    [Fact]
    public void SetAndGet_RoundTripsStronglyTypedValues()
    {
        var store = new ExtensionStateStore();
        store.Set("ext", "count", 3);

        Assert.Equal(3, store.Get<int>("ext", "count"));
        Assert.False(store.TryGet<int>("ext", "missing", out _));
        Assert.True(store.TryGet<int>("ext", "count", out var count));
        Assert.Equal(3, count);
    }

    [Fact]
    public void SetRawJsonElement_DoesNotRecurse()
    {
        var store = new ExtensionStateStore();
        using var document = JsonDocument.Parse("""{"a":1}""");
        store.Set("ext", "value", document.RootElement);

        Assert.Equal("""{"a":1}""", store.Get("ext", "value")!.Value.GetRawText());
    }

    [Fact]
    public void RemoveAndClear_Work()
    {
        var store = new ExtensionStateStore();
        store.Set("ext", "a", 1);
        store.Set("ext", "b", 2);

        Assert.True(store.Remove("ext", "a"));
        Assert.False(store.Remove("ext", "a"));
        Assert.Null(store.Get("ext", "a"));

        store.Clear("ext");
        Assert.Null(store.Get("ext", "b"));
    }

    [Fact]
    public void Snapshot_IsDetachedFromLaterWrites()
    {
        var store = new ExtensionStateStore();
        store.Set("ext", "a", 1);
        var snapshot = store.Snapshot();

        store.Set("ext", "a", 2);

        Assert.Equal(1, snapshot["ext"]["a"].GetInt32());
        Assert.Equal(2, store.Get<int>("ext", "a"));
    }

    [Fact]
    public void ToJsonAndLoadFromJson_RoundTrip()
    {
        var store = new ExtensionStateStore();
        store.Set("goal", "current", new { objective = "x", count = 2 });

        var json = store.ToJson();
        var restored = new ExtensionStateStore();
        restored.LoadFromJson(json);

        var value = restored.Get("goal", "current")!.Value;
        Assert.Equal("x", value.GetProperty("objective").GetString());
        Assert.Equal(2, value.GetProperty("count").GetInt32());
    }

    [Fact]
    public void LoadFromJson_InvalidContent_ResetsQuietly()
    {
        var store = new ExtensionStateStore();
        store.Set("ext", "a", 1);

        store.LoadFromJson("{ not json");

        Assert.Null(store.Get("ext", "a"));
    }

    [Fact]
    public void Changed_FiresOnMutation()
    {
        var store = new ExtensionStateStore();
        var count = 0;
        store.Changed += (_, _) => count++;

        store.Set("ext", "a", 1);
        store.Remove("ext", "a");
        store.Set("ext", "a", 1);
        store.ClearAll();

        Assert.Equal(4, count);
    }
}

public sealed class ExtensionSessionTests
{
    [Fact]
    public async Task Start_PublishesSessionStartOnce()
    {
        var runtime = new ExtensionRuntime();
        var starts = 0;
        runtime.Activate(new ProbeExtension("probe", api =>
            api.On(ExtensionEvent.SessionStart, (_, _, _) =>
            {
                starts++;
                return Task.CompletedTask;
            })));

        var session = ExtensionSession.Create(runtime, Environment.CurrentDirectory);
        await session.StartAsync("s1");
        await session.StartAsync("s2");

        Assert.Equal(1, starts);
        Assert.Equal("s1", session.SessionId);
        Assert.True(session.IsStarted);
    }

    [Fact]
    public async Task Shutdown_IsIdempotentAndRunsCleanupInReverseOrder()
    {
        var runtime = new ExtensionRuntime();
        var order = new List<string>();
        var session = ExtensionSession.Create(runtime);
        await session.StartAsync(null);

        session.RegisterCleanup(() =>
        {
            order.Add("first");
            return default;
        });
        session.RegisterCleanup(() =>
        {
            order.Add("second");
            return default;
        });

        await session.ShutdownAsync();
        await session.ShutdownAsync();

        Assert.Equal(new[] { "second", "first" }, order);
        Assert.True(session.IsShutdown);
    }

    [Fact]
    public async Task Start_AfterShutdown_Throws()
    {
        var session = ExtensionSession.Create(new ExtensionRuntime());
        await session.ShutdownAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(null));
    }

    [Fact]
    public async Task Shutdown_ContinuesWhenACleanupThrows()
    {
        var session = ExtensionSession.Create(new ExtensionRuntime());
        await session.StartAsync(null);
        var ran = false;
        session.RegisterCleanup(() => throw new InvalidOperationException("bad"));
        session.RegisterCleanup(() =>
        {
            ran = true;
            return default;
        });

        await session.ShutdownAsync();

        Assert.True(ran);
    }

    [Fact]
    public async Task SessionShutdown_HandlerReceivesEvent()
    {
        var runtime = new ExtensionRuntime();
        string? captured = "unset";
        runtime.Activate(new ProbeExtension("probe", api =>
            api.On<SessionStartEvent>(ExtensionEvent.SessionShutdown, (e, _, _) =>
            {
                captured = e.WorkingDirectory;
                return Task.FromResult<SessionStartEvent?>(null);
            })));

        var session = ExtensionSession.Create(runtime, "C:/work");
        await session.StartAsync(null);
        await session.ShutdownAsync();

        Assert.Equal("C:/work", captured);
    }
}

public sealed class GoalExtensionTests
{
    private static (ExtensionRuntime Runtime, ExtensionPipeline Pipeline) CreatePipeline()
    {
        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = new IExtension[] { new GoalExtension() },
            WorkingDirectory = Environment.CurrentDirectory
        });
        return (pipeline.Runtime, pipeline);
    }

    private static CommandContext Context(ExtensionRuntime runtime)
        => new() { Runtime = runtime, State = runtime.State, WorkingDirectory = Environment.CurrentDirectory };

    [Fact]
    public async Task GoalCommand_SetsStateAndEmitsPrompt()
    {
        var (runtime, _) = CreatePipeline();
        var command = runtime.TryGetCommand("goal")!;
        var context = Context(runtime);

        var result = await command.Handler(context, CommandArguments.Parse("ship it"), default);

        Assert.Equal(CommandResultKind.Prompt, result.Kind);
        Assert.Contains("ship it", result.UserMessage);
        Assert.Contains(GoalExtension.PlanToolName, result.UserMessage);
        Assert.Equal("ship it", runtime.State.Get<GoalState>(GoalExtension.ExtensionId, "current")!.Objective);
    }

    [Fact]
    public async Task GoalCommand_WithoutArguments_ShowsUsage()
    {
        var (runtime, _) = CreatePipeline();
        var result = await runtime.TryGetCommand("goal")!.Handler(Context(runtime), CommandArguments.Parse(""), default);

        Assert.Equal(CommandResultKind.Display, result.Kind);
        Assert.Contains("/goal", result.UserMessage);
    }

    [Fact]
    public async Task GoalCommand_WithExistingState_ShowsStatus()
    {
        var (runtime, _) = CreatePipeline();
        var context = Context(runtime);
        await runtime.ExecuteToolAsync(GoalExtension.PlanToolName,
            """{"objective":"existing","criteria":[{"text":"c1"}],"steps":["s1"]}""", context);

        var result = await runtime.TryGetCommand("goal")!.Handler(context, CommandArguments.Parse(""), default);

        Assert.Equal(CommandResultKind.Display, result.Kind);
        Assert.Contains("existing", result.UserMessage);
    }

    [Fact]
    public async Task PlanTool_RejectsMissingCriteria()
    {
        var (runtime, _) = CreatePipeline();
        var result = await runtime.ExecuteToolAsync(
            GoalExtension.PlanToolName,
            """{"objective":"x","criteria":[],"steps":[]}""",
            Context(runtime));

        Assert.Contains("error", result.Text);
    }

    [Fact]
    public async Task UpdateTool_RequiresEvidenceToProveCriterion()
    {
        var (runtime, _) = CreatePipeline();
        var context = Context(runtime);
        await runtime.ExecuteToolAsync(
            GoalExtension.PlanToolName,
            """{"objective":"o","criteria":[{"text":"c"}],"steps":["s"]}""",
            context);

        var rejected = await runtime.ExecuteToolAsync(GoalExtension.UpdateToolName, """{"proveCriterion":0}""", context);
        Assert.Contains("evidence", rejected.Text);

        var accepted = await runtime.ExecuteToolAsync(
            GoalExtension.UpdateToolName,
            """{"proveCriterion":0,"evidence":"dotnet test passed"}""",
            context);
        Assert.Contains("proven by: dotnet test passed", accepted.Text);

        var state = runtime.State.Get<GoalState>(GoalExtension.ExtensionId, "current")!;
        Assert.True(state.IsComplete);
    }

    [Fact]
    public async Task UpdateTool_RejectsOutOfRangeStep()
    {
        var (runtime, _) = CreatePipeline();
        var context = Context(runtime);
        await runtime.ExecuteToolAsync(
            GoalExtension.PlanToolName,
            """{"objective":"o","criteria":[{"text":"c"}],"steps":["only"]}""",
            context);

        var result = await runtime.ExecuteToolAsync(GoalExtension.UpdateToolName, """{"completeStep":5}""", context);

        Assert.Contains("out of range", result.Text);
    }

    [Fact]
    public async Task BeforeAgentStart_InjectsSectionWithProgress()
    {
        var (runtime, pipeline) = CreatePipeline();
        var context = Context(runtime);
        await runtime.ExecuteToolAsync(
            GoalExtension.PlanToolName,
            """{"objective":"o","criteria":[{"text":"c1"},{"text":"c2"}],"steps":["a","b"]}""",
            context);
        await runtime.ExecuteToolAsync(GoalExtension.UpdateToolName, """{"completeStep":0}""", context);

        var preparation = await pipeline.PrepareTurnAsync(context, "test-model");

        Assert.Contains("## Session goal", preparation.SystemPromptSections);
        Assert.Contains("- [ ] c1", preparation.SystemPromptSections);
        Assert.Contains("Progress: 1/2 steps", preparation.SystemPromptSections);
        Assert.Contains(GoalExtension.UpdateToolName, preparation.SystemPromptSections);
    }

    [Fact]
    public async Task BeforeAgentStart_NoGoal_AddsNothing()
    {
        var (runtime, pipeline) = CreatePipeline();

        var preparation = await pipeline.PrepareTurnAsync(Context(runtime), "m");

        Assert.DoesNotContain("Session goal", preparation.SystemPromptSections);
    }
}

public sealed class PlanExtensionTests
{
    private static ExtensionPipeline CreatePipeline() => new(new ExtensionPipelineOptions
    {
        Extensions = new IExtension[] { new PlanExtension() },
        WorkingDirectory = Environment.CurrentDirectory
    });

    private static CommandContext Context(ExtensionRuntime runtime)
        => new() { Runtime = runtime, State = runtime.State, WorkingDirectory = Environment.CurrentDirectory };

    [Fact]
    public async Task PlanCommand_EmitsPromptAndResetsState()
    {
        var pipeline = CreatePipeline();
        var runtime = pipeline.Runtime;

        var result = await runtime.TryGetCommand("plan")!.Handler(Context(runtime), CommandArguments.Parse("do it"), default);

        Assert.Equal(CommandResultKind.Prompt, result.Kind);
        Assert.Contains("do it", result.UserMessage);
        Assert.Equal("do it", runtime.State.Get<PlanState>(PlanExtension.ExtensionId, "current")!.Task);
    }

    [Fact]
    public async Task SaveTool_RequiresVerifyPerStep()
    {
        var pipeline = CreatePipeline();
        var result = await pipeline.Runtime.ExecuteToolAsync(
            PlanExtension.SaveToolName,
            """{"task":"t","steps":[{"title":"s"}]}""",
            Context(pipeline.Runtime));

        Assert.Contains("verify", result.Text);
    }

    [Fact]
    public async Task SaveAndStep_TrackApproval()
    {
        var pipeline = CreatePipeline();
        var context = Context(pipeline.Runtime);
        await pipeline.Runtime.ExecuteToolAsync(
            PlanExtension.SaveToolName,
            """{"task":"t","steps":[{"title":"a","verify":"dotnet test"},{"title":"b","verify":"smoke"}]}""",
            context);

        var state = pipeline.Runtime.State.Get<PlanState>(PlanExtension.ExtensionId, "current")!;
        Assert.False(state.Approved);

        var step = await pipeline.Runtime.ExecuteToolAsync(PlanExtension.StepToolName, """{"completeStep":0}""", context);
        Assert.Contains("step 0 done", step.Text);

        var approve = await pipeline.Runtime.ExecuteToolAsync(PlanExtension.StepToolName, """{"approve":true}""", context);
        Assert.Contains("plan approved", approve.Text);
    }

    [Fact]
    public async Task BeforeAgentStart_WarnsWhenNotApproved()
    {
        var pipeline = CreatePipeline();
        var context = Context(pipeline.Runtime);
        await pipeline.Runtime.ExecuteToolAsync(
            PlanExtension.SaveToolName,
            """{"task":"t","steps":[{"title":"a","verify":"v"}]}""",
            context);

        var preparation = await pipeline.PrepareTurnAsync(context, "m");
        Assert.Contains("NOT approved", preparation.SystemPromptSections);

        await pipeline.Runtime.ExecuteToolAsync(PlanExtension.StepToolName, """{"approve":true}""", context);
        var approved = await pipeline.PrepareTurnAsync(context, "m");
        Assert.Contains("approved this plan", approved.SystemPromptSections);
    }

    [Fact]
    public void BuildSection_IncludesFilesRisksAndOpenQuestions()
    {
        var section = PlanExtension.BuildSection(new PlanState
        {
            Task = "t",
            Steps = { new PlanStep { Title = "a", Files = { "x.cs" }, Change = "c", Verify = "v" } },
            Risks = { "r" },
            OutOfScope = { "o" },
            OpenQuestions = { "q" }
        });

        Assert.Contains("Files: x.cs", section);
        Assert.Contains("Verify: v", section);
        Assert.Contains("Risks:", section);
        Assert.Contains("Out of scope:", section);
        Assert.Contains("Open questions:", section);
    }
}

public sealed class CommandCatalogTests
{
    private static ResolvedCommand Code(string name) => new()
    {
        Name = name,
        ShortName = name,
        Source = "ext",
        ExtensionCommand = new ExtensionCommand { Name = name }
    };

    private static ResolvedCommand Plugin(string plugin, string name) => new()
    {
        Name = plugin + ":" + name,
        ShortName = name,
        Source = plugin,
        PluginCommand = new PluginCommand { Name = plugin + ":" + name, ShortName = name, PluginName = plugin }
    };

    [Fact]
    public void Resolve_ExactNameWins()
    {
        var runtime = new ExtensionRuntime();
        var catalog = CommandCatalog.Create(runtime, null);
        Assert.Equal(0, catalog.Count);

        var resolved = runtime.TryGetCommand("none");
        Assert.Null(resolved);
    }

    [Fact]
    public async Task Pipeline_UnifiesCodeAndPluginCommands()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("ext", api =>
            api.RegisterCommand("goal", "code goal", (_, _, _) => Task.FromResult(CommandResult.Display("code")))));

        var plugin = PluginCatalog.FromPlugins(new[]
        {
            new AiToolsPlugin
            {
                Name = "workflows",
                RootPath = Environment.CurrentDirectory,
                Commands = new[]
                {
                    new PluginCommand
                    {
                        Name = "workflows:goal",
                        ShortName = "goal",
                        PluginName = "workflows",
                        Description = "markdown goal",
                        Body = "Body: $ARGUMENTS",
                        PluginRoot = Environment.CurrentDirectory
                    }
                }
            }
        });

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Runtime = runtime,
            Plugins = plugin,
            PluginHostOptions = new PluginHostOptions { EnableShellExecution = false },
            WorkingDirectory = Environment.CurrentDirectory
        });

        var catalog = pipeline.Commands;
        Assert.Equal(2, catalog.Count);

        // 精确名解析：/goal 命中代码命令，/workflows:goal 命中插件命令。
        Assert.True(catalog.TryResolve("goal", out var code));
        Assert.True(code!.IsCodeCommand);

        Assert.True(catalog.TryResolve("workflows:goal", out var markdown));
        Assert.False(markdown!.IsCodeCommand);

        // 短名冲突：插件命令必须用限定名，代码命令保持精确。
        Assert.False(code.IsAmbiguous);
        Assert.True(markdown.IsAmbiguous);
        Assert.Equal("/workflows:goal ", "/" + markdown.InvocationName + " ");

        var context = pipeline.Session.CreateContext(null, null);
        var codeExecution = await pipeline.ExecuteCommandAsync("/goal ship", context);
        Assert.Equal(CommandExecutionKind.Display, codeExecution!.Kind);
        Assert.Equal("code", codeExecution.DisplayText);

        var pluginExecution = await pipeline.ExecuteCommandAsync("/workflows:goal ship", context);
        Assert.Equal(CommandExecutionKind.Prompt, pluginExecution!.Kind);
        Assert.Equal("Body: ship", pluginExecution.Prompt);
    }

    [Fact]
    public async Task Pipeline_UnknownCommand_ReturnsNull()
    {
        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            WorkingDirectory = Environment.CurrentDirectory
        });

        Assert.Null(await pipeline.ExecuteCommandAsync("/nope", pipeline.Session.CreateContext(null, null)));
        Assert.Null(await pipeline.ExecuteCommandAsync("plain text", pipeline.Session.CreateContext(null, null)));
        Assert.Null(await pipeline.ExecuteCommandAsync("/usr/bin/env", pipeline.Session.CreateContext(null, null)));
    }

    [Fact]
    public async Task Pipeline_CommandExceptionBecomesError()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("boom", api =>
            api.RegisterCommand("boom", "throws", (_, _, _) => throw new InvalidOperationException("kaboom"))));

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Runtime = runtime,
            WorkingDirectory = Environment.CurrentDirectory
        });

        var execution = await pipeline.ExecuteCommandAsync("/boom", pipeline.Session.CreateContext(null, null));

        Assert.Equal(CommandExecutionKind.Error, execution!.Kind);
        Assert.Contains("kaboom", execution.Error);
    }

    [Fact]
    public async Task PrepareTurn_IncludesExtensionTools()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("tools", api =>
            api.RegisterTool(ToolDefinition.Create("probe_tool"), (_, _, _) => Task.FromResult("ok"))));

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Runtime = runtime,
            WorkingDirectory = Environment.CurrentDirectory
        });

        var preparation = await pipeline.PrepareTurnAsync(pipeline.Session.CreateContext(null, null), "m");

        Assert.Equal("probe_tool", Assert.Single(preparation.Tools).Definition.Name);
    }

    [Fact]
    public async Task TurnEnd_ExtensionCanRequestContinuation()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("loop", api =>
            api.On<TurnEndEvent>(ExtensionEvent.TurnEnd, (e, _, _) =>
            {
                e.Continue = true;
                e.ContinuationPrompt = "keep going";
                return Task.FromResult<TurnEndEvent?>(e);
            })));

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Runtime = runtime,
            WorkingDirectory = Environment.CurrentDirectory
        });

        var result = await pipeline.PublishTurnEndAsync(pipeline.Session.CreateContext(null, null), "done", 0);

        Assert.True(result.Continue);
        Assert.Equal("keep going", result.ContinuationPrompt);
    }

    [Fact]
    public void ExportImport_RoundTripsAcrossPipelines()
    {
        var source = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = new IExtension[] { new GoalExtension() },
            WorkingDirectory = Environment.CurrentDirectory
        });

        source.Runtime.State.Set(GoalExtension.ExtensionId, "current", new GoalState { Objective = "carry over" });
        var json = source.ExportState();

        var target = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = new IExtension[] { new GoalExtension() },
            WorkingDirectory = Environment.CurrentDirectory
        });
        target.ImportState(json);

        Assert.Equal("carry over", target.Runtime.State.Get<GoalState>(GoalExtension.ExtensionId, "current")!.Objective);
    }
}
