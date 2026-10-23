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

/// <summary>OpenAI 图像 API 及兼容端点的图像生成客户端。</summary>
public sealed class OpenAiImageClient : IImageClient
{
    private readonly OpenAiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 <see cref="OpenAiImageClient"/> 类的新实例，并从提供的选项解析
    /// <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    public OpenAiImageClient(OpenAiOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// 使用显式指定的 <see cref="HttpClient"/> 初始化 <see cref="OpenAiImageClient"/> 类的新实例，
    /// 该客户端优先于 <see cref="AiHttpOptions.HttpClient"/>。
    /// 提供的客户端不会由本实例释放。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">要使用的 HTTP 客户端；为 null 时由选项（或共享的服务商）提供一个。</param>
    public OpenAiImageClient(OpenAiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => _options.ProviderName;

    /// <inheritdoc />
    public async Task<ImageGenerationResponse> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var wantsBase64 = request.ResponseFormat == ImageResponseFormat.Base64;
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.Count,
            ["response_format"] = wantsBase64 ? "b64_json" : "url"
        };

        if (!string.IsNullOrEmpty(request.Size))
        {
            body["size"] = request.Size;
        }

        if (!string.IsNullOrEmpty(request.Quality))
        {
            body["quality"] = request.Quality;
        }

        if (!string.IsNullOrEmpty(request.Style))
        {
            body["style"] = request.Style;
        }

        if (!string.IsNullOrEmpty(request.OutputFormat))
        {
            body["output_format"] = request.OutputFormat;
        }

        if (!string.IsNullOrEmpty(request.Background))
        {
            body["background"] = request.Background;
        }

        var json = OpenAiHelpers.MergeAdditionalParameters(body, request.AdditionalParameters);

        using var timeoutSource = OpenAiHelpers.CreateRequestCancellationSource(cancellationToken, _options.Timeout);
        using var response = await _httpClient
            .SendWithRetryAsync(
                () => OpenAiHelpers.CreateRequest(HttpMethod.Post, "images/generations", _options, AiContent.Json(json)),
                ProviderName,
                _options,
                timeoutSource.Token)
            .ConfigureAwait(false);

        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseResponse(responseJson, request.OutputFormat);
    }

    private static ImageGenerationResponse ParseResponse(string json, string? outputFormat)
    {
        using var document = JsonDocument.Parse(json);
        var images = new List<GeneratedImage>();

        var data = JsonHelpers.GetArray(document.RootElement, "data");
        if (data.HasValue)
        {
            foreach (var item in data.Value)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var url = JsonHelpers.GetString(item, "url");
                var revisedPrompt = JsonHelpers.GetString(item, "revised_prompt");
                var base64 = JsonHelpers.GetString(item, "b64_json");
                byte[]? bytes = null;

                if (!string.IsNullOrEmpty(base64))
                {
                    try
                    {
                        bytes = Convert.FromBase64String(base64!);
                    }
                    catch (FormatException)
                    {
                        bytes = null;
                    }
                }

                images.Add(new GeneratedImage
                {
                    Url = url,
                    Bytes = bytes,
                    RevisedPrompt = revisedPrompt,
                    MediaType = bytes != null ? ResolveMediaType(outputFormat) : null
                });
            }
        }

        return new ImageGenerationResponse { Images = images };
    }

    private static string ResolveMediaType(string? outputFormat)
    {
        if (string.IsNullOrEmpty(outputFormat))
        {
            return "image/png";
        }

        switch (outputFormat!.ToLowerInvariant())
        {
            case "jpg":
            case "jpeg":
                return "image/jpeg";
            case "webp":
                return "image/webp";
            case "gif":
                return "image/gif";
            default:
                return "image/" + outputFormat.ToLowerInvariant();
        }
    }
}
