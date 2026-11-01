using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Sessions;

namespace AiTools.Core.Chat;

/// <summary>上下文压缩配置。</summary>
/// <remarks>
/// 默认策略：当请求的估算 token 达到模型上下文窗口的 90% 时自动压缩，
/// 压缩到窗口的 50%，并至少保留最近 2 个用户轮次不被折叠。
/// </remarks>
public sealed class CompactionOptions
{
    /// <summary>获取或设置是否启用自动压缩。默认 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>获取或设置触发阈值（占上下文窗口的比例）。默认 0.9。</summary>
    public double Threshold { get; set; } = 0.9;

    /// <summary>获取或设置压缩后的目标占用比例。默认 0.5。</summary>
    public double Target { get; set; } = 0.5;

    /// <summary>获取或设置至少保留的最近用户轮次数。默认 2。</summary>
    public int MinRecentTurns { get; set; } = 2;

    /// <summary>获取或设置摘要请求的最大输出 token。默认 2048。</summary>
    public int SummaryMaxTokens { get; set; } = 2048;

    /// <summary>获取或设置上下文窗口覆盖值；null 表示按模型 id 推断。</summary>
    public int? ContextWindow { get; set; }
}

/// <summary>
/// 按模型 id 推断上下文窗口大小。表按前缀匹配，最长前缀优先；
/// 未命中时回退到 <see cref="DefaultWindow"/>。
/// </summary>
/// <remarks>
/// 这里是保守估计：宁可提前压缩，也不要在服务商拒绝请求后才发现超限。
/// 用户可以在 <c>settings.json</c> 的模型条目上写 <c>contextWindow</c> 覆盖。
/// </remarks>
public static class ContextWindows
{
    /// <summary>未识别模型使用的默认窗口。128k 覆盖了绝大多数现代模型。</summary>
    public const int DefaultWindow = 128_000;

    private static readonly (string Prefix, int Window)[] Known =
    {
        ("claude", 200_000),
        ("gpt-4.1", 1_047_576),
        ("gpt-4o", 128_000),
        ("gpt-4-turbo", 128_000),
        ("gpt-5", 400_000),
        ("o1", 200_000),
        ("o3", 200_000),
        ("o4", 200_000),
        ("gemini-2.5", 1_048_576),
        ("gemini-2.0", 1_048_576),
        ("gemini-1.5", 1_048_576),
        ("gemini", 1_048_576),
        ("deepseek", 64_000),
        ("kimi", 128_000),
        ("moonshot", 128_000),
        ("qwen-max", 32_768),
        ("qwen-plus", 131_072),
        ("qwen-turbo", 1_000_000),
        ("qwen", 131_072),
        ("llama", 128_000),
        ("mistral", 128_000)
    };

    /// <summary>解析模型可用的上下文窗口。</summary>
    /// <param name="modelId">模型标识，可带 <c>provider/</c> 或 <c>models/</c> 前缀。</param>
    /// <param name="overrideWindow">显式覆盖值；大于 0 时优先返回。</param>
    public static int Resolve(string? modelId, int? overrideWindow = null)
    {
        if (overrideWindow.HasValue && overrideWindow.Value > 0)
        {
            return overrideWindow.Value;
        }

        var normalized = NormalizeModelId(modelId);
        if (normalized.Length == 0)
        {
            return DefaultWindow;
        }

        var bestLength = 0;
        var window = DefaultWindow;
        foreach (var entry in Known)
        {
            if (entry.Prefix.Length > bestLength &&
                normalized.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                bestLength = entry.Prefix.Length;
                window = entry.Window;
            }
        }

        return window;
    }

    private static string NormalizeModelId(string? modelId)
    {
        var value = (modelId ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        // "anthropic/claude-sonnet-4-5" → "claude-sonnet-4-5"
        var slash = value.LastIndexOf('/');
        if (slash >= 0 && slash < value.Length - 1)
        {
            value = value.Substring(slash + 1);
        }

        // Gemini 的资源名形如 "models/gemini-2.5-pro"，上面已去掉前缀；
        // 兼容直接以 "models/" 开头但没有其它分隔的情况。
        if (value.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring("models/".Length);
        }

        return value;
    }
}

/// <summary>
/// 粗略的 token 估算。不依赖服务商分词器，用于判断"是否接近上限"，
/// 精度足以支撑阈值触发，且对多语言保持一致。
/// </summary>
/// <remarks>
/// 经验值：英文约 4 字符 / token，中日韩等非 ASCII 字符约 1.4 字符 / token。
/// 估算略偏高，宁可提前压缩，也不要让服务商返回 <c>context_length_exceeded</c>。
/// </remarks>
public static class TokenEstimator
{
    private const double AsciiCharsPerToken = 4.0;
    private const double WideCharsPerToken = 1.4;
    private const int PerMessageOverhead = 4;
    private const int PerImageTokens = 1_000;
    private const int PerFileTokens = 2_000;

