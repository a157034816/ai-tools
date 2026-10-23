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

/// <summary>Google Gemini 音频客户端，实现 <see cref="IAudioClient"/>。</summary>
/// <remarks>
/// <para>
/// 语音合成使用 <c>generateContent</c> 并设置 <c>generationConfig.responseModalities = ["AUDIO"]</c>
/// 以及 <c>speechConfig</c> 块。因此 <see cref="SpeechRequest.Model"/> 必须是支持 TTS 的
/// Gemini 模型，例如 <c>gemini-2.5-flash-preview-tts</c>。<see cref="SpeechRequest.Voice"/> 会作为
/// 预置音色名称发送（例如 <c>Kore</c>）；统一默认值 <c>alloy</c> 会映射为 <c>Kore</c>，
/// 因为 Gemini 未定义 <c>alloy</c> 音色。
/// </para>
/// <para>
/// 转写会将音频以内联方式发送，并附带要求返回 JSON 的文本 Prompt，然后解析模型的文本
/// 响应。当 JSON 格式错误时，原始文本会作为 <see cref="TranscriptionResponse.Text"/> 返回。
/// </para>
/// </remarks>
public sealed class GeminiAudioClient : IAudioClient
{
    private readonly GeminiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>使用提供的选项初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    public GeminiAudioClient(GeminiOptions options)
        : this(options, null)
    {
    }

    /// <summary>使用提供的选项和 HTTP 客户端初始化新客户端。</summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">可选的 HTTP 客户端；为 null 时创建共享客户端。</param>
    public GeminiAudioClient(GeminiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, GeminiInfrastructure.ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => GeminiInfrastructure.ProviderName;

    /// <inheritdoc />
    public async Task<SpeechResponse> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var body = GeminiInfrastructure.MergeTopLevel(BuildSpeechBody(request), request.AdditionalParameters);
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
            var candidate = GeminiInfrastructure.GetFirstCandidate(root);
            if (candidate != null &&
                GeminiInfrastructure.TryGetProperty(candidate.Value, "content", out var content) &&
                content.ValueKind == JsonValueKind.Object &&
                GeminiInfrastructure.TryGetProperty(content, "parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object ||
                        !GeminiInfrastructure.TryGetProperty(part, "inlineData", out var inlineData) ||
                        inlineData.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var audio = GeminiInfrastructure.TryDecodeBase64(
                        GeminiInfrastructure.ReadString(inlineData, "data"));
                    if (audio != null)
                    {
                        return new SpeechResponse
                        {
                            Audio = audio,
                            MediaType = GeminiInfrastructure.ReadString(inlineData, "mimeType")
                        };
                    }
                }
            }

            return new SpeechResponse
            {
                Audio = Array.Empty<byte>(),
                MediaType = NormalizeAudioMediaType(request.ResponseFormat)
            };
        }
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

