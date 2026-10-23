using System;
using AiTools.Abstractions;

namespace AiTools.Mcp;

/// <summary>当 MCP 服务器返回 JSON-RPC error 对象时引发的异常。</summary>
public sealed class McpException : AiToolsException
{
    /// <summary>创建 MCP 协议异常。</summary>
    /// <param name="message">服务器返回的、便于人工阅读的错误消息。</param>
    /// <param name="code">JSON-RPC 错误码。</param>
    /// <param name="data">服务器提供的原始 JSON-RPC 错误数据（如有）。</param>
    /// <param name="innerException">可选的内层异常。</param>
    public McpException(string message, int code, string? data = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Data = data;
    }

    /// <summary>JSON-RPC 错误码（例如 -32601 表示 "method not found"）。</summary>
    public int Code { get; }

    /// <summary>原始 JSON-RPC 错误数据；服务器未提供时为 null。</summary>
    public new string? Data { get; }
}
