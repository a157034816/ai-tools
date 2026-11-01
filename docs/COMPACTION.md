# 上下文压缩（自动摘要）

对话变长后，模型请求迟早会超出上下文窗口。本项目在**估算 token 达到模型窗口的 90%** 时
自动把早期对话压成摘要，保留最近轮次原文；也可以随时用 `/compact` 手动压缩。

## 默认行为

| 项 | 默认值 | 说明 |
|---|---|---|
| 触发阈值 | 窗口的 **90%** | `CompactionOptions.Threshold` |
| 压缩目标 | 窗口的 **50%** | `CompactionOptions.Target`；从最旧开始折叠，够用即止 |
| 保底保留 | 最近 **2 个用户轮次** | `CompactionOptions.MinRecentTurns`；无论超限多少都不折叠 |
| 摘要模型 | 当前会话的模型 | 非流式请求，不进入工具循环，不会递归触发压缩 |
| 摘要上限 | 2048 输出 token | `CompactionOptions.SummaryMaxTokens` |

阈值与目标都是**比例**，因此换模型（窗口变化）无需改配置。压缩只在轮次之间发生，
不会打断正在进行的流式输出。

## 窗口从哪里来

按模型 id 前缀匹配内置表（最长前缀优先），未命中时用 128k：

| 模型前缀 | 窗口 |
|---|---|
| `claude` | 200,000 |
| `gpt-4.1` | 1,047,576 |
| `gpt-4o` / `gpt-4-turbo` | 128,000 |
| `gpt-5` | 400,000 |
| `o1` / `o3` / `o4` | 200,000 |
| `gemini`（含 2.5 / 2.0 / 1.5） | 1,048,576 |
| `deepseek` | 64,000 |
| `kimi` / `moonshot` | 128,000 |
| `qwen-max` | 32,768 |
| `qwen-plus` / `qwen` | 131,072 |
| `qwen-turbo` | 1,000,000 |
| `llama` / `mistral` | 128,000 |
| 其他 | 128,000 |

`anthropic/claude-sonnet-4-5`、`models/gemini-2.5-pro` 这类带前缀的 id 会自动去掉前缀再匹配。

**覆盖方式**：在 `settings.json` 的模型条目上写 `contextWindow`（token 数）：

```json
{
  "providers": [
    {
      "id": "my-gateway",
      "models": [
        { "id": "my-model", "contextWindow": 32768 }
      ]
    }
  ]
}
```

优先级：`ModelConfig.ContextWindow` > `CompactionOptions.ContextWindow` > 内置表 > 128k。

## token 如何估算

不依赖服务商分词器：ASCII 约 4 字符 / token，非 ASCII（中日韩等）约 1.4 字符 / token，
每条消息 +4 开销，每张图片按 1000、每个文件/音频按 2000 计。估算**略偏高**，
宁可提前压缩，也不要让服务商返回 `context_length_exceeded`。

估算覆盖：系统提示、工具定义（名称 + 描述 + JSON Schema）、全部消息正文与工具调用参数。

## 压缩后模型看到什么

消息本体**完整保留在会话记录里**（`/resume`、`/fork` 都不丢原文），只是发送请求时折叠：

```
[系统提示]                        ← 不变
[用户] 以下是对更早对话的摘要…      ← 取代前 N 条消息
  <摘要正文>
[用户] 最近第 1 轮
[助手] …
[用户] 最近第 2 轮
[助手] …
```

再次压缩时，旧摘要并入新摘要（滚动合并），`CoveredMessageCount` 指向新的切分点。
切分点总是落在用户消息上，不会把一次工具调用链拆开。

## 手动压缩

| 宿主 | 命令 | 行为 |
|---|---|---|
| 终端 Agent | `/compact` | 先显示当前占用与窗口，再强制压缩 |
| WPF 对话页 | `/compact` | 状态栏显示进度，压缩结果以助手气泡出现在对话流里 |

