using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Core.Json;

namespace AiTools.OpenAI;

/// <summary>
/// OpenAI <c>GET /models</c> 及兼容端点的模型列表客户端。
/// </summary>
public sealed class OpenAiModelClient : IModelCatalog
{
    private readonly OpenAiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 <see cref="OpenAiModelClient"/> 类的新实例，并从提供的选项解析
    /// <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    public OpenAiModelClient(OpenAiOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// 使用显式指定的 <see cref="HttpClient"/> 初始化 <see cref="OpenAiModelClient"/> 类的新实例。
    /// 提供的客户端不会由本实例释放。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">要使用的 HTTP 客户端；为 null 时由选项（或共享的服务商）提供一个。</param>
    public OpenAiModelClient(OpenAiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => _options.ProviderName;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var timeoutSource = OpenAiHelpers.CreateRequestCancellationSource(cancellationToken, _options.Timeout);
        using var response = await _httpClient
            .SendWithRetryAsync(
                () => OpenAiHelpers.CreateRequest(HttpMethod.Get, "models", _options, null),
                ProviderName,
                _options,
                timeoutSource.Token,
                new AiRequestContext { ProviderName = ProviderName, Operation = "models" })
            .ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseModels(json);
    }

    private IReadOnlyList<ModelInfo> ParseModels(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var models = new List<ModelInfo>();

            // 标准响应为 { "data": [...] }；少数兼容网关直接返回数组。
            JsonElement.ArrayEnumerator? data = null;
            if (root.ValueKind == JsonValueKind.Array)
            {
                data = root.EnumerateArray();
            }
            else
            {
                data = JsonHelpers.GetArray(root, "data");
            }

            if (data.HasValue)
            {
                foreach (var item in data.Value)
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var id = JsonHelpers.GetString(item, "id");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    DateTimeOffset? createdAt = null;
                    var created = JsonHelpers.GetInt32(item, "created");
                    if (created is > 0)
                    {
                        try
                        {
                            createdAt = DateTimeOffset.FromUnixTimeSeconds(created.Value);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // 兼容端点可能返回异常时间戳，忽略即可。
                        }
                    }

                    models.Add(new ModelInfo
                    {
                        Id = id!,
                        OwnedBy = JsonHelpers.GetString(item, "owned_by"),
                        CreatedAt = createdAt
                    });
                }
            }

            return models;
        }
        catch (JsonException ex)
        {
            throw new AiServiceException(
                $"The {ProviderName} model list response could not be parsed as JSON.",
                ProviderName,
                responseBody: json,
                innerException: ex);
        }
    }
}
