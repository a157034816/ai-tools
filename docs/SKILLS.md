# Skills（Agent Skills）

框架内置 **Agent Skills** 支持：技能是"文件夹 + `SKILL.md`"组成的知识包（指令 + 资源 + 可选脚本），通过**渐进式披露**交给模型——系统提示里只放名称和描述，模型判断需要时才加载完整内容。该能力位于 `AiTools.Skills`，**与厂商无关**，OpenAI / Anthropic / Gemini 皆可使用。

## 技能包格式

```
my-skill/
├── SKILL.md              # 必需：YAML frontmatter + Markdown 指令
├── references/           # 可选：参考文档
│   └── guide.md
├── scripts/              # 可选：脚本
└── assets/               # 可选：模板等资源
```

`SKILL.md`：

```markdown
---
name: pdf-processing
description: Extract text and tables from PDF files. Use when the user asks to parse a PDF.
allowed-tools: [Read, Bash]
license: MIT
version: 1.0.0
metadata:
  author: example-org
---

# PDF Processing

1. Read `references/style-guide.md` before producing output.
2. Use pdfplumber to extract text.
```

| 字段 | 必需 | 说明 |
|---|---|---|
| `name` | ✅ | 小写字母、数字、单连字符，≤64 字符，须与文件夹名一致 |
| `description` | ✅ | ≤1024 字符；模型据此判断何时加载，写清「做什么 + 何时用」 |
| `allowed-tools` | — | 支持列表或 `[a, b]` 内联写法 |
| `license` / `version` / `compatibility` | — | 元数据 |
| `metadata.*` | — | 任意键值对 |

`name` 与 `description` 以外的 frontmatter 字段都是可选的，未知字段会被忽略。

## 快速开始

### 1. 加载技能

```csharp
using AiTools.Skills;

var catalog = SkillCatalog.LoadFromDirectory("D:/my-app/skills");
foreach (var diagnostic in catalog.Diagnostics)
{
    Console.WriteLine($"技能问题：{diagnostic.Path} — {diagnostic.Message}");
}

var toolset = new SkillToolset(catalog);
```

发现过程**永不抛异常**：无效技能会被跳过并通过 `Diagnostics` 报告（缺少 description、名称非法、重复名称、目录名不匹配等）。

### 2. 接入任意 Provider

```csharp
var request = ChatRequest.FromUser("gpt-4.1", "帮我审一下这个 PDF 的排版");

// 方式一：手动组合
var prepared = toolset.ApplyTo(request);
var answer = await client.Chat.CompleteWithToolsAsync(prepared, toolset.CreateToolHandler());

// 方式二：使用扩展方法（可与其他工具混用）
var answer2 = await client.Chat.CompleteWithSkillsAsync(
    request,
    toolset,
    additionalToolHandler: MyOtherTools.ExecuteAsync);
```

`ApplyTo` 会：
1. 把技能清单（名称 + 描述）追加到系统提示词；
2. 添加两个元工具：`load_skill` 与 `read_skill_file`。

### 3. 注入的提示词

```markdown
## Skills

You have access to skills: packages of instructions and resources for specific tasks.
When a task matches a skill's description, call the `load_skill` tool ... before doing the work.
After loading a skill, use `read_skill_file` to read any bundled file it references.

Available skills:
- `pdf-processing`: Extract text and tables from PDF files. Use when ...
- `commit-message`: 根据改动内容生成符合约定式提交规范的 Git 提交信息 ...
```

完整指令**不会**进入系统提示——只有模型调用 `load_skill` 时才返回，这就是渐进式披露。

## 两个元工具

| 工具 | 参数 | 行为 |
|---|---|---|
| `load_skill` | `name` | 返回该技能的完整 Markdown 指令、技能目录路径与文件读取提示 |
| `read_skill_file` | `skill`, `path` | 读取技能目录内的文件（相对路径，使用 `/`） |

安全检查：
- 拒绝包含 `..` 的路径；
- 解析后必须位于该技能目录内（可用 `AllowFilesOutsideSkillDirectory` 关闭）；
- 读取内容超过 `MaxFileCharacters`（默认 200,000）时截断并注明。

## 配置项

```csharp
var toolset = new SkillToolset(catalog, new SkillToolOptions
{
    MaxFileCharacters = 100_000,
    DisabledSkills = new[] { "experimental-skill" },
    // 完全替换默认的 "## Skills" 提示词段落
    InstructionsOverride = "使用技能前必须先调用 load_skill。"
});
```

## WPF 集成

```csharp
using AiTools.Wpf.Skills;

var provider = SkillChatToolProvider.FromDirectory(Path.Combine(AppContext.BaseDirectory, "skills"));

AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    // 只注入名称与描述，保持上下文精简
    DefaultSystemPrompt = basePrompt + "\n\n" + provider.Toolset.BuildSystemPrompt()
});

AiToolsWpf.RegisterToolProvider(provider);
```

演示程序 `samples/AiTools.Wpf.Demo` 的 `skills/commit-message` 就是一个可直接试用的技能。

## 设计说明

- **与厂商无关**：技能只依赖 `ToolDefinition` / `ToolCall` 抽象，任何 Provider 都支持。
- **渐进式披露**：常驻上下文只有名称与描述，完整指令按需加载，节省 token。
- **元工具而非硬编码**：加载技能本身也是工具调用，因此可与自动工具循环（`CompleteWithToolsAsync` / `StreamWithToolsExactAsync`）天然配合。
- **目录即边界**：`read_skill_file` 被限制在技能目录内，避免技能读取任意本机文件。
- **容错发现**：单个坏技能不会让整个目录加载失败，问题通过 `Diagnostics` 暴露给宿主。
