---
name: commit-message
description: 根据改动内容生成符合约定式提交（Conventional Commits）规范的 Git 提交信息。当用户要求写 commit message、整理提交说明或审查提交信息时使用。
metadata:
  author: AiTools Demo
  version: "1.0"
---

# 生成 Conventional Commits 提交信息

## 格式

```
<type>(<scope>): <subject>

<body>

<footer>
```

## 类型（type）

| 类型 | 含义 |
|---|---|
| `feat` | 新功能 |
| `fix` | 修复缺陷 |
| `docs` | 仅文档变更 |
| `style` | 代码格式（不影响逻辑） |
| `refactor` | 重构（非新功能、非修复） |
| `perf` | 性能优化 |
| `test` | 测试相关 |
| `build` | 构建系统或依赖变更 |
| `ci` | CI 配置变更 |
| `chore` | 杂项维护 |

## 规则

1. subject 使用祈使句、小写开头、不超过 72 个字符、结尾不加句号。
2. scope 可选，表示影响范围（如 `auth`、`wpf`、`skills`）。
3. body 每行不超过 72 字符，说明「为什么」而不是「做了什么」——做了什么代码自己会说。
4. 破坏性变更必须在 footer 写 `BREAKING CHANGE: <说明>`，或在 type 后加 `!`。
5. 关联 issue 时在 footer 写 `Closes #123`。

## 输出要求

只输出最终提交信息，不要解释、不要 Markdown 代码围栏。如有多个逻辑变更，给出多条分别用空行分隔的提交信息，并说明各自覆盖哪些文件。

## 示例

输入：新增了设置页的 API Key 加密存储。

```
feat(settings): encrypt API keys at rest with DPAPI

Users' provider keys were stored as plaintext in settings.json, which
leaked credentials to anyone with read access to the profile directory.

Store them through ISecretProtector so the default implementation uses
Windows DPAPI scoped to the current user.

Closes #42
```
