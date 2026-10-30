using System;
using System.Collections.Generic;

namespace AiTools.Extensions.Packages;

/// <summary>
/// 把一个包扩展与它的随包技能绑定起来，实现 <see cref="IPackagedExtension"/>。
/// </summary>
/// <remarks>
/// 只在宿主手动加载包、把扩展直接交给管道时使用（<see cref="ExtensionPackage.GetExtensionsWithSkills"/>）。
/// 激活、名称与描述全部委托给被包装的扩展。
/// </remarks>
internal sealed class PackageExtensionBinding : IExtension, IPackagedExtension
{
    private readonly IExtension _inner;
    private readonly IReadOnlyList<AiTools.Skills.SkillDefinition> _skills;

    public PackageExtensionBinding(string packageName, IExtension inner, IReadOnlyList<AiTools.Skills.SkillDefinition> skills)
    {
        PackageName = packageName ?? throw new ArgumentNullException(nameof(packageName));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _skills = skills ?? Array.Empty<AiTools.Skills.SkillDefinition>();
    }

    public string PackageName { get; }

    public IReadOnlyList<AiTools.Skills.SkillDefinition> Skills => _skills;

    public string Name => _inner.Name;

    public string? Description => _inner.Description;

    public void Activate(IExtensionApi api) => _inner.Activate(api);
}
