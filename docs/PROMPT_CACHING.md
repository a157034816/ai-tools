# 提示缓存与预热

三个内置协议都支持服务商的**提示缓存**（prompt caching），但机制不同：

| 协议 | 缓存方式 | 客户端要做什么 |
|---|---|---|
| Anthropic | 显式标记：只缓存 `cache_control` 之前的前缀 | 写入 `cache_control`（已自动完成） |
| OpenAI / 兼容端点 | 隐式前缀缓存：≥1024 token 的稳定前缀自动缓存 | 无（保持前缀稳定即可）；可选设置路由键与保留策略 |
| Gemini | 隐式缓存：`generateContent` 自动缓存 | 无 |

因此对 OpenAI 与 Gemini 来说，本项目的贡献集中在**预热**（让缓存不过期自毁）；
对 Anthropic 还额外负责**写入标记**，否则缓存永远不会被创建。

### OpenAI：两项可选的显式调优

OpenAI 的前缀缓存是自动的，但有两项参数能显著改变命中率，默认不下发（兼容端点可能拒绝）：

| 参数 | 配置项 | 作用 |
|---|---|---|
| `prompt_cache_key` | `OpenAiOptions.PromptCacheKey` | 稳定路由键。多个会话共享同一前缀时给相同的键，可让它们路由到同一后端并共享缓存；不同前缀共用同一个键则会互相挤占 |
| `prompt_cache_retention` | `OpenAiOptions.PromptCacheRetention` | 保留策略，可选 `in_memory`（默认）与 `24h`。`24h` 写入价更高、需要账户权限，只在对话间隔超过数分钟且前缀足够大时划算 |

```csharp
var client = new OpenAiChatClient(new OpenAiOptions
{
    ApiKey = key,
    PromptCacheKey = "support-bot:v3",        // 同一系统提示的会话共用
    PromptCacheRetention = "24h"              // 长间隔对话
});
```

这两个参数与本项目的预热机制相互独立：预热在过期前刷新条目，它们决定服务端愿意保留多久。

## Anthropic：推理块必须原样回传

使用 adaptive thinking 时，响应里的 `thinking` / `redacted_thinking` 块**必须**在同模型的后续请求中原样回传。
丢掉它们不只是丢内容，还会破坏签名校验，并使提示缓存的前缀失配——每轮都全量重算。

本项目把这两类块建模为 `ThinkingPart` / `RedactedThinkingPart`，在以下位置保持原样：

- 非流式响应解析（`ParseResponse`）；
- 流式聚合（`ChatStreamAccumulator` 按块序号还原文本与签名，`signature_delta` 不再被丢弃）；
- 重建请求时按 `thinking → redacted_thinking → text → tool_use` 的原始顺序写回。

转向 OpenAI / Gemini 时这些块会被跳过，因此同一份会话历史可以在协议之间切换。

## Anthropic：标记放在哪

Anthropic 的缓存是**前缀缓存**——`cache_control` 之前的所有内容会被缓存，之后不会。
标记放错位置是最常见的失败原因：放在每条消息上不会增加命中率，只会写多份缓存条目。

默认策略 `Automatic` 按 **工具 → 系统提示 → 末条消息** 的顺序放置，从后往前保证每个标记都覆盖它之前的全部内容：

```
[tools ... 最后一个工具带 cache_control]   ← 缓存点 1
[system（块数组，带 cache_control）]        ← 缓存点 2
[messages ... 最后一条消息带 cache_control] ← 缓存点 3（滚动）
```

- **工具在前**：工具定义通常整轮不变，命中率最高。
- **系统提示其次**：由宿主与扩展拼装，轮与轮之间稳定。
- **末条消息**：让对话历史在后续轮次命中。关闭它则只有前两个缓存点，适合历史每轮都大幅变化的场景。

### 配置

```csharp
// 直接使用 Provider 时
var client = new AnthropicClient(new AnthropicOptions
{
    ApiKey = key,
    PromptCaching = AnthropicPromptCachingOptions.Automatic
});

// 只要 1 小时保留
var longLived = new AnthropicClient(new AnthropicOptions
{
    ApiKey = key,
    PromptCaching = new AnthropicPromptCachingOptions { UseLongRetention = true }
});
```

