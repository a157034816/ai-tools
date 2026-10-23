# AiTools Provider 开发契约

本文件是各 Provider 实现（OpenAI / Anthropic / Gemini / MCP）必须遵守的共享 API 契约。
实现前请先阅读 `src/AiTools.Abstractions/` 与 `src/AiTools.Core/` 下的源码，本文件只是摘要。

## 项目约束

- 目标框架：`netstandard2.0;net8.0`；`LangVersion=latest`、`Nullable=enable`、`ImplicitUsings=enable`。
- `Directory.Build.props` 会为 netstandard2.0 自动注入 `IsExternalInit`，可直接使用 `init` 访问器。
- netstandard2.0 下可用：`System.Text.Json 8.0.5`、`Microsoft.Bcl.AsyncInterfaces 8.0.0`（已引用）。
- 避免使用仅 net8.0 的 API（如 `HttpClient.Send`、`System.Net.Http.Json`），必要时用 `#if NET8_0_OR_GREATER` 条件编译。
- 公共类型与成员必须有英文 XML 文档注释；注释风格与现有代码保持一致。
- 不要修改 `src/AiTools.Abstractions/`、`src/AiTools.Core/`、`AiTools.sln` 或其他项目；如确需改动共享代码，在最终报告中说明原因而不是直接修改。

## 命名空间与核心类型

### AiTools.Abstractions

