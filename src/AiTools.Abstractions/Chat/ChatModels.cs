using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AiTools.Abstractions;

/// <summary>聊天消息角色。</summary>
public enum ChatRole
{
    /// <summary>系统指令。</summary>
    System,
    /// <summary>用户输入。</summary>
    User,
    /// <summary>模型回复。</summary>
    Assistant,
    /// <summary>工具执行结果。</summary>
    Tool
}

/// <summary>聊天内容片段基类，支持文本、图片、文件与音频等多模态输入。</summary>
public abstract class ContentPart
{
    private protected ContentPart()
    {
    }

    /// <summary>创建文本片段。</summary>
    public static TextPart FromText(string text) => new(text);

    /// <summary>创建通过 URL 引用的图片片段。</summary>
    public static ImagePart FromImageUri(string uri, string? mediaType = null) => new(BinarySource.FromUri(uri, mediaType));

    /// <summary>创建内联字节的图片片段。</summary>
    public static ImagePart FromImageBytes(byte[] data, string mediaType) => new(BinarySource.FromBytes(data, mediaType));

    /// <summary>创建通过 URL 引用的文件片段（如 PDF）。</summary>
    public static FilePart FromFileUri(string uri, string? mediaType = null, string? fileName = null) => new(BinarySource.FromUri(uri, mediaType, fileName));

    /// <summary>创建内联字节的文件片段（如 PDF、纯文本）。</summary>
    public static FilePart FromFileBytes(byte[] data, string mediaType, string? fileName = null) => new(BinarySource.FromBytes(data, mediaType, fileName));

    /// <summary>创建内联字节的音频片段。</summary>
    public static AudioPart FromAudioBytes(byte[] data, string mediaType) => new(BinarySource.FromBytes(data, mediaType));
}

/// <summary>文本内容片段。</summary>
public sealed class TextPart : ContentPart
{
    public TextPart(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>文本内容。</summary>
    public string Text { get; }
}

/// <summary>图片内容片段。</summary>
public sealed class ImagePart : ContentPart
{
    internal ImagePart(BinarySource source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>图片来源。</summary>
    public BinarySource Source { get; }
}

/// <summary>文件内容片段（例如 PDF 文档）。</summary>
public sealed class FilePart : ContentPart
{
    internal FilePart(BinarySource source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>文件来源。</summary>
    public BinarySource Source { get; }
}

/// <summary>音频内容片段。</summary>
public sealed class AudioPart : ContentPart
{
    internal AudioPart(BinarySource source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>音频来源。</summary>
    public BinarySource Source { get; }
}

/// <summary>二进制内容来源，可以是远程 URL 或内联字节。</summary>
public sealed class BinarySource
{
    private BinarySource(Uri? uri, byte[]? data, string? mediaType, string? fileName)
    {
        Uri = uri;
        Data = data;
        MediaType = mediaType;
        FileName = fileName;
    }

    /// <summary>远程地址；内联数据时为 null。</summary>
    public Uri? Uri { get; }

    /// <summary>内联字节；URL 引用时为 null。</summary>
    public byte[]? Data { get; }

    /// <summary>MIME 类型，例如 image/png。</summary>
    public string? MediaType { get; }

    /// <summary>可选文件名。</summary>
    public string? FileName { get; }

    /// <summary>是否通过 URL 引用。</summary>
    public bool IsUri => Uri != null;

    /// <summary>通过 URL 创建来源。</summary>
    public static BinarySource FromUri(string uri, string? mediaType = null, string? fileName = null)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }

        return new BinarySource(new Uri(uri, UriKind.RelativeOrAbsolute), null, mediaType, fileName);
    }

    /// <summary>通过内联字节创建来源。</summary>
    public static BinarySource FromBytes(byte[] data, string mediaType, string? fileName = null)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        return new BinarySource(null, data, mediaType, fileName);
    }

    /// <summary>转换为 data URI（仅适用于内联数据）。</summary>
    public string ToDataUri()
    {
        if (Data == null)
        {
            throw new InvalidOperationException("The source does not contain inline data.");
        }

        return $"data:{MediaType ?? "application/octet-stream"};base64,{Convert.ToBase64String(Data)}";
    }
}

/// <summary>模型请求的一次工具调用。</summary>
public sealed class ToolCall
{
    public ToolCall(string id, string name, string argumentsJson = "{}")
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ArgumentsJson = argumentsJson ?? "{}";
    }

