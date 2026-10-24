using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Extensions;
using AiTools.Extensions.Workflows;
using Xunit;
using Xunit.Abstractions;

namespace AiTools.Tests.Extensions;

/// <summary>
/// 验证 JavaScript 工作流：<c>meta</c> 由真实 JS 求值解析、沙箱全局的行为、
/// 超时能杀掉无限循环、以及命令适配器把结果作为 <see cref="CommandResultKind.Display"/> 返回。
/// </summary>
public sealed class WorkflowRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aitools-wf-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public WorkflowRuntimeTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            RemoveLinks(_root);
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            // 链接未被完全清理时（Windows 目录句柄仍被占用）放弃清理：临时目录由系统回收。
        }
    }

    /// <summary>先删掉测试创建的链接/联结点，否则递归删除会跟随或被拒绝（Windows 上表现为 UnauthorizedAccess）。</summary>
    private static void RemoveLinks(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var child in Directory.GetDirectories(directory))
        {
            try
            {
                var info = new DirectoryInfo(child);
                if (info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(child, recursive: false);
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            RemoveLinks(child);
        }
    }

    // ---------- loader ----------

    [Fact]
    public void LoadDirectory_ParsesMetaWithJint()
    {
        Write("audit.js", """
            export const meta = {
              name: 'audit-routes',
              description: 'Audit the route table',
              parameters: ['path'],
              arguments: 'target=$path'
            };
            const target = arguments;
            ({ target });
            """);

        var workflows = WorkflowLoader.LoadDirectory(_root, "workflows", out var diagnostics);

        Assert.Empty(diagnostics);
        var workflow = Assert.Single(workflows);
        Assert.Equal("audit-routes", workflow.Name);
        Assert.Equal("Audit the route table", workflow.Description);
        Assert.Equal("workflows", workflow.PluginName);
        Assert.Equal(new[] { "path" }, workflow.Parameters);
        Assert.Equal("target=$path", workflow.ArgumentTemplate);
        Assert.False(workflow.Script.Contains("export const meta", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_MissingMeta_FallsBackToFileNameAndWarns()
    {
        var path = Write("plain-script.js", "const value = 1;\nvalue;\n");

        var workflow = WorkflowLoader.Load(path, null, out var warning);

        Assert.NotNull(workflow);
        Assert.Equal("plain-script", workflow!.Name);
        Assert.Equal("host", workflow.PluginName);
        Assert.Contains("meta", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_MetaWithoutName_WarnsAndFallsBack()
    {
        var path = Write("no-name.js", "export const meta = { description: 'no name here' };\n1;\n");

        var workflow = WorkflowLoader.Load(path, "wf", out var warning);

        Assert.NotNull(workflow);
        Assert.Equal("no-name", workflow!.Name);
        Assert.Equal("no name here", workflow.Description);
        Assert.Contains("name", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_OversizeScript_IsRejected()
    {
        var padding = new string('x', WorkflowLoader.MaxScriptBytes + 512);
        var path = Write("huge.js", "export const meta = { name: 'huge' };\n// " + padding + "\n1;\n");

        var workflow = WorkflowLoader.Load(path, null, out var error);

        Assert.Null(workflow);
        Assert.Contains("上限", error!);
    }

    [Fact]
    public void LoadDirectory_OrdersByNameAndReportsDuplicates()
    {
        Write("b.js", "export const meta = { name: 'beta' };\n1;\n");
        Write("a.js", "export const meta = { name: 'alpha' };\n1;\n");
        Write("c.js", "export const meta = { name: 'alpha' };\n2;\n");

        var workflows = WorkflowLoader.LoadDirectory(_root, "wf", out var diagnostics);

        Assert.Equal(new[] { "alpha", "beta" }, workflows.Select(w => w.Name));
        Assert.Contains(diagnostics, d => d.Kind == PluginDiagnosticKind.Warning && d.Message.Contains("重复", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_ModuleLoadingSyntax_IsRejected()
    {
        var path = Write("imports.js", "import { x } from './other.js';\nexport const meta = { name: 'imports' };\n1;\n");

        var workflow = WorkflowLoader.Load(path, null, out var error);

        Assert.Null(workflow);
        Assert.Contains("模块", error!);
    }

    // ---------- runtime ----------

    [Fact]
    public async Task Run_ReturnsFinalExpression()
    {
        var workflow = Definition("""
            export const meta = { name: 'final', description: 'final expression' };
            const base = 40;
            ({ total: base + 2, ok: true });
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(42, JsonDocument.Parse(result.Output!).RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Run_TopLevelReturn_IsTheResult()
    {
        var workflow = Definition("""
            export const meta = { name: 'ret' };
            const items = [1, 2, 3];
            return items.map(x => x * 2);
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(new[] { 2, 4, 6 }, JsonDocument.Parse(result.Output!).RootElement.EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public async Task Agent_SendsPromptToChatClient()
    {
        var client = new CapturingChatClient("assistant-text");
        var workflow = Definition("""
            export const meta = { name: 'chat' };
            const answer = await agent('Summarize the diff', { label: 'summary' });
            ({ answer });
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions { Chat = client, Model = "test-model" });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, result.AgentCalls);
        var request = Assert.Single(client.Requests);
        Assert.Equal("test-model", request.Model);
        Assert.Equal("Summarize the diff", Assert.Single(request.Messages).GetText());
        Assert.Equal(ResponseFormatKind.Text, request.ResponseFormat!.Kind);
        Assert.Equal("assistant-text", JsonDocument.Parse(result.Output!).RootElement.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task Agent_WithSchema_ReturnsObjectToScript()
    {
        var client = new CapturingChatClient("{\"issues\":[\"a\",\"b\"],\"clean\":false}");
        var workflow = Definition("""
            export const meta = { name: 'schema' };
            const report = await agent('Review', {
              label: 'review',
              schema: { type: 'object', properties: { issues: { type: 'array' } }, required: ['issues'] }
            });
            ({ count: report.issues.length, first: report.issues[0], clean: report.clean });
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions { Chat = client, Model = "m" });

        Assert.True(result.Ok, result.Error);
        var request = Assert.Single(client.Requests);
        Assert.Equal(ResponseFormatKind.JsonSchema, request.ResponseFormat!.Kind);
        Assert.Equal("review", request.ResponseFormat.SchemaName);
        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.Equal(2, output.GetProperty("count").GetInt32());
        Assert.Equal("a", output.GetProperty("first").GetString());
        Assert.False(output.GetProperty("clean").GetBoolean());
    }

    [Fact]
    public async Task Agent_Failure_ThrowsCatchableScriptError()
    {
        var workflow = Definition("""
            export const meta = { name: 'agent-fail' };
            let caught = 'none';
            try { await agent('boom'); } catch (error) { caught = error.name + ':' + error.message; }
            ({ caught });
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions { Chat = new FailingChatClient() });

        Assert.True(result.Ok, result.Error);
        Assert.StartsWith("WorkflowError:", JsonDocument.Parse(result.Output!).RootElement.GetProperty("caught").GetString());
    }

    [Fact]
    public async Task Agent_WithoutChat_FailsTheRun()
    {
        var workflow = Definition("""
            export const meta = { name: 'no-chat' };
            await agent('anything');
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions());

        Assert.False(result.Ok);
        Assert.Contains("agent", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pipeline_RunsSequentiallyAndDropsNulls()
    {
        var order = new List<string>();
        var workflow = Definition("""
            export const meta = { name: 'pipeline' };
            const results = await pipeline(['one', 'two', 'three'], async (item, index) => {
              await tool('record', { item: item, index: index });
              return index === 1 ? null : item.toUpperCase();
            });
            return results;
            """);

        var options = new WorkflowRuntimeOptions
        {
            Policy = AllowAll(),
            ToolInvoker = (name, json) =>
            {
                using var document = JsonDocument.Parse(json);
                order.Add(name + ":" + document.RootElement.GetProperty("item").GetString());
                return Task.FromResult("ok");
            }
        };

        var result = await Run(workflow, options);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(new[] { "record:one", "record:two", "record:three" }, order);
        Assert.Equal(new[] { "ONE", "THREE" }, JsonDocument.Parse(result.Output!).RootElement.EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(3, result.ToolCalls);
    }

    [Fact]
    public async Task Tool_InvokesInvokerAndThrowsWhenMissing()
    {
        var seen = new List<string>();
        var workflow = Definition("""
            export const meta = { name: 'tool' };
            const value = await tool('search', { query: 'routes' });
            let missing = 'none';
            try { await tool('absent', {}); } catch (error) { missing = error.message; }
            ({ value, missing });
            """);

        var options = new WorkflowRuntimeOptions
        {
            Policy = AllowAll(),
            ToolInvoker = (name, json) =>
            {
                seen.Add(name + "|" + json);
                return name == "search"
                    ? Task.FromResult("found")
                    : throw new InvalidOperationException("no such tool");
            }
        };

        var result = await Run(workflow, options);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.ToolCalls);
        Assert.Contains("search|{\"query\":\"routes\"}", seen);
        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.Equal("found", output.GetProperty("value").GetString());
        Assert.Contains("no such tool", output.GetProperty("missing").GetString()!);
    }

    [Fact]
    public async Task Tool_WithoutInvoker_Fails()
    {
        var workflow = Definition("""
            export const meta = { name: 'no-tool' };
            await tool('anything', {});
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions());

        Assert.False(result.Ok);
        Assert.Contains("tool", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFile_RefusesPathEscape()
    {
        File.WriteAllText(Path.Combine(_root, "inside.txt"), "inside-content");
        var workflow = Definition("""
            export const meta = { name: 'files' };
            const inside = await readFile('inside.txt');
            let escaped = 'none';
            try { await readFile('../outside.txt'); } catch (error) { escaped = error.message; }
            let absolute = 'none';
            try { await readFile('C:/windows/win.ini'); } catch (error) { absolute = error.message; }
            ({ inside, escaped, absolute });
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions { WorkingDirectory = _root });

        Assert.True(result.Ok, result.Error);
        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.Equal("inside-content", output.GetProperty("inside").GetString());
        Assert.Contains("..", output.GetProperty("escaped").GetString()!);
        Assert.NotEqual("none", output.GetProperty("absolute").GetString());
    }

    [Fact]
    public async Task ReadFile_MissingFileReturnsEmptyString()
    {
        var workflow = Definition("""
            export const meta = { name: 'missing-file' };
            const text = await readFile('nope.txt');
            ({ length: text.length });
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions { WorkingDirectory = _root });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, JsonDocument.Parse(result.Output!).RootElement.GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task Log_IsCapturedNotWrittenToStdout()
    {
        var lines = new List<string>();
        var workflow = Definition("""
            export const meta = { name: 'logging' };
            log('first');
            log({ level: 'structured' });
            console.log('third');
            'done';
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions { Log = lines.Add });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(new[] { "first", "{\"level\":\"structured\"}", "third" }, lines);
    }

    [Fact]
    public async Task MaxSteps_AbortsWithClearError()
    {
        var workflow = Definition("""
            export const meta = { name: 'steps' };
            for (let i = 0; i < 10; i++) { await tool('step', { i }); }
            'never';
            """);

        var options = new WorkflowRuntimeOptions
        {
            MaxSteps = 3,
            Policy = AllowAll(),
            ToolInvoker = (_, _) => Task.FromResult("ok")
        };

        var result = await Run(workflow, options);

        Assert.False(result.Ok);
        Assert.Contains("最大步骤数", result.Error!);
        Assert.Equal(3, result.ToolCalls);
    }

    [Fact]
    public async Task Timeout_KillsInfiniteLoop()
    {
        var workflow = Definition("""
            export const meta = { name: 'spin' };
            while (true) { }
            'unreachable';
            """);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await Run(workflow, new WorkflowRuntimeOptions { Timeout = TimeSpan.FromSeconds(1) });
        stopwatch.Stop();

        Assert.False(result.Ok);
        Assert.Contains("超时", result.Error!);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), "超时未在合理时间内触发：" + stopwatch.Elapsed);
    }

    [Fact]
    public async Task HostCancellation_StopsInfiniteLoop()
    {
        var workflow = Definition("""
            export const meta = { name: 'cancelled' };
            while (true) { }
            """);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var options = new WorkflowRuntimeOptions { Timeout = TimeSpan.FromMinutes(5) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WorkflowRuntime(options).RunAsync(workflow, null, cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), "取消未在合理时间内生效：" + stopwatch.Elapsed);
    }

    [Fact]
    public async Task Runs_DoNotShareGlobals()
    {
        var first = Definition("""
            export const meta = { name: 'leak' };
            globalThis.leaked = 'from-first-run';
            'first';
            """);
        var second = Definition("""
            export const meta = { name: 'leak2' };
            ({ leaked: typeof globalThis.leaked });
            """);

        var options = new WorkflowRuntimeOptions();
        var firstResult = await Run(first, options);
        var secondResult = await Run(second, options);

        Assert.True(firstResult.Ok, firstResult.Error);
        Assert.True(secondResult.Ok, secondResult.Error);
        Assert.Equal("undefined", JsonDocument.Parse(secondResult.Output!).RootElement.GetProperty("leaked").GetString());
    }

    [Fact]
    public async Task Run_ExposesArgumentsAndMeta()
    {
        var workflow = Definition("""
            export const meta = { name: 'echo', description: 'echo args' };
            ({ text: arguments, name: meta.name, description: meta.description });
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions(), arguments: "focus on auth");

        Assert.True(result.Ok, result.Error);
        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.Equal("focus on auth", output.GetProperty("text").GetString());
        Assert.Equal("echo", output.GetProperty("name").GetString());
        Assert.Equal("echo args", output.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Run_MetaPreservesAuthorFields()
    {
        var workflow = WorkflowLoader.Load(
            Write("phases.js", "export const meta = { name: 'phased', phases: [{ title: 'scan' }, { title: 'report' }] };\nmeta.phases.map(p => p.title);\n"),
            "workflows",
            out _)!;

        var result = await Run(workflow, new WorkflowRuntimeOptions());

        Assert.True(result.Ok, result.Error);
        Assert.Equal(new[] { "scan", "report" }, JsonDocument.Parse(result.Output!).RootElement.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Run_ScriptError_ReturnsFailureWithoutThrowing()
    {
        var workflow = Definition("""
            export const meta = { name: 'broken' };
            throw new Error('script exploded');
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions());

        Assert.False(result.Ok);
        Assert.Contains("script exploded", result.Error!);
    }

    // ---------- sandbox hardening（安全回归）----------

    [Fact]
    public async Task ReadFile_JunctionInsideRoot_CannotEscape()
    {
        var sandbox = Path.Combine(_root, "sandbox");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(sandbox);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "TOP-SECRET");

        var linkPath = Path.Combine(sandbox, "link");
        if (!TryCreateDirectoryLink(linkPath, outside))
        {
            _output.WriteLine("跳过：当前平台/权限不允许创建目录链接，无法复现 junction 逃逸场景（词法检查仍然生效，但物理逃逸未被覆盖）。");
            return;
        }

        Assert.True(Directory.Exists(linkPath), "链接应已创建");
        Assert.NotNull(new DirectoryInfo(linkPath).LinkTarget);
        _output.WriteLine("已创建目录链接：" + linkPath + " -> " + new DirectoryInfo(linkPath).LinkTarget);

        var workflow = Definition("""
            export const meta = { name: 'junction' };
            let text = null;
            let error = null;
            try { text = await readFile('link/secret.txt'); } catch (thrown) { error = thrown.message; }
            ({ text, error });
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions { WorkingDirectory = sandbox });

        Assert.True(result.Ok, result.Error);
        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.Null(output.GetProperty("text").GetString());
        Assert.Contains("链接", output.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task ReadFile_DotDotInsideFileName_IsAllowed()
    {
        File.WriteAllText(Path.Combine(_root, "a..b.txt"), "double-dot-name");

        var workflow = Definition("""
            export const meta = { name: 'dots' };
            const text = await readFile('a..b.txt');
            let refused = null;
            try { await readFile('../x'); } catch (thrown) { refused = thrown.message; }
            ({ text, refused });
            """);

        var result = await Run(workflow, new WorkflowRuntimeOptions { WorkingDirectory = _root });

        Assert.True(result.Ok, result.Error);
        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.Equal("double-dot-name", output.GetProperty("text").GetString());
        Assert.Contains("..", output.GetProperty("refused").GetString()!);
    }

    [Fact]
    public async Task Tool_DenyPolicy_ReturnsDenialAndDoesNotInvokeBackend()
    {
        var calls = 0;
        var workflow = Definition("""
            export const meta = { name: 'deny' };
            const raw = await tool('bash', { command: 'rm -rf /' });
            ({ raw });
            """);

        var options = new WorkflowRuntimeOptions
        {
            Policy = new PermissionPolicy(new[]
            {
                new PermissionRule { Tool = "bash", Decision = PermissionDecision.Deny, Source = "test" }
            }),
            ToolInvoker = (_, _) =>
            {
                calls++;
                return Task.FromResult("should-not-run");
            }
        };

        var result = await Run(workflow, options);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, calls);
        Assert.Contains("denied", JsonDocument.Parse(result.Output!).RootElement.GetProperty("raw").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_AllowPolicy_InvokesOnce()
    {
        var calls = 0;
        var workflow = Definition("""
            export const meta = { name: 'allow' };
            const value = await tool('search', { query: 'x' });
            ({ value });
            """);

        var options = new WorkflowRuntimeOptions
        {
            Policy = AllowAll(),
            ToolInvoker = (_, _) =>
            {
                calls++;
                return Task.FromResult("result-text");
            }
        };

        var result = await Run(workflow, options);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, calls);
        Assert.Equal("result-text", JsonDocument.Parse(result.Output!).RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Tool_AskWithoutConfirm_DoesNotInvokeBackend()
    {
        var calls = 0;
        var workflow = Definition("""
            export const meta = { name: 'ask' };
            const raw = await tool('search', { query: 'x' });
            ({ raw });
            """);

        var options = new WorkflowRuntimeOptions
        {
            // 默认策略即 Ask，且 Confirm 为 null：必须 fail closed。
            Policy = null,
            ToolInvoker = (_, _) =>
            {
                calls++;
                return Task.FromResult("should-not-run");
            }
        };

        var result = await Run(workflow, options);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, calls);
        Assert.Contains("not approved", JsonDocument.Parse(result.Output!).RootElement.GetProperty("raw").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_AskWithApprovingConfirm_InvokesBackend()
    {
        var calls = 0;
        var workflow = Definition("""
            export const meta = { name: 'confirm' };
            const value = await tool('search', { query: 'x' });
            ({ value });
            """);

        var options = new WorkflowRuntimeOptions
        {
            Confirm = _ => Task.FromResult(true),
            ToolInvoker = (_, _) =>
            {
                calls++;
                return Task.FromResult("approved-result");
            }
        };

        var result = await Run(workflow, options);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, calls);
        Assert.Equal("approved-result", JsonDocument.Parse(result.Output!).RootElement.GetProperty("value").GetString());
    }

    // ---------- command adapter ----------

    [Fact]
    public void Adapter_RegistersOneCommandPerWorkflowAndListTool()
    {
        var runtime = new ExtensionRuntime();
        var extension = WorkflowCommandAdapter.CreateExtension(
            new[] { Definition("export const meta = { name: 'alpha' };\n1;", "alpha"), Definition("export const meta = { name: 'beta' };\n1;", "beta") },
            () => new WorkflowRuntimeOptions());

        runtime.Activate(extension);

        Assert.Equal(new[] { "alpha", "beta" }, runtime.Commands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        var listTool = Assert.Single(runtime.Tools);
        Assert.Equal(WorkflowCommandAdapter.ListToolName, listTool.Definition.Name);
        Assert.Equal(ToolExposure.ModelOnly, listTool.Exposure);
        Assert.Contains("alpha", listTool.Definition.Description!);
        Assert.All(runtime.Commands, command => Assert.Equal("workflows", command.ExtensionName));
    }

    [Fact]
    public async Task Adapter_RendersArgumentsIntoScriptAndDisplaysOutput()
    {
        var workflow = WorkflowLoader.Load(
            Write("args.js", """
                export const meta = { name: 'args', description: 'render args', parameters: ['target'], arguments: 'target=$target; rest=$ARGUMENTS' };
                ({ text: arguments });
                """),
            "workflows",
            out _)!;

        var runtime = new ExtensionRuntime();
        runtime.Activate(WorkflowCommandAdapter.CreateExtension(new[] { workflow }, () => new WorkflowRuntimeOptions()));

        var command = Assert.Single(runtime.Commands);
        var result = await command.Handler(
            Context(runtime),
            CommandArguments.Parse("src/AiTools.Extensions"),
            CancellationToken.None);

        Assert.Equal(CommandResultKind.Display, result.Kind);
        var text = JsonDocument.Parse(result.UserMessage!).RootElement.GetProperty("text").GetString();
        Assert.Equal("target=src/AiTools.Extensions; rest=src/AiTools.Extensions", text);
    }

    [Fact]
    public void Adapter_RendersPositionalArgumentsAndAppendsUnusedInput()
    {
        var workflow = WorkflowLoader.Load(
            Write("pos.js", """
                export const meta = { name: 'pos', arguments: 'first=$0 second=$1 all=$ARGUMENTS' };
                ({ text: arguments });
                """),
            "workflows",
            out _)!;

        var rendered = WorkflowCommandAdapter.RenderArguments(workflow.ArgumentTemplate, CommandArguments.Parse("alpha beta"), workflow.Parameters);

        Assert.Equal("first=alpha second=beta all=alpha beta", rendered);
    }

    [Fact]
    public async Task Adapter_FailurePathDisplaysErrorAndNotifies()
    {
        var workflow = WorkflowLoader.Load(
            Write("fail.js", """
                export const meta = { name: 'failing' };
                throw new Error('adapter-boom');
                """),
            "workflows",
            out _)!;

        var runtime = new ExtensionRuntime();
        runtime.Activate(WorkflowCommandAdapter.CreateExtension(new[] { workflow }, () => new WorkflowRuntimeOptions()));

        var notices = new List<(string Message, ExtensionNoticeKind Kind)>();
        var context = new CommandContext
        {
            Runtime = runtime,
            State = runtime.State,
            WorkingDirectory = _root,
            Notify = (message, kind) => notices.Add((message, kind))
        };

        var command = Assert.Single(runtime.Commands);
        var result = await command.Handler(context, CommandArguments.Parse(""), CancellationToken.None);

        Assert.Equal(CommandResultKind.Display, result.Kind);
        Assert.Contains("adapter-boom", result.UserMessage!);
        Assert.Contains(notices, notice => notice.Kind == ExtensionNoticeKind.Warning && notice.Message.Contains("adapter-boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adapter_ListToolReportsWorkflows()
    {
        var runtime = new ExtensionRuntime();
        var workflow = WorkflowLoader.Load(
            Write("list.js", "export const meta = { name: 'one', description: 'first' };\n1;"),
            "workflows",
            out _)!;
        runtime.Activate(WorkflowCommandAdapter.CreateExtension(new[] { workflow }, () => new WorkflowRuntimeOptions()));

        var tool = Assert.Single(runtime.Tools);
        var text = await tool.Execute(
            new ToolCall("1", WorkflowCommandAdapter.ListToolName, "{}"),
            new ToolContext { Runtime = runtime, State = runtime.State },
            CancellationToken.None);

        var root = JsonDocument.Parse(text).RootElement;
        var item = Assert.Single(root.GetProperty("workflows").EnumerateArray());
        Assert.Equal("one", item.GetProperty("name").GetString());
        Assert.Equal("first", item.GetProperty("description").GetString());
    }

    // ---------- helpers ----------

    private static CommandContext Context(ExtensionRuntime runtime)
        => new() { Runtime = runtime, State = runtime.State, WorkingDirectory = Environment.CurrentDirectory };

    private async Task<WorkflowRunResult> Run(WorkflowDefinition definition, WorkflowRuntimeOptions options, string? arguments = null)
    {
        options.WorkingDirectory ??= _root;
        var runtime = new WorkflowRuntime(options);
        return await runtime.RunAsync(definition, arguments);
    }

    private static WorkflowDefinition Definition(string script, string name = "inline") => new()
    {
        Name = name,
        Description = "inline",
        SourcePath = "inline.js",
        Script = script,
        PluginName = "workflows"
    };

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }


    private static PermissionPolicy AllowAll()
        => new(new[] { new PermissionRule { Tool = "*", Decision = PermissionDecision.Allow, Source = "test" } });

    /// <summary>尽最大努力创建目录链接；平台或权限不允许时返回 false（不视为失败）。</summary>
    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            // Windows 未开启开发者模式且非管理员时符号链接会被拒绝；退回 junction（需要目录且不跨卷）。
        }

        try
        {
            if (Path.GetPathRoot(linkPath) != Path.GetPathRoot(targetPath))
            {
                return false;
            }

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            process?.WaitForExit(10000);
            return Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        private readonly string _reply;

        public CapturingChatClient(string reply) => _reply = reply;

        public List<ChatRequest> Requests { get; } = new();

        public string ProviderName => "capturing";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatResponse
            {
                Message = ChatMessage.Assistant(_reply),
                FinishReason = ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ChatStreamUpdate { TextDelta = _reply, FinishReason = ChatFinishReason.Stop };
        }
    }

    private sealed class FailingChatClient : IChatClient
    {
        public string ProviderName => "failing";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("provider is offline");

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatStreamUpdate { FinishReason = ChatFinishReason.Error };
        }
    }
}
