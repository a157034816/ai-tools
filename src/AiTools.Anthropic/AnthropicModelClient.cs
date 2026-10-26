using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Core.Json;

namespace AiTools.Anthropic;

/// <summary>
/// Anthropic <c>GET /models</c> 的模型列表客户端。
/// </summary>
/// <remarks>
/// 该接口使用游标分页（<c>after_id</c> / <c>has_more</c> / <c>last_id</c>），
/// 本客户端会自动翻页直到取完。
/// </remarks>
public sealed class AnthropicModelClient : IModelCatalog
{
    private const int DefaultPageSize = 1000;

    private readonly AnthropicOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 <see cref="AnthropicModelClient"/> 类的新实例，并从提供的选项解析
    /// <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    public AnthropicModelClient(AnthropicOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// 使用显式指定的 <see cref="HttpClient"/> 初始化 <see cref="AnthropicModelClient"/> 类的新实例。
    /// 提供的客户端不会由本实例释放。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">要使用的 HTTP 客户端；为 null 时由选项（或共享的服务商）提供一个。</param>
    public AnthropicModelClient(AnthropicOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, "anthropic");
    }

    /// <inheritdoc />
    public string ProviderName => "anthropic";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = new List<ModelInfo>();
        string? afterId = null;

        while (true)
        {
            var uri = BuildEndpoint(afterId);
            using var timeoutSource = CreateCancellationSource(cancellationToken);
            using var response = await _httpClient
                .SendWithRetryAsync(
                    () => CreateRequest(uri),
                    ProviderName,
                    _options,
                    timeoutSource.Token,
                    new AiRequestContext { ProviderName = ProviderName, Operation = "models" })
                .ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var (page, hasMore, lastId) = ParsePage(json);

            // 服务端可能忽略分页参数并返回同一页；此时丢弃该页，避免重复与死循环。
            if (afterId != null && string.Equals(lastId, afterId, StringComparison.Ordinal))
            {
                break;
            }

            models.AddRange(page);

            if (!hasMore || string.IsNullOrWhiteSpace(lastId))
            {
                break;
            }

            afterId = lastId;
        }

        return models;
    }

    private Uri BuildEndpoint(string? afterId)
    {
        var baseAddress = _options.BaseAddress ?? _httpClient.BaseAddress;
        if (baseAddress == null)
        {
            throw new InvalidOperationException("An Anthropic base address must be configured.");
        }

        var builder = new UriBuilder(baseAddress.ToString().TrimEnd('/') + "/models");
        var query = "limit=" + DefaultPageSize;
        if (!string.IsNullOrWhiteSpace(afterId))
        {
            query += "&after_id=" + Uri.EscapeDataString(afterId!);
        }

        builder.Query = query;
        return builder.Uri;
    }

    private HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (!string.IsNullOrEmpty(_options.AuthorizationHeader))
        {
            request.Headers.TryAddWithoutValidation("Authorization", _options.AuthorizationHeader);
        }
        else if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        }

        if (!string.IsNullOrEmpty(_options.AnthropicVersion))
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", _options.AnthropicVersion);
        }

        if (!string.IsNullOrEmpty(_options.Beta))
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", _options.Beta);
        }

        if (_options.DefaultHeaders != null)
        {
            foreach (var header in _options.DefaultHeaders)
            {
                if (!string.IsNullOrEmpty(header.Key))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return request;
    }

    private CancellationTokenSource CreateCancellationSource(CancellationToken cancellationToken)
    {
        var timeout = _options.Timeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private (List<ModelInfo> Models, bool HasMore, string? LastId) ParsePage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var models = new List<ModelInfo>();

            if (JsonHelpers.GetArray(root, "data") is { } data)
            {
                foreach (var item in data)
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
                    if (JsonHelpers.GetString(item, "created_at") is { } createdAtText &&
                        DateTimeOffset.TryParse(createdAtText, out var parsed))
                    {
                        createdAt = parsed;
                    }

                    models.Add(new ModelInfo
                    {
                        Id = id!,
                        DisplayName = JsonHelpers.GetString(item, "display_name") ?? string.Empty,
                        CreatedAt = createdAt
                    });
                }
            }

            return (models, JsonHelpers.GetBool(root, "has_more") ?? false, JsonHelpers.GetString(root, "last_id"));
        }
        catch (JsonException ex)
        {
            throw new AiServiceException(
                "The anthropic model list response could not be parsed as JSON.",
                ProviderName,
                responseBody: json,
                innerException: ex);
        }
    }
}