`UseLongRetention` 会让标记变成 `{"type":"ephemeral","ttl":"1h"}`，并自动补上
`anthropic-beta: extended-cache-ttl-2025-04-11`（只在调用方没自己写时补）。
长保留的写入单价更高（1h 写入约为短保留的 2 倍，短保留约 1.25 倍），适合长时间离开后仍希望第一次请求就命中。

预热排程会跟随该开关：开启长保留后寿命按 1 小时计算，否则预热器会在缓存仍然有效时
反复重写条目，白付写入溢价。

不设置 `PromptCaching` 时客户端**不发送任何** `cache_control`，行为与引入该机制之前完全一致。
需要完全手写标记的调用方可以通过 `ChatRequest.AdditionalParameters` 或扩展的
`BeforeProviderRequest` 直接改写请求体。

### 缓存点数量上限

Anthropic 限制单个请求最多 4 个缓存点。默认策略用 3 个（工具、系统提示、末条消息），留一个余量给调用方。
如果你的系统提示被宿主拆成多段、想每段单独打点，关闭 `CacheSystem` 自行通过扩展注入。

## 预热：为什么要做，什么时候停

缓存有寿命（Anthropic 短保留 5 分钟、长保留 1 小时；OpenAI 与 Gemini 只承诺"最佳努力"，实际寿命由服务端决定）。
如果一次工具循环跑了 6 分钟才发下一次请求，Anthropic 的缓存已经过期，下一次请求要按**未命中**重新计费——
写入费比读取费贵得多，这是 agent 场景里最容易被忽略的开销。

预热器（`PromptCacheWarmer`）的做法与 Pi 一致：**在缓存过期前，把同一个请求重放一次，输出上限压到最小**。
重放会刷新与真实请求相同的缓存条目，代价接近一次缓存读。

输出上限按协议取（`PromptCachePolicy.WarmOutputTokens`）：

| 协议 | 输出上限 | 说明 |
|---|---|---|
| Anthropic | `max_tokens: 0` | 官方推荐的预热写法：不产生输出 token，意图明确 |
| OpenAI / Gemini / 兼容端点 | `max_tokens: 1` | 这些端点要求至少 1 个 token |

重放请求会清空 `ToolChoice` 与 `ResponseFormat`：强制工具选择和 JSON Schema 约束对"只写缓存"没有意义，
而且 `max_tokens: 0` 与它们组合会被 Anthropic 直接拒绝。

### 最小可缓存长度

低于服务商的下限时，`cache_control` 不报错，只是**不会创建缓存条目**——此时预热纯属白付一次请求。
预热器在估算阶段就拦住这种情况，并停止预热（`CacheWarmingDecision.BelowCacheableMinimum` 会说明原因）。

Anthropic 的下限按模型分代且**不单调**：

| 模型 | 最小可缓存长度 |
|---|---|
| Opus 5 / Fable 5 / Mythos 5 | 512 |
| Opus 4.8 / Sonnet 5 / Sonnet 4.6 / Sonnet 4.5 | 1024 |
| Opus 4.7 | 2048 |
| Opus 4.6 / Opus 4.5 / Haiku 4.5 | 4096 |

本项目取其中的最大值 **4096** 作为保守下限（`PromptCachePolicyCatalog.AnthropicConservativeMinimumTokens`）：
宁可少预热，也不要为没写进缓存的请求付费。要按模型精调时覆盖该常量即可。
OpenAI 与 Gemini 取 1024。

### 三种模式

| 模式 | 行为 | 适用 |
|---|---|---|
| `Streaming`（默认） | 只在一轮 agent 运行期间刷新；一轮结束即停 | 长工具循环；离开后不产生费用 |
| `Idle` | 一轮结束后继续刷新，直到 `MaxIdleAge`（默认 30 分钟） | 用户频繁回来但每次只问一句 |
| `Off` | 不预热 | 完全手动控制 |

