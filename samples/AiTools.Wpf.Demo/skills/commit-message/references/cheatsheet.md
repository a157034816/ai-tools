# 提交信息速查

| 场景 | type | 示例 |
|---|---|---|
| 新增功能 | feat | `feat(chat): add stop button` |
| 修复缺陷 | fix | `fix(openai): handle null content` |
| 文档 | docs | `docs: document skill format` |
| 重构 | refactor | `refactor(core): extract retry policy` |
| 依赖升级 | build | `build(deps): bump System.Text.Json to 8.0.5` |
| 破坏性变更 | feat! | `feat(api)!: rename ChatRequest.Model` |

## 常见错误

- ❌ `update code` —— 没有信息量
- ❌ `fix bug` —— 修复了什么？
- ❌ `feat: Added a new button.` —— 过去式 + 句号 + 大写
- ✅ `feat(toolbar): add stop button to cancel streaming`
- ✅ `fix(gemini): preserve thoughtSignature across tool rounds`
