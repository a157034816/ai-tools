using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Core.Http;

/// <summary>将服务商错误响应映射为统一的异常类型。</summary>
public static class AiErrorMapper
{
    /// <summary>为指定服务商与状态码创建合适的异常。</summary>
    public static AiServiceException Create(
        string providerName,
        HttpStatusCode statusCode,
        string? responseBody,
        TimeSpan? retryAfter = null)
    {
        var (errorCode, message) = TryExtractError(responseBody);
        var code = (int)statusCode;

        if (code == 401 || code == 403)
        {
            return new AiAuthenticationException(
                message ?? $"Authentication failed ({(int)statusCode} {statusCode}).",
                providerName,
                code,
                errorCode,
                responseBody);
        }

        if (code == 429)
        {
            return new AiRateLimitException(
                message ?? "Rate limit exceeded.",
                providerName,
                errorCode,
                responseBody,
                retryAfter);
        }

        if (code >= 500 && code <= 599)
        {
            return new AiServerException(
                message ?? $"The {providerName} service returned a server error ({(int)statusCode} {statusCode}).",
                providerName,
                code,
                errorCode,
                responseBody);
        }

        return new AiInvalidRequestException(
            message ?? $"The request was rejected by {providerName} ({(int)statusCode} {statusCode}).",
            providerName,
            code,
            errorCode,
            responseBody);
    }

    private static (string? ErrorCode, string? Message) TryExtractError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body!);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            // OpenAI / Anthropic / Gemini 的常见错误结构。
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object)
                {
                    // 各厂商字段语义不同：
                    //   OpenAI    — code 最精确（rate_limit_exceeded），type 是泛类别（requests）。
                    //   Anthropic — type 是错误类别（overloaded_error），没有 code。
                    //   Gemini    — status 是语义化错误码（INVALID_ARGUMENT），code 是数字状态码。
                    // 因此优先取第一个非纯数字的语义化值。
                    var code = FirstSemanticValue(
                        GetString(error, "code"),
                        GetString(error, "status"),
                        GetString(error, "type"));
                    var message = GetString(error, "message");
                    return (code, message);
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return (null, error.GetString());
                }
            }

            if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
            {
                return (GetString(root, "code"), messageElement.GetString());
            }

            return (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string? FirstSemanticValue(params string?[] candidates)
    {
        string? numeric = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            if (!IsAllDigits(candidate!))
            {
                return candidate;
            }

            numeric ??= candidate;
        }

        return numeric;
    }

    private static bool IsAllDigits(string value)
    {
        foreach (var c in value)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    /// <summary>从响应头解析 Retry-After（秒数或 HTTP 日期）。</summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        foreach (var raw in values)
        {
            if (int.TryParse(raw, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (DateTimeOffset.TryParse(raw, out var date))
            {
                var delay = date - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }
        }

        return null;
    }
}

/// <summary>请求发送与重试策略。</summary>
public static class RetryPolicy
{
    /// <summary>
    /// 以指数退避执行请求；仅当发生瞬时故障（429/5xx/网络错误）或服务端明确返回 409 时重试。
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string providerName,
        int maxRetries,
        TimeSpan baseDelay,
        CancellationToken cancellationToken)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (AiRateLimitException ex) when (attempt < maxRetries)
            {
                attempt++;
                var delay = ex.RetryAfter ?? ComputeDelay(attempt, baseDelay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (AiServerException) when (attempt < maxRetries)
            {
                attempt++;
                await Task.Delay(ComputeDelay(attempt, baseDelay), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                attempt++;
                await Task.Delay(ComputeDelay(attempt, baseDelay), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxRetries)
            {
                // 请求超时（非调用方主动取消）也值得重试一次。
                attempt++;
                await Task.Delay(ComputeDelay(attempt, baseDelay), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan ComputeDelay(int attempt, TimeSpan baseDelay)
    {
        var exponential = Math.Pow(2, attempt - 1);
        var milliseconds = baseDelay.TotalMilliseconds * exponential;
        // 抖动避免惊群，范围 0.8x - 1.2x。
        var jitter = 0.8 + (Environment.TickCount % 400) / 1000.0;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds * jitter, 30_000));
    }
}

/// <summary>HttpContent 工厂。</summary>
public static class AiContent
{
    /// <summary>创建保持 JSON 原文的字符串内容（避免二次转义）。</summary>
    public static StringContent Json(string json, Encoding? encoding = null) =>
        new StringContent(json, encoding ?? Encoding.UTF8, "application/json");

    /// <summary>创建多部分表单里的 JSON 字段，带字段名。</summary>
    public static StringContent JsonField(string name, string json) =>
        JsonField(name, json, "application/json");

    /// <summary>创建多部分表单里的文本字段。</summary>
    public static StringContent JsonField(string name, string value, string mediaType)
    {
        var content = new StringContent(value, Encoding.UTF8, mediaType);
        content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
        {
            Name = "\"" + name + "\""
        };
        return content;
    }

    /// <summary>创建 JSON 序列化的内容。</summary>
    public static StringContent JsonSerialized<T>(T value, JsonSerializerOptions options) =>
        Json(JsonSerializer.Serialize(value, options));
}
