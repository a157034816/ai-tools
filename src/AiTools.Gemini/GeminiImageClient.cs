using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Gemini.Internal;

namespace AiTools.Gemini;

/// <summary>Google Gemini 图像生成客户端，实现 <see cref="IImageClient"/>。</summary>
/// <remarks>
/// 图像通过 <c>generateContent</c> 并设置
/// <c>generationConfig.responseModalities = ["TEXT", "IMAGE"]</c> 生成，多模态图像模型
/// （如 <c>gemini-2.5-flash-image</c>）支持这种方式。不使用 Imagen 的 <c>:predict</c> 端点。
/// 当 <see cref="ImageGenerationRequest.Count"/> 大于一时，请求会依次发出。
/// 图像始终以字节形式返回；对 Gemini 而言 <see cref="GeneratedImage.Url"/> 永远为空。
/// Gemini 图像生成不接受的字段（尺寸、质量、风格、输出格式、背景、响应格式）会被忽略；
/// 请使用 <see cref="ImageGenerationRequest.AdditionalParameters"/> 设置
/// <c>generationConfig.imageConfig</c> 等服务商特有的控制项。
/// </remarks>
public sealed class GeminiImageClient : IImageClient
{
    private readonly GeminiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>使用提供的选项初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    public GeminiImageClient(GeminiOptions options)
        : this(options, null)
    {
    }

    /// <summary>使用提供的选项和 HTTP 客户端初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">可选的 HTTP 客户端；为 null 时创建共享客户端。</param>
    public GeminiImageClient(GeminiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, GeminiInfrastructure.ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => GeminiInfrastructure.ProviderName;

    /// <inheritdoc />
    public async Task<ImageGenerationResponse> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var count = Math.Max(1, request.Count);
        var images = new List<GeneratedImage>();

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            images.AddRange(await GenerateOnceAsync(request, cancellationToken).ConfigureAwait(false));
        }

        return new ImageGenerationResponse { Images = images };
    }

    private async Task<IReadOnlyList<GeneratedImage>> GenerateOnceAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var body = GeminiInfrastructure.MergeTopLevel(BuildBody(request), request.AdditionalParameters);
        var uri = GeminiInfrastructure.BuildUri(_options, request.Model, "generateContent", stream: false);

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
            var images = new List<GeneratedImage>();
            var candidate = GeminiInfrastructure.GetFirstCandidate(root);
            if (candidate == null)
            {
                return images;
            }

            if (!GeminiInfrastructure.TryGetProperty(candidate.Value, "content", out var content) ||
                content.ValueKind != JsonValueKind.Object ||
                !GeminiInfrastructure.TryGetProperty(content, "parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                return images;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!GeminiInfrastructure.TryGetProperty(part, "inlineData", out var inlineData) ||
                    inlineData.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var data = GeminiInfrastructure.TryDecodeBase64(
                    GeminiInfrastructure.ReadString(inlineData, "data"));
                if (data == null)
                {
                    continue;
                }

                images.Add(new GeneratedImage
                {
                    Bytes = data,
                    MediaType = GeminiInfrastructure.ReadString(inlineData, "mimeType")
                });
            }

            return images;
        }
    }

    private static string BuildBody(ImageGenerationRequest request)
    {
        return GeminiInfrastructure.WriteJson(writer =>
        {
            writer.WriteStartObject();

            writer.WritePropertyName("contents");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("text", request.Prompt ?? string.Empty);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WritePropertyName("generationConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("responseModalities");
            writer.WriteStartArray();
            writer.WriteStringValue("TEXT");
            writer.WriteStringValue("IMAGE");
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteEndObject();
        });
    }
}
