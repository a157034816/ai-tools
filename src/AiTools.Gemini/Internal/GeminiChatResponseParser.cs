using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using AiTools.Abstractions;

namespace AiTools.Gemini.Internal;

/// <summary>解析 Gemini <c>generateContent</c> 响应及流式 Chunk。</summary>
internal static class GeminiChatResponseParser
{
    internal static ChatResponse ParseComplete(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw GeminiInfrastructure.InvalidJson(body, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            var raw = root.Clone();
            var usage = GeminiInfrastructure.ParseUsage(root);
            var model = GeminiInfrastructure.ReadString(root, "modelVersion") ?? string.Empty;
            var responseId = GeminiInfrastructure.ReadString(root, "responseId");

            var blockReason = GeminiInfrastructure.GetBlockReason(root);
            if (blockReason != null)
            {
                return new ChatResponse
                {
                    Id = responseId,
                    Model = model,
                    Message = ChatMessage.Assistant(string.Empty),
                    FinishReason = ChatFinishReason.ContentFilter,
                    Usage = usage,
                    RawJson = raw
                };
            }

            var candidate = GeminiInfrastructure.GetFirstCandidate(root);
            if (candidate == null)
            {
                return new ChatResponse
                {
                    Id = responseId,
                    Model = model,
                    Message = ChatMessage.Assistant(string.Empty),
                    FinishReason = ChatFinishReason.None,
                    Usage = usage,
                    RawJson = raw
                };
            }

            var text = new StringBuilder();
            var toolCalls = new List<ToolCall>();
            var toolIndex = 0;

            if (GeminiInfrastructure.TryGetProperty(candidate.Value, "content", out var content) &&
                content.ValueKind == JsonValueKind.Object &&
                GeminiInfrastructure.TryGetProperty(content, "parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (GeminiInfrastructure.ReadBool(part, "thought") == true)
                    {
                        // 思考摘要通过 RawJson 暴露，但不包含在回答中。
                        continue;
                    }

                    var partText = GeminiInfrastructure.ReadString(part, "text");
                    if (!string.IsNullOrEmpty(partText))
                    {
                        if (text.Length > 0)
                        {
                            text.Append("\n\n");
                        }

                        text.Append(partText);
                    }

                    if (GeminiInfrastructure.TryGetProperty(part, "functionCall", out var functionCall) &&
                        functionCall.ValueKind == JsonValueKind.Object)
                    {
                        var name = GeminiInfrastructure.ReadString(functionCall, "name") ?? string.Empty;
                        var arguments = NormalizeArguments(functionCall);
                        JsonElement? providerData = null;

                        if (GeminiInfrastructure.TryGetProperty(part, "thoughtSignature", out var signature) &&
                            signature.ValueKind == JsonValueKind.String)
                        {
                            var value = signature.GetString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                providerData = GeminiInfrastructure.CreateThoughtSignatureData(value!);
                            }
                        }

                        toolCalls.Add(new ToolCall($"call_{toolIndex}", name, arguments) { ProviderData = providerData });
                        toolIndex++;
                    }
                }
            }

            var parts2 = new List<ContentPart>();
            if (text.Length > 0)
            {
                parts2.Add(ContentPart.FromText(text.ToString()));
            }

            var finishReason = GeminiInfrastructure.MapFinishReason(
                GeminiInfrastructure.ReadString(candidate.Value, "finishReason"));

            if (toolCalls.Count > 0 &&
                (finishReason == ChatFinishReason.Stop || finishReason == ChatFinishReason.None))
            {
                // Gemini 对包含 function call 的响应通常报告 STOP；此处改为暴露语义正确的
                // 原因，使统一的工具调用循环能够继续执行。
                finishReason = ChatFinishReason.ToolCalls;
            }

            var message = new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = parts2,
                ToolCalls = toolCalls
            };

