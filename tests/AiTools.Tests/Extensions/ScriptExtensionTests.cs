using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Extensions;
using AiTools.Extensions.Packages;
using AiTools.Extensions.Scripting;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>验证脚本扩展的准备、运行时桥接、隔离与 TypeScript 转译器。</summary>
public sealed class ScriptExtensionTests
{
    [Fact]
    public async Task JavaScriptPackage_LoadsThroughPipelineAndBridgesCommandsToolsEventsAndState()
    {
        using var packageDirectory = CopyFixture("script-js");
        var package = await LoadPackageAsync(packageDirectory.Root, new ScriptExtensionProvider());
        Assert.True(package.IsLoaded);
        Assert.DoesNotContain(package.Diagnostics, item => item.Kind == PluginDiagnosticKind.Error);
        Assert.Equal("script-js", Assert.Single(package.Extensions).Name);

        await using var pipeline = CreatePipeline(package.Extensions, packageDirectory.Root);
        Assert.True(pipeline.ActivationDiagnostics.All(item => item.Kind != PluginDiagnosticKind.Error), string.Join(Environment.NewLine, pipeline.ActivationDiagnostics));
        Assert.Contains(pipeline.Runtime.Commands, item => item.Name == "script-hello");
        var context = pipeline.Session.CreateContext(null, null);
        Assert.Contains(pipeline.Runtime.Commands, item => item.Name == "script-hello" && item.ExtensionName == "script-js");
        var command = await pipeline.ExecuteCommandAsync("/script-hello world", context);

        Assert.NotNull(command);
        Assert.Equal(CommandExecutionKind.Display, command!.Kind);
        Assert.Equal("hello world (1)", command.DisplayText);

        var toolResult = await pipeline.Runtime.ExecuteToolAsync("script_echo", "{\"value\":\"roundtrip\"}", context);
        Assert.Equal("{\"echoed\":\"roundtrip\"}", toolResult.Text);
        Assert.Equal("roundtrip", pipeline.Runtime.State.Get<string>("script-js", "toolValue"));

        var preparation = await pipeline.PrepareTurnAsync(context, "test-model");
        Assert.Contains("JS count=1", preparation.SystemPromptSections);
        var restored = new ExtensionStateStore();
        restored.LoadFromJson(pipeline.ExportState());
        Assert.Equal(1, restored.Get<int>("script-js", "count"));
        Assert.Equal("roundtrip", restored.Get<string>("script-js", "toolValue"));
    }

    [Fact]
    public async Task TypeScriptPackage_TranspilesAndExecutesAcceptedConstructsWithoutExternalToolchain()
    {
        using var packageDirectory = CopyFixture("script-ts");
        var provider = new ScriptExtensionProvider();
        var manifest = ParseManifest(packageDirectory.Root);

        var prepared = await provider.PrepareAsync(manifest, CancellationToken.None);

        Assert.True(prepared.Succeeded, string.Join(Environment.NewLine, prepared.Diagnostics.Select(item => item.Message)));
        Assert.NotNull(prepared.ArtifactPath);
        Assert.EndsWith(Path.Combine(".ai-tools", "build"), Path.GetDirectoryName(Path.GetDirectoryName(prepared.ArtifactPath!))!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(prepared.ArtifactPath));
        var output = File.ReadAllText(prepared.ArtifactPath!);
        Assert.DoesNotContain("interface Arguments", output, StringComparison.Ordinal);
        Assert.DoesNotContain("type Counter", output, StringComparison.Ordinal);
        Assert.DoesNotContain("enum Stage", output, StringComparison.Ordinal);
        Assert.DoesNotContain(" as string", output, StringComparison.Ordinal);
        Assert.DoesNotContain("hostProvidedValue", output, StringComparison.Ordinal);

        var package = await LoadPackageAsync(packageDirectory.Root, provider);
        Assert.True(package.IsLoaded, string.Join(Environment.NewLine, package.Diagnostics.Select(item => item.Message)));
        await using var pipeline = CreatePipeline(package.Extensions, packageDirectory.Root);
        var context = pipeline.Session.CreateContext(null, null);
        var command = await pipeline.ExecuteCommandAsync("/script-ts-hello world", context);
        Assert.NotNull(command);
        Assert.True(command!.Kind == CommandExecutionKind.Display, command.Error);
        Assert.Contains("ts world ts 4 0 result yes", command.DisplayText, StringComparison.Ordinal);
        Assert.Equal("world-ok", pipeline.Runtime.State.Get<string>("script-ts", "tsName"));

        var toolResult = await pipeline.Runtime.ExecuteToolAsync("script_ts_echo", "{\"value\":\"typed\"}", context);
        Assert.Equal("{\"value\":\"typed\",\"suffix\":\"-tool\"}", toolResult.Text);
        var turn = await pipeline.PrepareTurnAsync(context, "test-model");
        Assert.Contains("TS state=world-ok", turn.SystemPromptSections);
    }