    /// <summary>工具调用的唯一标识。</summary>
    public string Id { get; }

    /// <summary>工具名称。</summary>
    public string Name { get; }

    /// <summary>JSON 格式的参数。</summary>
    public string ArgumentsJson { get; }

    /// <summary>
    /// 服务商私有的不透明数据，需在后续请求中原样回传（例如 Gemini 的 thoughtSignature）。
    /// 使用统一抽象时无需关心；Provider 在重建请求时会自动携带。
    /// </summary>
    public JsonElement? ProviderData { get; init; }

    /// <summary>解析参数 JSON。</summary>
    public JsonDocument ParseArguments() => JsonDocument.Parse(ArgumentsJson);
}

/// <summary>可被模型调用的工具定义。</summary>
public sealed class ToolDefinition
{
    /// <summary>工具名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>工具描述，影响模型选择工具的准确度。</summary>
    public string? Description { get; init; }

    /// <summary>JSON Schema 描述的入参结构。</summary>
    public JsonElement? ParametersSchema { get; init; }

    /// <summary>创建工具定义。</summary>
    public static ToolDefinition Create(string name, string? description = null, JsonElement? parametersSchema = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Tool name must not be empty.", nameof(name));
        }

        return new ToolDefinition { Name = name, Description = description, ParametersSchema = parametersSchema };
    }

    /// <summary>从 JSON Schema 字符串创建工具定义。</summary>
    public static ToolDefinition FromJsonSchema(string name, string? description, string schemaJson)
    {
        if (schemaJson == null)
        {
            throw new ArgumentNullException(nameof(schemaJson));
        }

        using var document = JsonDocument.Parse(schemaJson);
        return Create(name, description, document.RootElement.Clone());
    }
}

/// <summary>工具选择策略。</summary>
public enum ToolChoiceMode
{
    /// <summary>由模型自行决定。</summary>
    Auto,
    /// <summary>禁止调用工具。</summary>
    None,
    /// <summary>必须调用至少一个工具。</summary>
    Required,
    /// <summary>强制调用指定工具。</summary>
    Specific
}

/// <summary>指定模型如何使用工具。</summary>
public sealed class ToolChoice
{
    private ToolChoice(ToolChoiceMode mode, string? toolName)
    {
        Mode = mode;
        ToolName = toolName;
    }

    public ToolChoiceMode Mode { get; }

    public string? ToolName { get; }

    public static ToolChoice Auto { get; } = new(ToolChoiceMode.Auto, null);

    public static ToolChoice None { get; } = new(ToolChoiceMode.None, null);

    public static ToolChoice Required { get; } = new(ToolChoiceMode.Required, null);

    /// <summary>强制调用指定名称的工具。</summary>
    public static ToolChoice Function(string toolName)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            throw new ArgumentException("Tool name must not be empty.", nameof(toolName));
        }

        return new ToolChoice(ToolChoiceMode.Specific, toolName);
    }
}

/// <summary>响应输出格式类型。</summary>
public enum ResponseFormatKind
{
    /// <summary>普通文本。</summary>
    Text,
    /// <summary>JSON 对象。</summary>
    JsonObject,
    /// <summary>遵循指定 JSON Schema 的 JSON。</summary>
    JsonSchema
}

/// <summary>控制模型输出格式。</summary>
public sealed class ResponseFormat
{
    private ResponseFormat(ResponseFormatKind kind, string? schemaName, JsonElement? schema, bool? strict)
    {
        Kind = kind;
        SchemaName = schemaName;
        Schema = schema;
        Strict = strict;
    }

    public ResponseFormatKind Kind { get; }

    public string? SchemaName { get; }

    public JsonElement? Schema { get; }

    public bool? Strict { get; }

    public static ResponseFormat Text { get; } = new(ResponseFormatKind.Text, null, null, null);

    public static ResponseFormat JsonObject { get; } = new(ResponseFormatKind.JsonObject, null, null, null);

