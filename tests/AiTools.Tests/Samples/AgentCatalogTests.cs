using AiTools.Samples.McpAgents;
using Xunit;

namespace AiTools.Tests.Samples;

public sealed class AgentCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aitools-catalog-" + Guid.NewGuid().ToString("N"));

    public AgentCatalogTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CreateAdapters_ContainsAllRequestedAgents()
    {
        var ids = AgentCatalog.CreateAdapters(_root, null).Select(adapter => adapter.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var expected in new[]
                 {
                     "claude-code", "codex", "grok", "gemini", "kimi", "openclaw",
                     "opencode", "pi", "omp", "hermes", "dsh"
                 })
        {
            Assert.Contains(expected, ids);
        }
    }

    [Fact]
    public void CreateAdapters_UsesKnownFormats()
    {
        var adapters = AgentCatalog.CreateAdapters(_root, null).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(AgentKind.JsonMcpServers, adapters["claude-code"].Kind);
        Assert.Equal(AgentKind.TomlMcpServers, adapters["codex"].Kind);
        Assert.Equal(AgentKind.TomlMcpServers, adapters["grok"].Kind);
        Assert.Equal(AgentKind.JsonMcpServers, adapters["gemini"].Kind);
        Assert.Equal(AgentKind.JsonMcpObject, adapters["opencode"].Kind);
        Assert.Equal(AgentKind.JsonMcpServers, adapters["pi"].Kind);
        Assert.Equal(AgentKind.JsonMcpServers, adapters["omp"].Kind);
        Assert.Equal(AgentKind.YamlMcpServers, adapters["hermes"].Kind);
        Assert.Equal(AgentKind.Unsupported, adapters["dsh"].Kind);
    }

    [Fact]
    public void CreateAdapters_ResolvesExpectedPaths()
    {
        var adapters = AgentCatalog.CreateAdapters(_root, Path.Combine(_root, "local")).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(Path.Combine(_root, ".claude.json"), adapters["claude-code"].ConfigPath);
        Assert.Equal(Path.Combine(_root, ".codex", "config.toml"), adapters["codex"].ConfigPath);
        Assert.Equal(Path.Combine(_root, ".grok", "config.toml"), adapters["grok"].ConfigPath);
        Assert.Equal(Path.Combine(_root, ".gemini", "settings.json"), adapters["gemini"].ConfigPath);
        Assert.Equal(Path.Combine(_root, ".kimi", "mcp.json"), adapters["kimi"].ConfigPath);
        Assert.Equal(Path.Combine(_root, ".config", "opencode", "opencode.json"), adapters["opencode"].ConfigPath);
        Assert.Equal(Path.Combine(_root, ".pi", "agent", "mcp.json"), adapters["pi"].ConfigPath);
        Assert.Equal(Path.Combine(_root, ".omp", "agent", "mcp.json"), adapters["omp"].ConfigPath);
        Assert.Equal(Path.Combine(_root, ".hermes", "config.yaml"), adapters["hermes"].ConfigPath);
    }

    [Fact]
    public void CreateAdapters_PrefersHermesLocalAppDataWhenItExists()
    {
        var localAppData = Path.Combine(_root, "local");
        var hermesPath = Path.Combine(localAppData, "hermes", "config.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(hermesPath)!);
        File.WriteAllText(hermesPath, "mcp_servers: {}\n");

        var adapter = AgentCatalog.CreateAdapters(_root, localAppData).Single(a => a.Id == "hermes");

        Assert.Equal(hermesPath, adapter.ConfigPath);
    }

    [Fact]
    public void WithConfigPath_CreatesCopyWithNewPath()
    {
        var original = AgentCatalog.CreateAdapters(_root, null).First(a => a.Id == "claude-code");
        var custom = Path.Combine(_root, "custom", "claude.json");

        var copy = original.WithConfigPath(custom);

        Assert.Equal(custom, copy.ConfigPath);
        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Kind, copy.Kind);
        Assert.NotEqual(original.ConfigPath, copy.ConfigPath);
    }

    [Fact]
    public void ProbeAll_DetectsConfigAndCli()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude"), string.Empty);
        var config = Path.Combine(_root, ".claude.json");
        File.WriteAllText(config, "{\"mcpServers\":{}}");

        var adapters = AgentCatalog.CreateAdapters(_root, null);
        var probes = new AgentProbeService(new[] { bin }).ProbeAll(adapters);
        var claude = probes.Single(p => p.Adapter.Id == "claude-code");

        Assert.True(claude.CliInstalled);
        Assert.True(claude.ConfigExists);
        Assert.True(claude.Managed);
        Assert.Equal("已配置 · 0 个 MCP", claude.GetStatusLabel(0));
    }

    [Fact]
    public void ProbeAll_UnmanagedAgentReportsExportHint()
    {
        var dsh = new AgentProbeService(Array.Empty<string>())
            .ProbeAll(AgentCatalog.CreateAdapters(_root, null))
            .Single(p => p.Adapter.Id == "dsh");

        Assert.False(dsh.Managed);
        Assert.Contains("导出", dsh.GetStatusLabel(0));
    }

    [Fact]
    public void ProbeAll_MissingAgentReportsNotDetected()
    {
        var kimi = new AgentProbeService(Array.Empty<string>())
            .ProbeAll(AgentCatalog.CreateAdapters(_root, null))
            .Single(p => p.Adapter.Id == "kimi");

        Assert.False(kimi.CliInstalled);
        Assert.False(kimi.ConfigExists);
        Assert.Equal("未检测到", kimi.GetStatusLabel(0));
    }

    [Fact]
    public void IsCliInstalled_RecognizesExeCmdBat()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "codex.exe"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "gemini.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(bin, "grok.bat"), string.Empty);
        var probe = new AgentProbeService(new[] { bin });

        Assert.True(probe.IsCliInstalled("codex"));
        Assert.True(probe.IsCliInstalled("gemini"));
        Assert.True(probe.IsCliInstalled("grok"));
        Assert.False(probe.IsCliInstalled("missing"));
        Assert.False(probe.IsCliInstalled(null));
    }
}

