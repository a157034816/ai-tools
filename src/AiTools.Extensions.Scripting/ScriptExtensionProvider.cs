using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Extensions.Packages;
namespace AiTools.Extensions.Scripting;

/// <summary>为 JavaScript 和 TypeScript 扩展包生成可执行脚本并创建 Jint 扩展。</summary>
/// <remarks>
/// <para>TypeScript 默认使用内置、无工具链的 tokenizer strip-and-rewrite 转译器；不执行类型检查。</para>
/// <para>支持类型注解、interface/type 别名、包内 import/export、函数/类声明泛型、as、非空断言、数值/字符串字面量 enum、可选参数和 declare。拒绝命名空间、装饰器、abstract/implements/readonly、satisfies、keyof/infer/unique、JSX、动态 import、export =、调用点泛型参数与复杂/计算 enum 初始化，并报告源文件和行号。</para>
/// </remarks>
public sealed class ScriptExtensionProvider : IScriptExtensionProvider
{
    /// <summary>默认脚本处理器超时。</summary>
    public static readonly TimeSpan DefaultInvocationTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _invocationTimeout;

    /// <summary>创建脚本提供者。</summary>
    /// <param name="invocationTimeout">命令、工具与事件处理器的超时；省略时为 30 秒。</param>
    public ScriptExtensionProvider(TimeSpan? invocationTimeout = null)
    {
        _invocationTimeout = invocationTimeout ?? DefaultInvocationTimeout;
        if (_invocationTimeout <= TimeSpan.Zero || _invocationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(invocationTimeout), "脚本处理器超时必须是正的有限时长。");
        }
    }

    /// <inheritdoc />
    public bool Supports(ExtensionPackageKind kind)
        => kind is ExtensionPackageKind.JavaScript or ExtensionPackageKind.TypeScript;

    /// <inheritdoc />
    public async Task<ScriptExtensionPreparation> PrepareAsync(ExtensionPackageManifest manifest, CancellationToken cancellationToken)
    {
        var diagnostics = new List<PluginDiagnostic>();
        try
        {
            if (manifest == null)
            {
                diagnostics.Add(Error(null, null, "扩展清单不能为空。"));
                return Failed(diagnostics);
            }

            if (!Supports(manifest.Kind))
            {
                diagnostics.Add(Error(manifest, manifest.ManifestPath, "脚本提供者不支持包类型 " + manifest.Kind + "。"));
                return Failed(diagnostics);
            }

            if (string.IsNullOrWhiteSpace(manifest.RootPath) || string.IsNullOrWhiteSpace(manifest.Entry))
            {
                diagnostics.Add(Error(manifest, manifest.ManifestPath, "脚本扩展必须声明 package root 和 entry。"));
                return Failed(diagnostics);
            }

            var packageRoot = Path.GetFullPath(manifest.RootPath);
            var entryPath = ResolvePackagePath(packageRoot, manifest.Entry);
            if (entryPath == null)
            {
                diagnostics.Add(Error(manifest, manifest.Entry, "入口文件必须位于扩展包目录内。"));
                return Failed(diagnostics);
            }

            var expectedExtension = manifest.Kind == ExtensionPackageKind.TypeScript
                ? ".ts"
                : Path.GetExtension(entryPath);
            if (manifest.Kind == ExtensionPackageKind.TypeScript && !string.Equals(expectedExtension, ".ts", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(manifest, entryPath, "TypeScript entry 必须是 .ts 文件。"));
                return Failed(diagnostics);
            }

            if (manifest.Kind == ExtensionPackageKind.JavaScript &&
                !new[] { ".js", ".cjs", ".mjs" }.Contains(expectedExtension, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(manifest, entryPath, "JavaScript entry 必须是 .js、.cjs 或 .mjs 文件。"));
                return Failed(diagnostics);
            }

            if (!File.Exists(entryPath))
            {
                diagnostics.Add(Error(manifest, entryPath, "脚本入口文件不存在。"));
                return Failed(diagnostics);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var requiresBundle = manifest.Kind == ExtensionPackageKind.TypeScript || ScriptModuleCompiler.RequiresBundle(entryPath, manifest.Require);
            if (!requiresBundle)
            {
                ScriptModuleCompiler.ValidateJavaScript(entryPath);
                return new ScriptExtensionPreparation { Succeeded = true, ArtifactPath = entryPath, Diagnostics = diagnostics };
            }

            var compiled = await Task.Run(
                () => ScriptModuleCompiler.BuildBundle(entryPath, packageRoot, manifest, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var buildDirectory = Path.Combine(packageRoot, ".ai-tools", "build", DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(buildDirectory);
            var artifactPath = Path.Combine(buildDirectory, Path.GetFileNameWithoutExtension(entryPath) + ".js");
            await File.WriteAllTextAsync(artifactPath, compiled, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return new ScriptExtensionPreparation { Succeeded = true, ArtifactPath = Path.GetFullPath(artifactPath), Diagnostics = diagnostics };
        }
        catch (TypeScriptTranspileException ex)
        {
            diagnostics.Add(Error(manifest, ex.Path, ex.Message));
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Warning,
                PluginName = manifest?.Name,
                Path = manifest?.ManifestPath,
                Message = "脚本扩展准备已取消。"
            });
        }
        catch (Exception ex)
        {
            diagnostics.Add(Error(manifest, manifest?.ManifestPath ?? manifest?.Entry, "准备脚本扩展失败：" + ex.Message));
        }

        return Failed(diagnostics);
    }

    /// <inheritdoc />
    public IExtensionFactory Create(ExtensionPackageManifest manifest, ScriptExtensionPreparation preparation, Action<PluginDiagnostic> report)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        if (report == null) throw new ArgumentNullException(nameof(report));
        if (preparation == null) throw new ArgumentNullException(nameof(preparation));
        if (!preparation.Succeeded || string.IsNullOrWhiteSpace(preparation.ArtifactPath))
        {
            report(Error(manifest, preparation.ArtifactPath ?? manifest.ManifestPath,
                preparation.Succeeded
                    ? "脚本准备结果违反契约：Succeeded 为 true 时 ArtifactPath 不能为空。"
                    : "未能创建脚本扩展：准备阶段未成功。"));
            return EmptyFactory.Instance;
        }

        var artifactPath = Path.GetFullPath(preparation.ArtifactPath);
        if (!File.Exists(artifactPath))
        {
            report(Error(manifest, artifactPath, "脚本准备结果指向不存在的产物。"));
            return EmptyFactory.Instance;
        }

        return new ScriptExtensionFactory(manifest, artifactPath, _invocationTimeout, report);
    }

    private static ScriptExtensionPreparation Failed(IReadOnlyList<PluginDiagnostic> diagnostics)
        => new() { Succeeded = false, Diagnostics = diagnostics };

    private static PluginDiagnostic Error(ExtensionPackageManifest? manifest, string? path, string message)
        => new()
        {
            Kind = PluginDiagnosticKind.Error,
            PluginName = manifest?.Name,
            Path = path,
            Message = message
        };

    private static string? ResolvePackagePath(string root, string declared)
    {
        if (Path.IsPathRooted(declared)) return null;
        var full = Path.GetFullPath(Path.Combine(root, declared));
        if (!IsWithin(root, full)) return null;
        var resolvedRoot = ScriptModuleCompiler.ResolveLinks(root);
        var resolvedFile = ScriptModuleCompiler.ResolveLinks(full);
        return IsWithin(resolvedRoot, resolvedFile) ? resolvedFile : null;
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptyFactory : IExtensionFactory
    {
        public static readonly EmptyFactory Instance = new();
        public IReadOnlyList<AiTools.Extensions.IExtension> Create(IExtensionFactoryContext context)
            => Array.Empty<AiTools.Extensions.IExtension>();
    }

    private sealed class ScriptExtensionFactory : IExtensionFactory
    {
        private readonly ExtensionPackageManifest _manifest;
        private readonly string _artifactPath;
        private readonly TimeSpan _timeout;
        private readonly Action<PluginDiagnostic> _report;

        public ScriptExtensionFactory(ExtensionPackageManifest manifest, string artifactPath, TimeSpan timeout, Action<PluginDiagnostic> report)
        {
            _manifest = manifest;
            _artifactPath = artifactPath;
            _timeout = timeout;
            _report = report;
        }

        public IReadOnlyList<AiTools.Extensions.IExtension> Create(IExtensionFactoryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (_manifest.MinHostVersion is { } requiredVersion)
            {
                if (!TryCompareVersions(requiredVersion, context.HostVersion, out var comparison))
                {
                    _report(new PluginDiagnostic
                    {
                        Kind = PluginDiagnosticKind.Warning,
                        PluginName = context.PackageName,
                        Path = _manifest.ManifestPath,
                        Message = "Extension package '" + context.PackageName + "' has minHostVersion '" + requiredVersion + "' and host version '" + context.HostVersion + "' that cannot be compared; loading anyway."
                    });
                }
                else if (comparison > 0)
                {
                    _report(new PluginDiagnostic
                    {
                        Kind = PluginDiagnosticKind.Error,
                        PluginName = context.PackageName,
                        Path = _manifest.ManifestPath,
                        Message = "Extension package '" + context.PackageName + "' requires host version " + requiredVersion + " or higher, but the current host version is " + context.HostVersion + "."
                    });
                    return Array.Empty<AiTools.Extensions.IExtension>();
                }
            }
            return new AiTools.Extensions.IExtension[]
            {
                new ScriptExtension(_manifest, _artifactPath, context, _timeout, _report)
            };
        }

        private static bool TryCompareVersions(string requiredVersion, string hostVersion, out int comparison)
        {
            comparison = 0;
            if (!TryParseVersionComponents(requiredVersion, out var required) ||
                !TryParseVersionComponents(hostVersion, out var host)) return false;

            for (var index = 0; index < Math.Max(required.Length, host.Length); index++)
            {
                var requiredPart = index < required.Length ? required[index] : "0";
                var hostPart = index < host.Length ? host[index] : "0";
                comparison = requiredPart.Length.CompareTo(hostPart.Length);
                if (comparison == 0) comparison = string.CompareOrdinal(requiredPart, hostPart);
                if (comparison != 0) return true;
            }
            return true;
        }

        private static bool TryParseVersionComponents(string version, out string[] components)
        {
            components = Array.Empty<string>();
            version = version.Trim();
            var suffixStart = version.IndexOf('-');
            if (suffixStart >= 0)
            {
                if (suffixStart == 0 || suffixStart == version.Length - 1) return false;
                for (var index = suffixStart + 1; index < version.Length; index++)
                {
                    var character = version[index];
                    if (!(character is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '.' or '-')) return false;
                }
                version = version.Substring(0, suffixStart);
            }

            var parts = version.Split('.');
            for (var index = 0; index < parts.Length; index++)
            {
                if (parts[index].Length == 0) return false;
                for (var digit = 0; digit < parts[index].Length; digit++)
                    if (parts[index][digit] is < '0' or > '9') return false;
                var normalized = parts[index].TrimStart('0');
                parts[index] = normalized.Length == 0 ? "0" : normalized;
            }
            components = parts;
            return true;
        }
    }
}
