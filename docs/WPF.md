# AiTools.Wpf

可复用的 WPF 预制项目：**设置页**（供应商与模型配置）+ **对话页**（流式聊天、工具调用、多模态输入），宿主通过**全局配置 + 接口实现**注册即可集成。

## 集成方式

### 1. 启动时初始化全局配置

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    AiToolsWpf.Initialize(new AiToolsWpfOptions
    {
        DefaultSystemPrompt = "你是简洁、准确的中文技术助手。",
        EnableTools = true,
        MaxToolRounds = 4,

        // 可选：自定义设置存储（默认 %LOCALAPPDATA%\AiTools\settings.json）
        SettingsStore = new JsonFileSettingsStore(@"D:\my-app\settings.json", new DpapiSecretProtector()),

        // 可选：自定义 Provider 工厂（密钥轮换、代理、日志等）
        // ProviderFactory = new MyProviderFactory(),

        // 可选：替换服务商模板列表
        // Providers = new[] { new ProviderDescriptor { Id = "my-gateway", ... } },

        // 可选：拦截请求构建 / 观测轮次完成
        RequestBuilder = context => null,
        OnTurnCompleted = context => { /* 记录日志或遥测 */ }
    });

    base.OnStartup(e);
}
```

### 2. 把页面放进宿主窗口

```xml
<Window xmlns:wpf="clr-namespace:AiTools.Wpf.Views;assembly=AiTools.Wpf">
    <Grid>
        <wpf:ChatPage x:Name="ChatView" />
    </Grid>
</Window>
```

```csharp
// 对话页请求打开设置时跳转
ChatView.ViewModel.SettingsRequested += (_, _) => ShowSettingsPage();
```

或通过代码创建：

```csharp
var chatPage = AiToolsWpf.CreateChatPage();
var settingsPage = AiToolsWpf.CreateSettingsPage();
```

### 3. 注册宿主工具（可选）

实现 `IChatToolProvider` 并注册，模型即可调用宿主能力：

```csharp
public sealed class OrderToolProvider : IChatToolProvider
{
    public IReadOnlyList<ToolDefinition> GetTools() => new[]
    {
        ToolDefinition.FromJsonSchema("query_order", "查询订单状态",
            """{"type":"object","properties":{"orderId":{"type":"string"}},"required":["orderId"]}""")
    };

    public async Task<string> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        using var args = call.ParseArguments();
        var orderId = args.RootElement.GetProperty("orderId").GetString();
        return await _orders.LookupAsync(orderId!, ct);
    }
}

