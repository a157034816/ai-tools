using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AiTools.Mcp;

/// <summary>MCP transport 共享的常量和辅助方法。</summary>
internal static class McpConstants
{
    internal const string ProviderName = "mcp";
    internal const string SessionHeader = "Mcp-Session-Id";
    internal const string ProtocolVersionHeader = "MCP-Protocol-Version";
    internal const int RequestCancelledCode = -32800;
}

/// <summary>客户端使用的 MCP JSON-RPC 方法名。</summary>
internal static class McpMethods
{
    internal const string Initialize = "initialize";
    internal const string Initialized = "notifications/initialized";
    internal const string Ping = "ping";
    internal const string ToolsList = "tools/list";
    internal const string ToolsCall = "tools/call";
    internal const string ResourcesList = "resources/list";
    internal const string ResourcesRead = "resources/read";
    internal const string ResourcesTemplatesList = "resources/templates/list";
    internal const string PromptsList = "prompts/list";
    internal const string PromptsGet = "prompts/get";
    internal const string LoggingSetLevel = "logging/setLevel";
    internal const string ProgressNotification = "notifications/progress";
    internal const string CancelledNotification = "notifications/cancelled";
}

/// <summary>允许客户端将协商后的协议版本告知 transport。</summary>
internal interface IProtocolVersionTransport
{
    string? ProtocolVersion { get; set; }
}

/// <summary>遵循 <see cref="AiTools.Core.Http.AiHttpOptions"/> 约定的 HTTP 标头辅助方法。</summary>
internal static class McpHttp
{
    internal static void ApplyHeaders(
        System.Net.Http.HttpRequestMessage request,
        AiTools.Core.Http.AiHttpOptions options,
        string? sessionId,
        string? protocolVersion)
    {
        if (options.DefaultHeaders != null)
        {
            foreach (var header in options.DefaultHeaders)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) && request.Content != null)
                {
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        string? authorization = null;
        if (!string.IsNullOrEmpty(options.AuthorizationHeader))
        {
            authorization = options.AuthorizationHeader;
        }
        else if (!string.IsNullOrEmpty(options.ApiKey))
        {
            authorization = "Bearer " + options.ApiKey;
        }

        if (authorization != null)
        {
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.Remove(McpConstants.SessionHeader);
            request.Headers.TryAddWithoutValidation(McpConstants.SessionHeader, sessionId);
        }

        if (!string.IsNullOrEmpty(protocolVersion))
        {
            request.Headers.Remove(McpConstants.ProtocolVersionHeader);
            request.Headers.TryAddWithoutValidation(McpConstants.ProtocolVersionHeader, protocolVersion);
        }
    }

    internal static string? GetSessionId(System.Net.Http.HttpResponseMessage response)
    {
        return GetHeader(response, McpConstants.SessionHeader);
    }

    internal static string? GetProtocolVersion(System.Net.Http.HttpResponseMessage response)
    {
        return GetHeader(response, McpConstants.ProtocolVersionHeader);
    }

    /// <summary>创建一个清晰描述 Streamable HTTP 会话已过期的异常。</summary>
    internal static AiTools.Abstractions.AiServiceException CreateNotFoundException(
        bool hadSession,
        AiTools.Abstractions.AiServiceException inner)
    {
        var message = hadSession
            ? "The MCP session has expired (the server returned HTTP 404). Initialize the client again to create a new session."
            : "The MCP endpoint was not found (HTTP 404). Verify the endpoint URL and that the server is running.";

        return new AiTools.Abstractions.AiServiceException(
            message,
            McpConstants.ProviderName,
            404,
            inner.ErrorCode,
            inner.ResponseBody,
            inner);
    }

    private static string? GetHeader(System.Net.Http.HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            foreach (var value in values)
            {
                return value;
            }
        }

        if (response.Content != null && response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            foreach (var value in contentValues)
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>用于可能为 null 或 JSON <c>undefined</c> 的值的通用辅助方法。</summary>
internal static class JsonValue
{
    internal static bool IsUsable(JsonElement? element)
    {
        return element.HasValue
            && element.Value.ValueKind != JsonValueKind.Undefined
            && element.Value.ValueKind != JsonValueKind.Null;
    }
}
