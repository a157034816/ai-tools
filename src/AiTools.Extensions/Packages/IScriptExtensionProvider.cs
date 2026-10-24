using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Extensions.Packages;

/// <summary>脚本扩展包的准备结果：转译/打包产物位置与诊断。</summary>
public sealed class ScriptExtensionPreparation
{
    /// <summary>获取准备是否成功。为 false 时包不会被创建。</summary>
    public bool Succeeded { get; init; }

    /// <summary>获取产物（可执行的 .js 文件）的绝对路径；无需转译且入口本身即可执行时为入口路径。</summary>
    public string? ArtifactPath { get; init; }

    /// <summary>获取准备过程中发现的问题。</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; init; } = Array.Empty<PluginDiagnostic>();
}

/// <summary>JS/TS 扩展包的解释器提供者。由 AiTools.Extensions.Scripting 实现；宿主按需注入。</summary>
public interface IScriptExtensionProvider
{
    /// <summary>判断是否支持该包类型。</summary>
    bool Supports(ExtensionPackageKind kind);

    /// <summary>为脚本包准备可执行产物（转译、打包）。返回结果，不抛异常，不修改清单。</summary>
    Task<ScriptExtensionPreparation> PrepareAsync(ExtensionPackageManifest manifest, CancellationToken cancellationToken);

    /// <summary>为脚本包创建扩展工厂。</summary>
    /// <param name="manifest">已解析的清单。</param>
    /// <param name="preparation">准备阶段的产物路径；<see cref="ScriptExtensionPreparation.Succeeded"/> 为 false 时不应被调用。</param>
    /// <param name="report">诊断上报回调。</param>
    IExtensionFactory Create(ExtensionPackageManifest manifest, ScriptExtensionPreparation preparation, Action<PluginDiagnostic> report);
}
