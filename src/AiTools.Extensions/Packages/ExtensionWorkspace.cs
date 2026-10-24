using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Extensions.Packages;

/// <summary>扩展工作区配置。</summary>
public sealed class ExtensionWorkspaceOptions
{
    /// <summary>获取或设置包含扩展包目录的根目录。</summary>
    public IReadOnlyList<string> Directories { get; set; } = Array.Empty<string>();

    /// <summary>获取或设置扩展数据目录。</summary>
    public string? DataDirectory { get; set; }

    /// <summary>获取或设置是否加载时构建 C# 包。</summary>
    public bool BuildOnLoad { get; set; } = true;

    /// <summary>获取或设置是否监视包文件并自动重载。</summary>
    public bool Watch { get; set; }

    /// <summary>获取或设置文件系统变更防抖间隔。</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>获取或设置 C# 构建超时时间。</summary>
    public TimeSpan? BuildTimeout { get; set; }

    /// <summary>获取或设置是否允许 dotnet build。</summary>
    public bool EnableDotnetBuild { get; set; } = true;

    /// <summary>获取或设置 JS/TS 脚本提供者。</summary>
    public IScriptExtensionProvider? ScriptProvider { get; set; }
}

/// <summary>扩展包工作区；负责扫描、依赖排序、重载和文件监视。</summary>
public sealed class ExtensionWorkspace : IDisposable, IAsyncDisposable
{
    private static readonly string[] IgnoredDirectories = { "bin", "obj", "node_modules", ".ai-tools", ".git" };
    private static readonly HashSet<string> SupportedExtensions = new(new[] { ".cs", ".csproj", ".js", ".ts", ".json" }, StringComparer.OrdinalIgnoreCase);
    private readonly ExtensionWorkspaceOptions _options;
    private readonly ExtensionPackageLoader _loader;
    private readonly Dictionary<string, ExtensionPackage> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private ExtensionPipeline? _pipeline;
    private readonly ExtensionSourceManager _sourceManager;
    private readonly List<PluginDiagnostic> _diagnostics = new();
    private bool _disposed;

    /// <summary>创建扩展工作区。</summary>
    /// <param name="options">工作区配置。</param>
    /// <param name="loader">包加载器；为 null 时创建默认加载器。</param>
    public ExtensionWorkspace(ExtensionWorkspaceOptions options, ExtensionPackageLoader? loader = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loader = loader ?? new ExtensionPackageLoader();
        var directories = _options.Directories.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();
        if (directories.Length == 0) directories = ExtensionWorkspacePaths.ResolveDefaultRoots().ToArray();
        if (directories.Length == 0)
        {
            directories = new[] { Path.Combine(Environment.CurrentDirectory, ".ai-tools", "extensions") };
        }
        _options.Directories = directories.Distinct(PathComparer()).ToArray();
        var sourceRoot = _options.Directories[0];
        _sourceManager = new ExtensionSourceManager(sourceRoot, _options.BuildTimeout);
        if (_options.Watch) StartWatchers();
    }

