using System;
using System.Text.Json;

namespace AiTools.Samples.McpToolServer;

/// <summary>
/// 示例服务器对外提供的 MCP Resource。Resource 均为只读 JSON 文档。
/// </summary>
internal sealed class ResourceCatalog
{
    private readonly Func<string?> _endpointAccessor;
    private readonly string _serverName;
    private readonly string _transport;

    /// <summary>创建 Resource 目录。</summary>
    /// <param name="serverName">在 <c>server://info</c> 中报告的服务器名称。</param>
    /// <param name="transport">传输方式标签，例如 <c>streamable-http</c> 或 <c>stdio</c>。</param>
    /// <param name="endpointAccessor">返回 HTTP 端点；stdio 模式下返回 null。</param>
    internal ResourceCatalog(string serverName, string transport, Func<string?> endpointAccessor)
    {
        _serverName = serverName;
        _transport = transport;
        _endpointAccessor = endpointAccessor;
    }

    /// <summary>写入 <c>resources/list</c> 结果中的 <c>resources</c> 数组。</summary>
    /// <param name="writer">目标写入器。</param>
    internal void WriteList(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("resources");
        writer.WriteStartArray();

        WriteResource(
            writer,
            "tool://catalog",
            "工具目录",
            "当前服务器暴露的全部 MCP 工具及其 JSON Schema。",
            "application/json");

        WriteResource(
            writer,
            "server://info",
            "服务器信息",
            "服务器名称、版本、传输方式、协议版本与工具列表。",
            "application/json");

        writer.WriteEndArray();
    }

    /// <summary>按 URI 读取 Resource。</summary>
    /// <param name="uri">Resource URI。</param>
    /// <returns>JSON 文本；URI 未知时返回 null。</returns>
    internal string? Read(string uri)
    {
        switch (uri)
        {
            case "tool://catalog":
                return BuildCatalog();

            case "server://info":
                return BuildServerInfo();

            default:
                return null;
        }
    }

    private static void WriteResource(Utf8JsonWriter writer, string uri, string name, string description, string mimeType)
    {
        writer.WriteStartObject();
        writer.WriteString("uri", uri);
        writer.WriteString("name", name);
        writer.WriteString("description", description);
        writer.WriteString("mimeType", mimeType);
        writer.WriteEndObject();
    }

    private static string BuildCatalog()
    {
        return McpJson.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("uri", "tool://catalog");
            writer.WriteNumber("toolCount", ToolCatalog.Tools.Count);
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in ToolCatalog.Tools)
            {
                ToolCatalog.WriteTool(writer, tool);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private string BuildServerInfo()
    {
        return McpJson.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", _serverName);
            writer.WriteString("version", ServerMetadata.Version);
            writer.WriteString("transport", _transport);

            var endpoint = _endpointAccessor();
            writer.WritePropertyName("endpoint");
            if (endpoint == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(endpoint);
            }

            writer.WritePropertyName("protocolVersions");
            writer.WriteStartArray();
            foreach (var version in ServerMetadata.SupportedProtocolVersions)
            {
                writer.WriteStringValue(version);
            }

            writer.WriteEndArray();
            writer.WriteNumber("pid", Environment.ProcessId);
            writer.WriteNumber("toolCount", ToolCatalog.Tools.Count);
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var name in ToolCatalog.ToolNames)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
            writer.WriteString("instructions", ServerMetadata.Instructions);
            writer.WriteEndObject();
        });
    }
}
