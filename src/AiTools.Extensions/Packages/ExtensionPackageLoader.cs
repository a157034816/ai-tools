using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Extensions.Packages;

/// <summary>扩展包加载器选项。</summary>
public sealed class ExtensionPackageLoaderOptions
{
    /// <summary>获取或设置所有包数据目录的父目录。</summary>
    public string? DataDirectory { get; set; }

    /// <summary>获取或设置宿主版本字符串。</summary>
    public string HostVersion { get; set; } = "0.0.0";

    /// <summary>获取或设置是否在加载时构建。</summary>
    public bool BuildOnLoad { get; set; } = true;

    /// <summary>获取或设置是否允许 dotnet build。</summary>
    public bool EnableDotnetBuild { get; set; } = true;

    /// <summary>获取或设置构建超时时间。</summary>
    public TimeSpan? BuildTimeout { get; set; }

    /// <summary>获取或设置 JavaScript/TypeScript 提供者。</summary>
    public IScriptExtensionProvider? ScriptProvider { get; set; }

    /// <summary>获取或设置已占用的包名。</summary>
    public IReadOnlyCollection<string> ExistingNames { get; set; } = Array.Empty<string>();
}

/// <summary>读取、构建并加载一个磁盘扩展包。</summary>
public sealed class ExtensionPackageLoader
{
    private static readonly Regex NamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ExtensionPackageBuilder _builder;
    private readonly ExtensionAssemblyLoader _assemblyLoader;

    /// <summary>创建扩展包加载器。</summary>
    /// <param name="builder">包构建器；为 null 时创建默认构建器。</param>
    /// <param name="assemblyLoader">程序集加载器；为 null 时创建默认加载器。</param>
    public ExtensionPackageLoader(ExtensionPackageBuilder? builder = null, ExtensionAssemblyLoader? assemblyLoader = null)
    {
        _builder = builder ?? new ExtensionPackageBuilder();
        _assemblyLoader = assemblyLoader ?? new ExtensionAssemblyLoader();
    }

    /// <summary>读取清单并加载指定包；所有加载错误均转换为包诊断。</summary>
    /// <param name="packageDirectory">包根目录。</param>
    /// <param name="options">加载选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>包状态，即使加载失败也会包含诊断。</returns>
    public async Task<ExtensionPackage> LoadAsync(string packageDirectory, ExtensionPackageLoaderOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ExtensionPackageLoaderOptions();
        var root = packageDirectory ?? string.Empty;
        var manifestPath = Path.Combine(root, "extension.json");
        var diagnostics = new List<PluginDiagnostic>();
        ExtensionPackageManifest? manifest = null;
        try
        {
            if (!File.Exists(manifestPath))
            {
                diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, Path = manifestPath, Message = "Package manifest extension.json was not found." });
                return new ExtensionPackage(EmptyManifest(root, manifestPath), diagnostics);
            }
            var manifestJson = await Task.Run(() => File.ReadAllText(manifestPath), cancellationToken).ConfigureAwait(false);
            manifest = ExtensionPackageManifest.Parse(manifestJson, out var parseDiagnostics, manifestPath);
            diagnostics.AddRange(parseDiagnostics);
            if (manifest == null)
            {
                return new ExtensionPackage(EmptyManifest(root, manifestPath), diagnostics);
            }
            manifest = WithPaths(manifest, Path.GetFullPath(root), Path.GetFullPath(manifestPath));
            var package = new ExtensionPackage(manifest, diagnostics);
            package.SetSkills(ExtensionSkillLoader.Load(manifest, package.Diagnostics.Add));

