using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Core.Json;
using AiTools.Core.Sse;

namespace AiTools.Anthropic;

/// <summary>
/// Anthropic Claude Messages API 的聊天客户端。
/// </summary>
/// <remarks>
/// <para>
/// 客户端将服务商无关的 <see cref="ChatRequest"/> 模型映射为 Anthropic 的
/// 用户/助手交替消息格式。角色相同的连续消息会合并为一条消息，
/// 因为 Anthropic 要求角色严格交替。
/// </para>
/// <para>
/// Anthropic 没有原生 JSON 输出模式。请求 <see cref="ResponseFormatKind.JsonSchema"/> 时，
/// 会在系统提示词后追加一段包含该 Schema 的简短指令。需要服务商专属选项
/// （例如 <c>thinking</c>、<c>metadata</c> 或
/// <c>tool_choice.disable_parallel_tool_use</c>）的调用方
/// 可通过 <see cref="ChatRequest.AdditionalParameters"/> 提供，该参数会浅合并到请求体中。
/// </para>
/// </remarks>
public sealed class AnthropicChatClient : IChatClient
{
    private const string Provider = "anthropic";
    private const int DefaultMaxTokens = 4096;

    private static readonly JsonElement EmptyObjectSchema = CreateElement(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonElement ToolUseProviderData = CreateElement(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("type", "tool_use");
        writer.WriteEndObject();
    });

    private readonly AnthropicOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 <see cref="AnthropicChatClient"/> 类的新实例。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <see langword="null"/>。</exception>
    public AnthropicChatClient(AnthropicOptions options)
        : this(options, HttpClientProvider.Resolve(
            options ?? throw new ArgumentNullException(nameof(options)),
            Provider))
    {
    }

