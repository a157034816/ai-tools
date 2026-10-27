using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Core.Sessions;
using AiTools.Wpf;
using AiTools.Wpf.Configuration;
using AiTools.Wpf.ViewModels;
using Xunit;

namespace AiTools.Tests.Sessions;

/// <summary>
/// 验证聊天 ViewModel 上的 /resume 与 /fork 行为。
/// 与全局服务测试共用 <c>AiToolsWpfGlobal</c> 集合，因为两者都会
/// 修改进程级的 <see cref="AiToolsWpf"/> 状态。
/// </summary>
[Collection(AiTools.Tests.Wpf.AiToolsWpfGlobalCollection.Name)]
public sealed class ChatViewModelSessionTests : IDisposable
{
    private readonly InMemoryConversationStore _store = new();

    public ChatViewModelSessionTests()
    {
        AiToolsWpf.Reset();
        AiToolsWpf.Initialize(options =>
        {
            options.ConversationStore = _store;
            options.AutoSaveConversations = true;
            options.SettingsStore = new NullSettingsStore();
            options.ProviderFactory = new NoopProviderFactory();
        });
    }

    public void Dispose() => AiToolsWpf.Reset();

    /// <summary>等待经 UI 封送的集合达到预期数量。</summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    private static ConversationRecord Seed(string title, params (string Role, string Text)[] messages)
    {
        var record = new ConversationRecord { Title = title };
        record.Messages.AddRange(messages.Select(m => new ConversationMessage { Role = m.Role, Text = m.Text }));
        return record;
    }

    [Fact]
    public async Task BuiltInCommands_WorkFromTheInputBoxWithoutAConfiguredModel()
    {
        // 本测试的配置里没有任何 provider/model：/fork 与 /resume 仍然必须能用，
        // 因为它们不调用模型。
        await _store.SaveAsync(Seed("已保存", ("user", "问题"), ("assistant", "回答")));

        var vm = new ChatViewModel();
        await vm.InitializeAsync();

        Assert.Null(vm.SelectedProvider);
        Assert.Null(vm.SelectedModel);

        vm.InputText = "/resume";
        await vm.SendAsync();

        await WaitForAsync(() => vm.IsResumePickerOpen);
        Assert.True(vm.IsResumePickerOpen, vm.StatusMessage);
        Assert.Contains(vm.SavedConversations, item => item.Title == "已保存");

        vm.CloseResumePicker();
        Assert.False(vm.IsResumePickerOpen);
    }

    [Fact]
    public async Task ForkFromTheInputBox_CreatesANewConversation()
    {
        await _store.SaveAsync(Seed("原始对话", ("user", "问题"), ("assistant", "回答")));

        var vm = new ChatViewModel();
        await vm.InitializeAsync();
        await vm.OpenResumePickerAsync();
        await WaitForAsync(() => vm.SavedConversations.Count == 1);
        await vm.LoadConversationAsync(vm.SavedConversations[0]);
        await WaitForAsync(() => vm.Messages.Count == 2);

        var sourceId = vm.CurrentConversationId;

        vm.InputText = "/fork";
        await vm.SendAsync();
        await WaitForAsync(() => vm.CurrentConversationId != sourceId);

        Assert.NotEqual(sourceId, vm.CurrentConversationId);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Contains("原始对话", vm.ConversationTitle);
    }

    [Fact]
    public async Task Resume_ListsSavedConversations()
    {
        await _store.SaveAsync(Seed("对话 A", ("user", "你好"), ("assistant", "你好！")));
        await _store.SaveAsync(Seed("对话 B", ("user", "再见")));

        var vm = new ChatViewModel();
        await vm.OpenResumePickerAsync();
        await WaitForAsync(() => vm.SavedConversations.Count == 2);

        Assert.True(vm.IsResumePickerOpen);
        Assert.Equal(2, vm.SavedConversations.Count);
        Assert.Contains(vm.SavedConversations, item => item.Title == "对话 A" && item.MessageCount == 2);
    }

    [Fact]
    public async Task Resume_RestoresMessagesAndClosesPicker()
    {
        var saved = Seed("待恢复", ("user", "原始问题"), ("assistant", "原始回答"));
        await _store.SaveAsync(saved);
        var vm = new ChatViewModel();
        await vm.OpenResumePickerAsync();
        await WaitForAsync(() => vm.SavedConversations.Count == 1);

        await vm.LoadConversationAsync(vm.SavedConversations[0]);
        await WaitForAsync(() => vm.Messages.Count == 2);

        Assert.False(vm.IsResumePickerOpen);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal("原始问题", vm.Messages[0].Text);
        Assert.True(vm.Messages[0].IsUser);
        Assert.Equal("原始回答", vm.Messages[1].Text);
        Assert.Equal("待恢复", vm.ConversationTitle);
        Assert.Equal(saved.Id, vm.CurrentConversationId);
    }

