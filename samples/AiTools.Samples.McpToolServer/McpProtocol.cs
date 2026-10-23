using System;
using System.Text.Json;

namespace AiTools.Samples.McpToolServer;

/// <summary>处理一条 JSON-RPC 消息的结果。</summary>
internal readonly struct McpReply
{
    private McpReply(bool isNotification, string? json, string? method, string? toolName, bool isError, string? protocolVersion)
    {
        IsNotification = isNotification;
        Json = json;
        Method = method;
        ToolName = toolName;
        IsError = isError;
        ProtocolVersion = protocolVersion;
    }

    /// <summary>获取消息是否为通知，因而不得生成响应体。</summary>
    internal bool IsNotification { get; }

    /// <summary>获取请求的紧凑 JSON-RPC 响应文本；通知为 null。</summary>
    internal string? Json { get; }

    /// <summary>获取所请求的方法名（在能够读取时）。</summary>
    internal string? Method { get; }

    /// <summary>获取 <c>tools/call</c> 所调用的工具名称（适用时）。</summary>
    internal string? ToolName { get; }

    /// <summary>获取工具是否报告了工具级失败。</summary>
    internal bool IsError { get; }

    /// <summary>获取 <c>initialize</c> 响应中回显的协议版本。</summary>
    internal string? ProtocolVersion { get; }

    /// <summary>创建通知结果。</summary>
    /// <param name="method">通知方法（已知时）。</param>
    internal static McpReply Notification(string? method) =>
        new(isNotification: true, json: null, method: method, toolName: null, isError: false, protocolVersion: null);

    /// <summary>创建响应结果。</summary>
    /// <param name="json">紧凑 JSON-RPC 响应文本。</param>
    /// <param name="method">所请求的方法名（已知时）。</param>
    /// <param name="toolName"><c>tools/call</c> 的工具名称（适用时）。</param>
    /// <param name="isError">工具是否报告了工具级失败。</param>
    /// <param name="protocolVersion">由 <c>initialize</c> 协商的协议版本。</param>
    internal static McpReply Response(
        string json,
        string? method,
        string? toolName = null,
        bool isError = false,
        string? protocolVersion = null) =>
        new(isNotification: false, json, method, toolName, isError, protocolVersion);
}

/// <summary>
/// 与传输方式无关的 MCP 请求路由器。它实现了 HTTP 与 stdio 传输共享的 JSON-RPC 方法集合，
/// 并且绝不会因工具级或协议级失败而抛出异常。
/// </summary>
internal sealed class McpProtocol
{
    /// <summary>资源 URI 未知时使用的 JSON-RPC 错误码。</summary>
    private const int ResourceNotFound = -32002;

    /// <summary>服务器发生意外失败时使用的 JSON-RPC 错误码。</summary>
    private const int InternalError = -32603;

    private readonly string _serverName;
    private readonly ResourceCatalog _resources;

    /// <summary>创建协议路由器。</summary>
    /// <param name="serverName">在 <c>serverInfo</c> 中报告的服务器名称。</param>
    /// <param name="resources">供 <c>resources/list</c> 与 <c>resources/read</c> 使用的 Resource 目录。</param>
    internal McpProtocol(string serverName, ResourceCatalog resources)
    {
        _serverName = serverName;
        _resources = resources;
    }

    /// <summary>处理一条 JSON-RPC 消息。</summary>
    /// <param name="message">已解析的 JSON 消息；必须是对象。</param>
    /// <returns>要发送的回复；当不得写入响应体时返回通知结果。</returns>
    internal McpReply Handle(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return McpReply.Response(
                McpJson.Error(null, McpJson.InvalidRequest, "JSON-RPC 消息必须是 JSON 对象。"),
                method: null);
        }

        var method = GetOptionalString(message, "method");
        if (!message.TryGetProperty("id", out var idElement) || idElement.ValueKind == JsonValueKind.Null)
        {
            // MCP 通知（notifications/initialized、notifications/cancelled 等）没有 id，不返回响应体。
            return McpReply.Notification(method);
        }

