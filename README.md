# AiTools

一套面向 .NET 的统一 AI 工具集：用同一组接口调用 OpenAI（及所有兼容端点）、Anthropic Claude、Google Gemini，并把 **MCP（Model Context Protocol）** 的工具、资源与提示词直接接进模型调用链。

```csharp
using AiTools.Abstractions;
using AiTools.OpenAI;

using var client = OpenAiClient.Create(new OpenAiOptions { ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")! });

var response = await client.Chat.CompleteAsync(new ChatRequest
{
    Model = "gpt-4.1",
    Messages = new[] { ChatMessage.User("用一句话解释什么是依赖注入。") }
});

Console.WriteLine(response.GetText());
```

## 特性

- **统一抽象**：`IChatClient` / `IEmbeddingClient` / `IImageClient` / `IAudioClient`，切换厂商只改一行构造代码。
- **多厂商**：OpenAI（含 DeepSeek、Moonshot/Kimi、通义千问兼容模式、Ollama、vLLM、OpenRouter、Groq 等兼容端点）、Anthropic、Gemini。
- **完整能力面**：聊天（含流式、多模态、JSON Schema 结构化输出）、函数/工具调用、嵌入、图像生成、语音合成与转录。
- **MCP 客户端**：stdio / Streamable HTTP / 旧版 HTTP+SSE 三种传输，`McpToolBridge` 一行代码把 MCP 工具变成模型可调用的函数。
- **Claude Code 插件**：直接加载 `.claude-plugin/plugin.json` 插件包，`commands/*.md`、`skills/*/SKILL.md`、`.mcp.json` 全部生效，内置 `/goal` 与 `/plan`。
- **生产级细节**：指数退避重试、`Retry-After` 支持、统一异常体系（认证/限流/参数/服务端）、SSE 与 NDJSON 双协议解析、可注入 `HttpClient`。
- **双目标**：`netstandard2.0` + `net8.0`，既能用于 .NET Framework 4.6.1+ 的老项目，也能享受现代运行时的性能。

## 包结构

| 包 | 说明 |
|---|---|
| `AiTools.Abstractions` | 与厂商无关的统一模型、接口、异常与工具调用循环扩展 |
| `AiTools.Core` | HTTP、SSE 解析、重试、错误映射、流式聚合等共享基础设施 |
| `AiTools.OpenAI` | OpenAI 与 OpenAI 兼容端点 |
| `AiTools.Anthropic` | Anthropic Messages API |
| `AiTools.Gemini` | Google Gemini API |
| `AiTools.Mcp` | Model Context Protocol 客户端 |
| `AiTools.Skills` | Agent Skills：技能发现、渐进式披露与模型可调用的技能加载工具 |
| `AiTools.Extensions` | Extensions：兼容 Claude Code 的插件框架（清单、斜杠命令、插件技能、MCP 声明） |
| `AiTools.Wpf` | 可复用 WPF 组件：设置页（供应商/模型配置）+ 对话页，宿主通过全局配置与接口注册 |

典型引用方式：引用所选 Provider + `AiTools.Abstractions`（`Core` 会作为传递依赖自动引入）。

## 快速开始

### 1. 聊天

```csharp
using AiTools.Abstractions;
using AiTools.Anthropic;

using var claude = AnthropicClient.Create(new AnthropicOptions
{
    ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")!
});

var reply = await claude.Chat.CompleteAsync(new ChatRequest
{
    Model = "claude-sonnet-4-5",
    SystemPrompt = "你是简洁的中文技术助手。",
    Messages = new[] { ChatMessage.User("C# 的 record 和 class 有什么区别？") },
    MaxOutputTokens = 1024,
    Temperature = 0.3
});

Console.WriteLine($"{reply.GetText()}\n(tokens: {reply.Usage?.InputTokens}/{reply.Usage?.OutputTokens})");
```

### 2. 流式输出

```csharp
using var client = GeminiClient.Create(new GeminiOptions { ApiKey = apiKey });

await foreach (var update in client.Chat.StreamAsync(new ChatRequest
{
    Model = "gemini-2.5-flash",
    Messages = new[] { ChatMessage.User("写一首关于编译器的五言绝句。") }
}))
{
    if (update.TextDelta is { Length: > 0 } delta)
    {
        Console.Write(delta);
    }

    if (update.ReasoningDelta is { Length: > 0 } reasoning)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(reasoning);
        Console.ResetColor();
    }
}
```

