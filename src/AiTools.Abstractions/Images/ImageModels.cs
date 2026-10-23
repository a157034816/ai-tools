using System;
using System.Collections.Generic;

namespace AiTools.Abstractions;

/// <summary>期望的图像返回形式。</summary>
public enum ImageResponseFormat
{
    /// <summary>返回可访问的 URL。</summary>
    Url,
    /// <summary>返回 base64 编码的字节。</summary>
    Base64
}

/// <summary>一次图像生成请求。</summary>
public sealed class ImageGenerationRequest
{
    /// <summary>图像模型名称。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>提示词。</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>尺寸，例如 "1024x1024"。</summary>
    public string? Size { get; init; }

    /// <summary>质量，例如 "standard"、"hd"、"high"。</summary>
    public string? Quality { get; init; }

    /// <summary>风格，例如 "vivid"、"natural"。</summary>
    public string? Style { get; init; }

    /// <summary>生成数量。</summary>
    public int Count { get; init; } = 1;

    /// <summary>返回形式。</summary>
    public ImageResponseFormat ResponseFormat { get; init; } = ImageResponseFormat.Url;

    /// <summary>输出格式，例如 "png"、"jpeg"、"webp"。</summary>
    public string? OutputFormat { get; init; }

    /// <summary>背景，例如 "transparent"、"opaque"。</summary>
    public string? Background { get; init; }

    /// <summary>追加到请求体的自定义参数。</summary>
    public System.Text.Json.JsonElement? AdditionalParameters { get; init; }
}

/// <summary>一张生成完成的图像。</summary>
public sealed class GeneratedImage
{
    /// <summary>图像 URL（当请求 URL 形式时）。</summary>
    public string? Url { get; init; }

    /// <summary>图像字节（当请求 Base64 形式时）。</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>模型改写后的提示词（部分服务商返回）。</summary>
    public string? RevisedPrompt { get; init; }

    /// <summary>MIME 类型。</summary>
    public string? MediaType { get; init; }
}

/// <summary>一次图像生成响应。</summary>
public sealed class ImageGenerationResponse
{
    public IReadOnlyList<GeneratedImage> Images { get; init; } = Array.Empty<GeneratedImage>();
}

/// <summary>统一的图像生成客户端接口。</summary>
public interface IImageClient
{
    /// <summary>服务商标识。</summary>
    string ProviderName { get; }

    /// <summary>根据提示词生成图像。</summary>
    Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default);
}