            return new ChatResponse
            {
                Id = responseId,
                Model = model,
                Message = message,
                FinishReason = finishReason,
                Usage = usage,
                RawJson = raw
            };
        }
    }

    internal static string NormalizeArguments(JsonElement functionCall)
    {
        if (!GeminiInfrastructure.TryGetProperty(functionCall, "args", out var args) ||
            args.ValueKind == JsonValueKind.Null ||
            args.ValueKind == JsonValueKind.Undefined)
        {
            return "{}";
        }

        return args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "{}";
    }

    internal static IEnumerable<ChatStreamUpdate> ParseChunk(JsonElement root, GeminiStreamState state)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                foreach (var update in ParseChunk(item, state))
                {
                    yield return update;
                }
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (state.Blocked)
        {
            // 被拦截的 Prompt 可能不再产生候选结果；忽略后续 Chunk。
            yield break;
        }

        var responseId = GeminiInfrastructure.ReadString(root, "responseId");
        if (responseId != null)
        {
            state.ResponseId = responseId;
        }

        var model = GeminiInfrastructure.ReadString(root, "modelVersion");
        if (model != null)
        {
            state.Model = model;
        }

        var usage = GeminiInfrastructure.ParseUsage(root);
        if (usage != null)
        {
            state.Usage = usage;
        }

        var blockReason = GeminiInfrastructure.GetBlockReason(root);
        if (blockReason != null)
        {
            state.Blocked = true;
            yield return new ChatStreamUpdate
            {
                FinishReason = ChatFinishReason.ContentFilter,
                ResponseId = state.ResponseId,
                Model = state.Model,
                Usage = state.Usage
            };
            yield break;
        }

        var candidate = GeminiInfrastructure.GetFirstCandidate(root);
        if (candidate == null)
        {
            // 仅含用量的 Chunk（例如某些代理的最后一个事件）不携带候选结果。
            if (usage != null)
            {
                yield return new ChatStreamUpdate
                {
                    Usage = usage,
                    ResponseId = state.ResponseId,
                    Model = state.Model
                };
            }

            yield break;
        }

        if (GeminiInfrastructure.TryGetProperty(candidate.Value, "content", out var content) &&
            content.ValueKind == JsonValueKind.Object &&
            GeminiInfrastructure.TryGetProperty(content, "parts", out var parts) &&
            parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var partText = GeminiInfrastructure.ReadString(part, "text");
                var thought = GeminiInfrastructure.ReadBool(part, "thought") == true;

                if (!string.IsNullOrEmpty(partText))
                {
                    yield return new ChatStreamUpdate
                    {
                        TextDelta = thought ? null : partText,
                        ReasoningDelta = thought ? partText : null,
                        ResponseId = state.ResponseId,
                        Model = state.Model
                    };
                }

                if (GeminiInfrastructure.TryGetProperty(part, "functionCall", out var functionCall) &&
                    functionCall.ValueKind == JsonValueKind.Object)
                {
                    state.SawFunctionCall = true;
                    var index = state.NextToolIndex++;
                    yield return new ChatStreamUpdate
                    {
                        ToolCallDelta = new ToolCallDelta
                        {
                            Index = index,
                            Id = $"call_{index}",
                            Name = GeminiInfrastructure.ReadString(functionCall, "name"),
                            ArgumentsDelta = NormalizeArguments(functionCall)
                        },
                        ResponseId = state.ResponseId,
                        Model = state.Model
                    };
                }
            }
        }

        var rawFinish = GeminiInfrastructure.ReadString(candidate.Value, "finishReason");
        var finishReason = GeminiInfrastructure.MapFinishReason(rawFinish);
        if (rawFinish != null)
        {
            if (state.SawFunctionCall &&
                (finishReason == ChatFinishReason.Stop || finishReason == ChatFinishReason.None))
            {
                finishReason = ChatFinishReason.ToolCalls;
            }

            yield return new ChatStreamUpdate
            {
                FinishReason = finishReason,
                ResponseId = state.ResponseId,
                Model = state.Model,
                Usage = state.Usage
            };
        }
        else if (usage != null)
        {
            yield return new ChatStreamUpdate
            {
                Usage = usage,
                ResponseId = state.ResponseId,
                Model = state.Model
            };
        }
    }

    /// <summary>跨流式 Chunk 传递的可变状态。</summary>
    internal sealed class GeminiStreamState
    {
        public int NextToolIndex;

        public bool SawFunctionCall;

        public bool Blocked;

        public string? ResponseId;

        public string? Model;

        public Usage? Usage;
    }
}
