# 能力支持矩阵

统一抽象在三个厂商间的能力覆盖情况。✅ = 直接支持，⚠️ = 有条件支持（见备注），❌ = 不支持（调用会抛出 `AiInvalidRequestException`）。

## 聊天

| 能力 | OpenAI | Anthropic | Gemini |
|---|---|---|---|
| 非流式补全 | ✅ | ✅ | ✅ |
| 流式补全 | ✅ | ✅ | ✅ |
| 系统提示词 | ✅ | ✅（无 system 角色，自动提到顶层 `system`） | ✅（`systemInstruction`） |
| 多轮对话 | ✅ | ✅（自动合并连续同角色消息） | ✅ |
| 温度 / Top-P | ✅ | ✅ | ✅ |
| Top-K | ⚠️ 透传（部分模型支持） | ✅ | ✅ |
| 停止序列 | ✅ | ✅ | ✅ |
| 最大输出 token | ✅ | ✅（未设置时默认 4096） | ✅ |
| Token 用量统计 | ✅ | ✅（含缓存读写） | ✅（含思考 token） |
| 推理/思考增量 | ✅ DeepSeek `reasoning_content`、OpenRouter `reasoning` | ✅ `thinking_delta` | ✅ `thought` 部分 |
| 原始响应 JSON | ✅ `RawJson` | ✅ | ✅ |
| 厂商特有参数 | ✅ `AdditionalParameters` | ✅ | ✅ |

## 多模态输入

| 片段类型 | OpenAI | Anthropic | Gemini |
|---|---|---|---|
| 文本 | ✅ | ✅ | ✅ |
| 图片 · URL | ✅ `image_url` | ✅ `source.url` | ✅ `fileData` |
| 图片 · 内联字节 | ✅ data URI | ✅ base64 source | ✅ `inlineData` |
| PDF 文件 | ⚠️ 仅部分模型（`file` 内容）；当前实现按非图片抛出 | ✅ `document` 块（仅 application/pdf） | ✅ `inlineData` |
| 音频输入 | ⚠️ 仅音频模型（`input_audio`） | ❌ | ✅ `inlineData` |

> 说明：`FilePart` 在 OpenAI 兼容端点上目前只支持图片走 `image_url`；其他 MIME 类型会抛出清晰异常，避免生成服务端看不懂的请求。需要文件理解时优先使用 Gemini（原生内联）或 Anthropic（PDF）。

## 工具调用

| 能力 | OpenAI | Anthropic | Gemini |
|---|---|---|---|
| 工具定义（JSON Schema） | ✅ | ✅ | ✅（schema 自动转为 Gemini 方言） |
| 自动选择 / 禁用 / 强制 | ✅ | ✅ | ✅ |
| 强制指定工具 | ✅ | ✅ | ✅（`allowedFunctionNames`） |
| 并行工具调用 | ✅ | ✅（可用 `AdditionalParameters` 关闭） | ✅ |
| 流式工具参数 | ✅ | ✅ | ✅（Gemini 单块返回，一次性给出） |
| 多轮工具循环 | ✅ | ✅ | ✅（需 `thoughtSignature`，已由 `ProviderData` 自动回传） |

配合 `ChatClientExtensions` 使用：

```csharp
var final = await chat.CompleteWithToolsAsync(request, handler);       // 非流式
await foreach (var delta in chat.StreamWithToolsAsync(request, handler)) // 流式
{
    Console.Write(delta.TextDelta);
}
```

### 需要保真 ProviderData 时的流式工具循环

流式增量不携带 `ToolCall.ProviderData`（Gemini 的 `thoughtSignature`、部分服务商的签名数据）。若在多轮工具调用中必须原样回传这些数据，使用 `AiTools.Core` 提供的 `StreamWithToolsExactAsync`：工具轮次以非流式补全获取完整调用，最终答复仍以流式呈现。

```csharp
using AiTools.Core.Chat;

var result = await chat.StreamWithToolsExactAsync(
    request,
    toolHandler,
    onText: delta => Console.Write(delta));
```

