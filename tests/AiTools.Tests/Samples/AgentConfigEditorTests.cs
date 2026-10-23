using System.Text.Json;
using AiTools.Samples.McpAgents;
using Xunit;

namespace AiTools.Tests.Samples;

public sealed class AgentConfigEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aitools-agents-" + Guid.NewGuid().ToString("N"));
    private readonly AgentConfigEditor _editor;

    public AgentConfigEditorTests()
    {
        Directory.CreateDirectory(_root);
        _editor = new AgentConfigEditor(Path.Combine(_root, "backups"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AgentAdapter Adapter(AgentKind kind, string name = "config.json")
    {
        var path = Path.Combine(_root, name);
        return new AgentAdapter("test-" + kind, "Test " + kind, null, null, kind, path);
    }

    private static McpPreset Preset(string id) => McpPresetCatalog.Find(id)!;

    // ---------- JSON（mcpServers） ----------

    [Fact]
    public void Json_InstallCreatesFileAndEntry()
    {
        var adapter = Adapter(AgentKind.JsonMcpServers);

        var result = _editor.Install(adapter, adapter.ConfigPath, Preset("filesystem"));

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(File.ReadAllText(adapter.ConfigPath));
        var server = document.RootElement.GetProperty("mcpServers").GetProperty("filesystem");
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        Assert.Equal("npx", server.GetProperty("command").GetString());
    }

    [Fact]
    public void Json_InstallPreservesExistingContent()
    {
        var adapter = Adapter(AgentKind.JsonMcpServers);
        File.WriteAllText(adapter.ConfigPath, """
            {
              "numStartups": 42,
              "theme": "dark",
              "mcpServers": {
                "existing": { "type": "stdio", "command": "old" }
              }
            }
            """);

        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));

        using var document = JsonDocument.Parse(File.ReadAllText(adapter.ConfigPath));
        Assert.Equal(42, document.RootElement.GetProperty("numStartups").GetInt32());
        Assert.Equal("dark", document.RootElement.GetProperty("theme").GetString());
        var servers = document.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("existing", out _));
        Assert.True(servers.TryGetProperty("memory", out _));
    }

    [Fact]
    public void Json_InstallIsIdempotent()
    {
        var adapter = Adapter(AgentKind.JsonMcpServers);

        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));
        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));

        var state = _editor.Read(adapter, adapter.ConfigPath);
        Assert.Single(state.InstalledServers);
    }

    [Fact]
    public void Json_RemoveDeletesOnlyTarget()
    {
        var adapter = Adapter(AgentKind.JsonMcpServers);
        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));
        _editor.Install(adapter, adapter.ConfigPath, Preset("filesystem"));

        var result = _editor.Remove(adapter, adapter.ConfigPath, "memory");

        Assert.True(result.Success);
        var state = _editor.Read(adapter, adapter.ConfigPath);
        Assert.Equal(new[] { "filesystem" }, state.InstalledServers);
    }

    [Fact]
    public void Json_RemoveMissing_Fails()
    {
        var adapter = Adapter(AgentKind.JsonMcpServers);
        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));

        var result = _editor.Remove(adapter, adapter.ConfigPath, "missing");

        Assert.False(result.Success);
        Assert.Contains("没有名为", result.Message);
    }

    [Fact]
    public void Json_ReadReportsInstalledServers()
    {
        var adapter = Adapter(AgentKind.JsonMcpServers);
        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));
        _editor.Install(adapter, adapter.ConfigPath, Preset("playwright"));

        var state = _editor.Read(adapter, adapter.ConfigPath);

        Assert.True(state.ConfigExists);
        Assert.Equal(new[] { "memory", "playwright" }, state.InstalledServers);
        Assert.True(state.Has("MEMORY"));
    }

    [Fact]
    public void Json_MissingFileReadsEmpty()
    {
        var adapter = Adapter(AgentKind.JsonMcpServers);

        var state = _editor.Read(adapter, adapter.ConfigPath);

        Assert.False(state.ConfigExists);
        Assert.Empty(state.InstalledServers);
    }

    [Fact]
    public void Json_CorruptFile_ReportsErrorInsteadOfThrowing()
    {
        var adapter = Adapter(AgentKind.JsonMcpServers);
        File.WriteAllText(adapter.ConfigPath, "{ broken json");

        var state = _editor.Read(adapter, adapter.ConfigPath);

        Assert.NotNull(state.Error);
    }

    [Fact]
    public void Install_CreatesBackup()
    {
        var adapter = Adapter(AgentKind.JsonMcpServers);
        File.WriteAllText(adapter.ConfigPath, "{\"mcpServers\":{}}");

        var result = _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));

        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath!));
    }

    // ---------- JSON（mcp） ----------

    [Fact]
    public void JsonMcpObject_UsesMcpProperty()
    {
        var adapter = Adapter(AgentKind.JsonMcpObject, "opencode.json");
        File.WriteAllText(adapter.ConfigPath, """{"$schema":"https://opencode.ai/config.json","mcp":{}}""");

        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));

        using var document = JsonDocument.Parse(File.ReadAllText(adapter.ConfigPath));
        Assert.Equal("https://opencode.ai/config.json", document.RootElement.GetProperty("$schema").GetString());
        Assert.True(document.RootElement.GetProperty("mcp").TryGetProperty("memory", out _));
        Assert.False(document.RootElement.TryGetProperty("mcpServers", out _));
    }

    // ---------- TOML ----------

    [Fact]
    public void Toml_InstallAppendsServerTable()
    {
        var adapter = Adapter(AgentKind.TomlMcpServers, "config.toml");
        File.WriteAllText(adapter.ConfigPath, """
            model = "gpt-6.1"
            approval_policy = "on-request"
            """);

        var result = _editor.Install(adapter, adapter.ConfigPath, Preset("filesystem"));

        Assert.True(result.Success);
        var text = File.ReadAllText(adapter.ConfigPath);
        Assert.Contains("model = \"gpt-6.1\"", text);
        Assert.Contains("[mcp_servers.filesystem]", text);
        Assert.Contains("command = \"npx\"", text);
        Assert.Contains("\"-y\"", text);
    }

    [Fact]
    public void Toml_InstallPreservesOtherMcpServers()
    {
        var adapter = Adapter(AgentKind.TomlMcpServers, "config.toml");
        File.WriteAllText(adapter.ConfigPath, """
            [mcp_servers.existing]
            command = "node"
            args = [ "server.js" ]

            [mcp_servers.existing.env]
            TOKEN = "abc"
            """);

        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));

        var state = _editor.Read(adapter, adapter.ConfigPath);
        Assert.Equal(new[] { "existing", "memory" }, state.InstalledServers);
        Assert.Contains("TOKEN = \"abc\"", File.ReadAllText(adapter.ConfigPath));
    }

    [Fact]
    public void Toml_RemoveDeletesTableAndNestedEnv()
    {
        var adapter = Adapter(AgentKind.TomlMcpServers, "config.toml");
        File.WriteAllText(adapter.ConfigPath, """
            model = "x"

            [mcp_servers.alpha]
            command = "a"

            [mcp_servers.alpha.env]
            K = "v"

            [mcp_servers.beta]
            command = "b"
            """);

        var result = _editor.Remove(adapter, adapter.ConfigPath, "alpha");

        Assert.True(result.Success);
        var text = File.ReadAllText(adapter.ConfigPath);
        Assert.DoesNotContain("[mcp_servers.alpha]", text);
        Assert.DoesNotContain("K = \"v\"", text);
        Assert.Contains("[mcp_servers.beta]", text);
        Assert.Contains("model = \"x\"", text);
    }

    [Fact]
    public void Toml_InstallWithEnvironment_WritesEnvTable()
    {
        var adapter = Adapter(AgentKind.TomlMcpServers, "config.toml");
        File.WriteAllText(adapter.ConfigPath, "model = \"x\"\n");
        var env = new Dictionary<string, string> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "ghp_x" };

        _editor.Install(adapter, adapter.ConfigPath, Preset("github"), env);

        var text = File.ReadAllText(adapter.ConfigPath);
        Assert.Contains("[mcp_servers.github.env]", text);
        Assert.Contains("GITHUB_PERSONAL_ACCESS_TOKEN = \"ghp_x\"", text);
    }

    [Fact]
    public void Toml_InstallIsIdempotent()
    {
        var adapter = Adapter(AgentKind.TomlMcpServers, "config.toml");
        File.WriteAllText(adapter.ConfigPath, "model = \"x\"\n");

        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));
        _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));

        var text = File.ReadAllText(adapter.ConfigPath);
        var occurrences = text.Split(new[] { "[mcp_servers.memory]" }, StringSplitOptions.None).Length - 1;
        Assert.Equal(1, occurrences);
    }

    // ---------- YAML ----------

    [Fact]
    public void Yaml_InstallAddsServer()
    {
        var adapter = Adapter(AgentKind.YamlMcpServers, "config.yaml");
        File.WriteAllText(adapter.ConfigPath, """
            name: hermes
            mcp_servers: {}
            """);

        var result = _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));

        Assert.True(result.Success);
        var text = File.ReadAllText(adapter.ConfigPath);
        Assert.Contains("mcp_servers:", text);
        Assert.Contains("memory:", text);
        Assert.Contains("command: npx", text);
    }

    [Fact]
    public void Yaml_ReadListsServers()
    {
        var adapter = Adapter(AgentKind.YamlMcpServers, "config.yaml");
        File.WriteAllText(adapter.ConfigPath, """
            mcp_servers:
              alpha:
                command: node
              beta:
                command: npx
            """);

        var state = _editor.Read(adapter, adapter.ConfigPath);

        Assert.Equal(new[] { "alpha", "beta" }, state.InstalledServers);
    }

    [Fact]
    public void Yaml_RemoveDeletesServer()
    {
        var adapter = Adapter(AgentKind.YamlMcpServers, "config.yaml");
        File.WriteAllText(adapter.ConfigPath, """
            mcp_servers:
              alpha:
                command: node
              beta:
                command: npx
            """);

        var result = _editor.Remove(adapter, adapter.ConfigPath, "alpha");

        Assert.True(result.Success);
        var state = _editor.Read(adapter, adapter.ConfigPath);
        Assert.Equal(new[] { "beta" }, state.InstalledServers);
    }

    // ---------- 不支持 ----------

    [Fact]
    public void Unsupported_InstallAndRemoveFailGracefully()
    {
        var adapter = Adapter(AgentKind.Unsupported, "nope.json");

        var install = _editor.Install(adapter, adapter.ConfigPath, Preset("memory"));
        var remove = _editor.Remove(adapter, adapter.ConfigPath, "memory");

        Assert.False(install.Success);
        Assert.False(remove.Success);
        Assert.Contains("导出", install.Message);
    }
}
