using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiTools.Samples.McpToolServer;

/// <summary>用于写入 JSON-RPC 2.0 消息和紧凑 JSON 负载的辅助方法。</summary>
internal static class McpJson
{
    /// <summary>JSON 负载格式错误时的 JSON-RPC 错误码。</summary>
    internal const int ParseError = -32700;

    /// <summary>请求对象无效时的 JSON-RPC 错误码。</summary>
    internal const int InvalidRequest = -32600;

    /// <summary>方法未知时的 JSON-RPC 错误码。</summary>
    internal const int MethodNotFound = -32601;

    /// <summary>参数格式错误或缺失时的 JSON-RPC 错误码。</summary>
    internal const int InvalidParams = -32602;

    /// <summary>当请求省略 <c>params</c> 或 <c>arguments</c> 时使用的共享空 JSON 对象。</summary>
    internal static JsonElement EmptyObject { get; } = CreateEmptyObject();

    /// <summary>写入 JSON-RPC 2.0 成功响应。</summary>
    /// <param name="id">要回显的请求标识符；为 null 时写入 JSON null。</param>
    /// <param name="writeResult">写入结果对象属性的回调。</param>
    /// <returns>紧凑 JSON 文本。</returns>
    internal static string Result(JsonElement? id, Action<Utf8JsonWriter> writeResult)
    {
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            WriteId(writer, id);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writeResult(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>写入 JSON-RPC 2.0 错误响应。</summary>
    /// <param name="id">要回显的请求标识符；为 null 时写入 JSON null。</param>
    /// <param name="code">JSON-RPC 错误码。</param>
    /// <param name="message">便于阅读的错误消息。</param>
    /// <param name="data">可选的错误数据字符串。</param>
    /// <returns>紧凑 JSON 文本。</returns>
    internal static string Error(JsonElement? id, int code, string message, string? data = null)
    {
        return WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            WriteId(writer, id);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            if (data != null)
            {
                writer.WriteString("data", data);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary>运行 JSON 写入器回调，并以字符串形式返回生成的 UTF-8 JSON。</summary>
    /// <param name="write">接收写入器的回调。</param>
    /// <param name="indented">是否格式化输出 JSON。</param>
    /// <returns>序列化后的 JSON 文本。</returns>
    internal static string WriteJson(Action<Utf8JsonWriter> write, bool indented = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = indented,
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteId(Utf8JsonWriter writer, JsonElement? id)
    {
        if (id.HasValue && id.Value.ValueKind != JsonValueKind.Undefined)
        {
            id.Value.WriteTo(writer);
            return;
        }

        writer.WriteNullValue();
    }

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
