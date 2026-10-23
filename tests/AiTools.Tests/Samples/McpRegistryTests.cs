using System.Diagnostics;
using System.Text.Json;
using AiTools.Samples.McpDiscovery;
using Xunit;

namespace AiTools.Tests.Samples;

/// <summary>
/// 所有直接操作 <see cref="McpRegistry"/> 静态路径覆盖的测试共享同一 collection，
/// 避免 xUnit 并行执行导致进程级路径互相覆盖。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class McpRegistryCollection
{
    public const string Name = "McpRegistry";
}

[Collection(McpRegistryCollection.Name)]
public sealed class McpRegistryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "aitools-registry-" + Guid.NewGuid().ToString("N"), "registry.json");

    public McpRegistryTests()
    {
        McpRegistry.OverridePath(_path);
    }

    public void Dispose()
    {
        McpRegistry.OverridePath(null);
        var dir = Path.GetDirectoryName(_path);
        if (dir != null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static McpRegistryEntry Entry(string id, int pid = 0) => new()
    {
        Id = id,
        Name = "Test " + id,
        Version = "1.2.3",
        Transport = McpTransportKinds.StreamableHttp,
        Endpoint = "http://127.0.0.1:5220/mcp",
        Pid = pid,
        ProcessStartedAt = DateTime.UtcNow,
        RegisteredAt = DateTime.UtcNow,
        LaunchCommand = "dotnet",
        LaunchArguments = { "server.dll", "--stdio" },
        WorkingDirectory = "C:\\server",
        Tools = { "echo", "get_time" },
        Metadata = new Dictionary<string, string> { ["env"] = "test" }
    };

    [Fact]
    public void ReadAll_WhenMissing_ReturnsEmpty()
    {
        Assert.Empty(McpRegistry.ReadAll());
    }

    [Fact]
    public void UpsertThenReadAll_RoundTripsAllFields()
    {
        McpRegistry.Upsert(Entry("a", pid: 123));

        var entries = McpRegistry.ReadAll();

        var entry = Assert.Single(entries);
        Assert.Equal("a", entry.Id);
        Assert.Equal("Test a", entry.Name);
        Assert.Equal("1.2.3", entry.Version);
        Assert.Equal("http://127.0.0.1:5220/mcp", entry.Endpoint);
        Assert.Equal(new[] { "echo", "get_time" }, entry.Tools);
        Assert.Equal(new[] { "server.dll", "--stdio" }, entry.LaunchArguments);
        Assert.Equal("test", entry.Metadata!["env"]);
    }

    [Fact]
    public void Upsert_WithSameId_ReplacesEntry()
    {
        McpRegistry.Upsert(Entry("a"));
        var updated = Entry("a");
        updated.Name = "Renamed";
        McpRegistry.Upsert(updated);

        var entry = Assert.Single(McpRegistry.ReadAll());
        Assert.Equal("Renamed", entry.Name);
    }

    [Fact]
    public void Upsert_MultipleEntries_KeepsAll()
    {
        McpRegistry.Upsert(Entry("a"));
        McpRegistry.Upsert(Entry("b"));

        Assert.Equal(new[] { "a", "b" }, McpRegistry.ReadAll().Select(e => e.Id).OrderBy(x => x));
    }

    [Fact]
    public void Remove_DeletesOnlyMatchingEntry()
    {
        McpRegistry.Upsert(Entry("a"));
        McpRegistry.Upsert(Entry("b"));

        Assert.True(McpRegistry.Remove("a"));

        var remaining = Assert.Single(McpRegistry.ReadAll());
        Assert.Equal("b", remaining.Id);
        Assert.False(McpRegistry.Remove("missing"));
    }

    [Fact]
    public void RemoveStale_RemovesDeadProcesses_KeepsLiveProcesses()
    {
        McpRegistry.Upsert(Entry("live", Environment.ProcessId));
        McpRegistry.Upsert(Entry("dead", pid: 999_999));

        var removed = McpRegistry.RemoveStale();

        Assert.Equal(1, removed);
        var remaining = Assert.Single(McpRegistry.ReadAll());
        Assert.Equal("live", remaining.Id);
    }

    [Fact]
    public void RemoveStale_KeepsProcessWithMatchingStartTime()
    {
        using var self = Process.GetCurrentProcess();
        var entry = Entry("live", Environment.ProcessId);
        entry.ProcessStartedAt = self.StartTime.ToUniversalTime();
        McpRegistry.Upsert(entry);

        Assert.Equal(0, McpRegistry.RemoveStale());
    }

    [Fact]
    public void RemoveStale_RemovesRecycledPid()
    {
        var entry = Entry("recycled", Environment.ProcessId);
        entry.ProcessStartedAt = DateTime.UtcNow.AddHours(-5);
        McpRegistry.Upsert(entry);

        Assert.Equal(1, McpRegistry.RemoveStale());
    }

    [Fact]
    public void ReadAll_CorruptFile_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ definitely not json");

        Assert.Empty(McpRegistry.ReadAll());
    }

    [Fact]
    public void Upsert_CorruptFile_RecoversAndBacksUp()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "garbage");

        McpRegistry.Upsert(Entry("a"));

        Assert.Single(McpRegistry.ReadAll());
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(_path)!, "*.corrupt-*"));
    }

    [Fact]
    public async Task ConcurrentWriteOperations_DoNotCorruptFile()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => McpRegistry.Upsert(Entry("p" + i))))
            .ToArray();

        await Task.WhenAll(tasks);

        var entries = McpRegistry.ReadAll();
        Assert.Equal(20, entries.Count);
        Assert.Equal(20, entries.Select(e => e.Id).Distinct().Count());

        // 文件必须是可解析的 JSON。
        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal(20, document.RootElement.GetProperty("Servers").GetArrayLength());
    }

    [Fact]
    public async Task ConcurrentReadDuringWrite_DoesNotThrow()
    {
        McpRegistry.Upsert(Entry("seed"));

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 15; i++)
            {
                McpRegistry.Upsert(Entry("w" + i));
            }
        });

        var failures = 0;
        while (!writer.IsCompleted)
        {
            try
            {
                McpRegistry.ReadAll();
            }
            catch (Exception)
            {
                failures++;
            }

            await Task.Yield();
        }

        await writer;
        Assert.Equal(0, failures);
    }

    [Fact]
    public void Clear_DeletesRegistryFile()
    {
        McpRegistry.Upsert(Entry("a"));
        Assert.True(File.Exists(_path));

        McpRegistry.Clear();

        Assert.False(File.Exists(_path));
        Assert.Empty(McpRegistry.ReadAll());
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var original = Entry("a");
        var copy = original.Clone();

        copy.Tools.Add("extra");
        copy.LaunchArguments[0] = "changed";
        copy.Metadata!["env"] = "changed";

        Assert.DoesNotContain("extra", original.Tools);
        Assert.Equal("server.dll", original.LaunchArguments[0]);
        Assert.Equal("test", original.Metadata!["env"]);
    }

    [Fact]
    public void IsProcessAlive_CurrentProcess_IsTrue()
    {
        Assert.True(McpRegistry.IsProcessAlive(Environment.ProcessId, DateTime.UtcNow.AddMinutes(-1)));
        Assert.False(McpRegistry.IsProcessAlive(0, default));
        Assert.False(McpRegistry.IsProcessAlive(-5, default));
    }

    [Fact]
    public void GetDisplayLabel_FallsBackToPid()
    {
        Assert.Equal("Name · PID 12", new McpRegistryEntry { Name = "Name", Pid = 12 }.GetDisplayLabel());
        Assert.Equal("MCP 服务 12", new McpRegistryEntry { Pid = 12 }.GetDisplayLabel());
    }
}
