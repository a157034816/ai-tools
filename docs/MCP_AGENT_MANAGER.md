# MCP Agent 管理器示例

一个 WPF 桌面程序，演示如何为**主流 AI coding agent 批量添加/移除预制的 MCP 服务**，并为不支持的 agent 导出可直接粘贴的 JSON。

## 支持矩阵

| Agent | 配置文件 | 格式 | 实测 |
|---|---|---|---|
| Claude Code | `~/.claude.json` → `mcpServers` | JSON | ✅ |
| Codex CLI | `~/.codex/config.toml` → `[mcp_servers.*]` | TOML | ✅ |
| Grok CLI | `~/.grok/config.toml` → `[mcp_servers.*]` | TOML | ✅ |
| Gemini CLI | `~/.gemini/settings.json` → `mcpServers` | JSON | ✅ |
| Kimi CLI | `~/.kimi/mcp.json` → `mcpServers` | JSON | 按 CLI 惯例 |
| OpenCode | `~/.config/opencode/opencode.json` → `mcp` | JSON | ✅ |
| OpenClaw | `~/.openclaw/mcp.json` → `mcpServers` | JSON | 待验证 |
| Pi | `~/.pi/agent/mcp.json` → `mcpServers` | JSON | ✅ |
| OMP (oh-my-pi) | `~/.omp/agent/mcp.json` → `mcpServers` | JSON | ✅ |
| Hermes | `%LOCALAPPDATA%\hermes\config.yaml` → `mcp_servers` | YAML | ✅ |
| dsh | — | 不支持写入，仅导出 JSON | — |

> 配置位置以作者本机实测为准（Claude Code 2.1、Codex、Grok、Gemini、Pi 18.12、OMP、Hermes）。各 agent 版本迭代可能调整路径，程序内提供「配置路径…」手动覆盖，覆盖项保存在 `~/.ai-tools/mcp-agent-manager/overrides.json`。

## 预置 MCP 服务

| ID | 名称 | 传输 | 说明 |
|---|---|---|---|
| `filesystem` | Filesystem | stdio (npx) | 读写指定目录 |
| `memory` | Memory | stdio (npx) | 知识图谱持久记忆 |
| `sequential-thinking` | Sequential Thinking | stdio (npx) | 结构化逐步推理 |
| `context7` | Context7 | stdio (npx) | 最新库文档与示例 |
| `github` | GitHub | stdio (npx) | 仓库/Issue/PR，**需要 PAT** |
| `playwright` | Playwright | stdio (npx) | 浏览器自动化 |
| `puppeteer` | Puppeteer | stdio (npx) | 无头浏览器抓取 |
| `fetch` | Fetch | stdio (uvx) | URL → Markdown |
| `git` | Git | stdio (uvx) | 本地 Git 操作 |
| `sqlite` | SQLite | stdio (uvx) | SQLite 查询 |
| `fetch-http` | Fetch (HTTP) | http | 远程 MCP 示例 |

需要用户输入环境变量的预置（如 `github`）会在安装前弹出输入框；值只写入目标 agent 的配置文件，不落盘到本程序。

## 项目结构

| 项目 | 说明 |
|---|---|
| `samples/AiTools.Samples.McpAgents` | 共享库：预置目录、agent 目录、配置读写器、JSON 导出 |
| `samples/AiTools.Samples.McpAgentManager` | WPF 演示程序 |

### 核心类型

```csharp
var adapters = AgentCatalog.CreateAdapters();              // 11 个 agent，含真实路径
var probes = new AgentProbeService().ProbeAll(adapters);   // CLI/配置探测

var editor = new AgentConfigEditor(backupDirectory);
var state = editor.Read(adapter, adapter.ConfigPath);      // 已安装的 MCP
var result = editor.Install(adapter, adapter.ConfigPath, preset);
var result2 = editor.Remove(adapter, adapter.ConfigPath, "memory");

var json = McpEntryFormatter.BuildPortableJson(presets);   // 未支持 agent 的通用 JSON
```

## 安全设计

- **备份**：每次写入前在备份目录生成带时间戳的 `.bak` 文件。
- **原子写入**：临时文件 + 原子替换，写入中断不会留下半截配置。
- **保留原有内容**：只改动目标 MCP 条目；Claude/Gemini 的其它设置、Codex/Grok 的 `[agents]` 等表、OpenCode 的 `$schema`/`provider`、Hermes 的其它 YAML 键全部原样保留。
- **幂等**：重复添加不会产生重复条目。
- **不碰未知格式**：`AgentKind.Unsupported`（dsh）只导出 JSON，绝不猜测写入。
- **只读探测**：程序启动时只读取配置，不会修改任何文件，除非用户主动点击添加/移除。

## 各格式要点

| 格式 | 处理方式 |
|---|---|
| JSON | `System.Text.Json` 的 `JsonNode`，保留未知字段与注释（`AllowTrailingCommas` / `Skip`） |
| TOML | 自带的切片式编辑器：按 `[mcp_servers.*]` 表定位，只重写目标表及其 `.env` 子表 |
| YAML | `YamlDotNet` 反序列化为字典 → 修改 `mcp_servers` → 重新序列化 |

TOML 编辑器没有引入完整 TOML 库，是为了在保留原有格式（注释、顺序、未被识别的表）的前提下做最小改动；测试覆盖了 Codex 与 Grok 的真实配置形状。

## 运行

```bash
dotnet run --project samples/AiTools.Samples.McpAgentManager
```

界面：左侧 agent 列表（状态徽章、配置路径、CLI 检测），右侧预置 MCP 网格（添加/移除/全部添加/全部移除），底部活动日志（含备份路径），以及「导出 JSON」面板。

> 演示程序默认只读探测；安装/移除会真实修改对应 agent 的配置文件（并生成备份）。第一次使用建议先在测试 agent 上验证，或先导出 JSON 手动确认内容。
