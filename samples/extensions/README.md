# 扩展包示例（samples/extensions）

四个**可独立成库**的示例扩展包，演示 AiTools 的磁盘扩展格式：一个目录 + 根部的 `extension.json`。
每个目录都是自包含的——复制出去、`git init` 就是一个可发布的扩展包仓库。

## 包一览

| 包 | `kind` | 演示内容 | 入口 |
|---|---|---|---|
| [`hello-csharp`](hello-csharp/) | `csharp` | `IExtension` + `IExtensionApi`：带状态计数器命令、工具、`BeforeAgentStart` 提示词段落；**随包技能 + `api.RegisterSkill` 动态技能**；加载时由宿主 `dotnet build` | `HelloCSharp.csproj` |
| [`hello-js`](hello-js/) | `javascript` | CommonJS 入口 `{ name, description, activate(api) }`；同样三件套；`lib/greet.js` 证明包内 `require`；**随包技能 + `api.registerSkill` 动态技能** | `index.js` |
| [`hello-ts`](hello-ts/) | `typescript` | 同上，但状态、参数、返回值全部有类型（`interface`/`enum`/`type`/泛型/`as`）；包内 `import` | `index.ts` |
| [`hello-ts-uses-js`](hello-ts-uses-js/) | `typescript` | **跨包依赖**：`require: ["hello-js"]` 保证加载顺序，命令处理器用 `ctx.executeTool` 驱动对方包的工具，并处理失败 | `index.ts` |

前三个包能力刻意对齐，便于横向比较三种写法；第四个演示包与包之间的关系：

| 能力 | C# | JS | TS | TS → JS |
|---|---|---|---|---|
| 命令 | `/hello [名字]` | `/hello-js [名字]` | `/hello-ts [名字]` | `/hello-uses-js [inc]` |
| 工具 | `hello_csharp_count` | `hello_js_count` | `hello_ts_count` | 调用 `hello_js_count` |
| 事件 | `BeforeAgentStart` | `before_agent_start` | `before_agent_start` | —（只在命令里跨包） |
| 技能 | `skills/counter-etiquette` + `hello-csharp-session` | `skills/greeting-style` + `hello-js-session` | — | — |
| 状态桶 | `hello-csharp` | `hello-js` | `hello-ts` | `hello-ts-uses-js` |

技能验证：`ExtensionSkillTests.SamplePackages_ShipSkillsThatLoadThroughThePublicLoader`
用真实的 `ExtensionPackageManifest.Parse` + `ExtensionSkillLoader.Load` 读取这两个示例包的 `skills/`，
文档与示例因此不会悄悄脱节。

## 清单字段

```json
{
  "name": "hello-ts-uses-js",
  "kind": "typescript",
  "entry": "index.ts",
  "require": ["hello-js"]
}
```

| 字段 | 说明 | 谁消费 |
|---|---|---|
| `name` | kebab-case 且不超过 64 字符；全局唯一，同时是状态桶名 | `ExtensionPackageManifest.Parse`、`ExtensionWorkspace.ScanAsync` |
| `kind` | `csharp` \| `javascript` \| `typescript`（也接受 `js`/`ts`） | `ParseKind` → `ExtensionPackageBuilder.BuildAsync` 分支 |
| `project` | 仅 C#：相对包根的 csproj 路径 | `ExtensionPackageBuilder.ResolveProject` |
| `entry` | 仅 JS/TS：相对包根的入口模块路径（`.js`/`.cjs`/`.mjs` 或 `.ts`） | `ExtensionPackageBuilder.ResolveScriptEntry`、`ScriptExtensionProvider.PrepareAsync` |
| `output` | 仅 C#：**构建根目录**，相对包根；省略时默认 `bin/ext`。宿主每次构建写入 `<output>/<ticks>/`，再在该目录内定位程序集 | `ExtensionPackageBuilder.ResolveOutputRoot` / `ResolveAssembly` |
| `require` | 必须先加载的其他包名 | `ExtensionWorkspace.OrderByRequirements` |
| `keywords` / `metadata` / `repository` / `license` | 发现与展示用元数据，不参与加载 | `ExtensionPackageManifest` |

清单解析由 `ExtensionPackageManifest.Parse` 完成：字段名大小写不敏感，允许尾随逗号与注释；
`name` 缺失、脚本包缺少 `entry` 都是**错误**，未知 `kind` 会降级为按 `entry`/`project` 推断并给出警告。