    /// <summary>估算一段文本的 token 数。</summary>
    /// <param name="text">要估算的文本。</param>
    public static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        double ascii = 0;
        double wide = 0;
        foreach (var c in text!)
        {
            if (c < 128)
            {
                ascii++;
            }
            else
            {
                wide++;
            }
        }

        return (int)Math.Ceiling(ascii / AsciiCharsPerToken + wide / WideCharsPerToken);
    }

    /// <summary>估算一条消息的 token 数（含多模态片段的固定开销）。</summary>
    /// <param name="message">要估算的消息。</param>
    public static int EstimateMessage(ChatMessage? message)
    {
        if (message == null)
        {
            return 0;
        }

        var total = PerMessageOverhead;
        foreach (var part in message.Content)
        {
            switch (part)
            {
                case TextPart text:
                    total += EstimateText(text.Text);
                    break;
                case ImagePart:
                    total += PerImageTokens;
                    break;
                case FilePart:
                    total += PerFileTokens;
                    break;
                case AudioPart:
                    total += PerFileTokens;
                    break;
            }
        }

        foreach (var call in message.ToolCalls)
        {
            total += PerMessageOverhead + EstimateText(call.Name) + EstimateText(call.ArgumentsJson);
        }

        return total;
    }

    /// <summary>估算一段消息序列的 token 数。</summary>
    /// <param name="messages">要估算的消息。</param>
    public static int EstimateMessages(IEnumerable<ChatMessage>? messages)
    {
        if (messages == null)
        {
            return 0;
        }

        var total = 0;
        foreach (var message in messages)
        {
            total += EstimateMessage(message);
        }

        return total;
    }

    /// <summary>估算区间 <c>[start, end)</c> 内消息的 token 数。</summary>
    /// <param name="messages">消息列表。</param>
    /// <param name="start">起始下标（含）。</param>
    /// <param name="end">结束下标（不含）。</param>
    public static int EstimateRange(IReadOnlyList<ChatMessage>? messages, int start, int end)
    {
        if (messages == null || messages.Count == 0)
        {
            return 0;
        }

        var from = Math.Max(0, start);
        var to = Math.Min(messages.Count, end);
        var total = 0;
        for (var index = from; index < to; index++)
        {
            total += EstimateMessage(messages[index]);
        }

        return total;
    }

    /// <summary>估算工具定义（名称、描述、参数 Schema）的 token 数。</summary>
    /// <param name="tools">工具定义。</param>
    public static int EstimateTools(IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools == null || tools.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var tool in tools)
        {
            total += PerMessageOverhead;
            total += EstimateText(tool.Name);
            total += EstimateText(tool.Description);
            if (tool.ParametersSchema.HasValue)
            {
                total += EstimateText(tool.ParametersSchema.Value.GetRawText());
            }
        }

        return total;
    }
}

/// <summary>把已压缩的对话折叠成模型实际看到的视图。</summary>
public static class CompactionView
{
    /// <summary>摘要消息的前缀，用于让模型知道自己看到的是压缩后的历史。</summary>
    public const string SummaryPreamble = "以下是对更早对话的摘要（原文已压缩，不再提供）：\n\n";

