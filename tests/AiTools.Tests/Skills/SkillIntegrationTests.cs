using System.Runtime.CompilerServices;
using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Skills;
using Xunit;

namespace AiTools.Tests.Skills;

/// <summary>验证 skill 工具循环能够通过与 provider 无关的聊天抽象正常工作。</summary>
public sealed class SkillIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aitools-skillint-" + Guid.NewGuid().ToString("N"));

    public SkillIntegrationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "refund-policy"));
        File.WriteAllText(Path.Combine(_root, "refund-policy", "SKILL.md"), """
            ---
            name: refund-policy
            description: Answer customer refund questions using the official policy.
            ---

            Refunds are allowed within 30 days of purchase with a receipt.
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteWithSkillsAsync_DrivesLoadSkillThenAnswers()
    {
        var toolset = new SkillToolset(SkillCatalog.LoadFromDirectory(_root));
        var client = new ScriptedClient(new[]
        {
            // 第一轮：模型调用 load_skill。
            new ChatResponse
            {
                Message = ChatMessage.AssistantWithToolCalls(new[]
                {
                    new ToolCall("c1", SkillToolset.LoadSkillToolName, """{"name":"refund-policy"}""")
                }),
                FinishReason = ChatFinishReason.ToolCalls
            },
            // 第二轮：拿到技能内容后作答。
            new ChatResponse
            {
                Message = ChatMessage.Assistant("30 天内凭收据可以退款。"),
                FinishReason = ChatFinishReason.Stop
            }
        });

        var request = ChatRequest.FromUser("gpt-test", "How long do I have to return an item?");
        var response = await client.CompleteWithSkillsAsync(request, toolset);

        Assert.Equal("30 天内凭收据可以退款。", response.GetText());

        // 第一轮请求应包含 skill 工具与技能清单。
        var first = client.Requests[0];
        Assert.Contains("refund-policy", first.SystemPrompt);
        Assert.Equal(2, first.Tools.Count);

        // 第二轮请求应包含工具返回的技能正文。
        var second = client.Requests[1];
        var toolMessage = second.Messages.Last();
        Assert.Equal(ChatRole.Tool, toolMessage.Role);
        Assert.Contains("Refunds are allowed within 30 days", toolMessage.GetText());
    }

    [Fact]
    public void ApplyTo_IsProviderAgnostic_RequestShapeOnly()
    {
        var toolset = new SkillToolset(SkillCatalog.LoadFromDirectory(_root));
        var prepared = toolset.ApplyTo(ChatRequest.FromUser("any-model", "hi"));

        // 适用于任何 Provider：只有统一的 ChatRequest 结构，没有厂商特有字段。
        Assert.NotNull(prepared.SystemPrompt);
        Assert.All(prepared.Tools, tool => Assert.Contains(tool.Name, new[] { "load_skill", "read_skill_file" }));
        Assert.Null(prepared.AdditionalParameters);
    }

    private sealed class ScriptedClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses;

        public ScriptedClient(IEnumerable<ChatResponse> responses) => _responses = new Queue<ChatResponse>(responses);

        public string ProviderName => "scripted";

        public List<ChatRequest> Requests { get; } = new();

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
