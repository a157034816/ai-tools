using System;
using System.Collections.Generic;
using System.Linq;

namespace AiTools.Extensions.Packages;

/// <summary>已发现、构建或加载的扩展包状态。</summary>
public sealed class ExtensionPackage : IDisposable
{
    private IReadOnlyList<IExtension> _extensions = Array.Empty<IExtension>();
    private IDisposable? _unloadHandle;
    private bool _isLoaded;

    /// <summary>创建扩展包状态。</summary>
    /// <param name="manifest">包清单。</param>
    /// <param name="diagnostics">初始诊断。</param>
    public ExtensionPackage(ExtensionPackageManifest manifest, IEnumerable<PluginDiagnostic>? diagnostics = null)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Diagnostics = new List<PluginDiagnostic>(diagnostics ?? Array.Empty<PluginDiagnostic>());
    }

    /// <summary>获取包清单。</summary>
    public ExtensionPackageManifest Manifest { get; }

    /// <summary>获取包携带的技能，按名称排序；配置在 <c>skills/</c> 或清单 <c>skills</c> 声明的目录下。</summary>
    public IReadOnlyList<AiTools.Skills.SkillDefinition> Skills { get; private set; } = Array.Empty<AiTools.Skills.SkillDefinition>();

    /// <summary>获取包诊断。</summary>
    public IList<PluginDiagnostic> Diagnostics { get; }

    /// <summary>获取包中当前已加载的扩展实例。</summary>
    public IReadOnlyList<IExtension> Extensions => _extensions;

    /// <summary>获取包是否成功加载。</summary>
    public bool IsLoaded => _isLoaded;

    /// <summary>获取程序集加载上下文的卸载句柄；不可卸载的平台为 null。</summary>
    public IDisposable? UnloadHandle => _unloadHandle;

    /// <summary>释放包扩展引用并请求卸载其程序集加载上下文。</summary>
    public void Unload()
    {
        _isLoaded = false;
        _extensions = Array.Empty<IExtension>();
        Skills = Array.Empty<AiTools.Skills.SkillDefinition>();
        var handle = _unloadHandle;
        _unloadHandle = null;
        handle?.Dispose();
    }

    /// <summary>释放包资源。</summary>
    public void Dispose() => Unload();

    /// <summary>
    /// 获取把包扩展与随包技能绑在一起的扩展列表。
    /// 宿主手动加载包时把它们传给 <see cref="ExtensionPipelineOptions.Extensions"/>，
    /// 就能让包里的 <c>SKILL.md</c> 与代码一同生效（工作区路径不需要：它由 <see cref="ExtensionPipeline.RefreshPackagedSkills"/> 处理）。
    /// </summary>
    public IReadOnlyList<IExtension> GetExtensionsWithSkills()
    {
        if (Skills.Count == 0)
        {
            return Extensions;
        }

        return Extensions
            .Select(extension => (IExtension)new PackageExtensionBinding(Manifest.Name, extension, Skills))
            .ToArray();
    }

    internal void SetSkills(IEnumerable<AiTools.Skills.SkillDefinition> skills)
        => Skills = skills.ToArray();

    internal void SetLoaded(IEnumerable<IExtension> extensions, IDisposable? unloadHandle)
    {
        _extensions = extensions.ToArray();
        _unloadHandle = unloadHandle;
        _isLoaded = true;
    }
}