    /// <summary>
    /// 按压缩状态折叠消息：用一条用户消息承载摘要，取代前 <c>CoveredMessageCount</c> 条。
    /// 覆盖数超出消息总数（历史被替换或缩短）时视为无效，原样返回。
    /// </summary>
    /// <param name="messages">完整历史（最旧在前）。</param>
    /// <param name="compaction">压缩状态；null 或已失效时原样返回。</param>
    public static IReadOnlyList<ChatMessage> Apply(
        IReadOnlyList<ChatMessage> messages,
        ConversationCompaction? compaction)
    {
        if (messages == null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (compaction == null || string.IsNullOrWhiteSpace(compaction.Summary))
        {
            return messages;
        }

        var covered = compaction.CoveredMessageCount;
        if (covered <= 0 || covered >= messages.Count)
        {
            // 覆盖全部消息等于没有上下文可留；视为无效状态。
            return messages;
        }

        var result = new List<ChatMessage>(messages.Count - covered + 1)
        {
            ChatMessage.User(SummaryPreamble + compaction.Summary!.Trim())
        };

        for (var index = covered; index < messages.Count; index++)
        {
            result.Add(messages[index]);
        }

        return result;
    }

    /// <summary>把待压缩区间拼成给摘要模型的纯文本转录。</summary>
    /// <param name="messages">完整历史。</param>
    /// <param name="covered">已被旧摘要覆盖的消息数。</param>
    /// <param name="split">本次压缩的切分点（不含）。</param>
    /// <param name="existingSummary">旧摘要；没有时为 null。</param>
    public static string BuildSource(
        IReadOnlyList<ChatMessage> messages,
        int covered,
        int split,
        string? existingSummary)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            builder.AppendLine("【此前生成的摘要】");
            builder.AppendLine(existingSummary!.Trim());
            builder.AppendLine();
        }

