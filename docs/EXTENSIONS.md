# Extensions（Claude Code 兼容插件）

`AiTools.Extensions` 让 AiTools 直接消费 Claude Code 的插件包：同一份插件目录既能被 `claude` 加载，也能被 AiTools 加载。它不依赖任何 UI 框架，控制台、WPF 与服务端共用同一套宿主。

## 支持的组件

| 组件 | 目录约定 | AiTools 中的表现 |
|---|---|---|
| 清单 | `.claude-plugin/plugin.json` | 名称、描述、版本、`commands` 内联/路径、`mcpServers` 内联/路径 |
| 命令 | `commands/*.md`（子目录成为 `:` 段） | 斜杠命令，调用后展开为一条用户消息 |
| 技能 | `skills/<name>/SKILL.md` | 既是斜杠命令，也可经 `load_skill` 由模型按需加载 |
| MCP 服务 | `.mcp.json` / manifest `mcpServers` | 连接服务器并把它提供的工具并入工具列表 |

`agents`、`hooks`、`lspServers`、`outputStyles`、`workflows`、`themes`、`monitors`、`channels` 等组件目前**不执行**，加载时以 `Info` 级诊断说明被忽略，不会导致插件加载失败。

## 快速开始

```csharp
using AiTools.Extensions;

var catalog = ExtensionCatalog.LoadFromDirectory("D:/my-app/plugins");
foreach (var diagnostic in catalog.Diagnostics)
{
    Console.WriteLine(diagnostic);
}

var host = new ExtensionHost(catalog, new ExtensionHostOptions
{
    ProjectDirectory = "D:/my-app",   // ${CLAUDE_PROJECT_DIR}
    EnableShellExecution = false      // 是否执行正文中的 !`cmd`
});

if (host.TryResolve("/goal 实现插件框架", out var command, out var arguments))
{
    var prompt = host.Render(command!, arguments, sessionId: "abc").Text;
    // 把 prompt 作为 ChatMessage.User 发送
}
```

### 目录发现规则

- 传入的路径本身含 `.claude-plugin/plugin.json`、`SKILL.md`、`skills/` 或 `commands/` 时，按**单个插件**加载。
- 否则扫描子目录（默认递归），把符合上述条件的目录当作插件根；`.`、`node_modules`、`bin`、`obj`、`.git` 会被跳过。
- 同名插件后者覆盖前者，并给出 `Warning` 诊断。

## 命令名与冲突

命令的限定名是 `plugin:command`（`commands/db/migrate.md` → `db:migrate`）。调用时可以写：

| 写法 | 何时可用 |
|---|---|
| `/workflows:goal` | 总是可用 |
| `/goal` | 短名在所有插件中唯一时可用；冲突时 `TryResolve` 返回 false，需用限定名 |

`ExtensionCatalog.GetShortNameConflicts()` 返回冲突组，`ExtensionHostOptions.AllowAmbiguousShortNames` 为 true 时冲突的短名会取第一个匹配。

## 命令正文的渲染

`CommandTemplateRenderer` 按 Claude Code 的规则处理正文，替换是**单遍**的——由参数值或命令输出注入的文本不会被再次扫描。

