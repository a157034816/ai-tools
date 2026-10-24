using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiTools.Extensions;
using AiTools.Extensions.Packages;
using Xunit;

namespace AiTools.Tests.Extensions;

public sealed class ExtensionPackageTests
{
    [Fact]
    public void Parse_ValidManifestAcceptsCommentsTrailingCommasAndUnknownFields()
    {
        const string json = "{\n // package\n \"name\": \"valid-package\", \"kind\": \"CSharp\", \"version\": \"1.2.3\", \"project\": \"x.csproj\", \"unknown\": 42,\n}";

        var manifest = ExtensionPackageManifest.Parse(json, out var diagnostics);

        Assert.NotNull(manifest);
        Assert.Equal("valid-package", manifest.Name);
        Assert.Equal(ExtensionPackageKind.CSharp, manifest.Kind);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Parse_MissingNameReturnsManifestAndErrorDiagnostic()
    {
        var manifest = ExtensionPackageManifest.Parse("{\"kind\":\"javascript\",\"entry\":\"main.js\"}", out var diagnostics);

        Assert.NotNull(manifest);
        Assert.Equal(string.Empty, manifest.Name);
        Assert.Contains(diagnostics, item => item.Kind == PluginDiagnosticKind.Error && item.Message.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_BadJsonReturnsDiagnosticWithoutThrowing()
    {
        var manifest = ExtensionPackageManifest.Parse("{not-json", out var diagnostics);

        Assert.Null(manifest);
        Assert.Contains(diagnostics, item => item.Kind == PluginDiagnosticKind.Error);
    }

    [Theory]
    [InlineData("{\"name\":\"script-ts\",\"entry\":\"src/main.ts\"}", ExtensionPackageKind.TypeScript)]
    [InlineData("{\"name\":\"script-js\",\"entry\":\"main.js\"}", ExtensionPackageKind.JavaScript)]
    public void Parse_InfersScriptKindFromEntry(string json, ExtensionPackageKind expected)
    {
        var manifest = ExtensionPackageManifest.Parse(json, out var diagnostics);

        Assert.NotNull(manifest);
        Assert.Equal(expected, manifest.Kind);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Parse_InfersCSharpWhenProjectFileExists()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "package.csproj"), "<Project />");
        var manifestPath = Path.Combine(temp.Path, "extension.json");

        var manifest = ExtensionPackageManifest.Parse("{\"name\":\"cs-package\"}", out var diagnostics, manifestPath);

        Assert.NotNull(manifest);
        Assert.Equal(ExtensionPackageKind.CSharp, manifest.Kind);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Parse_UnknownFieldsAreIgnored()
    {
        var manifest = ExtensionPackageManifest.Parse("{\"name\":\"known\",\"x-custom\":{\"value\":true}}", out var diagnostics);

        Assert.NotNull(manifest);
        Assert.Equal("known", manifest.Name);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UnloadExtension_RemovesOnlyThatExtensionsCommandsAndTools()
    {
        var runtime = new ExtensionRuntime();
        runtime.Activate(new ProbeExtension("remove-me", api =>
        {
            api.RegisterCommand("remove-command", "remove", (_, _, _) => Task.FromResult(CommandResult.Display("removed")));
            api.RegisterTool(AiTools.Abstractions.ToolDefinition.Create("remove-tool"), (_, _, _) => Task.FromResult("removed"));
        }));
        runtime.Activate(new ProbeExtension("keep-me", api =>
        {
            api.RegisterCommand("keep-command", "keep", (_, _, _) => Task.FromResult(CommandResult.Display("kept")));
            api.RegisterTool(AiTools.Abstractions.ToolDefinition.Create("keep-tool"), (_, _, _) => Task.FromResult("kept"));
        }));

        runtime.UnloadExtension("remove-me");

        Assert.Null(runtime.TryGetCommand("remove-command"));
        Assert.Null(runtime.TryGetTool("remove-tool"));
        Assert.NotNull(runtime.TryGetCommand("keep-command"));
        Assert.NotNull(runtime.TryGetTool("keep-tool"));
    }

    [Fact]
    public async Task CSharpPackage_BuildsLoadsActivatesInvokesAndReloadsChangedBehavior()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "extensions");
        var packageDirectory = Path.Combine(root, "fixture-extension");
        CopyFixture(packageDirectory);
        var project = Path.Combine(packageDirectory, "FixtureExtension.csproj");
        var projectText = File.ReadAllText(project).Replace(
            "../../../src/AiTools.Extensions/AiTools.Extensions.csproj",
            Path.GetFullPath(Path.Combine(FindRepositoryRoot(), "src", "AiTools.Extensions", "AiTools.Extensions.csproj")),
            StringComparison.Ordinal);
        File.WriteAllText(project, projectText);

        var workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions
        {
            Directories = new[] { root },
            DataDirectory = Path.Combine(temp.Path, "data"),
            BuildOnLoad = true,
            Watch = false
        });
        await using var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            Workspace = workspace,
            EnableWorkflows = false,
            WorkingDirectory = temp.Path
        });

        var packages = await pipeline.LoadWorkspaceAsync();
        var package = Assert.Single(packages);
        Assert.True(package.IsLoaded, string.Join(Environment.NewLine, package.Diagnostics.Select(item => item.ToString())));
        Assert.EndsWith("FixtureExtension.dll", package.Manifest.Entry!);

        var first = await pipeline.ExecuteCommandAsync("/fixture-say", pipeline.Session.CreateContext(null, null));
        Assert.Equal("fixture response v1", first!.DisplayText);

        var source = Path.Combine(packageDirectory, "FixtureExtension.cs");
        var changed = File.ReadAllText(source).Replace("fixture response v1", "fixture response v2", StringComparison.Ordinal);
        File.WriteAllText(source, changed);
        var reloaded = await pipeline.ReloadPackageAsync("fixture-extension");
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsLoaded, string.Join(Environment.NewLine, reloaded.Diagnostics.Select(item => item.ToString())));

        var second = await pipeline.ExecuteCommandAsync("/fixture-say", pipeline.Session.CreateContext(null, null));
        Assert.Equal("fixture response v2", second!.DisplayText);
        var sampleDirectory = Path.Combine(temp.Path, "hello-csharp");
        CopyPackageDirectory(Path.Combine(FindRepositoryRoot(), "samples", "extensions", "hello-csharp"), sampleDirectory);
        var sampleProject = Path.Combine(sampleDirectory, "HelloCSharp.csproj");
        var sampleProjectText = File.ReadAllText(sampleProject).Replace(
            Path.Combine("..", "..", "..", "src", "AiTools.Extensions", "AiTools.Extensions.csproj"),
            Path.GetFullPath(Path.Combine(FindRepositoryRoot(), "src", "AiTools.Extensions", "AiTools.Extensions.csproj")),
            StringComparison.Ordinal);
        File.WriteAllText(sampleProject, sampleProjectText);
        var samplePackage = await new ExtensionPackageLoader().LoadAsync(sampleDirectory, new ExtensionPackageLoaderOptions
        {
            DataDirectory = Path.Combine(temp.Path, "sample-data"),
            BuildOnLoad = true
        });
        Assert.True(samplePackage.IsLoaded, string.Join(Environment.NewLine, samplePackage.Diagnostics.Select(item => item.ToString())));
        Assert.Empty(samplePackage.Diagnostics);
        Assert.Contains(samplePackage.Extensions, extension => extension.Name == "hello-csharp");
        samplePackage.Unload();
    }

    private static void CopyPackageDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private static void CopyFixture(string destination)
    {
        Directory.CreateDirectory(destination);
        var fixture = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "ExtensionPackageFixture");
        foreach (var file in Directory.GetFiles(fixture, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AiTools.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("AiTools.slnx was not found from the test working directory.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aitools-extension-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (Exception) { }
        }
    }

    private sealed class ProbeExtension : IExtension
    {
        private readonly Action<IExtensionApi> _configure;
        public ProbeExtension(string name, Action<IExtensionApi> configure) { Name = name; _configure = configure; }
        public string Name { get; }
        public string? Description => null;
        public void Activate(IExtensionApi api) => _configure(api);
    }
}