    /// <summary>获取当前工作区包快照。</summary>
    public IReadOnlyList<ExtensionPackage> Packages
    {
        get { lock (_gate) return _packages.Values.OrderBy(package => package.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    /// <summary>获取单个包重载后的通知。</summary>
    public event Action<ExtensionPackage>? PackageReloaded;

    /// <summary>把工作区关联到宿主管道以支持扩展激活和重载。</summary>
    /// <param name="pipeline">目标管道。</param>
    public void AttachPipeline(ExtensionPipeline pipeline) => _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    /// <summary>获取当前首选工作区根目录的 Git 源码管理器。</summary>
    public ExtensionSourceManager Sources => _sourceManager;
    /// <summary>获取工作区扫描、监视和重载期间产生的诊断。</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics
    {
        get { lock (_gate) return _diagnostics.ToArray(); }
    }
    /// <summary>扫描各根目录的直接子目录并按 Require 依赖顺序加载包。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>扫描得到的包。</returns>
    public async Task<IReadOnlyList<ExtensionPackage>> ScanAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        lock (_gate) _diagnostics.Clear();
        var directories = new SortedSet<string>(PathComparer());
        foreach (var root in _options.Directories)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(Path.Combine(directory, "extension.json"))) directories.Add(directory);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lock (_gate) _diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, Path = root, Message = "Failed to scan extension root: " + ex.Message });
            }
        }

        var candidates = new List<ExtensionPackage>();
        var rejected = new List<ExtensionPackage>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, "extension.json");
            try
            {
                var manifest = ExtensionPackageManifest.Parse(File.ReadAllText(manifestPath), out var diagnostics, manifestPath);
                if (manifest == null)
                {
                    rejected.Add(await _loader.LoadAsync(directory, CreateLoaderOptions(Array.Empty<string>()), cancellationToken).ConfigureAwait(false));
                    continue;
                }
                var package = new ExtensionPackage(manifest, diagnostics);
                if (string.IsNullOrWhiteSpace(manifest.Name) || !IsValidPackageName(manifest.Name))
                {
                    package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = directory, Message = "Package name must be kebab-case and at most 64 characters." });
                    rejected.Add(package);
                    continue;
                }
                if (!names.Add(manifest.Name))
                {
                    package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = directory, Message = "Duplicate extension package name; package names must be unique across the workspace." });
                    rejected.Add(package);
                    continue;
                }
                candidates.Add(package);
            }
            catch (Exception ex)
            {
                rejected.Add(new ExtensionPackage(new ExtensionPackageManifest { RootPath = directory, ManifestPath = manifestPath }, new[]
                {
                    new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, Path = manifestPath, Message = "Failed to read package manifest: " + ex.Message }
                }));
            }
        }

        var orderedCandidates = OrderByRequirements(candidates);
        var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedByPath = new Dictionary<string, ExtensionPackage>(PathComparer());
        foreach (var candidate in orderedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = candidate.Manifest.RootPath!;
            if (candidate.Diagnostics.Any(item => item.Kind == PluginDiagnosticKind.Error))
            {
                loadedByPath[root] = candidate;
                continue;
            }
            var unavailableRequirement = candidate.Manifest.Require.FirstOrDefault(requirement => !loadedNames.Contains(requirement));
            if (unavailableRequirement != null)
            {
                candidate.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = candidate.Manifest.Name, Path = root, Message = "Required extension package is not loaded: " + unavailableRequirement });
                loadedByPath[root] = candidate;
                continue;
            }
            var package = await _loader.LoadAsync(root, CreateLoaderOptions(loadedNames), cancellationToken).ConfigureAwait(false);
            loadedByPath[root] = package;
            if (package.IsLoaded) loadedNames.Add(package.Manifest.Name);
        }

        foreach (var candidate in candidates)
        {
            if (!loadedByPath.ContainsKey(candidate.Manifest.RootPath!))
            {
                candidate.Unload();
                loadedByPath[candidate.Manifest.RootPath!] = candidate;
            }
        }
        var scanned = new List<ExtensionPackage>();
        foreach (var candidate in orderedCandidates) scanned.Add(loadedByPath[candidate.Manifest.RootPath!]);
        foreach (var candidate in rejected) scanned.Add(candidate);
        if (_pipeline != null)
        {
            await _pipeline.Runtime.CloseMcpServersAsync().ConfigureAwait(false);
            await _pipeline.Session.ShutdownAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            foreach (var old in _packages.Values)
            {
                if (_pipeline != null)
                    foreach (var extension in old.Extensions) _pipeline.Runtime.UnloadExtension(extension.Name);
                old.Unload();
            }
            _diagnostics.AddRange(scanned.SelectMany(package => package.Diagnostics));
            foreach (var package in scanned)
            {
                if (!string.IsNullOrWhiteSpace(package.Manifest.Name) && !_packages.ContainsKey(package.Manifest.Name))
                    _packages[package.Manifest.Name] = package;
            }
        }
        if (_pipeline != null) _pipeline.RefreshCommandCatalog();
        if (_pipeline != null) await _pipeline.ReloadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var package in scanned)
        {
            try { PackageReloaded?.Invoke(package); } catch (Exception) { }
        }
        return scanned;
    }

    /// <summary>卸载并重新构建、加载指定包，再通过管道重新激活。</summary>
    /// <param name="name">包名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重载后的包；未知名称时返回 null。</returns>
    public Task<ExtensionPackage?> ReloadPackageAsync(string name, CancellationToken cancellationToken = default)
        => ReloadPackageCoreAsync(name, forceBuild: false, cancellationToken);
    /// <summary>强制构建并重新加载指定包，无论工作区的 BuildOnLoad 选项如何设置。</summary>
    /// <param name="name">包名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>构建并加载后的包；未知名称时返回 null。</returns>
    public Task<ExtensionPackage?> BuildPackageAsync(string name, CancellationToken cancellationToken = default)
        => ReloadPackageCoreAsync(name, forceBuild: true, cancellationToken);

    private async Task<ExtensionPackage?> ReloadPackageCoreAsync(string name, bool forceBuild, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ExtensionPackage? existing;
        lock (_gate) _packages.TryGetValue(name, out existing);
        if (existing == null) return null;
        if (_pipeline != null)
        {
            await _pipeline.Runtime.CloseMcpServersAsync().ConfigureAwait(false);
            await _pipeline.Session.ShutdownAsync().ConfigureAwait(false);
        }
        foreach (var extension in existing.Extensions)
        {
            if (_pipeline != null) _pipeline.Runtime.UnloadExtension(extension.Name);
        }
        if (_pipeline != null) _pipeline.RefreshCommandCatalog();
        existing.Unload();
        var otherNames = new HashSet<string>(_packages.Keys.Where(key => !string.Equals(key, name, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        var package = await _loader.LoadAsync(existing.Manifest.RootPath!, CreateLoaderOptions(otherNames, forceBuild ? true : null), cancellationToken).ConfigureAwait(false);
        lock (_gate) _packages[name] = package;
        if (_pipeline != null) await _pipeline.ReloadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try { PackageReloaded?.Invoke(package); } catch (Exception) { }
        return package;
    }


    /// <summary>重新加载工作区全部包。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重载后的包。</returns>
    public async Task<IReadOnlyList<ExtensionPackage>> ReloadAllAsync(CancellationToken cancellationToken = default)
        => await ScanAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>获取所有已加载包中的扩展实例。</summary>
    public IReadOnlyList<IExtension> GetExtensions() => BuildExtensions();

    /// <summary>获取一个已发现包。</summary>
    /// <param name="name">包名称。</param>
    public ExtensionPackage? FindPackage(string name)
    {
        lock (_gate) return _packages.TryGetValue(name, out var package) ? package : null;
    }

    /// <summary>停止文件监视器并卸载包程序集。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        lock (_gate)
        {
            foreach (var package in _packages.Values)
            {
                if (_pipeline != null)
                    foreach (var extension in package.Extensions) _pipeline.Runtime.UnloadExtension(extension.Name);
                package.Unload();
            }
            _packages.Clear();
        }
        if (_pipeline != null) _pipeline.RefreshCommandCatalog();
    }

    /// <summary>异步停止文件监视器并卸载包程序集。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    private ExtensionPackageLoaderOptions CreateLoaderOptions(IReadOnlyCollection<string> existingNames, bool? buildOnLoad = null)
        => new ExtensionPackageLoaderOptions
        {
            DataDirectory = _options.DataDirectory,
            BuildOnLoad = buildOnLoad ?? _options.BuildOnLoad,
            BuildTimeout = _options.BuildTimeout,
            EnableDotnetBuild = _options.EnableDotnetBuild,
            ScriptProvider = _options.ScriptProvider,
            ExistingNames = existingNames
        };

    private IReadOnlyList<IExtension> BuildExtensions()
    {
        lock (_gate) return _packages.Values.Where(package => package.IsLoaded).SelectMany(package => package.Extensions).ToArray();
    }

    private List<ExtensionPackage> OrderByRequirements(IReadOnlyList<ExtensionPackage> packages)
    {
        var byName = new Dictionary<string, ExtensionPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (!string.IsNullOrWhiteSpace(package.Manifest.Name) && !byName.ContainsKey(package.Manifest.Name)) byName[package.Manifest.Name] = package;
        }
        var ordered = new List<ExtensionPackage>();
        var states = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        var cyclic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(ExtensionPackage package)
        {
            var name = package.Manifest.Name;
            if (states.TryGetValue(name, out var state))
            {
                if (state == 1)
                {
                    var start = stack.FindIndex(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
                    foreach (var cycleName in stack.Skip(Math.Max(0, start))) cyclic.Add(cycleName);
                }
                return;
            }
            states[name] = 1;
            stack.Add(name);
            foreach (var requirement in package.Manifest.Require)
            {
                if (!byName.TryGetValue(requirement, out var dependency))
                {
                    package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = name, Path = package.Manifest.RootPath, Message = "Required extension package was not found: " + requirement });
                    continue;
                }
                Visit(dependency);
            }
            stack.RemoveAt(stack.Count - 1);
            states[name] = 2;
            ordered.Add(package);
        }
        foreach (var package in packages) Visit(package);

        foreach (var name in cyclic)
        {
            if (byName.TryGetValue(name, out var package))
            {
                package.Unload();
                package.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = name, Path = package.Manifest.RootPath, Message = "Extension package Require dependency cycle detected; package skipped." });
            }
        }
        return ordered;
    }

    private void StartWatchers()
    {
        foreach (var root in _options.Directories)
        {
            try
            {
                Directory.CreateDirectory(root);
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Renamed += OnFileRenamed;
                watcher.Deleted += OnFileChanged;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                lock (_gate) _diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, Path = root, Message = "Failed to start extension root watcher: " + ex.Message });
            }
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs args) => ScheduleReload(args.FullPath);
    private void OnFileChanged(object sender, FileSystemEventArgs args) => ScheduleReload(args.FullPath);

    private void ScheduleReload(string path)
    {
        if (_disposed || !SupportedExtensions.Contains(Path.GetExtension(path)) || IsIgnoredPath(path)) return;
        var packageRoot = FindPackageRoot(path);
        if (packageRoot == null) return;
        lock (_gate)
        {
            if (_debounceTimers.TryGetValue(packageRoot, out var timer)) timer.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
            else _debounceTimers[packageRoot] = new Timer(async _ => await ReloadFromWatcherAsync(packageRoot).ConfigureAwait(false), null, _options.Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ReloadFromWatcherAsync(string packageRoot)
    {
        string? name;
        try
        {
            var manifest = ExtensionPackageManifest.Parse(File.ReadAllText(Path.Combine(packageRoot, "extension.json")), out var diagnostics, Path.Combine(packageRoot, "extension.json"));
            name = manifest?.Name;
            lock (_gate) _diagnostics.AddRange(diagnostics);
        }
        catch (Exception ex)
        {
            lock (_gate) _diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, Path = packageRoot, Message = "Failed to read package manifest during automatic reload: " + ex.Message });
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) return;
        try { await ReloadPackageAsync(name!, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { lock (_gate) _diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = name, Path = packageRoot, Message = "Automatic package reload failed: " + ex.Message }); }
    }

    private string? FindPackageRoot(string path)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "extension.json"))) return current;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent == null || !_options.Directories.Any(root => IsSameOrChild(parent, root))) break;
            current = parent;
        }
        return null;
    }

    private bool IsIgnoredPath(string path)
    {
        foreach (var root in _options.Directories)
        {
            if (!IsSameOrChild(path, root)) continue;
            var relative = GetRelativePath(root, path);
            if (relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => IgnoredDirectories.Contains(part, StringComparer.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static string GetRelativePath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullRoot, fullPath, comparison)) return ".";
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, comparison) ? fullPath.Substring(prefix.Length) : fullPath;
    }

    private static bool IsSameOrChild(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullRoot, fullPath, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
            || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsValidPackageName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64) return false;
        var previousDash = true;
        foreach (var character in name)
        {
            if (character == '-')
            {
                if (previousDash) return false;
                previousDash = true;
            }
            else if (character >= 'a' && character <= 'z' || character >= '0' && character <= '9') previousDash = false;
            else return false;
        }
        return !previousDash;
    }

    private static StringComparer PathComparer() => Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExtensionWorkspace));
    }
}

