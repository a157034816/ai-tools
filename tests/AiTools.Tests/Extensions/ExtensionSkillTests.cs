using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Extensions;
using AiTools.Extensions.Packages;
using AiTools.Extensions.Scripting;
using AiTools.Skills;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>
/// 扩展携带技能的端到端行为：代码扩展登记、扩展包随包 <c>SKILL.md</c>、
/// 脚本扩展 registerSkill，以及它们汇入命令目录与技能目录的方式。
/// </summary>
public sealed class ExtensionSkillTests
{
    [Fact]
    public async Task CodeExtension_RegistersArbitrarySkillsThatReachCatalogAndCommands()
    {
        var extension = new ProbeExtension("skill-owner", api =>
        {
            for (var index = 0; index < 5; index++)
            {
                api.RegisterSkill(new SkillDefinition
                {
                    Name = "generated-" + index,
                    Description = "Generated skill " + index,
                    Instructions = "Body " + index,
                    DirectoryPath = Path.GetTempPath(),
                    SkillFilePath = Path.Combine(Path.GetTempPath(), "SKILL.md")
                });
            }
        });

        await using var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = new IExtension[] { extension },
            WorkingDirectory = Environment.CurrentDirectory,
            EnableWorkflows = false
        });

        Assert.Equal(5, pipeline.Skills.Count);
        Assert.All(pipeline.Skills, skill => Assert.Equal("skill-owner", pipeline.Runtime.Skills.First(item => item.Name == skill.Name).ExtensionName));

        // 每个技能都有一条 /skill:<名称> 命令，执行时把正文作为提示词。
        var command = Assert.Single(pipeline.Commands.Commands, item => item.Name == "skill:generated-3");
        Assert.True(command.IsSkillCommand);
        Assert.Equal("skill-owner", command.Source);

        var execution = await pipeline.ExecuteCommandAsync("/skill:generated-3 补充要求", pipeline.Session.CreateContext(null, null));
        Assert.Equal(CommandExecutionKind.Prompt, execution!.Kind);
        Assert.Contains("Body 3", execution.Prompt);
        Assert.Contains("补充要求", execution.Prompt);
    }

    [Fact]
    public async Task PackageSkills_AreLoadedFromSkillsDirectoryAndSurviveUnload()
    {
        using var temp = new TemporaryDirectory();
        WriteSkill(Path.Combine(temp.Path, "skills", "pdf-tools"), "pdf-tools", "Work with PDFs.");
        WriteSkill(Path.Combine(temp.Path, "skills", "diagramming"), "diagramming", "Draw diagrams.");
        File.WriteAllText(Path.Combine(temp.Path, "index.js"), "module.exports = { name: 'doc-tools', activate(api) {} };\n");
        File.WriteAllText(Path.Combine(temp.Path, "extension.json"),
            "{\"name\":\"doc-tools\",\"kind\":\"javascript\",\"entry\":\"index.js\"}");

        var package = await LoadPackageAsync(temp.Path);
        Assert.True(package.IsLoaded, string.Join(Environment.NewLine, package.Diagnostics.Select(item => item.Message)));
        Assert.Equal(new[] { "diagramming", "pdf-tools" }, package.Skills.Select(skill => skill.Name));
        Assert.Equal("Work with PDFs.", package.Skills.Single(skill => skill.Name == "pdf-tools").Description);
        Assert.Contains("Use pdfplumber", package.Skills.Single(skill => skill.Name == "pdf-tools").Instructions);

        package.Unload();
        Assert.Empty(package.Skills);
    }

    [Fact]
    public async Task PackageManifest_SkillPathsOverrideDefaultDirectory()
    {
        using var temp = new TemporaryDirectory();
        WriteSkill(Path.Combine(temp.Path, "skills", "ignored"), "ignored", "Should not load.");
        WriteSkill(Path.Combine(temp.Path, "capabilities", "kept"), "kept", "Declared location.");
        File.WriteAllText(Path.Combine(temp.Path, "index.js"), "module.exports = { name: 'declared', activate(api) {} };\n");
        File.WriteAllText(Path.Combine(temp.Path, "extension.json"),
            "{\"name\":\"declared\",\"kind\":\"javascript\",\"entry\":\"index.js\",\"skills\":[\"capabilities\"]}");

        var package = await LoadPackageAsync(temp.Path);

        Assert.Equal("kept", Assert.Single(package.Skills).Name);
    }

    [Fact]
    public async Task PackageSkills_EnterPipelineSkillsAndCommandsAfterWorkspaceScan()
    {
        using var workspaceRoot = new TemporaryDirectory();
        using var packageDirectory = new TemporaryDirectory("pkg", workspaceRoot.Path);
        WriteSkill(Path.Combine(packageDirectory.Path, "skills", "workspace-skill"), "workspace-skill", "From a package.");
        File.WriteAllText(Path.Combine(packageDirectory.Path, "index.js"), "module.exports = { name: 'workspace-package', activate(api) {} };\n");
        File.WriteAllText(Path.Combine(packageDirectory.Path, "extension.json"),
            "{\"name\":\"workspace-package\",\"kind\":\"javascript\",\"entry\":\"index.js\"}");

        using var workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions
        {
            Directories = new[] { workspaceRoot.Path },
            ScriptProvider = new ScriptExtensionProvider(),
            BuildOnLoad = false
        });
        await using var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Workspace = workspace,
            WorkingDirectory = workspaceRoot.Path,
            EnableWorkflows = false
        });

        await pipeline.LoadWorkspaceAsync();

        var skill = Assert.Single(pipeline.Skills);
        Assert.Equal("workspace-skill", skill.Name);
        Assert.Contains(pipeline.Commands.Commands, item => item.Name == "skill:workspace-skill");
    }

    [Fact]
    public async Task ScriptExtension_RegisterSkillPublishesSkillToPipeline()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "index.js"), @"
