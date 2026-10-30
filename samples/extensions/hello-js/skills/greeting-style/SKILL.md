---
name: greeting-style
description: 说明 hello-js 示例扩展期望的问候语风格。当用户要求按示例扩展的约定写问候语时加载。
allowed-tools: [hello_js_count]
metadata:
  author: AiTools Contributors
---

# 问候语风格

写问候语时遵循以下约定：

1. 一句话内出现名字，不做自我介绍。
2. 不超过 20 个汉字；英文不超过 12 个单词。
3. 需要计数时先调用 `hello_js_count`（`{"increment": true}`），再用返回的数字。

示例：

> 你好，Ada！这是第 3 次问候。

参考 `references/tone.md` 可以读到更细的语气说明。
