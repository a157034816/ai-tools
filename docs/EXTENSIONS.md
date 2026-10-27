# Extensions（可编程扩展）

## 三个容易混淆的名字

`AiTools.Extensions` 里现在同时存在三种东西，名字相近但完全不同，先分清它们：

| 名称 | 形态 | 加载方式 |
|---|---|---|
| **Extension**（代码扩展） | C# 类实现 `IExtension` | 宿主直接引用（`AiToolsWpfOptions.Extensions`）**或**来自扩展包 |
| **Extension Package**（扩展包） | 磁盘目录 + 根部的 `extension.json` | `ExtensionWorkspace` 扫描 / 构建 / 热重载；C#、JS、TS 三种 `kind` |
| **Plugin**（Claude Code 插件） | `plugin.json` + `commands/*.md` + `skills/*/SKILL.md` + `.mcp.json` + `output-styles/*.md` + `workflows/*.js` | `PluginCatalog` / `PluginHost` |

关键区别：

- **Plugin 是 Claude Code 兼容层，不是扩展生态。** 它加载的是别人已经写好的 Markdown 插件包，扩展的是提示词与命令目录；它不能注册 C#/JS 代码、不能持有会话状态、不参与热重载。
- **Extension Package 是扩展的分发形式。** 一个包最终产出的仍然是 `IExtension` 实例（C# 或脚本），所以「包」与「扩展」不是并列关系：包是外壳，扩展是内容。
- **Extension 是运行时概念。** 直接 `new MyExtension()` 传给宿主、或写进 `AiToolsWpfOptions.Extensions`，与从包里加载得到的对象在运行时完全等价。

本页其余部分讲 Extension 运行时与 Plugin 兼容层；**如何写一个扩展包**见
[`EXTENSIONS_AUTHORING.md`](EXTENSIONS_AUTHORING.md)。

## 两层调用面

| 层 | 形态 | 适合 |
|---|---|---|
| **Extensions**（本页主述） | C# 类实现 `IExtension`，可注册命令、工具、事件处理器、持有会话状态 | 需要状态、工具、拦截、生命周期的工作流 |
| **Plugins**（见下节） | Claude Code 插件目录：`plugin.json` + `commands/*.md` + `skills/` + `.mcp.json` + `output-styles/` + `workflows/` | 分发既有的 Claude Code 插件包 |

设计取向来自 Pi：**扩展是可执行行为，不是提示词片段**。因此 `/goal` 与 `/plan` 不是两段 Markdown，而是带状态、工具与跨轮次注入的扩展。工具可达性、输出风格、工作流与权限/信任同样是运行时的一部分，不是提示词约定。

## 运行时结构

```
ExtensionPipeline          宿主唯一入口：命令目录、工具表、会话、输出风格、权限、状态导入导出
├── ExtensionRuntime       注册表 + 事件分发（Pi 的 pi.on / registerTool / registerCommand）
│   ├── ExtensionCommand   /goal 这类代码命令
│   ├── ExtensionTool      模型可调用，带曝光级别与可达范围
│   ├── ToolLoadout        装载结果：Declared / Callable / All + 准备器改写
│   ├── ToolSearchTool     内置 tool_search：查找并激活 Codemode/Deferred 工具
│   ├── CodemodeTool       内置 codemode：Jint 沙箱中的 JavaScript 工具脚本
│   └── ExtensionSession   session_start / session_shutdown、清理回调
├── ExtensionStateStore    按扩展名分桶的 JSON 状态，可选持久化
├── CommandCatalog         扩展命令 + 插件命令的统一解析
├── OutputStyleCatalog     插件/宿主输出风格汇总，SelectedStyle 改写系统提示
├── PermissionPolicy       规则引擎（deny > ask > allow）+ PermissionGate 闸门
│   └── ProjectTrust       加载项目插件前判定 Trusted / Untrusted
├── Workflows              Jint 沙箱里的 JS 工作流 → 斜杠命令 + workflow_list 工具
├── PluginHost             Claude Code 插件的渲染与 MCP 连接
└── Packages               磁盘扩展包子系统（见 EXTENSIONS_AUTHORING.md）
    ├── ExtensionWorkspace      扫描根目录、依赖排序、热重载、文件监视、Git 源码
    ├── ExtensionPackageLoader  单包加载：清单校验 → 构建/转译 → 扩展实例
    ├── ExtensionPackageBuilder C# 构建到 bin/ext/<ticks>；脚本走 IScriptExtensionProvider
    ├── ExtensionAssemblyLoader 可回收 AssemblyLoadContext 加载程序集
    ├── ExtensionSourceManager  git clone / pull --ff-only / remove
    └── IScriptExtensionProvider 脚本提供者接缝（实现位于 AiTools.Extensions.Scripting）
```

## 编写一个扩展

