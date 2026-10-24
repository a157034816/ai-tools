using System.Net;
using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Core.Sse;
using AiTools.Extensions;
using AiTools.OpenAI;
using AiTools.Wpf;
using AiTools.Wpf.Configuration;
using AiTools.Wpf.Extensions;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>记录回调的观察器，用于验证 provider 级钩子的接线。</summary>
internal sealed class RecordingObserver : IAiHttpObserver
{
    public List<string> Headers { get; } = new();
    public List<string> Bodies { get; } = new();
    public List<int> StatusCodes { get; } = new();
    public List<string> StreamEvents { get; } = new();
    public Func<AiRequestContext, string, string?>? RewriteBody { get; set; }
    public string? BlockReason { get; set; }

    public void OnBeforeHeaders(AiRequestContext context, IDictionary<string, string> headers)
    {
        Headers.Add(context.ProviderName);
        headers["X-Probe"] = "1";
    }

    public string? OnBeforeRequest(AiRequestContext context, string bodyJson)
    {
        Bodies.Add(bodyJson);
        if (BlockReason != null)
        {
            throw new AiRequestBlockedException(BlockReason);
        }

        return RewriteBody?.Invoke(context, bodyJson);
    }

    public void OnAfterResponse(AiRequestContext context, HttpResponseMessage response, TimeSpan duration)
        => StatusCodes.Add((int)response.StatusCode);

    public void OnStreamEvent(AiRequestContext context, string? eventType, string data)
        => StreamEvents.Add(data);
}

public sealed class HttpObserverTests
{
    private static HttpResponseMessage Ok(string body = "{\"ok\":true}")
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task SendObservedAsync_InvokesHeaderAndBodyHooks()
    {
        var observer = new RecordingObserver();
        var handler = new ScriptedHttpHandler(_ => Ok());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };
        var options = new AiHttpOptions { Observer = observer };

        using var response = await http.SendObservedWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "chat") { Content = new StringContent("{\"a\":1}") },
            new AiRequestContext { ProviderName = "probe", Operation = "chat", Model = "m" },
            options,
            CancellationToken.None);

        Assert.Single(observer.Headers);
        Assert.Equal("{\"a\":1}", Assert.Single(observer.Bodies));
        Assert.Equal(200, Assert.Single(observer.StatusCodes));
        Assert.Contains("X-Probe", handler.LastRequest!.Headers.Select(header => header.Key));
    }

    [Fact]
    public async Task SendObservedAsync_AppliesBodyRewrite()
    {
        var observer = new RecordingObserver { RewriteBody = (_, _) => "{\"rewritten\":true}" };
        var handler = new ScriptedHttpHandler(_ => Ok());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };
        var options = new AiHttpOptions { Observer = observer };

        using var response = await http.SendObservedWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "chat") { Content = new StringContent("{}") },
            new AiRequestContext { ProviderName = "probe" },
            options,
            CancellationToken.None);

        Assert.Equal("{\"rewritten\":true}", handler.LastBody);
    }

    [Fact]
    public async Task SendObservedAsync_PropagatesBlock()
    {
        var observer = new RecordingObserver { BlockReason = "blocked by policy" };
        var handler = new ScriptedHttpHandler(_ => Ok());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/") };
        var options = new AiHttpOptions { Observer = observer };

        var ex = await Assert.ThrowsAsync<AiRequestBlockedException>(async () =>
            await http.SendObservedWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "chat") { Content = new StringContent("{}") },
                new AiRequestContext { ProviderName = "probe" },
                options,
                CancellationToken.None).ConfigureAwait(false));

        Assert.Contains("policy", ex.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ParsedObservedSse_ReportsEvents()
    {
        var observer = new RecordingObserver();
        var context = new AiRequestContext { ProviderName = "probe" };
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("data: {\"a\":1}\n\ndata: [DONE]\n\n"));

        var events = new List<string>();
        await foreach (var evt in ObservedSseParser.ParseObservedAsync(stream, observer, context))
        {
            events.Add(evt.Data);
        }

        Assert.Equal(2, events.Count);
        Assert.Equal(2, observer.StreamEvents.Count);
        Assert.Contains("{\"a\":1}", observer.StreamEvents);
    }

    [Fact]
    public async Task OpenAiClient_UsesObserverWhenConfigured()
    {
        var observer = new RecordingObserver();
        var handler = new ScriptedHttpHandler(_ => Ok("""
            {"id":"x","model":"gpt","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}
            """));

        var client = new OpenAiChatClient(
            new OpenAiOptions
            {
                ApiKey = "k",
                BaseAddress = new Uri("https://example.com/v1/"),
                Observer = observer
            },
            new HttpClient(handler));

        await client.CompleteAsync(ChatRequest.FromUser("gpt-4.1", "hello"));

        Assert.Equal("openai", Assert.Single(observer.Headers));
        Assert.Contains("\"hello\"", Assert.Single(observer.Bodies));
        Assert.Equal(200, Assert.Single(observer.StatusCodes));
    }
}

