using System.Text.Json;
using AiTools.Extensions;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>共享的临时插件目录，测试结束后删除。</summary>
public sealed class TempPluginTree : IDisposable
{
    public TempPluginTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "aitools-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>创建一个最小的插件：清单 + 若干命令文件。</summary>
    public string CreateCommandPlugin(string name, params (string Command, string Body)[] commands)
    {
        var pluginRoot = Path.Combine(Root, name);
        Write(Path.Combine(name, ".claude-plugin", "plugin.json"), $$"""
            {
              "name": "{{name}}",
              "description": "test plugin",
              "commands": ["./commands"]
            }
            """);

        foreach (var (command, body) in commands)
        {
            Write(Path.Combine(name, "commands", command + ".md"), body);
        }

        return pluginRoot;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

public sealed class CommandArgumentsTests
{
    [Fact]
    public void Parse_SplitsQuotedAndBareTokens()
    {
        var arguments = CommandArguments.Parse("  \"hello world\" second\tthird ");

        Assert.Equal("\"hello world\" second\tthird", arguments.Raw);
        Assert.Equal(new[] { "hello world", "second", "third" }, arguments.Positional);
        Assert.Equal("hello world", arguments.Get(0));
        Assert.Null(arguments.Get(5));
    }

    [Fact]
    public void Parse_EmptyInput_IsEmpty()
    {
        var arguments = CommandArguments.Parse(null);

        Assert.True(arguments.IsEmpty);
        Assert.Empty(arguments.Positional);
        Assert.Equal(string.Empty, arguments.Raw);
    }
}

public sealed class CommandTemplateRendererTests
{
    private static PluginCommand Command(
        string body,
        string pluginRoot = "/plugin",
        string? skillDirectory = null,
        params string[] argumentNames) => new()
    {
        Name = "demo:cmd",
        ShortName = "cmd",
        PluginName = "demo",
        Body = body,
        PluginRoot = pluginRoot,
        SkillDirectory = skillDirectory,
        ArgumentNames = argumentNames
    };

    [Fact]
    public void Render_ReplacesArgumentsPlaceholders()
    {
        var command = Command("Fix $ARGUMENTS[0] in $1 and $0, all: $ARGUMENTS");
        var context = new CommandRenderContext { Arguments = CommandArguments.Parse("alpha beta") };

        var text = CommandTemplateRenderer.Render(command.Body, command, context);

        Assert.Equal("Fix alpha in beta and alpha, all: alpha beta", text);
    }

    [Fact]
    public void Render_OutOfRangeIndexStaysLiteral()
    {
        var command = Command("A=$0 B=$5");
        var context = new CommandRenderContext { Arguments = CommandArguments.Parse("only") };

        Assert.Equal("A=only B=$5", CommandTemplateRenderer.Render(command.Body, command, context));
    }

    [Fact]
    public void Render_NamedArgumentsExpandToEmptyWhenMissing()
    {
        var command = Command("branch=$branch issue=$issue", argumentNames: new[] { "issue", "branch" });
        var context = new CommandRenderContext { Arguments = CommandArguments.Parse("42") };

        Assert.Equal("branch= issue=42", CommandTemplateRenderer.Render(command.Body, command, context));
    }

    [Fact]
    public void Render_ArgumentValuesAreNotRescanned()
    {
        var command = Command("use $0 here");
        var context = new CommandRenderContext { Arguments = CommandArguments.Parse("\"$1 from yesterday\"") };

        Assert.Equal("use $1 from yesterday here", CommandTemplateRenderer.Render(command.Body, command, context));
    }

    [Fact]
    public void Render_EscapedPlaceholderBecomesLiteral()
    {
        var command = Command(@"cost is \$1.00 for $0");
        var context = new CommandRenderContext { Arguments = CommandArguments.Parse("widget") };

        Assert.Equal("cost is $1.00 for widget", CommandTemplateRenderer.Render(command.Body, command, context));
    }

    [Fact]
    public void Render_ReplacesClaudeVariables()
    {
        var command = Command(
            "root=${CLAUDE_PLUGIN_ROOT} skill=${CLAUDE_SKILL_DIR} project=${CLAUDE_PROJECT_DIR} session=${CLAUDE_SESSION_ID} data=${CLAUDE_PLUGIN_DATA}",
            pluginRoot: @"C:\plugins\demo",
            skillDirectory: @"C:\plugins\demo\skills\demo");
        var context = new CommandRenderContext
        {
            ProjectDirectory = @"C:\work",
            SessionId = "abc123",
            PluginDataDirectory = @"C:\data\demo"
        };

        Assert.Equal(
            @"root=C:\plugins\demo skill=C:\plugins\demo\skills\demo project=C:\work session=abc123 data=C:\data\demo",
            CommandTemplateRenderer.Render(command.Body, command, context));
    }

    [Fact]
    public void Render_ExecutesInlineAndBlockShellInjection()
    {
        var command = Command("""
            diff:
            !`git diff`

            env:
            ```!
            git status
            ```
            """);
        var requested = new List<string>();
        var context = new CommandRenderContext
        {
            ShellExecutor = script =>
            {
                requested.Add(script);
                return "OUTPUT";
            }
        };

        var text = CommandTemplateRenderer.Render(command.Body, command, context);

        Assert.Equal(new[] { "git diff", "git status" }, requested);
        Assert.Contains("OUTPUT", text);
        Assert.DoesNotContain("!`", text);
        Assert.DoesNotContain("```!", text);
    }

    [Fact]
    public void Render_ShellOutputIsNotRescannedForPlaceholders()
    {
        var command = Command("value: !`echo`");
        var context = new CommandRenderContext
        {
            Arguments = CommandArguments.Parse("ignored"),
            ShellExecutor = _ => "literal $0 and !`nested`"
        };

        var text = CommandTemplateRenderer.Render(command.Body, command, context);

        Assert.Equal("value: literal $0 and !`nested`", text);
    }

    [Fact]
    public void Render_DisabledShellExecution_UsesPlaceholder()
    {
        var command = Command("value: !`rm -rf /`");
        var context = new CommandRenderContext
        {
            DisableShellExecution = true,
            ShellExecutor = _ => throw new InvalidOperationException("must not run")
        };

        var text = CommandTemplateRenderer.Render(command.Body, command, context);

        Assert.Contains("[shell command execution disabled by policy]", text);
    }

    [Fact]
    public void Render_ShellFailureThrows()
    {
        var command = Command("value: !`false`");
        var context = new CommandRenderContext
        {
            ShellExecutor = _ => throw new InvalidOperationException("exit 1")
        };

        var ex = Assert.Throws<CommandExecutionException>(() =>
            CommandTemplateRenderer.Render(command.Body, command, context));
        Assert.Contains("false", ex.Message);
    }

    [Fact]
    public void ContainsShellInjection_IgnoresBangWithoutBacktick()
    {
        Assert.False(CommandTemplateRenderer.ContainsShellInjection("Hello! world"));
        Assert.True(CommandTemplateRenderer.ContainsShellInjection("!`echo hi`"));
        Assert.True(CommandTemplateRenderer.ContainsShellInjection("```!\necho hi\n```"));
    }

    [Fact]
    public void ExtractShellCommands_ReturnsDistinctCommands()
    {
        var commands = CommandTemplateRenderer.ExtractShellCommands("""
            a: !`git status`
            b: !`git status`
            ```!
            git diff
            ```
            """);

        Assert.Equal(new[] { "git status", "git diff" }, commands);
    }

    [Fact]
    public void AppendArgumentsIfUnused_AppendsWhenNoPlaceholderConsumed()
    {
        var arguments = CommandArguments.Parse("staging");

        var appended = CommandTemplateRenderer.AppendArgumentsIfUnused("Do the thing.", arguments, consumed: false);
        var skipped = CommandTemplateRenderer.AppendArgumentsIfUnused("Do the thing.", arguments, consumed: true);

        Assert.EndsWith("ARGUMENTS: staging", appended);
        Assert.Equal("Do the thing.", skipped);
    }

    [Fact]
    public void RenderResult_TracksWhetherArgumentsWereConsumed()
    {
        var command = Command("literal body");
        var context = new CommandRenderContext { Arguments = CommandArguments.Parse("x") };

        var result = CommandTemplateRenderer.RenderResult(command.Body, command, context);

        Assert.False(result.ArgumentsConsumed);
        Assert.Empty(result.ShellCommands);
    }
}

public sealed class PluginLoaderTests
{
    [Fact]
    public void Load_ReadsManifestAndCommands()
    {
        using var tree = new TempPluginTree();
        tree.CreateCommandPlugin(
            "workflows",
            ("goal", "---\ndescription: Track a goal\nargument-hint: <goal>\n---\nGoal: $ARGUMENTS"),
            ("plan", "---\ndescription: Make a plan\n---\nPlan it out."));

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "workflows"));

        Assert.Equal("workflows", plugin.Name);
        Assert.Equal("test plugin", plugin.Description);
        Assert.Equal(2, plugin.Commands.Count);
        var goal = plugin.Commands.Single(item => item.ShortName == "goal");
        Assert.Equal("workflows:goal", goal.Name);
        Assert.Equal("Track a goal", goal.Description);
        Assert.Equal("<goal>", goal.ArgumentHint);
        Assert.Contains("Goal: $ARGUMENTS", goal.Body);
        Assert.DoesNotContain("---", goal.Body);
    }

