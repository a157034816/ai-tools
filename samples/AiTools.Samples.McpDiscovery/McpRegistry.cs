using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AiTools.Samples.McpDiscovery;

/// <summary>
/// 本机运行的 MCP 服务的文件注册表。
/// 除非 <c>AITOOLS_MCP_REGISTRY_PATH</c> 环境变量覆盖位置，注册表位于
/// <c>~/.ai-tools/simple-mcp/registry.json</c>。
/// 所有读取-修改-写入操作均持有独占文件锁，以支持多个服务并发注册。
/// </summary>
public static class McpRegistry
{
    /// <summary>覆盖注册表文件路径的环境变量。便于测试。</summary>
    public const string PathEnvironmentVariable = "AITOOLS_MCP_REGISTRY_PATH";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const int LockRetries = 40;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>获取注册表文件路径。</summary>
    public static string RegistryPath
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return overridden!;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ai-tools",
                "simple-mcp",
                "registry.json");
        }
    }

    /// <summary>设置进程范围的注册表路径。供无法更改环境变量的测试使用。</summary>
    public static void OverridePath(string? path) => _pathOverride = path;

    private static string? _pathOverride;

    private static string CurrentPath => _pathOverride ?? RegistryPath;

    /// <summary>读取所有条目。从不抛出异常：注册表缺失或损坏时返回空列表。</summary>
    public static IReadOnlyList<McpRegistryEntry> ReadAll()
    {
        var path = CurrentPath;
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<McpRegistryEntry>();
            }

            using var stream = OpenWithRetry(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<McpRegistryEntry>();
            }

            var document = JsonSerializer.Deserialize<RegistryDocument>(json, SerializerOptions);
            return document?.Servers ?? (IReadOnlyList<McpRegistryEntry>)Array.Empty<McpRegistryEntry>();
        }
        catch (Exception)
        {
            return Array.Empty<McpRegistryEntry>();
        }
    }

    /// <summary>按 <see cref="McpRegistryEntry.Id"/> 插入或替换条目。</summary>
    public static void Upsert(McpRegistryEntry entry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        Update(entries =>
        {
            entries.RemoveAll(existing => string.Equals(existing.Id, entry.Id, StringComparison.Ordinal));
            entries.Add(entry.Clone());
            return true;
        });
    }

    /// <summary>移除具有指定 ID 的条目。</summary>
    public static bool Remove(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var removed = false;
        Update(entries =>
        {
            removed = entries.RemoveAll(existing => string.Equals(existing.Id, id, StringComparison.Ordinal)) > 0;
            return removed;
        });

        return removed;
    }

    /// <summary>移除进程已不再运行的条目。</summary>
    /// <returns>已移除的过期条目数量。</returns>
    public static int RemoveStale()
    {
        var removed = 0;
        Update(entries =>
        {
            removed = entries.RemoveAll(entry => !IsProcessAlive(entry.Pid, entry.ProcessStartedAt));
            return removed > 0;
        });

        return removed;
    }

    /// <summary>删除注册表文件。供测试使用。</summary>
    public static void Clear()
    {
        try
        {
            var path = CurrentPath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// 判断进程是否存活。当 <paramref name="expectedStartUtc"/> 可用时，还会比较进程
    /// 启动时间，以免被回收复用的 PID 被误认为原始服务。
    /// </summary>
    public static bool IsProcessAlive(int pid, DateTime expectedStartUtc)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            if (expectedStartUtc != default)
            {
                try
                {
                    var actual = process.StartTime.ToUniversalTime();
                    if (Math.Abs((actual - expectedStartUtc).TotalMinutes) > 2)
                    {
                        return false;
                    }
                }
                catch (Exception)
                {
                    // 启动时间访问可能被拒绝；此时仅回退到存活检查。
                }
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void Update(Func<List<McpRegistryEntry>, bool> mutate)
    {
        var path = CurrentPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 0; attempt < LockRetries; attempt++)
        {
            try
            {
                using var stream = OpenWithRetry(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(stream, leaveOpen: true);
                var json = reader.ReadToEnd();

                RegistryDocument document;
                if (string.IsNullOrWhiteSpace(json))
                {
                    document = new RegistryDocument();
                }
                else
                {
                    try
                    {
                        document = JsonSerializer.Deserialize<RegistryDocument>(json, SerializerOptions) ?? new RegistryDocument();
                    }
                    catch (JsonException)
                    {
                        BackUpCorruptFile(path, json);
                        document = new RegistryDocument();
                    }
                }

                document.Servers ??= new List<McpRegistryEntry>();
                if (!mutate(document.Servers))
                {
                    return;
                }

                var payload = JsonSerializer.Serialize(document, SerializerOptions);
                stream.SetLength(0);
                stream.Position = 0;
                using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
                writer.Write(payload);
                writer.Flush();
                stream.Flush(true);
                return;
            }
            catch (IOException) when (attempt < LockRetries - 1)
            {
                System.Threading.Thread.Sleep(LockRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < LockRetries - 1)
            {
                System.Threading.Thread.Sleep(LockRetryDelay);
            }
        }
    }

    private static FileStream OpenWithRetry(string path, FileMode mode, FileAccess access, FileShare share)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, mode, access, share);
            }
            catch (IOException) when (attempt < LockRetries - 1)
            {
                System.Threading.Thread.Sleep(LockRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < LockRetries - 1)
            {
                System.Threading.Thread.Sleep(LockRetryDelay);
            }
        }
    }

    private static void BackUpCorruptFile(string path, string contents)
    {
        try
        {
            // 文件此刻被当前流独占打开，无法 Move；改为另写一份备份，原文件随后由 Update 覆写。
            var backup = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            File.WriteAllText(backup, contents);
        }
        catch (Exception)
        {
        }
    }

    private sealed class RegistryDocument
    {
        public List<McpRegistryEntry>? Servers { get; set; } = new();
    }
}
