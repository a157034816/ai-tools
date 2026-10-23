using System;
using System.Collections.Generic;

namespace AiTools.Samples.McpAgents;

/// <summary>agent 存储其 MCP 配置的方式。</summary>
public enum AgentKind
{
    /// <summary>顶层包含 <c>mcpServers</c> 对象的 JSON 文件。</summary>
    JsonMcpServers,

    /// <summary>MCP 服务位于顶层 <c>mcp</c> 对象下的 JSON 文件（OpenCode 风格）。</summary>
    JsonMcpObject,

    /// <summary>包含 <c>[mcp_servers.&lt;name&gt;]</c> 表的 TOML 文件。</summary>
    TomlMcpServers,

    /// <summary>包含 <c>mcp_servers</c> 映射的 YAML 文件（Hermes 风格）。</summary>
    YamlMcpServers,

    /// <summary>该 agent 没有已知的磁盘存储格式；仅提供通用 JSON 导出。</summary>
    Unsupported
}

/// <summary>安装/移除操作的结果。</summary>
public sealed class AgentOperationResult
{
    /// <summary>获取操作是否成功。</summary>
    public bool Success { get; init; }

    /// <summary>获取面向用户的消息。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>获取被修改的路径（如适用）。</summary>
    public string? Path { get; init; }

    /// <summary>获取修改文件前创建的备份路径（如适用）。</summary>
    public string? BackupPath { get; init; }

    internal static AgentOperationResult Ok(string message, string? path = null, string? backup = null)
        => new() { Success = true, Message = message, Path = path, BackupPath = backup };

    internal static AgentOperationResult Fail(string message, string? path = null)
        => new() { Success = false, Message = message, Path = path };
}
