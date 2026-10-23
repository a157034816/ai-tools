# Extensions（可编程扩展）

`AiTools.Extensions` 有两层，二者共用同一个调用面：

| 层 | 形态 | 适合 |
|---|---|---|
| **Extensions**（本页主述） | C# 类实现 `IExtension`，可注册命令、工具、事件处理器、持有会话状态 | 需要状态、工具、拦截、生命周期的工作流 |
| **Plugins**（见下节） | Claude Code 插件目录：`plugin.json` + `commands/*.md` + `skills/` + `.mcp.json` | 分发既有的 Claude Code 插件包 |

设计取向来自 Pi：**扩展是可执行行为，不是提示词片段**。因此 `/goal` 与 `/plan` 不是两段 Markdown，而是带状态、工具与跨轮次注入的扩展。

## 运行时结构

```
ExtensionPipeline          宿主唯一入口：命令目录、工具表、会话、状态导入导出
├── ExtensionRuntime       注册表 + 事件分发（Pi 的 pi.on / registerTool / registerCommand）
│   ├── ExtensionCommand   /goal 这类代码命令
│   ├── ExtensionTool      模型可调用，带曝光级别
│   └── ExtensionSession   session_start / session_shutdown、清理回调
├── ExtensionStateStore    按扩展名分桶的 JSON 状态，可选持久化
├── CommandCatalog         扩展命令 + 插件命令的统一解析
└── PluginHost             Claude Code 插件的渲染与 MCP 连接
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

`Activate` 只在构造管道时调用一次，**必须快速返回且不启动进程或定时器**（与 Pi 的工厂约束一致）；长生命周期资源放到 `SessionStart`，并在 `SessionShutdown` 里幂等释放。

## 事件

| 事件 | 语义 | 载荷 / 可做的事情 |
|---|---|---|
| `SessionStart` | 会话开始 | `SessionStartEvent`：启动资源、读会话状态 |
| `SessionShutdown` | 会话关闭 | 释放资源；处理器必须幂等 |
| `Input` | 用户输入 | `InputEvent`：改写 `Text` 或 `Suppress` 吞掉 |
| `BeforeAgentStart` | 请求发出前 | `BeforeAgentStartEvent`：追加 `PromptSections`、`PrependMessages` |
| `ToolCall` | 工具调用前 | `ToolCallEvent`：改写 `ArgumentsJson`、`BlockReason` 拦截 |
| `ToolResult` | 工具返回后 | `ToolResultEvent`：脱敏/截断 `Text` |
| `TurnEnd` | 一轮结束 | `TurnEndEvent`：`Continue` + `ContinuationPrompt` 请求追加一轮 |

组合语义（与 Pi 一致）：

- 处理器按**注册顺序**执行。
- 转换类事件链式传递：后一个看到前一个的修改。
- **拦截类 fail-closed**：`ToolCall` 处理器抛异常时按拒绝执行处理，不会放行。
- 通知类（`SessionStart` 等）的异常被吞掉，不打断对话。
- `pi.on` 的等价物返回一个取消订阅的 `Action`。

## 工具与曝光级别

| 曝光 | 声明给模型 | 可被其它工具调用 |
|---|---|---|
| `Direct`（默认） | ✅ | ✅ |
| `ModelOnly` | ✅ | ❌ |
| `Deferred` | ❌ | ✅（登记后即可调用） |
| `Hidden` | ❌ | ❌（用于撤回） |

调用路径：

- 模型调用 → 宿主 → `ToolCall` 事件 → `InvokeToolAsync` → `ToolResult` 事件。
- 扩展调用扩展 → `ctx.ExecuteToolAsync(name, json)`，走同一条事件链（`Runtime.ExecuteToolAsync`）。
- 宿主已经自行分发过事件时用 `Runtime.InvokeToolAsync`，避免重复触发。

单个工具执行抛异常会变成模型可见的错误结果；`ToolCall` 被拦截时返回 `{"error": "<reason>"}`。

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
| 每轮 | 注入 `## Implementation plan`；未确认时明确写入 "NOT approved" |

## Claude Code 插件（声明式层）

同一套目录约定仍然支持：`.claude-plugin/plugin.json`、`commands/*.md`、`skills/<name>/SKILL.md`、`.mcp.json`。

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

`agents`、`hooks`、`lspServers`、`outputStyles`、`workflows`、`themes`、`monitors`、`channels`、`userConfig` 不执行；加载时以 `Info` 级诊断说明被忽略。无 marketplace、无权限系统：`allowed-tools` 只作为元数据。

## 宿主集成（WPF）

| 选项 | 含义 |
|---|---|
| `EnableExtensions` | 是否启用可编程扩展，默认 true |
| `Extensions` | 要激活的扩展；null 时使用内置 `GoalExtension` + `PlanExtension`，空数组则禁用 |
| `PluginsDirectory` / `Plugins` | Claude Code 插件目录或已加载的插件 |
| `PluginHostOptions` | 插件命令渲染选项（项目根、超时、短名冲突策略） |
| `AllowPluginShellExecution` | 是否执行插件正文的 `` !`cmd` ``，默认 false |

`ChatViewModel` 侧：`Pipeline`、`Commands`、`TryExecuteCommandAsync`、`AvailableCommands`、`IsCommandPaletteOpen`。宿主级命令 `/resume`、`/fork`、`/new`、`/clear`、`/settings`、`/help` 由 ViewModel 直接处理，不发送请求。

## 与 Pi 的差异

| Pi | 本项目 |
|---|---|
| TypeScript + `jiti`，扩展是文件 | C# 类，编译期注册；无热重载 |
| `context` / `context_with_system` / provider 钩子 | 未实现：只到 `BeforeAgentStart` 粒度 |
| `tool_execution_start/update/end`、`provider_stream_event` | 未实现 |
| `ctx.ui`（select/confirm/input） | 仅 `Notify` 与 `AppendMessage`；WPF 侧映射为状态栏/气泡 |
| `appendEntry`、分支感知状态重建 | 用 `ExtensionStateStore` + `ConversationRecord.ExtensionState` 代替，`/fork` 直接复制 |
| `registerProvider` / `registerMcpServer` / `registerVirtualModel` | 未实现；MCP 仍由插件声明或宿主注册 |
| 工具 render 钩子 | 未实现（WPF 只有工具 chip） |
