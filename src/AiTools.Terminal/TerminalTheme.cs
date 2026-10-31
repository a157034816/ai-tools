using System;
using System.Text;
using AiTools.Terminal.Rendering;

namespace AiTools.Terminal;

/// <summary>
/// 终端主题：一处定义颜色与符号，视图只引用语义名。
/// 支持真彩色时使用 24 位色板，否则退化为 256 色索引。
/// </summary>
public sealed class TerminalTheme
{
    /// <summary>创建默认主题。</summary>
    /// <param name="trueColor">是否使用真彩色。</param>
    /// <param name="unicode">是否使用 Unicode 符号与边框。</param>
    public TerminalTheme(bool trueColor, bool unicode)
    {
        TrueColor = trueColor;
        Unicode = unicode;

        Text = Color(0xE6E8EF, 252);
        Muted = Color(0x8B90A3, 245);
        Faint = Color(0x5C6172, 241);
        Accent = Color(0x7AA2F7, 111);
        AccentSoft = Color(0x3D59A1, 61);
        Secondary = Color(0xBB9AF7, 141);
        Success = Color(0x9ECE6A, 114);
        Warning = Color(0xE0AF68, 179);
        Error = Color(0xF7768E, 204);
        Border = Color(0x3B4261, 238);
        BorderFocus = Color(0x7AA2F7, 111);
        Surface = Color(0x1A1B26, 234);
        SurfaceAlt = Color(0x20222F, 235);
        CodeBackground = Color(0x1F2335, 236);
        CodeText = Color(0xC0CAF5, 189);

        BaseStyle = new TextStyle(Text, TerminalColor.Default);
        MutedStyle = new TextStyle(Muted, TerminalColor.Default);
        FaintStyle = new TextStyle(Faint, TerminalColor.Default);
        AccentStyle = new TextStyle(Accent, TerminalColor.Default);
        SecondaryStyle = new TextStyle(Secondary, TerminalColor.Default);
        SuccessStyle = new TextStyle(Success, TerminalColor.Default);
        WarningStyle = new TextStyle(Warning, TerminalColor.Default);
        ErrorStyle = new TextStyle(Error, TerminalColor.Default);
        BoldStyle = new TextStyle(Text, TerminalColor.Default, bold: true);
        CodeStyle = new TextStyle(CodeText, CodeBackground);
        InlineCodeStyle = new TextStyle(CodeText, SurfaceAlt);
        UserStyle = new TextStyle(Text, TerminalColor.Default);
        ReasoningStyle = new TextStyle(Faint, TerminalColor.Default, italic: true);
        InputStyle = new TextStyle(Text, SurfaceAlt);
        InputMutedStyle = new TextStyle(Muted, SurfaceAlt);
        InputPlaceholderStyle = new TextStyle(Faint, SurfaceAlt, italic: true);
        StatusStyle = new TextStyle(Muted, Surface);
        StatusAccentStyle = new TextStyle(Accent, Surface, bold: true);
        HeaderStyle = new TextStyle(Muted, Surface);
        HeaderTitleStyle = new TextStyle(Text, Surface, bold: true);
        SelectionStyle = new TextStyle(TerminalColor.Rgb(0x1A, 0x1B, 0x26), Accent, bold: true);
        OverlayStyle = new TextStyle(Text, SurfaceAlt);
        OverlayMutedStyle = new TextStyle(Muted, SurfaceAlt);
        OverlayTitleStyle = new TextStyle(Accent, SurfaceAlt, bold: true);
        OverlaySelectionStyle = new TextStyle(Text, AccentSoft, bold: true);
        OverlayBorderStyle = new TextStyle(Accent, SurfaceAlt);
        ToolStyle = new TextStyle(Secondary, TerminalColor.Default);
        ToolArgumentStyle = new TextStyle(Faint, TerminalColor.Default);
        ToolOkStyle = new TextStyle(Success, TerminalColor.Default);
        ToolErrorStyle = new TextStyle(Error, TerminalColor.Default);
        DiffAddStyle = new TextStyle(Success, TerminalColor.Default);
        DiffRemoveStyle = new TextStyle(Error, TerminalColor.Default);
        LinkStyle = new TextStyle(Accent, TerminalColor.Default, underline: true);
    }

    /// <summary>获取是否使用真彩色。</summary>
    public bool TrueColor { get; }

    /// <summary>获取是否使用 Unicode 符号。</summary>
    public bool Unicode { get; }

    /// <summary>正文色。</summary>
    public TerminalColor Text { get; }

    /// <summary>次要文字。</summary>
    public TerminalColor Muted { get; }

    /// <summary>更淡的辅助文字。</summary>
    public TerminalColor Faint { get; }

    /// <summary>强调色（品牌色）。</summary>
    public TerminalColor Accent { get; }

    /// <summary>强调色的暗调（选中背景）。</summary>
    public TerminalColor AccentSoft { get; }

    /// <summary>次级强调色（工具名等）。</summary>
    public TerminalColor Secondary { get; }

    /// <summary>成功色。</summary>
    public TerminalColor Success { get; }

    /// <summary>警告色。</summary>
    public TerminalColor Warning { get; }

    /// <summary>错误色。</summary>
    public TerminalColor Error { get; }

    /// <summary>边框色。</summary>
    public TerminalColor Border { get; }

    /// <summary>聚焦边框色。</summary>
    public TerminalColor BorderFocus { get; }

    /// <summary>面板底色。</summary>
    public TerminalColor Surface { get; }

    /// <summary>次级面板底色。</summary>
    public TerminalColor SurfaceAlt { get; }