强制压缩同样跳过阈值判断，但仍要求"压缩后估算低于目标"，否则视为无需压缩。

## 超限兜底

估算保守时，服务商仍可能先报错。宿主识别以下错误（错误码或消息文本）后会自动
压缩一次并重试本轮：

- `context_length_exceeded`（OpenAI 系）
- `prompt is too long` / `input is too long`（Anthropic 系）
- `maximum context length` / `context window` / `too many tokens`（通用）

兜底触发时 `PreCompact.Reason` 为 `overflow`。重试只发生一次；再次失败按普通请求错误处理。

## 接入

两个宿主都默认开启，无需配置。要自定义：

```csharp
// 终端
new TerminalOptions
{
    Compaction = new CompactionOptions
    {
        Threshold = 0.85,
        Target = 0.4,
        MinRecentTurns = 3,
        SummaryMaxTokens = 4096
    },
    ContextSummarizer = mySummarizer   // 可选：换成自己的摘要实现
};

// WPF
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    Compaction = new CompactionOptions { /* 同上 */ },
    ContextSummarizer = mySummarizer
});
```

关闭自动压缩：`Compaction.Enabled = false`。此时 `/compact` 也不生效。

### 自定义摘要器

```csharp
public sealed class CheapModelSummarizer : IContextSummarizer
{
    public async Task<string> SummarizeAsync(
        IChatClient client, string model, string source,
        string? existingSummary, int maxTokens, CancellationToken cancellationToken)
    {
        // 用更便宜的模型（或本地模型）做摘要，而不是当前会话模型。
        var request = new ChatRequest
        {
            Model = "gpt-4.1-mini",
            SystemPrompt = "把对话压成摘要，保留目标、决定、文件路径、待办与错误结论。",
            Messages = new[] { ChatMessage.User(source) },
            MaxOutputTokens = maxTokens
        };
        var response = await myClient.CompleteAsync(request, cancellationToken);
        return response.GetText();
    }
}
```

## 扩展事件

三个时机与 Claude Code 的 `PreCompact` / `PostCompact` 对应：

| 事件 | 时机 | 可否改写 |
|---|---|---|
| `PreCompact` | 摘要生成前 | 可 `Cancel(reason)` 取消；设置 `Summary` 直接提供摘要，跳过模型调用 |
| `PostCompact` | 压缩完成后 | 只通知（`Summary`、`CoveredMessageCount`、前后 token 数） |
| `CompactFailed` | 摘要生成失败 | 只通知（`Error`）；宿主保持原消息继续本轮 |

```csharp
api.On<PreCompactEvent>(ExtensionEvent.PreCompact, (e, _, _) =>
{
    if (e.TokenCount < 100_000) e.Cancel("还没到需要压缩的程度");
    return Task.FromResult<PreCompactEvent?>(e);
});

api.On<PostCompactEvent>(ExtensionEvent.PostCompact, (e, ctx, _) =>
{
    ctx.NotifyUser($"压缩完成：{e.OriginalTokenCount} → {e.CompactedTokenCount} tokens");
    return Task.FromResult<PostCompactEvent?>(null);
});
```

脚本扩展可直接读取 `event.reason` / `event.tokenCount` / `event.contextWindow`，
并通过返回对象里的 `cancelReason` 或 `summary` 生效。

## 已知边界

- **文件与图片按固定值估算**：多模态消息的 token 与像素尺寸无关，超大图片可能被低估。
  接入此类内容多的会话时，建议把 `Threshold` 调到 0.85 附近留余量。
- **压缩发生在轮次边界**：单轮内的工具循环如果自身就超过窗口（例如一次读取巨型文件），
  会走"超限兜底"路径，压缩后重发整轮。
- **摘要质量取决于当前模型**：小模型的摘要可能丢失细节；需要稳定结果时接自定义
  `IContextSummarizer`，固定用强模型。
- **WPF 的 `/compact` 气泡不计入历史**：提示气泡只用于展示，不会进入下一次请求。
