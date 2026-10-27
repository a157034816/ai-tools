using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Extensions;
using AiTools.Extensions.BuiltIn;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>
/// <c>ask</c> 是通用提问工具：标题 + 若干带说明的选项。它不依赖任何具体工作流，
/// 因此这里只验证它自身的行为与三种宿主退化路径。
/// </summary>
public sealed class AskExtensionTests
{
    private static (ExtensionRuntime Runtime, ExtensionPipeline Pipeline) CreatePipeline(IExtensionUi? ui = null)
    {
        var pipeline = new ExtensionPipeline(new ExtensionPipelineOptions
        {
            Extensions = new IExtension[] { new AskExtension() },
            WorkingDirectory = Environment.CurrentDirectory
        })
        {
            Ui = ui
        };

        return (pipeline.Runtime, pipeline);
    }

    private static ToolContext Context(ExtensionRuntime runtime, IExtensionUi? ui = null)
        => new() { Runtime = runtime, State = runtime.State, Ui = ui };

    private const string TwoOptions = """
        {
          "title": "Which scope?",
          "message": "This decides how much of the module moves.",
          "options": [
            { "label": "Only the parser", "description": "Lower risk; callers keep using the old shape." },
            { "label": "Parser and callers", "description": "One atomic change; every call site migrates now." }
          ]
        }
        """;

    [Fact]
    public void Tool_IsDeclaredToTheModelAsAsk()
    {
        var (runtime, _) = CreatePipeline();

        var declared = runtime.DeclaredTools.Select(tool => tool.Definition.Name).ToArray();

        Assert.Contains("ask", declared);
        Assert.True(runtime.TryGetTool("ask")!.ReadOnlyHint);
    }

    [Fact]
    public void ToolDescription_IsWorkflowAgnostic()
    {
        var (runtime, _) = CreatePipeline();
        var description = runtime.TryGetTool("ask")!.Definition.Description ?? string.Empty;

        // 通用工具不能把任何具体工作流写进契约，否则模型会以为它只能配合那个流程使用。
        // 按词边界匹配，避免把 "explanation" 之类的普通词误判成工作流名。
        Assert.DoesNotMatch(@"\bplan\b", description);
        Assert.DoesNotMatch(@"\bgoal\b", description);
    }

    [Fact]
    public async Task ReturnsSelectedOption_WithItsDescription()
    {
        var ui = new ScriptedChoiceUi(selectedIndex: 1);
        var (runtime, _) = CreatePipeline(ui);

        var result = await runtime.InvokeToolAsync(AskExtension.ToolName, TwoOptions, Context(runtime, ui));

        Assert.False(result.IsError, result.Text);
        using var document = JsonDocument.Parse(result.Text);
        var root = document.RootElement;
        Assert.Equal("Which scope?", root.GetProperty("question").GetString());
        Assert.Equal("Parser and callers", root.GetProperty("selected").GetProperty("label").GetString());
        Assert.Equal(
            "One atomic change; every call site migrates now.",
            root.GetProperty("selected").GetProperty("description").GetString());

        // 富选项接口拿到的是结构化数据，而不是被拼接过的字符串。
        var received = Assert.Single(ui.Received);
        Assert.Equal("This decides how much of the module moves.", received.Message);
        Assert.Equal(2, received.Choices.Count);
        Assert.Equal("Only the parser", received.Choices[0].Label);
    }

    [Fact]
    public async Task PlainSelectUi_GetsLabelsWithDescriptions()
    {
        var ui = new PlainSelectUi("Only the parser — Lower risk; callers keep using the old shape.");
        var (runtime, _) = CreatePipeline(ui);

        var result = await runtime.InvokeToolAsync(AskExtension.ToolName, TwoOptions, Context(runtime, ui));

        using var document = JsonDocument.Parse(result.Text);
        Assert.Equal("Only the parser", document.RootElement.GetProperty("selected").GetProperty("label").GetString());

        // 退化路径把说明拼进标签，信息不丢失。
        Assert.NotNull(ui.SeenOptions);
        Assert.All(ui.SeenOptions!, option => Assert.Contains(" — ", option));
    }