```csharp
public sealed class CounterExtension : IExtension
{
    public string Name => "counter";              // 同时是状态桶名
    public string? Description => "Counts mentions.";

    public void Activate(IExtensionApi api)
    {
        api.RegisterCommand("count", "统计提及次数", async (ctx, args, ct) =>
        {
            var n = ctx.State.Get<int>(Name, "n") + 1;
            ctx.State.Set(Name, "n", n);
            return CommandResult.Display($"第 {n} 次");
        });

        api.RegisterTool(
            ToolDefinition.FromJsonSchema("counter_bump", "Bump the counter.", """{"type":"object","properties":{}}"""),
            (call, ctx, ct) =>
            {
                var n = ctx.State.Get<int>(Name, "n") + 1;
                ctx.State.Set(Name, "n", n);
                return Task.FromResult($"count={n}");
            });

        // 每轮把状态回灌到系统提示。
        api.On<BeforeAgentStartEvent>(ExtensionEvent.BeforeAgentStart, (e, ctx, ct) =>
        {
            e.PromptSections.Add($"## Counter\n\ncount={ctx.State.Get<int>(Name, "n")}");
            return Task.FromResult<BeforeAgentStartEvent?>(null);
        });
    }
}
```

注册：

```csharp
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    Extensions = new IExtension[] { new CounterExtension(), new GoalExtension(), new PlanExtension() }
});
```

### provider 级钩子如何生效

Provider 层通过 `AiHttpOptions.Observer`（`IAiHttpObserver`）暴露接缝，`ExtensionPipeline.CreateHttpObserver()` 把它桥到扩展事件：

```csharp
var observer = pipeline.CreateHttpObserver(() => context);
var client = new OpenAiChatClient(new OpenAiOptions
{
    ApiKey = key,
    BaseAddress = new Uri("https://api.openai.com/v1"),
    Observer = observer        // BeforeProviderHeaders / BeforeProviderRequest / … 生效
});
```

WPF 宿主在构造聊天客户端时自动挂接（自定义 `IProviderFactory` 仍然优先，此时观察器不注入）。`BeforeProviderRequest` 抛 `AiRequestBlockedException` 会让请求不发出。

### 扩展界（`ctx.Ui`）

```csharp
if (ctx.Ui is { HasDialogs: true })
{
    if (!await ctx.Ui.ConfirmAsync("确认", "允许修改这个文件？")) return CommandResult.Noop;
}

var choice = await ctx.Ui.SelectAsync("选择环境", new[] { "staging", "production" });
ctx.Ui.SetStatus("正在部署…");
```

WPF 侧 `WpfExtensionUi` 把通知接到状态栏，`ConfirmAsync` / `InputAsync` / `SelectAsync` 使用模态窗口，`ChooseAsync` 交给可替换的提问呈现策略（默认内联面板）；无 `Application` 时确认/输入/选择退化为非交互（确认返回默认值，输入/选择返回 null）。宿主不提供 UI 时 `ctx.Ui` 为 null，扩展必须处理。

需要在选项上附带说明时，让 UI 实现可选的 `IExtensionChoiceUi.ChooseAsync(title, message, choices)`：`choices` 是 `ExtensionChoice { Label, Description }` 列表，返回 `ExtensionChoiceResult { Index, Label, Description }`（取消为 null）。调用方应检测接口可用性并退化到 `SelectAsync`，`ask` 工具与脚本侧的 `ctx.ui.choose` 都是这样做的。

WPF 宿主把"怎么呈现"抽成了可替换的策略（`AiTools.Wpf.Asking.IAskPresenter`）：

| 实现 | 行为 |
|---|---|
| `InlineAskPresenter`（默认） | 交给 `InlineAskHost`，由对话页在**输入框上方**绘制面板；`ChatViewModel.AskHost` / `CurrentAskPrompt` / `SelectAskOptionCommand` / `CloseAskCommand` 就是它的绑定面 |
| 自定义 | 设置 `AiToolsWpfOptions.AskPresenter`；可以是模态窗口、控制台提示，或直接返回固定答案（自动化测试） |

配置了呈现策略后 `WpfExtensionUi.HasDialogs` 恒为 true——内联面板由视图绘制，不依赖 WPF `Application`，因此无窗口的宿主（含单元测试）也能正常提问。

### 命令与工具上下文（`CommandContext` / `ToolContext`）

| 成员 | 命令上下文 | 工具上下文 | 行为 |
|---|---|---|---|
| `WorkingDirectory` | `CommandContext.WorkingDirectory` | `ToolContext.WorkingDirectory` | Runtime 的显式上下文分发会复制命令工作目录；简化命令调用重载新建上下文时使用 `Environment.CurrentDirectory`。 |
| `ExecuteToolAsync(...)` | `CommandContext.ExecuteToolAsync` | `ToolContext.ExecuteToolAsync` | 命令上下文调用简化重载；工具上下文嵌套调用优先沿用 `CallContext`，否则从自身 `WorkingDirectory` 创建调用上下文。 |

具体而言，从命令处理器调用 `ctx.ExecuteToolAsync` 会进入不接收 `CommandContext` 的简化重载，因此目标工具看到 `Environment.CurrentDirectory`；从工具处理器嵌套调用时，`ToolContext.ExecuteToolAsync` 沿用 `CallContext`，继承 `ToolContext.WorkingDirectory`。脚本 `ctx.workingDirectory` 遵循相同规则：命令处理器看到会话工作目录，工具处理器看到实际传入的工具上下文目录。若工具要相对项目目录读文件，请通过参数显式传入目录或使用包根，不要在工具可能由命令调用时依赖 `ctx.workingDirectory`。

