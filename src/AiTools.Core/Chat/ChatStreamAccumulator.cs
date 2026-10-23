using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Json;
using AiTools.Core.Sse;

namespace AiTools.Core.Chat;

/// <summary>流式更新的聚合器：把增量片段聚合成一个完整响应。</summary>
public sealed class ChatStreamAccumulator
{
    private readonly System.Text.StringBuilder _text = new();
    private readonly System.Text.StringBuilder _reasoning = new();
    private readonly System.Collections.Generic.SortedDictionary<int, ToolEntry> _tools = new();
    private ChatFinishReason _finishReason = ChatFinishReason.None;
    private Usage? _usage;
    private string? _id;
    private string? _model;

    /// <summary>添加一个增量事件。</summary>
    public void Add(ChatStreamUpdate update)
    {
        if (update == null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        if (update.TextDelta != null)
        {
            _text.Append(update.TextDelta);
        }

        if (update.ReasoningDelta != null)
        {
            _reasoning.Append(update.ReasoningDelta);
        }

        if (update.ToolCallDelta != null)
        {
            AddToolDelta(update.ToolCallDelta);
        }

        if (update.FinishReason.HasValue)
        {
            _finishReason = update.FinishReason.Value;
        }

        if (update.Usage != null)
        {
            _usage = update.Usage;
        }

        if (!string.IsNullOrEmpty(update.ResponseId))
        {
            _id = update.ResponseId;
        }

        if (!string.IsNullOrEmpty(update.Model))
        {
            _model = update.Model;
        }
    }

    /// <summary>获取聚合后的完整文本。</summary>
    public string Text => _text.ToString();

    /// <summary>获取聚合后的推理文本。</summary>
    public string Reasoning => _reasoning.ToString();

    /// <summary>构建聚合后的响应。</summary>
    public ChatResponse Build()
    {
        var parts = new System.Collections.Generic.List<ContentPart>();
        if (_text.Length > 0)
        {
            parts.Add(ContentPart.FromText(_text.ToString()));
        }

        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = parts,
            ToolCalls = BuildToolCalls()
        };

        return new ChatResponse
        {
            Id = _id,
            Model = _model ?? string.Empty,
            Message = message,
            FinishReason = _finishReason,
            Usage = _usage
        };
    }

    private void AddToolDelta(ToolCallDelta delta)
    {
        if (!_tools.TryGetValue(delta.Index, out var entry))
        {
            entry = new ToolEntry();
            _tools[delta.Index] = entry;
        }

        if (!string.IsNullOrEmpty(delta.Id))
        {
            entry.Id = delta.Id;
        }

        if (!string.IsNullOrEmpty(delta.Name))
        {
            entry.Name = delta.Name;
        }

        if (!string.IsNullOrEmpty(delta.ArgumentsDelta))
        {
            entry.Arguments.Append(delta.ArgumentsDelta);
        }
    }

    private System.Collections.Generic.IReadOnlyList<ToolCall> BuildToolCalls()
    {
        if (_tools.Count == 0)
        {
            return Array.Empty<ToolCall>();
        }

        var calls = new System.Collections.Generic.List<ToolCall>(_tools.Count);
        foreach (var pair in _tools)
        {
            var entry = pair.Value;
            calls.Add(new ToolCall(
                entry.Id ?? $"call_{pair.Key}",
                entry.Name ?? string.Empty,
                entry.Arguments.Length == 0 ? "{}" : entry.Arguments.ToString()));
        }

        return calls;
    }

    private sealed class ToolEntry
    {
        public string? Id;
        public string? Name;
        public System.Text.StringBuilder Arguments { get; } = new System.Text.StringBuilder();
    }
}
