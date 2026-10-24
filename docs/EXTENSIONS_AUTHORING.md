# 扩展包编写指南（Extension Packages）

本页面向**扩展包作者**：你的产物是一个目录，根部一份 `extension.json`，加载后向宿主注册命令、工具与事件处理器。
文中每个 API 名、字段名与诊断文本都引自 `src/AiTools.Extensions/Packages/` 与 `src/AiTools.Extensions.Scripting/` 的实现。

配套阅读：

- [`EXTENSIONS.md`](EXTENSIONS.md)：运行时结构、事件表、工具曝光与权限模型。
- [`samples/extensions/`](../samples/extensions/)：四个可直接复制走的示例包（C# / JS / TS / 跨包依赖）。
- [`ARCHITECTURE.md`](ARCHITECTURE.md)：`AiTools.Extensions.Scripting` 为什么是独立、可选、net8-only 的程序集。

## 1. 一个包是什么

**一个目录 = 一个 git 仓库 = 一份 `extension.json`。**

```
my-extension/
├── extension.json     # 唯一必需文件
├── README.md
├── .gitignore
└── …                  # 其余内容随 kind 而定
```

工作区（`ExtensionWorkspace`）扫描的是根目录的**直接子目录**，每个含 `extension.json` 的子目录就是一个包；
`bin`、`obj`、`node_modules`、`.ai-tools`、`.git` 会被跳过（`ExtensionWorkspace.IgnoredDirectories`）。
包目录之外只有两份约定：C# 包在编译期引用 `AiTools.Extensions`，脚本包不引用任何外部文件。

三种 `kind`：

| `kind` | 产物 | 加载方式 |
|---|---|---|
| `csharp` | .NET 程序集 | 宿主 `dotnet build` 到新目录，再在可回收 `AssemblyLoadContext` 里加载 |
| `javascript` | `.js` / `.cjs` / `.mjs` | Jint 引擎执行；有包内 `require` 或 `require` 依赖时先打包 |
| `typescript` | `.ts` | 先用内置 strip-and-rewrite 转译器转成 JS，再按 JS 方式执行 |

## 2. 清单字段

清单由 `ExtensionPackageManifest.Parse` 解析（`ExtensionPackageManifest.cs:84`）。字段名**大小写不敏感**，
允许尾随逗号与注释（`JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }`）。

| 字段 | 必填 | 类型 | 默认 | 说明 |
|---|---|---|---|---|
| `name` | **是** | string | — | 稳定标识，同时是状态桶名。必须 kebab-case 且 ≤ 64 字符（`ExtensionPackageLoader.NamePattern` = `^[a-z0-9]+(?:-[a-z0-9]+)*$`），全局唯一 |
| `kind` | 否 | `csharp` \| `javascript` \| `typescript`（也接受 `js` / `ts`） | 推断 | 未知值降级为推断并给 `Warning` |
| `displayName` | 否 | string | `null` | 面板展示名 |
| `version` | 否 | string | `null` | 版本号；`/extension list` 里未知时显示 `unknown` |
| `description` | 否 | string | `null` | 一句话描述 |
| `entry` | 脚本包**是** | string | `null` | 相对包根的入口文件。`typescript` 必须 `.ts`；`javascript` 必须 `.js`/`.cjs`/`.mjs` |
| `project` | C# 常用 | string | `null` | 相对包根的 csproj。省略时取包根下**唯一**的 `*.csproj` |
| `output` | 否 | string | 省略时 `Path.Combine("bin", "ext")` | **C# 构建根目录**（相对包根）。宿主每次写入 `<output>/<ticks>/`。脚本包不使用此字段 |
| `require` | 否 | string[] | `[]` | 必须先加载的包名 |
| `keywords` | 否 | string[] | `[]` | 搜索关键词 |
| `metadata` | 否 | object | `{}` | 未解释的任意 JSON（`IReadOnlyDictionary<string, JsonElement>`） |
| `repository` | 否 | string | `null` | 源码仓库 URL |
| `author` | 否 | string | `null` | 作者（也可写在 `metadata.author`） |
| `license` | 否 | string | `null` | 许可证标识 |
| `homepage` | 否 | string | `null` | 主页 |
| `minHostVersion` | 否 | string | `null` | 最低宿主版本。仅 `javascript` / `typescript` 脚本包由 `ScriptExtensionProvider` 执行检查：宿主低于要求时发出 `Error` 且不创建扩展；按数值组件逐段比较，去掉可选 `-suffix`，缺少组件按 `0`（`1.2` 与 `1.2.0` 相等）。要求值或宿主版本任一无法解析时发出 `Warning` 并继续加载。C# 包的 `ExtensionPackageBuilder` / `ExtensionPackageLoader` 不比较此字段，不据此拒绝加载 |
| `rootPath` / `manifestPath` | — | string | 加载器填入 | 不要手写；`WithPaths` 会覆盖 |

字段与消费方一一对应：

| 字段 | 由谁读取 |
|---|---|
| `name` | `ExtensionPackageLoader.LoadAsync` 校验 kebab-case；`ExtensionStateStore` 分桶 |
| `kind` | `ExtensionPackageBuilder.BuildAsync` 分支；`ScriptExtensionProvider.Supports` |
| `entry` | `ExtensionPackageBuilder.ResolveScriptEntry`；`ScriptExtensionProvider.PrepareAsync` 校验扩展名与存在性 |
| `project` | `ExtensionPackageBuilder.ResolveProject` |
| `output` | `ExtensionPackageBuilder.ResolveOutputRoot`（`ExtensionPackageManifest.Output` 的 XML 注释明确写着「脚本包不使用此字段」） |
| `require` | `ExtensionWorkspace.OrderByRequirements` + `ScanAsync` 的加载前检查；`ScriptModuleCompiler` 的跨包打包 |