### 自定义条目（`appendEntry`）

```csharp
ctx.AppendEntry("checkpoint", new { step = 3, note = "after refactor" });
var last = ctx.ReadEntry("checkpoint");
```

条目写入扩展状态桶（键前缀 `entries.`），随 `ConversationRecord.ExtensionState` 持久化，**不进入模型上下文**——对应 Pi 的 `appendEntry`。

### provider / 虚拟模型 / MCP 注册

```csharp
api.RegisterProvider(new ExtensionProviderRegistration
{
    Name = "router-local", Protocol = "openai",
    BaseAddress = new Uri("http://localhost:1234/v1"), ApiKey = "local", DefaultModel = "qwen"
});

api.RegisterVirtualModel(new VirtualModelRegistration
{
    VirtualModelName = "smart", Provider = "router-local", TargetModel = "qwen",
    Rewrite = request => request          // 可选：进一步改写请求
});

var unregister = api.RegisterMcpServer(new PluginMcpServer
{
    Name = "db", Transport = PluginMcpTransportKind.Stdio,
    Command = "npx", Arguments = new[] { "-y", "db-server" }
});
```

- `RegisterProvider`：`ExtensionProviderRouter` 在创建聊天客户端时优先匹配它（协议复用内置 OpenAI/Anthropic/Gemini 客户端）。
- `RegisterVirtualModel`：用户选择 `smart` 时，请求被改写为 `qwen` 发往 `router-local`。
- `RegisterMcpServer`：`ExtensionPipeline.StartAsync` 连接后把工具登记为 `ext_<server>_<tool>`；失败只记 `Warning` 诊断，`unregister` 可移除声明。

### 工具渲染

```csharp
api.RegisterToolRenderer(ctx => ctx.ToolName == "goal_update" && !ctx.IsResult
    ? "🎯 更新目标进度"
    : null);   // 返回 null 交给下一个渲染器，最后回退到默认 chip
```

WPF 对话页用渲染结果替换默认的 `🔧 name(args)` 标签。

### 热重载

```csharp
var diagnostics = await pipeline.ReloadAsync();
```

流程：关闭扩展注册的 MCP 连接 → `session_shutdown` → 清空注册表 → 重新 `Activate` → 重新 `session_start`。状态保留（同一个 `ExtensionStateStore`），旧 `Session` 上下文失效。

`Activate` 只在构造管道时调用一次，**必须快速返回且不启动进程或定时器**（与 Pi 的工厂约束一致）；长生命周期资源放到 `SessionStart`，并在 `SessionShutdown` 里幂等释放。

## 事件

| 事件 | 语义 | 载荷 / 可做的事情 |
|---|---|---|
| `SessionStart` | 会话开始 | `SessionStartEvent`：启动资源、读会话状态 |
| `SessionShutdown` | 会话关闭 | 释放资源；处理器必须幂等 |
| `Input` | 用户输入 | `InputEvent`：改写 `Text` 或 `Suppress` 吞掉 |
| `BeforeAgentStart` | 请求发出前 | `BeforeAgentStartEvent`：追加 `PromptSections`、`PrependMessages` |
| `Context` | provider 请求前 | `ContextEvent`：过滤/改写消息（系统提示不受影响） |
| `ContextWithSystem` | provider 请求前 | `ContextEvent`：同时改写 `SystemPrompt` |
| `ToolCall` | 工具调用前 | `ToolCallEvent`：改写 `ArgumentsJson`、`BlockReason` 拦截；覆盖所有分发路径（含 `codemode` 脚本的嵌套调用与 `ctx.ExecuteToolAsync`） |
| `ToolResult` | 工具返回后 | `ToolResultEvent`：脱敏/截断 `Text` |
| `ToolExecutionStart` / `Update` / `End` | 工具生命周期 | `ToolExecutionEvent`：观察参数、增量输出、耗时 |
| `BeforeProviderHeaders` | 请求头构造后 | `ProviderHeadersEvent`：就地改写请求头 |
| `BeforeProviderRequest` | 请求体序列化后 | `ProviderRequestEvent`：改写 `BodyJson` 或 `BlockReason` 阻止发出 |
| `AfterProviderResponse` | 收到响应头 | `ProviderResponseEvent`：状态码、长度、耗时 |
| `ProviderStreamEvent` | 每个流事件 | `ProviderStreamEvent`：原始 SSE/NDJSON 数据（只读） |
| `TurnEnd` | 一轮结束 | `TurnEndEvent`：`Continue` + `ContinuationPrompt` 请求追加一轮 |

组合语义（与 Pi 一致）：

- 处理器按**注册顺序**执行。
- 转换类事件链式传递：后一个看到前一个的修改。
- **拦截类 fail-closed**：`ToolCall` 处理器抛异常时按拒绝执行处理，不会放行。
- 通知类（`SessionStart` 等）的异常被吞掉，不打断对话。
- `pi.on` 的等价物返回一个取消订阅的 `Action`。