    [Fact]
    public void Load_WithoutManifest_UsesDirectoryNameAndConventions()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("bare-plugin", "commands", "hello.md"), "Say hello.");

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "bare-plugin"));

        Assert.Equal("bare-plugin", plugin.Name);
        Assert.Null(plugin.ManifestPath);
        var command = Assert.Single(plugin.Commands);
        Assert.Equal("hello", command.ShortName);
        Assert.Equal("Say hello.", command.Body.Trim());
    }

    [Fact]
    public void Load_SkillBecomesCommandAndSkill()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("skills-plugin", ".claude-plugin", "plugin.json"), """{"name":"skills-plugin"}""");
        tree.Write(Path.Combine("skills-plugin", "skills", "review", "SKILL.md"), """
            ---
            name: review
            description: Reviews a pull request.
            allowed-tools: Read Grep
            ---

            Review the changed files.
            """);

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "skills-plugin"));

        var skill = Assert.Single(plugin.Skills);
        Assert.Equal("skills-plugin:review", skill.QualifiedName);
        Assert.Equal(new[] { "Read", "Grep" }, skill.AllowedTools);
        var command = Assert.Single(plugin.Commands);
        Assert.NotNull(command.SkillDirectory);
        Assert.Equal("skills-plugin:review", command.Name);
        Assert.Equal(skill.DirectoryPath, command.SkillDirectory);
    }

    [Fact]
    public void Load_ManifestInlineCommand()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("inline", ".claude-plugin", "plugin.json"), """
            {
              "name": "inline",
              "commands": {
                "about": { "content": "Explain this plugin.", "description": "About it" }
              }
            }
            """);

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "inline"));

        var command = Assert.Single(plugin.Commands);
        Assert.Equal("inline:about", command.Name);
        Assert.Equal("About it", command.Description);
        Assert.Equal("Explain this plugin.", command.Body);
        Assert.Equal(string.Empty, command.SourcePath);
    }

    [Fact]
    public void Load_RejectsPathEscapingPluginRoot()
    {
        using var tree = new TempPluginTree();
        tree.Write("outside.md", "should not load");
        tree.Write(Path.Combine("escape", ".claude-plugin", "plugin.json"), """
            { "name": "escape", "commands": ["../outside.md"] }
            """);

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "escape"));

        Assert.Empty(plugin.Commands);
        Assert.Contains(plugin.Diagnostics, item => item.Message.Contains("outside the plugin directory"));
    }

    [Fact]
    public void Load_InvalidJson_ReportsErrorAndLoadsNothing()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("broken", ".claude-plugin", "plugin.json"), "{ not json");

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "broken"));

        Assert.Empty(plugin.Commands);
        Assert.Contains(plugin.Diagnostics, item => item.Kind == PluginDiagnosticKind.Error);
    }

    [Fact]
    public void Load_ReportsUnsupportedComponentsAsInfo()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("hooksy", ".claude-plugin", "plugin.json"), """
            { "name": "hooksy", "hooks": "./hooks/hooks.json", "agents": ["./agents/a.md"] }
            """);
        tree.Write(Path.Combine("hooksy", "commands", "go.md"), "Go.");

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "hooksy"));

        Assert.Single(plugin.Commands);
        Assert.Contains(plugin.Diagnostics, item => item.Message.Contains("'hooks'"));
        Assert.Contains(plugin.Diagnostics, item => item.Message.Contains("'agents'"));
    }

    [Fact]
    public void Load_NestedCommandDirectoryBecomesColonName()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("nested", ".claude-plugin", "plugin.json"), """{"name":"nested"}""");
        tree.Write(Path.Combine("nested", "commands", "db", "migrate.md"), "Migrate.");

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "nested"));

        Assert.Contains(plugin.Commands, item => item.ShortName == "db:migrate");
    }

    [Fact]
    public void Load_McpServersFromDefaultFileAndManifest()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("mcpsy", ".claude-plugin", "plugin.json"), """
            {
              "name": "mcpsy",
              "mcpServers": { "extra": { "command": "node", "args": ["${CLAUDE_PLUGIN_ROOT}/server.js"] } }
            }
            """);
        tree.Write(Path.Combine("mcpsy", "commands", "go.md"), "Go.");
        tree.Write(Path.Combine("mcpsy", ".mcp.json"), """
            {
              "mcpServers": {
                "db": { "command": "npx", "args": ["-y", "db-server"], "env": { "TOKEN": "x" } },
                "web": { "type": "http", "url": "https://example.com/mcp" }
              }
            }
            """);

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "mcpsy"));

        Assert.Equal(3, plugin.McpServers.Count);
        var db = plugin.McpServers.Single(item => item.Name == "db");
        Assert.Equal(PluginMcpTransportKind.Stdio, db.Transport);
        Assert.Equal("npx", db.Command);
        Assert.Equal("x", db.Environment["TOKEN"]);

        var web = plugin.McpServers.Single(item => item.Name == "web");
        Assert.Equal(PluginMcpTransportKind.Http, web.Transport);
        Assert.Equal(new Uri("https://example.com/mcp"), web.Url);

        var extra = plugin.McpServers.Single(item => item.Name == "extra");
        Assert.Equal(Path.Combine(tree.Root, "mcpsy", "server.js").Replace('\\', '/'), extra.Arguments[0]);
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsDiagnosticInsteadOfThrowing()
    {
        var plugin = PluginLoader.Load(Path.Combine(Path.GetTempPath(), "aitools-missing-" + Guid.NewGuid().ToString("N")));

        Assert.Empty(plugin.Commands);
        Assert.Contains(plugin.Diagnostics, item => item.Kind == PluginDiagnosticKind.Error);
    }
}