        var from = Math.Max(0, covered);
        var to = Math.Min(messages.Count, split);
        for (var index = from; index < to; index++)
        {
            var message = messages[index];
            var text = message.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.Append('[')
                .Append(message.Role switch
                {
                    ChatRole.User => "用户",
                    ChatRole.Assistant => "助手",
                    ChatRole.Tool => "工具",
                    _ => "系统"
                })
                .Append("] ")
                .AppendLine(text.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>一次压缩的规划结果（纯算法，不涉及模型调用）。</summary>
public sealed class CompactionPlan
{
    /// <summary>获取本次压缩的切分点：前 <see cref="SplitIndex"/> 条消息被折叠。</summary>
    public int SplitIndex { get; init; }

    /// <summary>获取本次实际被折叠的消息数（不含已覆盖部分）。</summary>
    public int FoldedMessageCount { get; init; }

    /// <summary>获取规划时的总估算 token（含系统提示与工具）。</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>获取压缩后预计占用的 token。</summary>
    public int ProjectedTokens { get; init; }

    /// <summary>获取上下文窗口。</summary>
    public int ContextWindow { get; init; }
}

/// <summary>决定是否压缩、以及折叠到哪里。纯算法，便于单测。</summary>
public static class CompactionPlanner
{
    /// <summary>
    /// 规划一次压缩。触发条件是估算 token 超过 <c>窗口 × Threshold</c>；
    /// 目标是压到 <c>窗口 × Target</c>，同时不折叠最近 <c>MinRecentTurns</c> 个用户轮次。
    /// </summary>
    /// <param name="messages">完整历史（最旧在前）。</param>
    /// <param name="coveredCount">已被现有摘要覆盖的消息数。</param>
    /// <param name="existingSummary">现有摘要；没有时为 null。</param>
    /// <param name="fixedTokens">系统提示与工具定义的估算 token。</param>
    /// <param name="contextWindow">模型上下文窗口。</param>
    /// <param name="options">压缩配置。</param>
    /// <param name="force">true 表示手动压缩，跳过阈值判断。</param>
    /// <returns>需要压缩时返回规划；否则返回 null。</returns>
    public static CompactionPlan? Plan(
        IReadOnlyList<ChatMessage> messages,
        int coveredCount,
        string? existingSummary,
        int fixedTokens,
        int contextWindow,
        CompactionOptions options,
        bool force = false)
    {
        if (messages == null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var window = Math.Max(1_000, contextWindow);
        var threshold = Clamp(options.Threshold, 0.05, 1.0, 0.9);
        var target = Clamp(options.Target, 0.05, threshold, 0.5);
        var minRecent = Math.Max(0, options.MinRecentTurns);

        var covered = Math.Max(0, Math.Min(coveredCount, messages.Count));
        var summaryTokens = string.IsNullOrWhiteSpace(existingSummary)
            ? 0
            : TokenEstimator.EstimateText(CompactionView.SummaryPreamble + existingSummary);
        var estimated = fixedTokens + summaryTokens + TokenEstimator.EstimateRange(messages, covered, messages.Count);

        var thresholdTokens = (int)(window * threshold);
        var targetTokens = (int)(window * target);
        if (!force && estimated <= thresholdTokens)
        {
            return null;
        }

        if (force && estimated <= targetTokens)
        {
            // 手动压缩但本来就不超标：没有值得折叠的内容。
            return null;
        }

        // 以用户消息作为轮次边界；最后 minRecent 轮永不折叠。
        // userStarts[k] 是第 k 轮（从 0 计）的起点。保留最近 minRecent 轮意味着
        // 最大可折叠位置是 userStarts[userStarts.Count - minRecent]。
        var userStarts = new List<int>();
        for (var index = covered; index < messages.Count; index++)
        {
            if (messages[index].Role == ChatRole.User)
            {
                userStarts.Add(index);
            }
        }

        var maxFoldableTurn = userStarts.Count - minRecent;
        if (maxFoldableTurn <= 0)
        {
            return null;
        }

        var summaryBudget = Math.Max(256, options.SummaryMaxTokens);
        var selected = -1;
        var projected = estimated;
        for (var turn = 1; turn <= maxFoldableTurn; turn++)
        {
            var split = userStarts[turn];
            if (split <= covered)
            {
                continue;
            }

            var folded = TokenEstimator.EstimateRange(messages, covered, split);
            // 摘要本身也占 token；按折叠量的 20% 粗略估计，并受 SummaryMaxTokens 约束。
            var newSummaryTokens = Math.Min(summaryBudget, Math.Max(128, (int)(folded * 0.2)));
            projected = fixedTokens
                        + newSummaryTokens
                        + TokenEstimator.EstimateRange(messages, split, messages.Count);

            selected = split;
            if (projected <= targetTokens)
            {
                break;
            }
        }

        if (selected <= covered)
        {
            return null;
        }

        return new CompactionPlan
        {
            SplitIndex = selected,
            FoldedMessageCount = selected - covered,
            EstimatedTokens = estimated,
            ProjectedTokens = projected,
            ContextWindow = window
        };
    }

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return fallback;
        }

        return value < min ? min : value > max ? max : value;
    }
}

/// <summary>压缩前提供给宿主的上下文，用于扩展交互与展示。</summary>
public sealed class CompactionContext
{
    /// <summary>获取触发原因：<c>threshold</c>、<c>manual</c> 或 <c>overflow</c>。</summary>
    public string Reason { get; init; } = "threshold";

    /// <summary>获取模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>获取本次将折叠的消息数。</summary>
    public int MessageCount { get; init; }

    /// <summary>获取当前请求的估算 token。</summary>
    public int TokenCount { get; init; }

    /// <summary>获取上下文窗口。</summary>
    public int ContextWindow { get; init; }
}

/// <summary>压缩前的决策：取消本次压缩，或直接提供摘要。</summary>
public sealed class CompactionDecision
{
    /// <summary>获取或设置取消原因；非空即取消本次压缩。</summary>
    public string? CancelReason { get; set; }

    /// <summary>获取或设置由调用方提供的摘要；非空时跳过模型调用。</summary>
    public string? Summary { get; set; }

    /// <summary>获取是否被取消。</summary>
    public bool Cancelled => !string.IsNullOrEmpty(CancelReason);

    /// <summary>继续压缩（由宿主调用模型生成摘要）。</summary>
    public static CompactionDecision Proceed() => new();

    /// <summary>取消本次压缩。</summary>
    /// <param name="reason">展示给用户的原因。</param>
    public static CompactionDecision Cancel(string reason) => new() { CancelReason = reason };

    /// <summary>使用调用方提供的摘要，跳过模型调用。</summary>
    /// <param name="summary">摘要正文。</param>
    public static CompactionDecision WithSummary(string summary) => new() { Summary = summary };
}

/// <summary>一次压缩的结果。</summary>
public sealed class CompactionResult
{
    /// <summary>获取触发原因。</summary>
    public string Reason { get; init; } = "threshold";

    /// <summary>获取摘要正文。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>获取摘要覆盖的消息数。</summary>
    public int CoveredMessageCount { get; init; }

    /// <summary>获取压缩前的估算 token。</summary>
    public int OriginalTokens { get; init; }

    /// <summary>获取压缩后的估算 token。</summary>
    public int CompactedTokens { get; init; }

    /// <summary>获取可直接写回会话记录的压缩状态。</summary>
    public ConversationCompaction ToRecord() => new()
    {
        Summary = Summary,
        CoveredMessageCount = CoveredMessageCount,
        CreatedAt = DateTimeOffset.UtcNow,
        OriginalTokenCount = OriginalTokens,
        CompactedTokenCount = CompactedTokens
    };
}

/// <summary>压缩失败的信息。</summary>
public sealed class CompactionFailure
{
    /// <summary>获取触发原因。</summary>
    public string Reason { get; init; } = "threshold";

    /// <summary>获取错误描述。</summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary>一次压缩请求。</summary>
public sealed class CompactionRequest
{
    /// <summary>获取用于生成摘要的聊天客户端。</summary>
    public IChatClient Client { get; init; } = null!;

    /// <summary>获取模型名（用于窗口推断与摘要请求）。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>获取完整历史（最旧在前）。</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = Array.Empty<ChatMessage>();

    /// <summary>获取当前压缩状态；没有时为 null。</summary>
    public ConversationCompaction? Current { get; init; }

    /// <summary>获取系统提示与工具的估算 token。</summary>
    public int FixedTokens { get; init; }

    /// <summary>获取上下文窗口。</summary>
    public int ContextWindow { get; init; } = ContextWindows.DefaultWindow;

    /// <summary>获取压缩配置。</summary>
    public CompactionOptions Options { get; init; } = new();

    /// <summary>获取是否跳过阈值判断（手动压缩）。</summary>
    public bool Force { get; init; }

    /// <summary>获取触发原因。</summary>
    public string Reason { get; init; } = "threshold";

    /// <summary>获取或设置压缩前的回调；返回 null 表示按默认流程继续。</summary>
    public Func<CompactionContext, Task<CompactionDecision?>>? OnBefore { get; init; }

    /// <summary>获取或设置压缩成功后的回调。</summary>
    public Func<CompactionResult, Task>? OnAfter { get; init; }

    /// <summary>获取或设置压缩失败时的回调。</summary>
    public Func<CompactionFailure, Task>? OnFailed { get; init; }
}

/// <summary>把一段对话转录压缩成摘要。默认实现复用当前聊天客户端。</summary>
public interface IContextSummarizer
{
    /// <summary>生成摘要。</summary>
    /// <param name="client">聊天客户端。</param>
    /// <param name="model">模型名。</param>
    /// <param name="source">待压缩的转录文本。</param>
    /// <param name="existingSummary">此前生成的摘要；没有时为 null。</param>
    /// <param name="maxTokens">摘要的最大输出 token。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<string> SummarizeAsync(
        IChatClient client,
        string model,
        string source,
        string? existingSummary,
        int maxTokens,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认摘要器：用当前会话的模型发一次非流式请求，把转录压成结构化摘要。
/// </summary>
/// <remarks>
/// 复用当前模型的好处是不需要额外配置；摘要质量与当前对话能力一致。
/// 请求不带工具、不进入宿主的工具循环，因此不会递归触发压缩。
/// </remarks>
public sealed class ChatContextSummarizer : IContextSummarizer
{
    private const string SystemPrompt =
        "你是对话压缩助手。把给定的对话转录压缩成一份信息密度高、可直接用于继续工作的中文摘要。\n" +
        "必须保留：用户的目标与意图、已确认的事实与决定、涉及的文件路径与代码位置、已完成的修改、" +
        "未完成的待办、关键错误与结论、重要的工具执行结果。\n" +
        "可以丢弃：寒暄、重复表述、已被推翻的中间推理、冗长的原文引用。\n" +
        "只输出摘要正文，不要前言、不要客套话，不要使用 Markdown 代码围栏包裹整篇摘要。";

    /// <inheritdoc />
    public async Task<string> SummarizeAsync(
        IChatClient client,
        string model,
        string source,
        string? existingSummary,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            builder.AppendLine("这是此前已经生成的摘要，请把它并入新摘要：");
            builder.AppendLine(existingSummary!.Trim());
            builder.AppendLine();
            builder.AppendLine("下面是之后新增的对话：");
            builder.AppendLine();
        }

        builder.Append(source);

        var request = new ChatRequest
        {
            Model = model,
            SystemPrompt = SystemPrompt,
            Messages = new[] { ChatMessage.User(builder.ToString()) },
            MaxOutputTokens = Math.Max(256, maxTokens)
        };

        var response = await client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        var summary = response.GetText()?.Trim() ?? string.Empty;
        return summary;
    }
}

/// <summary>
/// 上下文压缩的编排器：判断是否需要压缩、调用摘要器、维护压缩状态，
/// 并通过回调把时机暴露给宿主（宿主再转成扩展事件与界面提示）。
/// </summary>
public sealed class ContextCompactionService
{
    private readonly IContextSummarizer _summarizer;

    /// <summary>创建服务。</summary>
    /// <param name="summarizer">摘要器；null 时使用 <see cref="ChatContextSummarizer"/>。</param>
    public ContextCompactionService(IContextSummarizer? summarizer = null)
        => _summarizer = summarizer ?? new ChatContextSummarizer();

    /// <summary>
    /// 按需压缩一次。未触发或无需压缩时返回 null；摘要失败时返回 null 并回调 <see cref="CompactionRequest.OnFailed"/>。
    /// </summary>
    /// <param name="request">压缩请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<CompactionResult?> CompactAsync(
        CompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Client == null)
        {
            throw new ArgumentException("A chat client is required.", nameof(request));
        }

        var options = request.Options ?? new CompactionOptions();
        if (!options.Enabled)
        {
            return null;
        }

        var covered = request.Current == null
            ? 0
            : Math.Max(0, Math.Min(request.Current.CoveredMessageCount, request.Messages.Count));

        var plan = CompactionPlanner.Plan(
            request.Messages,
            covered,
            request.Current?.Summary,
            request.FixedTokens,
            request.ContextWindow,
            options,
            request.Force);

        if (plan == null)
        {
            return null;
        }

        var context = new CompactionContext
        {
            Reason = request.Reason,
            Model = request.Model,
            MessageCount = plan.FoldedMessageCount,
            TokenCount = plan.EstimatedTokens,
            ContextWindow = plan.ContextWindow
        };

        var summary = string.Empty;
        var provided = false;
        if (request.OnBefore != null)
        {
            try
            {
                var decision = await request.OnBefore(context).ConfigureAwait(false);
                if (decision != null)
                {
                    if (decision.Cancelled)
                    {
                        return null;
                    }

                    if (!string.IsNullOrWhiteSpace(decision.Summary))
                    {
                        summary = decision.Summary!.Trim();
                        provided = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 观察者失败不应阻断压缩本身。
                await NotifyFailedAsync(request, request.Reason, ex.Message).ConfigureAwait(false);
            }
        }

        if (!provided)
        {
            var source = CompactionView.BuildSource(
                request.Messages,
                covered,
                plan.SplitIndex,
                request.Current?.Summary);

            try
            {
                summary = await _summarizer
                    .SummarizeAsync(
                        request.Client,
                        request.Model,
                        source,
                        request.Current?.Summary,
                        options.SummaryMaxTokens,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await NotifyFailedAsync(request, request.Reason, ex.Message).ConfigureAwait(false);
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            await NotifyFailedAsync(request, request.Reason, "摘要模型返回了空内容。").ConfigureAwait(false);
            return null;
        }

        var summaryTokens = TokenEstimator.EstimateText(CompactionView.SummaryPreamble + summary);
        var result = new CompactionResult
        {
            Reason = request.Reason,
            Summary = summary,
            CoveredMessageCount = plan.SplitIndex,
            OriginalTokens = plan.EstimatedTokens,
            CompactedTokens = request.FixedTokens
                              + summaryTokens
                              + TokenEstimator.EstimateRange(request.Messages, plan.SplitIndex, request.Messages.Count)
        };

        if (request.OnAfter != null)
        {
            try
            {
                await request.OnAfter(result).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 回调失败不影响压缩结果。
            }
        }

        return result;
    }

    private static async Task NotifyFailedAsync(CompactionRequest request, string reason, string error)
    {
        if (request.OnFailed == null)
        {
            return;
        }

        try
        {
            await request.OnFailed(new CompactionFailure { Reason = reason, Error = error }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 观察者失败不应冒泡。
        }
    }
}