## 工具：曝光、激活集合与命名空间

工具的可达性由三件事共同决定：**曝光级别**、**激活集合**、**装载准备器**。声明面（进入模型请求的工具清单）与可调用面（其它工具经 `ExecuteToolAsync` 能调到的工具）是两套判定。

| 曝光 | 声明给模型 | 可被其它工具调用 | 登记时的激活 |
|---|---|---|---|
| `Direct`（默认） | ✅ | ✅（需在激活集合中） | 自动激活 |
| `ModelOnly` | ✅ | ❌（编排/提问型工具） | 自动激活 |
| `Codemode` | 激活后 ✅ | ✅（登记即可调用） | 不激活 |
| `Deferred` | 激活后 ✅ | ✅（登记即可调用） | 不激活 |
| `Hidden` | ❌ | ❌ | 不激活，激活是空操作 |

- `Codemode` 与 `Deferred` 的执行语义相同（登记即可被调用），区别只在声明意图：`Codemode` 面向由 `codemode` JavaScript 脚本调用并列入其描述的工具，`Deferred` 面向只在需要时才被发现的工具。`tool_search` 两类都搜。
- 激活：`pipeline.ActivateTool(name)` 或 `pipeline.SetActiveTools(names)`（整体替换）。未登记的名字被忽略，`Hidden`/`Direct`/`ModelOnly` 的激活请求返回 false 且无副作用。
- `Hidden` 用于撤回：登记后既不声明也不可调用；撤回已有工具用 `DeactivateTool`，只从声明面移除用装载准备器的 `HiddenDeclarations`。
- `pipeline.BuildToolRegistrations()` 只返回 `Declared`；运行时的 `ExecuteToolAsync` 判定 `Callable`。

### 命名空间与分组

```csharp
api.RegisterNamespace(new ToolNamespace { Name = "mcp__docs", Aliases = new[] { "docs" }, Description = "文档库" });
api.RegisterTool(def, execute, ToolExposure.Direct, @namespace: "mcp__docs");
```

命名空间名与别名在比较时归一到小写且 `-` 与 `_` 等价：`my-docs` ≡ `my_docs`。`ctx.ToolGroups()`（或 `Runtime.ToolGroups()`）按命名空间分组返回可调用工具，未分组的工具单独成组。

### `ToolReach`：一次调用能触达什么

编排型工具用 `reachesTools` 声明自己一次性能够到的工具范围：

```csharp
api.RegisterTool(def, execute, ToolExposure.ModelOnly, reachesTools: ToolReach.Words("mcp__docs", "grep"));
```

`ToolReach.All()` 不限制；`ToolReach.Words(...)` 只允许命中词的工具。词匹配工具名、工具所属命名空间的名称与别名（包含关系，大小写与分隔符差异不敏感）。嵌套调用时父工具与调用者声明的范围都会被检查，越界的调用返回 `{"error": "Tool 'x' is outside the reach of 'y'."}`。

### `prepareLoadout`：装载准备器

```csharp
api.RegisterLoadoutPreparer(loadout =>
{
    loadout.Descriptions["huge_tool"] = "按需检索文档。";        // 只改声明用的描述
    loadout.HiddenDeclarations.Add("internal_trace");            // 已激活但不声明
    return loadout;
});
```

- 准备器按注册顺序链式执行，每个准备器拿到上一环结果的副本；返回新对象或就地改写都行。
- `Descriptions` 只作用于声明面，可调用面与 `All` 不考虑它。
- `HiddenDeclarations` 里的工具**保持激活且仍可被其它工具调用**，只是不进模型请求的工具清单——这是"撤回声明但保留编排能力"的手段。
- 准备器抛异常时整体丢弃它这次的改写，装载回退到它之前的结果，不影响后续准备器。

### 调用路径

- 模型调用 → 宿主 → `ToolCall` 事件 → `InvokeToolAsync` → `ToolResult` 事件。
- 扩展调用扩展 → `ctx.ExecuteToolAsync(name, json)`，走同一条事件链（`Runtime.ExecuteToolAsync`）。
- 宿主已经自行分发过事件时用 `Runtime.InvokeToolAsync`，避免重复触发。

单个工具执行抛异常会变成模型可见的错误结果；`ToolCall` 被拦截时返回 `{"error": "<reason>"}`。

### 两个内置编排工具

两者都由运行时自动登记为 `ModelOnly`（声明给模型，不可被其它工具调用）；`tool_search` 声明 `ToolReach.All()`，`codemode` 则在每次运行时从脚本推导可达范围。

**`tool_search(query, activate?)`** —— 只在存在 `Codemode` 或 `Deferred` 工具时登记。

| 参数 | 语义 |
|---|---|
| `query` | 大小写不敏感的子串，匹配工具名、命名空间名/别名、描述；空串匹配全部候选 |
| `activate` | 默认 true：把**前 10 个**命中项加入激活集合，下一轮即声明给模型 |

返回每个命中的 `{name, namespace, exposure, description, activated}`。它只激活，不执行。

