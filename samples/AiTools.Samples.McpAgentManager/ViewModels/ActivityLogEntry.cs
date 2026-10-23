using System;

namespace AiTools.Samples.McpAgentManager.ViewModels;

/// <summary>底部活动日志中的一行带时间戳的记录。</summary>
public sealed class ActivityLogEntry
{
    /// <summary>创建一条日志记录。</summary>
    /// <param name="timestamp">操作的本地时间。</param>
    /// <param name="agentName">智能体显示名称，或系统标签。</param>
    /// <param name="presetName">预设显示名称，或 <c>全部预设</c> 之类的范围标签。</param>
    /// <param name="action">简短的操作标签，例如 <c>添加</c> 或 <c>导出</c>。</param>
    /// <param name="message">便于人阅读的结果消息。</param>
    /// <param name="isSuccess">操作是否成功。</param>
    /// <param name="backupPath">写入前创建的备份文件（若存在）。</param>
    /// <param name="filePath">涉及的配置文件或导出文件（若存在）。</param>
    /// <param name="isSkipped">操作是否被有意跳过，例如需要用户输入的预设。</param>
    public ActivityLogEntry(
        DateTime timestamp,
        string agentName,
        string presetName,
        string action,
        string message,
        bool isSuccess,
        string? backupPath = null,
        string? filePath = null,
        bool isSkipped = false)
    {
        Timestamp = timestamp;
        AgentName = agentName;
        PresetName = presetName;
        Action = action;
        Message = message;
        IsSuccess = isSuccess;
        BackupPath = backupPath;
        FilePath = filePath;
        IsSkipped = isSkipped;
    }

    /// <summary>获取本地时间戳。</summary>
    public DateTime Timestamp { get; }

    /// <summary>获取按日志列格式化的时间戳。</summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss");

    /// <summary>获取智能体显示名称。</summary>
    public string AgentName { get; }

    /// <summary>获取预设或范围标签。</summary>
    public string PresetName { get; }

    /// <summary>获取简短的操作标签。</summary>
    public string Action { get; }

    /// <summary>获取结果消息。</summary>
    public string Message { get; }

    /// <summary>获取操作是否成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>获取操作是否被有意跳过。</summary>
    public bool IsSkipped { get; }

    /// <summary>获取本地化的结果标签。</summary>
    public string ResultText => IsSkipped ? "跳过" : IsSuccess ? "成功" : "失败";

    /// <summary>获取编辑器创建的备份文件路径（若有）。</summary>
    public string? BackupPath { get; }

    /// <summary>获取是否存在备份文件路径。</summary>
    public bool HasBackup => !string.IsNullOrWhiteSpace(BackupPath);

    /// <summary>获取作为第二行显示的备份标签。</summary>
    public string BackupText => "备份：" + BackupPath;

    /// <summary>获取涉及的配置文件或导出文件路径（若有）。</summary>
    public string? FilePath { get; }

    /// <summary>获取是否存在文件路径。</summary>
    public bool HasFilePath => !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>获取作为第二行显示的文件标签。</summary>
    public string FileText => "文件：" + FilePath;
}
