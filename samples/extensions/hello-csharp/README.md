# Hello C#（示例扩展包）

最小的 C# 扩展包：一个目录 + `extension.json` + 一个 csproj + 一个实现 `IExtension` 的类。
它同时演示扩展的三件套——**带状态的命令**、**模型可调用的工具**、**每轮注入的提示词段落**。

## 结构

```
hello-csharp/
├── extension.json      包清单（kind = csharp，project 指向 csproj）
├── HelloCSharp.csproj  net8.0，引用 ../../.. 的 AiTools.Extensions
├── HelloExtension.cs   实现 IExtension 的扩展本体
└── README.md
```

## 能力

| 能力 | 触发方式 | 行为 |
|---|---|---|
| 命令 | `/hello <名字>` | 会话计数 +1；有参数时返回 `CommandResult.Prompt`，把提示词发给模型 |
| 命令 | 无参数执行 `/hello` | 返回 `CommandResult.Display`，只显示当前计数，不发起模型轮次 |
| 工具 | 模型调用 `hello_csharp_count` | 参数 `{ "increment": true }` 先自增再返回；否则只读。返回 `{ count, sessionId }` |
| 事件 | `BeforeAgentStart` | 计数大于 0 时追加 `## Hello C#` 段落，把进度回灌进系统提示 |

状态写在 `context.State`（`ExtensionStateStore`）里，以扩展名 `hello-csharp` 分桶，随会话持久化，
**不占用对话历史**——这是 Pi 式扩展的关键点：命令建立状态、工具推进状态、事件把状态送回提示词。

## 关键代码

```csharp
public sealed class HelloExtension : IExtension
{
    public const string ExtensionId = "hello-csharp";

    public string Name => ExtensionId;                       // 同时是状态桶名
    public string? Description => "示例扩展：…";

    public void Activate(IExtensionApi api)
    {
        api.RegisterCommand("hello", "…", HandleHelloAsync, "<名字>");

        api.RegisterTool(
            ToolDefinition.FromJsonSchema(CountToolName, "…", """{ "type": "object", … }"""),
            ExecuteCountAsync,
            ToolExposure.Direct);

        api.On<BeforeAgentStartEvent>(ExtensionEvent.BeforeAgentStart, AppendCounterSectionAsync);
    }
}
```

`Activate` 只登记、不启动任何资源；`api.On<TEvent>` 的泛型重载返回 `Task<TEvent?>`，
返回 `null` 表示不改写事件对象，只就地改 `PromptSections`。

## 构建

包在加载时由宿主用 `dotnet build … -o bin/ext/<ticks>` 现场构建，也可以单独验证：

```bash
dotnet build samples/extensions/hello-csharp/HelloCSharp.csproj -m:1 -nodeReuse:false
```

`extension.json` 的 `output` 是**构建根目录**（相对包根，省略时默认 `bin/ext`），不是 `bin/Debug/net8.0`：
加载器每次把产物写进 `bin/ext/<ticks>/`，再在该目录内按 csproj 同名 dll 定位程序集，
找不到时回退到目录中第一个非宿主 dll。单独执行 `dotnet build`（本页的命令）走的是常规 `bin/Debug/net8.0`，
与加载器的输出目录互不冲突，两者都在 `.gitignore` 里。

`project` 是相对包根的 csproj 路径，因此整个目录可以被复制到任意位置（只有 csproj 对 `src/` 的相对引用是固定的）。

## 注意

- 扩展类必须有**公共无参构造函数**，否则程序集加载器只会记一条诊断并跳过它。
- `AiTools.Extensions.dll` 由宿主提供，构建期引用只用于编译；不要把它复制进包的输出目录。
- `bin/`、`obj/` 已在 `.gitignore` 中忽略。