**`codemode({script: string, timeoutMs?: integer})`** —— 只在存在 `Codemode` 工具、或某个工具声明了 `ToolReach` 时登记。它在一次性 Jint 沙箱中执行 JavaScript，脚本末尾表达式或顶层 `return` 的值作为结果。

```json
{
  "script": "const match = tools.find(tool => tool.name === 'mcp__docs__search'); return match ? await executeTool(match.name, { q: 'auth' }) : null;",
  "timeoutMs": 10000
}
```

- 沙箱提供 `tools`（本次可达且可调用工具的 `{name, namespace, description}` 清单）、`executeTool(name, args)`、`describeNamespace(name)`（返回命名空间 `Instructions`，未知时为空串）与默认无操作的 `log(message)`。
- `executeTool` 嵌套调用经过完整的 `tool_call`/`tool_result` 事件链，错误会变成可由脚本捕获的 JavaScript 异常；嵌套调用不添加对话记录。
- `reachesTools` 使用本地 token-scan 启发式：出现 `tools` 标识符、精确的 `tool_search` 标识符/字符串字面量，或 `<本次可调用工具所属的已知命名空间|别名>.list|.search|.find|.tools` 时推导为 `ToolReach.All()`；否则标识符/字符串字面量与可调用工具名、命名空间名/别名做整 token 匹配，推导为 `ToolReach.Words(...)`。结果再与配置的 `reachesTools` 和父工具可达范围取交集。这不是安全边界；例如 `globalThis["tools"]` 不会触发列举规则。
- `nestedCalls` 记录只随本次调用结果返回，不跨调用保留；单次最多执行 64 次嵌套调用，第 65 次尝试会中止并报告 `failedCall: 64`。参数超过 8 KiB 会从记录省略；工具结果超过 32 KiB 不写入记录并令 `complete` 为 false。记录始终不含工具结果。
- `prepareLoadout` 动态把 `Codemode` 曝光工具名加入 codemode 描述。此运行时没有 per-tool `usage` 统计，也未提供 Pi 的 `parentToolCallId`/斜线形式调用 ID 与 `onUpdate` 回调。

## 会话状态

`ExtensionStateStore` 按扩展名分桶，值以 JSON 保存：

```csharp
ctx.State.Set(Name, "plan", myPlanState);        // 强类型写入（序列化）
var plan = ctx.State.Get<PlanState>(Name, "plan"); // 不存在时返回 default
if (ctx.State.TryGet<int>(Name, "n", out var n)) { /* 区分缺失与 0 */ }
```

分层（对照 Pi 的状态建议）：

| 需求 | 位置 |
|---|---|
| 跟随对话分支、不进模型上下文 | `ExtensionStateStore` + `ConversationRecord.ExtensionState` |
| 要让模型看到 | `BeforeAgentStart` 注入 `PromptSections` |
| 要出现在对话记录里 | `ctx.AppendMessage(...)` |
| 跨会话保存 | 宿主自行持久化 `ExportState()` 的结果 |

WPF 宿主在每轮结束、命令执行、工具调用后写入 `ConversationRecord.ExtensionState`；`/resume` 恢复、`/fork` 复制、`/new` 清空。

## 内置扩展

### `/ask`（`AskExtension`，工具 `ask`）

通用提问工具：模型调用时传入一个 `title`、可选的 `message`，以及至少两个带 `description` 的选项，
宿主弹出对话框让用户拍板，工具返回被选中的那一条。它不属于任何工作流——任何需要用户决策的场合都可以调用，
`/plan` 只是把它当作记录计划前的前置步骤之一。

| 宿主能力 | 行为 |
|---|---|
| 实现 `IExtensionChoiceUi` | 交给宿主的提问呈现策略；WPF 默认在输入框上方叠一个内联面板 |
| 只实现 `IExtensionUi` | 退化为 `SelectAsync`，说明拼进选项标签 |
| 没有任何 UI | 返回 `unavailable`，模型改为在对话里直接提问 |

用户关闭对话框时返回 `cancelled`，模型应停下等待下一条消息而不是自行猜测。其它扩展可以用
`AskExtension.ParseResult(json)` 解析工具返回值，判断是否真的得到了答案。

### `/goal`（`GoalExtension`）

| 交互 | 行为 |
|---|---|
| `/goal <目标>` | 覆盖当前目标并发出指令提示词 |
| `/goal` | 显示当前目标与进度；无目标时给出用法 |
| `goal_plan` 工具 | 记录 objective、criteria、steps、assumptions |
| `goal_update` 工具 | `completeStep` / `proveCriterion` + **必填** `evidence` / `blocker` |
| 每轮 | 注入 `## Session goal`：标准勾选状态、`N/M steps`、假设、行为约束 |

没有证据就不能把标准标记为完成——这是刻意的：目标完成的判据是可观察的证据，不是模型的自述。

### `/plan`（`PlanExtension`）

| 交互 | 行为 |
|---|---|
| `/plan <任务>` | 重置计划并要求先取证再出计划 |
| `/plan` | 显示当前计划与完成度；无计划时给出用法 |
| `plan_save` 工具 | 记录 context、steps（每步必须有 `verify`）、risks、outOfScope、openQuestions |
| `plan_step` 工具 | `completeStep` / `approve` |
| 前置提问 | 任务留有影响实现的开放决策时，先调用通用的 `ask` 工具收敛，再记录计划 |
| 每轮 | 注入 `## Implementation plan`；未确认时明确写入 "NOT approved" |

