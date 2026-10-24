using System;
using System.Collections.Generic;
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

namespace AiTools.OpenAI;

/// <summary>
/// OpenAI Chat Completions API 及 OpenAI 兼容端点的聊天客户端。
/// </summary>
/// <remarks>
/// <para>
/// 系统提示词会成为开头的 system 消息，工具结果会成为带 <c>tool_call_id</c> 的 <c>tool</c> 消息，
/// 助手工具调用则序列化为 <c>tool_calls</c>。
/// </para>
/// <para>
/// Chat Completions API 不支持通用文件输入。media type 为 <c>image/*</c> 的 <see cref="FilePart"/>
/// 会作为 <c>image_url</c> 部分发送，<c>audio/*</c> 的值会作为 <c>input_audio</c> 部分发送；
/// 其他任何文件 media type 都会引发 <see cref="AiInvalidRequestException"/>。
/// 音频输入仅对支持音频的模型有意义。
/// </para>
/// </remarks>
public sealed class OpenAiChatClient : IChatClient
{
    private static readonly JsonElement EmptyParametersSchema = CreateEmptyParametersSchema();

    private readonly OpenAiOptions _options;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 <see cref="OpenAiChatClient"/> 类的新实例，并从提供的选项解析
    /// <see cref="HttpClient"/>。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    public OpenAiChatClient(OpenAiOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// 使用显式指定的 <see cref="HttpClient"/> 初始化 <see cref="OpenAiChatClient"/> 类的新实例，
    /// 该客户端优先于 <see cref="AiHttpOptions.HttpClient"/>。
    /// 提供的客户端不会由本实例释放。
    /// </summary>
    /// <param name="options">服务商选项。</param>
    /// <param name="httpClient">要使用的 HTTP 客户端；为 null 时由选项（或共享的服务商）提供一个。</param>
    public OpenAiChatClient(OpenAiOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? HttpClientProvider.Resolve(options, ProviderName);
    }