### `kind` 的推断

省略 `kind`（或写了无法识别的值）时走 `InferKind`：

1. 有 `project`，或包根下有 `*.csproj` → `CSharp`；
2. `entry` 以 `.ts` 结尾 → `TypeScript`；
3. `entry` 以 `.js` / `.mjs` 结尾 → `JavaScript`；
4. 否则 `null`，最终回退 `CSharp`。

脚本包缺少 `entry` 是**错误**：`Required field 'entry' is missing for inferred kind <kind>.`

## 3. C# 包

### 3.1 最小实现

```csharp
using AiTools.Abstractions;
using AiTools.Extensions;

public sealed class HelloExtension : IExtension
{
    public string Name => "hello-csharp";                       // 同时是状态桶名
    public string? Description => "示例扩展。";

    public void Activate(IExtensionApi api)
    {
        api.RegisterCommand("hello", "问好", async (ctx, args, ct) =>
        {
            var count = ctx.State.Get<int>(Name, "count") + 1;
            ctx.State.Set(Name, "count", count);
            return CommandResult.Display($"第 {count} 次");
        });

        api.RegisterTool(
            ToolDefinition.FromJsonSchema("hello_count", "读计数", """{"type":"object","properties":{}}"""),
            (call, ctx, ct) => Task.FromResult($"{{\"count\":{ctx.State.Get<int>(Name, "count")}}}"),
            ToolExposure.Direct);

        api.On<BeforeAgentStartEvent>(ExtensionEvent.BeforeAgentStart, (e, ctx, ct) =>
        {
            e.PromptSections.Add($"## Hello\n\ncount={ctx.State.Get<int>(Name, "count")}");
            return Task.FromResult<BeforeAgentStartEvent?>(null);
        });
    }
}
```

真实签名（`Runtime/ExtensionEvents.cs:240`）：

```csharp
public interface IExtension
{
    string Name { get; }
    string? Description { get; }
    void Activate(IExtensionApi api);
}
```

`IExtensionApi` 的关键成员：

```csharp
void RegisterCommand(string name, string description, Func<CommandContext, CommandArguments, CancellationToken, Task<CommandResult>> handler, string? argumentHint = null);
void RegisterTool(ToolDefinition definition, Func<ToolCall, ToolContext, CancellationToken, Task<string>> execute, ToolExposure exposure = ToolExposure.Direct, bool readOnlyHint = false, string? @namespace = null, ToolReach? reachesTools = null);
Action On<TEvent>(ExtensionEvent @event, Func<TEvent, CommandContext, CancellationToken, Task<TEvent?>> handler) where TEvent : class;
ExtensionStateStore State { get; }
```

`On<TEvent>` 的泛型重载返回 `Task<TEvent?>`；返回 `null` 表示**不替换**事件对象，就地修改照样生效。

`CommandResult` 只有三个工厂：`CommandResult.Noop`、`CommandResult.Prompt(userMessage)`、`CommandResult.Display(assistantText)`。

### 3.2 csproj 要求

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\src\AiTools.Extensions\AiTools.Extensions.csproj" />
  </ItemGroup>