需要聚合结果时：

```csharp
var response = await client.Chat.StreamAsync(request).AggregateAsync();
```

### 3. 工具调用（自动循环）

```csharp
var tools = new[] { ToolDefinition.FromJsonSchema(
    "get_weather", "查询城市天气",
    """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""") };

var result = await client.Chat.CompleteWithToolsAsync(
    new ChatRequest { Model = "claude-sonnet-4-5", Messages = new[] { ChatMessage.User("北京今天天气如何？") }, Tools = tools },
    async (call, ct) =>
    {
        using var args = call.ParseArguments();
        var city = args.RootElement.GetProperty("city").GetString();
        return await WeatherApi.QueryAsync(city!, ct);
    });

Console.WriteLine(result.GetText());
```

流式工具调用用 `StreamWithToolsAsync`，工具调用过程对上层透明。

### 4. 多模态输入

```csharp
var message = ChatMessage.UserParts(
    ContentPart.FromText("这张图里是什么？"),
    ContentPart.FromImageUri("https://example.com/cat.jpg"),
    ContentPart.FromImageBytes(pngBytes, "image/png"));

var response = await client.Chat.CompleteAsync(new ChatRequest
{
    Model = "gemini-2.5-flash",
    Messages = new[] { message }
});
```

PDF 文档可用 `ContentPart.FromFileBytes(pdfBytes, "application/pdf")`（OpenAI 兼容端点与 Anthropic 对 PDF 的支持范围见各 Provider 文档）。

### 5. 结构化输出

```csharp
var request = new ChatRequest
{
    Model = "gpt-4.1",
    Messages = new[] { ChatMessage.User("提取：张三，28 岁，软件工程师") },
    ResponseFormat = ResponseFormat.JsonSchema("person", """
        {"type":"object","properties":{"name":{"type":"string"},"age":{"type":"integer"},"job":{"type":"string"}},"required":["name","age","job"]}
        """)
};

var json = (await client.Chat.CompleteAsync(request)).GetText();
```

### 6. OpenAI 兼容端点

```csharp
using var deepSeek = OpenAiClient.Create(OpenAiOptions.ForCompatibleProvider(
    "https://api.deepseek.com/v1", deepSeekKey, providerName: "deepseek"));

using var ollama = OpenAiClient.Create(OpenAiOptions.ForCompatibleProvider(
    "http://localhost:11434/v1", "ollama", providerName: "ollama"));
```

### 7. MCP 工具接入模型

```csharp
using AiTools.Mcp;

await using var transport = new StdioMcpTransport(new StdioTransportOptions
{
    Command = "npx",
    Arguments = new[] { "-y", "@modelcontextprotocol/server-filesystem", "D:/workspace" }
});

await using var mcp = new McpClient(transport);
await mcp.InitializeAsync();

var tools = await mcp.ListToolsAsync();

var response = await client.Chat.CompleteWithToolsAsync(
    new ChatRequest
    {
        Model = "gpt-4.1",
        Messages = new[] { ChatMessage.User("列出 workspace 下的所有 Markdown 文件") },
        Tools = McpToolBridge.ToToolDefinitions(tools)
    },
    McpToolBridge.CreateToolHandler(mcp, tools));

Console.WriteLine(response.GetText());
```

### 8. 嵌入 / 图像 / 音频

```csharp
var embedding = await client.Embeddings.EmbedAsync(new EmbeddingRequest
{
    Model = "text-embedding-3-small",
    Inputs = new[] { "第一段", "第二段" }
});

var image = await client.Images.GenerateAsync(new ImageGenerationRequest
{
    Model = "gpt-image-1",
    Prompt = "一只戴眼镜的橘猫在写 C#",
    Size = "1024x1024",
    ResponseFormat = ImageResponseFormat.Base64
});

var speech = await client.Audio.SynthesizeAsync(new SpeechRequest
{
    Model = "gpt-4o-mini-tts",
    Input = "你好，世界",
    Voice = "alloy",
    ResponseFormat = "mp3"
});

var transcript = await client.Audio.TranscribeAsync(new TranscriptionRequest
{
    Model = "whisper-1",
    Audio = mp3Bytes,
    Language = "zh"
});
```