/// <summary>
/// 针对开发机上观察到的真实配置结构进行的只读 regression 测试。
/// 每个测试都会在临时目录下写入一份代表性副本，绝不触碰真实文件。
/// </summary>
public sealed class RealConfigShapeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aitools-shapes-" + Guid.NewGuid().ToString("N"));
    private readonly AgentConfigEditor _editor;

    public RealConfigShapeTests()
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

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ClaudeCode_RealShapeRoundTrips()
    {
        var path = Write("claude.json", """
            {
              "numStartups": 152,
              "installMethod": "native",
              "mcpServers": {
                "rea": {
                  "type": "stdio",
                  "command": "node",
                  "args": ["rea.mjs", "mcp"],
                  "env": { "JAVA_HOME": "C:\\Java" }
                }
              }
            }
            """);
        var adapter = new AgentAdapter("claude-code", "Claude Code", "claude", null, AgentKind.JsonMcpServers, path);

        _editor.Install(adapter, path, McpPresetCatalog.Find("filesystem")!);
        var state = _editor.Read(adapter, path);

        Assert.Contains("filesystem", state.InstalledServers);
        Assert.Contains("rea", state.InstalledServers);
        var text = File.ReadAllText(path);
        Assert.Contains("\"numStartups\": 152", text);
        Assert.Contains("JAVA_HOME", text);
    }

    [Fact]
    public void Codex_RealShapeRoundTrips()
    {
        var path = Write("codex/config.toml", """
            model_provider = "custom"
            model = "gpt-6.1"

            [agents]
            max_threads = 64

            [mcp_servers.Playwright]
            command = "cmd"
            args = ["/c", "npx", "-y", "@playwright/mcp@latest"]

            [mcp_servers.node_repl]
            command = "node"

            [mcp_servers.node_repl.env]
            NODE_OPTIONS = "--no-warnings"
            """);
        var adapter = new AgentAdapter("codex", "Codex", "codex", null, AgentKind.TomlMcpServers, path);

        Assert.Equal(new[] { "node_repl", "Playwright" }, _editor.Read(adapter, path).InstalledServers);

        _editor.Install(adapter, path, McpPresetCatalog.Find("memory")!);
        Assert.Contains("memory", _editor.Read(adapter, path).InstalledServers);

        _editor.Remove(adapter, path, "Playwright");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("[mcp_servers.Playwright]", text);
        Assert.Contains("[agents]", text);
        Assert.Contains("model = \"gpt-6.1\"", text);
        Assert.Contains("[mcp_servers.node_repl.env]", text);
    }

    [Fact]
    public void Grok_RealShapeRoundTrips()
    {
        var path = Write("grok/config.toml", """
            default_skills_installs_purged = true

            [mcp_servers.rea]
            command = "node"
            args = [ "rea.mjs", "mcp" ]
            startup_timeout_sec = 30

            [mcp_servers.rea.env]
            GHIDRA_INSTALL_DIR = "C:\\ghidra"
            """);
        var adapter = new AgentAdapter("grok", "Grok", "grok", null, AgentKind.TomlMcpServers, path);

        Assert.Equal(new[] { "rea" }, _editor.Read(adapter, path).InstalledServers);

        _editor.Install(adapter, path, McpPresetCatalog.Find("memory")!);
        var text = File.ReadAllText(path);
        Assert.Contains("default_skills_installs_purged = true", text);
        Assert.Contains("[mcp_servers.memory]", text);
    }

    [Fact]
    public void Gemini_RealShapeRoundTrips()
    {
        var path = Write("gemini/settings.json", """
            {
              "general": { "preferredEditor": "vscode" },
              "ide": { "enabled": true, "hasSeenNudge": true },
              "mcpServers": {},
              "security": { "auth": { "selectedType": "oauth-personal" } }
            }
            """);
        var adapter = new AgentAdapter("gemini", "Gemini", "gemini", null, AgentKind.JsonMcpServers, path);

        _editor.Install(adapter, path, McpPresetCatalog.Find("context7")!);

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("vscode", document.RootElement.GetProperty("general").GetProperty("preferredEditor").GetString());
        Assert.True(document.RootElement.GetProperty("mcpServers").TryGetProperty("context7", out _));
    }

    [Fact]
    public void OpenCode_RealShapeRoundTrips()
    {
        var path = Write("opencode/opencode.json", """
            {
              "$schema": "https://opencode.ai/config.json",
              "disabled_providers": ["NVIDIA"],
              "mcp": {},
              "model": "deepseek/deepseek-v4-flash"
            }
            """);
        var adapter = new AgentAdapter("opencode", "OpenCode", "opencode", null, AgentKind.JsonMcpObject, path);

        _editor.Install(adapter, path, McpPresetCatalog.Find("memory")!);

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("https://opencode.ai/config.json", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("deepseek/deepseek-v4-flash", document.RootElement.GetProperty("model").GetString());
        Assert.True(document.RootElement.GetProperty("mcp").TryGetProperty("memory", out _));
    }

    [Fact]
    public void OmpAndPi_RealShapeRoundTrips()
    {
        const string shape = """
            {
              "mcpServers": {
                "rea": {
                  "type": "stdio",
                  "command": "node",
                  "args": ["rea.mjs", "mcp"],
                  "env": { "JAVA_HOME": "C:\\Java" }
                }
              }
            }
            """;

        foreach (var (name, kind) in new[] { ("omp", AgentKind.JsonMcpServers), ("pi", AgentKind.JsonMcpServers) })
        {
            var path = Write($"{name}/mcp.json", shape);
            var adapter = new AgentAdapter(name, name, name, null, kind, path);

            _editor.Install(adapter, path, McpPresetCatalog.Find("fetch")!);

            var state = _editor.Read(adapter, path);
            Assert.Contains("fetch", state.InstalledServers);
            Assert.Contains("rea", state.InstalledServers);
        }
    }

    [Fact]
    public void Hermes_RealShapeRoundTrips()
    {
        var path = Write("hermes/config.yaml", """
            model: hermes-3
            ui:
              theme: dark
            mcp_servers: {}
            """);
        var adapter = new AgentAdapter("hermes", "Hermes", "hermes", null, AgentKind.YamlMcpServers, path);

        _editor.Install(adapter, path, McpPresetCatalog.Find("memory")!);

        var text = File.ReadAllText(path);
        Assert.Contains("model: hermes-3", text);
        Assert.Contains("theme: dark", text);
        Assert.Contains("memory:", text);
        Assert.Contains("memory", _editor.Read(adapter, path).InstalledServers);
    }
}