            if (diagnostics.Any(item => item.Kind == PluginDiagnosticKind.Error)) return package;
            if (!NamePattern.IsMatch(manifest.Name) || manifest.Name.Length > 64)
            {
                package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = manifestPath, Message = "Package name must be kebab-case and at most 64 characters." });
                return package;
            }
            if (options.ExistingNames.Any(name => string.Equals(name, manifest.Name, StringComparison.OrdinalIgnoreCase)))
            {
                package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = root, Message = "Duplicate extension package name; package names must be unique across the workspace." });
                return package;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var factoryContext = CreateContext(manifest, options, package);
            var build = await _builder.BuildAsync(manifest, options.BuildOnLoad, options.EnableDotnetBuild, options.BuildTimeout, cancellationToken, options.ScriptProvider).ConfigureAwait(false);
            foreach (var diagnostic in build.Diagnostics) package.Diagnostics.Add(diagnostic);
            if (!build.Succeeded || build.EntryPath == null) return package;

            if (manifest.Kind == ExtensionPackageKind.CSharp)
            {
                var extensions = _assemblyLoader.Load(build.EntryPath, manifest, factoryContext, package.Diagnostics.Add, out var unloadHandle);
                var usableExtensions = FilterExtensions(extensions, manifest, build.EntryPath, package.Diagnostics.Add);
                if (usableExtensions.Count == 0)
                {
                    unloadHandle?.Dispose();
                    return package;
                }
                package.SetLoaded(usableExtensions, unloadHandle);
                _builder.PruneBuildDirectories(manifest, 3);
                return package;
            }

            var scriptProvider = options.ScriptProvider;
            var preparation = build.ScriptPreparation;
            if (scriptProvider == null || preparation == null)
            {
                package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = manifest.RootPath, Message = "Script package preparation result is unavailable." });
                return package;
            }
            if (!preparation.Succeeded)
            {
                package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = manifest.RootPath, Message = "Script package preparation did not succeed; the package was not created." });
                return package;
            }
            var factory = scriptProvider.Create(manifest, preparation, package.Diagnostics.Add);
            var created = factory.Create(factoryContext);
            if (created == null || created.Count == 0)
            {
                package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = manifest.RootPath, Message = "Script extension factory did not create any extensions." });
                return package;
            }
            var usableCreated = FilterExtensions(created, manifest, manifest.RootPath, package.Diagnostics.Add);
            if (usableCreated.Count == 0)
            {
                package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = manifest.RootPath, Message = "Script extension factory did not create any usable extensions." });
                return package;
            }
            package.SetLoaded(usableCreated, null);
            return package;
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Warning, PluginName = manifest?.Name, Path = root, Message = "Package loading was cancelled." });
            return new ExtensionPackage(manifest ?? EmptyManifest(root, manifestPath), diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest?.Name, Path = root, Message = "Package load failed: " + ex.Message });
            return new ExtensionPackage(manifest ?? EmptyManifest(root, manifestPath), diagnostics);
        }
    }

    private static IReadOnlyList<IExtension> FilterExtensions(
        IReadOnlyList<IExtension> extensions,
        ExtensionPackageManifest manifest,
        string? path,
        Action<PluginDiagnostic> report)
    {
        var usable = new List<IExtension>(extensions.Count);
        foreach (var extension in extensions)
        {
            if (extension == null)
            {
                report(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = path, Message = "Extension factory returned a null extension." });
                continue;
            }
            if (string.IsNullOrWhiteSpace(extension.Name))
            {
                report(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = path, Message = "Loaded extension has an empty name." });
                continue;
            }
            usable.Add(extension);
        }
        return usable;
    }

    private static IExtensionFactoryContext CreateContext(ExtensionPackageManifest manifest, ExtensionPackageLoaderOptions options, ExtensionPackage package)
    {
        var parent = string.IsNullOrWhiteSpace(options.DataDirectory)
            ? AiTools.Core.AiToolsPaths.GetExtensionDataDirectory()
            : options.DataDirectory!;
        var dataPath = Path.Combine(parent, manifest.Name);
        Directory.CreateDirectory(dataPath);
        return new IExtensionFactoryContext(manifest.Name, manifest.RootPath!, dataPath, options.HostVersion, package.Diagnostics.Add);
    }

    private static ExtensionPackageManifest WithPaths(ExtensionPackageManifest manifest, string root, string manifestPath)
        => new ExtensionPackageManifest
        {
            Name = manifest.Name,
            DisplayName = manifest.DisplayName,
            Version = manifest.Version,
            Description = manifest.Description,
            Kind = manifest.Kind,
            Entry = manifest.Entry,
            Project = manifest.Project,
            Output = manifest.Output,
            Require = manifest.Require,
            SkillPaths = manifest.SkillPaths,
            Keywords = manifest.Keywords,
            Metadata = manifest.Metadata,
            Repository = manifest.Repository,
            Author = manifest.Author,
            License = manifest.License,
            Homepage = manifest.Homepage,
            MinHostVersion = manifest.MinHostVersion,
            RootPath = root,
            ManifestPath = manifestPath
        };

    private static ExtensionPackageManifest EmptyManifest(string root, string manifestPath)
        => new ExtensionPackageManifest { RootPath = root, ManifestPath = manifestPath };
}