    /// <summary>代码块底色。</summary>
    public TerminalColor CodeBackground { get; }

    /// <summary>代码文字色。</summary>
    public TerminalColor CodeText { get; }

    /// <summary>正文样式。</summary>
    public TextStyle BaseStyle { get; }

    /// <summary>次要文字样式。</summary>
    public TextStyle MutedStyle { get; }

    /// <summary>更淡的辅助文字样式。</summary>
    public TextStyle FaintStyle { get; }

    /// <summary>强调样式。</summary>
    public TextStyle AccentStyle { get; }

    /// <summary>次级强调样式。</summary>
    public TextStyle SecondaryStyle { get; }

    /// <summary>成功样式。</summary>
    public TextStyle SuccessStyle { get; }

    /// <summary>警告样式。</summary>
    public TextStyle WarningStyle { get; }

    /// <summary>错误样式。</summary>
    public TextStyle ErrorStyle { get; }

    /// <summary>加粗样式。</summary>
    public TextStyle BoldStyle { get; }

    /// <summary>代码块样式。</summary>
    public TextStyle CodeStyle { get; }

    /// <summary>行内代码样式。</summary>
    public TextStyle InlineCodeStyle { get; }

    /// <summary>用户输入样式。</summary>
    public TextStyle UserStyle { get; }

    /// <summary>推理过程样式。</summary>
    public TextStyle ReasoningStyle { get; }

    /// <summary>输入框正文样式。</summary>
    public TextStyle InputStyle { get; }

    /// <summary>输入框提示样式。</summary>
    public TextStyle InputMutedStyle { get; }

    /// <summary>输入框占位符样式。</summary>
    public TextStyle InputPlaceholderStyle { get; }

    /// <summary>状态栏样式。</summary>
    public TextStyle StatusStyle { get; }

    /// <summary>状态栏强调样式。</summary>
    public TextStyle StatusAccentStyle { get; }

    /// <summary>头部样式。</summary>
    public TextStyle HeaderStyle { get; }

    /// <summary>头部标题样式。</summary>
    public TextStyle HeaderTitleStyle { get; }

    /// <summary>选中项样式。</summary>
    public TextStyle SelectionStyle { get; }

    /// <summary>浮层正文样式。</summary>
    public TextStyle OverlayStyle { get; }

    /// <summary>浮层次要文字样式。</summary>
    public TextStyle OverlayMutedStyle { get; }

    /// <summary>浮层标题样式。</summary>
    public TextStyle OverlayTitleStyle { get; }

    /// <summary>浮层选中项样式。</summary>
    public TextStyle OverlaySelectionStyle { get; }

    /// <summary>浮层边框样式。</summary>
    public TextStyle OverlayBorderStyle { get; }

    /// <summary>工具名样式。</summary>
    public TextStyle ToolStyle { get; }

    /// <summary>工具参数样式。</summary>
    public TextStyle ToolArgumentStyle { get; }

    /// <summary>工具成功样式。</summary>
    public TextStyle ToolOkStyle { get; }

    /// <summary>工具失败样式。</summary>
    public TextStyle ToolErrorStyle { get; }

    /// <summary>diff 新增行样式。</summary>
    public TextStyle DiffAddStyle { get; }

    /// <summary>diff 删除行样式。</summary>
    public TextStyle DiffRemoveStyle { get; }

    /// <summary>链接样式。</summary>
    public TextStyle LinkStyle { get; }

    /// <summary>获取用户提示符。</summary>
    public string PromptSymbol => Unicode ? "❯" : ">";

    /// <summary>获取助手标记。</summary>
    public string AssistantSymbol => Unicode ? "●" : "*";

    /// <summary>获取工具标记。</summary>
    public string ToolSymbol => Unicode ? "⏺" : "#";

    /// <summary>获取项目符号。</summary>
    public string BulletSymbol => Unicode ? "•" : "*";

    /// <summary>获取完成勾选符号。</summary>
    public string CheckSymbol => Unicode ? "✓" : "+";

    /// <summary>获取失败叉号符号。</summary>
    public string CrossSymbol => Unicode ? "✗" : "x";

    /// <summary>获取思考中符号。</summary>
    public string ThinkingSymbol => Unicode ? "✳" : "*";

    /// <summary>获取当前主题的 spinner 帧。</summary>
    public IReadOnlyList<string> SpinnerFrames => Unicode
        ? new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" }
        : new[] { "|", "/", "-", "\\" };

    /// <summary>按主题能力创建默认主题。</summary>
    public static TerminalTheme CreateDefault()
    {
        var trueColor = TerminalCapabilities.SupportsTrueColor || IsModernTerminal();
        return new TerminalTheme(trueColor, SupportsUnicode());
    }

    private static bool IsModernTerminal()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))
               || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM_PROGRAM"))
               || string.Equals(Environment.GetEnvironmentVariable("TERM"), "xterm-256color", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SupportsUnicode()
    {
        try
        {
            var encoding = Console.OutputEncoding;
            if (encoding is UTF8Encoding)
            {
                return true;
            }

            if (encoding.CodePage == 65001)
            {
                return true;
            }
        }
        catch (Exception)
        {
            // 忽略：按环境变量判断。
        }

        var lang = Environment.GetEnvironmentVariable("LC_ALL")
                   ?? Environment.GetEnvironmentVariable("LC_CTYPE")
                   ?? Environment.GetEnvironmentVariable("LANG")
                   ?? string.Empty;
        return lang.IndexOf("UTF", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private TerminalColor Color(int rgb, byte fallbackIndex)
        => TrueColor
            ? TerminalColor.Rgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF))
            : TerminalColor.Indexed(fallbackIndex);
}