## WPF 预制组件（AiTools.Wpf）

现成的**设置页**（供应商 / 模型 / API Key 配置）与**对话页**（流式聊天、工具调用、停止/重发、用量展示），宿主通过全局配置 + 接口实现注册：

```csharp
// App.xaml.cs
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    DefaultSystemPrompt = "你是简洁、准确的中文技术助手。",
    EnableTools = true,
    // SettingsStore / ProviderFactory / SecretProtector 均可替换
});

AiToolsWpf.RegisterToolProvider(new MyOrderToolProvider()); // 可选：宿主工具
```

```xml
<Window xmlns:wpf="clr-namespace:AiTools.Wpf.Views;assembly=AiTools.Wpf">
    <wpf:ChatPage x:Name="ChatView" />
</Window>
```

可替换接口：`ISettingsStore`、`ISecretProtector`（默认 DPAPI 加密）、`IProviderFactory`、`IChatToolProvider`。内置服务商模板：OpenAI、Anthropic、Gemini、DeepSeek、Kimi、通义千问、Ollama、自定义兼容端点。

详见 [`docs/WPF.md`](docs/WPF.md)，演示程序：`dotnet run --project samples/AiTools.Wpf.Demo`。

## MCP Agent 管理器示例

一个 WPF 桌面程序，为主流 AI coding agent 批量添加/移除预制 MCP 服务，并为不支持的 agent 导出通用 JSON。

```bash
dotnet run --project samples/AiTools.Samples.McpAgentManager
```

支持 11 个 agent（Claude Code / Codex / Grok / Gemini / Kimi / OpenClaw / OpenCode / Pi / OMP / Hermes / dsh），各自使用正确的配置格式（JSON / TOML / YAML），写入前自动备份、原子替换、保留原有内容。详见 [`docs/MCP_AGENT_MANAGER.md`](docs/MCP_AGENT_MANAGER.md)。

## 会话恢复与分叉（/resume、/fork）

对话页内置会话持久化：每轮自动保存，可随时恢复或从任意对话分叉。

```csharp
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    ConversationStore = new JsonConversationStore(@"D:\my-app\data"), // 默认 %LOCALAPPDATA%\AiTools
    AutoSaveConversations = true
});
```

| 命令 | 行为 |
|---|---|
| `/resume` | 打开选择器，恢复历史对话或删除；恢复时一并还原服务商/模型选择 |
| `/fork` | 复制当前对话为新会话（原会话不变），可继续独立演进 |

存储层 `AiTools.Core.Sessions` 与 UI 无关，控制台/Web/服务端亦可直接使用。详见 [`docs/SESSIONS.md`](docs/SESSIONS.md)。

## Skills（Agent Skills）技能是"文件夹 + `SKILL.md`"组成的知识包，通过**渐进式披露**交给模型：系统提示只放名称与描述，模型需要时调用 `load_skill` 加载完整指令，用 `read_skill_file` 读取随包资源。与厂商无关，OpenAI / Anthropic / Gemini 通用。

```csharp
using AiTools.Skills;

var toolset = new SkillToolset(SkillCatalog.LoadFromDirectory("D:/my-app/skills"));

var answer = await client.Chat.CompleteWithSkillsAsync(
    ChatRequest.FromUser("gpt-4.1", "根据改动写一条提交信息"),
    toolset);
```

WPF 宿主一行接入：

```csharp
AiToolsWpf.RegisterToolProvider(SkillChatToolProvider.FromDirectory("skills"));
```

详见 [`docs/SKILLS.md`](docs/SKILLS.md)；演示见 `samples/AiTools.Wpf.Demo/skills/commit-message`。

## Extensions（Claude Code 兼容插件）

`AiTools.Extensions` 直接读取 Claude Code 的插件目录约定：`.claude-plugin/plugin.json`、`commands/*.md`、`skills/<name>/SKILL.md`、`.mcp.json`。插件命令会展开成一条用户消息再发给模型，其中的 `$ARGUMENTS`、`$1`、`${CLAUDE_PLUGIN_ROOT}` 等占位符按 Claude Code 规则替换。

