using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AiTools.Terminal;
using AiTools.Terminal.Agent;
using AiTools.Terminal.Input;
using AiTools.Terminal.Rendering;
using AiTools.Terminal.Views;
using Xunit;

namespace AiTools.Tests.Terminal;

public class TextWidthTests
{
    [Theory]
    [InlineData("abc", 3)]
    [InlineData("你好", 4)]
    [InlineData("a你b好", 6)]
    [InlineData("", 0)]
    [InlineData("🙂", 2)]
    public void Of_CountsDisplayColumns(string text, int expected)
    {
        Assert.Equal(expected, TextWidth.Of(text));
    }

    [Fact]
    public void CountFit_DoesNotSplitWideCharacter()
    {
        var count = TextWidth.CountFit("你好", 0, 3, out var width, out var truncated);

        Assert.Equal(1, count);
        Assert.Equal(2, width);
        Assert.True(truncated);
    }

    [Fact]
    public void Truncate_AppendsEllipsisWithinBudget()
    {
        var result = TextWidth.Truncate("你好世界", 5);

        Assert.True(TextWidth.Of(result) <= 5);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_ReplacesControlCharacters()
    {
        var result = TextWidth.Sanitize("a\ab\tc");

        Assert.DoesNotContain('\a', result);
        Assert.Contains("    ", result);
    }
}

public class ScreenBufferTests
{
    [Fact]
    public void Write_PlacesWideCharacterWithContinuationCell()
    {
        var screen = new ScreenBuffer();
        screen.Resize(10, 1);

        screen.Write(0, 0, new[] { new StyledText("你a", TextStyle.Default) });

        var row = screen.GetRow(0);
        Assert.Equal('你', row[0].Character);
        Assert.True(row[1].Continuation);
        Assert.Equal('a', row[2].Character);
    }

    [Fact]
    public void Write_StopsBeforeWideCharacterThatDoesNotFit()
    {
        var screen = new ScreenBuffer();
        screen.Resize(3, 1);

        screen.Write(2, 0, new[] { new StyledText("你", TextStyle.Default) });

        Assert.Equal(' ', screen.GetRow(0)[2].Character);
    }

    [Fact]
    public void Compose_FirstFrameRepaintsWholeScreen()
    {
        var screen = new ScreenBuffer();
        screen.Resize(4, 2);
        screen.Write(0, 0, new[] { new StyledText("ab", TextStyle.Default) });
        var composer = new FrameComposer();

        var frame = composer.Compose(screen);

        Assert.Contains("\u001b[2J", frame);
        Assert.Contains("ab", frame);
    }

    [Fact]
    public void Compose_OnlyRepaintsChangedRows()
    {
        var screen = new ScreenBuffer();
        screen.Resize(6, 2);
        screen.Write(0, 0, new[] { new StyledText("line1", TextStyle.Default) });
        screen.Write(0, 1, new[] { new StyledText("line2", TextStyle.Default) });
        var composer = new FrameComposer();
        composer.Compose(screen);

        screen.Write(0, 1, new[] { new StyledText("LINE2", TextStyle.Default) });
        var frame = composer.Compose(screen);

        Assert.Contains("LINE2", frame);
        Assert.DoesNotContain("line1", frame);
    }

    [Fact]
    public void Theme_SgrUsesAnsiSequences()
    {
        var style = new TextStyle(TerminalColor.Rgb(1, 2, 3), TerminalColor.Indexed(4), bold: true);

        var sgr = style.ToSgr();

        Assert.StartsWith("\u001b[", sgr);
        Assert.Contains("1", sgr);
        Assert.Contains("38;2;1;2;3", sgr);
        Assert.Contains("44", sgr);
    }
}

public class KeyParserTests
{
    [Theory]
    [InlineData("a", TerminalKeyKind.Character)]
    [InlineData("\r", TerminalKeyKind.Enter)]
    [InlineData("\u007f", TerminalKeyKind.Backspace)]
    [InlineData("\t", TerminalKeyKind.Tab)]
    public void Parse_SingleCharacters(string input, TerminalKeyKind expected)
    {
        var parser = new KeyParser();
        parser.Feed(input);

        Assert.True(parser.TryParse(out var key));
        Assert.Equal(expected, key.Kind);
    }

