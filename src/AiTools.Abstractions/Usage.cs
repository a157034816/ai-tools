namespace AiTools.Abstractions;

/// <summary>Token 用量统计。不同服务商填充的字段不同，未提供的字段为 null。</summary>
public sealed class Usage
{
    /// <summary>输入（提示）token 数。</summary>
    public int? InputTokens { get; init; }

    /// <summary>输出（补全）token 数。</summary>
    public int? OutputTokens { get; init; }

    /// <summary>总 token 数（服务商提供时）。</summary>
    public int? TotalTokens { get; init; }

    /// <summary>命中提示缓存的输入 token 数。</summary>
    public int? CacheReadInputTokens { get; init; }

    /// <summary>写入提示缓存的输入 token 数（Anthropic）。</summary>
    public int? CacheCreationInputTokens { get; init; }

    /// <summary>推理过程消耗的 token 数。</summary>
    public int? ReasoningTokens { get; init; }
}
