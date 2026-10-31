# AiTools 终端 Agent（全屏 TUI）

`AiTools.Terminal` 是 AiTools 的**官方终端 Agent**：一个不依赖任何 UI 框架的全屏 TUI，
把本仓库已有的聊天、工具调用、MCP、技能、扩展与插件能力搬进终端。
它与 `AiTools.Wpf` 并列，共用同一份 `~/.ai-tools/settings.json` 与会话存储，因此
「桌面里配好的模型」在终端里直接可用，反之亦然。

```
samples/AiTools.Cli        ← 可执行程序（命令名 aitools）
        │
src/AiTools.Terminal       ← 全屏 TUI + Agent 会话编排
        │
src/AiTools.Configuration  ← 设置模型 / JSON 存储 / 密钥保护 / 服务商工厂（跨平台）
        │
  AiTools.Extensions / AiTools.Skills / AiTools.Mcp / Providers
```

## 快速开始

```bash
# 直接运行（首次需配置）
dotnet run --project samples/AiTools.Cli

# 发布后命令名为 aitools
dotnet publish samples/AiTools.Cli -c Release -o ./out
./out/aitools
```

配置方式（任一即可）：

| 方式 | 说明 |
|---|---|
| `~/.ai-tools/settings.json` | 与 WPF 设置页共用；`AITOOLS_HOME` 可重定向根目录 |
| WPF 演示程序 | `dotnet run --project samples/AiTools.Wpf.Demo`，在设置页添加服务商 |
| 环境变量 | `OPENAI_API_KEY` / `ANTHROPIC_API_KEY` / `GEMINI_API_KEY`（可配 `*_CHAT_MODEL`、`*_BASE_URL`） |
| `--provider` / `--model` | 临时指定服务商与模型 |

没有可用模型时，启动横幅会提示缺少哪些配置；`--list-models` 可以直接检查当前选择。

> Windows 的 Git Bash 会把以 `/` 开头的参数改写成路径（`/goal` → `C:/Program Files/Git/goal`）。
> 在 Git Bash 中运行一次性命令时请改用 `cmd.exe` / PowerShell，或写成 `aitools -p "/goal ..."`
> 以外的形式（例如进入交互界面再输入命令）。

## 全屏界面

```
 AiTools 0.1.0  ·  新对话  ·  Anthropic · claude-sonnet-4-5        ⠹
────────────────────────────────────────────────────────────────────────
 ❯ 帮我给这个仓库加一个 --json 输出
 ● 我会先看看 CLI 的入口，再加一个参数分支。…
   ⏺ read_file  {"path":"samples/AiTools.Cli/Program.cs"}
   ✓ read_file  0.1s  已读取 420 行
 ● 建议在 CommandLine 里增加一个 bool 属性…
────────────────────────────────────────────────────────────────────────
 ❯ ▏
 就绪 · Anthropic · claude-sonnet-4-5                        思考 · Esc 停止
```

| 区域 | 行为 |
|---|---|
| 头部 | 应用名、版本、会话标题、当前服务商/模型、输出风格、spinner |
| 记录区 | 用户气泡、助手 Markdown（标题/列表/代码块/表格）、工具卡片、通知；可用 PgUp/PgDn 滚动 |
| 输入框 | 单行编辑，支持中文输入法、水平滚动、历史、Tab 补全斜杠命令 |
| 状态栏 | 当前状态、token 用量、滚动位置、思考开关 |

### 快捷键

| 按键 | 行为 |
|---|---|
| `Enter` | 发送（输入框为单行；粘贴的多行文本会折叠为空格） |
| `Tab` | 补全当前 `/` 命令 |
| `↑` / `↓` | 输入历史；输入为空时滚动记录 |
| `PgUp` / `PgDn` | 滚动记录 10 行 |
| `Space`（输入为空且已回滚） | 向下翻页浏览历史 |
| `Ctrl+O` | 显示 / 隐藏推理过程 |
| `Ctrl+L` | 强制重绘 |
| `Ctrl+R` | 重新载入设置 |
| `Ctrl+U` / `Ctrl+K` / `Ctrl+W` | 删除到行首 / 行尾 / 上一个词 |
| `Ctrl+A` / `Ctrl+E` | 行首 / 行尾 |
| `Esc` | 停止生成；空闲时清空输入；浮层中取消 |
| `Ctrl+C` | 正在生成时停止；空闲且输入为空时退出 |
| `Ctrl+D` / `/exit` | 退出 |
| `?` | 快捷键浮层 |
| `/` | 命令面板 |

### 内置命令

