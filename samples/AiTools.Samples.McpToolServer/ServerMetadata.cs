using System;
using System.Collections.Generic;

namespace AiTools.Samples.McpToolServer;

/// <summary>描述本示例 MCP 服务器的常量。</summary>
internal static class ServerMetadata
{
    /// <summary>在 <c>serverInfo</c> 与本地注册表中报告的默认服务器名称。</summary>
    internal const string DefaultName = "AiTools 示例工具服务";

    /// <summary>在 <c>serverInfo</c> 与本地注册表中报告的服务器版本。</summary>
    internal const string Version = "1.0.0";

    /// <summary>客户端未提出协议版本时使用的协议版本。</summary>
    internal const string DefaultProtocolVersion = "2025-06-18";

    /// <summary>Streamable HTTP 传输方式提供的 MCP 端点路径。</summary>
    internal const string McpPath = "/mcp";

    /// <summary>通过 <c>MCP-Protocol-Version</c> 标头接受的协议版本。</summary>
    internal static readonly IReadOnlyList<string> SupportedProtocolVersions =
        new[] { "2025-06-18", "2025-03-26" };

    /// <summary>由 <c>initialize</c> 请求返回的、便于阅读的使用说明。</summary>
    internal const string Instructions =
        "本服务器提供一组无外部依赖的本地工具：回显、时间、算术、文本统计、随机整数、哈希、Base64 与 UUID。" +
        "所有工具均为只读且不访问网络。请先调用 tools/list 查看每个工具的 JSON Schema，再通过 tools/call 执行。" +
        "工具参数错误会通过 isError=true 的文本结果返回，而不是 JSON-RPC 错误。";

    /// <summary>确定本服务器是否支持指定的协议版本。</summary>
    /// <param name="value">要测试的协议版本。</param>
    /// <returns>版本已知时返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    internal static bool IsSupportedProtocolVersion(string value)
    {
        foreach (var supported in SupportedProtocolVersions)
        {
            if (string.Equals(supported, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
