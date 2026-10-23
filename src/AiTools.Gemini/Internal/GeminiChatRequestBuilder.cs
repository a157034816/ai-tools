using System;
using System.Collections.Generic;
using System.Text.Json;
using AiTools.Abstractions;

namespace AiTools.Gemini.Internal;

/// <summary>根据统一聊天模型构建 Gemini <c>generateContent</c> 请求体。</summary>
internal static class GeminiChatRequestBuilder
{
    internal static string Build(ChatRequest request)
    {
        var json = GeminiInfrastructure.WriteJson(writer =>
        {
            writer.WriteStartObject();

            var system = CollectSystemInstruction(request);
            if (system != null)
            {
                writer.WritePropertyName("systemInstruction");
                writer.WriteStartObject();
                writer.WritePropertyName("parts");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("text", system);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WritePropertyName("contents");
            WriteContents(writer, request.Messages);

            if (request.Tools.Count > 0)
            {
                WriteTools(writer, request.Tools);
            }

            if (request.ToolChoice != null && request.Tools.Count > 0)
            {
                WriteToolConfig(writer, request.ToolChoice);
            }

            WriteGenerationConfig(writer, request);
            writer.WriteEndObject();
        });

        return GeminiInfrastructure.MergeTopLevel(json, request.AdditionalParameters);
    }

    private static string? CollectSystemInstruction(ChatRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            parts.Add(request.SystemPrompt!);
        }

        foreach (var message in request.Messages)
        {
            if (message.Role != ChatRole.System)
            {
                continue;
            }

            var text = message.GetText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static void WriteContents(Utf8JsonWriter writer, IReadOnlyList<ChatMessage> messages)
    {
        // Gemini 通过名称而非调用 ID 识别 function 响应。统一模型只在工具结果上
        // 携带 ID，因此在映射消息时记住 id -> name 的对应关系。
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var contents = new List<PendingContent>();

        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case ChatRole.System:
                    continue;

                case ChatRole.User:
                    if (HasContentParts(message.Content))
                    {
                        AppendContent(contents, "user", w => WriteMessageParts(w, message.Content));
                    }

                    break;

                case ChatRole.Assistant:
                    if (HasContentParts(message.Content) || message.ToolCalls.Count > 0)
                    {
                        AppendContent(contents, "model", w =>
                        {
                            WriteMessageParts(w, message.Content);
                            WriteToolCallParts(w, message.ToolCalls);
                        });
                    }

                    foreach (var call in message.ToolCalls)
                    {
                        if (!string.IsNullOrEmpty(call.Id) && !string.IsNullOrEmpty(call.Name))
                        {
                            toolNames[call.Id] = call.Name;
                        }
                    }

                    break;

                case ChatRole.Tool:
                    AppendContent(contents, "user", w => WriteFunctionResponse(w, message, toolNames));
                    break;
            }
        }

        writer.WriteStartArray();
        foreach (var content in contents)
        {
            if (content.Parts.Count == 0)
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("role", content.Role);
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            foreach (var part in content.Parts)
            {
                part(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void AppendContent(List<PendingContent> contents, string role, Action<Utf8JsonWriter> writer)
    {
        if (contents.Count > 0 && string.Equals(contents[contents.Count - 1].Role, role, StringComparison.Ordinal))
        {
            contents[contents.Count - 1].Parts.Add(writer);
            return;
        }

        var content = new PendingContent(role);
        content.Parts.Add(writer);
        contents.Add(content);
    }

    private static void WriteMessageParts(Utf8JsonWriter writer, IReadOnlyList<ContentPart> parts)
    {
        foreach (var part in parts)
        {
            GeminiInfrastructure.TryWriteContentPart(writer, part);
        }
    }

    /// <summary>返回是否至少有一个内容部分能映射为非空的 Gemini 部分。</summary>
    private static bool HasContentParts(IReadOnlyList<ContentPart> parts)
    {
        foreach (var part in parts)
        {
            switch (part)
            {
                case TextPart text when text.Text.Length > 0:
                    return true;
                case ImagePart image when HasBinarySource(image.Source):
                    return true;
                case FilePart file when HasBinarySource(file.Source):
                    return true;
                case AudioPart audio when HasBinarySource(audio.Source):
                    return true;
            }
        }

        return false;
    }

    private static bool HasBinarySource(BinarySource? source)
    {
        return source != null && (source.Data != null || source.Uri != null);
    }

    private static void WriteToolCallParts(Utf8JsonWriter writer, IReadOnlyList<ToolCall> toolCalls)
    {
        foreach (var call in toolCalls)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("functionCall");
            writer.WriteStartObject();
            writer.WriteString("name", call.Name);

            using (var arguments = GeminiInfrastructure.TryParseJson(call.ArgumentsJson))
            {
                writer.WritePropertyName("args");
                if (arguments != null && arguments.RootElement.ValueKind == JsonValueKind.Object)
                {
                    arguments.RootElement.WriteTo(writer);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();

            // Gemini 2.5 要求在下一次请求时将 thought signature 与 functionCall 一同回传。
            if (call.ProviderData is JsonElement providerData &&
                providerData.ValueKind == JsonValueKind.Object &&
                providerData.TryGetProperty("thoughtSignature", out var signature) &&
                signature.ValueKind == JsonValueKind.String)
            {
                writer.WriteString("thoughtSignature", signature.GetString());
            }

            writer.WriteEndObject();
        }
    }

    private static void WriteFunctionResponse(
        Utf8JsonWriter writer,
        ChatMessage message,
        IReadOnlyDictionary<string, string> toolNames)
    {
        var name = ResolveToolName(message, toolNames);
        var text = message.GetText();

        writer.WriteStartObject();
        writer.WritePropertyName("functionResponse");
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WritePropertyName("response");
        writer.WriteStartObject();
        writer.WritePropertyName("result");

        // 工具输出通常是 JSON；转发解析后的 JSON，使结构化数据在往返过程中得以保留。
        using (var parsed = GeminiInfrastructure.TryParseJson(text))
        {
            if (parsed != null && parsed.RootElement.ValueKind != JsonValueKind.Undefined)
            {
                parsed.RootElement.WriteTo(writer);
            }
            else
            {
                writer.WriteStringValue(text ?? string.Empty);
            }
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string ResolveToolName(ChatMessage message, IReadOnlyDictionary<string, string> toolNames)
    {
        if (message.ToolCallId != null && toolNames.TryGetValue(message.ToolCallId, out var name))
        {
            return name;
        }

        return string.IsNullOrEmpty(message.Name) ? "unknown_function" : message.Name!;
    }

    private static void WriteTools(Utf8JsonWriter writer, IReadOnlyList<ToolDefinition> tools)
    {
        writer.WritePropertyName("tools");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WritePropertyName("functionDeclarations");
        writer.WriteStartArray();

        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);

            if (!string.IsNullOrEmpty(tool.Description))
            {
                writer.WriteString("description", tool.Description);
            }

            if (tool.ParametersSchema is JsonElement schema)
            {
                var normalized = GeminiSchemaNormalizer.Normalize(schema);
                if (HasSchemaContent(normalized))
                {
                    writer.WritePropertyName("parameters");
                    normalized.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    private static bool HasSchemaContent(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // 只省略真正为空的 Schema（Gemini 会拒绝 "{}"）；其余内容全部原样传递。
        foreach (var _ in schema.EnumerateObject())
        {
            return true;
        }

        return false;
    }

    private static void WriteToolConfig(Utf8JsonWriter writer, ToolChoice choice)
    {
        string mode;
        string? allowed = null;

        switch (choice.Mode)
        {
            case ToolChoiceMode.None:
                mode = "NONE";
                break;
            case ToolChoiceMode.Required:
                mode = "ANY";
                break;
            case ToolChoiceMode.Specific:
                mode = "ANY";
                allowed = choice.ToolName;
                break;
            default:
                mode = "AUTO";
                break;
        }

        writer.WritePropertyName("toolConfig");
        writer.WriteStartObject();
        writer.WritePropertyName("functionCallingConfig");
        writer.WriteStartObject();
        writer.WriteString("mode", mode);

        if (!string.IsNullOrEmpty(allowed))
        {
            writer.WritePropertyName("allowedFunctionNames");
            writer.WriteStartArray();
            writer.WriteStringValue(allowed);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteGenerationConfig(Utf8JsonWriter writer, ChatRequest request)
    {
        var hasFormat = request.ResponseFormat != null && request.ResponseFormat.Kind != ResponseFormatKind.Text;
        var hasConfig = request.Temperature.HasValue ||
                        request.TopP.HasValue ||
                        request.TopK.HasValue ||
                        request.MaxOutputTokens.HasValue ||
                        request.StopSequences.Count > 0 ||
                        hasFormat;

        if (!hasConfig)
        {
            return;
        }

        writer.WritePropertyName("generationConfig");
        writer.WriteStartObject();

        if (request.Temperature.HasValue)
        {
            writer.WriteNumber("temperature", request.Temperature.Value);
        }

        if (request.TopP.HasValue)
        {
            writer.WriteNumber("topP", request.TopP.Value);
        }

        if (request.TopK.HasValue)
        {
            writer.WriteNumber("topK", request.TopK.Value);
        }

        if (request.MaxOutputTokens.HasValue)
        {
            writer.WriteNumber("maxOutputTokens", request.MaxOutputTokens.Value);
        }

        if (request.StopSequences.Count > 0)
        {
            writer.WritePropertyName("stopSequences");
            writer.WriteStartArray();
            foreach (var stop in request.StopSequences)
            {
                if (stop != null)
                {
                    writer.WriteStringValue(stop);
                }
            }

            writer.WriteEndArray();
        }

        if (request.ResponseFormat != null)
        {
            if (request.ResponseFormat.Kind == ResponseFormatKind.JsonObject)
            {
                writer.WriteString("responseMimeType", "application/json");
            }
            else if (request.ResponseFormat.Kind == ResponseFormatKind.JsonSchema)
            {
                writer.WriteString("responseMimeType", "application/json");
                if (request.ResponseFormat.Schema is JsonElement schema)
                {
                    writer.WritePropertyName("responseSchema");
                    GeminiSchemaNormalizer.Normalize(schema).WriteTo(writer);
                }
            }
        }

        writer.WriteEndObject();
    }

    private sealed class PendingContent
    {
        public PendingContent(string role)
        {
            Role = role;
        }

        public string Role { get; }

        public List<Action<Utf8JsonWriter>> Parts { get; } = new List<Action<Utf8JsonWriter>>();
    }
}
