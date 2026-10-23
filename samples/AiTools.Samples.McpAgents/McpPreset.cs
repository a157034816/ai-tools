using System;
using System.Collections.Generic;

namespace AiTools.Samples.McpAgents;

/// <summary>可安装到 agent 配置中的预设 MCP 服务。</summary>
public sealed class McpPreset
{
    /// <summary>获取配置文件中使用的预设标识符（同时作为 MCP 服务键）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>获取显示名称。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>获取 UI 中显示的简短描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>获取传输方式：<c>stdio</c>、<c>http</c> 或 <c>sse</c>。</summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>获取 stdio 预设的可执行文件，例如 <c>npx</c>。</summary>
    public string? Command { get; init; }

    /// <summary>获取 stdio 预设的参数。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>获取 stdio 预设的字面环境变量。</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>获取必须由用户提供的环境变量，以名称为键并附带说明。</summary>
    public IReadOnlyDictionary<string, string> RequiredEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>获取 http/sse 预设的 endpoint。</summary>
    public string? Url { get; init; }

    /// <summary>获取 http/sse 预设的可选 HTTP 头。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>获取该预设安装前是否需要用户提供环境变量。</summary>
    public bool RequiresUserInput => RequiredEnvironment.Count > 0;

    /// <summary>获取该预设是否为 stdio 服务。</summary>
    public bool IsStdio => string.Equals(Transport, "stdio", StringComparison.OrdinalIgnoreCase);
}
