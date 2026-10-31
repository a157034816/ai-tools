using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AiTools.Terminal;
using AiTools.Terminal.Agent;
using AiTools.Terminal.Input;
using AiTools.Terminal.Rendering;
using Xunit;

namespace AiTools.Tests.Terminal;

/// <summary>内存终端宿主：把按键排队，把每一帧记录为文本。</summary>
internal sealed class FakeTerminalHost : ITerminalHost
{
    private readonly Channel<TerminalKey> _keys = Channel.CreateUnbounded<TerminalKey>();
    private readonly List<string> _frames = new();

    public FakeTerminalHost(int width = 72, int height = 18)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool SupportsAnsi => true;

    public List<ScreenBuffer> Screens { get; } = new();

    public void Present(ScreenBuffer screen, bool force = false)
    {
        lock (_frames)
        {
            var builder = new StringBuilder();
            for (var row = 0; row < screen.Height; row++)
            {
                builder.AppendLine(RenderRow(screen, row));
            }

            _frames.Add(builder.ToString());
            Screens.Add(screen);
        }
    }

    /// <summary>获取最近一帧的文本。</summary>
    public string LatestFrame
    {
        get
        {
            lock (_frames)
            {
                return _frames.Count == 0 ? string.Empty : _frames[_frames.Count - 1];
            }
        }
    }

    /// <summary>获取是否出现过指定文本。</summary>
    /// <param name="text">文本。</param>
    public bool AnyFrameContains(string text)
    {
        lock (_frames)
        {
            return _frames.Any(frame => frame.Contains(text, StringComparison.Ordinal));
        }
    }

    public int FrameCount
    {
        get
        {
            lock (_frames)
            {
                return _frames.Count;
            }
        }
    }

    public ValueTask<TerminalKey?> ReadKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_keys.Reader.TryRead(out var key))
        {
            return new ValueTask<TerminalKey?>(key);
        }

        return SlowReadAsync(cancellationToken);
    }

    private async ValueTask<TerminalKey?> SlowReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(5);
        try
        {
            var ready = await _keys.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
            if (ready && _keys.Reader.TryRead(out var key))
            {
                return key;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }

    public (int Width, int Height)? TryConsumeResize() => null;

    public void RequestRedraw()
    {
    }

    public void Dispose()
    {
    }

    /// <summary>排队一个按键。</summary>
    public void Send(TerminalKey key) => _keys.Writer.TryWrite(key);

    /// <summary>排队一段文本。</summary>
    public void Type(string text)
    {
        foreach (var character in text)
        {
            Send(new TerminalKey(TerminalKeyKind.Character, character.ToString()));
        }
    }

    private static string RenderRow(ScreenBuffer screen, int row)
    {
        var cells = screen.GetRow(row);
        var builder = new StringBuilder();
        foreach (var cell in cells)
        {
            if (!cell.Continuation)
            {
                builder.Append(cell.Character);
            }
        }

        return builder.ToString().TrimEnd();
    }
}

public class TerminalAppTests
{
    [Fact]
    public async Task RunAsync_renderes_streamed_reply_and_exits_on_ctrl_c()
    {
        var client = new ScriptedChatClient().Reply("这是终端里的回答。");
        var settings = new AiTools.Configuration.AppSettings
        {
            Version = 1,
            Providers = new List<AiTools.Configuration.ProviderConfig>
            {
                new()
                {
                    Id = "test",
                    DisplayName = "Test",
                    Protocol = AiTools.Configuration.ProviderProtocols.OpenAi,
                    BaseUrl = "https://example.test/v1",
                    ApiKey = "key",
                    Models = new List<AiTools.Configuration.ModelConfig> { new() { Id = "scripted-model" } }
                }
            },
            SelectedProviderId = "test",
            SelectedModelId = "scripted-model"
        };

        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aitools-app-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        try
        {
            await using var session = new AgentSession(new TerminalOptions
            {
                SettingsStore = new StaticSettingsStore(settings),
                ProviderFactory = new ScriptedProviderFactory(client),
                ConversationStore = new AiTools.Core.Sessions.JsonConversationStore(root),
                WorkingDirectory = root,
                EnableSharedAgents = false,
                EnableExtensions = false,
                EnableExtensionPackages = false,
                AutoDiscoverPlugins = false,
                ApplicationName = "AiTools"
            });

            await session.InitializeAsync();

            using var host = new FakeTerminalHost();
            await using var app = new TerminalApp(session, host);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = app.RunAsync(cts.Token);

            host.Type("hello");
            host.Send(new TerminalKey(TerminalKeyKind.Enter));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !host.AnyFrameContains("这是终端里的回答。"))
            {
                await Task.Delay(25);
            }

            Assert.True(host.AnyFrameContains("这是终端里的回答。"), host.LatestFrame);

            host.Send(new TerminalKey(TerminalKeyKind.Character, "c", KeyModifiers.Ctrl));
            await run;

            Assert.True(app.ExitRequested);
            Assert.True(host.FrameCount > 0);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public async Task RunAsync_shows_help_overlay_on_question_mark()
    {
        var client = new ScriptedChatClient();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aitools-app-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);

        try
        {
            await using var session = new AgentSession(new TerminalOptions
            {
                SettingsStore = new StaticSettingsStore(new AiTools.Configuration.AppSettings()),
                ProviderFactory = new ScriptedProviderFactory(client),
                ConversationStore = new AiTools.Core.Sessions.JsonConversationStore(root),
                WorkingDirectory = root,
                EnableSharedAgents = false,
                EnableExtensions = false,
                EnableExtensionPackages = false,
                AutoDiscoverPlugins = false
            });

            await session.InitializeAsync();

            using var host = new FakeTerminalHost(96, 24);
            await using var app = new TerminalApp(session, host);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = app.RunAsync(cts.Token);

            host.Type("?");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !host.AnyFrameContains("快捷键"))
            {
                await Task.Delay(25);
            }

            Assert.True(host.AnyFrameContains("快捷键"), host.LatestFrame);

            host.Send(new TerminalKey(TerminalKeyKind.Escape));
            await Task.Delay(80);
            host.Send(new TerminalKey(TerminalKeyKind.Character, "c", KeyModifiers.Ctrl));
            await run;
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }
}