## Workflows（JS 工作流）

插件可以把 JavaScript 工作流放在 `workflows/*.js`（默认目录，不递归），或在 manifest 用 `workflows` 声明文件/目录。每个工作流成为一个斜杠命令（`/<name> <arguments>`），返回值以 `CommandResult.Display` 显示、不触发模型轮次；同时在存在工作流时登记一个 `workflow_list` 工具（`ModelOnly`，列出可用的名称与参数）。

```javascript
export const meta = {
  name: 'review',                       // 缺省时回退为文件名
  description: '审查改动并给出结论',
  arguments: '审查 $ARGUMENTS',          // 参数模板，支持 $ARGUMENTS/$1/$name
  parameters: ['scope']
};

const files = await agent('列出这次改动的文件，只回 JSON 数组', {
  label: 'collect',
  schema: { type: 'array', items: { type: 'string' } }   // 要求 JSON 输出并校验
});

const notes = await pipeline(files, async (file, index) => {
  const text = readFile(file);          // 同步读取，缺失时返回空串
  return text.length === 0 ? null : await agent(`审查 ${file}`, { label: `file-${index}` });
});

return notes;
```

脚本在 **Jint 4.17.0** 沙箱里执行，每次运行都新建 `Engine`，全局状态不跨运行泄漏。可用的全局刻意保持很少：

| 全局 | 语义 |
|---|---|
| `agent(prompt, options?)` | 调用一次模型并返回助手文本；`options.schema` 存在时要求 JSON 输出并校验（返回解析后的对象），`options.label` 只用于日志 |
| `pipeline(items, fn)` | 按顺序串行执行 `await fn(item, index)`，**丢弃 null/undefined**，返回结果数组 |
| `tool(name, args)` | 调用宿主注册的工具；**先过 `PermissionPolicy`**，Deny 或未获批准的 Ask 返回错误而不执行 |
| `readFile(path)` | 同步读取工作目录内的文本文件，缺失返回空串 |
| `readSkill(name)` | 读取 skill 正文；未配置或不存在时返回空串 |
| `log(msg)`、`console.*` | 写入 `WorkflowRuntimeOptions.Log`，不写 stdout |
| `meta` | 元数据对象（name、description、parameters、plugin、source） |
| `arguments` | 调用者参数文本（适配器已渲染占位符） |

边界与限制：

- `MaxSteps`（默认 64）限制 `agent` + `tool` 的**总调用次数**；`Timeout`（默认 60 秒）同时是 JS 引擎的执行上限，`while(true){}` 也会被打断。
- `eval` / `Function` 动态编译被禁用，递归深度、语句数都有上限。
- 脚本里的 `import` / `export … from` 会被加载器直接拒绝：沙箱不加载外部模块。单文件上限 200 KB。
- `readFile` 先按路径段拒绝 `..`（`a..b.txt` 这类名字合法），再把两侧路径逐段解析链接目标后重新做包含检查——根内的 junction/symlink 不能把读取引到根外。链接解析依赖 `File.ResolveLinkTarget`（.NET 6+）；`netstandard2.0` 目标下只有词法检查，该目标下的宿主必须自行保证工作目录里没有逃逸链接。
- **它不是安全边界**：脚本仍可通过 `tool` / `readFile` 触达宿主能力，沙箱只约束计算与资源消耗。真正的拦截在 `PermissionPolicy`。

## 输出风格

输出风格是带 frontmatter 的 Markdown，正文追加到系统提示，改变措辞与回复结构。来源有两处：插件的 `output-styles/<name>.md`（默认目录）或 manifest 的 `outputStyles`（目录或单个 `.md` 文件，声明后取代默认目录），以及宿主经 `OutputStyleCatalog.FromStyles` 注册。

```markdown
---
name: concise
description: 一段话说完，不列清单。
keep-coding-instructions: true
---

回答不超过一个自然段；先给结论，再给必要的原因。
```

| 字段 | 语义 |
|---|---|
| `name` | 风格名；缺失时取文件名 |
| `description` | 面板与 `/output-style` 列表中的说明；缺失时取正文首个非空行 |
| `keep-coding-instructions` | 是否保留宿主的编码指令，默认 false |
| 正文 | 追加到系统提示的内容 |

`OutputStylePrompt.Apply(style, baseSystemPrompt)` 的语义：宿主提示**原样保留在最前面**，其后是 `## Output style: <name>` 标题与正文；`keep-coding-instructions: false` 时额外写入一行 `Coding instructions: suspended` 作为标记，**从不删除宿主的提示**——是否真的丢弃编码指令由宿主决定。

管道侧：`pipeline.OutputStyles` 是汇总后的目录，`pipeline.SelectOutputStyle(name)` 切换（未知名称清空选择）。选中风格后，`PrepareTurnAsync(..., baseSystemPrompt)` 返回的 `SystemPromptOverride` 是"宿主提示 + 风格标题 + 正文"的完整结果，`SystemPromptSections` 则是该结果后面再拼上扩展注入的段落；WPF 宿主按 `SystemPromptOverride` 替换本轮系统提示。`/output-style` 列出可用风格，`/output-style <name>` 选择。