</Project>
```

- **必须 `net8.0`**：可卸载的 `AssemblyLoadContext` 只在 `NET8_0_OR_GREATER` 分支存在。
- 引用 `AiTools.Extensions` 只为**编译**。运行期这些程序集由宿主提供：`PackageLoadContext.IsHostAssembly` 把
  `AiTools.Extensions`、`AiTools.Abstractions`、`AiTools.Core`、`AiTools.Mcp`、`AiTools.Skills`、`System*`、`Jint*`
  直接交还宿主，**不要**把它们的 dll 复制进产物。
- 扩展类必须有**公共无参构造函数**：`ExtensionAssemblyLoader.CreateExtension` 用 `type.GetConstructor(Type.EmptyTypes)`。
  没有的话只会得到诊断 `Extension has no public parameterless constructor: <type>` 并被跳过。
- 一个程序集里可以声明多个 `IExtension`、多个 `IExtensionFactory`；两类都会被扫描到。

### 3.3 宿主如何构建

`ExtensionPackageBuilder.BuildAsync` 生成的命令是（`ExtensionPackageBuilder.cs:135` 附近）：

```text
dotnet build "<project>" -c Release -o "<outputRoot>/<ticks>" -m:1 -nodeReuse:false
```

其中 `outputRoot = manifest.Output ?? "bin/ext"`。要点：

- **Release** 配置，与你自己调试用的 `Debug` 输出互不干扰。
- `-m:1 -nodeReuse:false`：强制串行、不复用 MSBuild 节点。多个包同时构建时不会互相抢锁。
- `-o <fresh dir>`：每次构建一个新目录，名字是 `DateTime.UtcNow.Ticks`（并通过 `Interlocked` 保证单调递增）。
- 构建成功后 `ResolveAssembly` 依次尝试：`entry`（仅当它以 `.dll` 结尾）→ `<csproj 同名>.dll` → 目录里第一个非宿主 dll。
- 只保留最近 3 次构建：`PruneBuildDirectories(manifest, keep: 3)`，更旧的目录被删除（异常被吞掉）。
- 默认超时 120 秒（`timeout ?? TimeSpan.FromSeconds(120)`）。
- 找不到 `dotnet` 时诊断会被改写成：`The .NET SDK command 'dotnet' is unavailable. Install the .NET SDK and ensure dotnet is on PATH. …`

**为什么每次都要新目录**：程序集一旦被 `AssemblyLoadContext` 加载，Windows 上对应文件就被锁定，
无法覆盖同名文件。热重载必须能在**旧程序集仍然活着**的时候构建出**新程序集**，所以只能换目录；
旧目录要等 `ExtensionPackage.Unload()` → `PackageUnloadHandle.Dispose()` → `AssemblyLoadContext.Unload()`
之后才可能被回收。这也是 `PruneBuildDirectories` 保留 3 份而不是立刻删除的原因。

### 3.4 卸载的真实边界

| 目标框架 | 加载方式 | 能否卸载 |
|---|---|---|
| `net8.0` | 可回收 `AssemblyLoadContext`（`isCollectible: true`）+ `AssemblyDependencyResolver` | 能：`unloadHandle.Dispose()` 请求卸载 |
| `netstandard2.0` | `Assembly.LoadFrom` | **不能**，只给一条 `Warning`：`Assembly unloading is unavailable on netstandard2.0; restart the host to release this extension assembly.` |

即：宿主在 netstandard2.0 上仍可加载扩展包，但热重载只能替换实例，进程重启前程序集不会被释放。

## 4. JavaScript 包

### 4.1 入口契约

`entry` 指向一个 CommonJS 模块，导出一个对象：

```js
module.exports = {
  name: 'hello-js',                 // 可省略；省略时用 manifest 的 name
  description: '示例扩展。',         // 可省略；省略时用 manifest 的 description
  activate(api) {
    api.registerCommand('hello-js', '问好', async (ctx, args) => {
      const count = (ctx.state.get('count', 0) || 0) + 1;
      ctx.state.set('count', count);
      return args.isEmpty ? { display: '第 ' + count + ' 次' } : '这是第 ' + count + ' 次问候';
    });
  }
};
```

`ScriptExtension.Activate` 对入口做了这些约束（`ScriptExtension.cs:52` 起）：

- 旧式入口会被包进 `var module = { exports: {} }; var exports = module.exports;` 再执行，末尾取 `module.exports`；
  打包产物（路径含 `/.ai-tools/build/`）则直接执行并读 `globalThis.__script_extension_module`。
- 产物必须是 JavaScript：若准备好的文件仍是 `.ts`，直接抛
  `The prepared artifact must be executable JavaScript, not a TypeScript source file.`
- `module.exports` 必须是对象：`Script extension module.exports must be an object.`
- 必须导出 `activate` 函数：`Script extension must export an activate(api) function.`
- **`activate` 必须同步返回**：返回 Promise 会抛
  `activate(api) must be synchronous and return quickly; asynchronous activation is not supported.`

激活失败会记一条 `Error`（`JavaScript extension activation failed: …`）并向上抛，包不会进入已加载状态。

### 4.2 `api.*` 完整面

`CreateApiObject` 实际挂上的属性（`ScriptExtension.cs:180` 起）：

| 成员 | 真实签名 | 说明 |
|---|---|---|
| `api.name` | `string` | 扩展名 |
| `api.state` | `{ get(key, fallback), set(key, value) }` | 与 C# 的 `IExtensionApi.State` 同一份存储 |
| `api.registerCommand` | `(name, description, handler, hint?) => void` | `hint` 省略时按 `null` 传 |
| `api.registerTool` | `(definition, execute, options?) => void` | `options` 省略时按 `{}` 传 |
| `api.on` | `(eventName, handler) => unsubscribe` | 事件名只接受 **snake_case / kebab-case / 全大写**（`TryParseEvent` 先 `-`→`_` 再小写，与 `ToSnakeCase(枚举名)` 比较）。`before_agent_start`、`before-agent-start`、`BEFORE_AGENT_START` 都行；**`beforeAgentStart` 与 `BeforeAgentStart` 会抛** `Unknown extension event '<name>'.` |
| `api.registerProvider` | `(registration) => void` | 同 C# `ExtensionProviderRegistration` |
| `api.registerVirtualModel` | `(registration) => void` | 同 C# `VirtualModelRegistration` |
| `api.registerMcpServer` | `(server) => () => Promise` | 返回反注册函数 |
| `api.registerNamespace` | `(namespace) => void` | `{ name, aliases?, description?, instructions? }` |
| `api.registerLoadoutPreparer` | `(handler) => void` | **必须同步**，返回 Promise 会抛 `Loadout preparers must be synchronous.` |
| `api.registerToolRenderer` | `(handler) => void` | 返回 `undefined`/`null` 表示交给下一个渲染器 |

`registerTool` 的 `options` 读取字段：`exposure`（默认 `Direct`）、`readOnlyHint`（默认 `false`）、
`namespace`（默认 `null`）、`reachesTools`（`'*'` → `ToolReach.All()`，数组或字符串 → `ToolReach.Words(...)`）。
`definition` 读取 `name`、`description`、`parameters`；`parameters` 缺失时用 `{"type":"object","properties":{}}`。

### 4.3 `ctx` 桥

命令与工具处理器都拿到 `(…, ctx)`。`CreateContextCore` 提供（`ScriptExtension.cs:344` 起）：

| 成员 | 类型 | 行为 |
|---|---|---|
| `ctx.state.get(key, fallback)` | 任意 JSON | 缺失时返回 `fallback`；`fallback` 省略返回 `undefined` |
| `ctx.state.set(key, value)` | 任意可序列化值 | 传 `null`/`undefined` 会**删除**该键（`stateStore.Remove`） |
| `ctx.sessionId` | `string \| null` | 未持久化时为 `null` |
| `ctx.workingDirectory` | `string` | 命令与工具上下文均使用当前会话工作目录；脚本工具上下文由运行时从调用上下文传入 |
| `ctx.notify(message, kind?)` | `void` | `kind` 解析失败退化为 `Info` |
| `ctx.executeTool(name, argsJson)` | `Promise<string>` | 走完整 `tool_call`/`tool_result` 事件链与可达范围检查 |
| `ctx.appendEntry(kind, value)` | `void` | 不进模型上下文的自定义持久化 |
| `ctx.readEntry(kind)` | 任意 JSON | 读回最新条目 |
| `ctx.ui` | `object \| null` | 宿主无 UI 时为 `null`；否则 `{ hasDialogs, confirm, input, select, notify, setStatus }` |

`ctx.executeTool` 返回的是**文本**：未知工具得到 `{"error":"Unknown tool: <name>"}`，不可调用得到
`{"error":"Tool is not callable: <name>"}`，越界得到 `{"error":"Tool '<name>' is outside the reach of '<parent>'."}`。
**它不会因为这些情况抛异常**，必须检查文本（示例见 `samples/extensions/hello-ts-uses-js/lib/contract.ts` 的 `looksLikeToolError`）。

### 4.4 处理器返回约定

`registerCommand` 的返回值决定本轮走向（`ScriptExtension.cs` 命令包装）：

| 返回 | 结果 |
|---|---|
| `undefined` / `null` | `CommandResult.Noop` |
| 字符串 | `CommandResult.Prompt(value)` — 作为一条用户消息发给模型 |
| 含 `display` 属性的对象 | `CommandResult.Display(display.toString())` — 只显示，不调用模型 |
| 其它对象/数字/布尔 | `CommandResult.Prompt(<JSON>)` |

`registerTool` 的返回值：

| 返回 | 结果 |
|---|---|
| `undefined` / `null` | 空字符串 |
| 字符串 | 原样作为工具文本 |
| 其它 | JSON 序列化后作为文本 |

工具处理器内部抛异常时**不会**冒泡到模型，而是变成 `{"error": "<消息>"}` 文本。
命令处理器抛异常则抛 `InvalidOperationException`，由宿主处理。

**异步**：处理器可以是 `async`，`InvokeAsync` 会等待 Promise；超时由引擎的 `PromiseTimeout` 控制。

### 4.5 `require` 规则

脚本沙箱**没有 Node 标准库**。`ModuleContext.ExternalRequire`（`ScriptExtension.cs:695`）按顺序判断：

```csharp
if (specifier is "fs" or "node:fs" or "child_process" or "node:child_process" or "path" or "node:path" or "os" or "node:os" or "process" or "node:process")
    throw new InvalidOperationException("Node.js module '" + specifier + "' is unavailable in the script sandbox; filesystem and process APIs are disabled.");