    /// <summary>
    /// 使用自定义 HTTP 客户端初始化 <see cref="AnthropicChatClient"/> 类的新实例。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">用于所有请求的 HTTP 客户端。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> 或 <paramref name="httpClient"/> 为 <see langword="null"/>。
    /// </exception>
    public AnthropicChatClient(AnthropicOptions options, HttpClient httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc />
    public string ProviderName => Provider;

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var body = BuildRequestBody(request, stream: false);
        using var response = await SendAsync(body, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        try
        {
            return ParseResponse(json);
        }
        catch (JsonException ex)
        {
            throw new AiServiceException(
                "Anthropic returned a response that could not be parsed as JSON.",
                Provider,
                (int)response.StatusCode,
                errorCode: null,
                responseBody: json,
                innerException: ex);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var body = BuildRequestBody(request, stream: true);
        using var response = await SendAsync(body, cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

        int? inputTokens = null;
        int? cacheReadTokens = null;
        int? cacheCreationTokens = null;

        await foreach (var evt in SseParser.ParseAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (evt.IsDone)
            {
                yield break;
            }

            if (!SseParser.TryParseJson(evt, out var root))
            {
                continue;
            }

            var eventType = evt.EventType;
            if (string.IsNullOrEmpty(eventType))
            {
                eventType = JsonHelpers.GetString(root, "type");
            }

            switch (eventType)
            {
                case "message_start":
                    if (JsonHelpers.GetObject(root, "message") is { } message)
                    {
                        var usage = ParseUsage(message);
                        inputTokens = usage?.InputTokens;
                        cacheReadTokens = usage?.CacheReadInputTokens;
                        cacheCreationTokens = usage?.CacheCreationInputTokens;

                        var startId = JsonHelpers.GetString(message, "id");
                        var startModel = JsonHelpers.GetString(message, "model");
                        if (!string.IsNullOrEmpty(startId) || !string.IsNullOrEmpty(startModel))
                        {
                            yield return new ChatStreamUpdate
                            {
                                ResponseId = startId,
                                Model = startModel
                            };
                        }

                        if (usage != null)
                        {
                            yield return new ChatStreamUpdate { Usage = usage };
                        }
                    }

                    break;

                case "content_block_start":
                    if (JsonHelpers.GetObject(root, "content_block") is { } block &&
                        string.Equals(JsonHelpers.GetString(block, "type"), "tool_use", StringComparison.Ordinal))
                    {
                        yield return new ChatStreamUpdate
                        {
                            ToolCallDelta = new ToolCallDelta
                            {
                                Index = JsonHelpers.GetInt32(root, "index") ?? 0,
                                Id = JsonHelpers.GetString(block, "id"),
                                Name = JsonHelpers.GetString(block, "name"),
                                ArgumentsDelta = null
                            }
                        };
                    }

                    break;

                case "content_block_delta":
                    if (JsonHelpers.GetObject(root, "delta") is { } delta)
                    {
                        var deltaType = JsonHelpers.GetString(delta, "type");
                        if (string.Equals(deltaType, "text_delta", StringComparison.Ordinal))
                        {
                            var text = JsonHelpers.GetString(delta, "text");
                            if (text != null)
                            {
                                yield return new ChatStreamUpdate { TextDelta = text };
                            }
                        }
                        else if (string.Equals(deltaType, "input_json_delta", StringComparison.Ordinal))
                        {
                            var partial = JsonHelpers.GetString(delta, "partial_json");
                            if (partial != null)
                            {
                                yield return new ChatStreamUpdate
                                {
                                    ToolCallDelta = new ToolCallDelta
                                    {
                                        Index = JsonHelpers.GetInt32(root, "index") ?? 0,
                                        ArgumentsDelta = partial
                                    }
                                };
                            }
                        }
                        else if (string.Equals(deltaType, "thinking_delta", StringComparison.Ordinal))
                        {
                            var thinking = JsonHelpers.GetString(delta, "thinking");
                            if (thinking != null)
                            {
                                yield return new ChatStreamUpdate { ReasoningDelta = thinking };
                            }
                        }

                        // "signature_delta" 及未知的 delta 类型会被忽略。
                    }

                    break;

                case "message_delta":
                    ChatFinishReason? finishReason = null;
                    if (JsonHelpers.GetObject(root, "delta") is { } messageDelta)
                    {
                        finishReason = MapStopReason(JsonHelpers.GetString(messageDelta, "stop_reason"));
                    }

                    var deltaUsage = ParseUsage(root);
                    Usage? mergedUsage = null;
                    if (deltaUsage != null || inputTokens.HasValue || cacheReadTokens.HasValue || cacheCreationTokens.HasValue)
                    {
                        // 沿用 message_start 中提示侧（输入侧）的计数，使最终用量事件能同时
                        // 公开输入和输出总量。较新的 API 版本会在 message_delta 中重复给出
                        // 累计输入计数；如果存在则优先采用。
                        mergedUsage = new Usage
                        {
                            InputTokens = deltaUsage?.InputTokens ?? inputTokens,
                            OutputTokens = deltaUsage?.OutputTokens,
                            CacheReadInputTokens = deltaUsage?.CacheReadInputTokens ?? cacheReadTokens,
                            CacheCreationInputTokens = deltaUsage?.CacheCreationInputTokens ?? cacheCreationTokens
                        };
                    }

                    if (finishReason.HasValue || mergedUsage != null)
                    {
                        yield return new ChatStreamUpdate
                        {
                            FinishReason = finishReason,
                            Usage = mergedUsage
                        };
                    }

                    break;

                case "message_stop":
                    yield break;

                case "error":
                    throw CreateStreamError(root, evt.Data);

                case "ping":
                case null:
                    break;
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string body, CancellationToken cancellationToken)
    {
        return await _httpClient
            .SendWithRetryAsync(() => CreateHttpRequest(body), Provider, _options, cancellationToken)
            .ConfigureAwait(false);
    }

    private HttpRequestMessage CreateHttpRequest(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint())
        {
            Content = AiContent.Json(body)
        };

        if (!string.IsNullOrEmpty(_options.AuthorizationHeader))
        {
            request.Headers.TryAddWithoutValidation("Authorization", _options.AuthorizationHeader);
        }
        else if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        }

        if (!string.IsNullOrEmpty(_options.AnthropicVersion))
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", _options.AnthropicVersion);
        }

        if (!string.IsNullOrEmpty(_options.Beta))
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", _options.Beta);
        }

        if (_options.DefaultHeaders != null)
        {
            foreach (var header in _options.DefaultHeaders)
            {
                if (!string.IsNullOrEmpty(header.Key))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return request;
    }

    private Uri BuildEndpoint()
    {
        var baseAddress = _options.BaseAddress ?? _httpClient.BaseAddress;
        if (baseAddress == null)
        {
            throw new InvalidOperationException("An Anthropic base address must be configured.");
        }

        var text = baseAddress.ToString().TrimEnd('/');
        return new Uri(text + "/messages", UriKind.Absolute);
    }

    private static string BuildRequestBody(ChatRequest request, bool stream)
    {
        var system = BuildSystemPrompt(request);
        var messages = BuildMessages(request.Messages);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model);

            // Anthropic 要求每个请求都提供 max_tokens。
            writer.WriteNumber("max_tokens", request.MaxOutputTokens ?? DefaultMaxTokens);

            if (!string.IsNullOrEmpty(system))
            {
                writer.WriteString("system", system);
            }

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", message.Role);
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                foreach (var block in message.Blocks)
                {
                    block.WriteTo(writer);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (request.Tools.Count > 0)
            {
                WriteTools(writer, request.Tools);
                if (request.ToolChoice != null)
                {
                    WriteToolChoice(writer, request.ToolChoice);
                }
            }

            if (request.Temperature.HasValue)
            {
                writer.WriteNumber("temperature", request.Temperature.Value);
            }

            if (request.TopP.HasValue)
            {
                writer.WriteNumber("top_p", request.TopP.Value);
            }

            if (request.TopK.HasValue)
            {
                writer.WriteNumber("top_k", request.TopK.Value);
            }

            if (request.StopSequences.Count > 0)
            {
                writer.WritePropertyName("stop_sequences");
                writer.WriteStartArray();
                foreach (var stop in request.StopSequences)
                {
                    writer.WriteStringValue(stop);
                }

                writer.WriteEndArray();
            }

            if (stream)
            {
                writer.WriteBoolean("stream", true);
            }

            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.ToArray());
        return MergeAdditionalParameters(json, request.AdditionalParameters);
    }

    private static string? BuildSystemPrompt(ChatRequest request)
    {
        string? system = null;

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            system = request.SystemPrompt;
        }
        else
        {
            var systemParts = new List<string>();
            foreach (var message in request.Messages)
            {
                if (message.Role != ChatRole.System)
                {
                    continue;
                }

                var text = message.GetText();
                if (text.Length > 0)
                {
                    systemParts.Add(text);
                }
            }

            if (systemParts.Count > 0)
            {
                system = string.Join("\n\n", systemParts);
            }
        }

        var format = request.ResponseFormat;
        if (format == null)
        {
            return system;
        }

        if (format.Kind != ResponseFormatKind.JsonSchema || !format.Schema.HasValue)
        {
            return system;
        }

        var instruction = "Respond only with a JSON object matching this schema: " + format.Schema.Value.GetRawText();
        return string.IsNullOrEmpty(system) ? instruction : system + "\n\n" + instruction;
    }

    private static List<MessageGroup> BuildMessages(IReadOnlyList<ChatMessage> source)
    {
        var groups = new List<MessageGroup>();

        foreach (var message in source)
        {
            if (message.Role == ChatRole.System)
            {
                continue;
            }

            if (!TryMapMessage(message, out var role, out var blocks) || blocks.Count == 0)
            {
                continue;
            }

            if (groups.Count > 0 && string.Equals(groups[groups.Count - 1].Role, role, StringComparison.Ordinal))
            {
                groups[groups.Count - 1].Blocks.AddRange(blocks);
            }
            else
            {
                groups.Add(new MessageGroup(role, blocks));
            }
        }

        return groups;
    }

    private static bool TryMapMessage(ChatMessage message, out string role, out List<JsonElement> blocks)
    {
        blocks = new List<JsonElement>();

        switch (message.Role)
        {
            case ChatRole.User:
                role = "user";
                AddUserContent(blocks, message.Content);
                return true;

            case ChatRole.Assistant:
                role = "assistant";
                AddAssistantContent(blocks, message);
                return true;

            case ChatRole.Tool:
                role = "user";
                blocks.Add(CreateToolResultBlock(message));
                return true;

            default:
                role = string.Empty;
                return false;
        }
    }

    private static void AddUserContent(List<JsonElement> blocks, IReadOnlyList<ContentPart> content)
    {
        foreach (var part in content)
        {
            switch (part)
            {
                case TextPart text:
                    if (text.Text.Length > 0)
                    {
                        blocks.Add(CreateTextBlock(text.Text));
                    }

                    break;

                case ImagePart image:
                    blocks.Add(CreateImageBlock(image));
                    break;

                case FilePart file:
                    blocks.Add(CreateDocumentBlock(file));
                    break;

                case AudioPart _:
                    throw new AiInvalidRequestException(
                        "Anthropic does not support audio input in the Messages API.",
                        Provider);

                // 未知内容部分类型会被忽略，以保持向前兼容。
            }
        }
    }

    private static void AddAssistantContent(List<JsonElement> blocks, ChatMessage message)
    {
        foreach (var part in message.Content)
        {
            switch (part)
            {
                case TextPart text:
                    if (text.Text.Length > 0)
                    {
                        blocks.Add(CreateTextBlock(text.Text));
                    }

                    break;

                case ImagePart _:
                case FilePart _:
                case AudioPart _:
                    throw new AiInvalidRequestException(
                        "Anthropic assistant messages may only contain text and tool calls.",
                        Provider);
            }
        }

        foreach (var call in message.ToolCalls)
        {
            blocks.Add(CreateToolUseBlock(call));
        }
    }

    private static JsonElement CreateTextBlock(string text) => CreateElement(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
    });

