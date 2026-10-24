# Hello JS（示例扩展包）

最小的 JavaScript 扩展包：一个目录 + `extension.json` + 一个 CommonJS 入口模块。
它与 `hello-csharp` 能力对齐——**带状态的命令**、**模型可调用的工具**、**每轮注入的提示词段落**——
并额外用 `lib/greet.js` 证明包内 `require` 会被解析到包目录。

## 结构

```
hello-js/
├── extension.json   包清单（kind = javascript，entry 指向 index.js）
├── index.js         入口模块，导出 { name, description, activate(api) }
├── lib/
│   └── greet.js     包内第二个模块，被 index.js 以 require('./lib/greet') 引入
└── README.md
```

## 能力

| 能力 | 触发方式 | 行为 |
|---|---|---|
| 命令 | `/hello-js <名字>` | 会话计数 +1；返回字符串即作为一条用户消息发给模型 |
| 命令 | 无参数执行 `/hello-js` | 返回 `{ display: … }`，只显示、不调用模型 |
| 工具 | 模型调用 `hello_js_count` | 参数 `{ "increment": true }` 先自增再返回；否则只读。返回对象会 JSON 序列化 |
| 事件 | `before_agent_start` | 计数大于 0 时向 `event.promptSections` push `## Hello JS` 段落 |

## 入口模块形状

```js
module.exports = {
  name: 'hello-js',
  description: '…',
  activate(api) {
    api.registerCommand('hello-js', '…', async (ctx, args) => {
      // args 是解析后的对象：{ raw, positional, isEmpty }
      return args.isEmpty ? { display: '…' } : '…';
    });

    api.registerTool(
      { name: 'hello_js_count', description: '…', parameters: { type: 'object', properties: {} } },
      async (args, ctx) => ({ count: 3 }),      // args 是普通对象；返回值可为对象
      { exposure: 'direct' }
    );

    api.on('before_agent_start', async (event, ctx) => {
      event.promptSections.push('## Hello JS\n\nactive');
    });
  }
};
```

命令处理器的返回值决定本轮走向：`undefined` / `null` → 无动作；字符串 → 作为用户消息发给模型；
`{ display: '…' }` → 只在对话里显示，不调用模型。

## 上下文（`ctx`）

| 成员 | 说明 |
|---|---|
| `ctx.state.get(key, default)` | 读取会话状态，缺失时返回 default |
| `ctx.state.set(key, value)` | 写入会话状态；值必须是可 JSON 序列化的 |
| `ctx.sessionId` | 当前会话 ID；尚未持久化时为 `null` |
| `ctx.workingDirectory` | 当前工作目录 |
| `ctx.notify(message, kind)` | 向宿主 UI 发通知 |
| `ctx.executeTool(name, argsJson)` | 调用另一个已注册工具，返回其文本结果 |

## 包内 require

`index.js` 第一行就是 `require('./lib/greet')`，`lib/greet.js` 只依赖标准 JavaScript，不引入任何 npm 包。
这既是「包内模块可用」的证明，也让示例保持零依赖——**不要把 `node_modules` 塞进扩展包**。

## 自检

```bash
node -e "require('./samples/extensions/hello-js/index.js')"
```

只加载不激活：模块必须能被求值，并导出 `name` / `activate`。真正的注册发生在宿主调用 `activate(api)` 时。

## 注意

- `entry` 是相对包根的入口路径，`.js` 与 `.ts` 都支持。
- 状态按扩展名分桶，`hello-js` 与 `hello-csharp` 即使都用 `count` 键也互不干扰。
- `.ai-tools/`、`node_modules/` 已在 `.gitignore` 中忽略。