        var id = idElement.Clone();
        if (string.IsNullOrEmpty(method))
        {
            return McpReply.Response(
                McpJson.Error(id, McpJson.InvalidRequest, "缺少 method 字段。"),
                method: null);
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    return Initialize(id, message);

                case "ping":
                    return McpReply.Response(McpJson.Result(id, _ => { }), method);

                case "tools/list":
                    return ListTools(id, method);

                case "tools/call":
                    return CallTool(id, method, message);

                case "resources/list":
                    return McpReply.Response(McpJson.Result(id, _resources.WriteList), method);

                case "resources/read":
                    return ReadResource(id, method, message);

                case "prompts/list":
                    return McpReply.Response(McpJson.Result(id, PromptCatalog.WriteList), method);

                case "prompts/get":
                    return GetPrompt(id, method, message);

                case "logging/setLevel":
                    // 本示例不发送服务器日志消息，仅接受并忽略该级别设置。
                    return McpReply.Response(McpJson.Result(id, _ => { }), method);

                default:
                    return McpReply.Response(
                        McpJson.Error(id, McpJson.MethodNotFound, "不支持的方法 '" + method + "'。"),
                        method);
            }
        }
        catch (Exception ex)
        {
            return McpReply.Response(
                McpJson.Error(id, InternalError, "服务器内部错误：" + ex.Message),
                method);
        }
    }

    private McpReply Initialize(JsonElement id, JsonElement message)
    {
        if (!TryGetObjectParams(message, out var parameters, out var error))
        {
            return InvalidParams(id, "initialize", error!);
        }

        // 按 MCP 规范回显客户端建议的协议版本；未提供时使用本服务器默认版本。
        var requestedVersion = GetOptionalString(parameters, "protocolVersion");
        var negotiatedVersion = string.IsNullOrWhiteSpace(requestedVersion)
            ? ServerMetadata.DefaultProtocolVersion
            : requestedVersion!.Trim();

        var json = McpJson.Result(id, writer =>
        {
            writer.WriteString("protocolVersion", negotiatedVersion);

            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WritePropertyName("tools");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WritePropertyName("resources");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WritePropertyName("prompts");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WritePropertyName("logging");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WritePropertyName("serverInfo");
            writer.WriteStartObject();
            writer.WriteString("name", _serverName);
            writer.WriteString("version", ServerMetadata.Version);
            writer.WriteEndObject();

            writer.WriteString("instructions", ServerMetadata.Instructions);
        });

        return McpReply.Response(json, "initialize", protocolVersion: negotiatedVersion);
    }

    private static McpReply ListTools(JsonElement id, string method)
    {
        var json = McpJson.Result(id, writer =>
        {
            writer.WritePropertyName("tools");
            writer.WriteStartArray();
            foreach (var tool in ToolCatalog.Tools)
            {
                ToolCatalog.WriteTool(writer, tool);
            }

            writer.WriteEndArray();
        });

        return McpReply.Response(json, method);
    }

    private static McpReply CallTool(JsonElement id, string method, JsonElement message)
    {
        if (!TryGetObjectParams(message, out var parameters, out var error))
        {
            return InvalidParams(id, method, error!);
        }

        var name = GetOptionalString(parameters, "name");
        if (string.IsNullOrEmpty(name))
        {
            return InvalidParams(id, method, "tools/call 需要非空的字符串参数 name。");
        }

        var arguments = McpJson.EmptyObject;
        if (parameters.TryGetProperty("arguments", out var argumentsElement)
            && argumentsElement.ValueKind != JsonValueKind.Null)
        {
            if (argumentsElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidParams(id, method, "tools/call 的 arguments 必须是 JSON 对象。");
            }

            arguments = argumentsElement;
        }

        string text;
        var isError = false;
        try
        {
            text = ToolCatalog.Invoke(name, arguments);
        }
        catch (ToolExecutionException ex)
        {
            text = ex.Message;
            isError = true;
        }
        catch (Exception ex)
        {
            text = "工具执行失败：" + ex.Message;
            isError = true;
        }

        var json = McpJson.Result(id, writer =>
        {
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("isError", isError);
        });

        return McpReply.Response(json, method, name, isError);
    }

    private McpReply ReadResource(JsonElement id, string method, JsonElement message)
    {
        if (!TryGetObjectParams(message, out var parameters, out var error))
        {
            return InvalidParams(id, method, error!);
        }

        var uri = GetOptionalString(parameters, "uri");
        if (string.IsNullOrWhiteSpace(uri))
        {
            return InvalidParams(id, method, "resources/read 需要非空的字符串参数 uri。");
        }

        var text = _resources.Read(uri);
        if (text == null)
        {
            return McpReply.Response(
                McpJson.Error(
                    id,
                    ResourceNotFound,
                    "未找到资源 '" + uri + "'。可用资源：tool://catalog、server://info。"),
                method);
        }

        var json = McpJson.Result(id, writer =>
        {
            writer.WritePropertyName("contents");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteString("mimeType", "application/json");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
        });

        return McpReply.Response(json, method);
    }

    private static McpReply GetPrompt(JsonElement id, string method, JsonElement message)
    {
        if (!TryGetObjectParams(message, out var parameters, out var error))
        {
            return InvalidParams(id, method, error!);
        }

        var promptName = GetOptionalString(parameters, "name");
        if (string.IsNullOrEmpty(promptName))
        {
            return InvalidParams(id, method, "prompts/get 需要非空的字符串参数 name。");
        }

        if (!string.Equals(promptName, PromptCatalog.ExplainToolName, StringComparison.Ordinal))
        {
            return InvalidParams(
                id,
                method,
                "未知提示词 '" + promptName + "'。可用提示词：" + PromptCatalog.ExplainToolName + "。");
        }

        var arguments = McpJson.EmptyObject;
        if (parameters.TryGetProperty("arguments", out var argumentsElement)
            && argumentsElement.ValueKind != JsonValueKind.Null)
        {
            if (argumentsElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidParams(id, method, "prompts/get 的 arguments 必须是 JSON 对象。");
            }

            arguments = argumentsElement;
        }

        var toolName = GetOptionalString(arguments, "toolName");
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return InvalidParams(id, method, "提示词 explain_tool 需要字符串参数 toolName。");
        }

        var tool = ToolCatalog.Find(toolName);
        if (tool == null)
        {
            return InvalidParams(
                id,
                method,
                "未知工具 '" + toolName + "'。可用工具：" + string.Join("、", ToolCatalog.ToolNames) + "。");
        }

        var prompt = PromptCatalog.BuildExplainToolPrompt(tool);
        var json = McpJson.Result(id, writer =>
        {
            writer.WriteString("description", "让模型解释工具 “" + tool.Definition.Name + "” 的用途与参数。");
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", prompt);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
        });

        return McpReply.Response(json, method);
    }

    private static bool TryGetObjectParams(JsonElement message, out JsonElement parameters, out string? error)
    {
        if (!message.TryGetProperty("params", out parameters) || parameters.ValueKind == JsonValueKind.Null)
        {
            parameters = McpJson.EmptyObject;
            error = null;
            return true;
        }

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            error = "params 必须是 JSON 对象。";
            return false;
        }

        error = null;
        return true;
    }

    private static McpReply InvalidParams(JsonElement id, string method, string message) =>
        McpReply.Response(McpJson.Error(id, McpJson.InvalidParams, message), method);

    private static string? GetOptionalString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
