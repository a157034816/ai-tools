using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Mcp;

/// <summary>用于与 MCP 服务器交换 JSON-RPC 2.0 消息的 transport。</summary>
/// <remarks>
/// 各实现允许多个调用并发执行 <see cref="SendAsync"/>，但仅支持单一消费者读取
/// <see cref="ReceiveAsync"/>。不支持多个接收循环同时读取同一 transport。
/// </remarks>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>获取 transport 当前是否已启动并可用。</summary>
    bool IsConnected { get; }

    /// <summary>启动 transport（启动子进程或打开 HTTP 流）。</summary>
    /// <param name="ct">取消令牌。</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>向服务器写入单条 JSON-RPC 消息。</summary>
    /// <param name="message">要发送的 JSON-RPC 消息。</param>
    /// <param name="ct">取消令牌。</param>
    Task SendAsync(JsonElement message, CancellationToken ct = default);

    /// <summary>逐个产出从服务器接收的 JSON-RPC 消息，直到 transport 关闭。</summary>
    /// <param name="ct">取消令牌。</param>
    IAsyncEnumerable<JsonElement> ReceiveAsync(CancellationToken ct = default);

    /// <summary>获取由 transport 协商得到的会话标识符（如适用）。</summary>
    string? SessionId { get; }
}
