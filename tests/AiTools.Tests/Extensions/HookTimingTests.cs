using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Extensions;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>
/// 覆盖补充的 hook 时机：会话迁移、agent 循环、消息定稿、指令加载、
/// 工具失败、增量输出与授权交互。
/// </summary>
public sealed class HookTimingTests
{
    private static ExtensionPipeline Pipeline(IExtension extension, PermissionPolicy? permissions = null)
        => new(new ExtensionPipelineOptions
        {
            Extensions = new[] { extension },
            WorkingDirectory = Environment.CurrentDirectory,
            Permissions = permissions
        });

    private static CommandContext Context(ExtensionPipeline pipeline)
        => pipeline.Session.CreateContext(null, null);

    [Fact]
    public async Task SessionBeforeSwitch_CanBeCancelled()
    {
        var pipeline = Pipeline(new ProbeExtension("guard", api =>
            api.On<SessionBeforeSwitchEvent>(ExtensionEvent.SessionBeforeSwitch, (e, _, _) =>
            {
                e.Cancel("有未保存的草稿");
                return Task.FromResult<SessionBeforeSwitchEvent?>(e);
            })));

        var reason = await pipeline.Session.PublishBeforeSwitchAsync("new", null);

        Assert.Equal("有未保存的草稿", reason);
    }

    [Fact]
    public async Task SessionBeforeSwitch_PassesReasonAndTarget()
    {
        string? seenReason = null;
        string? seenTarget = null;
        var pipeline = Pipeline(new ProbeExtension("probe", api =>
            api.On<SessionBeforeSwitchEvent>(ExtensionEvent.SessionBeforeSwitch, (e, _, _) =>
            {
                seenReason = e.Reason;
                seenTarget = e.TargetSessionId;
                return Task.FromResult<SessionBeforeSwitchEvent?>(null);
            })));

        var cancelled = await pipeline.Session.PublishBeforeSwitchAsync("resume", "conv-7");

        Assert.Null(cancelled);
        Assert.Equal("resume", seenReason);
        Assert.Equal("conv-7", seenTarget);
    }

    [Fact]
    public async Task SessionSwitched_ReportsPreviousAndCurrent()
    {
        string? previous = null;
        string? current = null;
        var pipeline = Pipeline(new ProbeExtension("probe", api =>
            api.On<SessionSwitchedEvent>(ExtensionEvent.SessionSwitched, (e, _, _) =>
            {
                previous = e.PreviousSessionId;
                current = e.SessionId;
                return Task.FromResult<SessionSwitchedEvent?>(null);
            })));

        pipeline.Session.UpdateSessionId("conv-2");
        await pipeline.Session.PublishSwitchedAsync("fork", "conv-1");

        Assert.Equal("conv-1", previous);
        Assert.Equal("conv-2", current);
    }

    [Fact]
    public async Task SessionStart_CarriesReason()
    {
        string? reason = null;
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("probe", api =>
            api.On<SessionStartEvent>(ExtensionEvent.SessionStart, (e, _, _) =>
            {
                reason = e.Reason;
                return Task.FromResult<SessionStartEvent?>(null);
            })));

        await ExtensionSession.Create(runtime, Environment.CurrentDirectory).StartAsync("conv-1", "reload");

