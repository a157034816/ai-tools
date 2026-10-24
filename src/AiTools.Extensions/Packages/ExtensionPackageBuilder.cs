using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Extensions.Packages;

/// <summary>构建扩展包或选择既有构建产物的结果。</summary>
public sealed class ExtensionPackageBuildResult
{
    /// <summary>获取构建输出目录；未找到输出时为 null。</summary>
    public string? BuildDirectory { get; init; }

    /// <summary>获取要加载的程序集或脚本入口路径。</summary>
    public string? EntryPath { get; init; }

    /// <summary>获取构建诊断。</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; init; } = Array.Empty<PluginDiagnostic>();

    /// <summary>获取脚本提供者返回的准备结果；C# 包为 null。</summary>
    public ScriptExtensionPreparation? ScriptPreparation { get; init; }

    /// <summary>获取构建是否成功。</summary>
    public bool Succeeded => !string.IsNullOrWhiteSpace(EntryPath) && !Diagnostics.Any(item => item.Kind == PluginDiagnosticKind.Error);
}

/// <summary>构建扩展包，并为每次 C# 构建分配独立输出目录。</summary>
public sealed class ExtensionPackageBuilder
{
    private static long _lastBuildTicks;
    /// <summary>构建 C# 扩展包或准备脚本扩展包。</summary>
    /// <param name="manifest">包清单。</param>
    /// <param name="buildOnLoad">是否执行新构建。</param>
    /// <param name="enableDotnetBuild">是否允许调用 dotnet build。</param>
    /// <param name="timeout">构建超时时间；null 时使用 120 秒。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="scriptProvider">可选脚本扩展提供者。</param>
    /// <returns>构建路径和诊断。</returns>
    public async Task<ExtensionPackageBuildResult> BuildAsync(
        ExtensionPackageManifest manifest,
        bool buildOnLoad = true,
        bool enableDotnetBuild = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        IScriptExtensionProvider? scriptProvider = null)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        var diagnostics = new List<PluginDiagnostic>();
        var root = manifest.RootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            diagnostics.Add(Diagnostic(manifest, "Package root directory is unavailable.", root));
            return Result(diagnostics);
        }

        if (manifest.Kind != ExtensionPackageKind.CSharp)
        {
            if (scriptProvider == null)
            {
                diagnostics.Add(Diagnostic(manifest, "No IScriptExtensionProvider is configured for the " + manifest.Kind + " package.", root));
                return Result(diagnostics);
            }
            if (!scriptProvider.Supports(manifest.Kind))
            {
                diagnostics.Add(Diagnostic(manifest, "The configured script provider does not support " + manifest.Kind + " packages.", root));
                return Result(diagnostics);
            }
            ScriptExtensionPreparation? preparation;
            try
            {
                preparation = await scriptProvider.PrepareAsync(manifest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic(manifest, "Script package preparation failed: " + ex.Message, root));
                return Result(diagnostics);
            }
            if (preparation == null)
            {
                diagnostics.Add(Diagnostic(manifest, "Script provider returned no preparation result.", root));
                return Result(diagnostics);
            }
            diagnostics.AddRange(preparation.Diagnostics ?? Array.Empty<PluginDiagnostic>());
            if (!preparation.Succeeded)
            {
                if (!diagnostics.Any(item => item.Kind == PluginDiagnosticKind.Error))
                    diagnostics.Add(Diagnostic(manifest, "Script package preparation did not succeed.", root));
                return new ExtensionPackageBuildResult { Diagnostics = diagnostics, ScriptPreparation = preparation };
            }
            if (string.IsNullOrWhiteSpace(preparation.ArtifactPath) || !Path.IsPathRooted(preparation.ArtifactPath) || !File.Exists(preparation.ArtifactPath))
            {
                diagnostics.Add(Diagnostic(manifest, "Script provider did not return an existing absolute artifact path.", preparation.ArtifactPath ?? root));
                return new ExtensionPackageBuildResult { Diagnostics = diagnostics, ScriptPreparation = preparation };
            }
            return new ExtensionPackageBuildResult
            {
                BuildDirectory = Path.GetDirectoryName(preparation.ArtifactPath),
                EntryPath = preparation.ArtifactPath,
                Diagnostics = diagnostics,
                ScriptPreparation = preparation
            };
        }

        var project = ResolveProject(manifest);
        if (project == null)
        {
            diagnostics.Add(Diagnostic(manifest, "No C# project was found. Set the 'project' field to a .csproj relative to the package root.", root));
            return Result(diagnostics);
        }

        var outputRoot = ResolveOutputRoot(manifest);
        if (!buildOnLoad)
        {
            var existing = FindNewestBuild(outputRoot, manifest, project);
            if (existing == null)
            {
                diagnostics.Add(Diagnostic(manifest, "BuildOnLoad is disabled, but no existing extension build directory was found.", outputRoot));
                return Result(diagnostics);
            }
            return new ExtensionPackageBuildResult { BuildDirectory = Path.GetDirectoryName(existing), EntryPath = existing, Diagnostics = diagnostics };
        }

        if (!enableDotnetBuild)
        {
            diagnostics.Add(Diagnostic(manifest, "C# package build is disabled. EnableDotnetBuild or provide an existing build with BuildOnLoad disabled.", project));
            return Result(diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ticks = DateTime.UtcNow.Ticks;
        long previous;
        long next;
        do
        {
            previous = System.Threading.Interlocked.Read(ref _lastBuildTicks);
            next = Math.Max(ticks, previous + 1);
        }
        while (System.Threading.Interlocked.CompareExchange(ref _lastBuildTicks, next, previous) != previous);
        var buildDirectory = Path.Combine(outputRoot, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(buildDirectory);
            var command = "dotnet build " + Quote(project) + " -c Release -o " + Quote(buildDirectory) + " -m:1 -nodeReuse:false";
            await Task.Run(() => ShellCommandRunner.Run(command, root!, timeout ?? TimeSpan.FromSeconds(120)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            if (message.IndexOf("not recognized", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("No such file", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = "The .NET SDK command 'dotnet' is unavailable. Install the .NET SDK and ensure dotnet is on PATH. " + message;
            }
            diagnostics.Add(Diagnostic(manifest, "dotnet build failed: " + message, project));
            return Result(diagnostics, buildDirectory);
        }

        var assembly = ResolveAssembly(manifest, project, buildDirectory);
        if (assembly == null)
        {
            diagnostics.Add(Diagnostic(manifest, "Build succeeded, but no extension assembly was found in the output directory.", buildDirectory));
            return Result(diagnostics, buildDirectory);
        }
        return new ExtensionPackageBuildResult { BuildDirectory = buildDirectory, EntryPath = assembly, Diagnostics = diagnostics };
    }


    /// <summary>删除除最近成功构建外的旧 C# 构建目录，保留指定数量。</summary>
    /// <param name="manifest">包清单。</param>
    /// <param name="keep">要保留的最新目录数量。</param>
    public void PruneBuildDirectories(ExtensionPackageManifest manifest, int keep = 3)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep));
        if (string.IsNullOrWhiteSpace(manifest.RootPath)) return;
        var root = ResolveOutputRoot(manifest);
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.GetDirectories(root).OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal).Skip(keep))
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception) { }
        }
    }

    private static string? ResolveProject(ExtensionPackageManifest manifest)
    {
        var root = manifest.RootPath!;
        if (!string.IsNullOrWhiteSpace(manifest.Project))
        {
            var project = Path.GetFullPath(Path.Combine(root, manifest.Project));
            return File.Exists(project) ? project : null;
        }
        var projects = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
        return projects.Length == 1 ? projects[0] : null;
    }
    private static string ResolveOutputRoot(ExtensionPackageManifest manifest)
    {
        var relative = string.IsNullOrWhiteSpace(manifest.Output) ? Path.Combine("bin", "ext") : manifest.Output!;
        return Path.GetFullPath(Path.Combine(manifest.RootPath!, relative));
    }



    private static string? FindNewestBuild(string outputRoot, ExtensionPackageManifest manifest, string project)
    {
        if (!Directory.Exists(outputRoot)) return null;
        foreach (var directory in Directory.GetDirectories(outputRoot).OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            var assembly = ResolveAssembly(manifest, project, directory);
            if (assembly != null) return assembly;
        }
        return null;
    }

    private static string? ResolveAssembly(ExtensionPackageManifest manifest, string project, string directory)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Entry) && string.Equals(Path.GetExtension(manifest.Entry), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            var entry = Path.GetFullPath(Path.Combine(directory, manifest.Entry));
            if (File.Exists(entry)) return entry;
        }
        var expected = Path.Combine(directory, Path.GetFileNameWithoutExtension(project) + ".dll");
        if (File.Exists(expected)) return expected;
        if (!Directory.Exists(directory)) return null;
        return Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(path => !IsHostAssembly(Path.GetFileNameWithoutExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsHostAssembly(string name)
        => name.StartsWith("AiTools.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Jint", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static PluginDiagnostic Diagnostic(ExtensionPackageManifest manifest, string message, string? path)
        => new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = manifest.Name, Path = path, Message = message };

    private static ExtensionPackageBuildResult Result(IReadOnlyList<PluginDiagnostic> diagnostics, string? directory = null)
        => new ExtensionPackageBuildResult { BuildDirectory = directory, Diagnostics = diagnostics };
}
