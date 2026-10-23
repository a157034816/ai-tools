using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AiTools.Abstractions;
using AiTools.Mcp;
using Xunit;

namespace AiTools.Tests.Providers;

/// <summary>内存中的假 MCP 服务器传输，用于端到端验证协议实现。</summary>
internal sealed class FakeMcpTransport : IMcpTransport
{
    private readonly Channel<JsonElement> _incoming = Channel.CreateUnbounded<JsonElement>();
    private readonly List<string> _methods = new();

    public bool IsConnected { get; private set; }

    public string? SessionId => "fake-session";

    public IReadOnlyList<string> ReceivedMethods => _methods;

    public Func<string, JsonElement?, JsonElement>? Handler { get; set; }

    public Action<JsonElement>? OnNotification { get; set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(JsonElement message, CancellationToken ct = default)
    {
        var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
        if (method != null)
        {
            _methods.Add(method);
        }

        if (message.TryGetProperty("id", out var id) && method != null)
        {
            var paramsElement = message.TryGetProperty("params", out var p) ? p : (JsonElement?)null;
            var result = Handler?.Invoke(method, paramsElement) ?? DefaultHandle(method, paramsElement);
            Respond(id, result);
        }
        else if (method != null)
        {
            // 来自客户端的通知（notifications/initialized）：无需处理。
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<JsonElement> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _incoming.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return default;
    }

    /// <summary>向客户端推送一条服务端通知。</summary>
    public void PushNotification(JsonElement notification) => _incoming.Writer.TryWrite(notification);

    private void Respond(JsonElement id, JsonElement result)
    {
        var response = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.GetInt64(),
            ["result"] = result
        });
        _incoming.Writer.TryWrite(response);
    }

    private static JsonElement DefaultHandle(string method, JsonElement? parameters)
    {
        return method switch
        {
            "initialize" => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new { tools = new { }, resources = new { }, prompts = new { } },
                ["serverInfo"] = new { name = "fake-server", version = "1.2.3" }
            }),
            "tools/list" => JsonSerializer.SerializeToElement(new
            {
                tools = new object[]
                {
                    new { name = "echo", description = "Echoes text", inputSchema = new { type = "object", properties = new { text = new { type = "string" } } } },
                    new { name = "add", description = "Adds numbers", inputSchema = new { type = "object", properties = new { a = new { type = "number" }, b = new { type = "number" } } } }
                }
            }),
            "tools/call" => BuildToolCallResult(parameters),
            "resources/list" => JsonSerializer.SerializeToElement(new
            {
                resources = new object[] { new { uri = "file:///a.txt", name = "A", mimeType = "text/plain" } }
            }),
            "resources/read" => JsonSerializer.SerializeToElement(new
            {
                contents = new object[] { new { uri = "file:///a.txt", mimeType = "text/plain", text = "file contents" } }
            }),
            "resources/templates/list" => JsonSerializer.SerializeToElement(new
            {
                resourceTemplates = new object[] { new { uriTemplate = "file:///{path}", name = "file" } }
            }),
            "prompts/list" => JsonSerializer.SerializeToElement(new
            {
                prompts = new object[] { new { name = "greet", description = "Greeting prompt", arguments = new object[] { new { name = "who", required = true } } } }
            }),
            "prompts/get" => JsonSerializer.SerializeToElement(new
            {
                description = "A greeting",
                messages = new object[] { new { role = "user", content = new { type = "text", text = "Hello, world" } } }
            }),
            "ping" => JsonSerializer.SerializeToElement(new { }),
            _ => JsonSerializer.SerializeToElement(new { })
        };
    }

    private static JsonElement BuildToolCallResult(JsonElement? parameters)
    {
        var name = parameters?.GetProperty("name").GetString();
        if (name == "echo")
        {
            var text = parameters!.Value.GetProperty("arguments").GetProperty("text").GetString();
            return JsonSerializer.SerializeToElement(new
            {
                content = new object[] { new { type = "text", text = "echo:" + text } },
                isError = false
            });
        }

        if (name == "fail")
        {
            return JsonSerializer.SerializeToElement(new
            {
                content = new object[] { new { type = "text", text = "boom" } },
                isError = true
            });
        }

        var arguments = parameters!.Value.GetProperty("arguments");
        var sum = arguments.GetProperty("a").GetDouble() + arguments.GetProperty("b").GetDouble();
        return JsonSerializer.SerializeToElement(new
        {
            content = new object[] { new { type = "text", text = sum.ToString("0") } },
            isError = false
        });
    }
}

public class McpClientTests
{
    private static async Task<(McpClient Client, FakeMcpTransport Transport)> CreateInitializedAsync()
    {
        var transport = new FakeMcpTransport();
        var client = new McpClient(transport);
        await client.InitializeAsync();
        return (client, transport);
    }

