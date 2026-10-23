using System;
using System.Collections.Generic;

namespace AiTools.Abstractions;

/// <summary>一次嵌入请求。</summary>
public sealed class EmbeddingRequest
{
    /// <summary>嵌入模型名称。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>待嵌入的文本列表。</summary>
    public IReadOnlyList<string> Inputs { get; init; } = Array.Empty<string>();

    /// <summary>可选的输出维度（部分模型支持降维）。</summary>
    public int? Dimensions { get; init; }

    /// <summary>可选的输入类型提示，例如 "query" 或 "document"（部分服务商支持）。</summary>
    public string? InputType { get; init; }

    /// <summary>追加到请求体的自定义参数。</summary>
    public System.Text.Json.JsonElement? AdditionalParameters { get; init; }
}

/// <summary>单条嵌入结果。</summary>
public sealed class Embedding
{
    /// <summary>在输入列表中的序号。</summary>
    public int Index { get; init; }

    /// <summary>向量。</summary>
    public float[] Vector { get; init; } = Array.Empty<float>();
}

/// <summary>一次嵌入响应。</summary>
public sealed class EmbeddingResponse
{
    public string Model { get; init; } = string.Empty;

    public IReadOnlyList<Embedding> Data { get; init; } = Array.Empty<Embedding>();

    public Usage? Usage { get; init; }
}

/// <summary>统一的嵌入客户端接口。</summary>
public interface IEmbeddingClient
{
    /// <summary>服务商标识。</summary>
    string ProviderName { get; }

    /// <summary>生成文本嵌入向量。</summary>
    Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default);
}
