# MCP 示例：工具服务 + 本机嗅探客户端（WPF 桌面版）

这组示例演示完整的 MCP（Model Context Protocol）工具链路，两个项目均为 **WPF 桌面应用**（深色 UI，与 `AiTools.Wpf` 同一套视觉语言）：

```
项目 1（Tool Server，WPF）                    项目 2（Explorer，WPF）
┌───────────────────────────┐                 ┌───────────────────────────┐
│ 工具目录（ToolDefinition）  │                 │ 本机服务列表（自动刷新）     │
│  ├ echo / get_time ...     │   注册表文件     │  ├ 在线/离线/工具数          │
│  ├ MCP Streamable HTTP     │ ──────────────▶ │  ├ 动态切换连接              │
│  ├ 实时请求日志             │  ~/.ai-tools/   │  ├ 工具目录 + Schema 驱动表单 │
│  └ 内置自测（直接调用工具）  │                 │  └ 调用结果 / 资源 / 提示词   │
└───────────────────────────┘                 └───────────────────────────┘
```

| 项目 | 位置 | 作用 |
|---|---|---|
| `AiTools.Samples.McpDiscovery` | `samples/AiTools.Samples.McpDiscovery` | 共享的本机 MCP 服务注册表（文件 + 进程存活检测） |
| `AiTools.Samples.McpToolServer` | `samples/AiTools.Samples.McpToolServer` | **项目 1**：WPF 工具服务界面，同时作为 MCP 服务端（HTTP / stdio）对外服务 |
| `AiTools.Samples.McpExplorer` | `samples/AiTools.Samples.McpExplorer` | **项目 2**：WPF 嗅探客户端，列出本机服务、动态切换、调用工具 |

## 为什么需要"注册表"

MCP 规范本身没有定义"服务发现"。因此项目 1 在启动后把自身信息（名称、版本、端点、PID、启动时间、工具列表）写入本机注册表，项目 2 读取注册表即可"嗅探"到本机所有运行中的实例。PID + 进程启动时间双重校验可避免 PID 复用导致的误判。

注册表位置：`~/.ai-tools/simple-mcp/registry.json`
可用环境变量 `AITOOLS_MCP_REGISTRY_PATH` 覆盖（测试用）。

所有读写均持有独占文件锁并带重试，多个服务同时注册不会互相破坏；损坏的注册表会自动备份为 `*.corrupt-*` 并从空状态恢复。

## 快速体验

### 1. 启动工具服务（项目 1）

```bash
# 打开 WPF 界面（自动启动服务，默认端口 5220，被占用时自动顺延）
dotnet run --project samples/AiTools.Samples.McpToolServer

# 指定端口 / 名称
dotnet run --project samples/AiTools.Samples.McpToolServer -- --port 5300 --name "我的工具服务"

# 无窗口模式（供自动化脚本 / MCP 客户端使用，行为与控制台版一致）
dotnet run --project samples/AiTools.Samples.McpToolServer -- --headless --port 5220

# 查看工具目录
dotnet run --project samples/AiTools.Samples.McpToolServer -- --list-tools

# stdio 模式（供 MCP 客户端以子进程方式直连，不写注册表）
dotnet run --project samples/AiTools.Samples.McpToolServer -- --stdio
```

WPF 界面包含：运行状态徽章与端点（可复制）、PID、启动/停止按钮、工具目录卡片（名称/描述/参数类型/必填标记）、实时请求日志（方法/工具/耗时/状态，最多 500 条，可暂停滚动与清空）、内置自测面板（按 Schema 动态生成参数输入，直接调用工具并显示结果 JSON）。

工具目录（全部为只读工具，带 `readOnlyHint` 注解）：

| 工具 | 参数 | 说明 |
|---|---|---|
| `echo` | text | 原样回显 |
| `get_time` | timezone? | 当前时间（可指定时区） |
| `calculate` | a, op, b | 四则运算 / 取余 / 幂 |
| `text_stats` | text | 字符、单词、行数统计 |
| `random_number` | min, max, seed? | 随机整数 |
| `hash_text` | text, algorithm? | SHA-256 / SHA-1 / MD5 摘要 |
| `base64` | mode, text | 编码 / 解码 |
| `uuid` | count? | 生成 UUID |

此外还提供 `resources/list|read`（工具目录、服务信息）与 `prompts/list|get`（`explain_tool`）。

### 2. 启动嗅探客户端（项目 2）

```bash
# 打开 WPF 界面
dotnet run --project samples/AiTools.Samples.McpExplorer

# 指定注册表文件（例如与其他实例隔离）
dotnet run --project samples/AiTools.Samples.McpExplorer -- --registry D:\temp\registry.json
```

界面分为三栏：

- **左栏 · 本机 MCP 服务**：自动列出注册表中所有服务（名称、版本、在线/离线徽章、HTTP/stdio、工具数），每 3 秒自动刷新（可关闭），选中后显示端点、PID、启动命令、注册时间。
- **中栏 · 工具目录**：选中服务点「连接」后填充工具列表，顶部显示连接状态（未连接 / 连接中 / 已连接）、服务器名、协商协议版本、传输与端点。
- **右栏 · 调用面板**：根据工具的 `inputSchema` 动态生成参数表单（string / number / integer / boolean / enum，必填标 `*`），也可切换到「以 JSON 编辑」直接写原始参数；调用结果按内容块渲染（text / image / audio / resource / embedded / unknown），并展示耗时与原始 JSON。
- 下方 Tab：**资源**（列表 + URI 读取）、**提示词**（列表 + 参数 + 获取渲染结果）、**活动日志**（最近 300 条）。

连接/切换/关闭窗口都会正确释放 MCP 会话与传输；连接失败只显示红色错误与日志，界面保持可用。

### 3. 脚本 / CI 用法（Headless）

项目 1 的无窗口模式适合自动化：

```bash
# 启动服务（不弹窗），并写入注册表
dotnet run --project samples/AiTools.Samples.McpToolServer -- --headless --port 5220 --name "CI 工具服务"

# 输出工具目录 JSON 后退出
dotnet run --project samples/AiTools.Samples.McpToolServer -- --list-tools
```

测试项目中的 `McpToolServerEndToEndTests` 就是用 `--headless` 启动真实服务进程，再直接发 HTTP 请求验证 MCP 握手与工具调用的。