刷新点默认取缓存寿命的 **90%**，并至少保留 10 秒余量（`RefreshFraction` / `MinimumMargin`）。
轮内预热最长 1 小时（`MaxStreamingAge`），空闲预热最长 30 分钟（`MaxIdleAge`），单请求最多刷新 24 次（`MaxRefreshes`）。

### 经济性阈值

不是每次刷新都划算：刷新本身要付一次缓存读 + 1 个输出 token。
预热器按下面的公式估算（单价来自 `PromptCachePolicyCatalog` 内置表）：

```
warmCost  = 提示token × 缓存读单价 + 1 × 输出单价
missCost  = 提示token × (输入单价 − 缓存读单价)
savings   = 概率 × missCost − warmCost
```

- **轮内**概率取 1（这一轮肯定还会发请求）。
- **空闲**概率默认 0.15（测得的历史经验值，明显低于"用户会回来"的直觉）。

只有当 `savings ≥ MinimumExpectedSavings`（默认 **$0.05**）时才发送刷新；
提示 token 数或单价未知时视为**经济性不可知**，一律不刷新——宁可少预热，也不要为不缓存的服务付费。

### 关闭与调整

```csharp
// 关闭缓存写入（Anthropic 不再发 cache_control）与预热
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    PromptCache = new PromptCacheOptions { Enabled = false }
});

// 保留缓存写入，只关闭预热
new TerminalOptions
{
    PromptCache = new PromptCacheOptions { WarmingEnabled = false }
};

// 调整预热参数（切成空闲模式、提高阈值）
new TerminalOptions
{
    PromptCacheWarming = new PromptCacheWarmingOptions
    {
        Mode = PromptCacheWarmingMode.Idle,
        MinimumExpectedSavings = 0.10,
        IdleContinuationProbability = 0.25
    }
};
```

宿主在以下时机自动停止预热，避免写一堆用不上的缓存：

| 时机 | 原因 |
|---|---|
| 一轮结束（`Streaming` 模式） | 本轮已结束 |
| `/new`、`/clear`、`/resume`、`/fork` | 会话已切换，旧前缀不再使用 |
| 切换模型 | 缓存键包含模型 |
| 真实请求不再可缓存 | 前缀已改变（例如换了不可缓存的模型） |
| 提示低于最小可缓存长度 | 服务端不会创建条目，刷新纯属浪费 |
| 刷新请求失败 | 避免持续失败重试 |

### 可缓存的范围

`PromptCachePolicyCatalog.Resolve(protocol, modelId)` 决定哪些请求参与缓存与预热：

- `openai` 协议：`gpt-4o` / `gpt-4.1` / `gpt-5` / `o3` / `o4` / `deepseek` / `kimi` / `moonshot` / `qwen` / `glm` / `gemini` 前缀。
  `o1-mini`、`ft:` 微调模型、未知模型返回"不可缓存"。
- `anthropic` 协议：全部 Messages 模型（含兼容端点）。
- `gemini` 协议：全部 `generateContent` 模型。
- 嵌入、图像、语音、转录模型一律返回"不可缓存"——这些端点没有对话前缀缓存语义。

模型名可带 `provider/` 或 `models/` 前缀，会先归一化再匹配。

## 扩展事件：`CacheWarmingDecision`

对应 Pi 的 `cache_warming_decision`。预热器在**每次刷新前**分发，载荷里带它自己的结论与经济性估算：

| 字段 | 说明 |
|---|---|
| `Phase` | `streaming`（轮内）或 `idle`（轮间） |
| `Model` / `PromptTokens` | 模型与上一次真实请求的提示规模 |
| `WarmCost` / `MissCost` / `ExpectedSavings` | 经济性估算（美元） |
| `ContinuationProbability` | "下一次请求会在过期前到来"的概率 |
| `EconomicsAvailable` | 单价或规模未知、或提示低于最小可缓存长度时为 false |
| `BelowCacheableMinimum` | 提示是否低于该模型的最小可缓存长度 |
| `MinimumCacheableTokens` | 该模型的最小可缓存长度（token） |
| `Action` | `warm` / `stop`，处理器可改写 |

