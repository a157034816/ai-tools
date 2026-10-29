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
                                             ▲
                              AiTools.Extensions.Scripting   （net8.0 专用，可选）
```

依赖方向严格单向：Provider 依赖 Core，Core 依赖 Abstractions，Abstractions 不依赖任何项目。

**`AiTools.Extensions.Scripting` 的硬约束：它依赖 `AiTools.Extensions`，反向绝不成立。**

| | `AiTools.Extensions` | `AiTools.Extensions.Scripting` |
|---|---|---|
| 目标框架 | `netstandard2.0` + `net8.0` | 仅 `net8.0` |
| 对 Jint 的用法 | 仅 `codemode` / `workflows` 的既有沙箱 | 完整的 JS/TS 扩展宿主（`ScriptExtension`、`ScriptExtensionProvider`） |
| 对核心的依赖 | 无（不引用 Scripting） | 引用 `AiTools.Extensions` |
| 是否必需 | 是 | 否，宿主按需注入 |

核心只暴露接缝 `IScriptExtensionProvider`（定义在 `AiTools.Extensions/Packages/`），由宿主把
`ScriptExtensionProvider` 实例塞进 `ExtensionWorkspaceOptions.ScriptProvider`。
这样 `AiTools.Extensions` 保持 netstandard2.0 可编译、不绑定 net8-only 的转译器，也不把 Jint 的
新 API 需求传染给老目标；没有注入提供者时，脚本包只会得到一条
`No IScriptExtensionProvider is configured for the <kind> package.` 诊断，C# 包不受影响。

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
| `AiToolsPaths.cs` | 用户级数据根 `~/.ai-tools`：设置、会话、技能、扩展的唯一路径来源 |

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
| `Runtime/ToolLoadout.cs` | 装载结果（`Declared`/`Callable`/`All` + `Descriptions`/`HiddenDeclarations`）、`ToolNamespace`、`ToolReach`、`ToolGroup` |
| `Runtime/ToolSearchTool.cs` | 内置 `tool_search`：在 `Codemode`/`Deferred` 里按子串查找并激活前 10 个 |
| `Runtime/CodemodeTool.cs` | 内置 `codemode`：Jint JavaScript 沙箱、脚本推导可达范围、嵌套工具调用记录与 60 秒上限 |
| `ExtensionStateStore.cs` | 按扩展名分桶的 JSON 状态，`TryGet<T>` 区分缺失与默认值，可 `ToJson`/`LoadFromJson` |
| `ExtensionPipeline.cs` | 宿主入口：命令目录、`PrepareTurnAsync`（提示词/输出风格/工具）、`GuardToolAsync`、权限后备、状态导入导出 |
| `OutputStyles.cs` | `OutputStyle`/`OutputStyleCatalog`/`OutputStyleSelection`；`OutputStylePrompt.Apply` 追加正文而不删宿主提示 |
| `Permissions.cs` | `PermissionRule`/`PermissionPolicy`（deny > ask > allow）、`PermissionGate`、`ProjectTrust`、`AccessControl` |
| `Workflows/` | `WorkflowDefinition`/`WorkflowLoader`、Jint 沙箱 `WorkflowRuntime`、把工作流变成命令的 `WorkflowCommandAdapter` |
| `CommandCatalog.cs` | 合并扩展命令、插件命令与共享 `.agents` 技能命令（`/skill:`），全名精确优先、短名冲突检测 |
| `BuiltIn/AskExtension.cs` | 通用提问工具 `ask`：标题 + 带说明的选项；`IExtensionChoiceUi` 富对话框，其余宿主退化 |
| `BuiltIn/GoalExtension.cs` | `/goal` + `goal_plan` / `goal_update` + 每轮目标注入 |
| `BuiltIn/PlanExtension.cs` | `/plan` + `plan_save` / `plan_step` + 每轮计划注入 |
| `BuiltIn/ExtensionManagerExtension.cs` | `/extension` 子命令：`list`/`scan`/`install`/`update`/`remove`/`reload`/`build` |

**扩展包子系统（`Packages/`）**

| 文件 | 内容 |
|---|---|
| `ExtensionPackageManifest.cs` | `extension.json` 解析：`name`/`kind`/`entry`/`project`/`output`/`require`/`keywords`/`metadata`/`repository`/`license` 等；未知 `kind` 降级为按文件推断并给 `Warning` |
| `ExtensionWorkspace.cs` | 扫描根目录的**直接子目录**、按 `require` 拓扑排序、`ScanAsync`/`ReloadPackageAsync`/`BuildPackageAsync`/`ReloadAllAsync`、`FileSystemWatcher` 防抖自动重载、`Diagnostics` |
| `ExtensionPackageLoader.cs` | 单包加载：清单校验（kebab-case、≤64 字符、重名）→ 构建/转译 → 组装扩展实例；失败一律转成 `PluginDiagnostic` |
| `ExtensionPackageBuilder.cs` | C#：`dotnet build -c Release -o <output>/<ticks> -m:1 -nodeReuse:false`；脚本：委托给 `IScriptExtensionProvider.PrepareAsync`；`PruneBuildDirectories(keep: 3)` |
| `ExtensionAssemblyLoader.cs` | net8.0 用可回收 `AssemblyLoadContext` + `AssemblyDependencyResolver`，把宿主程序集交还宿主；netstandard2.0 退回 `Assembly.LoadFrom` 并警告不可卸载 |
| `ExtensionSourceManager.cs` | Git 源码：`InstallAsync`（`git clone --depth 1`）、`UpdateAsync`（`git pull --ff-only`）、`RemoveAsync`（校验 Git 工作树） |
| `ExtensionPackage.cs` | 单个包的状态：`Manifest`、`Diagnostics`、`Extensions`、`IsLoaded`、`Unload()` |
| `IExtensionFactory.cs` | `IExtensionFactory` / `IExtensionFactoryContext`（包名、包目录、数据目录、宿主版本、诊断回调） |
| `IScriptExtensionProvider.cs` | `IScriptExtensionProvider` / `ScriptExtensionPreparation`：脚本包的转译与 `IExtension` 构造接缝 |

**插件层**

| 文件 | 内容 |
|---|---|
| `PluginManifest.cs` | 解析 `.claude-plugin/plugin.json`：元数据、`commands`、`mcpServers`、`outputStyles`、`workflows`，其余未支持组件记录为诊断 |
| `PluginLoader.cs` | 单插件加载：`skills/<name>/SKILL.md`、`commands/**/*.md`、`output-styles/*.md`、`workflows/*.js`、`.mcp.json`；路径限制在插件根内 |
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
- **权限 fail-closed**：`PermissionPolicy` 经 `tool_call` 事件生效，覆盖模型调用、`codemode` 脚本的嵌套调用、`ctx.ExecuteToolAsync`、`Runtime.ExecuteToolAsync` 与工作流 `tool()`；`Ask` 没有确认通道即拒绝。项目资源在 `ProjectTrust` 判定为 `Untrusted` 时不加载。
- **工作流沙箱只约束资源，不是安全边界**：`eval`/`Function` 禁用、步数与时长有上限，但脚本仍可通过 `tool`/`readFile` 触达宿主能力，拦截靠上一条。
- **加载不抛异常**：清单非法 → `Error` 且插件不加载；路径越界、重名、缺文件 → `Warning`；不支持的组件 → `Info`。扩展包同样遵守这条：`ExtensionPackageLoader.LoadAsync` 无论成败都返回带诊断的 `ExtensionPackage`。
- **构建产物隔离**：C# 包每次构建到 `bin/ext/<ticks>/` 新目录（Windows 上已加载程序集的文件被锁定，旧目录必须保持原样直到 `AssemblyLoadContext.Unload()`），只保留最近 3 份。
- **状态跨重载存活**：`ExtensionWorkspace` 重载时走 `Runtime.Reset()`，它清空注册表但**不清空** `ExtensionStateStore`；被丢弃的是 `AssemblyLoadContext`、Jint 引擎与 `ExtensionSession` 上下文。
- **脚本包依赖注入而非反向引用**：`AiTools.Extensions` 只定义 `IScriptExtensionProvider` 接缝，具体实现（Jint + 内置 TypeScript 转译）在 net8-only 的 `AiTools.Extensions.Scripting` 里，由宿主注入。

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