```csharp
using AiTools.Extensions;

var catalog = ExtensionCatalog.LoadFromDirectory("D:/my-app/plugins");
var host = new ExtensionHost(catalog, new ExtensionHostOptions
{
    ProjectDirectory = "D:/my-app",
    EnableShellExecution = false          // 是否允许正文里的 !`cmd` 动态上下文注入
});

if (host.TryResolve("/goal 实现插件框架", out var command, out var arguments))
{
    var prompt = host.Render(command!, arguments).Text;  // 展开后的提示词，直接作为 user 消息发送
}
```

仓库内的 [`plugins/workflows`](plugins/workflows) 是一个可直接使用的插件，提供两个命令：

| 命令 | 作用 |
|---|---|
| `/goal <目标>` | 把目标转成可验证的成功标准 + 分步计划，并在后续轮次按清单汇报进度 |
| `/plan <任务>` | 基于真实代码证据产出分步实施计划（文件、变更、验证方式、风险） |

WPF 宿主一行接入，输入框中键入 `/` 即出现命令面板：

```csharp
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    PluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins")
});
```

插件的 `skills/` 会通过 `load_skill` 元工具交给模型按需加载，`.mcp.json` 声明的 MCP 服务器会自动连接并把工具并入工具列表。详见 [`docs/EXTENSIONS.md`](docs/EXTENSIONS.md)。

## MCP 服务端 / 嗅探客户端示例（WPF 桌面版）

一组完整的 MCP 工具链路示例，**两个项目均为 WPF 应用**：

```bash
# 项目 1：WPF 工具服务（自动注册到本机注册表，暴露 8 个 AI 可调用工具）
# 界面含：运行状态、端点复制、工具目录、实时请求日志、内置自测调用
dotnet run --project samples/AiTools.Samples.McpToolServer

# 项目 2：WPF 嗅探客户端（列出本机服务 → 连接 → 浏览工具 → 调用）
# 界面含：自动刷新的服务列表、动态切换、Schema 驱动参数表单、资源/提示词/日志
dotnet run --project samples/AiTools.Samples.McpExplorer
```

项目 1 同时支持 MCP Streamable HTTP 与 stdio 两种传输，并提供 `--headless` 无窗口模式供自动化脚本与测试使用；项目 2 支持动态切换多个服务、浏览资源与提示词。详见 [`docs/MCP_SAMPLES.md`](docs/MCP_SAMPLES.md)。

## 配置与可靠性

所有 Options 继承自 `AiHttpOptions`：

```csharp
var options = new OpenAiOptions
{
    ApiKey = key,
    BaseAddress = new Uri("https://my-gateway.example.com/v1"), // 反代 / 网关
    Timeout = TimeSpan.FromSeconds(180),   // 流式读取的总时长
    MaxRetries = 5,                         // 仅对 429/5xx/网络错误重试
    RetryBaseDelay = TimeSpan.FromSeconds(1),
    HttpClient = sharedHttpClient,          // 复用连接池，建议设置 Timeout = Infinite
    DefaultHeaders = new Dictionary<string, string> { ["X-Trace-Id"] = "abc" }
};
```

异常体系：

```
AiToolsException
└─ AiServiceException            (ProviderName / StatusCode / ErrorCode / ResponseBody / IsTransient)
   ├─ AiAuthenticationException  (401 / 403)
   ├─ AiRateLimitException       (429，带 RetryAfter)
   ├─ AiInvalidRequestException  (400 / 404 / 413 / 422)
   └─ AiServerException          (5xx)
```

```csharp
try
{
    await client.Chat.CompleteAsync(request);
}
catch (AiRateLimitException ex)
{
    Console.WriteLine($"限流，建议 {ex.RetryAfter} 后重试");
}
catch (AiServiceException ex)
{
    Console.WriteLine($"{ex.ProviderName} 调用失败：{ex.Message}（{ex.StatusCode} / {ex.ErrorCode}）");
}
```

重试规则：`429` 优先采用服务端 `Retry-After`，`5xx` 与网络错误按指数退避（含抖动、上限 30 秒）重试，`4xx` 立即失败。流式请求的超时覆盖整个流的读取时长；取消 `CancellationToken` 会立即中断流并释放连接。