    [Theory]
    [InlineData("\u001b[A", TerminalKeyKind.ArrowUp)]
    [InlineData("\u001b[B", TerminalKeyKind.ArrowDown)]
    [InlineData("\u001b[C", TerminalKeyKind.ArrowRight)]
    [InlineData("\u001b[D", TerminalKeyKind.ArrowLeft)]
    [InlineData("\u001b[H", TerminalKeyKind.Home)]
    [InlineData("\u001b[F", TerminalKeyKind.End)]
    [InlineData("\u001b[3~", TerminalKeyKind.Delete)]
    [InlineData("\u001b[5~", TerminalKeyKind.PageUp)]
    [InlineData("\u001b[6~", TerminalKeyKind.PageDown)]
    public void Parse_CsiSequences(string input, TerminalKeyKind expected)
    {
        var parser = new KeyParser();
        parser.Feed(input);

        Assert.True(parser.TryParse(out var key));
        Assert.Equal(expected, key.Kind);
    }

    [Fact]
    public void Parse_CtrlLetter()
    {
        var parser = new KeyParser();
        parser.Feed("\u0003");

        Assert.True(parser.TryParse(out var key));
        Assert.True(key.IsCtrl);
        Assert.Equal("c", key.Text);
    }

    [Fact]
    public void Parse_BracketedPaste_ReturnsWholePayload()
    {
        var parser = new KeyParser();
        parser.Feed("\u001b[200~hello\nworld\u001b[201~");

        Assert.True(parser.TryParse(out var key));
        Assert.Equal(TerminalKeyKind.Paste, key.Kind);
        Assert.Equal("hello\nworld", key.Text);
    }

    [Fact]
    public void Parse_LoneEscape_IsBufferedUntilFlush()
    {
        var parser = new KeyParser();
        parser.Feed("\u001b");

        Assert.True(parser.HasLoneEscape);
        Assert.False(parser.TryParse(out _));

        var escape = parser.FlushEscape();
        Assert.Equal(TerminalKeyKind.Escape, escape.Kind);
        Assert.False(parser.HasLoneEscape);
    }

    [Fact]
    public void Parse_AltEnter_IsEnterWithAlt()
    {
        var parser = new KeyParser();
        parser.Feed("\u001b\r");

        Assert.True(parser.TryParse(out var key));
        Assert.Equal(TerminalKeyKind.Enter, key.Kind);
        Assert.True(key.IsAlt);
    }

    [Fact]
    public void Parse_SurrogatePair_IsSingleCharacter()
    {
        var parser = new KeyParser();
        parser.Feed("🙂");

        Assert.True(parser.TryParse(out var key));
        Assert.Equal("🙂", key.Text);
        Assert.False(parser.TryParse(out _));
    }
}

public class LineEditorTests
{
    [Fact]
    public void InsertAndBackspace_WorkOnSurrogatePairs()
    {
        var editor = new LineEditor();
        editor.Insert("a🙂");

        editor.Backspace();

        Assert.Equal("a", editor.Text);
    }

    [Fact]
    public void History_RemembersCommittedInput()
    {
        var editor = new LineEditor();
        editor.Text = "first";
        editor.CommitHistory();
        editor.Clear();
        editor.Text = "second";
        editor.CommitHistory();
        editor.Clear();

        Assert.True(editor.HistoryPrevious());
        Assert.Equal("second", editor.Text);
        Assert.True(editor.HistoryPrevious());
        Assert.Equal("first", editor.Text);
        Assert.True(editor.HistoryNext());
        Assert.Equal("second", editor.Text);
        Assert.True(editor.HistoryNext());
        Assert.Equal(string.Empty, editor.Text);
    }