    [Fact]
    public async Task InitializeAsync_PerformsHandshakeAndCapturesServerInfo()
    {
        var (client, transport) = await CreateInitializedAsync();

        Assert.True(client.IsInitialized);
        Assert.Equal("fake-server", client.ServerName);
        Assert.Equal("1.2.3", client.ServerVersion);
        Assert.NotNull(client.ServerCapabilities);
        Assert.Contains("initialize", transport.ReceivedMethods);
        Assert.Contains("notifications/initialized", transport.ReceivedMethods);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListToolsAsync_MapsToolsAndAnnotations()
    {
        var (client, _) = await CreateInitializedAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal(2, tools.Count);
        Assert.Equal("echo", tools[0].Name);
        Assert.Equal("Echoes text", tools[0].Description);
        Assert.NotNull(tools[0].InputSchema);
        Assert.Equal("object", tools[0].InputSchema!.Value.GetProperty("type").GetString());

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CallToolAsync_ReturnsTextAndErrorFlag()
    {
        var (client, _) = await CreateInitializedAsync();

        var ok = await client.CallToolAsync("add", JsonDocument.Parse("""{"a":2,"b":3}""").RootElement.Clone());
        Assert.False(ok.IsError);
        Assert.Equal("5", ok.GetText());

        var fail = await client.CallToolAsync("fail");
        Assert.True(fail.IsError);
        Assert.Equal("boom", fail.GetText());

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ResourcesAndPrompts_AreListedAndRead()
    {
        var (client, _) = await CreateInitializedAsync();

        var resources = await client.ListResourcesAsync();
        Assert.Single(resources);
        Assert.Equal("file:///a.txt", resources[0].Uri);

        var read = await client.ReadResourceAsync("file:///a.txt");
        Assert.Equal("file contents", Assert.Single(read.Contents).Text);

        var templates = await client.ListResourceTemplatesAsync();
        Assert.Equal("file:///{path}", Assert.Single(templates).UriTemplate);

        var prompts = await client.ListPromptsAsync();
        Assert.Equal("greet", Assert.Single(prompts).Name);

        var prompt = await client.GetPromptAsync("greet", new Dictionary<string, string> { ["who"] = "world" });
        Assert.Equal("A greeting", prompt.Description);
        Assert.Equal("Hello, world", Assert.IsType<McpTextContent>(Assert.Single(prompt.Messages).Content).Text);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ToolBridge_ConvertsDefinitionsAndResolvesSanitizedNames()
    {
        var (client, _) = await CreateInitializedAsync();
        var tools = await client.ListToolsAsync();

        var definitions = McpToolBridge.ToToolDefinitions(tools);
        Assert.Equal(new[] { "echo", "add" }, definitions.Select(d => d.Name));

        var handler = McpToolBridge.CreateToolHandler(client, tools);
        var result = await handler(new ToolCall("c1", "add", """{"a":1,"b":2}"""), CancellationToken.None);
        Assert.Equal("3", result);

        await client.DisposeAsync();
    }

    [Fact]
    public void ToolBridge_SanitizesNamesAndHandlesCollisions()
    {
        Assert.Equal("tool", McpToolBridge.SanitizeName(string.Empty));
        Assert.Equal("a_b_c", McpToolBridge.SanitizeName("a b.c"));

        var tools = new[]
        {
            new McpTool { Name = "a b" },
            new McpTool { Name = "a_b" },
            new McpTool { Name = "a b" }
        };

        var definitions = McpToolBridge.ToToolDefinitions(tools);

        Assert.Equal("a_b", definitions[0].Name);
        Assert.Equal("a_b_2", definitions[1].Name);
        Assert.Equal("a_b_3", definitions[2].Name);
    }

    [Fact]
    public async Task ToolBridge_ThrowsForUnknownTool()
    {
        var (client, _) = await CreateInitializedAsync();
        var tools = await client.ListToolsAsync();
        var handler = McpToolBridge.CreateToolHandler(client, tools);

        await Assert.ThrowsAsync<McpException>(() =>
            handler(new ToolCall("c1", "missing", "{}"), CancellationToken.None));

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveServerNotificationsAsync_DeliversPushedNotifications()
    {
        var (client, transport) = await CreateInitializedAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = client.ReceiveServerNotificationsAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // 订阅建立后才推送，确保不会与初始化握手交错。
        var move = enumerator.MoveNextAsync();
        await Task.Delay(50);
        transport.PushNotification(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/message",
            ["params"] = new { level = "info", data = "hello" }
        }));

        Assert.True(await move);
        Assert.Equal("notifications/message", enumerator.Current.GetProperty("method").GetString());

        await enumerator.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task PingAsync_Succeeds()
    {
        var (client, transport) = await CreateInitializedAsync();

        await client.PingAsync();

        Assert.Contains("ping", transport.ReceivedMethods);
        await client.DisposeAsync();
    }
}
