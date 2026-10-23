using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Core.Sse;

/// <summary>一条 SSE 事件。</summary>
public sealed class SseEvent
{
    /// <summary>事件类型（event: 字段）。</summary>
    public string? EventType { get; init; }

    /// <summary>事件数据（data: 字段合并结果）。</summary>
    public string Data { get; init; } = string.Empty;

    /// <summary>是否为 [DONE] 终止标记。</summary>
    public bool IsDone => string.Equals(Data.Trim(), "[DONE]", StringComparison.Ordinal);
}

/// <summary>服务端事件流（SSE）解析器。</summary>
public static class SseParser
{
    /// <summary>将原始流解析为事件序列。</summary>
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var data = new StringBuilder();
        string? eventType = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0 || eventType != null)
                {
                    var evt = new SseEvent { EventType = eventType, Data = data.ToString() };
                    data.Clear();
                    eventType = null;
                    yield return evt;

                    if (evt.IsDone)
                    {
                        yield break;
                    }
                }

                continue;
            }

            if (line[0] == ':')
            {
                // 注释行（keep-alive）。
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line.Substring(6).TrimStart();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.Substring(5);
                if (value.StartsWith(" ", StringComparison.Ordinal))
                {
                    value = value.Substring(1);
                }

                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
                continue;
            }

            // id:/retry: 等其他字段：忽略。
        }

        if (data.Length > 0)
        {
            yield return new SseEvent { EventType = eventType, Data = data.ToString() };
        }
    }

    /// <summary>解析一个 JSON 数据事件，跳过 [DONE] 与非 JSON 内容。</summary>
    public static bool TryParseJson(SseEvent evt, out JsonElement element)
    {
        element = default;
        var trimmed = evt.Data.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>将非 SSE 的 NDJSON 流（如 Ollama / NDJSON 模式）解析为 JSON 元素序列。</summary>
    public static async IAsyncEnumerable<JsonElement> ParseNdJsonAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
            {
                yield break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            JsonElement element;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                element = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            yield return element;
        }
    }
}
