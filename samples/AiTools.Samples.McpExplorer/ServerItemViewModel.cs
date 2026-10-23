using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AiTools.Samples.McpDiscovery;
using AiTools.Samples.McpExplorer.ViewModels;

namespace AiTools.Samples.McpExplorer;

/// <summary>本地注册表中发现的一个 MCP 服务器。</summary>
public sealed class ServerItemViewModel : ObservableObject
{
    private bool _isAlive;

    /// <summary>从注册表条目创建 ViewModel。</summary>
    /// <param name="entry">注册表条目。</param>
    public ServerItemViewModel(McpRegistryEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _isAlive = entry.IsProcessAlive();
    }

    /// <summary>获取底层注册表条目。</summary>
    public McpRegistryEntry Entry { get; }

    /// <summary>获取稳定的注册表标识符。</summary>
    public string Id => Entry.Id;

    /// <summary>获取服务器显示名称。</summary>
    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? $"MCP 服务 {Entry.Pid}" : Entry.Name;

    /// <summary>获取服务器版本。</summary>
    public string Version => string.IsNullOrWhiteSpace(Entry.Version) ? "未知" : Entry.Version;

    /// <summary>获取带 <c>v</c> 前缀的版本。</summary>
    public string VersionText => "v" + Version;

    /// <summary>获取由名称和 PID 组成的显示标签。</summary>
    public string DisplayLabel => Entry.GetDisplayLabel();

    /// <summary>获取注册表中记录的传输类型。</summary>
    public string Transport => string.IsNullOrWhiteSpace(Entry.Transport) ? "未知" : Entry.Transport;

    /// <summary>获取 HTTP 端点；仅使用 stdio 的条目为 null。</summary>
    public string? Endpoint => Entry.Endpoint;

    /// <summary>获取记录的进程 ID。</summary>
    public int Pid => Entry.Pid;

    /// <summary>获取进程存活标记。</summary>
    public bool IsAlive
    {
        get => _isAlive;
        private set
        {
            if (SetProperty(ref _isAlive, value))
            {
                OnPropertyChanged(nameof(LivenessText));
                OnPropertyChanged(nameof(LivenessBrush));
            }
        }
    }

    /// <summary>获取中文存活状态标签。</summary>
    public string LivenessText => IsAlive ? "在线" : "离线";

    /// <summary>获取用于存活状态徽章的画刷。</summary>
    public System.Windows.Media.Brush LivenessBrush =>
        IsAlive ? UiBrushes.Success : UiBrushes.Danger;

    /// <summary>获取描述传输类型的徽章标签。</summary>
    public string TransportLabel =>
        Endpoint != null ? "HTTP" : string.Equals(Transport, McpTransportKinds.Stdio, StringComparison.OrdinalIgnoreCase) ? "stdio" : Transport;

    /// <summary>获取服务器在启动时上报的工具数量。</summary>
    public int ToolCount => Entry.Tools?.Count ?? 0;

    /// <summary>获取工具数量的显示文本。</summary>
    public string ToolCountText => $"{ToolCount} 个工具";

    /// <summary>获取启动时上报的、以逗号分隔的工具名称。</summary>
    public string ToolNamesText =>
        ToolCount == 0 ? "未上报工具名" : string.Join("、", Entry.Tools);

    /// <summary>获取端点的显示文本。</summary>
    public string EndpointText => Endpoint ?? "（无 HTTP 端点）";

    /// <summary>获取 PID 的显示文本。</summary>
    public string PidText => Pid <= 0 ? "（未知）" : Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>获取启动命令的显示文本。</summary>
    public string LaunchCommandText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Entry.LaunchCommand))
            {
                return "（未上报）";
            }

            var arguments = Entry.LaunchArguments is { Count: > 0 } ? " " + string.Join(" ", Entry.LaunchArguments) : string.Empty;
            return Entry.LaunchCommand + arguments;
        }
    }

    /// <summary>获取工作目录的显示文本。</summary>
    public string WorkingDirectoryText => string.IsNullOrWhiteSpace(Entry.WorkingDirectory) ? "（未上报）" : Entry.WorkingDirectory!;

    /// <summary>获取相对注册时间。</summary>
    public string RegisteredAtText => ToolInvocation.FormatRegisteredAt(Entry.RegisteredAt);

    /// <summary>获取注册表条目是否描述了一次 stdio 启动。</summary>
    public bool UsesStdio =>
        !string.IsNullOrWhiteSpace(Entry.LaunchCommand)
        && (string.Equals(Entry.Transport, McpTransportKinds.Stdio, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Entry.Endpoint));

    /// <summary>获取标识底层服务器实例的简短签名。</summary>
    public string Signature => Id + "|" + Entry.Pid + "|" + Entry.Endpoint;

    /// <summary>从操作系统刷新存活标记。</summary>
    public void RefreshLiveness() => IsAlive = Entry.IsProcessAlive();

    /// <summary>获取启动时上报的工具名称（如果有）。</summary>
    /// <returns>工具名称。</returns>
    public IReadOnlyList<string> GetReportedTools() =>
        Entry.Tools is { Count: > 0 } ? Entry.Tools.ToList() : Array.Empty<string>();
}