    [Fact]
    public void DeleteWordBackward_RemovesWholeWord()
    {
        var editor = new LineEditor();
        editor.Text = "hello world";
        editor.MoveEnd();

        editor.DeleteWordBackward();

        Assert.Equal("hello ", editor.Text);
    }

    [Fact]
    public void Render_ShowsPlaceholderWhenEmpty()
    {
        var editor = new LineEditor { Placeholder = "type here" };

        editor.Render(40, new TerminalTheme(false, true), "> ", out var segments, out var cursor);

        Assert.Contains(segments, segment => segment.Text.Contains("type here", StringComparison.Ordinal));
        Assert.Equal(2, cursor);
    }

    [Fact]
    public void Render_ScrollsToKeepCursorVisible()
    {
        var editor = new LineEditor();
        editor.Text = new string('x', 100);
        editor.MoveEnd();

        editor.Render(20, new TerminalTheme(false, true), "> ", out var segments, out var cursor);

        Assert.InRange(cursor, 0, 19);
        Assert.DoesNotContain(new string('x', 100), string.Concat(segments.Select(item => item.Text)));
    }
}

public class MarkdownRendererTests
{
    private static MarkdownRenderer Renderer() => new(new TerminalTheme(false, true));

    [Fact]
    public void Render_KeepsHeadingText()
    {
        var lines = Renderer().Render("# 标题\n\n正文", 40);

        Assert.Contains(lines, line => line.Text.Contains("标题", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Text.Contains("正文", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_WrapsLongParagraphByDisplayWidth()
    {
        var text = string.Concat(Enumerable.Repeat("中文内容", 20));

        var lines = Renderer().Render(text, 20);

        Assert.True(lines.Count > 1);
        Assert.All(lines, line => Assert.True(TextWidth.Of(line.Text) <= 20));
    }

    [Fact]
    public void Render_CodeFence_UsesCodeStyle()
    {
        var theme = new TerminalTheme(false, true);
        var renderer = new MarkdownRenderer(theme);

        var lines = renderer.Render("```csharp\nvar x = 1;\n```", 40);

        Assert.Contains(lines, line => line.Segments.Any(segment => segment.Style.Equals(theme.CodeStyle)));
    }

    [Fact]
    public void Render_ListItems_UseBullets()
    {
        var lines = Renderer().Render("- 第一项\n- 第二项", 40);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.StartsWith("•", line.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Render_InlineCodeAndBold_AreStyled()
    {
        var theme = new TerminalTheme(false, true);
        var renderer = new MarkdownRenderer(theme);

        var lines = renderer.Render("这是 `code` 与 **bold**", 60);

        Assert.Contains(lines[0].Segments, segment => segment.Style.Equals(theme.InlineCodeStyle));
        Assert.Contains(lines[0].Segments, segment => segment.Style.Bold);
    }

    [Fact]
    public void Render_TableLikePipes_RemainReadable()
    {
        var lines = Renderer().Render("| a | b |\n|---|---|\n| 1 | 2 |", 40);

        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, line => line.Text.Contains("| a | b |", StringComparison.Ordinal));
    }
}

public class TranscriptRendererTests
{
    [Fact]
    public void Render_ToolEntry_ShowsNameAndDuration()
    {
        var theme = new TerminalTheme(false, true);
        var renderer = new TranscriptRenderer(theme);
        var entry = new TranscriptEntry(TranscriptEntryKind.Tool, "ok")
        {
            ToolName = "read_file",
            Duration = TimeSpan.FromSeconds(1.25)
        };

        var lines = renderer.Render(new[] { entry }, 60);

        Assert.Contains(lines, line => line.Segments.Any(segment => segment.Text.Contains("read_file", StringComparison.Ordinal)));
        Assert.Contains(lines, line => line.Segments.Any(segment => segment.Text.Contains("1.3s", StringComparison.Ordinal)));
    }

    [Fact]
    public void Render_UserEntry_PrefixesPromptSymbol()
    {
        var renderer = new TranscriptRenderer(new TerminalTheme(false, true));
        var entry = new TranscriptEntry(TranscriptEntryKind.User, "hello");

        var lines = renderer.Render(new[] { entry }, 40);

        Assert.StartsWith("❯ hello", lines[0].Segments[0].Text + string.Concat(lines[0].Segments.Skip(1).Select(item => item.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_CachesUntouchedEntries()
    {
        var renderer = new TranscriptRenderer(new TerminalTheme(false, true));
        var entry = new TranscriptEntry(TranscriptEntryKind.Assistant, "answer");

        var first = renderer.Render(new[] { entry }, 40);
        var second = renderer.Render(new[] { entry }, 40);

        Assert.Equal(first.Count, second.Count);
        Assert.Same(first[0], second[0]);
    }

    [Fact]
    public void Render_RepaintsTouchedEntry()
    {
        var renderer = new TranscriptRenderer(new TerminalTheme(false, true));
        var entry = new TranscriptEntry(TranscriptEntryKind.Assistant, "a");

        var first = string.Concat(renderer.Render(new[] { entry }, 40).SelectMany(line => line.Segments).Select(segment => segment.Text));
        entry.Text = "abc";
        entry.Touch();
        var second = string.Concat(renderer.Render(new[] { entry }, 40).SelectMany(line => line.Segments).Select(segment => segment.Text));

        Assert.NotEqual(first, second);
        Assert.Contains("abc", second, StringComparison.Ordinal);
    }
}

public class OverlayTests
{
    [Fact]
    public async Task ListOverlay_EnterConfirmsSelection()
    {
        var overlay = new ListOverlay("pick", null, new List<(string, string?)>
        {
            ("one", null),
            ("two", null)
        });

        Assert.False(overlay.HandleKey(new TerminalKey(TerminalKeyKind.ArrowDown)));
        Assert.True(overlay.HandleKey(new TerminalKey(TerminalKeyKind.Enter)));
        Assert.Equal(1, await overlay.Result);
    }

    [Fact]
    public async Task ListOverlay_EscapeReturnsNull()
    {
        var overlay = new ListOverlay("pick", null, new List<(string, string?)> { ("one", null) });

        Assert.True(overlay.HandleKey(new TerminalKey(TerminalKeyKind.Escape)));
        Assert.Null(await overlay.Result);
    }

    [Fact]
    public async Task ListOverlay_TypeFiltersItems()
    {
        var overlay = new ListOverlay("pick", null, new List<(string, string?)>
        {
            ("alpha", null),
            ("beta", null),
            ("alphabet", null)
        });

        overlay.HandleKey(new TerminalKey(TerminalKeyKind.Character, "b"));
        overlay.HandleKey(new TerminalKey(TerminalKeyKind.Enter));

        Assert.Equal(1, await overlay.Result);
    }

    [Fact]
    public async Task ConfirmOverlay_YShortcutApproves()
    {
        var overlay = new ConfirmOverlay("run?", "danger", defaultYes: false);

        Assert.True(overlay.HandleKey(new TerminalKey(TerminalKeyKind.Character, "y")));
        Assert.True(await overlay.Result);
    }

    [Fact]
    public async Task TextInputOverlay_ReturnsTypedText()
    {
        var overlay = new TextInputOverlay("name", null);
        overlay.HandleKey(new TerminalKey(TerminalKeyKind.Character, "a"));
        overlay.HandleKey(new TerminalKey(TerminalKeyKind.Character, "中"));
        overlay.HandleKey(new TerminalKey(TerminalKeyKind.Backspace));
        overlay.HandleKey(new TerminalKey(TerminalKeyKind.Character, "b"));

        Assert.True(overlay.HandleKey(new TerminalKey(TerminalKeyKind.Enter)));
        Assert.Equal("ab", await overlay.Result);
    }
}
