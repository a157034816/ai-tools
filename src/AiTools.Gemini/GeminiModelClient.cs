using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Core.Json;
using AiTools.Gemini.Internal;

namespace AiTools.Gemini;

/// <summary>
/// Google Gemini <c>GET /models</c> 的模型列表客户端。
/// </summary>
/// <remarks>
/// 该接口使用 <c>pageToken</c> / <c>nextPageToken</c> 分页，本客户端会自动翻页。
/// 返回的模型名带有 <c>models/</c> 前缀，本客户端会将其剥离，便于直接写入配置。
/// </remarks>
public sealed class GeminiModelClient : IModelCatalog
{
    private const int DefaultPageSize = 1000;

    private readonly GeminiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>使用提供的选项初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    public GeminiModelClient(GeminiOptions options)
        : this(options, null)
    {
    }

    /// <summary>使用提供的选项和 HTTP 客户端初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">可选的 HTTP 客户端；为 null 时使用共享客户端。</param>
    public GeminiModelClient(GeminiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, GeminiInfrastructure.ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => GeminiInfrastructure.ProviderName;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = new List<ModelInfo>();
        string? pageToken = null;

        while (true)
        {
            var uri = BuildEndpoint(pageToken);
            using var timeout = GeminiInfrastructure.CreateTimeoutSource(_options, cancellationToken);
            var token = timeout?.Token ?? cancellationToken;

            using var response = await _httpClient
                .SendWithRetryAsync(
                    () => GeminiInfrastructure.CreateRequest(HttpMethod.Get, uri, _options, body: null, stream: false),
                    ProviderName,
                    _options,
                    token,
                    new AiRequestContext { ProviderName = ProviderName, Operation = "models" })
                .ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var (page, nextPageToken) = ParsePage(json);

            // 服务端可能忽略 pageToken 并返回同一页；此时丢弃该页，避免重复与死循环。
            if (pageToken != null && string.Equals(nextPageToken, pageToken, StringComparison.Ordinal))
            {
                break;
            }

            models.AddRange(page);

            if (string.IsNullOrWhiteSpace(nextPageToken))
            {
                break;
            }

            pageToken = nextPageToken;
        }

        return models;
    }

    private Uri BuildEndpoint(string? pageToken)
    {
        var baseAddress = _options.BaseAddress ?? GeminiOptions.DefaultBaseAddress;
        var baseText = baseAddress.IsAbsoluteUri ? baseAddress.AbsoluteUri : baseAddress.OriginalString;
        baseText = baseText.TrimEnd('/');

        // 基地址可能已包含版本段（默认就是这样）；若宿主自行去掉了版本段，补回 ApiVersion。
        var version = (_options.ApiVersion ?? string.Empty).Trim().Trim('/');
        if (version.Length > 0)
        {
            var slashIndex = baseText.LastIndexOf('/');
            var lastSegment = slashIndex >= 0 ? baseText.Substring(slashIndex + 1) : baseText;
            var looksLikeVersion = lastSegment.Length >= 2 &&
                                   (lastSegment[0] == 'v' || lastSegment[0] == 'V') &&
                                   char.IsDigit(lastSegment[1]);
            if (!looksLikeVersion)
            {
                baseText = baseText + "/" + version;
            }
        }

        var query = new List<string> { "pageSize=" + DefaultPageSize };
        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            query.Add("pageToken=" + Uri.EscapeDataString(pageToken!));
        }

        if (_options.UseQueryStringApiKey && !string.IsNullOrEmpty(_options.ApiKey))
        {
            query.Add("key=" + Uri.EscapeDataString(_options.ApiKey));
        }

        return new Uri(baseText + "/models?" + string.Join("&", query), UriKind.Absolute);
    }

    private (List<ModelInfo> Models, string? NextPageToken) ParsePage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var models = new List<ModelInfo>();

            if (JsonHelpers.GetArray(root, "models") is { } items)
            {
                foreach (var item in items)
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = JsonHelpers.GetString(item, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    models.Add(new ModelInfo
                    {
                        Id = GeminiInfrastructure.NormalizeModel(name),
                        DisplayName = JsonHelpers.GetString(item, "displayName") ?? string.Empty
                    });
                }
            }

            return (models, JsonHelpers.GetString(root, "nextPageToken"));
        }
        catch (JsonException ex)
        {
            throw new AiServiceException(
                "The gemini model list response could not be parsed as JSON.",
                ProviderName,
                responseBody: json,
                innerException: ex);
        }
    }
}
