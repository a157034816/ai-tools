# 会话恢复与分叉（/resume、/fork）

框架内置对话持久化，WPF 对话页提供两个命令：

| 命令 | 行为 |
|---|---|
| `/resume` | 打开会话选择器，列出已保存对话；点击「恢复」载入历史消息，点击「删除」移除 |
| `/fork` | 把当前对话复制成一个新会话并切换过去，原会话保持不变 |

## 存储层：`AiTools.Core.Sessions`

```csharp
using AiTools.Core.Sessions;

// 默认实现：每个会话一个 JSON 文件，原子写入
IConversationStore store = new JsonConversationStore(AiToolsPaths.GetConversationsRoot());

// 内存实现（测试 / 临时会话）
IConversationStore ephemeral = new InMemoryConversationStore();
```

| 接口 | 说明 |
|---|---|
| `IConversationStore` | `ListAsync` / `LoadAsync` / `SaveAsync` / `DeleteAsync` / `ForkAsync` |
| `JsonConversationStore` | 文件存储；写入采用临时文件 + 原子替换；损坏文件会被跳过而不影响列表 |
| `InMemoryConversationStore` | 内存存储，语义一致 |

### 数据模型

```csharp
public sealed class ConversationRecord
{
    public string Id { get; set; }                 // 仅字母、数字、- 和 _
    public string Title { get; set; }              // 缺省时取第一条用户消息（≤40 字 + 省略号）
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? ParentId { get; set; }          // 分叉来源
    public string? ProviderId { get; set; }
    public string? ModelId { get; set; }
    public List<ConversationMessage> Messages { get; set; }
}
```

`ConversationRecord.CreateFork()` 生成新 id、新时间戳、`ParentId = 原 id` 的深拷贝，原记录完全不变。

### 为什么 fork 是"复制而不是引用"

分叉后的会话是一份独立快照：继续对话、删除或再次分叉都不会影响父会话；`ParentId` 仅用于在 `/resume` 列表中显示「分叉」徽章与追溯来源。多次分叉会形成一条链（A → A' → A''），每一层都只记录直接父级。

## 接入 WPF

```csharp
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    // 默认已使用 ~/.ai-tools/conversations；
    // 这里演示自定义目录与关闭自动保存。
    ConversationStore = new JsonConversationStore(@"D:\my-app\data"),
    AutoSaveConversations = true
});
```

| 选项 | 默认 | 说明 |
|---|---|---|
| `ConversationStore` | `JsonConversationStore`（`~/.ai-tools`） | 会话存储实现 |
| `AutoSaveConversations` | `true` | 每轮对话完成后自动保存当前会话 |

### 对话页行为

- **自动保存**：每轮完成后保存；失败/取消的轮次已回滚，不会写入历史。
- **切换前保存**：`新建对话`、`/resume` 恢复、`/fork` 分叉之前都会先保存当前会话。
- **恢复**：载入消息、标题、以及当时使用的服务商/模型（若仍存在）；工具栏下方显示当前对话标题。
- **分叉**：标题自动加「（分叉）」后缀，列表中显示「分叉」徽章。
- **删除**：仅从存储中移除，不影响已打开的对话。

### ViewModel API

| 成员 | 说明 |
|---|---|
| `ResumeCommand` / `OpenResumePickerAsync()` | 打开选择器并刷新列表 |
| `LoadConversationCommand` / `LoadConversationAsync(item)` | 恢复会话 |
| `ForkCommand` / `ForkAsync()` | 分叉当前会话 |
| `DeleteConversationCommand` / `DeleteConversationAsync(item)` | 删除会话 |
| `CloseResumeCommand` | 关闭选择器 |
| `SavedConversations` | 选择器中的条目 |
| `IsResumePickerOpen` / `IsLoadingConversations` | 选择器状态 |
| `ConversationTitle` / `CurrentConversationId` / `CanFork` | 当前会话信息 |

`ConversationItemViewModel` 提供 `Title`、`Caption`（消息数 · 相对时间）、`IsFork` 用于列表渲染。

## 在非 WPF 场景使用

存储层与 UI 无关，可以直接用于控制台、Web 或服务端：

```csharp
var store = new JsonConversationStore(path);
var conversations = await store.ListAsync();
var record = await store.LoadAsync(conversations[0].Id);

record!.Messages.Add(ConversationMessage.FromChatMessage(ChatMessage.User("继续刚才的话题")));
await store.SaveAsync(record);

var fork = await store.ForkAsync(record, "尝试另一条思路");
```

## 安全与可靠性

- 会话 id 仅允许字母、数字、`-`、`_`，非法 id 直接拒绝，杜绝路径穿越。
- 写入使用临时文件 + 原子替换，进程中断不会留下半截文件。
- 单个损坏的会话文件只会在列表中被跳过，不会导致整个列表失败。
- 所有存储操作按顺序串行，避免并发写冲突。