AiToolsWpf.RegisterToolProvider(new OrderToolProvider());
```

## 可替换的接口

| 接口 | 用途 | 默认实现 |
|---|---|---|
| `ISettingsStore` | 设置持久化 | `JsonFileSettingsStore`（原子写入） |
| `ISecretProtector` | API Key 加密 | `DpapiSecretProtector`（Windows DPAPI，按用户） |
| `IProviderFactory` | 从配置创建聊天客户端 | `BuiltInProviderFactory`（OpenAI/Anthropic/Gemini） |
| `IChatToolProvider` | 向模型暴露宿主工具 | 无（可选注册） |

## 全局配置项

| 属性 | 说明 |
|---|---|
| `SettingsStore` | 设置存储实现 |
| `ProviderFactory` | Provider 工厂实现 |
| `SecretProtector` | 密钥保护实现（默认存储使用） |
| `Providers` | 设置页可选的服务商模板 |
| `DefaultSystemPrompt` | 新对话的默认系统提示词 |
| `EnableTools` | 是否向模型发送注册的工具 |
| `MaxToolRounds` | 自动工具调用最大轮数 |
| `RequestBuilder` | 自定义请求构建钩子（返回 null 用默认逻辑） |
| `OnTurnCompleted` | 每轮完成后的回调（日志/遥测） |
| `PluginsDirectory` | 插件目录（Claude Code 格式）；目录本身是插件时按单个插件加载，否则递归发现 |
| `Extensions` | 直接传入已加载的 `PluginCatalog`，优先于 `PluginsDirectory` |
| `ExtensionOptions` | 插件命令行为（项目根、数据目录、超时、短名冲突策略） |
| `AllowPluginShellExecution` | 是否执行命令正文里的 `` !`cmd` `` 注入，默认 false |

## 对话页功能

- 供应商 / 模型选择器（自动过滤不可用项）
- 流式输出（文本 + 推理过程分色显示）
- 自动工具调用循环（`CompleteWithToolsAsync`；需要 ProviderData 保真时使用 `StreamWithToolsExactAsync`）
- 停止生成、新建对话、清空、重发、复制回复
- Token 用量展示
- 空状态引导（无可用服务商时跳转设置）
- Enter 发送 / Shift+Enter 换行
- 斜杠命令面板：输入 `/` 列出插件命令，`↑`/`↓` 选择、`Enter` 插入、`Esc` 关闭
- 内置斜杠命令：`/resume`、`/fork`、`/new`、`/clear`、`/settings`、`/help [关键字]`

### ChatViewModel 公开 API

| 成员 | 说明 |
|---|---|
| `InitializeAsync()` | 加载设置、构建供应商/模型选择器，可重复调用 |
| `SendAsync()` / `SendCommand` | 发送输入框内容 |
| `StopCommand` / `Stop()` | 取消当前请求 |
| `ClearCommand` / `NewConversationCommand` | 清空对话 |
| `RetryCommand` | 重发最后一条用户消息 |
| `CopyLastCommand` | 复制最后一条助手回复 |
| `OpenSettingsCommand` | 触发 `SettingsRequested` 事件 |
| `SelectedProvider` / `SelectedModel` | 当前选择，变更时自动持久化 |
| `Messages` | 消息视图模型集合（文本/推理/工具调用/用量/错误） |
| `InputText` | 输入框内容 |
| `IsBusy` / `HasUsableProvider` / `StatusMessage` / `StatusKind` | UI 状态 |
| `SettingsRequested` / `ScrollToEndRequested` | 事件，用于宿主导航与自动滚动 |
| `AvailableCommands` / `IsCommandPaletteOpen` / `SelectedCommand` | 命令面板（扩展命令 + 插件命令） |
| `ApplyCommandCommand` / `CloseCommandPaletteCommand` | 面板操作 |
| `Pipeline` / `Commands` | 扩展管道与合并后的命令目录 |
| `TryInvokeBuiltInCommandAsync(text)` | 处理会话级斜杠命令；返回 true 表示已消费 |
| `TryExecuteCommandAsync(text)` | 执行扩展/插件命令（`Prompt`/`Display`/`Noop`/`Error`） |
| `RefreshAvailableCommands()` | 扩展或插件重新加载后刷新命令列表 |

## 扩展与插件

```csharp
AiToolsWpf.Initialize(new AiToolsWpfOptions
{
    EnableExtensions = true,                                    // 内置 /goal、/plan
    PluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins") // 可选
});
```

扩展用 C# 注册命令/工具/事件，状态随 `ConversationRecord.ExtensionState` 一起保存与恢复（`/resume` 恢复、`/fork` 复制、`/new` 清空）。插件提供的 `commands/*.md` 与 `skills/*/SKILL.md` 同样成为斜杠命令，`skills` 经 `load_skill` 按需加载，`.mcp.json` 服务器自动连接。完整语义见 [`EXTENSIONS.md`](EXTENSIONS.md)。

## 设置页功能

- 从内置模板添加服务商（OpenAI、Anthropic、Gemini、DeepSeek、Kimi、通义千问、Ollama、自定义）
- 编辑显示名、协议、Base URL、API Key（可显示/隐藏）、启用状态
- 模型列表管理（添加/删除/启用/显示名）
- 连接测试：发送最小请求验证 Base URL、密钥与模型是否可用，显示耗时与回复摘要
- 对话默认系统提示词（留空回落到宿主全局默认值）
- 保存前校验：协议合法、URL 为 http/https 绝对地址、启用项必须有模型；服务商 ID 自动去重
- 删除需二次确认（页面内联，不弹窗）
- 保存/重新加载；API Key 落盘前自动加密；保存采用原子写入；保存时保留对话页的供应商/模型选择

## 设计约束

- 库内不使用 `MessageBox` 或阻塞式对话框，所有错误以 UI 状态呈现。
- 所有 ViewModel 均可在 XAML 设计器中构造（未初始化时安全降级）。
- 主题为深色，资源字典位于 `Themes/DarkTheme.xaml` 与 `Themes/Converters.xaml`，页面内部自行合并，宿主无需额外配置。

## 运行演示

```bash
dotnet run --project samples/AiTools.Wpf.Demo
```

演示程序包含：供应商配置、对话、以及两个本地工具（当前时间、计算器）。