public sealed class ExtensionAdvancedApiTests
{
    private static CommandContext Context(ExtensionRuntime runtime)
        => new() { Runtime = runtime, State = runtime.State, WorkingDirectory = Environment.CurrentDirectory };

    [Fact]
    public void Provider_AndVirtualModel_Registration()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("router", api =>
        {
            api.RegisterProvider(new ExtensionProviderRegistration
            {
                Name = "router-local",
                Protocol = "openai",
                BaseAddress = new Uri("http://localhost:1234/v1"),
                ApiKey = "k",
                DefaultModel = "m1"
            });
            api.RegisterVirtualModel(new VirtualModelRegistration
            {
                VirtualModelName = "smart",
                Provider = "router-local",
                TargetModel = "m1",
                Description = "routes to local"
            });
        }));

        Assert.Equal("router-local", Assert.Single(runtime.Providers).Name);
        var virtualModel = runtime.ResolveVirtualModel("smart");
        Assert.NotNull(virtualModel);
        Assert.Equal("m1", virtualModel!.TargetModel);
        Assert.Null(runtime.ResolveVirtualModel("unknown"));
    }

    [Fact]
    public void VirtualModel_LaterRegistrationWins()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("a", api => api.RegisterVirtualModel(new VirtualModelRegistration
        {
            VirtualModelName = "v",
            TargetModel = "first"
        })));
        runtime.Activate(new ProbeExtension("b", api => api.RegisterVirtualModel(new VirtualModelRegistration
        {
            VirtualModelName = "v",
            TargetModel = "second"
        })));

        Assert.Equal("second", runtime.ResolveVirtualModel("v")!.TargetModel);
    }

    [Fact]
    public async Task McpServer_RegistrationIsScopedAndRemovable()
    {
        var runtime = new ExtensionRuntime();
        Func<Task>? unregister = null;

        runtime.Activate(new ProbeExtension("mcp-ext", api =>
        {
            unregister = api.RegisterMcpServer(new PluginMcpServer
            {
                Name = "db",
                Transport = PluginMcpTransportKind.Stdio,
                Command = "node"
            });
        }));

        var server = Assert.Single(runtime.McpServers);
        Assert.Equal("mcp-ext:db", server.QualifiedName);
        Assert.Equal("mcp-ext", server.PluginName);

        await unregister!();
        Assert.Empty(runtime.McpServers);
    }

    [Fact]
    public void ToolRenderer_FirstNonNullWins()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("a", api => api.RegisterToolRenderer(ctx =>
            ctx.ToolName == "x" ? null : "A:" + ctx.ToolName)));
        runtime.Activate(new ProbeExtension("b", api => api.RegisterToolRenderer(ctx => "B:" + ctx.ToolName)));

        // A 对 "x" 返回 null，因此交给 B；这是 Pi 的 next() ?? mine 语义。
        Assert.Equal("B:x", runtime.RenderTool(new ToolRenderContext { ToolName = "x" }));
        Assert.Equal("A:y", runtime.RenderTool(new ToolRenderContext { ToolName = "y" }));
    }

    [Fact]
    public void ToolRenderer_FailureFallsThrough()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("a", api => api.RegisterToolRenderer(_ => throw new InvalidOperationException("bad"))));
        runtime.Activate(new ProbeExtension("b", api => api.RegisterToolRenderer(_ => "ok")));

        Assert.Equal("ok", runtime.RenderTool(new ToolRenderContext { ToolName = "t" }));
    }

    [Fact]
    public async Task AppendEntry_PersistsInState()
    {
        var runtime = new ExtensionRuntime();
        var session = ExtensionSession.Create(runtime);
        await session.StartAsync("s1");
        var context = session.CreateContext(null, null);

        context.AppendEntry("checkpoint", new { step = 3 });
        var stored = context.ReadEntry("checkpoint");

        Assert.NotNull(stored);
        Assert.Contains("checkpoint", runtime.State.ToJson());
    }

    [Fact]
    public async Task Reset_ClearsEverythingForReload()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("probe", api =>
        {
            api.RegisterCommand("c", "d", (_, _, _) => Task.FromResult(CommandResult.Noop));
            api.RegisterTool(ToolDefinition.Create("t"), (_, _, _) => Task.FromResult(""));
            api.RegisterProvider(new ExtensionProviderRegistration { Name = "p", Protocol = "openai" });
        }));

        runtime.Reset();

        Assert.Empty(runtime.Extensions);
        Assert.Empty(runtime.Commands);
        Assert.Empty(runtime.Tools);
        Assert.Empty(runtime.Providers);
    }

    [Fact]
    public async Task PipelineReload_ReactivatesExtensionsAndKeepsState()
    {
        var extension = new ProbeExtension("probe", api =>
            api.RegisterCommand("c", "d", (_, _, _) => Task.FromResult(CommandResult.Display("hi"))));

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = new IExtension[] { extension },
            WorkingDirectory = Environment.CurrentDirectory
        });
        await pipeline.StartAsync("s1");
        pipeline.Runtime.State.Set("probe", "kept", 42);

        var diagnostics = await pipeline.ReloadAsync();

        Assert.Empty(diagnostics);
        Assert.Single(pipeline.Runtime.Commands);
        Assert.Equal(42, pipeline.Runtime.State.Get<int>("probe", "kept"));
        Assert.True(pipeline.Session.IsStarted);
    }

    [Fact]
    public async Task ContextHooks_RewriteMessagesAndSystemPrompt()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("ctx", api =>
        {
            api.On<ContextEvent>(ExtensionEvent.Context, (e, _, _) =>
            {
                e.Messages = e.Messages.Where(message => message.GetText() != "drop me").ToArray();
                return Task.FromResult<ContextEvent?>(e);
            });
            api.On<ContextEvent>(ExtensionEvent.ContextWithSystem, (e, _, _) =>
            {
                e.SystemPrompt = (e.SystemPrompt ?? string.Empty) + "\n[annotated]";
                return Task.FromResult<ContextEvent?>(e);
            });
        }));

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Runtime = runtime,
            WorkingDirectory = Environment.CurrentDirectory
        });

        var messages = new[]
        {
            ChatMessage.User("keep me"),
            ChatMessage.User("drop me")
        };

        var result = await pipeline.ApplyContextHooksAsync(
            pipeline.Session.CreateContext(null, null),
            messages,
            "base prompt",
            "m");

        Assert.Single(result.Messages);
        Assert.Equal("keep me", result.Messages[0].GetText());
        Assert.Contains("[annotated]", result.SystemPrompt);
    }

    [Fact]
    public async Task BeforeProviderHeaders_MutatesHeaders()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("headers", api =>
            api.On<ProviderHeadersEvent>(ExtensionEvent.BeforeProviderHeaders, (e, _, _) =>
            {
                e.Headers["X-Extension"] = "yes";
                return Task.FromResult<ProviderHeadersEvent?>(e);
            })));

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Runtime = runtime,
            WorkingDirectory = Environment.CurrentDirectory
        });

        var observer = pipeline.CreateHttpObserver();
        var headers = new Dictionary<string, string>();
        observer.OnBeforeHeaders(new AiRequestContext { ProviderName = "p" }, headers);

        Assert.Equal("yes", headers["X-Extension"]);
    }
}