    private static JsonElement CreateImageBlock(ImagePart image)
    {
        var source = image.Source;
        if (source.IsUri && source.Uri != null)
        {
            var url = source.Uri.ToString();
            return CreateElement(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image");
                writer.WritePropertyName("source");
                writer.WriteStartObject();
                writer.WriteString("type", "url");
                writer.WriteString("url", url);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
        }

        if (source.Data == null)
        {
            throw new AiInvalidRequestException(
                "An image part must contain either a URL or inline bytes.",
                Provider);
        }

        var mediaType = string.IsNullOrEmpty(source.MediaType) ? "image/png" : source.MediaType!;
        var data = Convert.ToBase64String(source.Data);
        return CreateElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "image");
            writer.WritePropertyName("source");
            writer.WriteStartObject();
            writer.WriteString("type", "base64");
            writer.WriteString("media_type", mediaType);
            writer.WriteString("data", data);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static JsonElement CreateDocumentBlock(FilePart file)
    {
        var source = file.Source;
        if (!string.Equals(source.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new AiInvalidRequestException(
                "Only files with media type 'application/pdf' can be sent to Anthropic.",
                Provider);
        }

        if (source.IsUri && source.Uri != null)
        {
            var url = source.Uri.ToString();
            return CreateElement(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "document");
                writer.WritePropertyName("source");
                writer.WriteStartObject();
                writer.WriteString("type", "url");
                writer.WriteString("url", url);
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
        }

        if (source.Data == null)
        {
            throw new AiInvalidRequestException(
                "A file part must contain either a URL or inline bytes.",
                Provider);
        }

        var data = Convert.ToBase64String(source.Data);
        return CreateElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "document");
            writer.WritePropertyName("source");
            writer.WriteStartObject();
            writer.WriteString("type", "base64");
            writer.WriteString("media_type", "application/pdf");
            writer.WriteString("data", data);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static JsonElement CreateToolResultBlock(ChatMessage message)
    {
        var toolCallId = message.ToolCallId;
        if (string.IsNullOrEmpty(toolCallId))
        {
            throw new AiInvalidRequestException(
                "Tool result messages must specify the ToolCallId of the originating tool call.",
                Provider);
        }

        var content = message.GetText();
        return CreateElement(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "tool_result");
            writer.WriteString("tool_use_id", toolCallId);
            writer.WriteString("content", content);
            writer.WriteEndObject();
        });
    }

