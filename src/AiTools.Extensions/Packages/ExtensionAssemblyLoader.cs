using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AiTools.Extensions.Packages;

/// <summary>从 C# 扩展包程序集发现扩展和扩展工厂。</summary>
public sealed class ExtensionAssemblyLoader
{
    /// <summary>加载程序集并创建其中声明的全部扩展。</summary>
    /// <param name="assemblyPath">程序集文件路径。</param>
    /// <param name="manifest">包清单。</param>
    /// <param name="context">扩展工厂上下文。</param>
    /// <param name="report">诊断回调。</param>
    /// <param name="unloadHandle">可卸载目标框架上的加载上下文句柄。</param>
    /// <returns>发现并创建的扩展。</returns>
    public IReadOnlyList<IExtension> Load(
        string assemblyPath,
        ExtensionPackageManifest manifest,
        IExtensionFactoryContext context,
        Action<PluginDiagnostic> report,
        out IDisposable? unloadHandle)
    {
        if (assemblyPath == null) throw new ArgumentNullException(nameof(assemblyPath));
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (report == null) throw new ArgumentNullException(nameof(report));
        unloadHandle = null;
        var result = new List<IExtension>();
        if (!File.Exists(assemblyPath))
        {
            Report(report, manifest, "Extension assembly was not found.", assemblyPath);
            return result;
        }

        Assembly assembly;
#if NET8_0_OR_GREATER
        PackageLoadContext? loadContext = null;
        try
        {
            loadContext = new PackageLoadContext(assemblyPath);
            assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            unloadHandle = new PackageUnloadHandle(loadContext);
            loadContext = null;
        }
        catch (Exception ex)
        {
            loadContext?.Unload();
            Report(report, manifest, "Failed to load extension assembly: " + ex.Message, assemblyPath);
            return result;
        }
#else
        try
        {
            assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
            Report(report, manifest, "Assembly unloading is unavailable on netstandard2.0; restart the host to release this extension assembly.", assemblyPath, PluginDiagnosticKind.Warning);
        }
        catch (Exception ex)
        {
            Report(report, manifest, "Failed to load extension assembly: " + ex.Message, assemblyPath);
            return result;
        }
#endif

        var types = GetTypes(assembly, manifest, assemblyPath, report);
        var factories = new List<IExtensionFactory>();
        foreach (var type in types)
        {
            if (type == null) continue;
            try
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters) continue;
                if (typeof(IExtension).IsAssignableFrom(type))
                    CreateExtension(type, manifest, assemblyPath, report, result);
                if (!typeof(IExtensionFactory).IsAssignableFrom(type)) continue;
                try
                {
                    var constructor = type.GetConstructor(Type.EmptyTypes);
                    if (constructor == null)
                    {
                        Report(report, manifest, "Extension factory has no public parameterless constructor: " + type.FullName, assemblyPath);
                        continue;
                    }
                    if (constructor.Invoke(null) is IExtensionFactory factory) factories.Add(factory);
                }
                catch (Exception ex)
                {
                    Report(report, manifest, "Failed to create extension factory " + type.FullName + ": " + Unwrap(ex).Message, assemblyPath);
                }
            }
            catch (Exception ex)
            {
                Report(report, manifest, "Failed to inspect extension type: " + ex.Message, assemblyPath);
            }
        }

        foreach (var factory in factories)
        {
            try
            {
                var produced = factory.Create(context);
                if (produced == null)
                {
                    Report(report, manifest, "Extension factory returned a null collection.", assemblyPath);
                    continue;
                }
                foreach (var extension in produced)
                {
                    if (extension == null)
                    {
                        Report(report, manifest, "Extension factory returned a null extension.", assemblyPath);
                        continue;
                    }
                    result.Add(extension);
                }
            }
            catch (Exception ex)
            {
                Report(report, manifest, "Extension factory failed: " + Unwrap(ex).Message, assemblyPath);
            }
        }

        if (result.Count == 0)
        {
            Report(report, manifest, "No usable IExtension implementations or factories were found.", assemblyPath);
            unloadHandle?.Dispose();
            unloadHandle = null;
        }
        return result;
    }

    private static IReadOnlyList<Type?> GetTypes(Assembly assembly, ExtensionPackageManifest manifest, string path, Action<PluginDiagnostic> report)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loaderException in ex.LoaderExceptions.Where(item => item != null))
            {
                Report(report, manifest, "A type in the extension assembly could not be loaded: " + loaderException!.Message, path);
            }
            return ex.Types;
        }
        catch (Exception ex)
        {
            Report(report, manifest, "Failed to inspect extension assembly types: " + ex.Message, path);
            return Array.Empty<Type>();
        }
    }

    private static void CreateExtension(Type type, ExtensionPackageManifest manifest, string path, Action<PluginDiagnostic> report, ICollection<IExtension> result)
    {
        try
        {
            var constructor = type.GetConstructor(Type.EmptyTypes);
            if (constructor == null)
            {
                Report(report, manifest, "Extension has no public parameterless constructor: " + type.FullName, path);
                return;
            }
            if (constructor.Invoke(null) is IExtension extension) result.Add(extension);
        }
        catch (Exception ex)
        {
            Report(report, manifest, "Failed to create extension " + type.FullName + ": " + Unwrap(ex).Message, path);
        }
    }

    private static Exception Unwrap(Exception exception)
        => exception is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : exception;

    private static void Report(Action<PluginDiagnostic> report, ExtensionPackageManifest manifest, string message, string path, PluginDiagnosticKind kind = PluginDiagnosticKind.Error)
        => report(new PluginDiagnostic { Kind = kind, PluginName = manifest.Name, Path = path, Message = message });

#if NET8_0_OR_GREATER
    private sealed class PackageLoadContext : System.Runtime.Loader.AssemblyLoadContext
    {
        private readonly string _directory;
        private readonly System.Runtime.Loader.AssemblyDependencyResolver _resolver;

        public PackageLoadContext(string assemblyPath)
            : base("extension:" + Path.GetFileNameWithoutExtension(assemblyPath) + ":" + Guid.NewGuid().ToString("N"), isCollectible: true)
        {
            _directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? Environment.CurrentDirectory;
            _resolver = new System.Runtime.Loader.AssemblyDependencyResolver(Path.GetFullPath(assemblyPath));
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name ?? string.Empty;
            if (IsHostAssembly(name)) return null;

            string? path = null;
            try { path = _resolver.ResolveAssemblyToPath(assemblyName); }
            catch (Exception) { }
            if (path == null)
            {
                var candidate = Path.Combine(_directory, name + ".dll");
                if (File.Exists(candidate)) path = candidate;
            }
            return path == null ? null : LoadFromAssemblyPath(path);
        }

        private static bool IsHostAssembly(string name)
        {
            if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("System", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("Jint", StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(name, "AiTools.Extensions", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "AiTools.Abstractions", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "AiTools.Core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "AiTools.Mcp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "AiTools.Skills", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class PackageUnloadHandle : IDisposable
    {
        private PackageLoadContext? _context;
        public PackageUnloadHandle(PackageLoadContext context) => _context = context;
        public void Dispose()
        {
            var context = _context;
            _context = null;
            context?.Unload();
        }
    }
#endif
}