public sealed class PluginCatalogTests
{
    [Fact]
    public void LoadFromDirectory_DiscoversSiblingPlugins()
    {
        using var tree = new TempPluginTree();
        tree.CreateCommandPlugin("alpha", ("one", "One."));
        tree.CreateCommandPlugin("beta", ("two", "Two."));

        var catalog = PluginCatalog.LoadFromDirectory(tree.Root);

        Assert.Equal(2, catalog.Count);
        Assert.Equal(new[] { "alpha", "beta" }, catalog.Plugins.Select(item => item.Name));
        Assert.Equal(2, catalog.Commands.Count);
    }

    [Fact]
    public void LoadFromDirectory_HonorsDisabledList()
    {
        using var tree = new TempPluginTree();
        tree.CreateCommandPlugin("alpha", ("one", "One."));
        tree.CreateCommandPlugin("beta", ("two", "Two."));

        var catalog = PluginCatalog.LoadFromDirectory(tree.Root, new PluginCatalogOptions
        {
            DisabledPlugins = new[] { "beta" }
        });

        Assert.Equal("alpha", Assert.Single(catalog.Plugins).Name);
    }

    [Fact]
    public void ResolveCommand_PrefersQualifiedName_AndRejectsAmbiguousShortNames()
    {
        using var tree = new TempPluginTree();
        tree.CreateCommandPlugin("alpha", ("go", "Alpha go."));
        tree.CreateCommandPlugin("beta", ("go", "Beta go."));

        var catalog = PluginCatalog.LoadFromDirectory(tree.Root);

        Assert.True(catalog.TryResolveCommand("beta:go", out var qualified));
        Assert.Equal("beta:go", qualified!.Name);

        Assert.False(catalog.TryResolveCommand("go", out _));
        Assert.True(catalog.TryResolveCommand("go", out var ambiguous, allowAmbiguousShortName: true));
        Assert.Equal("alpha:go", ambiguous!.Name);

        Assert.Contains(catalog.GetShortNameConflicts(), pair => pair.Key == "go" && pair.Value.Count == 2);
    }