if (_manifest.Require.Contains(specifier, StringComparer.OrdinalIgnoreCase))
    throw new InvalidOperationException("Sibling package module '" + specifier + "' is only available inside the prepared bundle.");
throw new InvalidOperationException("External module '" + specifier + "' is not permitted; require() is package-local only.");
```

三条规则：

1. **Node 内置模块一律拒绝**，`fs` / `child_process` 等七个（含 `node:` 前缀）都抛上面第一条。
2. **包内 `require('./lib/x')`** 在准备阶段被打包器静态解析（`ScriptModuleCompiler.BuildBundle`），运行期不走到
   `ExternalRequire`。解析范围限定在包目录内（`ResolveModule` 会校验 `IsWithin(packageRoot, …)` 并解符号链接），
   越界会抛 `unsupported module path: local require '<spec>' resolves outside the package directory`。
3. **`require('hello-js')` 形式（manifest `require` 里的兄弟包）** 也在准备阶段内联，因此运行期同样不走
   `ExternalRequire`；一旦走出来（例如打包时没识别到）就是上面第二条 —— 兄弟包只有打包这一条通道。

`globalThis.process` 与 `globalThis.Buffer` 被显式置为 `undefined`。

### 4.6 隔离保证

`ScriptExtension.Activate` 里每个引擎的配置：

| 选项 | 值 | 含义 |
|---|---|---|
| `options.Culture` | `CultureInfo.InvariantCulture` | 不受宿主区域设置影响 |
| `options.LimitRecursion(512)` | 512 | 递归深度上限，超限抛 `RecursionDepthOverflowException` → `JavaScript recursion depth exceeded.` |
| `options.Host.StringCompilationAllowed` | `false` | 禁止 `eval` / `new Function` 一类字符串编译 |
| `options.TimeoutInterval(_timeout)` | 默认 30s | 单次执行超时 |
| `options.Constraints.PromiseTimeout` | 默认 30s | 等待 Promise 的超时 |

**每个扩展实例一个引擎**（`ScriptExtension` 持有 `_engine`），会话关闭时 `Dispose()`；
`ScriptExtensionProvider` 的构造参数 `invocationTimeout` 可改默认 30 秒，`DefaultInvocationTimeout` 是那个常量。
引擎调用由 `SemaphoreSlim(1, 1)` 串行化，同一包的处理器不会并发进入同一个引擎。

`console.log/info/warn/error/debug` 不写 stdout，而是转成 `Info` 级 `PluginDiagnostic`，消息形如 `console.log: …`。

## 5. TypeScript 包

### 5.1 定位：strip-and-rewrite，无类型检查

`ScriptExtensionProvider` 的 XML 注释写明：

> TypeScript 默认使用内置、无工具链的 tokenizer strip-and-rewrite 转译器；**不执行类型检查**。

因此：

- **不需要 `tsc`、不需要 `node_modules`、不需要 `@types/*`**。`tsconfig.json` 完全是可选的，只为本地 `tsc --noEmit` 用；
  转译器根本不读它。
- 类型写错（包括 `any` 拼错成别的名字）**不会被发现**，转译器只做文本改写。
- 转译顺序（`TypeScriptTranspiler.Transpile`）：`RejectUnsupported` → `StripTypeDeclarations` → `RewriteEnums`
  → `RewriteModules` → `StripTypeSyntax`。拒绝检查在最前面，所以错误信息指向源文件的真实行号。
- 产物落在 `<package>/.ai-tools/build/<ticks>/index.js`（`ScriptExtensionProvider.PrepareAsync`）。

### 5.2 接受 / 拒绝表

下表**逐条用真实 `ScriptExtensionProvider.PrepareAsync` 跑过**（见文末验证说明），
诊断文本原样来自 `TypeScriptTranspiler.Unsupported`，格式为
`<path>:<line>: unsupported TypeScript construct: <construct>`。

| 构造 | 支持 | 诊断 |
| 变量 / 参数 / 返回值类型注解 `a: string`、`(): void` | ✅ | — |
| `interface` 声明（含泛型接口、可选属性） | ✅ | — |
| 内联对象类型：`{ g(k: string): number }`、`{ a: string; b?: number }`、`{ [k: string]: number }`、`{ a: { b: { c: string } } }` | ✅ | — |
| `type` 别名 | ✅ | — |
| enum 初始化器（恰好一个字面量 token，数值可前置一元负号）：`A = 4`、`A = -4`、`A = 1e-2`、`A = 0x10`、`A = 0b10`、`A = .5`、`A = 'a'`、`A = "a"` | ✅ | — |
| 函数 / 类**声明**上的泛型 `function f<T>(…)`、`class Box<T>` | ✅ | — |
| `as` 断言 | ✅ | 缺类型时 `as cast without a type` |
| 非空断言 `!` | ✅ | — |
| 可选参数 `b?: number`、可选属性 `a?: string` | ✅ | — |
| `declare const/var …`（非 `declare global`） | ✅ | — |
| 包内导入：`import x from './x'`、`import { a } from './x'`、`import { a as b } from './x'`、`import * as ns from './x'`（命名空间形式改写为 `const ns = require("./x");`） | ✅ | — |
| `export function/class/const`、`export { a as b }`、`export { a } from './x'`、`export default function` | ✅ | — |
| `import type { X } from './x'` | ✅ | 整条导入语句删除，不产生 `require` |
| 调用点类型实参 `f<string>(…)` | ❌ | `call-site generic arguments` |
| `namespace` 声明 | ❌ | `namespace declarations` |
| `module Foo { }` 声明 | ❌ | `module declarations` |
| 类 `implements` | ❌ | `class implements clauses` |
| `abstract` | ❌ | `abstract declarations` |
| `readonly` 修饰符 | ❌ | `readonly modifiers` |
| `satisfies` | ❌ | `satisfies expressions` |
| `keyof` | ❌ | `keyof type operators` |
| `infer` | ❌ | `infer type operators` |
| `unique symbol` | ❌ | `unique symbol types` |
| `declare global { }` | ❌ | `global declaration blocks` |
| 装饰器 `@x` | ❌ | `decorators` |
| JSX `const el = <div />` | ❌ | `JSX syntax` |
| 动态 `import('./x')` | ❌ | `dynamic import` |
| `export = value` | ❌ | `export = assignment; use module.exports = value instead` |
| `export * from './x'` | ❌ | `export * re-export` |
| enum 表达式：`enum E { A = 1 << 2 }`、`enum E { A = 1 + 1 }`、`enum E { A = someConst }`、`enum E { A = 'a' + 'b' }`、``enum E { A = `x${y}` }`` | ❌ | `enum initializer must be a string or numeric literal` |
| 字符串 enum 成员后省略数值成员 `enum E { A = 'a', B }` | ❌ | `numeric enum member after a string initializer must specify a value` |
| 复杂/计算 enum 成员名 | ❌ | `computed enum member name` |
| enum 初始化器不是字符串或数字字面量 | ❌ | `enum initializer must be a string or numeric literal` |
| 导出的声明没有简单名字 | ❌ | `exported declaration without a simple name` |
仅表中列出的 `import type { X } from './x'` 形式有此删除行为；若 `X` 仍在运行时代码中使用，转译时它已随整条导入消失，错误只会在打包/执行阶段暴露而非转译诊断；不要据此推断 `export type` 或其他类型专用语法也受支持。

`module.exports = value` 是**允许**的：拒绝检查只把 `module` 当作声明关键字当它后面不是 `.` 时才拦
（`if (token.Text == "module" && index + 2 < tokens.Count && tokens[index + 1].Text != "." && tokens[index + 2].Text == "{")`）。
但 `declare var module: { exports: unknown };` 这种写法仍会命中该检查被误拒 —— 所以**不要**在 `.ts` 里声明 `module` 全局，
改为在 `tsconfig.json` 里忽略这个类型错误（示例包用 `globals.d.ts` 只服务 `tsc`，该文件不在 `entry` 的转译路径上，
因为入口只走 `entry` 指向的那一个文件及其 `import` 图）。

### 5.3 枚举表达式的有意拒绝

枚举初始化器必须恰好是一个字面量 token；唯一允许的多 token 形式是「一元负号 + 一个数值 token」，任何表达式都主动拒绝。
例如，`enum E { A = 1 << 2 }` 过去会错误地输出 `e[e["A"] = 1] = "A"`，令 `E.A` 静默变成 `1` 而不是 `4`；
现在它会得到 `enum initializer must be a string or numeric literal` 的 `Error`。这是避免静默错误值的刻意规则，不是待绕过的偶发限制。

### 5.4 书写约定（为了让代码能转译）

结合上面的表，把 TypeScript 写成「转译器友好」的形状最省事：

| 约定 | 原因 |
|---|---|
| 只用**声明位置**的泛型 | 调用点类型实参被直接拒绝 |
| 使用接受表列出的 default / named / aliased / namespace 包内 import | 这些形式都会改写为包内 CommonJS `require` |
| 类型注解不写在**内联箭头**的返回位置 | 该组合曾把函数体一起吃掉；提到 `const handler = async (…): Promise<T> => {}` 再传入最稳 |
| 三元表达式与对象字面量先落到局部变量，再作为调用实参 | 复杂实参最容易踩到改写边界 |
| enum 初始化器只用接受表列出的字面量形式 | 任何表达式都主动拒绝，避免把计算式静默改写成错误成员值 |
| 内联对象类型可用于参数注解；可复用的契约仍可写成具名接口 | 两种形式都能剥离；按复用和可读性选择 |

示例包 `samples/extensions/hello-ts/index.ts` 与 `hello-ts-uses-js/index.ts` 都按这份清单写。

### 5.5 最小可运行片段

```ts
import { buildGreetingPrompt } from './lib/greet';

interface State {
  count: number;
}

interface StateStore {
  get(key: string, fallback: State): State;
  set(key: string, value: State): void;
}

interface Context {
  state: StateStore;
}

interface Api {
  registerCommand(name: string, description: string, handler: unknown, hint?: string): void;
}

const EmptyState: State = { count: 0 };

async function handleHello(context: Context): Promise<unknown> {
  const stored = context.state.get('count', EmptyState);
  const next = { count: stored.count + 1 };
  context.state.set('count', next);
  return buildGreetingPrompt(next.count);
}

const extension = {
  name: 'hello-ts',
  description: '…',
  activate(api: Api): void {
    api.registerCommand('hello-ts', '问好', handleHello, '<名字>');
  }
};

module.exports = extension;
```

`Context` / `StateStore` 在示例中使用具名接口是为了表达可复用契约；5.2 表中的内联对象类型（包括方法签名、可选成员、索引签名和嵌套对象）同样受支持并会被剥离。

`entry` 仍然是 `index.ts`，产物由 provider 生成，不要手写 `.ai-tools/build/` 里的文件。

## 6. 生命周期

### 6.1 `Activate` 必须快

`IExtension.Activate` 的 XML 注释：

> 在运行时登记命令、工具与事件处理器。此方法必须快速返回且不启动进程。

- 只登记，不做 I/O、不建立连接、不读大文件。
- 脚本侧对应的是 `activate(api)` **必须同步**，返回 Promise 会被拒。
- 需要长生命周期资源（MCP 连接、后台任务）时放到 `SessionStart`。

### 6.2 `SessionStart` / `SessionShutdown`

- `SessionStart`（`ExtensionEvent.SessionStart`）拿到 `{ sessionId, workingDirectory }`，是启动资源的地方。
- `SessionShutdown` **必须幂等**：切换会话、重载、进程退出前都可能调用。脚本扩展的 `SessionShutdown` 由运行时
  自动注册去 `Dispose()` 引擎，你自己注册的处理器不要假设只跑一次。
- 事件按注册顺序执行；转换类事件链式组合，拦截类（`ToolCall`、`BeforeProviderRequest`）fail-closed。

### 6.3 热重载保留什么、丢弃什么

`ExtensionWorkspace.ReloadPackageCoreAsync` 的顺序：

1. `Runtime.CloseMcpServersAsync()`（管道存在时）
2. `Session.ShutdownAsync()`
3. 对旧包的每个扩展 `Runtime.UnloadExtension(extension.Name)`
4. `existing.Unload()` — 清空扩展实例并 `Dispose` 卸载句柄
5. 重新 `LoadAsync` → 构建/转译 → 激活
6. `pipeline.ReloadAsync(…)`：`Runtime.Reset()` 后重新激活**全部**扩展，重建命令目录，新建 `ExtensionSession` 并 `StartAsync`

| | 结果 |
|---|---|
| **保留** | `ExtensionStateStore`：`Runtime.Reset()` 只清空注册表（命令、工具、订阅、provider、命名空间、renderer、激活集合），**不动 State**。所以计数、计划、目标等状态跨重载存活 |
| **丢弃** | C# 的 `AssemblyLoadContext`（旧实例卸载，新实例从新构建目录加载）；脚本的 Jint 引擎（`Dispose` 后重建，引擎内的全局变量全部丢失）；`ExtensionSession` 上下文（重建并重新 `StartAsync`） |
| **代价** | C# 包要跑一次 `dotnet build -c Release`；脚本包要重新打包/转译；所有扩展重新 `Activate`；所有 MCP 服务器重连。默认 120 秒构建超时，失败则以诊断形式返回、旧包已被卸载 |

`Watch = true` 时 `FileSystemWatcher` 监听 `.cs`、`.csproj`、`.js`、`.ts`、`.json`，
按包根做 `Debounce`（默认 750ms）合并，然后走同一条 `ReloadPackageAsync` 路径。
忽略目录内的变更不会触发重载。

## 7. 依赖

### 7.1 `require` 排序

`ExtensionWorkspace.OrderByRequirements` 做深度优先拓扑排序，然后 `ScanAsync` 在加载前再检查一次：

```csharp
var unavailableRequirement = candidate.Manifest.Require.FirstOrDefault(requirement => !loadedNames.Contains(requirement));
if (unavailableRequirement != null)
{
    candidate.Diagnostics.Add(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = candidate.Manifest.Name, Path = root, Message = "Required extension package is not loaded: " + unavailableRequirement });
    …
}
```

诊断文本：

| 情况 | 诊断 |
|---|---|
| 依赖在扫描范围内不存在 | `Required extension package was not found: <name>` |
| 依赖存在但没加载成功 | `Required extension package is not loaded: <name>` |
| 存在环 | `Extension package Require dependency cycle detected; package skipped.` |

注意 `Require` 只保证**加载顺序**，不做模块注入：它不会把对方的 `module.exports` 塞给你。

### 7.2 跨包调用

要驱动另一个包的能力，走运行时工具表：

```js
const text = await ctx.executeTool('hello_js_count', '{"increment":true}');
```

前提是对方包**已经注册**该工具（因此 `require` 顺序很重要），并且可达性允许。
完整可运行示例：`samples/extensions/hello-ts-uses-js`（`require: ["hello-js"]`，并在 `lib/contract.ts` 里
把「抛异常 / `{"error":…}` 负载 / 结构不符」三种失败都折叠成诊断字符串）。

## 8. 发布

### 8.1 仓库布局

一个仓库一个包，仓库根就是包根：

```
my-extension/
├── extension.json      # 必需
├── README.md
├── .gitignore          # 至少忽略 bin/ obj/ .ai-tools/ node_modules/
└── （C#：*.csproj + *.cs ／ JS：index.js + lib/ ／ TS：index.ts + tsconfig.json）
```

脚本包**只需要 `extension.json` + `entry` 指向的那个文件**（加它的包内依赖）。C# 包还需要 csproj 与源码，
以及到 `AiTools.Extensions` 的编译期引用（这是唯一允许的包外引用）。

### 8.2 安装

```text
/extension install <git-url> [name]
```

`ExtensionSourceManager.InstallAsync` 执行 `git clone --depth 1 "<url>" "<root>/<name>"`；
给了 `reference` 时再 `git -C "<dir>" checkout "<ref>"`。
- 目录名省略时从 URL 推导；必须是 kebab-case 且 ≤ 64 字符，否则
  `Extension directory name must be kebab-case and at most 64 characters.`
- 目标已存在时**拒绝覆盖**：`Install target already exists; refusing to overwrite it.`
- 更新用 `git -C "<dir>" pull --ff-only`（`UpdateAsync`），只快进。
- 删除前会校验是 Git 工作树（`IsGitWorkingTree`），不是就拒绝删除。

### 8.3 `keywords` / `metadata` 约定

示例包统一这样写：

```json
{
  "keywords": ["example", "csharp", "hello"],
  "metadata": { "author": "AiTools Contributors", "docs": "README.md" }
}
```

`keywords` 供发现/搜索；`metadata` 的值是原始 `JsonElement`，加载器不解释，宿主可以自行使用。
作者既可写在顶层 `author`，也可写在 `metadata.author` —— 顶层字段是强类型的，优先用它。

## 9. 调试

### 9.1 诊断长什么样

`PluginDiagnostic` 字段：`Kind`（`Info` / `Warning` / `Error`）、`PluginName`、`Path`、`Message`。
`ToString()` 的输出格式是：

```text
<Kind>: [<PluginName>] <Message> (<Path>)
```

加载过程**永不抛异常**（`PluginDiagnostic` 的注释明确写着「加载过程永不抛出异常」），失败都变成诊断。
唯一的例外是 `OperationCanceledException`（取消会继续向上传播）。

### 9.2 从哪看

| 入口 | 看到什么 |
|---|---|
| `/extension list` | 每个包一行：`<name> \| <Kind> \| <version> \| loaded/not loaded \| diagnostics: <N>` |
| `/extension reload <name>` / `build <name>` | 重载或强制构建后的同一行摘要 |
| `workspace.Diagnostics` | 扫描、监视、自动重载期间累积的诊断（`ScanAsync` 开始时清空） |
| `package.Diagnostics` | 单个包的诊断（`IList<PluginDiagnostic>`，可追加） |
| 脚本 `console.*` | `Info` 级诊断，消息前缀 `console.<level>: ` |

`/extension` 的子命令全集：`list`、`scan`、`install <git-url> [name]`、`update <name>|--all`、
`remove <name>`、`reload [name]|--all`、`build <name>`。未配置工作区时返回 `no workspace`。
`update` 与 `remove` 是破坏性操作：非交互宿主下必须带 `--yes`，否则返回
`… refused: interactive confirmation is unavailable; pass --yes to confirm.`

### 9.3 常见失败信息与成因

| 诊断 | 成因 |
|---|---|
| `Package manifest extension.json was not found.` | 目录里没有 `extension.json` |
| `The extension manifest must be a JSON object.` | 根部不是对象（数组/标量） |
| `Required field 'name' is missing.` | 缺 `name` |
| `Required field 'entry' is missing for inferred kind <kind>.` | 脚本包没写 `entry` |
| `Field '<x>' must be a string.` / `must be an array of strings.` | 类型写错 |
| `Package name must be kebab-case and at most 64 characters.` | `name` 含大写/下划线/超长 |
| `Duplicate extension package name; package names must be unique across the workspace.` | 两个包同名 |
| `No C# project was found. Set the 'project' field to a .csproj relative to the package root.` | C# 包既没写 `project`，包根下也没有唯一的 csproj |
| `dotnet build failed: …` | 编译失败；缺 SDK 时会被改写为 `The .NET SDK command 'dotnet' is unavailable. …` |
| `Build succeeded, but no extension assembly was found in the output directory.` | 产物里没有可加载的 dll（全被 `IsHostAssembly` 过滤了） |
| `Extension has no public parameterless constructor: <type>` | 扩展类缺无参构造 |
| `No usable IExtension implementations or factories were found.` | 程序集里没有 `IExtension` / `IExtensionFactory` |
| `Loaded extension has an empty name.` | `Name` 返回空 |
| `No IScriptExtensionProvider is configured for the <kind> package.` | 宿主没注入脚本提供者 |
| `The configured script provider does not support <kind> packages.` | 提供者不支持该 kind |
| `脚本扩展必须声明 package root 和 entry。` | 清单缺 `rootPath`/`entry`（一般是直接调用 provider 而非走加载器） |
| `入口文件必须位于扩展包目录内。` | `entry` 是绝对路径或 `..` 逃逸 |
| `TypeScript entry 必须是 .ts 文件。` / `JavaScript entry 必须是 .js、.cjs 或 .mjs 文件。` | `entry` 扩展名与 `kind` 不符 |
| `<path>:<line>: unsupported TypeScript construct: <x>` | 命中第 5.2 节的拒绝表 |
| `Script extension must export an activate(api) function.` | `module.exports.activate` 缺失或不可调用 |
| `activate(api) must be synchronous and return quickly; asynchronous activation is not supported.` | `activate` 是 `async` |
| `Unknown extension event '<name>'.` | `api.on` 用了 camelCase/PascalCase；改成 snake_case 或 kebab-case |
| `JavaScript handler timed out.` | 超过 `invocationTimeout`（默认 30s） |
| `JavaScript recursion depth exceeded.` | 超过 `LimitRecursion(512)` |
| `Node.js module '<x>' is unavailable in the script sandbox; filesystem and process APIs are disabled.` | `require('fs')` 一类 |
| `External module '<x>' is not permitted; require() is package-local only.` | require 了非包内、非 `require` 声明的模块 |
| `Required extension package is not loaded: <name>` | 依赖没加载成功（常见于对方包本身报错） |

## 10. 已验证的最小示例

两个可直接复制的骨架（完整版在 `samples/extensions/`）：

**C#（`samples/extensions/hello-csharp`）** — `extension.json` 用 `"kind": "csharp"`、`"project": "HelloCSharp.csproj"`、
`"output": "bin/ext"`；`HelloExtension.cs` 实现 `IExtension`，注册一条命令、一个工具、一个 `BeforeAgentStart` 处理器。

**TS（`samples/extensions/hello-ts`）** — `"kind": "typescript"`、`"entry": "index.ts"`；
`tsconfig.json` 用 `noEmit: false`、`outDir: ".ai-tools/build"`，仅供本地 `tsc --noEmit`；
实际转译由 provider 完成。

**JS（`samples/extensions/hello-js`）** — `"kind": "javascript"`、`"entry": "index.js"`；
`lib/greet.js` 演示包内 `require`。

**跨包（`samples/extensions/hello-ts-uses-js`）** — `"require": ["hello-js"]`，命令里 `ctx.executeTool` 驱动对方工具。

## 11. 明确没有实现的东西

为避免误解，以下能力**当前不存在**：

- **类型检查**：转译器只 strip + rewrite，不检查类型，不读 `tsconfig.json`。
- **`tsc` 加速或复用**：没有调用外部 TypeScript 编译器的路径。
- **netstandard2.0 上的卸载**：只能 `Assembly.LoadFrom`，程序集要等进程退出才释放。
- **脚本包的 `output`**：该字段对脚本包无效，产物路径由 `ScriptExtensionPreparation.ArtifactPath` 决定。
- **`minHostVersion` 的检查范围**：仅 JavaScript / TypeScript 脚本包由 `ScriptExtensionProvider` 强制检查；C# 包的加载器不执行版本比较（见第 2 节）。
- **任意 npm 依赖**：`require` 只接受包内文件与 `require` 声明的兄弟包；Node 内置模块被显式拒绝。
- **沙箱内的文件/进程访问**：`process`、`Buffer` 被置为 `undefined`，`fs`/`child_process` 抛异常。

---

## 本文档的验证方式

文中「接受 / 拒绝」表与 enum 表达式的有意拒绝说明依据真实实现；接受 / 拒绝表由 `ScriptExtensionProvider.PrepareAsync` 验证：

- **接受 / 拒绝表**：每个格子在临时目录里生成最小包（`extension.json` + 入口文件），调用真实的
  `ScriptExtensionProvider.PrepareAsync`，比较期望与实际，并记录返回的诊断原文。
- **示例包**：`hello-js`、`hello-ts`、`hello-ts-uses-js` 同样走真实 `PrepareAsync` 验证通过。
- **C# 片段**（3.1）：按文中代码原样抽出，用 `net8.0` + `AiTools.Extensions` 引用编译通过（0 错误）。
- **JS 片段**（4.1）：用 Node 加载并调用 `activate`，验证命令注册与 `ctx.state` 往返。
- **TS 片段**（5.5）：先 `tsc --noEmit`（`strict: true`）通过，再走真实 `PrepareAsync` 转译成功。
- **行为断言**：事件名归一规则（4.2）、`ctx.executeTool` 的错误文本与命令 / 工具上下文的 `ctx.workingDirectory`（4.3）都由真实实现确认。
