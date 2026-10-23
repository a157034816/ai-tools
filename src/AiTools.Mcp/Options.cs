using System;
using System.Collections.Generic;
using AiTools.Core.Http;

namespace AiTools.Mcp;

/// <summary>以子进程方式启动 MCP 服务器并通过标准 I/O 进行 JSON-RPC 通信的选项。</summary>
public sealed class StdioTransportOptions
{
    /// <summary>要启动的可执行文件，例如 <c>npx</c> 或服务器二进制文件的路径。</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>传递给 <see cref="Command"/> 的命令行参数。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>子进程的可选工作目录。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>在继承的环境变量之上应用的可选环境变量。</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}

/// <summary>MCP Streamable HTTP transport 的选项。</summary>
public sealed class StreamableHttpTransportOptions : AiHttpOptions
{
    /// <summary>完整的 MCP 端点 URL，例如 <c>http://localhost:3000/mcp</c>。</summary>
    public Uri? Endpoint { get; init; }
}

/// <summary>旧版 MCP HTTP+SSE transport 的选项。</summary>
public sealed class SseMcpTransportOptions : AiHttpOptions
{
    /// <summary>用于流式传输 server-sent events 的 GET 端点。</summary>
    public Uri? SseEndpoint { get; init; }
}

/// <summary>控制 MCP 客户端握手与请求行为的选项。</summary>
public sealed class McpClientOptions
{
    /// <summary>initialization 期间向服务器报告的客户端名称。</summary>
    public string ClientName { get; init; } = "AiTools.Mcp";

    /// <summary>initialization 期间向服务器报告的客户端版本。</summary>
    public string ClientVersion { get; init; } = "0.1.0";

    /// <summary>initialization 期间请求的协议版本。</summary>
    public string ProtocolVersion { get; init; } = "2025-06-18";

    /// <summary>应用于每个请求的超时时间。使用 <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> 可禁用它。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