单轮工具调用（无后续轮次）也可以使用 `StreamTurnAsync` 获得含工具调用的聚合结果。

## 结构化输出

| 形式 | OpenAI | Anthropic | Gemini |
|---|---|---|---|
| JSON 对象模式 | ✅ `json_object` | ⚠️ 自动追加 schema 指令到 system | ✅ `responseMimeType` |
| JSON Schema 严格模式 | ✅ `json_schema`（strict） | ⚠️ 同上（提示词约束） | ✅ `responseSchema` |
| 一键反序列化 | ✅ `response.GetJson<T>()` | ✅ | ✅ |

> Anthropic 没有原生 JSON 模式；`ResponseFormat.JsonSchema` 会把 schema 作为指令附加到 system 提示词，可靠性依赖模型遵循指令。

## 嵌入

| 能力 | OpenAI | Gemini |
|---|---|---|
| 批量输入 | ✅ | ✅ |
| 输出维度控制 | ✅ `dimensions` | ✅ `outputDimensionality` |
| 输入类型提示 | ⚠️ 透传 `input_type`（部分兼容服务商） | ❌ |
| Anthropic | ❌（Anthropic 未提供嵌入 API） |

## 图像生成

| 能力 | OpenAI | Gemini |
|---|---|---|
| 文本生图 | ✅ `/images/generations` | ✅ `generateContent` + `responseModalities` |
| 返回 URL / Base64 | ✅ | ✅（仅内联字节） |
| 尺寸 / 质量 / 风格 | ✅ | ⚠️ 通过 `AdditionalParameters` 透传 |
| 多张生成 | ✅ `n` | ⚠️ 顺序多次请求 |

## 音频

| 能力 | OpenAI | Gemini |
|---|---|---|
| 语音合成 | ✅ `/audio/speech` | ✅ TTS 模型（`responseModalities:["AUDIO"]`） |
| 语音转录 | ✅ `/audio/transcriptions` | ✅ 音频内联 + JSON 结构化输出 |
| 转录分段/时间戳 | ✅ `verbose_json` | ⚠️ 由模型按 schema 返回，不保证精确时间戳 |

## MCP

| 能力 | 状态 |
|---|---|
| stdio 传输 | ✅ 子进程 + 换行分隔 JSON-RPC |
| Streamable HTTP | ✅ POST + SSE 响应、`Mcp-Session-Id` 会话 |
| 旧版 HTTP+SSE | ✅ GET 事件流 + POST 消息端点 |
| tools/list、tools/call | ✅（含进度通知、取消） |
| resources/list、resources/read、templates | ✅ |
| prompts/list、prompts/get | ✅ |
| 日志级别设置 | ✅ `logging/setLevel` |
| 与聊天抽象桥接 | ✅ `McpToolBridge` |

## OpenAI 兼容端点

`OpenAiOptions.ForCompatibleProvider(baseUrl, apiKey, providerName)` 适用于绝大多数提供 OpenAI 协议的网关与厂商：

| 端点类型 | 示例 BaseAddress | 备注 |
|---|---|---|
| DeepSeek | `https://api.deepseek.com/v1` | 支持 `reasoning_content` |
| Moonshot / Kimi | `https://api.moonshot.cn/v1` | |
| 通义千问兼容模式 | `https://dashscope.aliyuncs.com/compatible-mode/v1` | |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` | |
| Ollama | `http://localhost:11434/v1` | 本地模型 |
| vLLM / LM Studio | 自定 | |
| OpenRouter | `https://openrouter.ai/api/v1` | 支持 `reasoning` 字段 |
| Groq | `https://api.groq.com/openai/v1` | |
| Together | `https://api.together.xyz/v1` | |

流式用量统计要求服务端支持 `stream_options.include_usage`；不支持的网关会忽略该字段，此时流式响应不携带 `Usage`。可通过 `OpenAiOptions.IncludeStreamUsage = false` 关闭。
