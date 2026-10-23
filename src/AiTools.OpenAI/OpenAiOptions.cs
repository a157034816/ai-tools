using System;
using AiTools.Core.Http;

namespace AiTools.OpenAI;

/// <summary>
/// OpenAI 服务商的配置。同一选项类型也用于 OpenAI 兼容端点
/// （DeepSeek、Moonshot/Kimi、Qwen、Ollama、vLLM、OpenRouter、Groq、Together 等）。
/// </summary>
public class OpenAiOptions : AiHttpOptions
{
    /// <summary>默认的 OpenAI API 基地址。</summary>
    public const string DefaultBaseAddress = "https://api.openai.com/v1";

    /// <summary>
    /// 使用默认的 OpenAI 基地址初始化 <see cref="OpenAiOptions"/> 类的新实例。
    /// 若要指向其他端点，请为 <see cref="AiHttpOptions.BaseAddress"/> 赋值。
    /// </summary>
    public OpenAiOptions()
    {
        BaseAddress = new Uri(DefaultBaseAddress, UriKind.Absolute);
    }

    /// <summary>可选的 OpenAI 组织标识符，通过 <c>OpenAI-Organization</c> 标头发送。</summary>
    public string? Organization { get; init; }

    /// <summary>可选的 OpenAI 项目标识符，通过 <c>OpenAI-Project</c> 标头发送。</summary>
    public string? Project { get; init; }

    /// <summary>可选的终端用户标识符，通过 <c>OpenAI-User</c> 标头发送。</summary>
    public string? User { get; init; }

    /// <summary>
    /// 生成的客户端所公开的服务商标识符。默认为 <c>openai</c>；当目标为兼容端点时，
    /// 请将其设置为更具描述性的值（例如 <c>deepseek</c>）。
    /// </summary>
    public string ProviderName { get; init; } = "openai";

    /// <summary>
    /// 流式聊天请求是否应包含 <c>stream_options.include_usage</c>，以便服务在最后一个 Chunk 中
    /// 报告 Token 用量。默认为 <see langword="true"/>。对于拒绝该参数的兼容服务商，
    /// 请禁用此项。
    /// </summary>
    public bool IncludeStreamUsage { get; init; } = true;

    /// <summary>
    /// 为 OpenAI 兼容端点创建选项。
    /// </summary>
    /// <param name="baseUrl">
    /// 绝对的 API 基 URL，包含任何版本段，例如
    /// <c>https://api.deepseek.com/v1</c> 或 <c>http://localhost:11434/v1</c>。末尾斜杠会被忽略。
    /// </param>
    /// <param name="apiKey">
    /// 用于构建 <c>Authorization: Bearer</c> 标头的 API 密钥。对于不需要身份验证的本地服务，
    /// 请传入空字符串。
    /// </param>
    /// <param name="providerName">
    /// 可选的、由客户端反映的服务商标识符。默认为 <c>openai</c>。
    /// </param>
    /// <returns>已配置的选项实例。</returns>
    /// <exception cref="ArgumentException">当 <paramref name="baseUrl"/> 不是绝对的 HTTP(S) URL 时抛出。</exception>
    public static OpenAiOptions ForCompatibleProvider(string baseUrl, string apiKey, string? providerName = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("A base URL is required.", nameof(baseUrl));
        }

        var normalized = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "The base URL must be an absolute HTTP or HTTPS URL, for example https://api.deepseek.com/v1.",
                nameof(baseUrl));
        }

        return new OpenAiOptions
        {
            BaseAddress = uri,
            ApiKey = apiKey,
            ProviderName = providerName ?? "openai"
        };
    }
}
