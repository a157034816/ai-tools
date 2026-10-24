using System;
using System.Collections.Generic;

namespace AiTools.Extensions.Packages;

/// <summary>根据包清单创建一个或多个扩展实例。</summary>
public interface IExtensionFactory
{
    /// <summary>创建该工厂提供的全部扩展实例。</summary>
    /// <param name="context">扩展工厂上下文。</param>
    /// <returns>创建的扩展实例。</returns>
    IReadOnlyList<IExtension> Create(IExtensionFactoryContext context);
}

/// <summary>扩展工厂运行时可用的包信息与诊断回调。</summary>
public sealed class IExtensionFactoryContext
{
    /// <summary>创建工厂上下文。</summary>
    /// <param name="packageName">包名称。</param>
    /// <param name="packageDirectory">包目录。</param>
    /// <param name="dataDirectory">包数据目录。</param>
    /// <param name="hostVersion">宿主版本。</param>
    /// <param name="report">诊断回调。</param>
    public IExtensionFactoryContext(string packageName, string packageDirectory, string dataDirectory, string hostVersion, Action<PluginDiagnostic> report)
    {
        PackageName = packageName ?? throw new ArgumentNullException(nameof(packageName));
        PackageDirectory = packageDirectory ?? throw new ArgumentNullException(nameof(packageDirectory));
        DataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        HostVersion = hostVersion ?? throw new ArgumentNullException(nameof(hostVersion));
        Report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>获取包名称。</summary>
    public string PackageName { get; }

    /// <summary>获取包目录。</summary>
    public string PackageDirectory { get; }

    /// <summary>获取包数据目录。</summary>
    public string DataDirectory { get; }

    /// <summary>获取宿主版本。</summary>
    public string HostVersion { get; }

    /// <summary>获取诊断回调。</summary>
    public Action<PluginDiagnostic> Report { get; }
}