module.exports = {
  name: 'script-skills',
  activate(api) {
    api.registerSkill({
      name: 'from-script',
      description: 'Registered from JavaScript.',
      instructions: 'Do the scripted thing.',
      allowedTools: ['beta-tools'],
      metadata: { author: 'js' }
    });
  }
};
");
        File.WriteAllText(Path.Combine(temp.Path, "extension.json"),
            "{\"name\":\"script-skills\",\"kind\":\"javascript\",\"entry\":\"index.js\"}");
        var package = await LoadPackageAsync(temp.Path);
        Assert.True(package.IsLoaded, string.Join(Environment.NewLine, package.Diagnostics.Select(item => item.Message)));

        await using var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = package.Extensions,
            WorkingDirectory = temp.Path,
            EnableWorkflows = false
        });

        var skill = Assert.Single(pipeline.Skills);
        Assert.Equal("from-script", skill.Name);
        Assert.Equal("Registered from JavaScript.", skill.Description);
        Assert.Equal("Do the scripted thing.", skill.Instructions);
        Assert.Equal(new[] { "beta-tools" }, skill.AllowedTools);
        Assert.Equal("js", skill.Metadata["author"]);
    }

    [Fact]
    public async Task Skills_LaterSourcesWinAndPluginSkillsKeepPriority()
    {
        var plugins = PluginCatalog.FromPlugins(new[]
        {
            new AiToolsPlugin
            {
                Name = "plug",
                RootPath = Environment.CurrentDirectory,
                Skills = new[]
                {
                    new PluginSkill
                    {
                        Name = "shared-name",
                        QualifiedName = "plug:shared-name",
                        PluginName = "plug",
                        Description = "Plugin version.",
                        Instructions = "Plugin body.",
                        DirectoryPath = Environment.CurrentDirectory,
                        SkillFilePath = Path.Combine(Environment.CurrentDirectory, "SKILL.md"),
                        Command = new PluginCommand
                        {
                            Name = "plug:shared-name",
                            ShortName = "shared-name",
                            PluginName = "plug",
                            Description = "Plugin version.",
                            Body = "Plugin body.",
                            PluginRoot = Environment.CurrentDirectory
                        }
                    }
                }
            }
        });

        await using var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = new IExtension[]
            {
                new ProbeExtension("ext", api => api.RegisterSkill(new SkillDefinition
                {
                    Name = "shared-name",
                    Description = "Extension version.",
                    Instructions = "Extension body.",
                    DirectoryPath = Environment.CurrentDirectory,
                    SkillFilePath = Path.Combine(Environment.CurrentDirectory, "SKILL.md")
                }))
            },
            Plugins = plugins,
            SharedSkills = new[]
            {
                new SkillDefinition
                {
                    Name = "shared-name",
                    Description = "Shared version.",
                    Instructions = "Shared body.",
                    DirectoryPath = Environment.CurrentDirectory,
                    SkillFilePath = Path.Combine(Environment.CurrentDirectory, "SKILL.md")
                }
            },
            WorkingDirectory = Environment.CurrentDirectory,
            EnableWorkflows = false
        });

        // 同名技能只生成一条 /skill: 命令；插件来源最具体，扩展与共享均让位。
        var skillCommands = pipeline.Commands.Commands
            .Where(item => string.Equals(item.ShortName, "skill:shared-name", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(skillCommands);
        Assert.NotNull(skillCommands[0].Skill);
        Assert.Null(skillCommands[0].SharedSkill);

        // 运行时仍然记录扩展登记的定义（插件技能不经过运行时）。
        Assert.Equal("Extension version.", pipeline.Skills.Single().Description);
    }

    [Fact]
    public async Task WorkspaceRemoval_RefreshesPackageSkillsWithoutTouchingExtensionSkills()
    {
        using var workspaceRoot = new TemporaryDirectory();
        using var packageDirectory = new TemporaryDirectory("pkg", workspaceRoot.Path);
        WriteSkill(Path.Combine(packageDirectory.Path, "skills", "package-skill"), "package-skill", "Package skill.");
        File.WriteAllText(Path.Combine(packageDirectory.Path, "index.js"), "module.exports = { name: 'removable-package', activate(api) {} };\n");
        File.WriteAllText(Path.Combine(packageDirectory.Path, "extension.json"),
            "{\"name\":\"removable-package\",\"kind\":\"javascript\",\"entry\":\"index.js\"}");

        using var workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions
        {
            Directories = new[] { workspaceRoot.Path },
            ScriptProvider = new ScriptExtensionProvider(),
            BuildOnLoad = false
        });
        await using var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = new IExtension[]
            {
                new ProbeExtension("code-extension", api => api.RegisterSkill(new SkillDefinition
                {
                    Name = "code-skill",
                    Description = "Registered by code.",
                    DirectoryPath = workspaceRoot.Path,
                    SkillFilePath = Path.Combine(workspaceRoot.Path, "SKILL.md")
                }))
            },
            Workspace = workspace,
            WorkingDirectory = workspaceRoot.Path,
            EnableWorkflows = false
        });

        await pipeline.LoadWorkspaceAsync();
        Assert.Equal(new[] { "code-skill", "package-skill" }, pipeline.Skills.Select(skill => skill.Name).OrderBy(name => name));

        // 删除包目录后重新扫描：包技能消失，扩展代码登记的技能必须保留。
        Directory.Delete(packageDirectory.Path, recursive: true);
        await workspace.ScanAsync();

        var remaining = Assert.Single(pipeline.Skills);
        Assert.Equal("code-skill", remaining.Name);
        Assert.DoesNotContain(pipeline.Commands.Commands, item => item.Name == "skill:package-skill");
        Assert.Contains(pipeline.Commands.Commands, item => item.Name == "skill:code-skill");
    }

    [Fact]
    public void PackageSkills_SurviveUnloadingAnExtensionWithTheSameName()
    {
        var runtime = new ExtensionRuntime();
        var packageSkill = new SkillDefinition
        {
            Name = "package-skill",
            Description = "From a package.",
            DirectoryPath = Environment.CurrentDirectory,
            SkillFilePath = Path.Combine(Environment.CurrentDirectory, "SKILL.md")
        };
        runtime.ReplacePackageSkills(new[] { ("shared-name", packageSkill) });

        // 扩展名与包名相同：卸载扩展不得连带删除包技能。
        runtime.Activate(new ProbeExtension("shared-name", api => api.RegisterSkill(new SkillDefinition
        {
            Name = "code-skill",
            Description = "From code.",
            DirectoryPath = Environment.CurrentDirectory,
            SkillFilePath = Path.Combine(Environment.CurrentDirectory, "SKILL.md")
        })));
        Assert.Equal(2, runtime.Skills.Count);

        runtime.UnloadExtension("shared-name");

        Assert.Equal("package-skill", Assert.Single(runtime.Skills).Name);
    }

    [Fact]
    public void SkillsVersionedAndUnregisterable()
    {
        var runtime = new ExtensionRuntime();
        var before = runtime.SkillsVersion;

        runtime.Activate(new ProbeExtension("owner", api => api.RegisterSkill(new SkillDefinition
        {
            Name = "versioned",
            Description = "Versioned skill.",
            DirectoryPath = Environment.CurrentDirectory,
            SkillFilePath = Path.Combine(Environment.CurrentDirectory, "SKILL.md")
        })));

        Assert.True(runtime.SkillsVersion > before);
        Assert.Single(runtime.Skills);

        Assert.True(runtime.UnregisterSkills("owner"));
        Assert.Empty(runtime.Skills);
        Assert.False(runtime.UnregisterSkills("owner"));

        // 卸载扩展同样会移除它登记过的技能。
        runtime.Activate(new ProbeExtension("second-owner", api => api.RegisterSkill(new SkillDefinition
        {
            Name = "again",
            Description = "Another skill.",
            DirectoryPath = Environment.CurrentDirectory,
            SkillFilePath = Path.Combine(Environment.CurrentDirectory, "SKILL.md")
        })));
        Assert.Single(runtime.Skills);
        runtime.UnloadExtension("second-owner");
        Assert.Empty(runtime.Skills);
    }

    [Fact]
    public void RegisterSkill_RequiresNameAndDescription()
    {
        var runtime = new ExtensionRuntime();

        Assert.Throws<ArgumentException>(() => runtime.RegisterSkill(new SkillDefinition { Name = "x" }, "owner"));
        Assert.Throws<ArgumentException>(() => runtime.RegisterSkill(new SkillDefinition { Description = "d" }, "owner"));
        Assert.Throws<ArgumentNullException>(() => runtime.RegisterSkill(null!, "owner"));
    }

    [Fact]
    public async Task ManualPackageLoad_ExposesPackagedSkillsThroughGetExtensionsWithSkills()
    {
        using var temp = new TemporaryDirectory();
        WriteSkill(Path.Combine(temp.Path, "skills", "packaged-skill"), "packaged-skill", "Shipped in the package.");
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));
        File.WriteAllText(Path.Combine(temp.Path, "index.js"), @"
module.exports = {
  name: 'mixed-skills',
  activate(api) {
    api.registerSkill({ name: 'runtime-skill', description: 'Registered in code.', instructions: 'runtime body', directory: 'docs' });
  }
};
");
        File.WriteAllText(Path.Combine(temp.Path, "extension.json"),
            "{\"name\":\"mixed-skills\",\"kind\":\"javascript\",\"entry\":\"index.js\"}");
        var package = await LoadPackageAsync(temp.Path);
        Assert.True(package.IsLoaded, string.Join(Environment.NewLine, package.Diagnostics.Select(item => item.Message)));
        Assert.Equal("packaged-skill", Assert.Single(package.Skills).Name);

        await using var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            // 手动加载的包：用 GetExtensionsWithSkills 把随包技能一起交给管道。
            Extensions = package.GetExtensionsWithSkills(),
            WorkingDirectory = temp.Path,
            EnableWorkflows = false
        });

        Assert.Equal(new[] { "packaged-skill", "runtime-skill" }, pipeline.Skills.Select(skill => skill.Name));
        Assert.Equal(Path.Combine(temp.Path, "docs"), pipeline.Skills.Single(skill => skill.Name == "runtime-skill").DirectoryPath);
        Assert.Equal(2, pipeline.Commands.Commands.Count(item => item.IsSkillCommand));
    }

    [Fact]
    public void SamplePackages_ShipSkillsThatLoadThroughThePublicLoader()
    {
        // 防止文档与示例脱节：两个示例包都必须真的带出可用技能。
        var helloJs = LoadSampleSkills("hello-js");
        Assert.Contains(helloJs, skill => skill.Name == "greeting-style");
        var greeting = helloJs.Single(skill => skill.Name == "greeting-style");
        Assert.Contains("问候语风格", greeting.Instructions);
        Assert.True(File.Exists(Path.Combine(greeting.DirectoryPath, "references", "tone.md")));
        Assert.Contains("hello_js_count", greeting.AllowedTools);

        var helloCSharp = LoadSampleSkills("hello-csharp");
        Assert.Contains(helloCSharp, skill => skill.Name == "counter-etiquette");
    }

    private static IReadOnlyList<SkillDefinition> LoadSampleSkills(string name)
    {
        var directory = Path.Combine(FindRepositoryRoot(), "samples", "extensions", name);
        var manifestPath = Path.Combine(directory, "extension.json");
        var manifest = ExtensionPackageManifest.Parse(File.ReadAllText(manifestPath), out var diagnostics, manifestPath);
        Assert.NotNull(manifest);
        Assert.DoesNotContain(diagnostics, item => item.Kind == PluginDiagnosticKind.Error);

        return ExtensionSkillLoader.Load(manifest);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AiTools.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("AiTools.slnx was not found from the test working directory.");
    }

    private static async Task<ExtensionPackage> LoadPackageAsync(string directory)
        => await new ExtensionPackageLoader().LoadAsync(directory, new ExtensionPackageLoaderOptions
        {
            ScriptProvider = new ScriptExtensionProvider(),
            BuildOnLoad = false,
            EnableDotnetBuild = false
        });

    private static void WriteSkill(string directory, string name, string description)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            "---\nname: " + name + "\ndescription: " + description + "\n---\n\nUse pdfplumber to extract text.\n");
    }

    private sealed class ProbeExtension : IExtension
    {
        private readonly Action<IExtensionApi> _configure;

        public ProbeExtension(string name, Action<IExtensionApi> configure)
        {
            Name = name;
            _configure = configure;
        }

        public string Name { get; }

        public string? Description => null;

        public void Activate(IExtensionApi api) => _configure(api);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix = "aitools-skills", string? parent = null)
        {
            Path = System.IO.Path.Combine(parent ?? System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
