using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Core.Http;

/// <summary>Provider 共享的 HTTP 选项。各 Provider 通过继承添加自身的专属配置。</summary>
public class AiHttpOptions
{
    /// <summary>API 基础地址，例如 https://api.openai.com/v1。</summary>
    public Uri? BaseAddress { get; init; }

    /// <summary>API 密钥。</summary>
    public string? ApiKey { get; init; }

    /// <summary>自定义的 Authorization 请求头值；为空时按 Provider 约定从 <see cref="ApiKey"/> 生成。</summary>
    public string? AuthorizationHeader { get; init; }

    /// <summary>额外的默认请求头。</summary>
    public IReadOnlyDictionary<string, string>? DefaultHeaders { get; init; }

    /// <summary>单次请求超时（针对流式响应，超时覆盖整个流的读取时间）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>最大重试次数（仅对瞬时故障生效）。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>基础退避时长，实际采用指数退避。</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 自定义 HttpClient。为 null 时由 <see cref="HttpClientProvider"/> 创建并复用。
    /// 注意：若设置了超时，应同时设置 HttpClient.Timeout 为 Infinite 以避免与流式读取冲突。
    /// </summary>
    public HttpClient? HttpClient { get; init; }
}
