# Hello TS uses JS（跨包依赖示例）

一个 **TypeScript** 扩展包，演示两件事：

1. 用 manifest 的 `require` 声明**包与包之间的加载顺序**；
2. 在自己的命令处理器里用 `ctx.executeTool` **驱动另一个包的工具**，并把失败折叠成可展示的诊断，而不是抛异常。

它依赖 [`../hello-js`](../hello-js/)，后者提供 `hello_js_count` 工具。

## 结构

```
hello-ts-uses-js/
├── extension.json   包清单（kind = typescript，entry = index.ts，require = ["hello-js"]）
├── index.ts         入口：命令 /hello-uses-js，内部调用 hello_js_count
├── globals.d.ts     只为 tsc 声明 module 全局，不产生运行时代码
├── tsconfig.json    可选的 tsc 配置（outDir = .ai-tools/build）
├── lib/
│   └── contract.ts  跨包调用的契约与实现（结果类型、错误判定、负载校验）
└── README.md
```

## 依赖怎么生效

```json
{
  "name": "hello-ts-uses-js",
  "kind": "typescript",
  "entry": "index.ts",
  "require": ["hello-js"]
}
```

`ExtensionWorkspace.ScanAsync` 按 `require` 做拓扑排序后再逐个加载，所以：

- `hello-js` 先加载，它注册的 `hello_js_count` 才存在于运行时工具表里；
- 若 `hello-js` 不存在（或没加载成功），本包会拿到
  `Required extension package is not loaded: hello-js` 的错误诊断，而不会带着半个依赖启动。

`require` 只保证**加载顺序**，不注入模块。要调用对方的工具，仍然通过宿主提供的 `ctx.executeTool`：

```ts
const outcome = await invokeUpstreamTool(context, 'hello_js_count', argumentsJson);
```

## 为什么把调用放进 `lib/contract.ts`

`ctx.executeTool` 不抛异常表示工具失败——扩展运行时的工具错误统一以 `{"error": …}` 文本返回。
如果调用方只看返回值不看内容，就会把错误当成正常计数继续用。因此 `lib/contract.ts` 在边界处做三件事：

| 检查 | 失败时 |
|---|---|
| `executeTool` 本身抛异常（对方包没加载、工具不可达、超时） | `{ ok: false, reason: '调用被宿主拒绝' }` |
| 返回文本是 `{"error": …}` 负载 | `{ ok: false, reason: '对方工具返回错误负载' }` |
| 返回文本不是 `{ count, sessionId }` 结构 | `{ ok: false, reason: '返回结果不是预期的计数结构' }` |

三条失败路径都变成**诊断字符串**从命令返回，宿主会把它当作一条用户消息显示，而不会中断本轮。

## 命令

| 触发 | 行为 |
|---|---|
| `/hello-uses-js` | 以 `{}` 调用 `hello_js_count`，显示对方当前计数 |
| `/hello-uses-js inc` | 以 `{"increment":true}` 调用，让对方计数 +1 |

成功时返回 `{ display: … }`（只显示、不调用模型）；失败时返回字符串（作为用户消息发给模型，便于模型据此排查）。

## 类型

| 名字 | 种类 | 作用 |
|---|---|---|
| `CallOutcome` | `interface`（`lib/contract.ts`） | 跨包调用结果：`ok` / `text` / `reason` |
| `CountPayload` | `interface`（`lib/contract.ts`） | 对方工具返回值：`count` / `sessionId` |
| `ToolCaller` | `interface`（`lib/contract.ts`） | 只声明 `executeTool` 的最小宿主能力 |
| `BridgeState` | `interface`（`index.ts`） | 本包状态：成功调用次数与最近错误 |
| `invokeUpstreamTool` | 具名函数 | 把三种失败折叠成 `CallOutcome` |

类型只在声明处使用（接口、函数签名、返回注解）；不写 `import type`，也不写调用点类型实参
（`f<T>(…)`），因为扩展转译器只剥离声明位置的类型参数。

两个与转译器相关的小约束已经体现在代码里：

- 处理器先提成 `const` 再登记，返回类型注解才不会在转译时连函数体一起被吃掉；
- 三元表达式与对象字面量不直接写进调用实参，先落到局部变量。

## 类型检查

```bash
tsc --noEmit -p samples/extensions/hello-ts-uses-js/tsconfig.json
```

`tsconfig.json` 用 `strict: true`、`types: []`、`noEmit: false`、`outDir: ".ai-tools/build"`，
与宿主脚本 provider 的产物目录约定一致；`entry` 始终指向源码 `index.ts`。

## 注意

- 本包只依赖 `hello-js` 的**工具名**，不 `require` 对方的源码；包间没有源码耦合。
- 想让示例跑起来，宿主必须同时扫描到 `hello-js` 与本包，例如把 `samples/extensions` 整个目录交给工作区。
- `.ai-tools/`、`node_modules/` 已在 `.gitignore` 中忽略。