```csharp
enum ChatRole { System, User, Assistant, Tool }

abstract class ContentPart
{
    static TextPart  FromText(string text);
    static ImagePart FromImageUri(string uri, string? mediaType = null);
    static ImagePart FromImageBytes(byte[] data, string mediaType);
    static FilePart  FromFileUri(string uri, string? mediaType = null, string? fileName = null);
    static FilePart  FromFileBytes(byte[] data, string mediaType, string? fileName = null);
    static AudioPart FromAudioBytes(byte[] data, string mediaType);
}
sealed class TextPart  : ContentPart { string Text { get; } }
sealed class ImagePart : ContentPart { BinarySource Source { get; } }
sealed class FilePart  : ContentPart { BinarySource Source { get; } }
sealed class AudioPart : ContentPart { BinarySource Source { get; } }

sealed class BinarySource
{
    Uri? Uri; byte[]? Data; string? MediaType; string? FileName; bool IsUri;
    string ToDataUri(); // data:{MediaType};base64,...
}

sealed class ToolCall
{
    ToolCall(string id, string name, string argumentsJson = "{}");
    string Id; string Name; string ArgumentsJson;
    JsonElement? ProviderData { get; init; } // 需原样回传的服务商私有数据
    JsonDocument ParseArguments();
}

sealed class ToolDefinition
{
    string Name; string? Description; JsonElement? ParametersSchema;
    static ToolDefinition Create(string name, string? description = null, JsonElement? parametersSchema = null);
    static ToolDefinition FromJsonSchema(string name, string? description, string schemaJson);
}

sealed class ToolChoice
{
    static ToolChoice Auto / None / Required;
    static ToolChoice Function(string toolName);
    ToolChoiceMode Mode; string? ToolName;
}
enum ToolChoiceMode { Auto, None, Required, Specific }

sealed class ResponseFormat
{
    static ResponseFormat Text / JsonObject;
    static ResponseFormat JsonSchema(string schemaName, JsonElement schema, bool strict = true);
    static ResponseFormat JsonSchema(string schemaName, string schemaJson, bool strict = true);
    ResponseFormatKind Kind; string? SchemaName; JsonElement? Schema; bool? Strict;
}

sealed class ChatMessage
{
    ChatRole Role; IReadOnlyList<ContentPart> Content; string? Name;
    IReadOnlyList<ToolCall> ToolCalls; string? ToolCallId;
    static ChatMessage System/User/Assistant(string text);
    static ChatMessage UserParts(params ContentPart[] parts);
    static ChatMessage AssistantParts(params ContentPart[] parts);
    static ChatMessage AssistantWithToolCalls(IReadOnlyList<ToolCall> toolCalls, string? text = null);
    static ChatMessage ToolResult(string toolCallId, string content);
    string GetText();
}

sealed class ChatRequest
{
    string Model; string? SystemPrompt; IReadOnlyList<ChatMessage> Messages;
    IReadOnlyList<ToolDefinition> Tools; ToolChoice? ToolChoice;
    double? Temperature; double? TopP; int? TopK; int? MaxOutputTokens;
    IReadOnlyList<string> StopSequences; ResponseFormat? ResponseFormat;
    JsonElement? AdditionalParameters; // 浅合并进请求体
    ChatRequest WithMessages(IReadOnlyList<ChatMessage> messages);
}

enum ChatFinishReason { None, Stop, Length, ToolCalls, ContentFilter, Error, Other }

sealed class ChatResponse
{
    string? Id; string Model; ChatMessage Message; ChatFinishReason FinishReason;
    Usage? Usage; JsonElement? RawJson;
    string GetText(); IReadOnlyList<ToolCall> ToolCalls;
}

sealed class ChatStreamUpdate
{
    string? TextDelta; string? ReasoningDelta; ToolCallDelta? ToolCallDelta;
    ChatFinishReason? FinishReason; Usage? Usage; string? ResponseId; string? Model;
}

sealed class ToolCallDelta
{
    int Index; string? Id; string? Name; string? ArgumentsDelta;
}

interface IChatClient
{
    string ProviderName { get; }
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default);
    IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, CancellationToken ct = default);
}

sealed class Usage
{
    int? InputTokens; OutputTokens; TotalTokens;
    CacheReadInputTokens; CacheCreationInputTokens; ReasoningTokens;
}

// 嵌入
sealed class EmbeddingRequest { string Model; IReadOnlyList<string> Inputs; int? Dimensions; string? InputType; JsonElement? AdditionalParameters; }
sealed class Embedding { int Index; float[] Vector; }
sealed class EmbeddingResponse { string Model; IReadOnlyList<Embedding> Data; Usage? Usage; }
interface IEmbeddingClient { string ProviderName; Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default); }

// 图像
enum ImageResponseFormat { Url, Base64 }
sealed class ImageGenerationRequest
{
    string Model; string Prompt; string? Size; string? Quality; string? Style;
    int Count = 1; ImageResponseFormat ResponseFormat = Url;
    string? OutputFormat; string? Background; JsonElement? AdditionalParameters;
}
sealed class GeneratedImage { string? Url; byte[]? Bytes; string? RevisedPrompt; string? MediaType; }
sealed class ImageGenerationResponse { IReadOnlyList<GeneratedImage> Images; }
interface IImageClient { string ProviderName; Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default); }

// 音频
sealed class SpeechRequest { string Model; string Input; string Voice = "alloy"; string? ResponseFormat; double? Speed; JsonElement? AdditionalParameters; }
sealed class SpeechResponse { byte[] Audio; string? MediaType; }
sealed class TranscriptionRequest { string Model; byte[] Audio; string FileName = "audio.mp3"; string? MediaType; string? Language; string? Prompt; bool IncludeSegments; JsonElement? AdditionalParameters; }
sealed class TranscriptionSegment { int Id; double Start; double End; string Text; }
sealed class TranscriptionResponse { string Text; string? Language; double? DurationSeconds; IReadOnlyList<TranscriptionSegment> Segments; }
interface IAudioClient
{
    string ProviderName;
    Task<SpeechResponse> SynthesizeAsync(SpeechRequest request, CancellationToken ct = default);
    Task<TranscriptionResponse> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default);
}

// 异常
AiToolsException : Exception
AiServiceException : AiToolsException { string ProviderName; int? StatusCode; string? ErrorCode; string? ResponseBody; bool IsTransient; }
AiAuthenticationException / AiRateLimitException (TimeSpan? RetryAfter) / AiInvalidRequestException / AiServerException : AiServiceException
```

### AiTools.Core