        var prompt = string.IsNullOrWhiteSpace(request.Prompt)
            ? "Transcribe this audio accurately."
            : request.Prompt!;

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            prompt += " The audio language is " + request.Language!.Trim() + ".";
        }

        prompt += request.IncludeSegments
            ? " Return JSON with fields text, language, and segments (each segment has start, end, text)."
            : " Return JSON with fields text and language.";

        var body = GeminiInfrastructure.MergeTopLevel(BuildTranscriptionBody(request, prompt), request.AdditionalParameters);
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

        string text;
        using (document)
        {
            var root = document.RootElement;
            var candidate = GeminiInfrastructure.GetFirstCandidate(root);
            text = ExtractResponseText(candidate) ?? string.Empty;
        }

        return ParseTranscription(text, request.IncludeSegments);
    }

    private static string? ExtractResponseText(JsonElement? candidate)
    {
        if (candidate == null ||
            !GeminiInfrastructure.TryGetProperty(candidate.Value, "content", out var content) ||
            content.ValueKind != JsonValueKind.Object ||
            !GeminiInfrastructure.TryGetProperty(content, "parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object ||
                GeminiInfrastructure.ReadBool(part, "thought") == true)
            {
                continue;
            }

            var value = GeminiInfrastructure.ReadString(part, "text");
            if (!string.IsNullOrEmpty(value))
            {
                if (builder.Length > 0)
                {
                    builder.Append("\n\n");
                }

                builder.Append(value);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static TranscriptionResponse ParseTranscription(string text, bool includeSegments)
    {
        var trimmed = StripCodeFence(text).Trim();
        if (trimmed.Length == 0)
        {
            return new TranscriptionResponse { Text = text ?? string.Empty };
        }

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            // 模型有时会忽略 responseSchema；此时回退为原始文本。
        }

        if (document == null)
        {
            return new TranscriptionResponse { Text = text ?? string.Empty };
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new TranscriptionResponse { Text = text ?? string.Empty };
            }

            var resultText = GeminiInfrastructure.ReadString(root, "text");
            var language = GeminiInfrastructure.ReadString(root, "language");
            var segments = new List<TranscriptionSegment>();

            if (includeSegments &&
                GeminiInfrastructure.TryGetProperty(root, "segments", out var segmentArray) &&
                segmentArray.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var segment in segmentArray.EnumerateArray())
                {
                    if (segment.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    segments.Add(new TranscriptionSegment
                    {
                        Id = GeminiInfrastructure.ReadInt(segment, "id") ?? index,
                        Start = GeminiInfrastructure.ReadDouble(segment, "start") ?? 0,
                        End = GeminiInfrastructure.ReadDouble(segment, "end") ?? 0,
                        Text = GeminiInfrastructure.ReadString(segment, "text") ?? string.Empty
                    });
                    index++;
                }
            }

            return new TranscriptionResponse
            {
                Text = resultText ?? string.Empty,
                Language = language,
                Segments = segments
            };
        }
    }

    private static string StripCodeFence(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value!.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var newline = text.IndexOf('\n');
        if (newline < 0)
        {
            return text;
        }

        text = text.Substring(newline + 1).TrimEnd();
        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text.Substring(0, text.Length - 3);
        }

        return text.Trim();
    }

    private static string BuildSpeechBody(SpeechRequest request)
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
            writer.WriteString("text", request.Input ?? string.Empty);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WritePropertyName("generationConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("responseModalities");
            writer.WriteStartArray();
            writer.WriteStringValue("AUDIO");
            writer.WriteEndArray();

            writer.WritePropertyName("speechConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("voiceConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("prebuiltVoiceConfig");
            writer.WriteStartObject();
            writer.WriteString("voiceName", ResolveVoiceName(request.Voice));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();

            writer.WriteEndObject();
        });
    }

    private static string ResolveVoiceName(string? voice)
    {
        if (string.IsNullOrWhiteSpace(voice) ||
            string.Equals(voice, "alloy", StringComparison.OrdinalIgnoreCase))
        {
            // "alloy" 是统一模型的默认值；Gemini 要求使用其预置音色名称之一。
            return "Kore";
        }

        return voice!;
    }

    private static string BuildTranscriptionBody(TranscriptionRequest request, string prompt)
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
            writer.WriteString("text", prompt);
            writer.WriteEndObject();

            var mediaType = ResolveMediaType(request);
            writer.WriteStartObject();
            writer.WritePropertyName("inlineData");
            writer.WriteStartObject();
            writer.WriteString("mimeType", mediaType);
            writer.WriteString("data", Convert.ToBase64String(request.Audio ?? Array.Empty<byte>()));
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WritePropertyName("generationConfig");
            writer.WriteStartObject();
            writer.WriteString("responseMimeType", "application/json");
            writer.WritePropertyName("responseSchema");
            WriteTranscriptionSchema(writer, request.IncludeSegments);
            writer.WriteEndObject();

            writer.WriteEndObject();
        });
    }

    private static void WriteTranscriptionSchema(Utf8JsonWriter writer, bool includeSegments)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "OBJECT");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();

        writer.WritePropertyName("text");
        writer.WriteStartObject();
        writer.WriteString("type", "STRING");
        writer.WriteEndObject();

        writer.WritePropertyName("language");
        writer.WriteStartObject();
        writer.WriteString("type", "STRING");
        writer.WriteEndObject();

        if (includeSegments)
        {
            writer.WritePropertyName("segments");
            writer.WriteStartObject();
            writer.WriteString("type", "ARRAY");
            writer.WritePropertyName("items");
            writer.WriteStartObject();
            writer.WriteString("type", "OBJECT");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();

            writer.WritePropertyName("start");
            writer.WriteStartObject();
            writer.WriteString("type", "NUMBER");
            writer.WriteEndObject();

            writer.WritePropertyName("end");
            writer.WriteStartObject();
            writer.WriteString("type", "NUMBER");
            writer.WriteEndObject();

            writer.WritePropertyName("text");
            writer.WriteStartObject();
            writer.WriteString("type", "STRING");
            writer.WriteEndObject();

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string ResolveMediaType(TranscriptionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.MediaType))
        {
            return request.MediaType!.Trim();
        }

        var fileName = request.FileName ?? string.Empty;
        var extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        switch (extension)
        {
            case ".wav":
                return "audio/wav";
            case ".mp3":
                return "audio/mp3";
            case ".aiff":
            case ".aif":
                return "audio/aiff";
            case ".aac":
                return "audio/aac";
            case ".ogg":
                return "audio/ogg";
            case ".flac":
                return "audio/flac";
            case ".m4a":
                return "audio/mp4";
            case ".webm":
                return "audio/webm";
            default:
                return "audio/mp3";
        }
    }

    private static string? NormalizeAudioMediaType(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var value = format!.Trim();
        return value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            ? value
            : "audio/" + value.ToLower(CultureInfo.InvariantCulture);
    }
}
