using System;
using AiTools.Core.Http;

namespace AiTools.Gemini;

/// <summary>
/// Google Gemini 服务商的配置选项。
/// </summary>
/// <remarks>
/// 认证使用 <c>x-goog-api-key</c> 请求头。也可以设置 <see cref="AiHttpOptions.AuthorizationHeader"/>
/// 改为发送其他授权头（例如代理接受的 bearer Token）。
/// </remarks>
public sealed class GeminiOptions : AiHttpOptions
{
    /// <summary>默认的 Gemini API 基地址。</summary>
    public static readonly Uri DefaultBaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta", UriKind.Absolute);

    /// <summary>
    /// 初始化 <see cref="GeminiOptions"/> 类的新实例，
    /// 并将 <see cref="AiHttpOptions.BaseAddress"/> 设置为 <see cref="DefaultBaseAddress"/>。
    /// </summary>
    public GeminiOptions()
    {
        BaseAddress = DefaultBaseAddress;
    }

    /// <summary>
    /// 追加到 <see cref="AiHttpOptions.BaseAddress"/>（或替换其中版本段）的 API 版本路径段。
    /// 默认为 <c>v1beta</c>。设置为 <see langword="null"/> 或空字符串则按原样使用基地址。
    /// </summary>
    public string? ApiVersion { get; init; } = "v1beta";

    /// <summary>
    /// 当为 <see langword="true"/> 时，API 密钥会作为 <c>key</c> 查询字符串参数追加，
    /// 而不是通过 <c>x-goog-api-key</c> 请求头发送。这对于只接受查询字符串形式的代理很有用。
    /// 默认为 <see langword="false"/>。
    /// </summary>
    public bool UseQueryStringApiKey { get; init; }
}