    /// <summary>要求输出符合 JSON Schema 的对象。</summary>
    public static ResponseFormat JsonSchema(string schemaName, JsonElement schema, bool strict = true)
    {
        if (string.IsNullOrEmpty(schemaName))
        {
            throw new ArgumentException("Schema name must not be empty.", nameof(schemaName));
        }

        return new ResponseFormat(ResponseFormatKind.JsonSchema, schemaName, schema, strict);
    }

    /// <summary>从 JSON Schema 字符串创建。</summary>
    public static ResponseFormat JsonSchema(string schemaName, string schemaJson, bool strict = true)
    {
        using var document = JsonDocument.Parse(schemaJson);
        return JsonSchema(schemaName, document.RootElement.Clone(), strict);
    }
}

/// <summary>聊天消息。</summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }

    /// <summary>多模态内容片段。</summary>
    public IReadOnlyList<ContentPart> Content { get; init; } = Array.Empty<ContentPart>();

    /// <summary>可选的参与者名称。</summary>
    public string? Name { get; init; }

    /// <summary>助手消息中请求的工具调用。</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = Array.Empty<ToolCall>();

    /// <summary>工具消息对应的调用标识。</summary>
    public string? ToolCallId { get; init; }

    public static ChatMessage System(string text) => FromText(ChatRole.System, text);

    public static ChatMessage User(string text) => FromText(ChatRole.User, text);

    public static ChatMessage Assistant(string text) => FromText(ChatRole.Assistant, text);

    public static ChatMessage UserParts(params ContentPart[] parts) => new() { Role = ChatRole.User, Content = parts };

    public static ChatMessage AssistantParts(params ContentPart[] parts) => new() { Role = ChatRole.Assistant, Content = parts };

    /// <summary>创建带工具调用的助手消息。</summary>
    public static ChatMessage AssistantWithToolCalls(IReadOnlyList<ToolCall> toolCalls, string? text = null)
    {
        var content = string.IsNullOrEmpty(text)
            ? Array.Empty<ContentPart>()
            : new ContentPart[] { ContentPart.FromText(text!) };

        return new ChatMessage { Role = ChatRole.Assistant, Content = content, ToolCalls = toolCalls };
    }

    /// <summary>创建工具执行结果消息。</summary>
    public static ChatMessage ToolResult(string toolCallId, string content)
    {
        if (toolCallId == null)
        {
            throw new ArgumentNullException(nameof(toolCallId));
        }

        return new ChatMessage
        {
            Role = ChatRole.Tool,
            ToolCallId = toolCallId,
            Content = new ContentPart[] { ContentPart.FromText(content ?? string.Empty) }
        };
    }

    /// <summary>拼接消息中的所有文本片段。</summary>
    public string GetText()
    {
        var builder = new StringBuilder();
        foreach (var part in Content)
        {
            if (part is TextPart text)
            {
                builder.Append(text.Text);
            }
        }

        return builder.ToString();
    }

    private static ChatMessage FromText(ChatRole role, string text) => new()
    {
        Role = role,
        Content = new ContentPart[] { ContentPart.FromText(text ?? string.Empty) }
    };
}

/// <summary>一次聊天补全请求。</summary>
public sealed class ChatRequest
{
    /// <summary>模型名称（部分服务商使用部署名）。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>系统提示词。与 Messages 中的 System 消息二选一，Provider 会统一处理。</summary>
    public string? SystemPrompt { get; init; }

    public IReadOnlyList<ChatMessage> Messages { get; init; } = Array.Empty<ChatMessage>();

    public IReadOnlyList<ToolDefinition> Tools { get; init; } = Array.Empty<ToolDefinition>();

    public ToolChoice? ToolChoice { get; init; }

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    /// <summary>Top-K 采样（Anthropic / Gemini 支持）。</summary>
    public int? TopK { get; init; }

    public int? MaxOutputTokens { get; init; }

    public IReadOnlyList<string> StopSequences { get; init; } = Array.Empty<string>();

    public ResponseFormat? ResponseFormat { get; init; }

    /// <summary>
    /// 追加到请求体的自定义参数（JSON 对象），用于透传各服务商的特有参数。
    /// Provider 会将其浅合并到最终请求体中。
    /// </summary>
    public JsonElement? AdditionalParameters { get; init; }