    [Fact]
    public async Task NoUi_ReportsUnavailableInsteadOfGuessing()
    {
        var (runtime, _) = CreatePipeline();

        var result = await runtime.InvokeToolAsync(AskExtension.ToolName, TwoOptions, Context(runtime));

        Assert.False(result.IsError, result.Text);
        using var document = JsonDocument.Parse(result.Text);
        Assert.True(document.RootElement.GetProperty("unavailable").GetBoolean());
        Assert.Contains("plain text", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DismissedDialog_ReportsCancellation()
    {
        var ui = new ScriptedChoiceUi(selectedIndex: null);
        var (runtime, _) = CreatePipeline(ui);

        var result = await runtime.InvokeToolAsync(AskExtension.ToolName, TwoOptions, Context(runtime, ui));

        using var document = JsonDocument.Parse(result.Text);
        Assert.True(document.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("selected", out _));
    }

    [Theory]
    [InlineData("""{"title":"t","options":[]}""", "two options")]
    [InlineData("""{"title":"t","options":[{"label":"a","description":"da"}]}""", "two options")]
    [InlineData("""{"title":"","options":[{"label":"a","description":"da"},{"label":"b","description":"db"}]}""", "title")]
    [InlineData("""{"options":[{"label":"a","description":"da"},{"label":"b","description":"db"}]}""", "title")]
    [InlineData("""{"title":"t","options":[{"label":"a","description":"da"},{"label":"b","description":""}]}""", "description")]
    [InlineData("""{"title":"t","options":[{"label":"a","description":"da"},{"description":"db"}]}""", "description")]
    [InlineData("not json", "JSON")]
    public async Task InvalidArguments_ReturnClearErrors(string arguments, string expected)
    {
        var ui = new ScriptedChoiceUi(selectedIndex: 0);
        var (runtime, _) = CreatePipeline(ui);

        var result = await runtime.InvokeToolAsync(AskExtension.ToolName, arguments, Context(runtime, ui));

        Assert.Contains(expected, result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ui.Received);
    }

    [Fact]
    public async Task ParseResult_RoundTripsTheToolPayload()
    {
        var ui = new ScriptedChoiceUi(selectedIndex: 0);
        var (runtime, _) = CreatePipeline(ui);

        var text = (await runtime.InvokeToolAsync(AskExtension.ToolName, TwoOptions, Context(runtime, ui))).Text;
        var parsed = AskExtension.ParseResult(text);

        Assert.NotNull(parsed);
        Assert.True(parsed!.HasAnswer);
        Assert.Equal("Only the parser", parsed.Selected!.Label);
        Assert.Equal("Which scope?", parsed.Question);
    }

    [Fact]
    public void ParseResult_RejectsForeignPayloads()
    {
        Assert.Null(AskExtension.ParseResult(null));
        Assert.Null(AskExtension.ParseResult(""));
        Assert.Null(AskExtension.ParseResult("not json"));
        Assert.Null(AskExtension.ParseResult("""{"unavailable":true}"""));
    }

    private sealed class ScriptedChoiceUi : IExtensionUi, IExtensionChoiceUi
    {
        private readonly int? _selectedIndex;

        public ScriptedChoiceUi(int? selectedIndex) => _selectedIndex = selectedIndex;

        public List<(string? Message, IReadOnlyList<ExtensionChoice> Choices)> Received { get; } = new();

        public bool HasDialogs => true;

        public void Notify(string message, ExtensionNoticeKind kind = ExtensionNoticeKind.Info) { }

        public void SetStatus(string? text) { }

        public Task<bool> ConfirmAsync(string title, string message, bool defaultValue = false, CancellationToken cancellationToken = default)
            => Task.FromResult(defaultValue);

        public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
            => Task.FromResult(_selectedIndex is { } index && index < options.Count ? options[index] : null);

        public Task<ExtensionChoiceResult?> ChooseAsync(
            string title,
            string? message,
            IReadOnlyList<ExtensionChoice> choices,
            CancellationToken cancellationToken = default)
        {
            Received.Add((message, choices));
            if (_selectedIndex is not { } index || index >= choices.Count)
            {
                return Task.FromResult<ExtensionChoiceResult?>(null);
            }

            return Task.FromResult<ExtensionChoiceResult?>(new ExtensionChoiceResult
            {
                Index = index,
                Label = choices[index].Label,
                Description = choices[index].Description
            });
        }
    }

    private sealed class PlainSelectUi : IExtensionUi
    {
        private readonly string? _answer;

        public PlainSelectUi(string? answer) => _answer = answer;

        public IReadOnlyList<string>? SeenOptions { get; private set; }

        public bool HasDialogs => true;

        public void Notify(string message, ExtensionNoticeKind kind = ExtensionNoticeKind.Info) { }

        public void SetStatus(string? text) { }

        public Task<bool> ConfirmAsync(string title, string message, bool defaultValue = false, CancellationToken cancellationToken = default)
            => Task.FromResult(defaultValue);

        public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
        {
            SeenOptions = options;
            return Task.FromResult(_answer);
        }
    }
}
