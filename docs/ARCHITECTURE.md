# AiTools 架构

```
AiTools.Abstractions   ← 统一模型 / 接口 / 异常 / 工具调用循环
        ▲
AiTools.Core           ← HTTP、SSE/NDJSON、重试、错误映射、流式聚合
        ▲
┌───────┴────────┬───────────────┬─────────────┬────────────┐
AiTools.OpenAI  AiTools.Anthropic  AiTools.Gemini  AiTools.Mcp
                                                    ▲
                    AiTools.Skills ──► AiTools.Extensions ──► AiTools.Wpf
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

### AiTools.Extensions

两层：可编程 **Extensions** 运行时 + 声明式 **Plugins**（Claude Code 兼容）。均不依赖 UI。

**可编程运行时**

| 文件 | 内容 |
|---|---|
| `Runtime/ExtensionContracts.cs` | `IExtension` / `IExtensionApi`、`ExtensionCommand`、`ExtensionTool`、`ToolExposure`、`CommandResult`、`CommandContext` / `ToolContext` |
| `Runtime/ExtensionEvents.cs` | 事件枚举与载荷：`Input`、`BeforeAgentStart`、`ToolCall`、`ToolResult`、`TurnEnd`、`SessionStart`/`SessionShutdown` |
| `Runtime/ExtensionRuntime.cs` | 注册表与分发：后注册胜出、转换类链式组合、拦截类 fail-closed；`ExecuteToolAsync`（带事件）与 `InvokeToolAsync`（不带） |
| `Runtime/ExtensionSession.cs` | `session_start` / 幂等 `session_shutdown`、逆序清理回调 |
| `ExtensionStateStore.cs` | 按扩展名分桶的 JSON 状态，`TryGet<T>` 区分缺失与默认值，可 `ToJson`/`LoadFromJson` |
| `ExtensionPipeline.cs` | 宿主入口：命令目录、`PrepareTurnAsync`（提示词段落 + 工具）、`PublishTurnEndAsync`、状态导入导出 |
| `CommandCatalog.cs` | 合并扩展命令与插件命令，全名精确优先、短名冲突检测 |
| `BuiltIn/GoalExtension.cs` | `/goal` + `goal_plan` / `goal_update` + 每轮目标注入 |
| `BuiltIn/PlanExtension.cs` | `/plan` + `plan_save` / `plan_step` + 每轮计划注入 |

**插件层**

| 文件 | 内容 |
|---|---|
| `PluginManifest.cs` | 解析 `.claude-plugin/plugin.json`：元数据、`commands`、`mcpServers`，未支持组件记录为诊断 |
| `PluginLoader.cs` | 单插件加载：`skills/<name>/SKILL.md`、`commands/**/*.md`、`.mcp.json`；路径限制在插件根内 |
| `PluginComponents.cs` | `PluginCommand` / `PluginSkill` / `PluginMcpServer` 模型 |
| `PluginCatalog.cs` | 多插件发现、短名冲突检测、`ToSkillCatalog()` 聚合插件技能 |
| `CommandTemplateRenderer.cs` | 正文的单遍渲染：`$ARGUMENTS`/`$N`/`$name`、`${CLAUDE_*}`、`` !`cmd` `` 与 ` ```! ` 注入 |
| `PluginHost.cs` | 插件命令解析/渲染、系统提示清单、MCP 服务器连接的生命周期 |
| `ShellCommandRunner.cs` | 动态注入执行器（Windows `cmd.exe`，其他平台 `/bin/sh`，临时脚本 + 超时） |

设计要点：

- **扩展是可执行行为**：状态、工具、事件与命令都在 C# 中，提示词只是命令的一个产物。
- **Activate 不启动资源**：与 Pi 的工厂约束一致，进程/定时器放到 `session_start`，`session_shutdown` 幂等释放。
- **事件组合可预测**：注册顺序 + 链式转换 + 拦截 fail-closed；宿主已分发事件时用 `InvokeToolAsync` 防止重复触发。
- **状态分层**：跟随对话的状态存在 `ExtensionStateStore`，经 `ConversationRecord.ExtensionState` 持久化，不进模型上下文；要让模型看到就注入 `BeforeAgentStart`。
- **单遍替换**：插件命令的参数与 shell 输出不会被二次扫描。
- **加载不抛异常**：清单非法 → `Error` 且插件不加载；路径越界、重名、缺文件 → `Warning`；不支持的组件 → `Info`。

## 线程与生命周期

- `HttpClient`：由 `HttpClientProvider` 按 `(provider, baseAddress)` 缓存；注入自定义 `HttpClient` 时由调用方负责其生命周期。
- 流式响应：`HttpCompletionOption.ResponseHeadersRead` 打开流；`await foreach` 结束后响应与流被释放。
- `McpClient`：单个接收泵循环分发响应；并发 `Send` 由信号量保护；`DisposeAsync` 关闭传输（stdio 会终止子进程）。
- `PluginHost`：按 `plugin:server` 缓存 `McpClient`，首次 `ConnectMcpServerAsync` 完成握手；`DisposeAsync` 逐个关闭（单个失败不影响其它）。
- 插件命令的动态注入：每条命令在独立临时脚本中执行，默认 60 秒超时（`PluginHostOptions.ShellTimeout`），非零退出码中止整次调用。
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