public sealed class VirtualModelRoutingTests
{
    private static ProviderConfig Provider() => new()
    {
        Id = "p",
        DisplayName = "P",
        BaseUrl = "https://example.com/v1",
        ApiKey = "k",
        Models = new List<ModelConfig> { new() { Id = "smart", Enabled = true } }
    };

    [Fact]
    public async Task VirtualModel_RewritesModelAndPreservesRequestShape()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("router", api => api.RegisterVirtualModel(new VirtualModelRegistration
        {
            VirtualModelName = "smart",
            TargetModel = "gpt-4.1-mini",
            Rewrite = request => new ChatRequest
            {
                Model = request.Model,
                Messages = request.Messages,
                Temperature = 0.1
            }
        })));

        var seen = new List<ChatRequest>();
        var factory = new CapturingProviderFactory(seen);
        var router = new ExtensionProviderRouter(runtime, factory);

        var client = router.CreateChatClient(Provider(), "smart");
        await client.CompleteAsync(ChatRequest.FromUser("smart", "hi"));

        var request = Assert.Single(seen);
        Assert.Equal("gpt-4.1-mini", request.Model);
        Assert.Equal(0.1, request.Temperature);
    }

    [Fact]
    public async Task VirtualModel_WithoutRewrite_KeepsOriginalFields()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("router", api => api.RegisterVirtualModel(new VirtualModelRegistration
        {
            VirtualModelName = "smart",
            TargetModel = "target-model"
        })));

        var seen = new List<ChatRequest>();
        var router = new ExtensionProviderRouter(runtime, new CapturingProviderFactory(seen));
        var client = router.CreateChatClient(Provider(), "smart");

        await client.CompleteAsync(new ChatRequest
        {
            Model = "smart",
            Messages = new[] { ChatMessage.User("x") },
            MaxOutputTokens = 256,
            SystemPrompt = "sys"
        });

        var request = Assert.Single(seen);
        Assert.Equal("target-model", request.Model);
        Assert.Equal("sys", request.SystemPrompt);
        Assert.Equal(256, request.MaxOutputTokens);
    }

    [Fact]
    public void Router_DelegatesToFallbackWhenNoRegistration()
    {
        var runtime = new ExtensionRuntime();
        var factory = new CapturingProviderFactory(new List<ChatRequest>());
        var router = new ExtensionProviderRouter(runtime, factory);

        var client = router.CreateChatClient(Provider(), "plain");

        Assert.NotNull(client);
        Assert.Single(factory.Created);
        Assert.Empty(runtime.Providers);
    }

    private sealed class CapturingProviderFactory : IProviderFactory
    {
        private readonly List<ChatRequest> _seen;

        public CapturingProviderFactory(List<ChatRequest> seen) => _seen = seen;

        public List<ProviderConfig> Created { get; } = new();

        public IChatClient CreateChatClient(ProviderConfig provider)
        {
            Created.Add(provider);
            return new CapturingClient(_seen);
        }

        private sealed class CapturingClient : IChatClient
        {
            private readonly List<ChatRequest> _seen;

            public CapturingClient(List<ChatRequest> seen) => _seen = seen;

            public string ProviderName => "capturing";

            public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            {
                _seen.Add(request);
                return Task.FromResult(new ChatResponse { Message = ChatMessage.Assistant("ok") });
            }

            public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
                ChatRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _seen.Add(request);
                await Task.CompletedTask;
                yield break;
            }
        }
    }
}