    /// <summary>复制请求并替换消息列表，便于实现多轮工具调用。</summary>
    public ChatRequest WithMessages(IReadOnlyList<ChatMessage> messages) => new()
    {
        Model = Model,
        SystemPrompt = SystemPrompt,
        Messages = messages,
        Tools = Tools,
        ToolChoice = ToolChoice,
        Temperature = Temperature,
        TopP = TopP,
        TopK = TopK,
        MaxOutputTokens = MaxOutputTokens,
        StopSequences = StopSequences,
        ResponseFormat = ResponseFormat,
        AdditionalParameters = AdditionalParameters
    };

    /// <summary>创建单轮用户消息的请求。</summary>
    public static ChatRequest FromUser(string model, string text) => new()
    {
        Model = model,
        Messages = new[] { ChatMessage.User(text) }
    };

    /// <summary>创建带系统提示词的单轮用户消息请求。</summary>
    public static ChatRequest FromUser(string model, string systemPrompt, string text) => new()
    {
        Model = model,
        SystemPrompt = systemPrompt,
        Messages = new[] { ChatMessage.User(text) }
    };
}

/// <summary>模型停止生成的原因。</summary>
public enum ChatFinishReason
{
    /// <summary>尚未结束或未知。</summary>
    None,
    /// <summary>正常结束。</summary>
    Stop,
    /// <summary>达到最大 token 数。</summary>
    Length,
    /// <summary>模型请求调用工具。</summary>
    ToolCalls,
    /// <summary>内容被安全策略过滤。</summary>
    ContentFilter,
    /// <summary>服务端错误。</summary>
    Error,
    /// <summary>其他原因。</summary>
    Other
}

/// <summary>一次聊天补全的响应。</summary>
public sealed class ChatResponse
{
    /// <summary>响应标识。</summary>
    public string? Id { get; init; }

    /// <summary>实际使用的模型。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>助手回复消息。</summary>
    public ChatMessage Message { get; init; } = ChatMessage.Assistant(string.Empty);

    public ChatFinishReason FinishReason { get; init; }

    public Usage? Usage { get; init; }

    /// <summary>服务商原始响应 JSON（已 Clone，可安全持有），供高级用法读取。</summary>
    public JsonElement? RawJson { get; init; }

    /// <summary>助手回复的文本内容。</summary>
    public string GetText() => Message.GetText();

    /// <summary>助手请求的工具调用。</summary>
    public IReadOnlyList<ToolCall> ToolCalls => Message.ToolCalls;
}

/// <summary>流式响应中的一个增量事件。</summary>
public sealed class ChatStreamUpdate
{
    /// <summary>本次增量产生的文本。</summary>
    public string? TextDelta { get; init; }

    /// <summary>推理/思考过程的增量文本（如 DeepSeek reasoning、Claude thinking）。</summary>
    public string? ReasoningDelta { get; init; }

    /// <summary>工具调用的增量片段。</summary>
    public ToolCallDelta? ToolCallDelta { get; init; }

    /// <summary>若本次事件携带结束原因，则非空。</summary>
    public ChatFinishReason? FinishReason { get; init; }

    /// <summary>若本次事件携带用量统计，则非空。</summary>
    public Usage? Usage { get; init; }

    /// <summary>响应标识。</summary>
    public string? ResponseId { get; init; }

    /// <summary>实际使用的模型。</summary>
    public string? Model { get; init; }
}

/// <summary>流式工具调用的增量片段，需按 Index 聚合。</summary>
public sealed class ToolCallDelta
{
    /// <summary>同一响应中工具调用的序号。</summary>
    public int Index { get; init; }

    /// <summary>工具调用标识（通常仅在首个片段中出现）。</summary>
    public string? Id { get; init; }

    /// <summary>工具名称（通常仅在首个片段中出现）。</summary>
    public string? Name { get; init; }

    /// <summary>JSON 参数增量。</summary>
    public string? ArgumentsDelta { get; init; }
}

/// <summary>统一的聊天客户端接口。</summary>
public interface IChatClient
{
    /// <summary>服务商标识，例如 "openai"、"anthropic"。</summary>
    string ProviderName { get; }

    /// <summary>发起一次非流式聊天补全。</summary>
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);

    /// <summary>发起一次流式聊天补全。</summary>
    IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
