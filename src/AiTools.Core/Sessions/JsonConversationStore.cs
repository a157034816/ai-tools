using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Core.Sessions;

/// <summary>
/// 默认的基于文件的对话存储。每个对话对应 <c>&lt;root&gt;/conversations/&lt;id&gt;.json</c>
/// 下的一个 JSON 文件；写入是原子的（临时文件 + 替换），并发访问通过信号量串行化。
/// </summary>
public sealed class JsonConversationStore : IConversationStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>创建以指定目录为根目录的存储。</summary>
    /// <param name="rootDirectory">用于容纳 <c>conversations</c> 子目录的根目录。</param>
    public JsonConversationStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
        }

        _directory = Path.Combine(Path.GetFullPath(rootDirectory), "conversations");
    }

    /// <summary>获取存储对话文件的目录。</summary>
    public string DirectoryPath => _directory;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory))
            {
                return Array.Empty<ConversationSummary>();
            }

            var summaries = new List<ConversationSummary>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = await ReadFileAsync(file, cancellationToken).ConfigureAwait(false);
                if (record != null)
                {
                    summaries.Add(record.ToSummary());
                }
            }

            return summaries
                .OrderByDescending(summary => summary.UpdatedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConversationRecord?> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // 先把不安全的 id 拒绝掉，再进入文件系统。
        var path = GetPath(id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return File.Exists(path) ? await ReadFileAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.Id))
        {
            record.Id = Guid.NewGuid().ToString("N");
        }

        record.UpdatedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(record.Title))
        {
            record.Title = ConversationRecord.DeriveTitle(record.Messages);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var path = GetPath(record.Id);
            var temp = path + ".tmp";
            var json = JsonSerializer.Serialize(record, ConversationJson.Options);

            await WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPath(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConversationRecord> ForkAsync(
        ConversationRecord source,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var fork = source.CreateFork(title);
        await SaveAsync(fork, cancellationToken).ConfigureAwait(false);
        return fork;
    }

    private string GetPath(string id)
    {
        // 会话 id 必须只包含安全字符，避免路径穿越与意外文件覆盖。
        if (string.IsNullOrWhiteSpace(id) ||
            id.Length > 128 ||
            id.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_')))
        {
            throw new ArgumentException("Invalid conversation id. Ids may only contain letters, digits, '-' and '_'.", nameof(id));
        }

        return Path.Combine(_directory, id + ".json");
    }

    private static async Task<ConversationRecord?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            string json;
            using (var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                json = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options);
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            // 损坏或不可读的会话文件不应破坏列表；调用方可以从磁盘手动恢复。
            return null;
        }
    }

    private static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        using (var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(content).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>内存中的对话存储，适用于测试与临时会话。</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Dictionary<string, ConversationRecord> _records = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ConversationSummary> result = _records.Values
                .Select(record => record.ToSummary())
                .OrderByDescending(summary => summary.UpdatedAt)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<ConversationRecord?> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_records.TryGetValue(id ?? string.Empty, out var record) ? Clone(record) : null);
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(ConversationRecord record, CancellationToken cancellationToken = default)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(record.Id))
            {
                record.Id = Guid.NewGuid().ToString("N");
            }

            record.UpdatedAt = DateTimeOffset.UtcNow;
            if (string.IsNullOrWhiteSpace(record.Title))
            {
                record.Title = ConversationRecord.DeriveTitle(record.Messages);
            }

            _records[record.Id] = Clone(record);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _records.Remove(id ?? string.Empty);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ConversationRecord> ForkAsync(
        ConversationRecord source,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var fork = source.CreateFork(title);
        await SaveAsync(fork, cancellationToken).ConfigureAwait(false);
        return fork;
    }

    private static ConversationRecord Clone(ConversationRecord record)
    {
        var json = JsonSerializer.Serialize(record, ConversationJson.Options);
        return JsonSerializer.Deserialize<ConversationRecord>(json, ConversationJson.Options)!;
    }
}