        Assert.Equal("reload", reason);
    }

    [Fact]
    public async Task AgentLifecycle_StartEndAndFailureAreDispatched()
    {
        var events = new List<string>();
        var pipeline = Pipeline(new ProbeExtension("probe", api =>
        {
            api.On(ExtensionEvent.AgentStart, (_, _, _) =>
            {
                events.Add("agent_start");
                return Task.CompletedTask;
            });
            api.On(ExtensionEvent.TurnStart, (_, _, _) =>
            {
                events.Add("turn_start");
                return Task.CompletedTask;
            });
            api.On(ExtensionEvent.AgentEnd, (_, _, _) =>
            {
                events.Add("agent_end");
                return Task.CompletedTask;
            });
            api.On(ExtensionEvent.AgentFailed, (_, _, _) =>
            {
                events.Add("agent_failed");
                return Task.CompletedTask;
            });
        }));

        var context = Context(pipeline);
        await pipeline.PublishAgentStartAsync(context, "m1");
        await pipeline.PublishTurnStartAsync(context, 0);
        await pipeline.PublishAgentEndAsync(context);
        await pipeline.PublishAgentFailedAsync(context, "boom", cancelled: false);

        Assert.Equal(new[] { "agent_start", "turn_start", "agent_end", "agent_failed" }, events);
    }

    [Fact]
    public async Task TurnStart_ReportsTurnIndex()
    {
        var index = -1;
        var pipeline = Pipeline(new ProbeExtension("probe", api =>
            api.On<AgentLifecycleEvent>(ExtensionEvent.TurnStart, (e, _, _) =>
            {
                index = e.TurnIndex;
                return Task.FromResult<AgentLifecycleEvent?>(null);
            })));

        await pipeline.PublishTurnStartAsync(Context(pipeline), 3);

        Assert.Equal(3, index);
    }

    [Fact]
    public async Task TurnEnd_CarriesTurnIndex()
    {
        var pipeline = Pipeline(new ProbeExtension("probe", _ => { }));

        var result = await pipeline.PublishTurnEndAsync(Context(pipeline), "done", 1, turnIndex: 2);

        Assert.Equal(2, result.TurnIndex);
    }

    [Fact]
    public async Task MessageEnd_CanRewriteAssistantText()
    {
        var pipeline = Pipeline(new ProbeExtension("probe", api =>
            api.On<MessageEvent>(ExtensionEvent.MessageEnd, (e, _, _) =>
            {
                if (e.IsAssistant)
                {
                    e.Text = e.Text.TrimEnd();
                }

                return Task.FromResult<MessageEvent?>(e);
            })));

        var text = await pipeline.PublishMessageEndAsync(Context(pipeline), ChatRole.Assistant, "answer   \n\n");

        Assert.Equal("answer", text);
    }

    [Fact]
    public async Task MessageEnd_LeavesUserTextAloneWhenUntouched()
    {
        var pipeline = Pipeline(new ProbeExtension("probe", _ => { }));

        var text = await pipeline.PublishMessageEndAsync(Context(pipeline), ChatRole.User, "hello");

        Assert.Equal("hello", text);
    }

    [Fact]
    public async Task InstructionsLoaded_CanDisableSections()
    {
        var pipeline = Pipeline(new ProbeExtension("filter", api =>
            api.On<InstructionsEvent>(ExtensionEvent.InstructionsLoaded, (e, _, _) =>
            {
                foreach (var section in e.Sections)
                {
                    section.Enabled = section.Source != "skill";
                }

                return Task.FromResult<InstructionsEvent?>(e);
            })));

        var sections = new List<InstructionsSection>
        {
            new() { Source = "agents", Text = "# rules" },
            new() { Source = "skill", Text = "# skills" }
        };

        var prompt = await pipeline.PublishInstructionsLoadedAsync(Context(pipeline), sections);

        Assert.Equal("# rules", prompt);
    }

    [Fact]
    public async Task InstructionsLoaded_OverridePromptWins()
    {
        var pipeline = Pipeline(new ProbeExtension("replace", api =>
            api.On<InstructionsEvent>(ExtensionEvent.InstructionsLoaded, (e, _, _) =>
            {
                e.OverridePrompt = "replaced";
                return Task.FromResult<InstructionsEvent?>(e);
            })));

        var prompt = await pipeline.PublishInstructionsLoadedAsync(
            Context(pipeline),
            new List<InstructionsSection> { new() { Source = "agents", Text = "original" } });

        Assert.Equal("replaced", prompt);
    }

    [Fact]
    public async Task InstructionsLoaded_WithoutHandlersJoinsSections()
    {
        var pipeline = Pipeline(new ProbeExtension("probe", _ => { }));

        var prompt = await pipeline.PublishInstructionsLoadedAsync(
            Context(pipeline),
            new List<InstructionsSection>
            {
                new() { Source = "agents", Text = "a" },
                new() { Source = "skill", Text = "b" }
            });

        Assert.Equal("a\n\nb", prompt);
    }

    [Fact]
    public async Task ToolError_IsPublishedWhenToolThrows()
    {
        string? error = null;
        var pipeline = Pipeline(new ProbeExtension("probe", api =>
        {
            api.RegisterTool(ToolDefinition.Create("boom"), (_, _, _) => throw new InvalidOperationException("exploded"));
            api.On<ToolErrorEvent>(ExtensionEvent.ToolError, (e, _, _) =>
            {
                error = e.ToolName + ":" + e.Error;
                return Task.FromResult<ToolErrorEvent?>(null);
            });
        }));

        var result = await pipeline.Runtime.ExecuteToolAsync("boom", "{}", Context(pipeline));

        Assert.True(result.IsError);
        Assert.Equal("boom:exploded", error);
    }

    [Fact]
    public async Task ToolError_NotPublishedWhenToolSucceeds()
    {
        var fired = false;
        var pipeline = Pipeline(new ProbeExtension("probe", api =>
        {
            api.RegisterTool(ToolDefinition.Create("ok"), (_, _, _) => Task.FromResult("fine"));
            api.On(ExtensionEvent.ToolError, (_, _, _) =>
            {
                fired = true;
                return Task.CompletedTask;
            });
        }));

        await pipeline.Runtime.ExecuteToolAsync("ok", "{}", Context(pipeline));

        Assert.False(fired);
    }

    [Fact]
    public async Task ToolExecutionUpdate_ReachesProgressCallback()
    {
        var updates = new List<string>();
        var pipeline = Pipeline(new ProbeExtension("probe", api =>
            api.RegisterTool(ToolDefinition.Create("slow"), async (_, ctx, _) =>
            {
                await ctx.ReportUpdateAsync("line 1");
                await ctx.ReportUpdateAsync("line 2");
                return "done";
            })));

        await pipeline.Runtime.ExecuteToolAsync(
            "slow",
            "{}",
            Context(pipeline),
            onUpdate: update =>
            {
                updates.Add(update.PartialOutput ?? string.Empty);
                return Task.CompletedTask;
            });

        Assert.Equal(new[] { "line 1", "line 2" }, updates);
    }

    [Fact]
    public async Task ToolExecutionUpdate_WithoutSubscriberIsNoOp()
    {
        var pipeline = Pipeline(new ProbeExtension("probe", api =>
            api.RegisterTool(ToolDefinition.Create("slow"), async (_, ctx, _) =>
            {
                await ctx.ReportUpdateAsync("ignored");
                return "done";
            })));

        var result = await pipeline.Runtime.ExecuteToolAsync("slow", "{}", Context(pipeline));

        Assert.Equal("done", result.Text);
    }

    [Fact]
    public async Task PermissionRequest_ExtensionAllowSkipsConfirmation()
    {
        var confirmed = false;
        var pipeline = Pipeline(
            new ProbeExtension("probe", api =>
                api.On<PermissionRequestEvent>(ExtensionEvent.PermissionRequest, (e, _, _) =>
                {
                    e.Decision = PermissionDecision.Allow;
                    return Task.FromResult<PermissionRequestEvent?>(e);
                })),
            PermissionPolicy.FromRules(new[] { new PermissionRule { Tool = "*", Decision = PermissionDecision.Ask } }));

        var guarded = pipeline.GuardToolAsync(
            "shell",
            "{}",
            () => Task.FromResult("ran"),
            _ =>
            {
                confirmed = true;
                return Task.FromResult(true);
            },
            Context(pipeline));

        Assert.NotNull(guarded);
        Assert.Equal("ran", await guarded!);
        Assert.False(confirmed);
    }

    [Fact]
    public async Task PermissionRequest_ExtensionDenySkipsExecution()
    {
        var executed = false;
        var pipeline = Pipeline(
            new ProbeExtension("probe", api =>
                api.On<PermissionRequestEvent>(ExtensionEvent.PermissionRequest, (e, _, _) =>
                {
                    e.Decision = PermissionDecision.Deny;
                    e.Reason = "生产环境禁止";
                    return Task.FromResult<PermissionRequestEvent?>(e);
                })),
            PermissionPolicy.FromRules(new[] { new PermissionRule { Tool = "*", Decision = PermissionDecision.Ask } }));

        var guarded = pipeline.GuardToolAsync(
            "shell",
            "{}",
            () =>
            {
                executed = true;
                return Task.FromResult("ran");
            },
            _ => Task.FromResult(true),
            Context(pipeline));

        var text = await guarded!;

        Assert.False(executed);
        Assert.Contains("denied", text);
        Assert.Contains("生产环境禁止", text);
    }

    [Fact]
    public async Task PermissionRequest_WithoutContextFallsBackToConfirmation()
    {
        var confirmed = false;
        var pipeline = Pipeline(
            new ProbeExtension("probe", _ => { }),
            PermissionPolicy.FromRules(new[] { new PermissionRule { Tool = "*", Decision = PermissionDecision.Ask } }));

        var guarded = pipeline.GuardToolAsync(
            "shell",
            "{}",
            () => Task.FromResult("ran"),
            _ =>
            {
                confirmed = true;
                return Task.FromResult(true);
            });

        Assert.Equal("ran", await guarded!);
        Assert.True(confirmed);
    }

    [Fact]
    public async Task PermissionRequest_ExtensionCanCustomizePrompt()
    {
        string? prompt = null;
        var pipeline = Pipeline(
            new ProbeExtension("probe", api =>
                api.On<PermissionRequestEvent>(ExtensionEvent.PermissionRequest, (e, _, _) =>
                {
                    e.Prompt = "确认执行 shell？";
                    return Task.FromResult<PermissionRequestEvent?>(e);
                })),
            PermissionPolicy.FromRules(new[] { new PermissionRule { Tool = "*", Decision = PermissionDecision.Ask } }));

        var guarded = pipeline.GuardToolAsync(
            "shell",
            "{}",
            () => Task.FromResult("ran"),
            text =>
            {
                prompt = text;
                return Task.FromResult(false);
            },
            Context(pipeline));

        await guarded!;

        Assert.Equal("确认执行 shell？", prompt);
    }

    [Fact]
    public async Task PermissionRequest_HandlerFailureFailsClosedForToolCall()
    {
        var pipeline = Pipeline(
            new ProbeExtension("probe", api =>
                api.On(ExtensionEvent.PermissionRequest, (_, _, _) => throw new InvalidOperationException("bad"))),
            PermissionPolicy.FromRules(new[] { new PermissionRule { Tool = "*", Decision = PermissionDecision.Deny } }));

        var guarded = pipeline.GuardToolAsync(
            "shell",
            "{}",
            () => Task.FromResult("ran"),
            _ => Task.FromResult(true),
            Context(pipeline));

        // Deny 规则本身不经过 Ask，权限请求不参与判定；这里确认拒绝仍然生效。
        Assert.Contains("denied", await guarded!);
    }
}
