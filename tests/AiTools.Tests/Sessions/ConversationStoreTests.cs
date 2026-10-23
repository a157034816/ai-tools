using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Core.Sessions;
using Xunit;

namespace AiTools.Tests.Sessions;

public sealed class JsonConversationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aitools-conv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ConversationRecord Sample(string title = "第一次对话") => new()
    {
        Title = title,
        ProviderId = "openai",
        ModelId = "gpt-4.1",
        Messages = new List<ConversationMessage>
        {
            new() { Role = "user", Text = "你好", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-3) },
            new() { Role = "assistant", Text = "你好！有什么可以帮你？", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2) }
        }
    };

    [Fact]
    public async Task SaveThenLoad_RoundTripsMessages()
    {
        var store = new JsonConversationStore(_root);
        var record = Sample();

        await store.SaveAsync(record);
        var loaded = await store.LoadAsync(record.Id);

        Assert.NotNull(loaded);
        Assert.Equal(record.Id, loaded!.Id);
        Assert.Equal("第一次对话", loaded.Title);
        Assert.Equal("openai", loaded.ProviderId);
        Assert.Equal("gpt-4.1", loaded.ModelId);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal("你好", loaded.Messages[0].Text);
        Assert.Equal("assistant", loaded.Messages[1].Role);
    }

    [Fact]
    public async Task ListAsync_ReturnsMostRecentlyUpdatedFirst()
    {
        var store = new JsonConversationStore(_root);
        var older = Sample("旧对话");
        older.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var newer = Sample("新对话");
        newer.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await store.SaveAsync(older);
        await store.SaveAsync(newer);

        var list = await store.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Contains("新对话", list[0].Title);
        Assert.Contains("旧对话", list[1].Title);
    }

    [Fact]
    public async Task ListAsync_MissingDirectory_ReturnsEmpty()
    {
        var store = new JsonConversationStore(Path.Combine(_root, "does-not-exist"));

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task LoadAsync_Missing_ReturnsNull()
    {
        var store = new JsonConversationStore(_root);

        Assert.Null(await store.LoadAsync("missing"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesConversation()
    {
        var store = new JsonConversationStore(_root);
        var record = Sample();
        await store.SaveAsync(record);

        await store.DeleteAsync(record.Id);

        Assert.Null(await store.LoadAsync(record.Id));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task SaveAsync_UsesAtomicWrite_NoTempFileLeft()
    {
        var store = new JsonConversationStore(_root);
        var record = Sample();
        await store.SaveAsync(record);
        await store.SaveAsync(record);

        var files = Directory.GetFiles(store.DirectoryPath);
        Assert.Single(files);
        Assert.DoesNotContain(".tmp", files[0]);
    }

    [Fact]
    public async Task SaveAsync_DerivesTitleFromFirstUserMessage()
    {
        var store = new JsonConversationStore(_root);
        var record = new ConversationRecord
        {
            Messages = new List<ConversationMessage>
            {
                new() { Role = "user", Text = "这是一个很长的用户问题，用来测试标题自动生成逻辑是否工作正常，并且确保它会被截断。" }
            }
        };

        await store.SaveAsync(record);

        var loaded = await store.LoadAsync(record.Id);
        Assert.StartsWith("这是一个很长的用户问题", loaded!.Title);
        Assert.EndsWith("…", loaded.Title);
        Assert.Equal(41, loaded.Title.Length);
    }

    [Fact]
    public async Task ListAsync_CorruptFile_IsSkipped()
    {
        var store = new JsonConversationStore(_root);
        await store.SaveAsync(Sample("good"));
        await File.WriteAllTextAsync(Path.Combine(store.DirectoryPath, "broken.json"), "{ not json");

        var list = await store.ListAsync();

        Assert.Single(list);
        Assert.Equal("good", list[0].Title);
    }

    [Fact]
    public async Task LoadAsync_RejectsPathTraversalIds()
    {
        var store = new JsonConversationStore(_root);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.LoadAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task ForkAsync_CreatesIndependentCopyWithParentLink()
    {
        var store = new JsonConversationStore(_root);
        var source = Sample("原始对话");
        await store.SaveAsync(source);

        var fork = await store.ForkAsync(source);

        Assert.NotEqual(source.Id, fork.Id);
        Assert.Equal(source.Id, fork.ParentId);
        Assert.Equal("原始对话（分叉）", fork.Title);
        Assert.Equal(source.Messages.Count, fork.Messages.Count);
        Assert.Equal(source.ProviderId, fork.ProviderId);

        // 修改分叉不应影响原会话。
        fork.Messages.Add(new ConversationMessage { Role = "user", Text = "新增消息" });
        await store.SaveAsync(fork);

        var reloadedSource = await store.LoadAsync(source.Id);
        Assert.Equal(2, reloadedSource!.Messages.Count);
        Assert.Equal(3, (await store.LoadAsync(fork.Id))!.Messages.Count);
    }

    [Fact]
    public async Task ForkAsync_CustomTitle_IsUsed()
    {
        var store = new JsonConversationStore(_root);
        var source = Sample();
        await store.SaveAsync(source);

        var fork = await store.ForkAsync(source, "尝试另一种方案");

        Assert.Equal("尝试另一种方案", fork.Title);
    }

    [Fact]
    public async Task ForkAsync_CanBeForkedAgain()
    {
        var store = new JsonConversationStore(_root);
        var source = Sample();
        await store.SaveAsync(source);

        var first = await store.ForkAsync(source);
        var second = await store.ForkAsync(first);

        Assert.Equal(first.Id, second.ParentId);
        Assert.Equal(3, (await store.ListAsync()).Count);
    }

    [Fact]
    public void ConversationMessage_RoundTripsRoles()
    {
        Assert.Equal("user", ConversationMessage.FromChatMessage(ChatMessage.User("hi")).Role);
        Assert.Equal("assistant", ConversationMessage.FromChatMessage(ChatMessage.Assistant("hi")).Role);
        Assert.Equal("system", ConversationMessage.FromChatMessage(ChatMessage.System("hi")).Role);

        var tool = ConversationMessage.FromChatMessage(ChatMessage.ToolResult("call_1", "result"));
        Assert.Equal("tool", tool.Role);
        Assert.Equal("call_1", tool.ToolCallId);
        Assert.Equal("result", tool.ToChatMessage().GetText());
    }

    [Fact]
    public async Task InMemoryStore_SupportsSameOperations()
    {
        IConversationStore store = new InMemoryConversationStore();
        var record = Sample();
        await store.SaveAsync(record);

        Assert.Single(await store.ListAsync());
        var fork = await store.ForkAsync(record);
        Assert.Equal(record.Id, fork.ParentId);
        await store.DeleteAsync(record.Id);
        Assert.Single(await store.ListAsync());
    }
}
