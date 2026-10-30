using System.Collections.Generic;
using System.Linq;
using AiTools.Extensions;
using AiTools.Skills;
using AiTools.Wpf.Skills;
using Xunit;

namespace AiTools.Tests.Skills;

/// <summary>
/// 验证多个 agent 工具共用的 <c>.agents</c> 目录：技能发现、项目级覆盖用户级，
/// 以及 <c>AGENTS.md</c> 指令的合并与注入。
/// </summary>
public sealed class SharedAgentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aitools-agents-" + Guid.NewGuid().ToString("N"));
    private readonly string _userAgents;
    private readonly string _project;
    private readonly string _projectAgents;

    public SharedAgentsTests()
    {
        _userAgents = Path.Combine(_root, "home", ".agents");
        _project = Path.Combine(_root, "work", "repo");
        _projectAgents = Path.Combine(_project, ".agents");
        Directory.CreateDirectory(_userAgents);
        Directory.CreateDirectory(_projectAgents);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void WriteSkill(string agentsDirectory, string name, string description, string? body = null)
    {
        var directory = Path.Combine(agentsDirectory, "skills", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\n{body ?? "Body of " + name + "."}");
    }

    [Fact]
    public void Discover_ReadsUserAndProjectSkills()
    {
        WriteSkill(_userAgents, "shared-review", "User-level review.");
        WriteSkill(_projectAgents, "project-deploy", "Project-level deploy.");

        var catalog = SharedAgents.Discover(_project, includeUser: true, userDirectory: _userAgents);

        Assert.Equal(new[] { "project-deploy", "shared-review" }, catalog.Skills.Skills.Select(skill => skill.Name));
        Assert.Equal(2, catalog.SkillRoots.Count);
    }

    [Fact]
    public void Discover_ProjectDefinitionOverridesUserDefinition()
    {
        WriteSkill(_userAgents, "review", "User-level version.", "user body");
        WriteSkill(_projectAgents, "review", "Project-level version.", "project body");

        var catalog = SharedAgents.Discover(_project, includeUser: true, userDirectory: _userAgents);

        var skill = Assert.Single(catalog.Skills.Skills);
        Assert.Equal("Project-level version.", skill.Description);
        Assert.Contains("project body", skill.Instructions);

        // 覆盖关系要留下诊断，否则用户会困惑"为什么我的技能没生效"。
        Assert.Contains(catalog.Diagnostics, item => item.Message.Contains("overridden"));
    }

    [Fact]
    public void Discover_IncludeUserFalse_IgnoresTheUserDirectory()
    {
        WriteSkill(_userAgents, "user-only", "Should be skipped.");
        WriteSkill(_projectAgents, "project-only", "Kept.");

        var catalog = SharedAgents.Discover(_project, includeUser: false);

        var skill = Assert.Single(catalog.Skills.Skills);
        Assert.Equal("project-only", skill.Name);
    }

    [Fact]
    public void Discover_NoProjectDirectory_ReadsOnlyUserResources()
    {
        WriteSkill(_userAgents, "user-only", "Kept.");

        var catalog = SharedAgents.Discover(projectDirectory: null, includeUser: true, userDirectory: _userAgents);

        Assert.Single(catalog.Skills.Skills);
        Assert.Single(catalog.SkillRoots);
    }

    [Fact]
    public void Discover_MissingDirectories_ProduceNoDiagnostics()
    {
        var missingProject = Path.Combine(_root, "does-not-exist");
        var missingUser = Path.Combine(_root, "also-missing");

        var catalog = SharedAgents.Discover(missingProject, includeUser: true, userDirectory: missingUser);

        Assert.Empty(catalog.Skills.Skills);
        Assert.Empty(catalog.Instructions);
        Assert.Empty(catalog.SkillRoots);
        Assert.Empty(catalog.Diagnostics);
    }

    [Fact]
    public void Discover_ReadsAgentsMarkdownFromBothScopes()
    {
        File.WriteAllText(Path.Combine(_userAgents, "AGENTS.md"), "# Global\n\nBe concise.");
        File.WriteAllText(Path.Combine(_projectAgents, "AGENTS.md"), "# Project\n\nUse tabs.");

        var catalog = SharedAgents.Discover(_project, includeUser: true, userDirectory: _userAgents);

        Assert.Equal(2, catalog.Instructions.Count);
        Assert.Equal(AgentInstructionsScope.User, catalog.Instructions[0].Scope);
        Assert.Equal(AgentInstructionsScope.Project, catalog.Instructions[1].Scope);

        var prompt = catalog.BuildInstructionsPrompt();
        Assert.Contains("## Agent instructions", prompt);
        Assert.Contains("Be concise.", prompt);
        Assert.Contains("Use tabs.", prompt);
        // 用户级在前、项目级在后：越靠后越具体。
        Assert.True(prompt.IndexOf("Be concise.", StringComparison.Ordinal) <
                    prompt.IndexOf("Use tabs.", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInstructionsPrompt_WithoutInstructions_IsEmpty()
    {
        var catalog = SharedAgents.Discover(null, includeUser: false);

        Assert.Equal(string.Empty, catalog.BuildInstructionsPrompt());
    }

    [Fact]
    public void Discover_ReportsUnreadableSkillFilesInsteadOfThrowing()
    {
        WriteSkill(_userAgents, "valid", "Fine.");
        var broken = Path.Combine(_userAgents, "skills", "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "SKILL.md"), "no frontmatter here");

        var catalog = SharedAgents.Discover(null, includeUser: true, userDirectory: _userAgents);

        Assert.Single(catalog.Skills.Skills);
        Assert.NotEmpty(catalog.Diagnostics);
    }

    [Fact]
    public void FindAgentDefinitions_ListsYamlFiles()
    {
        var skill = Path.Combine(_userAgents, "skills", "with-agents");
        Directory.CreateDirectory(Path.Combine(skill, "agents"));
        File.WriteAllText(Path.Combine(skill, "agents", "openai.yaml"), "interface: {}");
        File.WriteAllText(Path.Combine(skill, "agents", "readme.md"), "not an agent definition");

        var definitions = SharedAgents.FindAgentDefinitions(skill);

        var definition = Assert.Single(definitions);
        Assert.EndsWith("openai.yaml", definition);
    }

    [Fact]
    public void FindAgentDefinitions_MissingDirectoryIsEmpty()
    {
        Assert.Empty(SharedAgents.FindAgentDefinitions(Path.Combine(_root, "nope")));
        Assert.Empty(SharedAgents.FindAgentDefinitions(string.Empty));
    }

    [Fact]
    public void Discover_NativeSkillsDirectoryWinsOverSharedAgents()
    {
        WriteSkill(_userAgents, "review", "Shared version.", "shared body");

        var nativeRoot = Path.Combine(_root, "home", ".ai-tools");
        var nativeSkill = Path.Combine(nativeRoot, "skills", "review");
        Directory.CreateDirectory(nativeSkill);
        File.WriteAllText(
            Path.Combine(nativeSkill, "SKILL.md"),
            "---\nname: review\ndescription: Native version.\n---\n\nnative body");
        File.WriteAllText(Path.Combine(nativeRoot, "AGENTS.md"), "Native instructions.");

        var catalog = SharedAgents.Discover(
            _project,
            includeUser: true,
            userDirectory: _userAgents,
            nativeDirectory: nativeRoot);

        var skill = catalog.Skills.Skills.Single(item => item.Name == "review");
        Assert.Equal("Native version.", skill.Description);
        Assert.Contains("native body", skill.Instructions);
        Assert.Contains(catalog.Diagnostics, item => item.Message.Contains("overridden"));
        Assert.Contains(catalog.Instructions, item => item.Content.Contains("Native instructions."));
    }

    [Fact]
    public void Discover_SandboxedUserDirectory_SkipsNativeRoot()
    {
        // 调用方显式给定 userDirectory 且未给 nativeDirectory 时，不能偷偷读开发者真实主目录。
        WriteSkill(_userAgents, "only-shared", "Shared only.");

        var catalog = SharedAgents.Discover(_project, includeUser: true, userDirectory: _userAgents);

        Assert.Equal(new[] { "only-shared" }, catalog.Skills.Skills.Select(skill => skill.Name));
        Assert.DoesNotContain(
            catalog.SkillRoots,
            root => root.Contains(".ai-tools", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadFromDirectories_LaterRootsWin()
    {
        var low = Path.Combine(_root, "low");
        var high = Path.Combine(_root, "high");
        WriteSkill(low, "dup", "low version");
        WriteSkill(high, "dup", "high version");

        var catalog = SkillCatalog.LoadFromDirectories(new[] { low, high });

        var skill = Assert.Single(catalog.Skills);
        Assert.Equal("high version", skill.Description);
        Assert.Contains(catalog.Diagnostics, item => item.Message.Contains("overridden"));
    }

    [Fact]
    public void SkillCatalogChatToolProvider_MergesPluginAndSharedSkills()
    {
        WriteSkill(_userAgents, "shared-skill", "From .agents.");
        var shared = SharedAgents.Discover(null, includeUser: true, userDirectory: _userAgents);

        var pluginSkill = new SkillDefinition
        {
            Name = "plugin-skill",
            Description = "From a plugin.",
            DirectoryPath = Path.Combine(_root, "plugin"),
            SkillFilePath = Path.Combine(_root, "plugin", "SKILL.md")
        };

        var provider = SkillCatalogChatToolProvider.Create(
            plugins: null,
            shared: shared);
        Assert.Equal(new[] { "shared-skill" }, provider.Toolset.Skills.Select(skill => skill.Name));

        // 两种来源合并后一并暴露给模型。
        var merged = new SkillCatalogChatToolProvider(
            SkillCatalog.FromSkills(shared.Skills.Skills.Concat(new[] { pluginSkill })));
        Assert.Equal(new[] { "plugin-skill", "shared-skill" }, merged.Toolset.Skills.Select(skill => skill.Name));
        Assert.Equal(2, merged.GetTools().Count);
    }

    [Fact]
    public void SkillCatalogChatToolProvider_NoSources_YieldsNoTools()
    {
        var provider = SkillCatalogChatToolProvider.Create(plugins: null, shared: null);

        Assert.Equal(0, provider.SkillCount);
        Assert.Empty(provider.GetTools());
    }

    [Fact]
    public void SkillCatalogChatToolProvider_RefreshesWhenFactoryContentChanges()
    {
        // 模拟扩展在会话期间登记技能：工厂返回的内容随时变化。
        var skills = new List<SkillDefinition>();
        var provider = new SkillCatalogChatToolProvider(() => SkillCatalog.FromSkills(skills.ToArray()));

        Assert.Null(provider.GetTools().FirstOrDefault(tool => tool.Name == SkillToolset.LoadSkillToolName));
        Assert.Empty(provider.Toolset.Skills);

        skills.Add(new SkillDefinition
        {
            Name = "late-skill",
            Description = "Registered later.",
            Instructions = "Do it.",
            DirectoryPath = _root,
            SkillFilePath = Path.Combine(_root, "SKILL.md")
        });

        // 访问即刷新：新技能出现在工具与清单里，无需重新注册 provider。
        Assert.Contains(provider.GetTools(), tool => tool.Name == SkillToolset.LoadSkillToolName);
        Assert.Equal("late-skill", Assert.Single(provider.Toolset.Skills).Name);
        Assert.Contains("late-skill", provider.Toolset.BuildSystemPrompt());
    }

    [Fact]
    public void SkillCatalogChatToolProvider_UnchangedContentKeepsToolsetInstance()
    {
        var skills = new[]
        {
            new SkillDefinition
            {
                Name = "stable",
                Description = "Stable.",
                DirectoryPath = _root,
                SkillFilePath = Path.Combine(_root, "SKILL.md")
            }
        };

        // 每次调用都返回新目录对象，但内容相同。
        var provider = new SkillCatalogChatToolProvider(() => SkillCatalog.FromSkills(skills));
        var first = provider.Toolset;
        provider.GetTools();

        Assert.Same(first, provider.Toolset);
    }
}
