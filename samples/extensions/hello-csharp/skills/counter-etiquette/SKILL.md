---
name: counter-etiquette
description: hello-csharp 示例扩展的问候礼仪。当用户要求按该示例扩展的约定问好、或要检查计数工具返回结构时加载。
allowed-tools: [hello_csharp_count]
license: MIT
version: 1.0.0
metadata:
  author: AiTools Contributors
---

# 问候礼仪

`hello-csharp` 把问候次数保存在扩展状态桶 `hello-csharp` 的 `count` 键里，随会话持久化。

## 约定

1. 问好只用一句话，包含对方名字。
2. 需要知道当前次数时调用工具 `hello_csharp_count`：
   - `{"increment": true}`：先自增再返回，适合"又要问候一次"。
   - `{}` 或省略参数：只读，适合回答"已经问候几次"。
3. 工具返回 `{ "count": <数字>, "sessionId": "<会话 ID>" }`；`count` 从 0 开始。

## 不要做的事

- 不要把计数写进对话历史；它属于扩展状态，由 `BeforeAgentStart` 自动回灌。
- 不要在计数为 0 时说"第 0 次"。