| 命令 | 说明 |
|---|---|
| `/help` | 列出命令并打开快捷键浮层 |
| `/model` | 浮层选择服务商与模型（写入 `settings.json`） |
| `/resume` | 恢复已保存的会话 |
| `/fork` | 把当前会话分叉为新会话 |
| `/new` | 开始新会话（旧会话已保存） |
| `/clear` | 清空当前记录（磁盘上的会话保留） |
| `/copy [--last]` | 复制整个会话或最后一条回复到剪贴板 |
| `/status` | 会话、模型、技能、插件、扩展包与诊断摘要 |
| `/output-style [名称]` | 查看或切换输出风格 |
| `/extensions` | 列出扩展、扩展包、插件与技能 |
| `/reasoning [on\|off]` | 显示 / 隐藏推理过程 |
| `/settings` | 设置文件位置与模型列表 |
| `/exit`、`/quit` | 退出 |

扩展命令、Claude Code 插件命令、技能命令（`/skill:<名称>`）与内置命令共用一个面板，
由 `ExtensionPipeline` 统一解析；面板中输入 `skill` 相关关键字也能命中去前缀的技能名。

### 浮层

- **命令/模型/会话选择**：`↑↓` 选择、输入筛选、`Enter` 确认、`Esc` 取消。
- **提问（`ask` 工具）**：扩展的 `IExtensionChoiceUi` 映射为带说明的选项列表；
  宿主不可交互时退化为 `unavailable`，由模型改为在对话中直接提问。
- **权限确认**：配置了 `PermissionPolicy` 且判定为 `Ask` 时弹出「允许/拒绝」浮层；
  没有浮层通道时按失败关闭（fail-closed）。
- **文本输入 / 确认**：`ctx.Ui.InputAsync` / `ConfirmAsync` 的终端实现。
- **会话恢复**：`/resume` 选中后会清空并回放历史；`--resume` / `--continue` 启动时同样回放。

## Agent 会话能力

`AgentSession`（`src/AiTools.Terminal/Agent/AgentSession.cs`）是与界面无关的编排层，
复刻 WPF 对话页的全部行为，界面只消费 `AgentUpdate` 事件：

| 能力 | 说明 |
|---|---|
| 请求编排 | `before_agent_start` → 系统提示拼装（插件清单、AGENTS.md、技能目录、输出风格）→ `context` / `context_with_system` → Provider 请求 |
| 工具循环 | `StreamWithToolsExactAsync`，保留 Gemini `thoughtSignature` 等 ProviderData；单轮上限默认 24 |
| 扩展事件 | `input`、`tool_call`、`tool_result`、`tool_execution_*`、`turn_end`（可追加一轮）、`session_start/shutdown` |
| 技能 | 插件技能 + 扩展技能 + 共享 `.agents` 技能合并为 `load_skill` / `read_skill_file` |
| MCP | 插件 `.mcp.json` 与扩展注册的服务器自动连接，工具以 `plugin_*` / `ext_*` 前缀并入工具表 |
| 扩展包 | `~/.ai-tools/extensions` 下的 `extension.json` 包（C# / JS / TS）扫描并加载 |
| 插件 | 项目 `.claude/plugins`、`./plugins`、`~/.ai-tools/plugins`；加载前经 `ProjectTrust` 判定 |
| 权限 | `PermissionPolicy`（deny > ask > allow）经 `GuardToolAsync` 与 `tool_call` 后备双重生效 |
| 会话 | 每轮自动保存到 `~/.ai-tools/conversations`，支持恢复、分叉与扩展状态导入导出 |
| 虚拟模型 | 扩展注册的 provider 与 `RegisterVirtualModel` 路由对 CLI 同样生效 |

`AgentSession` 也可被其它宿主复用（例如 Web 端或 CI 脚本）：

```csharp
await using var session = new AgentSession(new TerminalOptions
{
    ProviderId = "anthropic",
    ModelId = "claude-sonnet-4-5",
    WorkingDirectory = "/repo"
});

session.Updates += update =>
{
    if (update.Kind == AgentUpdateKind.TextDelta) Console.Write(update.Text);
};

await session.InitializeAsync();
await session.SubmitAsync("总结这个仓库的构建方式");
```

## 运行模式

| 模式 | 触发条件 | 说明 |
|---|---|---|
| 全屏 TUI | 终端支持 ANSI 且输入输出未重定向 | 备用屏幕逐帧差分渲染，退出后恢复原终端内容 |
| `--plain` | 显式指定或 IDE 内嵌终端 | 仍逐帧渲染，但不切换到备用屏幕 |
| 行式 REPL | 输入/输出被重定向或终端不支持 ANSI | 纯文本流式输出，适合管道与远程会话 |
| 一次性（`-p`） | 显式指定 | 把回答写到 stdout，错误写 stderr，退出码可用于脚本 |

