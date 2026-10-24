using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Extensions;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>工具编排（曝光/激活集合/命名空间/装载准备器/可达范围/内置编排工具）的测试。</summary>
public sealed class ToolOrchestrationTests
{
    private static CommandContext Context(ExtensionRuntime runtime)
        => new() { Runtime = runtime, State = runtime.State, WorkingDirectory = Environment.CurrentDirectory };

    private static ToolContext ToolContext(ExtensionRuntime runtime)
        => new() { Runtime = runtime, State = runtime.State };

    private static string[] Names(IEnumerable<ExtensionTool> tools)
        => tools.Select(tool => tool.Definition.Name).ToArray();

    /// <summary>声明面里由扩展登记的工具（排除运行时内置的编排工具）。</summary>
    private static string[] Declared(IEnumerable<ExtensionTool> tools)
        => tools
            .Where(tool => !string.Equals(tool.ExtensionName, ExtensionRuntime.RuntimeOwner, StringComparison.Ordinal))
            .Select(tool => tool.Definition.Name)
            .ToArray();

    private static async Task<JsonElement> InvokeBuiltInAsync(ExtensionRuntime runtime, string name, string argumentsJson)
    {
        var result = await runtime.InvokeToolAsync(name, argumentsJson, ToolContext(runtime));
        Assert.False(result.IsError, result.Text);
        using var document = JsonDocument.Parse(result.Text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ExposureMatrix_DeclaresOnlyDirectAndModelOnly()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("matrix", api =>
        {
            api.RegisterTool(ToolDefinition.Create("e_direct"), (_, _, _) => Task.FromResult("direct"));
            api.RegisterTool(ToolDefinition.Create("e_model"), (_, _, _) => Task.FromResult("model"), ToolExposure.ModelOnly);
            api.RegisterTool(ToolDefinition.Create("e_codemode"), (_, _, _) => Task.FromResult("codemode"), ToolExposure.Codemode);
            api.RegisterTool(ToolDefinition.Create("e_deferred"), (_, _, _) => Task.FromResult("deferred"), ToolExposure.Deferred);
            api.RegisterTool(ToolDefinition.Create("e_hidden"), (_, _, _) => Task.FromResult("hidden"), ToolExposure.Hidden);
        }));

        var declared = Names(runtime.DeclaredTools);
        Assert.Contains("e_direct", declared);
        Assert.Contains("e_model", declared);
        Assert.DoesNotContain("e_codemode", declared);
        Assert.DoesNotContain("e_deferred", declared);
        Assert.DoesNotContain("e_hidden", declared);

        var callable = Names(runtime.BuildLoadout().Callable);
        Assert.Contains("e_direct", callable);
        Assert.DoesNotContain("e_model", callable);
        Assert.Contains("e_codemode", callable);
        Assert.Contains("e_deferred", callable);
        Assert.DoesNotContain("e_hidden", callable);

        // 激活 codemode/deferred 只影响声明面；hidden 永远不可达，激活无副作用。
        Assert.True(runtime.ActivateTool("e_codemode"));
        Assert.True(runtime.ActivateTool("e_deferred"));
        Assert.False(runtime.ActivateTool("e_hidden"));

        declared = Names(runtime.DeclaredTools);
        Assert.Contains("e_codemode", declared);
        Assert.Contains("e_deferred", declared);
        Assert.DoesNotContain("e_hidden", declared);
    }

