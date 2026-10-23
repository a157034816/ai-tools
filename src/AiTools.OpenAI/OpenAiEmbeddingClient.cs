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

/// <summary>OpenAI 嵌入 API 及兼容端点的嵌入客户端。</summary>
public sealed class OpenAiEmbeddingClient : IEmbeddingClient
{
    private readonly OpenAiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 <see cref="OpenAiEmbeddingClient"/> 类的新实例，并从提供的选项解析
    /// <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    public OpenAiEmbeddingClient(OpenAiOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// 使用显式指定的 <see cref="HttpClient"/> 初始化 <see cref="OpenAiEmbeddingClient"/> 类的新实例，
    /// 该客户端优先于 <see cref="AiHttpOptions.HttpClient"/>。
    /// 提供的客户端不会由本实例释放。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">要使用的 HTTP 客户端；为 null 时由选项（或共享的服务商）提供一个。</param>
    public OpenAiEmbeddingClient(OpenAiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => _options.ProviderName;

    /// <inheritdoc />
    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Inputs.Count == 1 ? request.Inputs[0] : request.Inputs,
            ["encoding_format"] = "float"
        };

        if (request.Dimensions.HasValue)
        {
            body["dimensions"] = request.Dimensions.Value;
        }

        if (!string.IsNullOrEmpty(request.InputType))
        {
            body["input_type"] = request.InputType;
        }

        var json = OpenAiHelpers.MergeAdditionalParameters(body, request.AdditionalParameters);

        using var timeoutSource = OpenAiHelpers.CreateRequestCancellationSource(cancellationToken, _options.Timeout);
        using var response = await _httpClient
            .SendWithRetryAsync(
                () => OpenAiHelpers.CreateRequest(HttpMethod.Post, "embeddings", _options, AiContent.Json(json)),
                ProviderName,
                _options,
                timeoutSource.Token)
            .ConfigureAwait(false);

        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseResponse(responseJson, request.Model);
    }

    private static EmbeddingResponse ParseResponse(string json, string requestedModel)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var model = JsonHelpers.GetString(root, "model") ?? requestedModel;
        var usage = OpenAiHelpers.MapUsage(JsonHelpers.GetObject(root, "usage"));
        var data = new List<Embedding>();

        var items = JsonHelpers.GetArray(root, "data");
        if (items.HasValue)
        {
            var position = 0;
            foreach (var item in items.Value)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    position++;
                    continue;
                }

                data.Add(new Embedding
                {
                    Index = JsonHelpers.GetInt32(item, "index") ?? position,
                    Vector = ReadVector(item)
                });

                position++;
            }
        }

        return new EmbeddingResponse
        {
            Model = model,
            Data = data,
            Usage = usage
        };
    }

    private static float[] ReadVector(JsonElement item)
    {
        var values = JsonHelpers.GetArray(item, "embedding");
        if (!values.HasValue)
        {
            return Array.Empty<float>();
        }

        var result = new List<float>();
        foreach (var value in values.Value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number))
            {
                result.Add(number);
            }
            else if (value.ValueKind == JsonValueKind.String &&
                float.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
            {
                result.Add(number);
            }
        }

        return result.ToArray();
    }
}
