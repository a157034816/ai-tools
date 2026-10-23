using System;

namespace AiTools.Abstractions;

/// <summary>一次语音合成请求。</summary>
public sealed class SpeechRequest
{
    /// <summary>语音模型名称。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>要朗读的文本。</summary>
    public string Input { get; init; } = string.Empty;

    /// <summary>音色名称。</summary>
    public string Voice { get; init; } = "alloy";

    /// <summary>输出音频格式，例如 "mp3"、"opus"、"wav"。</summary>
    public string? ResponseFormat { get; init; }

    /// <summary>语速倍率。</summary>
    public double? Speed { get; init; }

    /// <summary>追加到请求体的自定义参数。</summary>
    public System.Text.Json.JsonElement? AdditionalParameters { get; init; }
}

/// <summary>一次语音合成响应。</summary>
public sealed class SpeechResponse
{
    /// <summary>音频字节。</summary>
    public byte[] Audio { get; init; } = Array.Empty<byte>();

    /// <summary>音频 MIME 类型或格式。</summary>
    public string? MediaType { get; init; }
}

/// <summary>一次语音转录请求。</summary>
public sealed class TranscriptionRequest
{
    /// <summary>转录模型名称。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>音频字节。</summary>
    public byte[] Audio { get; init; } = Array.Empty<byte>();

    /// <summary>上传文件名（用于推断格式）。</summary>
    public string FileName { get; init; } = "audio.mp3";

    /// <summary>音频 MIME 类型。</summary>
    public string? MediaType { get; init; }

    /// <summary>提示语言（ISO-639-1），为空则自动检测。</summary>
    public string? Language { get; init; }

    /// <summary>可选的上下文提示词。</summary>
    public string? Prompt { get; init; }

    /// <summary>是否返回带时间戳的分段结果。</summary>
    public bool IncludeSegments { get; init; }

    /// <summary>追加到请求体的自定义参数。</summary>
    public System.Text.Json.JsonElement? AdditionalParameters { get; init; }
}

/// <summary>转录结果中的一个分段。</summary>
public sealed class TranscriptionSegment
{
    public int Id { get; init; }

    public double Start { get; init; }

    public double End { get; init; }

    public string Text { get; init; } = string.Empty;
}

/// <summary>一次语音转录响应。</summary>
public sealed class TranscriptionResponse
{
    /// <summary>完整转录文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>检测到的语言。</summary>
    public string? Language { get; init; }

    /// <summary>音频时长（秒）。</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>分段结果（当请求 IncludeSegments 时）。</summary>
    public System.Collections.Generic.IReadOnlyList<TranscriptionSegment> Segments { get; init; }
        = System.Array.Empty<TranscriptionSegment>();
}

/// <summary>统一的音频客户端接口（语音合成与转录）。</summary>
public interface IAudioClient
{
    /// <summary>服务商标识。</summary>
    string ProviderName { get; }

    /// <summary>将文本合成为语音。</summary>
    Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default);

    /// <summary>将音频转录为文本。</summary>
    Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken = default);
}
