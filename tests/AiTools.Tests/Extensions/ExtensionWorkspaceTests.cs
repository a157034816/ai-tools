using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiTools.Extensions.Packages;
using Xunit;
using AiTools.Extensions;

namespace AiTools.Tests.Extensions;

public sealed class ExtensionWorkspaceTests
{
    [Fact]
    public async Task Pipeline_RegistersExtensionManagerOnlyWhenWorkspaceIsConfigured()
    {
        using var temp = new TemporaryDirectory();
        await using var withoutWorkspace = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            EnableWorkflows = false,
            WorkingDirectory = temp.Path
        });
        Assert.Null(withoutWorkspace.Runtime.TryGetCommand("extension"));

        var workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions
        {
            Directories = new[] { Path.Combine(temp.Path, "extensions") },
            Watch = false
        });
        await using var withWorkspace = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = Array.Empty<IExtension>(),
            EnableWorkflows = false,
            Workspace = workspace,
            WorkingDirectory = temp.Path
        });
        Assert.NotNull(withWorkspace.Runtime.TryGetCommand("extension"));
    }

    [Fact]
    public async Task Scan_RejectsDuplicatePackageNames()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "extensions");
        CreateScriptPackage(root, "first", "duplicate-name");
        CreateScriptPackage(root, "second", "duplicate-name");
        using var workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions { Directories = new[] { root }, BuildOnLoad = false });

        var packages = await workspace.ScanAsync();

        Assert.Equal(2, packages.Count);
        Assert.Contains(packages.SelectMany(package => package.Diagnostics), diagnostic => diagnostic.Message.Contains("Duplicate extension package name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scan_ReportsRequireCyclesAndSkipsCyclicPackages()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "extensions");
        CreateScriptPackage(root, "alpha", "alpha", "beta");
        CreateScriptPackage(root, "beta", "beta", "alpha");
        using var workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions { Directories = new[] { root } });

        var packages = await workspace.ScanAsync();

        Assert.Equal(2, packages.Count);
        Assert.All(packages, package => Assert.False(package.IsLoaded));
        Assert.Contains(packages.SelectMany(package => package.Diagnostics), diagnostic => diagnostic.Message.Contains("dependency cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scan_ConvertsMalformedManifestIntoDiagnostics()
    {
        using var temp = new TemporaryDirectory();
        var root = Path.Combine(temp.Path, "extensions");
        var packageDirectory = Path.Combine(root, "broken");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "extension.json"), "{broken");
        using var workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions { Directories = new[] { root } });

        var packages = await workspace.ScanAsync();

        var package = Assert.Single(packages);
        Assert.False(package.IsLoaded);
        Assert.Contains(package.Diagnostics, diagnostic => diagnostic.Kind == AiTools.Extensions.PluginDiagnosticKind.Error);
        Assert.NotEmpty(workspace.Diagnostics);
    }

    [Fact]
    public async Task RemoveAsync_RefusesDirectoryWithoutGitMetadata()
    {
        using var temp = new TemporaryDirectory();
        var directory = Path.Combine(temp.Path, "not-a-repository");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "keep.txt"), "do not delete");
        var manager = new ExtensionSourceManager(temp.Path);

        var result = await manager.RemoveAsync("not-a-repository");

        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(directory));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("not its own Git working tree", StringComparison.Ordinal));
    }

    private static void CreateScriptPackage(string root, string directoryName, string name, params string[] requires)
    {
        var directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);
        var requireJson = string.Join(",", requires.Select(value => "\"" + value + "\""));
        File.WriteAllText(Path.Combine(directory, "extension.json"), "{\"name\":\"" + name + "\",\"kind\":\"JavaScript\",\"entry\":\"index.js\",\"require\":[" + requireJson + "]}");
        File.WriteAllText(Path.Combine(directory, "index.js"), "// fixture");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aitools-workspace-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (Exception) { }
        }
    }
}
