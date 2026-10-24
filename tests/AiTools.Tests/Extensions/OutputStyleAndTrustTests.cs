using System.Text.Json;
using AiTools.Extensions;
using Xunit;

namespace AiTools.Tests.Extensions;

public sealed class OutputStyleAndTrustTests
{
    // ---- 输出风格 ----

    [Fact]
    public void Load_ParsesFrontmatterAndDefaults()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("styles", ".claude-plugin", "plugin.json"), """{"name":"styles"}""");
        tree.Write(Path.Combine("styles", "output-styles", "concise.md"), """
            ---
            name: concise
            description: Short answers.
            keep-coding-instructions: true
            ---

            Answer in one paragraph.
            """);

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "styles"));

        var style = Assert.Single(plugin.OutputStyles);
        Assert.Equal("concise", style.Name);
        Assert.Equal("Short answers.", style.Description);
        Assert.True(style.KeepCodingInstructions);
        Assert.Equal("Answer in one paragraph.", style.Body);
        Assert.Equal("styles", style.Source);
        Assert.Equal(Path.Combine(tree.Root, "styles", "output-styles", "concise.md"), style.SourcePath);
    }

    [Fact]
    public void Load_WithoutFrontmatterParts_UsesFilenameAndBodyFallback()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("bare", ".claude-plugin", "plugin.json"), """{"name":"bare"}""");
        tree.Write(Path.Combine("bare", "output-styles", "terse.md"), "No preamble here.\n\nStay brief.");

        var style = Assert.Single(PluginLoader.Load(Path.Combine(tree.Root, "bare")).OutputStyles);

        Assert.Equal("terse", style.Name);
        Assert.Equal("No preamble here.", style.Description);
        Assert.False(style.KeepCodingInstructions);
        Assert.Equal("No preamble here.\n\nStay brief.", style.Body);
    }

    [Fact]
    public void Load_KeepCodingInstructionsFalseIsExplicit()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("flags", ".claude-plugin", "plugin.json"), """{"name":"flags"}""");
        tree.Write(Path.Combine("flags", "output-styles", "warm.md"), "---\nname: warm\nkeep-coding-instructions: false\ndescription: Warm tone.\n---\nBe warm.");

        var style = Assert.Single(PluginLoader.Load(Path.Combine(tree.Root, "flags")).OutputStyles);

        Assert.False(style.KeepCodingInstructions);
        Assert.Equal("Warm tone.", style.Description);
        Assert.Equal("Be warm.", style.Body);
    }

    [Fact]
    public void Load_ManifestOutputStylePath_DirectoryAndFile()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("declared", ".claude-plugin", "plugin.json"), """
            { "name": "declared", "outputStyles": ["./styles"] }
            """);
        tree.Write(Path.Combine("declared", "styles", "one.md"), "---\nname: one\ndescription: First.\n---\nOne body.");
        tree.Write(Path.Combine("declared", "output-styles", "ignored.md"), "---\nname: ignored\n---\nNot loaded.");
        tree.Write(Path.Combine("declared", "commands", "go.md"), "Go.");

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "declared"));

        Assert.Equal("one", Assert.Single(plugin.OutputStyles).Name);
        Assert.DoesNotContain(plugin.OutputStyles, style => style.Name == "ignored");
    }

    [Fact]
    public void Load_ManifestOutputStylePath_SingleFile()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("single", ".claude-plugin", "plugin.json"), """
            { "name": "single", "outputStyles": "./styles/solo.md" }
            """);
        tree.Write(Path.Combine("single", "styles", "solo.md"), "---\nname: solo\n---\nSolo body.");

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "single"));

        Assert.Equal("solo", Assert.Single(plugin.OutputStyles).Name);
        Assert.DoesNotContain(plugin.Diagnostics, item => item.Message.Contains("'outputStyles'"));
    }

    [Fact]
    public void Load_DuplicateStyleNames_FirstWinsWithDiagnostic()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("dupes", ".claude-plugin", "plugin.json"), """{"name":"dupes"}""");
        tree.Write(Path.Combine("dupes", "output-styles", "a.md"), "---\nname: shared\ndescription: First.\n---\nFirst body.");
        tree.Write(Path.Combine("dupes", "output-styles", "b.md"), "---\nname: shared\ndescription: Second.\n---\nSecond body.");

        var plugin = PluginLoader.Load(Path.Combine(tree.Root, "dupes"));

        var style = Assert.Single(plugin.OutputStyles);
        Assert.Equal("First body.", style.Body);
        Assert.Contains(plugin.Diagnostics, item =>
            item.Kind == PluginDiagnosticKind.Warning &&
            item.Message.Contains("Duplicate output style name 'shared'"));
    }

    [Fact]
    public void Catalog_FromPlugins_AggregatesAndKeepsFirstDuplicate()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("alpha", ".claude-plugin", "plugin.json"), """{"name":"alpha"}""");
        tree.Write(Path.Combine("alpha", "output-styles", "shared.md"), "---\nname: shared\n---\nFrom alpha.");
        tree.Write(Path.Combine("beta", ".claude-plugin", "plugin.json"), """{"name":"beta"}""");
        tree.Write(Path.Combine("beta", "output-styles", "shared.md"), "---\nname: shared\n---\nFrom beta.");

        var plugins = PluginCatalog.LoadFromDirectory(tree.Root);
        var catalog = OutputStyleCatalog.LoadFromPlugins(plugins);

        var style = Assert.Single(catalog.Styles);
        Assert.Equal("alpha", style.Source);
        Assert.Contains(catalog.Diagnostics, item => item.Message.Contains("Duplicate output style name 'shared'"));
    }

    [Fact]
    public void Catalog_FromStyles_TryGetIsCaseInsensitive()
    {
        var catalog = OutputStyleCatalog.FromStyles(new[]
        {
            new OutputStyle { Name = "concise", Body = "b", Source = "host" },
            new OutputStyle { Name = "verbose", Body = "v", Source = "host" }
        });

        Assert.Equal(2, catalog.Count);
        Assert.Equal("verbose", catalog.TryGet("VERBOSE")!.Name);
        Assert.Null(catalog.TryGet("missing"));
        Assert.Null(catalog.TryGet(null));
        Assert.Equal(new[] { "concise", "verbose" }, catalog.Styles.Select(style => style.Name));
    }

    [Fact]
    public void Catalog_PluginsExposeAggregatedStyles()
    {
        using var tree = new TempPluginTree();
        tree.Write(Path.Combine("stylesy", ".claude-plugin", "plugin.json"), """{"name":"stylesy"}""");
        tree.Write(Path.Combine("stylesy", "output-styles", "tone.md"), "---\nname: tone\n---\nTone.");

        var plugins = PluginCatalog.LoadFromDirectory(tree.Root);

        Assert.Equal("tone", Assert.Single(plugins.OutputStyles).Name);
        Assert.Equal("tone", Assert.Single(plugins.Plugins).OutputStyles[0].Name);
        Assert.DoesNotContain(plugins.Diagnostics, item => item.Message.Contains("no supported components"));
    }

    [Fact]
    public void Prompt_Apply_KeepsBasePromptAndAppendsHeading()
    {
        var style = new OutputStyle
        {
            Name = "concise",
            KeepCodingInstructions = true,
            Body = "Answer briefly."
        };

        var applied = OutputStylePrompt.Apply(style, "You are a coding agent.");

        Assert.StartsWith("You are a coding agent.", applied);
        Assert.Contains("## Output style: concise", applied);
        Assert.Contains("Answer briefly.", applied);
        Assert.True(applied.IndexOf("## Output style: concise", StringComparison.Ordinal) >
                    applied.IndexOf("You are a coding agent.", StringComparison.Ordinal));
        Assert.DoesNotContain("Coding instructions: suspended", applied);
    }

    [Fact]
    public void Prompt_Apply_MarksSuspendedCodingInstructions()
    {
        var style = new OutputStyle { Name = "prose", KeepCodingInstructions = false, Body = "Write prose." };

        var applied = OutputStylePrompt.Apply(style, "Base.");

        Assert.Contains("Coding instructions: suspended", applied);
        Assert.Contains("Base.", applied);
        Assert.Contains("Write prose.", applied);
    }

    [Fact]
    public void Prompt_Apply_NullBasePrompt_ProducesStandaloneSection()
    {
        var style = new OutputStyle { Name = "only", KeepCodingInstructions = true, Body = "Just this." };

        var applied = OutputStylePrompt.Apply(style, null);

        Assert.StartsWith("## Output style: only", applied);
        Assert.DoesNotContain("Coding instructions: suspended", applied);
    }

    [Fact]
    public void Selection_AppliesOnlyWhenResolved()
    {
        var catalog = OutputStyleCatalog.FromStyles(new[]
        {
            new OutputStyle { Name = "concise", Body = "Brief.", KeepCodingInstructions = true, Source = "host" }
        });

        Assert.Equal("Base", OutputStyleSelection.None.ApplyTo("Base"));
        Assert.Equal("Base", OutputStyleSelection.Resolve(catalog, "missing").ApplyTo("Base"));

        var resolved = OutputStyleSelection.Resolve(catalog, "concise");
        Assert.True(resolved.HasStyle);
        Assert.Contains("## Output style: concise", resolved.ApplyTo("Base"));
    }

    // ---- 权限策略 ----

    private static PermissionRule Rule(string tool, PermissionDecision decision, string source = "test", string? argumentPattern = null)
        => new() { Tool = tool, Decision = decision, Source = source, ArgumentPattern = argumentPattern };

    [Fact]
    public void Evaluate_DenyBeatsAskBeatsAllow()
    {
        var policy = PermissionPolicy.FromRules(new[]
        {
            Rule("write", PermissionDecision.Allow),
            Rule("write", PermissionDecision.Ask),
            Rule("write", PermissionDecision.Deny)
        });

        Assert.Equal(PermissionDecision.Deny, policy.Evaluate("write", "{}"));
    }

    [Fact]
    public void Evaluate_AskBeatsEarlyAllow()
    {
        var policy = PermissionPolicy.FromRules(new[]
        {
            Rule("*", PermissionDecision.Allow),
            Rule("shell", PermissionDecision.Ask)
        });

        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("read", "{}"));
        Assert.Equal(PermissionDecision.Ask, policy.Evaluate("shell", "{}"));
    }

    [Fact]
    public void Evaluate_FirstMatchWithinSameClassWins()
    {
        var policy = PermissionPolicy.FromRules(new[]
        {
            Rule("shell", PermissionDecision.Ask, "project"),
            Rule("shell", PermissionDecision.Ask, "user")
        });

        Assert.Equal(PermissionDecision.Ask, policy.Evaluate("shell", "{}", out var matched));
        Assert.Equal("project", matched!.Source);
    }

    [Fact]
    public void Evaluate_NamespacePrefixMatchesOnlyThatNamespace()
    {
        var policy = PermissionPolicy.FromRules(new[] { Rule("fs:*", PermissionDecision.Allow) });

        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("fs:read", "{}"));
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("fs:", "{}"));
        Assert.Equal(PermissionDecision.Ask, policy.Evaluate("fs_read", "{}"));
        Assert.Equal(PermissionDecision.Ask, policy.Evaluate("http:read", "{}"));
    }

    [Fact]
    public void Evaluate_ArgumentGlobRestrictsMatch()
    {
        var policy = PermissionPolicy.FromRules(new[]
        {
            Rule("shell", PermissionDecision.Deny, argumentPattern: "{\"command\":\"rm *")
        });

        Assert.Equal(PermissionDecision.Deny, policy.Evaluate("shell", "{\"command\":\"rm -rf /\"}"));
        Assert.Equal(PermissionDecision.Ask, policy.Evaluate("shell", "{\"command\":\"ls\"}"));
    }

    [Fact]
    public void Evaluate_UnknownToolAndEmptyPolicyAsks()
    {
        var policy = PermissionPolicy.FromRules(new[] { Rule("known", PermissionDecision.Allow) });

        Assert.Equal(PermissionDecision.Ask, policy.Evaluate("unknown", "{}"));
        Assert.Equal(PermissionDecision.Ask, PermissionPolicy.Default.Evaluate("known", "{}"));
        Assert.Equal(PermissionDecision.Ask, policy.Evaluate(null, "{}"));
        Assert.Equal(PermissionDecision.Ask, policy.Evaluate("  ", "{}"));
    }

    [Fact]
    public async Task Gate_AllowExecutes()
    {
        var policy = PermissionPolicy.FromRules(new[] { Rule("read", PermissionDecision.Allow) });
        var executed = 0;

        var result = await PermissionGate.GuardAsync(
            policy, "read", "{}",
            () => { executed++; return Task.FromResult("content"); },
            null);

        Assert.Equal("content", result);
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task Gate_DenyDoesNotExecute()
    {
        var policy = PermissionPolicy.FromRules(new[] { Rule("shell", PermissionDecision.Deny, "project") });
        var executed = 0;

        var result = await PermissionGate.GuardAsync(
            policy, "shell", "{}",
            () => { executed++; return Task.FromResult("ran"); },
            _ => Task.FromResult(true));

        Assert.Equal(0, executed);
        using var document = JsonDocument.Parse(result);
        Assert.Equal("denied", document.RootElement.GetProperty("error").GetString());
        Assert.Contains("'project' rule", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Gate_AskWithoutConfirmFailsClosed()
    {
        var policy = PermissionPolicy.FromRules(new[] { Rule("shell", PermissionDecision.Ask) });
        var executed = 0;

        var result = await PermissionGate.GuardAsync(
            policy, "shell", "{\"command\":\"ls\"}",
            () => { executed++; return Task.FromResult("ran"); },
            null);

        Assert.Equal(0, executed);
        using var document = JsonDocument.Parse(result);
        Assert.Equal("not approved", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Gate_AskWithConfirmTrueExecutes()
    {
        var policy = PermissionPolicy.FromRules(new[] { Rule("shell", PermissionDecision.Ask) });
        string? prompt = null;

        var result = await PermissionGate.GuardAsync(
            policy, "shell", "{\"command\":\"ls\"}",
            () => Task.FromResult("ran"),
            message => { prompt = message; return Task.FromResult(true); });

        Assert.Equal("ran", result);
        Assert.Contains("shell", prompt);
        Assert.Contains("\"command\":\"ls\"", prompt);
    }

    [Fact]
    public async Task Gate_AskWithConfirmFalseDoesNotExecute()
    {
        var executed = 0;

        var result = await PermissionGate.GuardAsync(
            PermissionPolicy.Default, "shell", "{}",
            () => { executed++; return Task.FromResult("ran"); },
            _ => Task.FromResult(false));

        Assert.Equal(0, executed);
        using var document = JsonDocument.Parse(result);
        Assert.Equal("not approved", document.RootElement.GetProperty("error").GetString());
    }

    // ---- 项目信任 ----

    private static TrustDecision Evaluate(string? directory, params string[] trustedRoots)
        => ProjectTrust.Evaluate(directory, trustedRoots, requireTrust: true);

    [Fact]
    public void Evaluate_WithoutRequirementIsTrusted()
    {
        var decision = ProjectTrust.Evaluate("C:/anything", Array.Empty<string>(), requireTrust: false);

        Assert.Equal(TrustLevel.Trusted, decision.Level);
        Assert.True(decision.IsTrusted);
        Assert.Empty(decision.SkippedSources);
    }

    [Fact]
    public void Evaluate_ExactDirectoryIsTrusted()
    {
        var decision = Evaluate("C:/work/project", "C:/work/project");

        Assert.Equal(TrustLevel.Trusted, decision.Level);
        Assert.Contains("trusted root", decision.Reason);
    }

    [Fact]
    public void Evaluate_NestedDirectoryIsTrusted()
    {
        var decision = Evaluate("C:/work/project/src/app", "C:/work/project");

        Assert.Equal(TrustLevel.Trusted, decision.Level);
    }

    [Fact]
    public void Evaluate_SiblingPrefixIsNotTrusted()
    {
        var decision = Evaluate("C:/ab", "C:/a");

        Assert.Equal(TrustLevel.Untrusted, decision.Level);
        Assert.Equal(Path.GetFullPath("C:/ab").TrimEnd(Path.DirectorySeparatorChar), Assert.Single(decision.SkippedSources));
    }

    [Fact]
    public void Evaluate_ParentEscapeIsNotTrusted()
    {
        var decision = Evaluate("C:/a/../b", "C:/a");

        Assert.Equal(TrustLevel.Untrusted, decision.Level);
    }

    [Fact]
    public void Evaluate_IsCaseInsensitiveAndSeparatorNormalized()
    {
        Assert.Equal(TrustLevel.Trusted, Evaluate("C:/WORK/Project/Sub", "c:/work/project").Level);
        Assert.Equal(TrustLevel.Trusted, Evaluate(@"C:\work\project\Sub", "C:/work/project").Level);
        Assert.Equal(TrustLevel.Trusted, Evaluate("C:/work/project", @"c:\WORK\PROJECT\").Level);
    }

    [Fact]
    public void Evaluate_MissingDirectoryIsUntrustedWithReason()
    {
        var decision = Evaluate(null, "C:/work");

        Assert.Equal(TrustLevel.Untrusted, decision.Level);
        Assert.False(decision.IsTrusted);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Evaluate_UntrustedReportsSkippedSource()
    {
        var decision = Evaluate("C:/elsewhere", "C:/work");

        Assert.Equal(TrustLevel.Untrusted, decision.Level);
        var skipped = Assert.Single(decision.SkippedSources);
        Assert.Equal(Path.GetFullPath("C:/elsewhere").TrimEnd(Path.DirectorySeparatorChar), skipped);
    }

    [Fact]
    public void AccessControl_GatesProjectResourcesAndTools()
    {
        var denied = new AccessControl(
            PermissionPolicy.FromRules(new[]
            {
                new PermissionRule { Tool = "shell", Decision = PermissionDecision.Deny, Source = "project" }
            }),
            new TrustDecision(TrustLevel.Untrusted, "not trusted"));

        Assert.False(denied.AllowsProjectResources);
        Assert.True(AccessControl.Unrestricted.AllowsProjectResources);
        Assert.Equal(PermissionDecision.Deny, denied.Policy.Evaluate("shell", "{}"));
    }
}
