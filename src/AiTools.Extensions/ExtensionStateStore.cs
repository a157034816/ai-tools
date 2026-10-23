using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace AiTools.Extensions;

/// <summary>
/// 扩展的会话状态。状态以 JSON 值保存，按扩展名分桶；只有宿主显式持久化时才会落盘，
/// 因此扩展可以自由存放进度、计划或在轮次间传递的簿记数据。
/// </summary>
/// <remarks>
/// 状态读写都在内存中完成，<see cref="Changed"/> 用来告诉宿主"需要落盘了"。
/// 与 Pi 的分层一致：跟随分支的状态放在这里，不进模型上下文。
/// </remarks>
public sealed class ExtensionStateStore
{
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _buckets =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    /// <summary>当任一扩展修改了状态时触发。</summary>
    public event EventHandler? Changed;

    /// <summary>读取给定扩展的某个键；不存在时返回 null。</summary>
    public JsonElement? Get(string extensionName, string key)
    {
        lock (_gate)
        {
            if (_buckets.TryGetValue(extensionName, out var bucket) && bucket.TryGetValue(key, out var value))
            {
                return value;
            }

            return null;
        }
    }

    /// <summary>写入 JSON 值。值为 null、<c>JsonValueKind.Null</c> 或 <c>Undefined</c> 时删除该键。</summary>
    public void Set(string extensionName, string key, JsonElement? value)
    {
        lock (_gate)
        {
            if (value == null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (_buckets.TryGetValue(extensionName, out var existing) && existing.Remove(key))
                {
                    Raise();
                }

                return;
            }

            if (!_buckets.TryGetValue(extensionName, out var bucket))
            {
                bucket = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                _buckets[extensionName] = bucket;
            }

            bucket[key] = value.Value.Clone();
        }

        Raise();
    }

    /// <summary>用强类型值写入（序列化为 JSON）。</summary>
    public void Set<T>(string extensionName, string key, T value)
    {
        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(value, ExtensionStateJson.Options);
        }
        catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
        {
            throw new ArgumentException($"Value for '{key}' cannot be serialized to JSON.", nameof(value), ex);
        }

        // 显式转换为可空 JsonElement，避免 T = JsonElement 时递归调用自身。
        Set(extensionName, key, (JsonElement?)element);
    }

    /// <summary>读取强类型值；不存在或无法反序列化时返回 default。</summary>
    public T? Get<T>(string extensionName, string key)
    {
        var element = Get(extensionName, key);
        if (element == null)
        {
            return default;
        }

        try
        {
            return element.Value.Deserialize<T>(ExtensionStateJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// 读取强类型值并区分"不存在"与"默认值"（例如 <c>0</c> 与缺失）。
    /// </summary>
    public bool TryGet<T>(string extensionName, string key, out T? value)
    {
        value = default;
        var element = Get(extensionName, key);
        if (element == null)
        {
            return false;
        }

        try
        {
            value = element.Value.Deserialize<T>(ExtensionStateJson.Options);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>删除扩展的某个键。返回是否存在过。</summary>
    public bool Remove(string extensionName, string key)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(extensionName, out var bucket) || !bucket.Remove(key))
            {
                return false;
            }
        }

        Raise();
        return true;
    }

    /// <summary>清空某个扩展的全部状态。</summary>
    public void Clear(string extensionName)
    {
        bool removed;
        lock (_gate)
        {
            removed = _buckets.Remove(extensionName);
        }

        if (removed)
        {
            Raise();
        }
    }

    /// <summary>清空全部状态。</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            if (_buckets.Count == 0)
            {
                return;
            }

            _buckets.Clear();
        }

        Raise();
    }

    /// <summary>获取按扩展名分组的只读快照。</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Snapshot()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _buckets)
            {
                var bucket = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var entry in pair.Value)
                {
                    bucket[entry.Key] = entry.Value;
                }

                result[pair.Key] = bucket;
            }

            return result;
        }
    }

    /// <summary>用快照替换全部状态（用于恢复会话）。</summary>
    public void Restore(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>? snapshot)
    {
        lock (_gate)
        {
            _buckets.Clear();
            if (snapshot != null)
            {
                foreach (var pair in snapshot)
                {
                    var bucket = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var entry in pair.Value)
                    {
                        bucket[entry.Key] = entry.Value;
                    }

                    _buckets[pair.Key] = bucket;
                }
            }
        }

        Raise();
    }

    /// <summary>把状态序列化为可持久化的 JSON 文本。</summary>
    public string ToJson()
    {
        var snapshot = Snapshot();
        var builder = new StringBuilder();
        builder.Append('{');
        var firstExtension = true;
        foreach (var pair in snapshot)
        {
            if (!firstExtension)
            {
                builder.Append(',');
            }

            firstExtension = false;
            builder.Append(JsonSerializer.Serialize(pair.Key));
            builder.Append(":{");
            var firstEntry = true;
            foreach (var entry in pair.Value)
            {
                if (!firstEntry)
                {
                    builder.Append(',');
                }

                firstEntry = false;
                builder.Append(JsonSerializer.Serialize(entry.Key));
                builder.Append(':');
                builder.Append(entry.Value.GetRawText());
            }

            builder.Append('}');
        }

        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>从 <see cref="ToJson"/> 的输出恢复状态；无法解析时保持为空。</summary>
    public void LoadFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            Restore(null);
            return;
        }

        Dictionary<string, Dictionary<string, JsonElement>>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(json!, ExtensionStateJson.Options);
        }
        catch (JsonException)
        {
            Restore(null);
            return;
        }

        var snapshot = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
        if (parsed != null)
        {
            foreach (var pair in parsed)
            {
                snapshot[pair.Key] = pair.Value;
            }
        }

        Restore(snapshot);
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>扩展状态使用的序列化选项。</summary>
public static class ExtensionStateJson
{
    /// <summary>获取序列化选项：宽松转义、忽略大小写、允许注释。</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
