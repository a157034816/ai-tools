using System;
using AiTools.Core.Http;

namespace AiTools.Anthropic;

/// <summary>
/// Anthropic Claude 服务商的配置选项。
/// </summary>
/// <remarks>
/// 默认使用 <c>x-api-key</c> 标头进行身份验证。如果设置了
/// <see cref="AiHttpOptions.AuthorizationHeader"/>，则改为发送该值（例如使用 OAuth bearer Token 时）。
/// </remarks>
public class AnthropicOptions : AiHttpOptions
{
    /// <summary>默认的 Anthropic Messages API 基地址。</summary>
    public const string DefaultBaseAddress = "https://api.anthropic.com/v1";

    /// <summary>
    /// 初始化 <see cref="AnthropicOptions"/> 类的新实例，
    /// 并将 <see cref="AiHttpOptions.BaseAddress"/> 设置为默认的 Anthropic 端点。
    /// </summary>
    public AnthropicOptions()
    {
        BaseAddress = new Uri(DefaultBaseAddress, UriKind.Absolute);
    }

    /// <summary>
    /// <c>anthropic-version</c> 标头的值。默认为 <c>2023-06-01</c>；
    /// 设为 <see langword="null"/> 或空字符串可省略该标头。
    /// </summary>
    public string? AnthropicVersion { get; init; } = "2023-06-01";

    /// <summary>
    /// <c>anthropic-beta</c> 标头的可选值，例如
    /// <c>interleaved-thinking-2025-05-14</c> 或 <c>prompt-caching-2024-07-31</c>。
    /// </summary>
    public string? Beta { get; init; }
}