## 权限与信任

两层，缺一不可：

1. **fail-closed 后备**（必须）：`ExtensionPipelineOptions.Permissions` 非 null 时，管道自动激活一个内部扩展，把 `PermissionPolicy` 映射到 `ToolCall` 事件——除 `Allow` 外的判定都写入 `BlockReason`（宿主的直接工具调用例外：`Ask` 会交给第 2 层的交互 seam 决定，嵌套调用没有交互通道，`Ask` 即拒绝）。因为 `tool_call` 覆盖所有分发路径（WPF 模型调用、`codemode` 脚本的嵌套调用、`ctx.ExecuteToolAsync`、`Runtime.ExecuteToolAsync`、工作流 `tool()`），这一层无法被绕开。它同时注册装载准备器，把判定为 `Deny` 的工具从模型声明中移除。
2. **交互式确认**（可选）：`pipeline.GuardToolAsync(name, json, execute, confirm)` 在宿主 seam 上把 `Ask` 交给用户；没有 `confirm` 时 `Ask` 按拒绝处理。未配置策略时返回 null，表示宿主直接执行。

规则与判定：

```csharp
var policy = PermissionPolicy.FromRules(new[]
{
    new PermissionRule { Tool = "*",     Decision = PermissionDecision.Allow, Source = "user" },
    new PermissionRule { Tool = "shell", Decision = PermissionDecision.Ask,   Source = "project" },
    // ArgumentPattern 匹配参数 JSON 原文（区分大小写）：只有含 "rm -rf" 的 shell 调用被拒绝。
    new PermissionRule { Tool = "shell", Decision = PermissionDecision.Deny,  Source = "project",
                         ArgumentPattern = "*rm -rf*" },
});
```

- `Tool`：精确名、`*`、或 `<ns>:*` 前缀形式。
- `Decision`：`Allow` / `Ask` / `Deny`。
- `ArgumentPattern`：对参数 JSON 的 glob（只支持 `*`），为 null 时不约束参数。
- 优先级 **deny > ask > allow**；同一优先级内**先出现者获胜**。没有任何规则命中、或工具名未知时返回 `Ask`。
- `PermissionGate.GuardAsync` 返回工具结果，或被拒绝时的 `{"error":"denied"|"not approved","reason":…}` JSON，两种拒绝都不会执行工具本体。

项目信任在**加载项目插件之前**判定：

```csharp
var trust = ProjectTrust.Evaluate(workingDirectory, trustedRoots, requireTrust);
if (trust.IsTrusted) { plugins = PluginCatalog.LoadFromDirectory(directory); }
```

`requireTrust: false` 直接返回 `Trusted`；否则目录必须位于某个受信任根之下（含根本身）。比较不区分大小写、归一化分隔符、拒绝 `..` 逃逸；`C:/a` 不会信任 `C:/ab`。不受信任时返回 `Untrusted`，并填好 `Reason` 与 `SkippedSources`。WPF 宿主把它接在 `PluginHost` 的首次发现处，结果放在 `AiToolsWpf.LastTrustDecision` 供 UI 显示。

## Claude Code 插件（声明式层）

同一套目录约定仍然支持：`.claude-plugin/plugin.json`、`commands/*.md`、`skills/<name>/SKILL.md`、`.mcp.json`、`output-styles/*.md`、`workflows/*.js`。

```csharp
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    PluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins")
});
```

