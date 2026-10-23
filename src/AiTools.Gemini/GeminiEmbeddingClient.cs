using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Gemini.Internal;

namespace AiTools.Gemini;

/// <summary>Google Gemini 嵌入客户端，实现 <see cref="IEmbeddingClient"/>。</summary>
/// <remarks>
/// 单个输入使用 <c>models/{model}:embedContent</c>；多个输入使用
/// <c>models/{model}:batchEmbedContents</c>。<see cref="EmbeddingRequest.Dimensions"/> 映射为
/// <c>outputDimensionality</c>，常见的 <see cref="EmbeddingRequest.InputType"/> 值
/// （<c>query</c>、<c>document</c> 等）映射为 Gemini 的 <c>taskType</c> 值。
/// Gemini 不会为嵌入返回 Token 用量，因此 <see cref="EmbeddingResponse.Usage"/>
/// 通常为 <see langword="null"/>。
/// </remarks>
public sealed class GeminiEmbeddingClient : IEmbeddingClient
{
    private readonly GeminiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>使用提供的选项初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    public GeminiEmbeddingClient(GeminiOptions options)
        : this(options, null)
    {
    }

    /// <summary>使用提供的选项和 HTTP 客户端初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">可选的 HTTP 客户端；为 null 时创建共享客户端。</param>
    public GeminiEmbeddingClient(GeminiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, GeminiInfrastructure.ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => GeminiInfrastructure.ProviderName;

    /// <inheritdoc />
    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var inputs = request.Inputs ?? Array.Empty<string>();
        if (inputs.Count == 0)
        {
            return new EmbeddingResponse
            {
                Model = request.Model,
                Data = Array.Empty<Embedding>(),
                Usage = null
            };
        }

        var model = GeminiInfrastructure.NormalizeModel(request.Model);
        if (model.Length == 0)
        {
            throw new ArgumentException("A model name is required.", nameof(request));
        }

        var single = inputs.Count == 1;
        var method = single ? "embedContent" : "batchEmbedContents";
        var body = GeminiInfrastructure.MergeTopLevel(
            single ? BuildSingleBody(model, inputs[0], request) : BuildBatchBody(model, inputs, request),
            request.AdditionalParameters);
        var uri = GeminiInfrastructure.BuildUri(_options, request.Model, method, stream: false);

        using var timeout = GeminiInfrastructure.CreateTimeoutSource(_options, cancellationToken);
        var token = timeout?.Token ?? cancellationToken;

        using var response = await _httpClient
            .SendWithRetryAsync(
                () => GeminiInfrastructure.CreateRequest(HttpMethod.Post, uri, _options, body, stream: false),
                ProviderName,
                _options,
                token)
            .ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException ex)
        {
            throw GeminiInfrastructure.InvalidJson(responseBody, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var embeddings = new List<Embedding>();

            if (single)
            {
                if (GeminiInfrastructure.TryGetProperty(root, "embedding", out var embedding) &&
                    embedding.ValueKind == JsonValueKind.Object)
                {
                    embeddings.Add(new Embedding { Index = 0, Vector = ReadVector(embedding) });
                }
            }
            else if (GeminiInfrastructure.TryGetProperty(root, "embeddings", out var array) &&
                     array.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in array.EnumerateArray())
                {
                    embeddings.Add(new Embedding
                    {
                        Index = index,
                        Vector = item.ValueKind == JsonValueKind.Object ? ReadVector(item) : Array.Empty<float>()
                    });
                    index++;
                }
            }

            return new EmbeddingResponse
            {
                Model = request.Model,
                Data = embeddings,
                Usage = GeminiInfrastructure.ParseUsage(root)
            };
        }
    }

    private static string BuildSingleBody(string model, string input, EmbeddingRequest request)
    {
        return GeminiInfrastructure.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("model", "models/" + model);
            writer.WritePropertyName("content");
            writer.WriteStartObject();
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("text", input ?? string.Empty);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();

            if (request.Dimensions.HasValue)
            {
                writer.WriteNumber("outputDimensionality", request.Dimensions.Value);
            }

            WriteTaskType(writer, request.InputType);
            writer.WriteEndObject();
        });
    }

    private static string BuildBatchBody(string model, IReadOnlyList<string> inputs, EmbeddingRequest request)
    {
        return GeminiInfrastructure.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("requests");
            writer.WriteStartArray();
            foreach (var input in inputs)
            {
                writer.WriteStartObject();
                writer.WriteString("model", "models/" + model);
                writer.WritePropertyName("content");
                writer.WriteStartObject();
                writer.WritePropertyName("parts");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("text", input ?? string.Empty);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();

                if (request.Dimensions.HasValue)
                {
                    writer.WriteNumber("outputDimensionality", request.Dimensions.Value);
                }

                WriteTaskType(writer, request.InputType);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static void WriteTaskType(Utf8JsonWriter writer, string? inputType)
    {
        var taskType = MapTaskType(inputType);
        if (taskType != null)
        {
            writer.WriteString("taskType", taskType);
        }
    }

    private static string? MapTaskType(string? inputType)
    {
        if (string.IsNullOrWhiteSpace(inputType))
        {
            return null;
        }

        switch (inputType!.Trim().ToLowerInvariant())
        {
            case "query":
                return "RETRIEVAL_QUERY";
            case "document":
            case "passage":
                return "RETRIEVAL_DOCUMENT";
            case "similarity":
            case "semantic_similarity":
                return "SEMANTIC_SIMILARITY";
            case "classification":
                return "CLASSIFICATION";
            case "clustering":
                return "CLUSTERING";
            case "question_answering":
                return "QUESTION_ANSWERING";
            case "fact_verification":
                return "FACT_VERIFICATION";
            case "code_retrieval_query":
                return "CODE_RETRIEVAL_QUERY";
            default:
                return inputType.Trim().ToUpperInvariant();
        }
    }

    private static float[] ReadVector(JsonElement embedding)
    {
        if (!GeminiInfrastructure.TryGetProperty(embedding, "values", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<float>();
        }

        var list = new List<float>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                list.Add((float)number);
            }
            else if (value.ValueKind == JsonValueKind.String &&
                     double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                list.Add((float)number);
            }
        }

        return list.ToArray();
    }
}
