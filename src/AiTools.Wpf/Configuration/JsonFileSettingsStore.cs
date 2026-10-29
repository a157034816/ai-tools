using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Wpf.Configuration;

/// <summary>
/// 默认设置存储，将 JSON 写入 <c>~/.ai-tools/settings.json</c>（或指定的路径），
/// 并使用 <see cref="ISecretProtector"/> 保护 API Key。
/// </summary>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _filePath;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>在默认位置（<c>~/.ai-tools/settings.json</c>）创建存储。</summary>
    public JsonFileSettingsStore(ISecretProtector protector)
        : this(AiTools.Core.AiToolsPaths.GetSettingsFile(), protector)
    {
    }

    /// <summary>在指定的文件路径创建存储。</summary>
    public JsonFileSettingsStore(string filePath, ISecretProtector protector)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>获取此存储使用的文件路径。</summary>
    public string FilePath => _filePath;

    /// <inheritdoc />
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new AiToolsWpfException($"无法读取设置文件：{_filePath}", ex);
            }

            AppSettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            }
            catch (JsonException ex)
            {
                throw new AiToolsWpfException($"设置文件格式无效：{_filePath}", ex);
            }

            if (settings == null)
            {
                return new AppSettings();
            }

            foreach (var provider in settings.Providers)
            {
                provider.ApiKey = _protector.Unprotect(provider.ApiKey);
            }

            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = settings.Clone();
            foreach (var provider in snapshot.Providers)
            {
                provider.ApiKey = _protector.Protect(provider.ApiKey);
            }

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

            // 原子替换，避免写入中途崩溃导致设置文件损坏。
            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        catch (IOException ex)
        {
            throw new AiToolsWpfException($"无法保存设置文件：{_filePath}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }
}