命令正文支持 `$ARGUMENTS`、`$ARGUMENTS[N]`、`$N`、`$name`、`\$1` 字面量、`${CLAUDE_PLUGIN_ROOT}`/`${CLAUDE_SKILL_DIR}`/`${CLAUDE_PROJECT_DIR}`/`${CLAUDE_SESSION_ID}`/`${CLAUDE_PLUGIN_DATA}`，以及 `` !`cmd` `` / ```` ```! ```` 动态注入（单遍替换，输出不会被再次扫描）。

### 命令优先级

扩展命令先登记，插件命令随后登记，**同名的全名精确命中的优先**：

| 输入 | 命中 |
|---|---|
| `/goal` | 扩展命令 `goal`（全名与短名相同，精确命中） |
| `/workflows:goal` | 插件命令 `workflows:goal` |
| 短名冲突时 | 面板插入限定名，例如 `/workflows:goal ` |

### 插件侧限制

`agents`、`hooks`、`lspServers`、`themes`、`monitors`、`channels`、`userConfig` 仍不执行，加载时以 `Info` 级诊断说明被忽略。`outputStyles` 与 `workflows` 已支持（见「输出风格」与「Workflows」两节）；`allowed-tools` 仍只作为元数据，工具是否放行由 `PermissionPolicy` 决定。

## 宿主集成（WPF）

| 选项 | 含义 |
|---|---|
| `EnableExtensions` | 是否启用可编程扩展，默认 true |
| `Extensions` | 要激活的扩展；null 时使用内置 `GoalExtension` + `PlanExtension`，空数组则禁用 |
| `PluginsDirectory` / `Plugins` | Claude Code 插件目录或已加载的插件 |
| `PluginHostOptions` | 插件命令渲染选项（项目根、超时、短名冲突策略） |
| `AllowPluginShellExecution` | 是否执行插件正文的 `` !`cmd` ``，默认 false |
| `RequireProjectTrust` / `TrustedRoots` | 是否要求项目目录位于受信任根下才加载项目插件，默认 false；判定结果见 `AiToolsWpf.LastTrustDecision` |
| `OutputStyle` | 初始选中的输出风格名；运行时用 `/output-style` 切换 |
| `Permissions` | 工具权限策略；null 时不启用权限闸门 |
| `EnableWorkflows` | 是否激活插件工作流，默认 true |

`ChatViewModel` 侧：`Pipeline`、`Commands`、`TryExecuteCommandAsync`、`AvailableCommands`、`IsCommandPaletteOpen`。宿主级命令 `/resume`、`/fork`、`/new`、`/clear`、`/settings`、`/help`、`/output-style [名称]` 由 ViewModel 直接处理，不发送请求。

### 宿主接线（非 WPF 宿主）

WPF 已内建：`ExtensionPipelineOptions.OutputStyle` / `Permissions` 从同名选项透传，`PrepareTurnAsync` 返回 `SystemPromptOverride`，权限后备扩展自动激活。自建宿主时最小接线：

```csharp
var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
{
    Plugins = catalogs, OutputStyle = "concise", Permissions = policy, WorkingDirectory = cwd
});

// 每轮：把宿主提示传进来，拿回已拼接好的完整提示。
var turn = await pipeline.PrepareTurnAsync(context, model, ct, baseSystemPrompt);
var systemPrompt = turn.SystemPromptOverride ?? baseSystemPrompt;

// 工具调用：ToolCall 事件已兜底，这里只负责把 Ask 变成交互确认。
var text = pipeline.GuardToolAsync(call.Name, call.ArgumentsJson, RunAsync, ConfirmAsync)
            is { } guarded ? await guarded : await RunAsync();

// 加载项目插件前：
var trust = ProjectTrust.Evaluate(cwd, trustedRoots, requireTrust);
```

## 与 Pi 的差异

| Pi | 本项目 |
|---|---|
| TypeScript + `jiti`，扩展是磁盘文件、可自加载 | C# 类，编译期注册；`ReloadAsync` 提供热重载，但**不扫描磁盘**（热加载 C# 扩展仍未实现） |
| `context` / `context_with_system` | 已实现（`ContextEvent`） |
| `before_provider_headers` / `before_provider_request` / `after_provider_response` | 已实现（经 `IAiHttpObserver`） |
| `provider_stream_event` | 已实现（只读；SSE 与 NDJSON 都上报，`[DONE]` 也计入） |
| `tool_execution_start/update/end` | 已实现；`Update` 只有显式上报时才触发（当前内置工具不上报增量） |
| `ctx.ui`（select/confirm/input） | 已实现（`IExtensionUi`；WPF 用模态窗口，无 UI 时退化） |
| `appendEntry`、分支感知状态 | `AppendEntry` 已实现（状态桶 + 会话持久化）；分支重建靠 `/fork` 复制快照 |
| `registerProvider` / `registerMcpServer` / `registerVirtualModel` | 已实现 |
| 工具 render 钩子 | 已实现（`RegisterToolRenderer`，WPF 用于工具 chip） |
| `prepareLoadout` | 已实现（`RegisterLoadoutPreparer`：`Descriptions` / `HiddenDeclarations`，抛异常的准备器被跳过） |
| `namespace` | 已实现（`ToolNamespace` + `RegisterNamespace`；`my-docs` ≡ `my_docs`） |
| `exposure: codemode` / `tool_search` | 已实现（五种曝光 + `ActivateTool`/`SetActiveTools`；内置 `tool_search` 与 JavaScript 沙箱 `codemode`） |
| `workflows` | 已实现（Jint 沙箱、`workflows/*.js`、`workflow_list`） |
| `outputStyle` | 已实现（`output-styles/*.md` 与 manifest `outputStyles`；`/output-style` 切换，正文追加而非替换系统提示） |
| 权限/信任模型（`project_trust`） | 已实现（`ProjectTrust` 判定 + `PermissionPolicy` 规则引擎 + fail-closed 的 `tool_call` 后备 + 可交互的 `GuardToolAsync`） |
| 终端 TUI 组件、themes、monitors、channels | 未实现 |
| `codemode` 的语义 | **部分对齐**：实现了 JavaScript 沙箱、`executeTool`、`describeNamespace` 与嵌套调用事件链；差异：reach 是本地 token/namespace 启发式而非 Pi 的 script-words 规则，`nestedCalls` 仅随单次调用结果返回而非 session-level bounded record，单次最多 64 次嵌套调用；仍无 per-tool `usage` 统计、`parentToolCallId`/`<parent id>/<n>` 调用 ID 或 `{signal, onUpdate}` 桥接 |
