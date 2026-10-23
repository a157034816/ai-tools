using System;

namespace AiTools.Extensions;

/// <summary>发现或加载插件时遇到的问题的严重级别。</summary>
public enum PluginDiagnosticKind
{
    /// <summary>信息，例如某个组件被有意忽略。</summary>
    Info,

    /// <summary>可恢复的问题，插件或组件仍可能部分工作。</summary>
    Warning,

    /// <summary>该插件无法加载。</summary>
    Error
}

/// <summary>插件发现/加载过程中产生的一条诊断。加载过程永不抛出异常。</summary>
public sealed class PluginDiagnostic
{
    /// <summary>获取诊断的严重级别。</summary>
    public PluginDiagnosticKind Kind { get; init; }

    /// <summary>获取相关插件的名称；加载单个插件失败前可能为空。</summary>
    public string? PluginName { get; init; }

    /// <summary>获取与问题相关的路径。</summary>
    public string? Path { get; init; }

    /// <summary>获取人类可读的问题描述。</summary>
    public string Message { get; init; } = string.Empty;

    /// <inheritdoc />
    public override string ToString()
    {
        var location = string.IsNullOrEmpty(Path) ? string.Empty : " (" + Path + ")";
        var plugin = string.IsNullOrEmpty(PluginName) ? string.Empty : "[" + PluginName + "] ";
        return Kind + ": " + plugin + Message + location;
    }
}

/// <summary>执行插件命令时无法继续（例如 shell 注入失败）所抛出的异常。</summary>
public sealed class CommandExecutionException : Exception
{
    /// <summary>使用消息文本创建异常。</summary>
    public CommandExecutionException(string message)
        : base(message)
    {
    }

    /// <summary>使用消息文本和内部异常创建异常。</summary>
    public CommandExecutionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