```csharp
namespace AiTools.Core.Http;

class AiHttpOptions // 可继承
{
    Uri? BaseAddress; string? ApiKey; string? AuthorizationHeader;
    IReadOnlyDictionary<string,string>? DefaultHeaders;
    TimeSpan Timeout = 120s; int MaxRetries = 3; TimeSpan RetryBaseDelay = 500ms;
    HttpClient? HttpClient; // 注入自定义客户端（测试用）
}

static class HttpClientProvider { static HttpClient Resolve(AiHttpOptions options, string providerName); }

static class AiErrorMapper
{
    static AiServiceException Create(string providerName, HttpStatusCode status, string? body, TimeSpan? retryAfter = null);
    static TimeSpan? ParseRetryAfter(HttpResponseMessage response);
}
// 已解析 OpenAI/Anthropic/Gemini 常见错误结构，会自动映射 401/403/429/5xx/4xx。

static class RetryPolicy { static Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>>, string providerName, int maxRetries, TimeSpan baseDelay, CancellationToken ct); }

static class AiContent
{
    static StringContent Json(string json, Encoding? encoding = null);
    static StringContent JsonField(string name, string json);            // multipart 字段
    static StringContent JsonField(string name, string value, string mediaType);
    static StringContent JsonSerialized<T>(T value, JsonSerializerOptions options);
}

static class AiHttpClientExtensions
{
    // 发送请求（ResponseHeadersRead）并在非 2xx 时抛出映射异常
    static Task<HttpResponseMessage> SendAndEnsureAsync(this HttpClient, Func<HttpRequestMessage> factory, string providerName, CancellationToken ct);
    // 带重试
    static Task<HttpResponseMessage> SendWithRetryAsync(this HttpClient, Func<HttpRequestMessage> factory, string providerName, AiHttpOptions options, CancellationToken ct);
}

namespace AiTools.Core.Sse;

sealed class SseEvent { string? EventType; string Data; bool IsDone; }
static class SseParser
{
    static IAsyncEnumerable<SseEvent> ParseAsync(Stream stream, CancellationToken ct = default);
    static bool TryParseJson(SseEvent evt, out JsonElement element); // 自动 Clone
    static IAsyncEnumerable<JsonElement> ParseNdJsonAsync(Stream stream, CancellationToken ct = default);
}

namespace AiTools.Core.Json;

static class JsonHelpers
{
    static JsonSerializerOptions DefaultOptions; // 忽略 null、不转义非 ASCII
    static JsonSerializerOptions CreateOptions();
    static string? GetString(JsonElement, string name);
    static int?    GetInt32(JsonElement, string name);
    static bool?   GetBool(JsonElement, string name);
    static JsonElement? GetObject(JsonElement, string name);
    static JsonElement.ArrayEnumerator? GetArray(JsonElement, string name);
    static string? GetStringOrNull(JsonElement);
}

namespace AiTools.Core.Chat;

sealed class ChatStreamAccumulator
{
    void Add(ChatStreamUpdate update);
    string Text; string Reasoning;
    ChatResponse Build();
}
```

## 通用实现要求

1. 认证头默认由 `ApiKey` 生成，若 `AuthorizationHeader` 非空则直接使用它；`DefaultHeaders` 全部附加。
2. 请求体在序列化前通过 `AdditionalParameters` 做浅合并（JSON 对象，覆盖同名字段）。
3. 非 2xx 响应一律通过 `SendAndEnsureAsync` / `SendWithRetryAsync` 抛出统一异常；不要自行抛 `HttpRequestException`。
4. 流式读取使用 `HttpCompletionOption.ResponseHeadersRead`，解析后 `yield return`；确保响应在流结束/异常时释放。
5. 每个 Provider 至少暴露一个构造函数接受其 Options 与可选的 `HttpClient`；Options 继承 `AiHttpOptions`。
6. 请求/响应映射中，未提供的可选字段不要写进 JSON（用 `DefaultIgnoreCondition.WhenWritingNull` 或条件添加）。
7. 完成前运行 `dotnet build <你的项目.csproj>`，确保 0 error（尽量 0 warning）。
