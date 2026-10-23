using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AiTools.Mcp;

/// <summary>用于构建和检查 JSON-RPC 2.0 消息的辅助方法。</summary>
internal static class JsonRpc
{
    internal const string Version = "2.0";
    internal const int MethodNotFound = -32601;
    internal const int InvalidParams = -32602;

    internal static JsonElement CreateRequest(long id, string method, JsonElement? parameters)
    {
        return CreateObject(writer =>
        {
            writer.WriteString("jsonrpc", Version);
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (parameters.HasValue)
            {
                writer.WritePropertyName("params");
                parameters.Value.WriteTo(writer);
            }
        });
    }

    internal static JsonElement CreateNotification(string method, JsonElement? parameters)
    {
        return CreateObject(writer =>
        {
            writer.WriteString("jsonrpc", Version);
            writer.WriteString("method", method);
            if (parameters.HasValue)
            {
                writer.WritePropertyName("params");
                parameters.Value.WriteTo(writer);
            }
        });
    }

    internal static JsonElement CreateResult(JsonElement id, JsonElement? result)
    {
        return CreateObject(writer =>
        {
            writer.WriteString("jsonrpc", Version);
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            if (result.HasValue)
            {
                result.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
        });
    }

    internal static JsonElement CreateError(JsonElement id, int code, string message, string? data = null)
    {
        return CreateObject(writer =>
        {
            writer.WriteString("jsonrpc", Version);
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            if (data != null)
            {
                writer.WriteString("data", data);
            }

            writer.WriteEndObject();
        });
    }

    internal static JsonElement CreateObject(Action<Utf8JsonWriter> writeProperties)
    {
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writeProperties(writer);
                writer.WriteEndObject();
            }

            return Parse(buffer.ToArray());
        }
    }

    internal static bool TryGetId(JsonElement message, out long id)
    {
        id = 0;
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("id", out var idElement))
        {
            return false;
        }

        if (idElement.ValueKind == JsonValueKind.Number)
        {
            return idElement.TryGetInt64(out id);
        }

        // 容忍将数值标识符以字符串形式回显的服务器。
        return idElement.ValueKind == JsonValueKind.String
            && long.TryParse(idElement.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    internal static string? GetMethod(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String
            ? method.GetString()
            : null;
    }

    internal static bool HasId(JsonElement message)
    {
        return message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("id", out var id)
            && id.ValueKind != JsonValueKind.Null;
    }

    private static JsonElement Parse(byte[] utf8Json)
    {
        using (var document = JsonDocument.Parse(utf8Json))
        {
            return document.RootElement.Clone();
        }
    }
}