    /// <inheritdoc />
    public string ProviderName => _options.ProviderName;

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var body = BuildRequestBody(request, stream: false);
        using var timeoutSource = OpenAiHelpers.CreateRequestCancellationSource(cancellationToken, _options.Timeout);
        using var response = await _httpClient
            .SendWithRetryAsync(
                () => OpenAiHelpers.CreateRequest(HttpMethod.Post, "chat/completions", _options, AiContent.Json(body)),
                ProviderName,
                _options,
                timeoutSource.Token,
                new AiRequestContext { ProviderName = ProviderName, Operation = "chat", Model = request.Model })
            .ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseResponse(json, request.Model);
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
        using var timeoutSource = OpenAiHelpers.CreateRequestCancellationSource(cancellationToken, _options.Timeout);
        using var response = await _httpClient
            .SendWithRetryAsync(
                () => OpenAiHelpers.CreateRequest(HttpMethod.Post, "chat/completions", _options, AiContent.Json(body)),
                ProviderName,
                _options,
                timeoutSource.Token,
                new AiRequestContext { ProviderName = ProviderName, Operation = "chat", Model = request.Model, IsStreaming = true })
            .ConfigureAwait(false);

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await foreach (var evt in ObservedSseParser.ParseObservedAsync(
            stream,
            _options.Observer,
            new AiRequestContext { ProviderName = ProviderName, Operation = "chat", Model = request.Model, IsStreaming = true },
            timeoutSource.Token).ConfigureAwait(false))
        {
            if (evt.IsDone)
            {
                yield break;
            }

            if (!SseParser.TryParseJson(evt, out var chunk) || chunk.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var chunkId = JsonHelpers.GetString(chunk, "id");
            var chunkModel = JsonHelpers.GetString(chunk, "model");
            var usage = OpenAiHelpers.MapUsage(JsonHelpers.GetObject(chunk, "usage"));

            var sawChoices = false;
            var emittedTerminal = false;
            var choices = JsonHelpers.GetArray(chunk, "choices");
            if (choices.HasValue)
            {
                foreach (var choice in choices.Value)
                {
                    sawChoices = true;
                    var delta = JsonHelpers.GetObject(choice, "delta");
                    var finishReason = MapFinishReason(JsonHelpers.GetString(choice, "finish_reason"));

                    if (!delta.HasValue)
                    {
                        if (finishReason.HasValue || usage != null)
                        {
                            emittedTerminal = true;
                            yield return new ChatStreamUpdate
                            {
                                FinishReason = finishReason,
                                Usage = usage,
                                ResponseId = chunkId,
                                Model = chunkModel
                            };
                        }

                        continue;
                    }

                    var deltaElement = delta.Value;
                    var text = ExtractText(deltaElement, "content");
                    var reasoning = ExtractText(deltaElement, "reasoning_content");
                    if (string.IsNullOrEmpty(reasoning))
                    {
                        reasoning = ExtractText(deltaElement, "reasoning");
                    }

                    var toolDeltas = ExtractToolCallDeltas(deltaElement);

                    if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(reasoning))
                    {
                        // 当同时存在工具增量时，不要在此附加结束原因：工具调用消费方不能错过它，
                        // 因此会在下方单独发出。
                        yield return new ChatStreamUpdate
                        {
                            TextDelta = string.IsNullOrEmpty(text) ? null : text,
                            ReasoningDelta = string.IsNullOrEmpty(reasoning) ? null : reasoning,
                            FinishReason = toolDeltas.Count == 0 ? finishReason : null,
                            Usage = toolDeltas.Count == 0 ? usage : null,
                            ResponseId = chunkId,
                            Model = chunkModel
                        };

                        if (toolDeltas.Count == 0 && (finishReason.HasValue || usage != null))
                        {
                            emittedTerminal = true;
                        }
                    }

                    foreach (var toolDelta in toolDeltas)
                    {
                        yield return new ChatStreamUpdate
                        {
                            ToolCallDelta = toolDelta,
                            ResponseId = chunkId,
                            Model = chunkModel
                        };
                    }

                    if (toolDeltas.Count > 0 && (finishReason.HasValue || usage != null))
                    {
                        emittedTerminal = true;
                        yield return new ChatStreamUpdate
                        {
                            FinishReason = finishReason,
                            Usage = usage,
                            ResponseId = chunkId,
                            Model = chunkModel
                        };
                    }
                    else if (toolDeltas.Count == 0 &&
                        string.IsNullOrEmpty(text) &&
                        string.IsNullOrEmpty(reasoning) &&
                        (finishReason.HasValue || usage != null))
                    {
                        // 仅包含 role 的增量和保活增量会被跳过；终止 Chunk 不会。
                        emittedTerminal = true;
                        yield return new ChatStreamUpdate
                        {
                            FinishReason = finishReason,
                            Usage = usage,
                            ResponseId = chunkId,
                            Model = chunkModel
                        };
                    }
                }
            }

            if (!sawChoices && usage != null)
            {
                // DeepSeek 等服务商会在没有 choices 的最后一个 Chunk 上报告用量。
                yield return new ChatStreamUpdate
                {
                    Usage = usage,
                    ResponseId = chunkId,
                    Model = chunkModel
                };
            }
            else if (sawChoices && !emittedTerminal && usage != null)
            {
                // 存在多个 choice 但没有为它们发出终止事件：仍然要上报用量。
                yield return new ChatStreamUpdate
                {
                    Usage = usage,
                    ResponseId = chunkId,
                    Model = chunkModel
                };
            }
        }
    }

    private string BuildRequestBody(ChatRequest request, bool stream)
    {
        var additional = request.AdditionalParameters;
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = BuildMessages(request),
            ["stream"] = stream
        };

        if (stream && _options.IncludeStreamUsage)
        {
            body["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        }

        if (request.Tools.Count > 0)
        {
            var tools = new List<object>(request.Tools.Count);
            foreach (var tool in request.Tools)
            {
                var function = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name
                };

                if (tool.Description != null)
                {
                    function["description"] = tool.Description;
                }

                if (tool.ParametersSchema.HasValue)
                {
                    function["parameters"] = tool.ParametersSchema.Value;
                }
                else
                {
                    // API 需要 JSON Schema；空对象 Schema 接受任意参数。
                    function["parameters"] = EmptyParametersSchema;
                }

                tools.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = function
                });
            }

            body["tools"] = tools;
        }

        if (request.ToolChoice != null)
        {
            body["tool_choice"] = MapToolChoice(request.ToolChoice);
        }

        if (request.Temperature.HasValue)
        {
            body["temperature"] = request.Temperature.Value;
        }

        if (request.TopP.HasValue)
        {
            body["top_p"] = request.TopP.Value;
        }

        // 较新的 OpenAI 模型使用 max_completion_tokens；调用方可通过 AdditionalParameters 选择启用。
        var hasMaxCompletionTokens = additional.HasValue &&
            additional.Value.ValueKind == JsonValueKind.Object &&
            additional.Value.TryGetProperty("max_completion_tokens", out _);

        if (request.MaxOutputTokens.HasValue && !hasMaxCompletionTokens)
        {
            body["max_tokens"] = request.MaxOutputTokens.Value;
        }

        if (request.StopSequences.Count == 1)
        {
            body["stop"] = request.StopSequences[0];
        }
        else if (request.StopSequences.Count > 1)
        {
            body["stop"] = request.StopSequences;
        }

        if (request.ResponseFormat != null && request.ResponseFormat.Kind != ResponseFormatKind.Text)
        {
            body["response_format"] = MapResponseFormat(request.ResponseFormat);
        }

        return OpenAiHelpers.MergeAdditionalParameters(body, additional);
    }

    private List<object> BuildMessages(ChatRequest request)
    {
        var messages = new List<object>(request.Messages.Count + 1);

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }

        foreach (var message in request.Messages)
        {
            messages.Add(BuildMessage(message));
        }

        return messages;
    }

    private object BuildMessage(ChatMessage message)
    {
        var result = new Dictionary<string, object?>
        {
            ["role"] = MapRole(message.Role)
        };

        if (!string.IsNullOrEmpty(message.Name))
        {
            result["name"] = message.Name;
        }

        if (message.Role == ChatRole.Tool)
        {
            if (!string.IsNullOrEmpty(message.ToolCallId))
            {
                result["tool_call_id"] = message.ToolCallId;
            }

            result["content"] = message.GetText();
            return result;
        }

        var content = BuildContent(message.Content, _options.ProviderName);

        if (message.Role == ChatRole.Assistant)
        {
            // 助手工具调用消息在没有附带文本时，content 使用 null。
            result["content"] = content ?? (message.ToolCalls.Count > 0 ? null : string.Empty);

            if (message.ToolCalls.Count > 0)
            {
                var calls = new List<object>(message.ToolCalls.Count);
                foreach (var call in message.ToolCalls)
                {
                    calls.Add(new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = call.Name,
                            ["arguments"] = string.IsNullOrEmpty(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                        }
                    });
                }

                result["tool_calls"] = calls;
            }
        }
        else
        {
            result["content"] = content ?? string.Empty;
        }

        return result;
    }

    private static object? BuildContent(IReadOnlyList<ContentPart> parts, string providerName)
    {
        var hasNonText = false;
        foreach (var part in parts)
        {
            if (!(part is TextPart))
            {
                hasNonText = true;
                break;
            }
        }

        if (!hasNonText)
        {
            if (parts.Count == 0)
            {
                return null;
            }

            if (parts.Count == 1 && parts[0] is TextPart single)
            {
                return single.Text;
            }

            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (part is TextPart text)
                {
                    builder.Append(text.Text);
                }
            }

            return builder.ToString();
        }

        var content = new List<object>(parts.Count);
        foreach (var part in parts)
        {
            content.Add(BuildContentPart(part, providerName));
        }

        return content;
    }

    private static object BuildContentPart(ContentPart part, string providerName)
    {
        switch (part)
        {
            case TextPart text:
                return new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = text.Text
                };
            case ImagePart image:
                return BuildImagePart(image.Source);
            case FilePart file:
                var mediaType = file.Source.MediaType;
                if (IsMediaType(mediaType, "image"))
                {
                    return BuildImagePart(file.Source);
                }

                if (IsMediaType(mediaType, "audio"))
                {
                    return BuildAudioPart(file.Source, providerName);
                }

                throw new AiInvalidRequestException(
                    "OpenAI chat completions does not support generic file parts. " +
                    "Use an image/* media type (sent as image_url), an audio/* media type (sent as input_audio), " +
                    "or include the file contents as text.",
                    providerName);
            case AudioPart audio:
                return BuildAudioPart(audio.Source, providerName);
            default:
                throw new AiInvalidRequestException(
                    $"Unsupported content part type '{part.GetType().Name}'.",
                    providerName);
        }
    }

    private static object BuildImagePart(BinarySource source)
    {
        var url = source.IsUri
            ? source.Uri!.ToString()
            : source.ToDataUri();

        return new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?>
            {
                ["url"] = url
            }
        };
    }

    private static object BuildAudioPart(BinarySource source, string providerName)
    {
        if (source.Data == null)
        {
            throw new AiInvalidRequestException(
                "OpenAI input_audio content requires inline audio bytes; remote audio URLs are not supported.",
                providerName);
        }

        string format;
        var mediaType = source.MediaType;
        if (string.IsNullOrEmpty(mediaType))
        {
            throw new AiInvalidRequestException(
                "Audio content must specify a media type such as audio/wav or audio/mpeg.",
                providerName);
        }

        var separator = mediaType!.IndexOf('/');
        format = separator >= 0 ? mediaType.Substring(separator + 1) : mediaType;
        var parameters = format.IndexOf(';');
        if (parameters >= 0)
        {
            format = format.Substring(0, parameters);
        }

        format = format.Trim();
        if (format.Length == 0)
        {
            throw new AiInvalidRequestException(
                "Unable to determine the audio format from the media type '" + mediaType + "'.",
                providerName);
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "input_audio",
            ["input_audio"] = new Dictionary<string, object?>
            {
                ["data"] = Convert.ToBase64String(source.Data),
                ["format"] = format
            }
        };
    }

    private static object MapToolChoice(ToolChoice choice)
    {
        switch (choice.Mode)
        {
            case ToolChoiceMode.Auto:
                return "auto";
            case ToolChoiceMode.None:
                return "none";
            case ToolChoiceMode.Required:
                return "required";
            case ToolChoiceMode.Specific:
                return new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = choice.ToolName
                    }
                };
            default:
                return "auto";
        }
    }

    private static object MapResponseFormat(ResponseFormat format)
    {
        if (format.Kind == ResponseFormatKind.JsonObject)
        {
            return new Dictionary<string, object?> { ["type"] = "json_object" };
        }

        var schema = new Dictionary<string, object?>
        {
            ["name"] = format.SchemaName
        };

        if (format.Schema.HasValue)
        {
            schema["schema"] = format.Schema.Value;
        }

        if (format.Strict.HasValue)
        {
            schema["strict"] = format.Strict.Value;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "json_schema",
            ["json_schema"] = schema
        };
    }

    private static string MapRole(ChatRole role)
    {
        switch (role)
        {
            case ChatRole.System:
                return "system";
            case ChatRole.Assistant:
                return "assistant";
            case ChatRole.Tool:
                return "tool";
            default:
                return "user";
        }
    }

    private static bool IsMediaType(string? mediaType, string type)
    {
        return mediaType != null &&
            mediaType.StartsWith(type + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement CreateEmptyParametersSchema()
    {
        using var document = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
        return document.RootElement.Clone();
    }

    private static ChatFinishReason? MapFinishReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return null;
        }

        switch (reason)
        {
            case "stop":
                return ChatFinishReason.Stop;
            case "length":
                return ChatFinishReason.Length;
            case "tool_calls":
            case "function_call":
                return ChatFinishReason.ToolCalls;
            case "content_filter":
                return ChatFinishReason.ContentFilter;
            default:
                return ChatFinishReason.Other;
        }
    }

    private ChatResponse ParseResponse(string json, string requestedModel)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.Clone();

        var id = JsonHelpers.GetString(root, "id");
        var model = JsonHelpers.GetString(root, "model") ?? requestedModel;
        var usage = OpenAiHelpers.MapUsage(JsonHelpers.GetObject(root, "usage"));

        JsonElement? messageElement = null;
        string? finishReason = null;
        var choices = JsonHelpers.GetArray(root, "choices");
        if (choices.HasValue)
        {
            foreach (var choice in choices.Value)
            {
                messageElement = JsonHelpers.GetObject(choice, "message");
                finishReason = JsonHelpers.GetString(choice, "finish_reason");
                break;
            }
        }

        var parts = new List<ContentPart>();
        var toolCalls = new List<ToolCall>();

        if (messageElement.HasValue)
        {
            ParseMessageContent(messageElement.Value, parts);
            ParseToolCalls(messageElement.Value, toolCalls);
        }

        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = parts,
            ToolCalls = toolCalls
        };

        return new ChatResponse
        {
            Id = id,
            Model = model,
            Message = message,
            FinishReason = MapFinishReason(finishReason) ?? ChatFinishReason.None,
            Usage = usage,
            RawJson = root
        };
    }

    private static void ParseMessageContent(JsonElement message, List<ContentPart> parts)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return;
        }

        AppendContent(content, parts);
    }

    private static void AppendContent(JsonElement content, List<ContentPart> parts)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(ContentPart.FromText(text!));
                }

                break;
            case JsonValueKind.Array:
                foreach (var part in content.EnumerateArray())
                {
                    AppendContentPart(part, parts);
                }

                break;
            case JsonValueKind.Object:
                AppendContentPart(content, parts);
                break;
        }
    }

    private static void AppendContentPart(JsonElement part, List<ContentPart> parts)
    {
        if (part.ValueKind != JsonValueKind.Object)
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                var raw = part.GetString();
                if (!string.IsNullOrEmpty(raw))
                {
                    parts.Add(ContentPart.FromText(raw!));
                }
            }

            return;
        }

        var type = JsonHelpers.GetString(part, "type");
        if (type == "text" || type == "output_text")
        {
            var text = JsonHelpers.GetString(part, "text");
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(ContentPart.FromText(text!));
            }

            return;
        }

        if (type == "image_url" || type == "image")
        {
            string? url = null;
            if (part.TryGetProperty("image_url", out var image))
            {
                url = image.ValueKind == JsonValueKind.String
                    ? image.GetString()
                    : JsonHelpers.GetString(image, "url");
            }

            if (string.IsNullOrEmpty(url))
            {
                url = JsonHelpers.GetString(part, "url");
            }

            if (!string.IsNullOrEmpty(url))
            {
                parts.Add(ContentPart.FromImageUri(url!));
            }
        }
    }

    private static void ParseToolCalls(JsonElement message, List<ToolCall> toolCalls)
    {
        var calls = JsonHelpers.GetArray(message, "tool_calls");
        if (calls.HasValue)
        {
            var position = 0;
            foreach (var item in calls.Value)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    position++;
                    continue;
                }

                var function = JsonHelpers.GetObject(item, "function");
                var id = JsonHelpers.GetString(item, "id");
                var name = function.HasValue ? JsonHelpers.GetString(function.Value, "name") : null;
                var arguments = function.HasValue ? ReadArguments(function.Value, "{}") ?? "{}" : "{}";

                toolCalls.Add(new ToolCall(
                    string.IsNullOrEmpty(id) ? "call_" + position : id!,
                    name ?? string.Empty,
                    arguments));

                position++;
            }
        }

        if (toolCalls.Count == 0 &&
            message.TryGetProperty("function_call", out var legacy) &&
            legacy.ValueKind == JsonValueKind.Object)
        {
            toolCalls.Add(new ToolCall(
                "call_0",
                JsonHelpers.GetString(legacy, "name") ?? string.Empty,
                ReadArguments(legacy, "{}") ?? "{}"));
        }
    }

    private static string? ReadArguments(JsonElement function, string? fallback)
    {
        if (!function.TryGetProperty("arguments", out var arguments))
        {
            return fallback;
        }

        var result = arguments.ValueKind switch
        {
            JsonValueKind.String => arguments.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            _ => arguments.GetRawText()
        };

        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    private static string? ExtractText(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Object:
                return JsonHelpers.GetString(value, "text") ?? JsonHelpers.GetString(value, "content");
            case JsonValueKind.Array:
                var builder = new StringBuilder();
                foreach (var part in value.EnumerateArray())
                {
                    var text = part.ValueKind == JsonValueKind.String
                        ? part.GetString()
                        : JsonHelpers.GetString(part, "text") ?? JsonHelpers.GetString(part, "content");
                    if (text != null)
                    {
                        builder.Append(text);
                    }
                }

                return builder.ToString();
            default:
                return null;
        }
    }

    private static List<ToolCallDelta> ExtractToolCallDeltas(JsonElement delta)
    {
        var result = new List<ToolCallDelta>();
        var calls = JsonHelpers.GetArray(delta, "tool_calls");
        if (calls.HasValue)
        {
            var position = 0;
            foreach (var item in calls.Value)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    position++;
                    continue;
                }

                var function = JsonHelpers.GetObject(item, "function");
                result.Add(new ToolCallDelta
                {
                    Index = JsonHelpers.GetInt32(item, "index") ?? position,
                    Id = JsonHelpers.GetString(item, "id"),
                    Name = function.HasValue ? JsonHelpers.GetString(function.Value, "name") : null,
                    ArgumentsDelta = function.HasValue ? ReadArguments(function.Value, null) : null
                });

                position++;
            }
        }

        if (result.Count == 0 &&
            delta.TryGetProperty("function_call", out var legacy) &&
            legacy.ValueKind == JsonValueKind.Object)
        {
            result.Add(new ToolCallDelta
            {
                Index = 0,
                Name = JsonHelpers.GetString(legacy, "name"),
                ArgumentsDelta = ReadArguments(legacy, null)
            });
        }

        return result;
    }
}
