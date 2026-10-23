# AiTools 架构

```
AiTools.Abstractions   ← 统一模型 / 接口 / 异常 / 工具调用循环
        ▲
AiTools.Core           ← HTTP、SSE/NDJSON、重试、错误映射、流式聚合
        ▲
┌───────┴────────┬───────────────┬─────────────┬────────────┐
AiTools.OpenAI  AiTools.Anthropic  AiTools.Gemini  AiTools.Mcp
```

依赖方向严格单向：Provider 依赖 Core，Core 依赖 Abstractions，Abstractions 不依赖任何项目。

## 分层职责

### AiTools.Abstractions

| 文件 | 内容 |
|---|---|
| `Chat/ChatModels.cs` | `ChatRole`、`ContentPart` 家族、`ToolCall/ToolDefinition/ToolChoice`、`ResponseFormat`、`ChatMessage`、`ChatRequest/Response/StreamUpdate`、`IChatClient` |
| `Chat/ChatClientExtensions.cs` | `CompleteWithToolsAsync`、`StreamWithToolsAsync`、`AggregateAsync` |
| `Chat/ChatTurnExtensions.cs` | 需要保留 `ProviderData` 的流式工具循环（`StreamTurnAsync`、`StreamWithToolsExactAsync`） |
| `Embeddings/EmbeddingModels.cs` | `EmbeddingRequest/Response`、`IEmbeddingClient` |
| `Images/ImageModels.cs` | `ImageGenerationRequest/Response`、`IImageClient` |
| `Audio/AudioModels.cs` | `SpeechRequest`、`TranscriptionRequest/Response`、`IAudioClient` |
| `Exceptions.cs` | `AiToolsException` → `AiServiceException` → 认证/限流/参数/服务端子类 |
| `Usage.cs` | 统一用量统计 |

**设计要点**

- `ContentPart` 是抽象基类 + 静态工厂（`FromText` / `FromImageUri` / `FromImageBytes` / `FromFileBytes` / `FromAudioBytes`），Provider 用模式匹配渲染为各自的 JSON 结构。
- `ChatRequest.WithMessages` 支持不可变地派生新请求，工具调用循环依赖它。
- `ChatRequest.AdditionalParameters` 与 `ChatResponse.RawJson` 保证统一抽象不封死厂商特有能力。
- `ToolCall.ProviderData` 承载必须原样回传的私有数据（Gemini `thoughtSignature`、Anthropic thinking 签名等）。

### AiTools.Core

| 目录 | 内容 |
|---|---|
| `Http/AiHttpOptions.cs` | 可继承的共享配置（BaseAddress、ApiKey、超时、重试、Headers、HttpClient 注入） |
| `Http/HttpClientProvider.cs` | 按 Provider + BaseAddress 缓存 `HttpClient`，避免端口耗尽；流式场景下 `Timeout = Infinite` |
| `Http/AiErrorMapper.cs` | 状态码 → 异常子类；解析 OpenAI/Anthropic/Gemini 错误体；`Retry-After` |
| `Http/AiErrorMapper.cs`（`RetryPolicy`） | 指数退避 + 抖动；仅对瞬时故障重试 |
| `Http/AiHttpClientExtensions.cs` | `SendAndEnsureAsync`（失败即抛出映射异常）、`SendWithRetryAsync` |
| `Sse/SseParser.cs` | 通用 SSE 解析（多行 data、注释、`[DONE]`、`event:`）与 NDJSON 解析 |
| `Json/JsonHelpers.cs` | 容错读取 JSON 字段的静态辅助方法 |
| `Chat/ChatStreamAccumulator.cs` | 将流式增量聚合为完整 `ChatResponse` |

### Provider 约定

每个 Provider：

1. 定义自己的 `XxxOptions : AiHttpOptions`（含默认 BaseAddress、认证头生成、专属选项）。
2. 实现一个或多个抽象接口；构造函数接受 Options 与可选 `HttpClient`。
3. 单轮请求/响应映射；流式解析映射为 `ChatStreamUpdate`。
4. 不自行捕获 HTTP 错误——统一由 `SendWithRetryAsync`/`SendAndEnsureAsync` 抛出。
5. 提供一个 `XxxClient` 便捷聚合类（`Chat`/`Embeddings`/`Images`/`Audio` 属性共享连接池）。

完整契约见 [`docs/PROVIDER_CONTRACT.md`](PROVIDER_CONTRACT.md)。

### AiTools.Mcp

独立的客户端子系统：

- **传输层** `IMcpTransport`：JSON-RPC 2.0 消息收发。实现 `StdioMcpTransport`（子进程 + 换行分隔 JSON）、`StreamableHttpMcpTransport`（POST + SSE 响应流、`Mcp-Session-Id`）、`SseMcpTransport`（GET 事件流 + POST 消息端点）。
- **协议层** `McpClient`：`initialize` 握手、`tools/list|call`、`resources/list|read|templates`、`prompts/list|get`、`ping`、`logging/setLevel`、进度通知。
- **桥接层** `McpToolBridge`：把 MCP 工具转换为 `ToolDefinition` 与 `CompleteWithToolsAsync` 所需的处理器，名称做合规化并维护反查表。

## 线程与生命周期

- `HttpClient`：由 `HttpClientProvider` 按 `(provider, baseAddress)` 缓存；注入自定义 `HttpClient` 时由调用方负责其生命周期。
- 流式响应：`HttpCompletionOption.ResponseHeadersRead` 打开流；`await foreach` 结束后响应与流被释放。
- `McpClient`：单个接收泵循环分发响应；并发 `Send` 由信号量保护；`DisposeAsync` 关闭传输（stdio 会终止子进程）。
- 取消：所有异步 API 接受 `CancellationToken`，流式 API 在每轮迭代检查。

## 目标框架策略

`netstandard2.0` 与 `net8.0` 双编译：

- `IsExternalInit` 由 `build/NetStandardPolyfills.cs` 在 netstandard2.0 下自动注入，因此可自由使用 `init` 访问器。
- 不使用 `System.Net.Http.Json`、`HttpClient.Send`、`Stream.ReadAsync(Memory<byte>)` 等仅现代目标可用的 API；需要时用 `#if NET8_0_OR_GREATER` 条件编译。
- `SocketsHttpHandler` 仅在 net8.0 下使用；netstandard2.0 回落到 `HttpClientHandler`。

## 测试策略

`tests/AiTools.Tests` 覆盖 Core 与 Abstractions 的纯逻辑：

- SSE/NDJSON 解析（多行、注释、异常负载）
- 流式聚合（文本、工具调用片段按索引拼接、用量）
- 工具调用循环（多轮、轮数上限、原请求不可变）
- 错误映射（状态码、OpenAI/Anthropic/Gemini 错误体、`Retry-After`）
- 重试策略（瞬时故障重试、非瞬时立即失败、取消传播、上限）
- JSON 辅助方法

Provider 的网络层通过注入自定义 `HttpClient`（`AiHttpOptions.HttpClient`）可在集成测试中用假处理器验证请求体与响应映射。