环境变量 `AITOOLS_HOME` 会把设置、会话、插件、扩展与技能根一起重定向，便于测试与便携部署。
`--list-models`、`--list-sessions`、`--list-commands`、`-r <id>`、`-c` 都可用于脚本化操作；
`AITOOLS_EXTENSIONS=<目录>` 可为 CLI 指定扩展包根（与 WPF 的默认发现路径一致，便于临时验证）。

## 渲染实现

TUI 不依赖任何第三方库（无 Spectre/Terminal.Gui），全部自绘：

| 文件 | 职责 |
|---|---|
| `Rendering/TextWidth.cs` | 东亚宽度、码点安全截断、控制字符清洗 |
| `Rendering/TerminalStyle.cs` | 真彩色 / 256 色 / 16 色降级与 SGR 生成 |
| `Rendering/ScreenBuffer.cs` | 单元格虚拟屏幕、宽字符连续格、逐行 diff 合成 |
| `Input/TerminalKey.cs` | 纯函数式按键解析（CSI、SS3、括号粘贴、Alt 组合、代理对） |
| `ConsoleTerminalHost.cs` | 备用屏幕、VT 输入模式（Windows）/ `stty raw`（Unix）、尺寸轮询 |
| `MarkdownRenderer.cs` | 段落、标题、列表、引用、围栏代码、行内样式、链接、表格 |
| `Views/*` | 记录区模型与渲染缓存、行编辑器、浮层（列表/输入/确认/帮助） |

设计取舍：

- **虚拟屏幕 + 行级 diff**：流式输出时只重绘变化行，长会话不闪烁，也不会每帧重排历史。
- **按显示宽度排版**：中文、emoji 与组合字符都按终端列计算，避免整行错位。
- **按键解析与终端解耦**：`KeyParser` 是纯函数，可用单元测试覆盖各种终端差异。
- **降级显式化**：没有 ANSI、没有交互浮层、没有剪贴板命令时都会退化为明确的行为，而不是悄悄失败。

## 与 WPF 的关系

| | `AiTools.Wpf` | `AiTools.Terminal` |
|---|---|---|
| 目标 | 桌面设置页 + 对话页 | 全屏终端 Agent |
| 目标框架 | `net8.0-windows` | `net8.0`（跨平台） |
| 配置模型 | `AiTools.Configuration` | 同一个 |
| 扩展 UI | WPF 对话框 / 内联面板 | 终端浮层 |
| 会话存储 | `~/.ai-tools/conversations` | 同一个 |

共享配置包 `AiTools.Configuration` 位于 `src/AiTools.Configuration`，包含
`AppSettings`、`ProviderConfig`、`JsonFileSettingsStore`、`DpapiSecretProtector`、
`ProviderDescriptor` 与 `BuiltInProviderFactory`。WPF 的类型名保持不变（命名空间改为
`AiTools.Configuration`），`AiToolsWpfException` 继承 `AiToolsConfigurationException`，
因此现有的异常处理代码无需修改。

## 扩展点

| 接缝 | 用途 |
|---|---|
| `TerminalOptions` | 设置存储、服务商工厂、插件目录、扩展集合、权限、工具 provider 等 |
| `IInteractivePrompt` | 替换确认/输入/选择/带说明选项的呈现方式（默认由 TUI 浮层实现） |
| `IChatToolProvider` | 注入宿主工具（与 WPF 相同） |
| `ITerminalHost` | 替换终端后端（例如测试用的内存宿主、WebSocket 终端） |
| `TerminalTheme` | 配色、符号与 spinner；`CreateDefault` 按终端能力选择真彩色 |
| `AgentSession.Updates` | 消费增量更新，自建界面 |

## 测试

`tests/AiTools.Tests/Terminal/` 覆盖：

- 显示宽度与截断（宽字符、emoji、控制字符）
- 虚拟屏幕写入与差分合成
- 按键解析（CSI/SS3、括号粘贴、Alt 组合、代理对、单独 ESC）
- 行编辑（历史、按词删除、滚动）
- Markdown（换行、代码块、列表、行内样式、表格）
- 记录区渲染缓存
- 浮层交互
- `AgentSession` 端到端（流式文本、工具循环、持久化、恢复、分叉、环境变量兜底）

```bash
dotnet test tests/AiTools.Tests/AiTools.Tests.csproj --filter "FullyQualifiedName~Terminal"
```