    [Fact]
    public void ResolveCommand_AllowsUniqueShortName()
    {
        using var tree = new TempPluginTree();
        tree.CreateCommandPlugin("alpha", ("goal", "Goal."));

        var catalog = PluginCatalog.LoadFromDirectory(tree.Root);

        Assert.True(catalog.TryResolveCommand("goal", out var command));
        Assert.Equal("alpha:goal", command!.Name);
    }

    [Fact]
    public void ToSkillCatalog_UsesShortNamesWhenUnique()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("skillsy", ".claude-plugin", "plugin.json"), """{"name":"skillsy"}""");
        tree.Write(Path.Combine("skillsy", "skills", "deploy", "SKILL.md"), "---\ndescription: Deploy it.\n---\nBody");

        var catalog = PluginCatalog.LoadFromDirectory(tree.Root);
        var skills = catalog.ToSkillCatalog();

        Assert.Equal("deploy", Assert.Single(skills.Skills).Name);
    }
}

public sealed class PluginHostTests
{
    private static PluginHost CreateHost(TempPluginTree tree)
    {
        var catalog = PluginCatalog.LoadFromDirectory(tree.Root);
        return new PluginHost(catalog, new PluginHostOptions
        {
            EnableShellExecution = false,
            ProjectDirectory = tree.Root
        });
    }