    [Fact]
    public async Task ExposureMatrix_ExecuteToolAsyncReachesOnlyCallableExposures()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("matrix", api =>
        {
            api.RegisterTool(ToolDefinition.Create("e_direct"), (_, _, _) => Task.FromResult("ok"));
            api.RegisterTool(ToolDefinition.Create("e_model"), (_, _, _) => Task.FromResult("ok"), ToolExposure.ModelOnly);
            api.RegisterTool(ToolDefinition.Create("e_codemode"), (_, _, _) => Task.FromResult("ok"), ToolExposure.Codemode);
            api.RegisterTool(ToolDefinition.Create("e_deferred"), (_, _, _) => Task.FromResult("ok"), ToolExposure.Deferred);
            api.RegisterTool(ToolDefinition.Create("e_hidden"), (_, _, _) => Task.FromResult("ok"), ToolExposure.Hidden);
        }));

        var context = Context(runtime);
        Assert.False((await runtime.ExecuteToolAsync("e_direct", "{}", context)).IsError);
        Assert.True((await runtime.ExecuteToolAsync("e_model", "{}", context)).IsError);
        Assert.False((await runtime.ExecuteToolAsync("e_codemode", "{}", context)).IsError);
        Assert.False((await runtime.ExecuteToolAsync("e_deferred", "{}", context)).IsError);
        Assert.True((await runtime.ExecuteToolAsync("e_hidden", "{}", context)).IsError);
    }

    [Fact]
    public void SetActiveTools_IgnoresUnknownNamesAndDrivesDeclarations()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("active", api =>
        {
            api.RegisterTool(ToolDefinition.Create("one"), (_, _, _) => Task.FromResult(""));
            api.RegisterTool(ToolDefinition.Create("two"), (_, _, _) => Task.FromResult(""), ToolExposure.Deferred);
        }));

        Assert.Equal(new[] { "one" }, Declared(runtime.DeclaredTools));

        // 未知名字被忽略：只剩一个真实名字时结果仍是一次真实变更。
        Assert.True(runtime.SetActiveTools(new[] { "nope", "two" }));
        Assert.Equal(new[] { "two" }, Declared(runtime.DeclaredTools));

        // 相同集合不算变更。
        Assert.False(runtime.SetActiveTools(new[] { "two" }));

        // 直接曝光工具登记即激活，停用是无操作。
        Assert.False(runtime.DeactivateTool("one"));
        Assert.False(runtime.ActivateTool("one"));
        Assert.False(runtime.ActivateTool("nope"));

        // 直接曝光工具被 SetActiveTools 移出后仍需 SetActiveTools 放回（ActivateTool 对它无操作）。
        Assert.True(runtime.SetActiveTools(new[] { "one" }));
        Assert.Equal(new[] { "one" }, Declared(runtime.DeclaredTools));
    }

    [Fact]
    public void Namespace_LookupToleratesSeparatorsAndGroupsTools()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("docs", api =>
        {
            api.RegisterNamespace(new ToolNamespace
            {
                Name = "my-docs",
                Aliases = new[] { "docs" },
                Description = "Documentation tools",
                Instructions = "Read before writing."
            });
            api.RegisterTool(ToolDefinition.Create("docs_read"), (_, _, _) => Task.FromResult(""), @namespace: "my-docs");
            api.RegisterTool(ToolDefinition.Create("loose"), (_, _, _) => Task.FromResult(""));
        }));

        Assert.Equal("my-docs", runtime.FindNamespace("my_docs")!.Name);
        Assert.Equal("my-docs", runtime.FindNamespace("docs")!.Name);
        Assert.Null(runtime.FindNamespace("missing"));
        Assert.Equal("my-docs", runtime.TryGetTool("docs_read")!.Namespace);

        var groups = runtime.ToolGroups();
        var docs = Assert.Single(groups, group => group.Namespace?.Name == "my-docs");
        Assert.Equal(new[] { "docs_read" }, Names(docs.Tools));
        Assert.Contains(groups, group => group.Namespace == null && Names(group.Tools).Contains("loose"));
    }

    [Fact]
    public async Task ToolSearch_MatchesNameNamespaceAliasAndDescription()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("search", api =>
        {
            api.RegisterNamespace(new ToolNamespace { Name = "my-docs", Aliases = new[] { "docs" } });
            api.RegisterTool(
                ToolDefinition.Create("pdf_export", "Export a document as PDF."),
                (_, _, _) => Task.FromResult("{\"ok\":1}"),
                ToolExposure.Codemode,
                @namespace: "my-docs");
            api.RegisterTool(
                ToolDefinition.Create("db_query", "Run a read-only database query."),
                (_, _, _) => Task.FromResult("{}"),
                ToolExposure.Deferred);
        }));

        Assert.NotNull(runtime.TryGetTool("tool_search"));

        var byName = await InvokeBuiltInAsync(runtime, "tool_search", """{"query":"pdf"}""");
        Assert.Equal("pdf_export", Assert.Single(byName.EnumerateArray()).GetProperty("name").GetString());

        // 别名（分隔符不同也算命中）：my_docs → my-docs。
        var byAlias = await InvokeBuiltInAsync(runtime, "tool_search", """{"query":"my_docs"}""");
        Assert.Equal("pdf_export", Assert.Single(byAlias.EnumerateArray()).GetProperty("name").GetString());

        var byDescription = await InvokeBuiltInAsync(runtime, "tool_search", """{"query":"database"}""");
        var match = Assert.Single(byDescription.EnumerateArray());
        Assert.Equal("db_query", match.GetProperty("name").GetString());
        Assert.Equal("deferred", match.GetProperty("exposure").GetString());
        Assert.True(match.GetProperty("activated").GetBoolean());

        Assert.Contains("db_query", Names(runtime.DeclaredTools));
    }

    [Fact]
    public async Task ToolSearch_CanSearchWithoutActivating()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("search", api =>
            api.RegisterTool(ToolDefinition.Create("later"), (_, _, _) => Task.FromResult(""), ToolExposure.Deferred)));

        var payload = await InvokeBuiltInAsync(runtime, "tool_search", """{"query":"later","activate":false}""");
        Assert.False(Assert.Single(payload.EnumerateArray()).GetProperty("activated").GetBoolean());
        Assert.DoesNotContain("later", Names(runtime.DeclaredTools));
    }

    [Fact]
    public void ToolSearch_AbsentWithoutCodemodeOrDeferredTools()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("plain", api =>
            api.RegisterTool(ToolDefinition.Create("direct"), (_, _, _) => Task.FromResult(""))));

        Assert.Null(runtime.TryGetTool("tool_search"));
        Assert.Null(runtime.TryGetTool("codemode"));
    }

    [Fact]
    public void PrepareLoadout_ChainsPreparersAndAppliesDescriptions()
    {
        var runtime = new ExtensionRuntime();
        var seen = new List<string>();
        runtime.Activate(new ProbeExtension("first", api => api.RegisterLoadoutPreparer(loadout =>
        {
            seen.Add("first");
            loadout.Descriptions["t"] = "A";
            return loadout;
        })));
        runtime.Activate(new ProbeExtension("second", api => api.RegisterLoadoutPreparer(loadout =>
        {
            seen.Add("second:" + loadout.Descriptions["t"]);
            loadout.Descriptions["t"] = loadout.Descriptions["t"] + "+B";
            return loadout;
        })));
        runtime.Activate(new ProbeExtension("tools", api =>
            api.RegisterTool(ToolDefinition.Create("t", "original"), (_, _, _) => Task.FromResult(""))));

        var declared = Assert.Single(runtime.BuildLoadout().Declared);
        Assert.Equal("A+B", declared.Definition.Description);
        Assert.Equal(new[] { "first", "second:A" }, seen);
        Assert.Equal("A+B", Assert.Single(runtime.DeclaredTools).Definition.Description);
    }

    [Fact]
    public void PrepareLoadout_HiddenDeclarationsOmitButKeepCallable()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("hider", api => api.RegisterLoadoutPreparer(loadout =>
        {
            loadout.HiddenDeclarations.Add("visible_one");
            return loadout;
        })));
        runtime.Activate(new ProbeExtension("tools", api =>
        {
            api.RegisterTool(ToolDefinition.Create("visible_one"), (_, _, _) => Task.FromResult(""));
            api.RegisterTool(ToolDefinition.Create("visible_two"), (_, _, _) => Task.FromResult(""));
        }));

        var loadout = runtime.BuildLoadout();
        Assert.Equal(new[] { "visible_two" }, Names(loadout.Declared));
        Assert.Contains("visible_one", Names(loadout.Callable));
        Assert.Contains("visible_one", Names(loadout.All));
        Assert.Equal(new[] { "visible_two" }, Names(runtime.DeclaredTools));
    }

    [Fact]
    public void PrepareLoadout_ThrowingPreparerIsSkipped()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("boom", api => api.RegisterLoadoutPreparer(_ =>
            throw new InvalidOperationException("nope"))));
        runtime.Activate(new ProbeExtension("after", api => api.RegisterLoadoutPreparer(loadout =>
        {
            loadout.Descriptions["t"] = "after";
            return loadout;
        })));
        runtime.Activate(new ProbeExtension("tools", api =>
            api.RegisterTool(ToolDefinition.Create("t", "original"), (_, _, _) => Task.FromResult(""))));

        var declared = Assert.Single(runtime.BuildLoadout().Declared);
        Assert.Equal("after", declared.Definition.Description);
    }

    [Fact]
    public async Task ReachesTools_WordsAndNamespaces()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("reach", api =>
        {
            api.RegisterNamespace(new ToolNamespace { Name = "my-docs", Aliases = new[] { "docs" } });
            api.RegisterTool(
                ToolDefinition.Create("docs_read"),
                (_, _, _) => Task.FromResult("read"),
                @namespace: "my-docs");
            api.RegisterTool(ToolDefinition.Create("other_tool"), (_, _, _) => Task.FromResult("other"));
            api.RegisterTool(
                ToolDefinition.Create("orchestrate"),
                (_, _, _) => Task.FromResult("orch"),
                ToolExposure.ModelOnly,
                reachesTools: ToolReach.Words("doc"));
        }));

        var context = Context(runtime);
        var allowed = await runtime.ExecuteToolAsync("docs_read", "{}", context, parentToolName: "orchestrate");
        var blocked = await runtime.ExecuteToolAsync("other_tool", "{}", context, parentToolName: "orchestrate");

        Assert.False(allowed.IsError);
        Assert.True(blocked.IsError);
        Assert.True(blocked.Blocked);

        var reach = runtime.ToolReachOf("orchestrate");
        Assert.NotNull(reach);
        Assert.False(reach!.IsAll);
    }

    [Fact]
    public async Task ReachesTools_AllAllowsEveryTool()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("reach", api =>
        {
            api.RegisterTool(ToolDefinition.Create("target"), (_, _, _) => Task.FromResult("ok"));
            api.RegisterTool(
                ToolDefinition.Create("codemode_only"),
                (_, _, _) => Task.FromResult(""),
                ToolExposure.ModelOnly,
                reachesTools: ToolReach.All());
        }));

        Assert.True(runtime.ToolReachOf("codemode_only")!.IsAll);
        var result = await runtime.ExecuteToolAsync("target", "{}", Context(runtime), parentToolName: "codemode_only");
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task ReachesTools_AppliesToNestedToolContextCalls()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("reach", api =>
        {
            api.RegisterTool(ToolDefinition.Create("allowed_tool"), (_, _, _) => Task.FromResult("ok"));
            api.RegisterTool(ToolDefinition.Create("forbidden_tool"), (_, _, _) => Task.FromResult("ok"));
            api.RegisterTool(
                ToolDefinition.Create("narrow"),
                async (_, ctx, token) => await ctx.ExecuteToolAsync("forbidden_tool", "{}", token),
                reachesTools: ToolReach.Words("allowed"));
        }));

        var context = Context(runtime);
        var blocked = await runtime.ExecuteToolAsync("narrow", "{}", context);
        Assert.Contains("outside the reach", blocked.Text);

        var codemode = CodemodeTool.Create(runtime, ToolReach.Words("allowed"));
        var stepped = await codemode.Execute(
            new ToolCall("t1", "codemode", """{"steps":[{"tool":"forbidden_tool"}]}"""),
            ToolContext(runtime),
            CancellationToken.None);
        Assert.Contains("outside the reach", stepped);
    }

    [Fact]
    public async Task Codemode_RunsStepsAndResolvesArgumentsFromPreviousResult()
    {
        var runtime = new ExtensionRuntime();
        var observed = string.Empty;
        runtime.Activate(new ProbeExtension("steps", api =>
        {
            api.RegisterTool(
                ToolDefinition.Create("get_item"),
                (_, _, _) => Task.FromResult("""{"items":[{"id":"abc","name":"n1"}]}"""));
            api.RegisterTool(
                ToolDefinition.Create("echo_id"),
                (call, _, _) =>
                {
                    using var document = JsonDocument.Parse(call.ArgumentsJson);
                    observed = document.RootElement.GetProperty("id").GetString() ?? string.Empty;
                    return Task.FromResult("{\"id\":\"" + observed + "\"}");
                });
        }));

        var codemode = CodemodeTool.Create(runtime);
        var result = await codemode.Execute(
            new ToolCall("t1", "codemode", """
                {"steps":[
                  {"tool":"get_item"},
                  {"tool":"echo_id","argumentsFrom":{"id":"result.items.0.id"}}
                ]}
                """),
            ToolContext(runtime),
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("results").GetArrayLength());
        Assert.Equal("abc", observed);
    }

    [Fact]
    public async Task Codemode_StepFailureStopsRunAndReportsIndex()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("steps", api =>
        {
            api.RegisterTool(ToolDefinition.Create("fine"), (_, _, _) => Task.FromResult("ok"));
            api.RegisterTool(ToolDefinition.Create("bad"), (_, _, _) => throw new InvalidOperationException("boom"));
        }));

        var codemode = CodemodeTool.Create(runtime);
        var result = await codemode.Execute(
            new ToolCall("t1", "codemode", """{"steps":[{"tool":"fine"},{"tool":"bad"},{"tool":"fine"}]}"""),
            ToolContext(runtime),
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("failedStep").GetInt32());
        Assert.Contains("boom", document.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task Codemode_ReachViolationFailsStep()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("steps", api =>
        {
            api.RegisterTool(ToolDefinition.Create("allowed_tool"), (_, _, _) => Task.FromResult("ok"));
            api.RegisterTool(ToolDefinition.Create("forbidden_tool"), (_, _, _) => Task.FromResult("ok"));
        }));

        var codemode = CodemodeTool.Create(runtime, ToolReach.Words("allowed"), "scoped_codemode");
        var result = await codemode.Execute(
            new ToolCall("t1", "scoped_codemode", """{"steps":[{"tool":"forbidden_tool"}]}"""),
            ToolContext(runtime),
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("failedStep").GetInt32());
        Assert.Contains("outside the reach", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Codemode_RejectsMoreThanMaxSteps()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("steps", api =>
            api.RegisterTool(ToolDefinition.Create("noop"), (_, _, _) => Task.FromResult("ok"))));

        var steps = string.Join(",", Enumerable.Range(0, CodemodeTool.MaxSteps + 1)
            .Select(_ => "{\"tool\":\"noop\"}"));
        var codemode = CodemodeTool.Create(runtime);
        var result = await codemode.Execute(
            new ToolCall("t1", "codemode", "{\"steps\":[" + steps + "]}"),
            ToolContext(runtime),
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("32", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Codemode_RegisteredAutomaticallyWithCodemodeOrReachTools()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("codemode_tool", api =>
            api.RegisterTool(ToolDefinition.Create("script_like"), (_, _, _) => Task.FromResult(""), ToolExposure.Codemode)));

        Assert.NotNull(runtime.TryGetTool("codemode"));
        Assert.True(runtime.ToolReachOf("codemode")!.IsAll);
        Assert.True(runtime.ToolReachOf("tool_search")!.IsAll);
        Assert.Contains("codemode", Names(runtime.DeclaredTools));
        Assert.Contains("tool_search", Names(runtime.DeclaredTools));
    }

    [Fact]
    public async Task Pipeline_PrepareTurn_UsesLoadoutDeclarations()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("hider", api => api.RegisterLoadoutPreparer(loadout =>
        {
            loadout.HiddenDeclarations.Add("hidden_by_loadout");
            return loadout;
        })));
        runtime.Activate(new ProbeExtension("tools", api =>
        {
            api.RegisterTool(ToolDefinition.Create("shown"), (_, _, _) => Task.FromResult(""));
            api.RegisterTool(ToolDefinition.Create("hidden_by_loadout"), (_, _, _) => Task.FromResult(""));
            api.RegisterTool(ToolDefinition.Create("deferred_tool"), (_, _, _) => Task.FromResult(""), ToolExposure.Deferred);
            api.RegisterTool(
                ToolDefinition.Create("runner"),
                (_, _, _) => Task.FromResult(""),
                ToolExposure.ModelOnly,
                reachesTools: ToolReach.All());
        }));

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Runtime = runtime,
            WorkingDirectory = Environment.CurrentDirectory
        });

        var preparation = await pipeline.PrepareTurnAsync(pipeline.Session.CreateContext(null, null), "m");
        var names = preparation.Tools.Select(item => item.Definition.Name).ToArray();

        Assert.Contains("shown", names);
        Assert.Contains("runner", names);
        Assert.Contains("tool_search", names);
        Assert.Contains("codemode", names);
        Assert.DoesNotContain("hidden_by_loadout", names);
        Assert.DoesNotContain("deferred_tool", names);

        // 隐藏声明仍然可调用；激活后即随下一轮声明。
        Assert.False((await runtime.ExecuteToolAsync(
            "hidden_by_loadout",
            "{}",
            Context(runtime))).IsError);

        Assert.True(pipeline.ActivateTool("deferred_tool"));
        var activated = await pipeline.PrepareTurnAsync(pipeline.Session.CreateContext(null, null), "m");
        Assert.Contains("deferred_tool", activated.Tools.Select(item => item.Definition.Name));
        Assert.DoesNotContain("hidden_by_loadout", activated.Tools.Select(item => item.Definition.Name));

        Assert.True(pipeline.SetActiveTools(new[] { "shown" }));
        var trimmed = await pipeline.PrepareTurnAsync(pipeline.Session.CreateContext(null, null), "m");
        Assert.Equal(
            new[] { "codemode", "shown", "tool_search" },
            trimmed.Tools.Select(item => item.Definition.Name).ToArray());
    }

    [Fact]
    public void Reset_ClearsActiveSetNamespacesAndPreparers()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("probe", api =>
        {
            api.RegisterNamespace(new ToolNamespace { Name = "ns" });
            api.RegisterLoadoutPreparer(loadout =>
            {
                loadout.Descriptions["t"] = "prepared";
                return loadout;
            });
            api.RegisterTool(ToolDefinition.Create("t"), (_, _, _) => Task.FromResult(""));
            api.RegisterTool(ToolDefinition.Create("later"), (_, _, _) => Task.FromResult(""), ToolExposure.Deferred);
        }));

        Assert.True(runtime.ActivateTool("later"));
        runtime.Reset();

        Assert.Null(runtime.FindNamespace("ns"));
        Assert.Empty(runtime.ActiveTools);
        Assert.Empty(runtime.DeclaredTools);

        runtime.Activate(new ProbeExtension("probe", api =>
            api.RegisterTool(ToolDefinition.Create("t", "original"), (_, _, _) => Task.FromResult(""))));

        Assert.Equal("original", Assert.Single(runtime.DeclaredTools).Definition.Description);
    }
}
