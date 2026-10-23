using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AiTools.Samples.McpDiscovery;
using Xunit;

namespace AiTools.Tests.Samples;

/// <summary>
/// 端到端测试：启动真实的 Tool Server 进程（HTTP 模式），验证 MCP 握手、工具发现、
/// 工具调用、注册表公告与退出清理。服务器不可用时（尚未构建）测试会跳过。
/// </summary>
[Collection(AiTools.Tests.Samples.McpRegistryCollection.Name)]
public sealed class McpToolServerEndToEndTests : IAsyncLifetime
{
    private const string ServerName = "E2E 工具服务";

    private readonly string _registryPath = Path.Combine(
        Path.GetTempPath(),
        "aitools-e2e-" + Guid.NewGuid().ToString("N"),
        "registry.json");

    private Process? _server;

    private static string? FindToolServerDll()
    {
        var baseDir = AppContext.BaseDirectory;
        // 从测试程序目录向上找到仓库根，再定位 Tool Server 的构建产物。
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AiTools.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            return null;
        }

        foreach (var configuration in new[] { "Debug", "Release" })
        {
            // 项目现已改造为 WPF（net8.0-windows）；保留 net8.0 以兼容旧构建产物。
            foreach (var targetFramework in new[] { "net8.0-windows", "net8.0" })
            {
                var candidate = Path.Combine(
                    dir.FullName, "samples", "AiTools.Samples.McpToolServer", "bin", configuration,
                    targetFramework, "AiTools.Samples.McpToolServer.dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public Task InitializeAsync()
    {
        var dll = FindToolServerDll();
        if (dll == null)
        {
            return Task.CompletedTask;
        }

        McpRegistry.OverridePath(_registryPath);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(dll);
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(ServerName);
        startInfo.Environment[McpRegistry.PathEnvironmentVariable] = _registryPath;

        _server = Process.Start(startInfo);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (_server != null)
        {
            try
            {
                if (!_server.HasExited)
                {
                    _server.Kill(entireProcessTree: true);
                    _server.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
            }

            _server.Dispose();
        }

        McpRegistry.OverridePath(null);
        var dir = Path.GetDirectoryName(_registryPath);
        if (dir != null && Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return Task.CompletedTask;
    }

    private async Task<McpRegistryEntry?> WaitForRegistryEntryAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // 只认本次测试启动的服务器，避免读到机器上其它真实验例的过期记录。
            var entry = McpRegistry.ReadAll()
                .FirstOrDefault(e => e.Name == ServerName && !string.IsNullOrEmpty(e.Endpoint));
            if (entry != null)
            {
                return entry;
            }

            await Task.Delay(100);
        }

        return null;
    }

    [Fact]
    public async Task Server_RegistersItself_AndServesMcp()
    {
        if (_server == null)
        {
            return; // Tool Server 尚未构建，跳过。
        }

        var entry = await WaitForRegistryEntryAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(entry);
        Assert.Equal(ServerName, entry!.Name);
        Assert.Equal(McpTransportKinds.StreamableHttp, entry.Transport);
        Assert.True(entry.Pid > 0);
        Assert.NotEmpty(entry.Tools);
        Assert.True(entry.IsProcessAlive());

        // 直接走 HTTP 验证 initialize + tools/list。
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var initResponse = await http.PostAsJsonAsync(entry.Endpoint, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "tests", version = "1.0.0" }
            }
        });

        Assert.True(initResponse.IsSuccessStatusCode);
        using (var initJson = JsonDocument.Parse(await initResponse.Content.ReadAsStringAsync()))
        {
            var result = initJson.RootElement.GetProperty("result");
            Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
            Assert.Equal("E2E 工具服务", result.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        }

        var listResponse = await http.PostAsJsonAsync(entry.Endpoint, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
            @params = new { }
        });

        Assert.True(listResponse.IsSuccessStatusCode);
        using (var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()))
        {
            var tools = listJson.RootElement.GetProperty("result").GetProperty("tools");
            Assert.True(tools.GetArrayLength() >= 6);

            var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
            Assert.Contains("echo", names);
            Assert.Contains("calculate", names);
            Assert.Contains("get_time", names);

            // 每个工具都必须有 inputSchema 与 readOnlyHint 注解。
            foreach (var tool in tools.EnumerateArray())
            {
                Assert.True(tool.TryGetProperty("inputSchema", out _));
                Assert.True(tool.TryGetProperty("annotations", out var annotations));
                Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
            }
        }
    }

    [Fact]
    public async Task Server_CallsToolsOverMcp()
    {
        if (_server == null)
        {
            return;
        }

        var entry = await WaitForRegistryEntryAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(entry);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var requestId = 10;

        async Task<JsonElement> CallToolAsync(string name, object arguments)
        {
            var response = await http.PostAsJsonAsync(entry!.Endpoint, new
            {
                jsonrpc = "2.0",
                id = requestId++,
                method = "tools/call",
                @params = new { name, arguments }
            });

            Assert.True(response.IsSuccessStatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("result").Clone();
        }

        var echo = await CallToolAsync("echo", new { text = "你好" });
        Assert.False(echo.GetProperty("isError").GetBoolean());
        Assert.Contains("你好", echo.GetProperty("content")[0].GetProperty("text").GetString());

        var calc = await CallToolAsync("calculate", new { a = 6, op = "*", b = 7 });
        Assert.False(calc.GetProperty("isError").GetBoolean());
        Assert.Contains("42", calc.GetProperty("content")[0].GetProperty("text").GetString());

        var badTool = await CallToolAsync("no_such_tool", new { });
        Assert.True(badTool.GetProperty("isError").GetBoolean());

        var badArgs = await CallToolAsync("calculate", new { a = 1, op = "*" });
        Assert.True(badArgs.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Server_RemovesRegistryEntryOnExit()
    {
        if (_server == null)
        {
            return;
        }

        var entry = await WaitForRegistryEntryAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(entry);

        _server!.Kill(entireProcessTree: true);
        _server.WaitForExit(5000);

        // 进程被强杀时 finally 可能来不及执行；RemoveStale 必须能清理。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            McpRegistry.RemoveStale();
            if (McpRegistry.ReadAll().Count == 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Empty(McpRegistry.ReadAll());
    }
}