| 语法 | 行为 |
|---|---|
| `$ARGUMENTS` | 完整参数串 |
| `$0` / `$ARGUMENTS[0]` | 第 1 个参数（shell 风格引号分组）；越界时保持原样 |
| `$name` | `arguments: [issue, branch]` 声明的具名位置参数；缺失时展开为空串 |
| `\$1` | 字面量 `$1` |
| `${CLAUDE_PLUGIN_ROOT}` | 插件根目录 |
| `${CLAUDE_SKILL_DIR}` | 技能目录（命令文件定义时为插件根） |
| `${CLAUDE_PROJECT_DIR}` | `ExtensionHostOptions.ProjectDirectory` |
| `${CLAUDE_SESSION_ID}` | `Render` 传入的会话 ID |
| `${CLAUDE_PLUGIN_DATA}` | 持久化数据目录 |
| `` !`cmd` `` | 行内动态注入，命令输出替换该片段 |
| ` ```! ` 块 | 多行动态注入 |

规律：

- 正文没有消费任何参数时，`Render` 会在末尾追加 `ARGUMENTS: <原始参数>`，模型仍能看到用户输入。
- 动态注入命令以非零退出码结束会抛出 `ExtensionCommandException`，整次调用中止——与 Claude Code 行为一致，不会把半成品提示词发给模型。
- `EnableShellExecution = false`（WPF 宿主默认）时，注入点被替换为 `[shell command execution disabled by policy]`。
- 注入输出只在行尾裁剪：保留内部换行，去掉尾部 `\r\n`。

## 插件技能

`skills/<name>/SKILL.md` 的 frontmatter 支持 `description`、`argument-hint`、`allowed-tools`、`disallowed-tools`、`arguments`、`model`、`disable-model-invocation`、`user-invocable`。

`ExtensionCatalog.ToSkillCatalog()` 把插件的技能转成 `SkillCatalog`：短名唯一时保留短名，冲突时改用 `plugin:name`。WPF 侧由 `PluginSkillChatToolProvider` 包装成 `IChatToolProvider`，模型可以调用 `load_skill` / `read_skill_file`。

## MCP 服务器

`.mcp.json` 与 manifest 的 `mcpServers` 支持三种形态：

```json
{
  "mcpServers": {
    "local": { "command": "npx", "args": ["-y", "some-server"], "env": { "TOKEN": "x" } },
    "remote": { "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer x" } },
    "legacy": { "type": "sse", "url": "https://example.com/sse" }
  }
}
```

`${CLAUDE_PLUGIN_ROOT}` 会在 `command`、`args`、`env`、`url`、`headers` 中展开（路径统一使用正斜杠）。通过 `ExtensionHost.ConnectMcpServerAsync` 连接；WPF 侧的 `PluginMcpChatToolProvider` 会连接全部服务器、把工具名加上 `plugin_<server>_` 前缀，并在 `LastErrors` 中报告连接失败。

## WPF 集成

```csharp
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    PluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins"),
    AllowPluginShellExecution = false,   // 默认值
    ExtensionOptions = new ExtensionHostOptions { ProjectDirectory = Environment.CurrentDirectory }
});
```

| 行为 | 说明 |
|---|---|
| 命令面板 | 输入框以 `/` 开头时列出插件命令，`↑`/`↓` 选择，`Enter` 插入，`Esc` 关闭 |
| 命令展开 | 命中插件命令→渲染正文并作为 user 消息发送；气泡只显示 `/goal`，完整提示词保存在历史与 `PromptText` 中 |
| 未命中 | `/unknown` 按普通消息发送（模型可以看到），不会静默丢弃 |
| 内置命令 | `/resume`、`/fork`、`/new`、`/clear`、`/settings`、`/help [关键字]` 由 ViewModel 直接处理，不发送请求 |
| 系统提示 | 不可由模型调用的命令（`disable-model-invocation`）不会出现在清单里 |
| 失败 | 渲染或 shell 注入失败时设置 `StatusKind.Error` 状态，不发送请求 |

## 仓库内的 `plugins/workflows`

```
plugins/workflows/
├── .claude-plugin/plugin.json
└── commands/
    ├── goal.md     → /goal <目标>
    └── plan.md     → /plan <任务>
```

- `/goal`：把目标改写成一句结果导向的陈述、2–5 条可验证的成功标准、假设与待确认问题、按依赖排序的清单，并说明后续每轮如何汇报进度；目标为空时反问而不是编造。
- `/plan`：先要求用工具确认文件/类型/调用方，再给出每步的文件、变更、验证方式与风险，明确列出 out-of-scope 与 open questions，并在计划后停下等待确认。

两者都使用 `$ARGUMENTS`；未传参数时正文自行反问，因此不会追加 `ARGUMENTS:` 块。

## 诊断与容错

加载过程**永不抛出异常**：

| 场景 | 结果 |
|---|---|
| 清单 JSON 非法 | `Error`，插件不加载 |
| 路径含 `..` 或逃出插件根 | `Warning`，该路径被忽略 |
| 命令名重复、技能重名 | `Warning`，保留第一个 |
| 未知顶层字段 | 忽略（`commands`/`mcpServers` 内的未知键不报错） |
| 不支持的组件（`hooks` 等） | `Info` |
| 目录不存在 | `Warning` / `Error`，返回空插件 |

## 与 Claude Code 的差异

- 不读取 `~/.claude/settings.json`、marketplace，也不处理插件的 `userConfig`、`${user_config.KEY}`。
- 不做权限系统：`allowed-tools` 只作为元数据暴露，是否拦截由宿主决定。
- `context: fork`、`background`、`paths`、`hooks`、`shell` 等 Claude Code 专属前端字段不生效。
- 动态注入使用一条合并的 shell 脚本（Windows `cmd.exe`、其他平台 `/bin/sh`），而不是真实的 Bash/PowerShell 工具，因此不共享 Bash 工具的超时与输出上限策略。