/// <summary>解析默认扩展工作区根目录。</summary>
public static class ExtensionWorkspacePaths
{
    /// <summary>按环境变量、项目本地目录和用户本地目录的优先级解析根目录，并追加存在的仓库本地扩展目录。</summary>
    /// <param name="workingDirectory">当前工作目录；null 时使用进程当前目录。</param>
    /// <returns>存在的唯一根目录。</returns>
    public static IReadOnlyList<string> ResolveDefaultRoots(string? workingDirectory = null)
    {
        var roots = new List<string>();
        var environment = Environment.GetEnvironmentVariable("AITOOLS_EXTENSIONS");
        var cwd = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            foreach (var path in environment.Split(Path.PathSeparator)) AddIfExists(roots, path);
        }
        else
        {
            var local = Path.Combine(cwd, ".ai-tools", "extensions");
            if (Directory.Exists(local)) AddIfExists(roots, local);
            else AddIfExists(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiTools", "extensions"));
        }
        var repositoryRoot = FindRepositoryRoot(cwd);
        if (repositoryRoot != null) AddIfExists(roots, Path.Combine(repositoryRoot, ".ai-tools", "extensions"));
        var comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return roots.Distinct(comparer).ToArray();
    }

    private static void AddIfExists(ICollection<string> roots, string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) roots.Add(Path.GetFullPath(path)); }
        catch (Exception) { }
    }

    private static string? FindRepositoryRoot(string path)
    {
        var current = new DirectoryInfo(path);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) return current.FullName;
            current = current.Parent;
        }
        return null;
    }
}
