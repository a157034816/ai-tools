using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Configuration;

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
    /// <param name="protector">密钥保护器。</param>
    public JsonFileSettingsStore(ISecretProtector protector)
        : this(AiTools.Core.AiToolsPaths.GetSettingsFile(), protector)
    {
    }

    /// <summary>在指定的文件路径创建存储。</summary>
    /// <param name="filePath">设置文件路径。</param>
    /// <param name="protector">密钥保护器。</param>
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
                json = await ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new AiToolsConfigurationException($"无法读取设置文件：{_filePath}", ex);
            }

            AppSettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            }
            catch (JsonException ex)
            {
                throw new AiToolsConfigurationException($"设置文件格式无效：{_filePath}", ex);
            }

            if (settings == null)
            {
                return new AppSettings();
            }

            if (settings.Providers == null)
            {
                settings.Providers = new List<ProviderConfig>();
            }

            foreach (var provider in settings.Providers)
            {
                provider.Models ??= new List<ModelConfig>();
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
            await WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

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
            throw new AiToolsConfigurationException($"无法保存设置文件：{_filePath}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return text;
        }
    }

    private static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(content).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