> 注意 `output` 的语义：它不是 `bin/Debug/net8.0`，而是**构建根目录**。宿主自己运行
> `dotnet build … -o <output>/<ticks>`，所以示例里写 `"output": "bin/ext"`；
> 单独执行 `dotnet build` 得到的是 `bin/Debug/net8.0`，两者互不冲突，且都被 `.gitignore` 覆盖。

## 宿主如何加载这个目录

工作区扫描的是「根目录的**直接子目录**」，所以根指向 `samples/extensions` 本身（不是某个包目录）。
真实 API 是 `ExtensionWorkspaceOptions` / `ExtensionWorkspace.ScanAsync` / `ReloadPackageAsync`：

```csharp
using AiTools.Extensions.Packages;

await using var workspace = new ExtensionWorkspace(new ExtensionWorkspaceOptions
{
    Directories = new[] { "samples/extensions" },   // 扫描每个直接子目录里的 extension.json
    DataDirectory = null,                            // 省略时用 ~/.ai-tools/extensions/data
    BuildOnLoad = true,                              // C# 包在加载时构建；脚本包装译在 PrepareAsync 里
    EnableDotnetBuild = true,
    Watch = false,                                   // true 时监听文件变化并自动重载
    ScriptProvider = new ScriptExtensionProvider()   // JS/TS 必需；不注入则脚本包报错跳过
});

IReadOnlyList<ExtensionPackage> packages = await workspace.ScanAsync();
foreach (var package in packages)
{
    Console.WriteLine($"{package.Manifest.Name}: loaded={package.IsLoaded} diagnostics={package.Diagnostics.Count}");
}

// 修改某个包之后只重载它（返回 null 表示名称不存在）：
ExtensionPackage? reloaded = await workspace.ReloadPackageAsync("hello-js");
```

`ScanAsync` 的行为：

1. 收集各根目录下**直接子目录**中带 `extension.json` 的目录，跳过 `bin`、`obj`、`node_modules`、`.ai-tools`、`.git`；
2. 按 `require` 做依赖排序（缺失依赖与环都会变成该包的错误诊断）；
3. 逐个交给 `ExtensionPackageLoader.LoadAsync`：C# 包先构建再按程序集加载，脚本包先经 `IScriptExtensionProvider.PrepareAsync` 转译/打包；
4. 依赖未加载成功的包会被标记 `Required extension package is not loaded: <name>`，不会带着半个依赖启动。

命令行一行指向同目录的等价写法：

```bash
AITOOLS_EXTENSIONS=samples/extensions dotnet run --project <宿主项目>
```

`ExtensionWorkspacePaths.ResolveDefaultRoots` 会读取该环境变量（`Path.PathSeparator` 分隔），
此外还包含 `.ai-tools/extensions`、用户本地数据目录与仓库根的 `.ai-tools/extensions`。

## 自检命令

```bash
# C#：单独构建（加载时宿主也会做同样的事，只是输出目录不同）
dotnet build samples/extensions/hello-csharp/HelloCSharp.csproj -m:1 -nodeReuse:false

# JS：模块必须能被求值并导出 activate
node -e "require('./samples/extensions/hello-js/index.js')"

# TS：纯类型检查，不需要宿主或 @types/node
tsc --noEmit -p samples/extensions/hello-ts/tsconfig.json
tsc --noEmit -p samples/extensions/hello-ts-uses-js/tsconfig.json
```

## 打包契约

- 每个包只写自己目录内的文件，产物目录（`bin/`、`obj/`、`.ai-tools/`）各自 `.gitignore`。
- C# 包对 `src/AiTools.Extensions` 的引用是**唯一**允许的包外引用，且只用于编译：`AiTools.Extensions.dll` 在运行期由宿主提供。
- JS/TS 包零依赖，不携带 `node_modules`；包内 `require`/`import` 只解析包目录内的文件。
- 跨包调用只走 manifest 的 `require` 声明与 `ctx.executeTool`，不存在直接 `require('hello-js')` 之外的第二条通道。
- 扩展到宿主的所有交互都通过 `IExtensionApi`（C#）或 `activate(api)` 传入的注册接口（JS/TS），不存在隐式全局。
