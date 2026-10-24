# Hello TypeScript（示例扩展包）

最小的 TypeScript 扩展包：一个目录 + `extension.json` + 一个带类型的入口模块。
能力与 `hello-js` 对齐——**带状态的命令**、**模型可调用的工具**、**每轮注入的提示词段落**——
区别在于状态、参数与返回值都有类型，且用 `lib/greet.ts` 演示包内 `import`。

## 结构

```
hello-ts/
├── extension.json   包清单（kind = typescript，entry 指向 index.ts）
├── index.ts         入口模块；末尾用 CommonJS 的 module.exports 导出
├── globals.d.ts     只为 tsc 声明 module 全局，不产生运行时代码
├── tsconfig.json    可选的 tsc 配置（outDir = .ai-tools/build）
├── lib/
│   └── greet.ts     包内第二个模块：enum、interface、type 别名与泛型
└── README.md
```

## 能力

| 能力 | 触发方式 | 行为 |
|---|---|---|
| 命令 | `/hello-ts <名字>` | 会话计数 +1 并记录最近名字；返回字符串即作为用户消息 |
| 命令 | 无参数执行 `/hello-ts` | 返回 `{ display: … }`，只显示、不调用模型 |
| 工具 | 模型调用 `hello_ts_count` | 参数 `{ "increment": true }` 先自增再返回。返回类型为 `CountToolResult` |
| 事件 | `before_agent_start` | 计数大于 0 时向 `event.promptSections` push `## Hello TypeScript` 段落 |

## 类型

| 名字 | 种类 | 作用 |
|---|---|---|
| `HelloState` | `interface` | 会话状态形状：`count` 与可选的 `lastGreeting` |
| `GreetingOptions` / `NameFormatter` | `interface` / `type` 别名 | `lib/greet.ts` 的公开契约 |
| `GreetingStyle` | `enum` | 问候措辞：`Plain` / `Warm` |
| `CountToolArguments` / `CountToolResult` | `interface` | 工具入参与返回值 |
| `CountToolSchema` / `CountToolDefinition` / `ToolRegistrationOptions` | `interface` | 工具定义与注册选项 |
| `CommandContextLike` / `StateStoreLike` / `ExtensionApiLike` | `interface` | 宿主注入对象中本扩展实际用到的子集 |
| `updateState<T>` | 泛型函数 | 在同一个函数里读状态、改状态、写回，避免命令与工具各自维护默认值 |

`index.ts` 使用的 TypeScript 语法限定在：类型注解、`interface`、`type` 别名、`enum`、
声明上的泛型、`as` 断言、可选参数与可选属性、包内 `import`。没有装饰器、命名空间、`satisfies`。

另外，为了配合宿主内置的 TypeScript 转译器（`AiTools.Extensions.Scripting`，不依赖 Node/tsc），
示例遵守四条书写约束——都属于上面这份语法清单的子集：

| 约束 | 写法 |
|---|---|
| 类型参数只写在**声明**位置 | `function updateState<T>(…)`；不写 `state.get<HelloState>(…)` |
| 不写 `import type` | 类型导出与值导出用同一条 `import`，或由函数返回值推导 |
| 返回类型注解不放在调用实参里的**内联箭头**上 | 处理器先提成 `const handleHello = async (…): Promise<unknown> => …` 再登记 |
| 三元表达式与对象字面量不直接做**调用实参** | 先落到局部变量：`const options = { name: …, style: … }` |

这些约束不影响可读性，却决定了代码能否被转译成可执行的 JavaScript。

## 为什么有 globals.d.ts

入口必须是 CommonJS（`module.exports = extension`）。TypeScript 只有在引入 `@types/node` 时才认识 `module`
全局；示例不想为一个全局变量引入开发依赖，因此用 `globals.d.ts` 声明这一行形状。
它只服务 `tsc`，被 `include` 收进编译单元，不产出任何 JavaScript。

## 类型检查与可选编译

```bash
# 只做类型检查（宿主的脚本 provider 自带转译，不需要这一步）
tsc --noEmit -p samples/extensions/hello-ts/tsconfig.json

# 想自己看产物时
tsc -p samples/extensions/hello-ts/tsconfig.json   # 输出到 .ai-tools/build/
```

`tsconfig.json` 中 `noEmit: false`、`outDir: ".ai-tools/build"`，与宿主脚本 provider 的产物目录约定一致；
`entry` 始终指向源码 `index.ts`，由 provider 负责转译。

## 注意

- 包内 `import` / `require` 只解析包目录内的文件；不要引用包外的相对路径。
- `strict: true`，`types: []` —— 示例不依赖任何 DefinitelyTyped 包。
- `.ai-tools/`、`node_modules/` 已在 `.gitignore` 中忽略。