    [Fact]
    public void LooksLikeCommand_DistinguishesPathsFromCommands()
    {
        Assert.True(PluginHost.LooksLikeCommand("/goal ship it"));
        Assert.True(PluginHost.LooksLikeCommand("  /plan"));
        Assert.False(PluginHost.LooksLikeCommand("/usr/bin/env"));
        Assert.False(PluginHost.LooksLikeCommand("hello /goal"));
        Assert.False(PluginHost.LooksLikeCommand("/"));
        Assert.False(PluginHost.LooksLikeCommand(null));
    }

    [Fact]
    public void SplitCommand_SeparatesNameAndArguments()
    {
        var (name, arguments) = PluginHost.SplitCommand("/goal make it fast");
        Assert.Equal("goal", name);
        Assert.Equal("make it fast", arguments);

        var (bare, empty) = PluginHost.SplitCommand("/plan");
        Assert.Equal("plan", bare);
        Assert.Equal(string.Empty, empty);
    }

    [Fact]
    public void TryResolve_ReturnsFalseForUnknownCommand()
    {
        using var tree = new TempPluginTree();
        tree.CreateCommandPlugin("alpha", ("goal", "Goal: $ARGUMENTS"));
        var host = CreateHost(tree);

        Assert.True(host.TryResolve("/goal ship", out var command, out var arguments));
        Assert.Equal("alpha:goal", command!.Name);
        Assert.Equal("ship", arguments.Raw);

        Assert.False(host.TryResolve("/nope", out _, out _));
        Assert.False(host.TryResolve("not a command", out _, out _));
    }

    [Fact]
    public void Render_ProducesPromptText()
    {
        using var tree = new TempPluginTree();
        tree.CreateCommandPlugin("alpha", ("goal", "Objective: $ARGUMENTS"));
        var host = CreateHost(tree);
        Assert.True(host.TryResolve("/goal ship it", out var command, out var arguments));

        var invocation = host.Render(command!, arguments, "session-1");

        Assert.Equal("Objective: ship it", invocation.Text);
        Assert.Empty(invocation.ShellCommands);
    }

    [Fact]
    public void Render_AppendsUnconsumedArguments()
    {
        using var tree = new TempPluginTree();
        tree.CreateCommandPlugin("alpha", ("echo", "Repeat after me."));
        var host = CreateHost(tree);
        Assert.True(host.TryResolve("/echo hello", out var command, out var arguments));

        var invocation = host.Render(command!, arguments);

        Assert.EndsWith("ARGUMENTS: hello", invocation.Text);
    }

    [Fact]
    public void Render_DoesNotExecuteShellWhenDisabled()
    {
        using var tree = new TempPluginTree();
        tree.CreateCommandPlugin("alpha", ("diff", "!`echo should-not-run`"));
        var host = CreateHost(tree);
        Assert.True(host.TryResolve("/diff", out var command, out var arguments));

        var invocation = host.Render(command!, arguments);

        Assert.Contains("[shell command execution disabled by policy]", invocation.Text);
        Assert.Empty(invocation.ShellCommands);
    }

    [Fact]
    public void BuildSystemPrompt_ListsCommandsButSkipsModelDisabledOnes()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("alpha", ".claude-plugin", "plugin.json"), """{"name":"alpha"}""");
        tree.Write(Path.Combine("alpha", "commands", "goal.md"), "---\ndescription: Set a goal\nargument-hint: <goal>\n---\nBody");
        tree.Write(Path.Combine("alpha", "commands", "deploy.md"), "---\ndescription: Deploy it\ndisable-model-invocation: true\n---\nBody");
        var host = CreateHost(tree);

        var prompt = host.BuildSystemPrompt();

        Assert.Contains("`/alpha:goal` <goal>: Set a goal", prompt);
        Assert.DoesNotContain("Deploy it", prompt);
    }
}
