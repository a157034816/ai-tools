using System.Text.Json;
using AiTools.Samples.McpAgents;
using Xunit;

namespace AiTools.Tests.Samples;

public sealed class McpEntryFormatterTests
{
    private static McpPreset StdioPreset() => McpPresetCatalog.Find("filesystem")!;

    [Fact]
    public void ToStandardEntry_BuildsStdioEntry()
    {
        var entry = McpEntryFormatter.ToStandardEntry(StdioPreset());

        Assert.Equal("stdio", entry["type"]);
        Assert.Equal("npx", entry["command"]);
        Assert.Equal(new[] { "-y", "@modelcontextprotocol/server-filesystem", "." }, (string[])entry["args"]!);
        Assert.False(entry.ContainsKey("env"));
    }

    [Fact]
    public void ToStandardEntry_MergesUserEnvironment()
    {
        var preset = McpPresetCatalog.Find("github")!;
        var env = new Dictionary<string, string> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "ghp_test" };

        var entry = McpEntryFormatter.ToStandardEntry(preset, env);

        var merged = Assert.IsType<Dictionary<string, string>>(entry["env"]);
        Assert.Equal("ghp_test", merged["GITHUB_PERSONAL_ACCESS_TOKEN"]);
    }

    [Fact]
    public void ToStandardEntry_BuildsHttpEntry()
    {
        var preset = McpPresetCatalog.Find("fetch-http")!;

        var entry = McpEntryFormatter.ToStandardEntry(preset);

        Assert.Equal("http", entry["type"]);
        Assert.Equal("https://api.example.com/mcp", entry["url"]);
        Assert.False(entry.ContainsKey("command"));
    }

    [Fact]
    public void ToTomlEntry_DropsTypeKey()
    {
        var entry = McpEntryFormatter.ToTomlEntry(StdioPreset());

        Assert.False(entry.ContainsKey("type"));
        Assert.Equal("npx", entry["command"]);
    }

    [Fact]
    public void BuildPortableJson_ProducesClaudeCompatibleShape()
    {
        var json = McpEntryFormatter.BuildPortableJson(StdioPreset());

        using var document = JsonDocument.Parse(json);
        var server = document.RootElement.GetProperty("mcpServers").GetProperty("filesystem");
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        Assert.Equal("npx", server.GetProperty("command").GetString());
    }

    [Fact]
    public void BuildPortableJson_MultiplePresets()
    {
        var json = McpEntryFormatter.BuildPortableJson(new[]
        {
            McpPresetCatalog.Find("memory")!,
            McpPresetCatalog.Find("playwright")!
        });

        using var document = JsonDocument.Parse(json);
        var servers = document.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("memory", out _));
        Assert.True(servers.TryGetProperty("playwright", out _));
    }

    [Fact]
    public void HasRequiredEnvironment_ChecksAllKeys()
    {
        var preset = McpPresetCatalog.Find("github")!;

        Assert.False(McpEntryFormatter.HasRequiredEnvironment(preset, null));
        Assert.False(McpEntryFormatter.HasRequiredEnvironment(preset, new Dictionary<string, string> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = " " }));
        Assert.True(McpEntryFormatter.HasRequiredEnvironment(preset, new Dictionary<string, string> { ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "token" }));

        // 不需要环境变量的预置项永远满足。
        Assert.True(McpEntryFormatter.HasRequiredEnvironment(McpPresetCatalog.Find("memory")!, null));
    }

    [Fact]
    public void GetMissingEnvironment_ListsKeys()
    {
        var preset = McpPresetCatalog.Find("github")!;

        var missing = McpEntryFormatter.GetMissingEnvironment(preset, null);

        Assert.Contains("GITHUB_PERSONAL_ACCESS_TOKEN", missing);
    }

    [Fact]
    public void Catalog_ContainsExpectedPresets()
    {
        Assert.NotNull(McpPresetCatalog.Find("filesystem"));
        Assert.NotNull(McpPresetCatalog.Find("Playwright")); // case-insensitive
        Assert.Null(McpPresetCatalog.Find("missing"));
        Assert.All(McpPresetCatalog.Presets, preset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Id));
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            if (preset.IsStdio)
            {
                Assert.False(string.IsNullOrWhiteSpace(preset.Command));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(preset.Url));
            }
        });
    }
}