    [Fact]
    public async Task TypeScriptPackage_NamespaceImportResolvesAlongsideDefaultAndAliasedImports()
    {
        using var packageDirectory = new TempDirectory("script-ts-namespace-import");
        Directory.CreateDirectory(Path.Combine(packageDirectory.Root, "lib"));
        File.WriteAllText(Path.Combine(packageDirectory.Root, "lib", "exports.ts"),
            "export const someExport = 'namespace-value'; export const namedExport = 'named-value'; export default 'default-value';\n");
        File.WriteAllText(Path.Combine(packageDirectory.Root, "index.ts"),
            "import defaultValue, { someExport as aliased } from './lib/exports';\n" +
            "import * as g from './lib/exports';\n" +
            "module.exports = { name: 'namespace-import', activate(api) { api.registerCommand('namespace-import', 'Read import values', async (ctx) => ({ display: g.someExport + '|' + aliased + '|' + defaultValue })); } };\n");
        var manifest = new ExtensionPackageManifest
        {
            Name = "namespace-import",
            Kind = ExtensionPackageKind.TypeScript,
            Entry = "index.ts",
            RootPath = packageDirectory.Root,
            ManifestPath = Path.Combine(packageDirectory.Root, "extension.json")
        };
        var provider = new ScriptExtensionProvider();
        var preparation = await provider.PrepareAsync(manifest, CancellationToken.None);
        Assert.True(preparation.Succeeded, string.Join(Environment.NewLine, preparation.Diagnostics.Select(item => item.Message)));
        var diagnostics = new System.Collections.Generic.List<PluginDiagnostic>();
        var extensions = provider.Create(manifest, preparation, diagnostics.Add).Create(
            new IExtensionFactoryContext(manifest.Name, packageDirectory.Root, Path.Combine(packageDirectory.Root, "data"), "1.0.0", diagnostics.Add));
        await using var pipeline = CreatePipeline(extensions, packageDirectory.Root);
        var command = await pipeline.ExecuteCommandAsync("/namespace-import", pipeline.Session.CreateContext(null, null));

        Assert.NotNull(command);
        Assert.Equal(CommandExecutionKind.Display, command!.Kind);
        Assert.Equal("namespace-value|namespace-value|default-value", command.DisplayText);
    }

