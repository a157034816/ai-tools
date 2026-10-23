using System;
using System.Collections.Generic;

namespace AiTools.Samples.McpDiscovery;

/// <summary>记录在本地注册表中的传输类型。</summary>
public static class McpTransportKinds
{
    /// <summary>MCP Streamable HTTP endpoint。</summary>
    public const string StreamableHttp = "streamable-http";

    /// <summary>基于标准输入/输出的 MCP（由客户端启动进程）。</summary>
    public const string Stdio = "stdio";
}

/// <summary>在本机注册的一个 MCP 服务实例。</summary>
public sealed class McpRegistryEntry
{
    /// <summary>每次服务启动时重新生成的稳定标识符。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>面向用户的服务名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>服务版本字符串。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>传输类型；参见 <see cref="McpTransportKinds"/>。</summary>
    public string Transport { get; set; } = McpTransportKinds.StreamableHttp;

    /// <summary>HTTP 传输的 MCP endpoint URL，例如 <c>http://127.0.0.1:5220/mcp</c>。</summary>
    public string? Endpoint { get; set; }

    /// <summary>服务的进程 ID。</summary>
    public int Pid { get; set; }

    /// <summary>进程启动时间（UTC），用于检测 PID 复用。</summary>
    public DateTime ProcessStartedAt { get; set; }

    /// <summary>此条目的注册时间（UTC）。</summary>
    public DateTime RegisteredAt { get; set; }

    /// <summary>以 stdio 模式重新启动服务所用的可执行文件（已知时）。</summary>
    public string? LaunchCommand { get; set; }

    /// <summary>以 stdio 模式重新启动服务所用的参数（已知时）。</summary>
    public List<string> LaunchArguments { get; set; } = new();

    /// <summary>重新启动时的工作目录（已知时）。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>服务公开的 Tool 名称（服务在启动时已上报时）。</summary>
    public List<string> Tools { get; set; } = new();

    /// <summary>可选的自由格式元数据。</summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>创建深拷贝。</summary>
    public McpRegistryEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        Version = Version,
        Transport = Transport,
        Endpoint = Endpoint,
        Pid = Pid,
        ProcessStartedAt = ProcessStartedAt,
        RegisteredAt = RegisteredAt,
        LaunchCommand = LaunchCommand,
        LaunchArguments = new List<string>(LaunchArguments),
        WorkingDirectory = WorkingDirectory,
        Tools = new List<string>(Tools),
        Metadata = Metadata == null ? null : new Dictionary<string, string>(Metadata)
    };

    /// <summary>获取记录的进程是否仍在运行。</summary>
    public bool IsProcessAlive() => McpRegistry.IsProcessAlive(Pid, ProcessStartedAt);

    /// <summary>获取简短的显示标签，例如 <c>name (pid 1234)</c>。</summary>
    public string GetDisplayLabel() => string.IsNullOrWhiteSpace(Name) ? $"MCP 服务 {Pid}" : $"{Name} · PID {Pid}";
}
