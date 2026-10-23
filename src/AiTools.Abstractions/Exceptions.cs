using System;

namespace AiTools.Abstractions;

/// <summary>AiTools 类库的根异常类型。</summary>
public class AiToolsException : Exception
{
    public AiToolsException(string message)
        : base(message)
    {
    }

    public AiToolsException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>服务商 API 调用失败时抛出的异常。</summary>
public class AiServiceException : AiToolsException
{
    public AiServiceException(
        string message,
        string providerName,
        int? statusCode = null,
        string? errorCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderName = providerName ?? string.Empty;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ResponseBody = responseBody;
    }

    /// <summary>服务商标识。</summary>
    public string ProviderName { get; }

    /// <summary>HTTP 状态码；非 HTTP 错误时为 null。</summary>
    public int? StatusCode { get; }

    /// <summary>服务商返回的错误码。</summary>
    public string? ErrorCode { get; }

    /// <summary>服务商原始响应正文，便于排查。</summary>
    public string? ResponseBody { get; }

    /// <summary>该错误是否属于可重试的瞬时故障。</summary>
    public bool IsTransient => StatusCode == 408 || StatusCode == 409 || StatusCode == 429 || (StatusCode >= 500 && StatusCode <= 599);
}

/// <summary>认证失败（401/403）。</summary>
public sealed class AiAuthenticationException : AiServiceException
{
    public AiAuthenticationException(string message, string providerName, int? statusCode = null, string? errorCode = null, string? responseBody = null)
        : base(message, providerName, statusCode, errorCode, responseBody)
    {
    }
}

/// <summary>请求频率超限（429）。</summary>
public sealed class AiRateLimitException : AiServiceException
{
    public AiRateLimitException(string message, string providerName, string? errorCode = null, string? responseBody = null, TimeSpan? retryAfter = null)
        : base(message, providerName, 429, errorCode, responseBody)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>服务端建议的重试等待时长。</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>请求参数不合法（400/404/413/422）。</summary>
public sealed class AiInvalidRequestException : AiServiceException
{
    public AiInvalidRequestException(string message, string providerName, int? statusCode = null, string? errorCode = null, string? responseBody = null)
        : base(message, providerName, statusCode, errorCode, responseBody)
    {
    }
}

/// <summary>服务端错误（5xx）。</summary>
public sealed class AiServerException : AiServiceException
{
    public AiServerException(string message, string providerName, int? statusCode = null, string? errorCode = null, string? responseBody = null)
        : base(message, providerName, statusCode, errorCode, responseBody)
    {
    }
}