    private static JsonElement CreateToolUseBlock(ToolCall call) => CreateElement(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("type", "tool_use");
        writer.WriteString("id", call.Id);
        writer.WriteString("name", call.Name);
        writer.WritePropertyName("input");
        ParseArgumentsObject(call.ArgumentsJson).WriteTo(writer);
        writer.WriteEndObject();
    });

    private static JsonElement ParseArgumentsObject(string? json)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json!);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // 工具参数格式错误时回退为空对象。
            }
        }

        return EmptyObjectSchema;
    }

    private static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<ToolDefinition> tools)
    {
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            if (!string.IsNullOrEmpty(tool.Description))
            {
                writer.WriteString("description", tool.Description);
            }

            writer.WritePropertyName("input_schema");
            if (tool.ParametersSchema.HasValue)
            {
                tool.ParametersSchema.Value.WriteTo(writer);
            }
            else
            {
                EmptyObjectSchema.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteToolChoice(Utf8JsonWriter writer, ToolChoice choice)
    {
        writer.WritePropertyName("tool_choice");
        writer.WriteStartObject();
        switch (choice.Mode)
        {
            case ToolChoiceMode.None:
                writer.WriteString("type", "none");
                break;

            case ToolChoiceMode.Required:
                writer.WriteString("type", "any");
                break;

            case ToolChoiceMode.Specific:
                writer.WriteString("type", "tool");
                writer.WriteString("name", choice.ToolName ?? string.Empty);
                break;

            default:
                writer.WriteString("type", "auto");
                break;
        }

        writer.WriteEndObject();
    }

    private static string MergeAdditionalParameters(string json, JsonElement? additional)
    {
        if (additional == null || additional.Value.ValueKind != JsonValueKind.Object)
        {
            return json;
        }

        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (additional.Value.TryGetProperty(property.Name, out _))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            foreach (var property in additional.Value.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static ChatResponse ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var textBlocks = new List<string>();
        var toolCalls = new List<ToolCall>();

        if (JsonHelpers.GetArray(root, "content") is { } content)
        {
            foreach (var block in content)
            {
                if (block.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = JsonHelpers.GetString(block, "type");
                if (string.Equals(type, "text", StringComparison.Ordinal))
                {
                    var text = JsonHelpers.GetString(block, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        textBlocks.Add(text!);
                    }
                }
                else if (string.Equals(type, "tool_use", StringComparison.Ordinal))
                {
                    var arguments = "{}";
                    if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                    {
                        arguments = input.GetRawText();
                    }

                    toolCalls.Add(new ToolCall(
                        JsonHelpers.GetString(block, "id") ?? string.Empty,
                        JsonHelpers.GetString(block, "name") ?? string.Empty,
                        arguments)
                    {
                        ProviderData = ToolUseProviderData.Clone()
                    });
                }

                // "thinking"、"redacted_thinking" 及未知块仅保留在 RawJson 中。
            }
        }

        var parts = new List<ContentPart>();
        if (textBlocks.Count > 0)
        {
            parts.Add(ContentPart.FromText(string.Join("\n\n", textBlocks)));
        }

        var stopReason = JsonHelpers.GetString(root, "stop_reason");

        return new ChatResponse
        {
            Id = JsonHelpers.GetString(root, "id"),
            Model = JsonHelpers.GetString(root, "model") ?? string.Empty,
            Message = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = parts,
                ToolCalls = toolCalls
            },
            FinishReason = MapStopReason(stopReason) ?? ChatFinishReason.Other,
            Usage = ParseUsage(root),
            RawJson = root.Clone()
        };
    }

    private static ChatFinishReason? MapStopReason(string? stopReason)
    {
        switch (stopReason)
        {
            case "end_turn":
            case "stop_sequence":
                return ChatFinishReason.Stop;

            case "max_tokens":
                return ChatFinishReason.Length;

            case "tool_use":
                return ChatFinishReason.ToolCalls;

            case "refusal":
                return ChatFinishReason.ContentFilter;

            case null:
            case "":
                return null;

            default:
                return ChatFinishReason.Other;
        }
    }

    private static Usage? ParseUsage(JsonElement parent)
    {
        if (!parent.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Usage
        {
            InputTokens = JsonHelpers.GetInt32(usage, "input_tokens"),
            OutputTokens = JsonHelpers.GetInt32(usage, "output_tokens"),
            CacheReadInputTokens = JsonHelpers.GetInt32(usage, "cache_read_input_tokens"),
            CacheCreationInputTokens = JsonHelpers.GetInt32(usage, "cache_creation_input_tokens")
        };

        if (result.InputTokens == null &&
            result.OutputTokens == null &&
            result.CacheReadInputTokens == null &&
            result.CacheCreationInputTokens == null)
        {
            return null;
        }

        return result;
    }

    private static AiServiceException CreateStreamError(JsonElement root, string rawData)
    {
        string? errorCode = null;
        string? message = null;

        if (JsonHelpers.GetObject(root, "error") is { } error)
        {
            errorCode = JsonHelpers.GetString(error, "type");
            message = JsonHelpers.GetString(error, "message");
        }

        return new AiServiceException(
            message ?? "Anthropic returned an error event.",
            Provider,
            statusCode: null,
            errorCode: errorCode,
            responseBody: rawData);
    }

    private static JsonElement CreateElement(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            write(writer);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private sealed class MessageGroup
    {
        public MessageGroup(string role, List<JsonElement> blocks)
        {
            Role = role;
            Blocks = blocks;
        }

        public string Role { get; }

        public List<JsonElement> Blocks { get; }
    }
}
