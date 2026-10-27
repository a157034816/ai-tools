using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Extensions;
using AiTools.Wpf.Asking;
using AiTools.Wpf.Extensions;
using Xunit;

namespace AiTools.Tests.Wpf;

/// <summary>
/// 验证提问呈现接缝：默认内联面板（<see cref="InlineAskHost"/>）的行为，
/// 以及 <see cref="WpfExtensionUi"/> 把 <c>ChooseAsync</c> 交给宿主策略。
/// </summary>
public sealed class AskPresenterTests
{
    private static AskPrompt Prompt() => new(
        "Which scope?",
        "This decides how much moves.",
        new[]
        {
            new AskPromptOption("Only the parser", "Lower risk.", 0),
            new AskPromptOption("Parser and callers", "One atomic change.", 1)
        });

    [Fact]
    public void AskPrompt_RejectsMissingTitle()
    {
        Assert.Throws<ArgumentException>(() => new AskPrompt(" ", null, System.Array.Empty<AskPromptOption>()));
    }

    [Fact]
    public async Task InlineHost_ExposesPromptUntilAnswered()
    {
        var host = new InlineAskHost();
        var changes = 0;
        host.Changed += (_, _) => changes++;

        Assert.False(host.HasPrompt);
        Assert.Null(host.Current);

        var pending = host.ShowAsync(Prompt());

        Assert.True(host.HasPrompt);
        Assert.Equal("Which scope?", host.Current!.Title);
        Assert.Equal(2, host.Current.Options.Count);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, changes);

        host.Select(1);

        var response = await pending;
        Assert.Equal(1, response.SelectedIndex);
        Assert.False(response.IsCancelled);
        Assert.False(host.HasPrompt);
        Assert.Null(host.Current);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task InlineHost_CancelReportsCancellation()
    {
        var host = new InlineAskHost();
        var pending = host.ShowAsync(Prompt());

        host.Cancel();

        var response = await pending;
        Assert.True(response.IsCancelled);
        Assert.False(host.HasPrompt);
    }

    [Fact]
    public async Task InlineHost_NewPromptCancelsThePreviousOne()
    {
        var host = new InlineAskHost();

        var first = host.ShowAsync(Prompt());
        var second = host.ShowAsync(new AskPrompt("Second?", null, Prompt().Options));

        // 旧提问按取消结束，避免调用方永久等待；当前提问是新的那个。
        Assert.True((await first).IsCancelled);
        Assert.Equal("Second?", host.Current!.Title);

        host.Select(0);
        Assert.Equal(0, (await second).SelectedIndex);
    }

    [Fact]
    public async Task InlineHost_StaleCancelDoesNotAffectANewerPrompt()
    {
        var host = new InlineAskHost();

        var first = host.ShowAsync(Prompt());
        var second = host.ShowAsync(new AskPrompt("Second?", null, Prompt().Options));
        await first;

        // 迟到的 Select 只对当前提问生效，不会把新提问也终结掉。
        host.Select(1);

        Assert.Equal(1, (await second).SelectedIndex);
    }

    [Fact]
    public async Task InlineHost_Cancellation_TokenCompletesTheWait()
    {
        var host = new InlineAskHost();
        using var cts = new CancellationTokenSource();

        var pending = host.ShowAsync(Prompt(), cts.Token);
        cts.Cancel();

        Assert.True((await pending).IsCancelled);
        Assert.False(host.HasPrompt);
    }

    [Fact]
    public async Task InlinePresenter_DelegatesToTheHost()
    {
        var host = new InlineAskHost();
        IAskPresenter presenter = new InlineAskPresenter(host);

        var pending = presenter.AskAsync(Prompt());
        Assert.True(host.HasPrompt);

        host.Select(1);
        Assert.Equal(1, (await pending).SelectedIndex);
    }

    [Fact]
    public async Task WpfExtensionUi_UsesTheConfiguredPresenterInsteadOfADialog()
    {
        var host = new InlineAskHost();
        var ui = new WpfExtensionUi(askPresenter: new InlineAskPresenter(host));
        var choices = new List<ExtensionChoice>
        {
            new() { Label = "A", Description = "first" },
            new() { Label = "B", Description = "second" }
        };

        var pending = ui.ChooseAsync("Pick", "context", choices);
        Assert.True(host.HasPrompt);

        // 选项按索引顺序传给呈现层，宿主不必再猜。
        Assert.Equal(1, host.Current!.Options[1].Index);
        Assert.Equal("second", host.Current.Options[1].Description);

        host.Select(1);

        var result = await pending;
        Assert.NotNull(result);
        Assert.Equal(1, result!.Index);
        Assert.Equal("B", result.Label);
        Assert.Equal("second", result.Description);
    }

    [Fact]
    public async Task WpfExtensionUi_NoApplicationWithoutPresenter_ReturnsNull()
    {
        // 没有 WPF Application 也没有呈现策略时，富选项退化为不可用（而不是抛异常）。
        var ui = new WpfExtensionUi();
        var result = await ui.ChooseAsync("t", "m", new[]
        {
            new ExtensionChoice { Label = "A", Description = "a" },
            new ExtensionChoice { Label = "B", Description = "b" }
        });

        Assert.Null(result);
    }

    [Fact]
    public async Task WpfExtensionUi_PresenterCancelMapsToNull()
    {
        var host = new InlineAskHost();
        var ui = new WpfExtensionUi(askPresenter: new InlineAskPresenter(host));

        var pending = ui.ChooseAsync("t", null, new[]
        {
            new ExtensionChoice { Label = "A", Description = "a" },
            new ExtensionChoice { Label = "B", Description = "b" }
        });

        host.Cancel();

        Assert.Null(await pending);
    }

    [Fact]
    public async Task WpfExtensionUi_PresenterWithOutOfRangeIndexMapsToNull()
    {
        var ui = new WpfExtensionUi(askPresenter: new FixedPresenter(5));

        var result = await ui.ChooseAsync("t", null, new[]
        {
            new ExtensionChoice { Label = "A", Description = "a" },
            new ExtensionChoice { Label = "B", Description = "b" }
        });

        Assert.Null(result);
    }

    private sealed class FixedPresenter : IAskPresenter
    {
        private readonly int _index;

        public FixedPresenter(int index) => _index = index;

        public Task<AskResponse> AskAsync(AskPrompt prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(AskResponse.Select(_index));
    }
}
