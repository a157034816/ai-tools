using System;
using AiTools.Configuration;

namespace AiTools.Wpf;

/// <summary>
/// 当 AiTools.Wpf 的使用方式不正确时抛出（未初始化、重复初始化、宿主工厂不支持某项能力）。
/// 设置存储与服务商工厂的失败使用 <see cref="AiToolsConfigurationException"/>。
/// </summary>
public sealed class AiToolsWpfException : AiToolsConfigurationException
{
    /// <summary>使用消息文本创建异常。</summary>
    public AiToolsWpfException(string message)
        : base(message)
    {
    }

    /// <summary>使用消息文本和内部异常创建异常。</summary>
    public AiToolsWpfException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
