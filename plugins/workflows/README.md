# workflows

演示插件：提供 `/goal` 与 `/plan` 两个斜杠命令。

```
workflows/
├── .claude-plugin/plugin.json
└── commands/
    ├── goal.md
    └── plan.md
```

命令名带插件前缀（`/workflows:goal`），在没有同名冲突时也可直接用 `/goal` 调用。

正文中的 `$ARGUMENTS` 会被调用参数替换；未被正文消费的参数会自动以 `ARGUMENTS: ...` 追加到末尾。