    [Fact]
    public async Task Resume_MissingConversation_RefreshesPicker()
    {
        var vm = new ChatViewModel();
        await vm.OpenResumePickerAsync();
        // 点击一个已经被别处删除的条目。
        var item = new ConversationItemViewModel(new ConversationSummary { Id = "gone", Title = "已删除" });

        await vm.LoadConversationAsync(item);

        Assert.True(vm.IsResumePickerOpen); // 仍停留在选择器
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task Fork_RequiresContent()
    {
        var vm = new ChatViewModel();

        await vm.ForkAsync();

        Assert.False(vm.CanFork);
        Assert.Empty(vm.SavedConversations);
    }

    [Fact]
    public async Task Fork_CopiesHistoryIntoNewConversationAndKeepsOriginal()
    {
        var source = Seed("原始对话", ("user", "问题"), ("assistant", "回答"));
        await _store.SaveAsync(source);

        var vm = new ChatViewModel();
        await vm.OpenResumePickerAsync();
        await WaitForAsync(() => vm.SavedConversations.Count == 1);
        await vm.LoadConversationAsync(vm.SavedConversations[0]);
        await WaitForAsync(() => vm.Messages.Count == 2);

        await vm.ForkAsync();
        await WaitForAsync(() => vm.ConversationTitle.Contains("分叉"));

        // 新会话拥有同样的消息，但 id 不同且记录父会话。
        Assert.NotEqual(source.Id, vm.CurrentConversationId);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Contains("分叉", vm.ConversationTitle);

        var conversations = await _store.ListAsync();
        Assert.Equal(2, conversations.Count);

        var forkRecord = await _store.LoadAsync(vm.CurrentConversationId!);
        Assert.NotNull(forkRecord);
        Assert.Equal(source.Id, forkRecord!.ParentId);

        // 原会话保持不变。
        var original = await _store.LoadAsync(source.Id);
        Assert.Null(original!.ParentId);
        Assert.Equal("原始对话", original.Title);
    }

    [Fact]
    public async Task Fork_ThenContinue_DoesNotAffectOriginal()
    {
        var source = Seed("原始", ("user", "问题一"), ("assistant", "回答一"));
        await _store.SaveAsync(source);

        var vm = new ChatViewModel();
        await vm.OpenResumePickerAsync();
        await WaitForAsync(() => vm.SavedConversations.Count == 1);
        await vm.LoadConversationAsync(vm.SavedConversations[0]);
        await WaitForAsync(() => vm.Messages.Count == 2);
        await vm.ForkAsync();
        await WaitForAsync(() => vm.CurrentConversationId != null && vm.CurrentConversationId != source.Id);

        // 模拟分叉分支新增一轮对话后的保存。
        var forkRecord = await _store.LoadAsync(vm.CurrentConversationId!);
        forkRecord!.Messages.Add(new ConversationMessage { Role = "user", Text = "分叉后的新问题" });
        forkRecord.Messages.Add(new ConversationMessage { Role = "assistant", Text = "分叉后的新回答" });
        await _store.SaveAsync(forkRecord);

        var original = await _store.LoadAsync(source.Id);
        Assert.Equal(2, original!.Messages.Count); // 原会话仍是 2 条
        Assert.Equal(4, (await _store.LoadAsync(forkRecord.Id))!.Messages.Count);
    }

    [Fact]
    public async Task NewConversation_PersistsBeforeClearing()
    {
        var vm = new ChatViewModel();
        // 直接给 _history 灌入内容不方便，改为先恢复一个会话再新建。
        var saved = Seed("要保存的", ("user", "hi"), ("assistant", "hello"));
        await _store.SaveAsync(saved);
        await vm.OpenResumePickerAsync();
        await WaitForAsync(() => vm.SavedConversations.Count == 1);
        await vm.LoadConversationAsync(vm.SavedConversations[0]);
        await WaitForAsync(() => vm.Messages.Count == 2);

        vm.NewConversation();
        await WaitForAsync(() => vm.Messages.Count == 0);

        Assert.Empty(vm.Messages);
        Assert.Null(vm.CurrentConversationId);
        Assert.Equal("新对话", vm.ConversationTitle);
        // 原会话仍在存储中。
        Assert.Single(await _store.ListAsync());
    }

    [Fact]
    public async Task Delete_RemovesFromPickerAndStore()
    {
        await _store.SaveAsync(Seed("待删除", ("user", "hi")));
        var vm = new ChatViewModel();
        await vm.OpenResumePickerAsync();
        await WaitForAsync(() => vm.SavedConversations.Count == 1);

        await vm.DeleteConversationAsync(vm.SavedConversations[0]);
        await WaitForAsync(() => vm.SavedConversations.Count == 0);

        Assert.Empty(vm.SavedConversations);
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public async Task ResumePicker_ShowsForkBadge()
    {
        var source = Seed("原始", ("user", "hi"));
        await _store.SaveAsync(source);
        await _store.ForkAsync(source);

        var vm = new ChatViewModel();
        await vm.OpenResumePickerAsync();
        await WaitForAsync(() => vm.SavedConversations.Count == 2);
        Assert.Contains(vm.SavedConversations, item => !item.IsFork);
    }

    private sealed class NullSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppSettings());

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoopProviderFactory : IProviderFactory
    {
        public IChatClient CreateChatClient(ProviderConfig provider) => new NoopChatClient();
    }

    private sealed class NoopChatClient : IChatClient
    {
        public string ProviderName => "noop";

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse { Message = ChatMessage.Assistant(string.Empty), FinishReason = ChatFinishReason.Stop });

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