    [Theory]
    [InlineData("namespace Local { export const value = 1; }", "namespace declarations")]
    [InlineData("@sealed class Example {}", "decorators")]
    [InlineData("interface Contract {} class Example implements Contract {}", "class implements clauses")]
    [InlineData("abstract class Example {}", "abstract declarations")]
    [InlineData("class Example { readonly value: string = 'x'; }", "readonly modifiers")]
    [InlineData("const value = {} satisfies object;", "satisfies expressions")]
    [InlineData("type Key = keyof object;", "keyof type operators")]
    [InlineData("type Value = infer T;", "infer type operators")]
    [InlineData("const symbol: unique symbol = Symbol();", "unique symbol types")]
    [InlineData("const view = <div />;", "JSX syntax")]
    [InlineData("const load = import('./module');", "dynamic import")]
    [InlineData("export = extension;", "export = assignment")]
    [InlineData("function create<T>(value: T): T { return value; } const result = create<string>('x');", "call-site generic arguments")]
    [InlineData("enum Value { A = compute() }", "enum initializer must be a string or numeric literal")]
    [InlineData("enum Value { A = 1 << 2 }", "enum initializer must be a string or numeric literal")]
    [InlineData("enum Value { A = 1 + 1 }", "enum initializer must be a string or numeric literal")]
    [InlineData("enum Value { A = someConst }", "enum initializer must be a string or numeric literal")]
    [InlineData("enum Value { A = 'a' + 'b' }", "enum initializer must be a string or numeric literal")]
    [InlineData("enum Value { A = `x${y}` }", "enum initializer must be a string or numeric literal")]
    [InlineData("module Local { export const value = 1; }", "module declarations")]
    [InlineData("export * from './module';", "export * re-export")]
    [InlineData("declare global { interface Window { value: string; } }", "global declaration blocks")]
    [InlineData("enum Value { [computed] = 1 }", "computed enum member name")]
    [InlineData("enum Value { A = 'a', B }", "numeric enum member after a string initializer must specify a value")]
    public async Task TypeScriptPackage_RejectsUnsupportedConstructsWithSourceAndLine(string source, string construct)
    {
        using var packageDirectory = new TempDirectory("script-ts-reject");
        File.WriteAllText(Path.Combine(packageDirectory.Root, "index.ts"), source + Environment.NewLine + "module.exports = {};\n");
        var manifest = new ExtensionPackageManifest
        {
            Kind = ExtensionPackageKind.TypeScript,
            Entry = "index.ts",
            RootPath = packageDirectory.Root,
            ManifestPath = Path.Combine(packageDirectory.Root, "extension.json")
        };

        var prepared = await new ScriptExtensionProvider().PrepareAsync(manifest, CancellationToken.None);

        Assert.False(prepared.Succeeded);
        var diagnostic = Assert.Single(prepared.Diagnostics, item => item.Kind == PluginDiagnosticKind.Error);
        Assert.Contains("index.ts", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("index.ts:1", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(construct, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("function identity<T>(value: T): T { return value; }")]
    [InlineData("function pair<A, B>(left: A, right: B): [A, B] { return [left, right]; }")]
    [InlineData("class Box<T> { value: T; constructor(value: T) { this.value = value; } }")]
    [InlineData("interface Value<T> { value: T; }")]
    [InlineData("type Identity<T> = T;")]
    public async Task TypeScriptPackage_AcceptsDeclarationGenerics(string source)
    {
        using var packageDirectory = new TempDirectory("script-ts-generics");
        File.WriteAllText(Path.Combine(packageDirectory.Root, "index.ts"), source + Environment.NewLine + "module.exports = { name: 'generic-fixture', activate(api) {} };\n");
        var manifest = new ExtensionPackageManifest
        {
            Name = "generic-fixture",
            Kind = ExtensionPackageKind.TypeScript,
            Entry = "index.ts",
            RootPath = packageDirectory.Root,
            ManifestPath = Path.Combine(packageDirectory.Root, "extension.json")
        };

        var prepared = await new ScriptExtensionProvider().PrepareAsync(manifest, CancellationToken.None);

        Assert.True(prepared.Succeeded, string.Join(Environment.NewLine, prepared.Diagnostics.Select(item => item.Message)));
    }

    [Theory]
    [InlineData("function f(a: { g(k: string): number }): number { return a.g('x'); }")]
    [InlineData("function f(a: { a: string; b?: number }): string { return a.a; }")]
    [InlineData("function f(a: { [k: string]: number }): number { return a['x']; }")]
    [InlineData("function f(a: { a: { b: { c: string } } }): string { return a.a.b.c; }")]
    public async Task TypeScriptPackage_AcceptsBalancedInlineObjectTypes(string source)
    {
        using var packageDirectory = new TempDirectory("script-ts-inline-type");
        File.WriteAllText(Path.Combine(packageDirectory.Root, "index.ts"),
            source + Environment.NewLine + "module.exports = { name: 'inline-type', activate(api) {} };\n");
        var manifest = new ExtensionPackageManifest
        {
            Name = "inline-type",
            Kind = ExtensionPackageKind.TypeScript,
            Entry = "index.ts",
            RootPath = packageDirectory.Root,
            ManifestPath = Path.Combine(packageDirectory.Root, "extension.json")
        };

        var prepared = await new ScriptExtensionProvider().PrepareAsync(manifest, CancellationToken.None);

        Assert.True(prepared.Succeeded, string.Join(Environment.NewLine, prepared.Diagnostics.Select(item => item.Message)));
    }

    [Theory]
    [InlineData("enum Value { A = -4 }")]
    [InlineData("enum Value { A = 1e-2, B }")]
    [InlineData("enum Value { A = 0x10, B }")]
    [InlineData("enum Value { A = 0b10, B }")]
    [InlineData("enum Value { A = .5, B }")]
    public async Task TypeScriptPackage_AcceptsSingleLiteralEnumInitializers(string source)
    {
        using var packageDirectory = new TempDirectory("script-ts-literal-enum");
        File.WriteAllText(Path.Combine(packageDirectory.Root, "index.ts"),
            source + "\nmodule.exports = { name: 'literal-enum', activate(api) {} };\n");
        var manifest = new ExtensionPackageManifest
        {
            Name = "literal-enum",
            Kind = ExtensionPackageKind.TypeScript,
            Entry = "index.ts",
            RootPath = packageDirectory.Root,
            ManifestPath = Path.Combine(packageDirectory.Root, "extension.json")
        };

        var prepared = await new ScriptExtensionProvider().PrepareAsync(manifest, CancellationToken.None);

        Assert.True(prepared.Succeeded, string.Join(Environment.NewLine, prepared.Diagnostics.Select(item => item.Message)));
    }

    [Fact]
    public async Task ScriptHandler_TimeoutDoesNotPoisonThePackageEngine()
    {
        using var packageDirectory = CopyFixture("script-js");
        var package = await LoadPackageAsync(packageDirectory.Root, new ScriptExtensionProvider(TimeSpan.FromMilliseconds(200)));
        Assert.True(package.IsLoaded);
        await using var pipeline = CreatePipeline(package.Extensions, packageDirectory.Root);
        var context = pipeline.Session.CreateContext(null, null);

        var timedOut = await pipeline.ExecuteCommandAsync("/script-spin", context);
        Assert.NotNull(timedOut);
        Assert.Equal(CommandExecutionKind.Error, timedOut!.Kind);
        Assert.Contains("timed out", timedOut.Error, StringComparison.OrdinalIgnoreCase);

        var usable = await pipeline.ExecuteCommandAsync("/script-ok", context);
        Assert.NotNull(usable);
        Assert.Equal(CommandExecutionKind.Display, usable!.Kind);
        Assert.Equal("usable", usable.DisplayText);
    }

    [Fact]
    public async Task ScriptRequire_FilesystemModulesFailWithClearError()
    {
        using var packageDirectory = CopyFixture("script-js");
        var package = await LoadPackageAsync(packageDirectory.Root, new ScriptExtensionProvider());
        await using var pipeline = CreatePipeline(package.Extensions, packageDirectory.Root);

        var execution = await pipeline.ExecuteCommandAsync("/script-fs", pipeline.Session.CreateContext(null, null));

        Assert.NotNull(execution);
        Assert.Equal(CommandExecutionKind.Error, execution!.Kind);
        Assert.Contains("module 'fs' is unavailable", execution.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtensionWorkspace_LoadsScriptPackages()
    {
        using var workspaceRoot = new TempDirectory("script-workspace");
        using var packageDirectory = CopyFixture("script-js", workspaceRoot.Root);
        using var workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions
        {
            Directories = new[] { workspaceRoot.Root },
            ScriptProvider = new ScriptExtensionProvider(),
            BuildOnLoad = false
        });

        var packages = await workspace.ScanAsync();

        Assert.True(Assert.Single(packages).IsLoaded);
        Assert.Contains(workspace.GetExtensions(), extension => extension.Name == "script-js");
    }

    [Fact]
    public async Task ScriptContext_ToolAndCommandWorkingDirectoriesAgree()
    {
        using var packageDirectory = new TempDirectory("script-context-directory");
        using var workingDirectory = new TempDirectory("script-context-working-directory");
        File.WriteAllText(Path.Combine(packageDirectory.Root, "index.js"), @"
module.exports = {
  name: 'context-directory',
  activate(api) {
    api.registerCommand('context-directory-command', 'Read working directory', async ctx => ({ display: ctx.workingDirectory }));
    api.registerTool({ name: 'context_directory_tool', description: 'Read working directory', parameters: { type: 'object', properties: {} } }, async (args, ctx) => ({ workingDirectory: ctx.workingDirectory }));
  }
};
");
        var manifest = new ExtensionPackageManifest
        {
            Name = "context-directory",
            Kind = ExtensionPackageKind.JavaScript,
            Entry = "index.js",
            RootPath = packageDirectory.Root,
            ManifestPath = Path.Combine(packageDirectory.Root, "extension.json")
        };
        var provider = new ScriptExtensionProvider();
        var prepared = await provider.PrepareAsync(manifest, CancellationToken.None);
        Assert.True(prepared.Succeeded, string.Join(Environment.NewLine, prepared.Diagnostics.Select(item => item.Message)));
        var diagnostics = new System.Collections.Generic.List<PluginDiagnostic>();
        var extensions = provider.Create(manifest, prepared, diagnostics.Add).Create(
            new IExtensionFactoryContext(manifest.Name, packageDirectory.Root, Path.Combine(packageDirectory.Root, "data"), "1.0.0", diagnostics.Add));
        await using var pipeline = CreatePipeline(extensions, workingDirectory.Root);
        var context = pipeline.Session.CreateContext(null, null);

        var command = await pipeline.ExecuteCommandAsync("/context-directory-command", context);
        var tool = await pipeline.Runtime.ExecuteToolAsync("context_directory_tool", "{}", context);

        Assert.NotNull(command);
        Assert.Equal(workingDirectory.Root, command!.DisplayText);
        using var result = JsonDocument.Parse(tool.Text);
        Assert.Equal(workingDirectory.Root, result.RootElement.GetProperty("workingDirectory").GetString());
        var fallbackContext = new ToolContext
        {
            Runtime = pipeline.Runtime,
            State = pipeline.Runtime.State,
            WorkingDirectory = workingDirectory.Root
        };
        var fallbackTool = await fallbackContext.ExecuteToolAsync("context_directory_tool", "{}");
        using var fallbackResult = JsonDocument.Parse(fallbackTool);
        Assert.Equal(workingDirectory.Root, fallbackResult.RootElement.GetProperty("workingDirectory").GetString());
    }

    [Theory]
    [InlineData("1.3", "1.2.9", false)]
    [InlineData("1.2.0", "1.2", true)]
    [InlineData("1.2", "1.2.1", true)]
    [InlineData("1.2.0-beta", "1.2", true)]
    public async Task ScriptPackage_MinHostVersionControlsExtensionCreation(string minHostVersion, string hostVersion, bool shouldLoad)
    {
        var outcome = await CreateVersionedScriptExtensionAsync(minHostVersion, hostVersion);

        Assert.Equal(shouldLoad ? 1 : 0, outcome.ExtensionCount);
        if (shouldLoad)
        {
            Assert.DoesNotContain(outcome.Diagnostics, item => item.Kind == PluginDiagnosticKind.Error);
        }
        else
        {
            var diagnostic = Assert.Single(outcome.Diagnostics, item => item.Kind == PluginDiagnosticKind.Error);
            Assert.Contains("versioned-script", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains(minHostVersion, diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains(hostVersion, diagnostic.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ScriptPackage_MalformedHostVersionWarnsAndStillLoads()
    {
        var outcome = await CreateVersionedScriptExtensionAsync("1.0.0", "host-version");

        Assert.Equal(1, outcome.ExtensionCount);
        var diagnostic = Assert.Single(outcome.Diagnostics, item => item.Kind == PluginDiagnosticKind.Warning);
        Assert.Contains("versioned-script", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("host-version", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptPackage_MalformedMinHostVersionWarnsAndStillLoads()
    {
        var outcome = await CreateVersionedScriptExtensionAsync("not-a-version", "1.0.0");

        Assert.Equal(1, outcome.ExtensionCount);
        var diagnostic = Assert.Single(outcome.Diagnostics, item => item.Kind == PluginDiagnosticKind.Warning);
        Assert.Contains("versioned-script", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("not-a-version", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", diagnostic.Message, StringComparison.Ordinal);
    }

    private static async Task<(int ExtensionCount, System.Collections.Generic.IReadOnlyList<PluginDiagnostic> Diagnostics)> CreateVersionedScriptExtensionAsync(string? minHostVersion, string hostVersion)
    {
        using var packageDirectory = new TempDirectory("script-min-host-version");
        File.WriteAllText(Path.Combine(packageDirectory.Root, "index.js"), "module.exports = { name: 'versioned-script', activate(api) {} };\n");
        var manifest = new ExtensionPackageManifest
        {
            Name = "versioned-script",
            Kind = ExtensionPackageKind.JavaScript,
            Entry = "index.js",
            RootPath = packageDirectory.Root,
            ManifestPath = Path.Combine(packageDirectory.Root, "extension.json"),
            MinHostVersion = minHostVersion
        };
        var provider = new ScriptExtensionProvider();
        var prepared = await provider.PrepareAsync(manifest, CancellationToken.None);
        Assert.True(prepared.Succeeded, string.Join(Environment.NewLine, prepared.Diagnostics.Select(item => item.Message)));
        var diagnostics = new System.Collections.Generic.List<PluginDiagnostic>();
        var extensions = provider.Create(manifest, prepared, diagnostics.Add).Create(
            new IExtensionFactoryContext(manifest.Name, packageDirectory.Root, Path.Combine(packageDirectory.Root, "data"), hostVersion, diagnostics.Add));
        return (extensions.Count, diagnostics);
    }

    private static async Task<ExtensionPackage> LoadPackageAsync(string directory, ScriptExtensionProvider provider)
        => await new ExtensionPackageLoader().LoadAsync(directory, new ExtensionPackageLoaderOptions
        {
            ScriptProvider = provider,
            BuildOnLoad = false,
            EnableDotnetBuild = false
        });

    private static ExtensionPipeline CreatePipeline(System.Collections.Generic.IReadOnlyList<IExtension> extensions, string workingDirectory)
        => new(new ExtensionPipelineOptions
        {
            Extensions = extensions,
            WorkingDirectory = workingDirectory,
            EnableWorkflows = false
        });

    private static ExtensionPackageManifest ParseManifest(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "extension.json");
        var result = ExtensionPackageManifest.Parse(File.ReadAllText(path), out var diagnostics, path);
        Assert.NotNull(result);
        Assert.DoesNotContain(diagnostics, item => item.Kind == PluginDiagnosticKind.Error);
        return new ExtensionPackageManifest
        {
            Name = result!.Name,
            DisplayName = result.DisplayName,
            Description = result.Description,
            Kind = result.Kind,
            Entry = result.Entry,
            RootPath = packageRoot,
            ManifestPath = path
        };
    }

    private static TempDirectory CopyFixture(string name, string? destinationRoot = null)
    {
        var source = FindFixture(name);
        var destination = new TempDirectory("script-" + name, destinationRoot);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return destination;
    }

    private static string FindFixture(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var fixture = Path.Combine(directory.FullName, "tests", "fixtures", "extensions", name);
            if (Directory.Exists(fixture)) return fixture;
        }
        throw new DirectoryNotFoundException("Could not locate extension fixture '" + name + "'.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix, string? parent = null)
        {
            Root = Path.Combine(parent ?? Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