```csharp
api.On<CacheWarmingDecisionEvent>(ExtensionEvent.CacheWarmingDecision, (e, ctx, _) =>
{
    // 空闲阶段也保持缓存：例如用户正在长会话里频繁追问。
    if (e.Phase == "idle" && e.EconomicsAvailable) e.Warm();

    // 本地网关不收费：别浪费请求。
    if (e.Model.StartsWith("local-")) e.Stop();

    return Task.FromResult<CacheWarmingDecisionEvent?>(e);
});
```

- 处理器按注册顺序执行，**最后一个**给出的结论生效。
- 处理器抛异常时回退到内置结论（fail-closed），不会阻断预热。
- 无处理器时预热的开销可忽略（一次字典查找加一次分支）。

脚本扩展可直接读写 `event.action`（`"warm"` / `"stop"`），其余字段与上表同名（camelCase）。

> 扩展改写**不会**让刷新超出去经济性估算之外——它只改变这一次的结论，不改变阈值。
> 想让阈值本身变化，请调整 `PromptCacheWarmingOptions`。

## 用量统计

`Usage` 的两个缓存字段（`CacheReadInputTokens` / `CacheCreationInputTokens`）三协议都会解析：

| 协议 | 缓存读 | 缓存写 |
|---|---|---|
| Anthropic | `cache_read_input_tokens` | `cache_creation_input_tokens` |
| OpenAI | `prompt_tokens_details.cached_tokens` | —（写入计入普通输入） |
| Gemini | `cachedContentTokenCount` | —（隐式缓存无写入费） |

口径不同，直接用 `Usage.InputTokens` 估算提示规模会算错：

- **Anthropic** 的 `input_tokens` **不含**缓存读写，总提示量是三者之和。
- **OpenAI / Gemini** 的 `InputTokens` **已包含**命中部分，再加缓存读会重复计算。

用 `PromptCacheAccounting.GetPromptTokens(providerName, usage)` 取统一口径，
`GetCacheHitRate(providerName, usage)` 取命中率。终端的完成状态行会把命中率直接显示出来
（例如 `完成 · 1200→340 tokens · 缓存 75%`）。

预热请求的用量**计入会话总量但不进入模型上下文**——与 Pi 的 `cache_warm` 记录一致。

## 已知边界

- **寿命是服务商的默认承诺**：OpenAI 与 Gemini 的前缀缓存是"best-effort"，服务端可能提前淘汰，
  官方也不承诺隐式缓存的 TTL。预热提高命中概率，但不能保证命中。
- **最小可缓存长度是保守值**：Anthropic 各代下限从 512 到 4096 不等且不单调，本项目统一取 4096。
  在 Opus 5 这类下限只有 512 的模型上，20K 以下的会话会被判为"不值得预热"，这是刻意的保守取舍。
- **单价表是内置近似值**：按主流模型定价写成，与实际账单可能有偏差。
  它只用于判断"值不值得刷新"，不影响计费；单价更新后改 `PromptCachePolicyCatalog` 即可。
- **重放请求不是免费的**：即使压到 1 个输出 token，缓存读仍按 token 计费。
  阈值默认 $0.05，提示很小（几 k token）时预热永远不会触发，这是预期行为。
- **不支持显式缓存管理**：Gemini 的 `cachedContents`（创建/续期/删除）与本机制的隐式缓存是两套东西，
  目前没有实现。需要时可通过 `AdditionalParameters` 手工接入。
- **Anthropic 长保留的写入费更高**：`UseLongRetention` 适合"离开一小时回来还想命中"，
  高频短间隔对话用默认的 5 分钟更划算。
- **`cache_control` 与 `AdditionalParameters` 的相互作用**：后者是顶层浅合并，
  无法改写 `system` / `tools` / `messages` 内部；需要完全自定义标记时请关闭 `PromptCaching`。