public sealed class WpfExtensionUiTests
{
    [Fact]
    public async Task NoApplication_DegradesToNonInteractive()
    {
        // 测试进程没有 WPF Application，对话框必须退化为安全默认值而不是抛异常。
        var ui = new WpfExtensionUi();
        var notified = new List<string>();
        var statuses = new List<string?>();
        var ui2 = new WpfExtensionUi((message, _) => notified.Add(message), status => statuses.Add(status));

        ui2.Notify("hello");
        ui2.SetStatus("busy");
        ui2.SetStatus(null);

        Assert.False(ui.HasDialogs);
        Assert.Equal(new[] { "hello" }, notified);
        Assert.Equal(new string?[] { "busy", null }, statuses);
        Assert.True(await ui.ConfirmAsync("t", "m", defaultValue: true));
        Assert.False(await ui.ConfirmAsync("t", "m"));
        Assert.Null(await ui.InputAsync("t"));
        Assert.Null(await ui.SelectAsync("t", new[] { "a", "b" }));
    }

    [Fact]
    public async Task NullUi_IsAllowedForExtensions()
    {
        var runtime = new ExtensionRuntime();
        string? observed = "unset";
        runtime.Activate(new ProbeExtension("ui", api =>
            api.On<InputEvent>(ExtensionEvent.Input, (e, ctx, _) =>
            {
                observed = ctx.Ui?.HasDialogs.ToString() ?? "null";
                return Task.FromResult<InputEvent?>(e);
            })));

        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Runtime = runtime,
            WorkingDirectory = Environment.CurrentDirectory
        });

        await pipeline.Runtime.DispatchAsync(
            ExtensionEvent.Input,
            new InputEvent { Text = "x" },
            pipeline.Session.CreateContext(null, null));

        Assert.Equal("null", observed);
    }
}

/// <summary>可控的 HTTP 处理器，记录最后一次请求。</summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    public int CallCount { get; private set; }

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        if (request.Content != null)
        {
            LastBody = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        return _responder(request);
    }
}