### 流式聚合与中断

```csharp
// 边流式输出边收集
await foreach (var update in client.Chat.StreamAsync(request, cancellationToken))
{
    Console.Write(update.TextDelta);
}

// 或一步聚合
var full = await client.Chat.StreamAsync(request).AggregateAsync();
```

## 设计说明

- **统一抽象不牺牲厂商能力**：`ChatRequest.AdditionalParameters`（JSON 对象，浅合并进请求体）透传任意厂商特有参数；`ChatResponse.RawJson` 保留原始响应；`ToolCall.ProviderData` 承载需原样回传的私有数据（如 Gemini 的 `thoughtSignature`）。
- **工具调用循环**在 `ChatClientExtensions` 中实现，Provider 只需正确实现单轮 `CompleteAsync`/`StreamAsync`。
- **流式解析**统一走 `SseParser`（SSE）与 `ParseNdJsonAsync`（NDJSON），不同厂商的增量结构在 Provider 内映射为 `ChatStreamUpdate`。

## 构建

```bash
dotnet build AiTools.slnx
dotnet test tests/AiTools.Tests/AiTools.Tests.csproj
dotnet run --project samples/AiTools.Samples.Console            # 全部演示
dotnet run --project samples/AiTools.Samples.Console -- offline # 无需 API Key
```

示例程序按环境变量自动选择 Provider：

| 环境变量 | 用途 |
|---|---|
| `OPENAI_API_KEY` / `OPENAI_BASE_URL` / `OPENAI_CHAT_MODEL` | OpenAI 或兼容端点 |
| `ANTHROPIC_API_KEY` / `ANTHROPIC_CHAT_MODEL` | Anthropic |
| `GEMINI_API_KEY` / `GEMINI_CHAT_MODEL` | Gemini |
| `MCP_SERVER_COMMAND` / `MCP_SERVER_ARGS` | 启动本地 MCP 服务器 |

## 项目结构

```
src/
  AiTools.Abstractions/   统一模型、接口、异常、工具调用循环
  AiTools.Core/           HTTP、SSE、重试、错误映射、Schema 构建器
  AiTools.OpenAI/         OpenAI 与兼容端点（聊天/嵌入/图像/音频）
  AiTools.Anthropic/      Claude Messages API
  AiTools.Gemini/         Gemini（聊天/嵌入/图像/音频）
  AiTools.Mcp/            MCP 客户端（stdio / Streamable HTTP / HTTP+SSE）
  AiTools.Skills/         Agent Skills（技能发现 + 渐进式披露）
  AiTools.Extensions/     Claude Code 兼容插件（清单 / 斜杠命令 / 插件技能 / MCP 声明）
  AiTools.Wpf/            WPF 设置页与对话页
tests/
  AiTools.Tests/          301 个单元与集成测试（假 HTTP 处理器 + 真实进程端到端）
plugins/
  workflows/              /goal 与 /plan 插件（Claude Code 格式）
samples/
  AiTools.Samples.Console/        可运行的演示程序
  AiTools.Samples.McpDiscovery/   本机 MCP 服务注册表（共享）
  AiTools.Samples.McpToolServer/  MCP 工具服务端示例（项目 1，WPF）
  AiTools.Samples.McpExplorer/    MCP 嗅探/调试客户端示例（项目 2，WPF）
  AiTools.Samples.McpAgents/      多 agent MCP 配置管理共享库
  AiTools.Samples.McpAgentManager/ 多 agent MCP 管理器示例（WPF）
  AiTools.Wpf.Demo/               WPF 组件演示（含示例技能）
docs/
  ARCHITECTURE.md         分层设计
  CAPABILITY_MATRIX.md    各厂商能力对比
  PROVIDER_CONTRACT.md    Provider 实现契约
  WPF.md                  WPF 组件集成指南
  MCP_SAMPLES.md          MCP 服务端 / 嗅探客户端示例说明
  SKILLS.md               Agent Skills 集成指南
  EXTENSIONS.md           插件框架与斜杠命令指南
  SESSIONS.md             会话恢复与分叉指南
  MCP_AGENT_MANAGER.md    多 agent MCP 管理器说明
```

## 许可证

MIT
