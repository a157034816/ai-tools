using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Core.Json;

namespace AiTools.OpenAI;

/// <summary>OpenAI 语音合成与转写端点的音频客户端。</summary>
public sealed class OpenAiAudioClient : IAudioClient
{
    private readonly OpenAiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 <see cref="OpenAiAudioClient"/> 类的新实例，并从提供的选项解析
    /// <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    public OpenAiAudioClient(OpenAiOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// 使用显式指定的 <see cref="HttpClient"/> 初始化 <see cref="OpenAiAudioClient"/> 类的新实例，
    /// 该客户端优先于 <see cref="AiHttpOptions.HttpClient"/>。
    /// 提供的客户端不会由本实例释放。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">要使用的 HTTP 客户端；为 null 时由选项（或共享的服务商）提供一个。</param>
    public OpenAiAudioClient(OpenAiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => _options.ProviderName;

    /// <inheritdoc />
    public async Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Input,
            ["voice"] = string.IsNullOrEmpty(request.Voice) ? "alloy" : request.Voice
        };

        if (!string.IsNullOrEmpty(request.ResponseFormat))
        {
            body["response_format"] = request.ResponseFormat;
        }

        if (request.Speed.HasValue)
        {
            body["speed"] = request.Speed.Value;
        }

        var json = OpenAiHelpers.MergeAdditionalParameters(body, request.AdditionalParameters);

        using var timeoutSource = OpenAiHelpers.CreateRequestCancellationSource(cancellationToken, _options.Timeout);
        using var response = await _httpClient
            .SendWithRetryAsync(
                () => OpenAiHelpers.CreateRequest(HttpMethod.Post, "audio/speech", _options, AiContent.Json(json)),
                ProviderName,
                _options,
                timeoutSource.Token)
            .ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return new SpeechResponse
        {
            Audio = bytes,
            MediaType = request.ResponseFormat ?? "mp3"
        };
    }

    /// <inheritdoc />
    public async Task<TranscriptionResponse> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using var timeoutSource = OpenAiHelpers.CreateRequestCancellationSource(cancellationToken, _options.Timeout);
        using var response = await _httpClient
            .SendWithRetryAsync(
                () => OpenAiHelpers.CreateRequest(HttpMethod.Post, "audio/transcriptions", _options, BuildTranscriptionContent(request)),
                ProviderName,
                _options,
                timeoutSource.Token)
            .ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseTranscription(json);
    }

    private static HttpContent BuildTranscriptionContent(TranscriptionRequest request)
    {
        var form = new MultipartFormDataContent();
        var additional = request.AdditionalParameters;
        var overridden = new HashSet<string>(StringComparer.Ordinal);
        if (additional.HasValue && additional.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in additional.Value.EnumerateObject())
            {
                overridden.Add(property.Name);
            }
        }

        var fileName = string.IsNullOrEmpty(request.FileName) ? "audio.mp3" : request.FileName;
        var mediaType = !string.IsNullOrEmpty(request.MediaType)
            ? request.MediaType!
            : GuessMediaType(fileName);

        var fileContent = new ByteArrayContent(request.Audio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(fileContent, "file", fileName);

        if (!overridden.Contains("model"))
        {
            form.Add(new StringContent(request.Model), "model");
        }

        if (!string.IsNullOrEmpty(request.Language) && !overridden.Contains("language"))
        {
            form.Add(new StringContent(request.Language!), "language");
        }

        if (!string.IsNullOrEmpty(request.Prompt) && !overridden.Contains("prompt"))
        {
            form.Add(new StringContent(request.Prompt!), "prompt");
        }

        if (!overridden.Contains("response_format"))
        {
            form.Add(new StringContent(request.IncludeSegments ? "verbose_json" : "json"), "response_format");
        }

        if (additional.HasValue && additional.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in additional.Value.EnumerateObject())
            {
                var value = OpenAiHelpers.StringifyJsonValue(property.Value);
                if (value != null)
                {
                    form.Add(new StringContent(value), property.Name);
                }
            }
        }

        return form;
    }

    private static TranscriptionResponse ParseTranscription(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // 兼容服务商有时在请求 JSON 的情况下也会返回纯文本。
            return new TranscriptionResponse { Text = body };
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new TranscriptionResponse { Text = body };
            }

            var segments = new List<TranscriptionSegment>();
            var segmentArray = JsonHelpers.GetArray(root, "segments");
            if (segmentArray.HasValue)
            {
                var position = 0;
                foreach (var item in segmentArray.Value)
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        position++;
                        continue;
                    }

                    segments.Add(new TranscriptionSegment
                    {
                        Id = JsonHelpers.GetInt32(item, "id") ?? position,
                        Start = OpenAiHelpers.GetDouble(item, "start") ?? 0,
                        End = OpenAiHelpers.GetDouble(item, "end") ?? 0,
                        Text = JsonHelpers.GetString(item, "text") ?? string.Empty
                    });

                    position++;
                }
            }

            return new TranscriptionResponse
            {
                Text = JsonHelpers.GetString(root, "text") ?? string.Empty,
                Language = JsonHelpers.GetString(root, "language"),
                DurationSeconds = OpenAiHelpers.GetDouble(root, "duration"),
                Segments = segments
            };
        }
    }

    private static string GuessMediaType(string fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(extension) && extension!.Length > 1)
        {
            switch (extension.Substring(1).ToLowerInvariant())
            {
                case "mp3":
                case "mpga":
                case "mpeg":
                    return "audio/mpeg";
                case "wav":
                    return "audio/wav";
                case "m4a":
                case "mp4":
                    return "audio/mp4";
                case "webm":
                    return "audio/webm";
                case "ogg":
                    return "audio/ogg";
                case "flac":
                    return "audio/flac";
            }
        }

        return "application/octet-stream";
    }
}
