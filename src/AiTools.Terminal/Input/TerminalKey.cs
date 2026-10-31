using System;
using System.Text;

namespace AiTools.Terminal.Input;

/// <summary>按键修饰键。</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>无修饰。</summary>
    None = 0,

    /// <summary>Shift。</summary>
    Shift = 1,

    /// <summary>Alt（部分终端用 Meta）。</summary>
    Alt = 2,

    /// <summary>Ctrl。</summary>
    Ctrl = 4
}

/// <summary>终端按键的种类。</summary>
public enum TerminalKeyKind
{
    /// <summary>可打印字符（可能包含多个码点，例如输入法上屏的结果）。</summary>
    Character,

    /// <summary>回车（提交）。</summary>
    Enter,

    /// <summary>Tab。</summary>
    Tab,

    /// <summary>退格。</summary>
    Backspace,

    /// <summary>Delete。</summary>
    Delete,

    /// <summary>Escape。</summary>
    Escape,

    /// <summary>上方向键。</summary>
    ArrowUp,

    /// <summary>下方向键。</summary>
    ArrowDown,

    /// <summary>左方向键。</summary>
    ArrowLeft,

    /// <summary>右方向键。</summary>
    ArrowRight,

    /// <summary>Home。</summary>
    Home,

    /// <summary>End。</summary>
    End,

    /// <summary>Page Up。</summary>
    PageUp,

    /// <summary>Page Down。</summary>
    PageDown,

    /// <summary>Insert。</summary>
    Insert,

    /// <summary>F1。</summary>
    F1,

    /// <summary>F2。</summary>
    F2,

    /// <summary>F3。</summary>
    F3,

    /// <summary>F4。</summary>
    F4,

    /// <summary>F5。</summary>
    F5,

    /// <summary>F6。</summary>
    F6,

    /// <summary>F7。</summary>
    F7,

    /// <summary>F8。</summary>
    F8,

    /// <summary>F9。</summary>
    F9,

    /// <summary>F10。</summary>
    F10,

    /// <summary>F11。</summary>
    F11,

    /// <summary>F12。</summary>
    F12,

    /// <summary>括号粘贴（整段文本一次到达）。</summary>
    Paste
}

/// <summary>一个解析后的终端按键事件。</summary>
public readonly struct TerminalKey
{
    /// <summary>创建按键。</summary>
    /// <param name="kind">种类。</param>
    /// <param name="text">文本（<see cref="TerminalKeyKind.Character"/> 与 <see cref="TerminalKeyKind.Paste"/> 有效）。</param>
    /// <param name="modifiers">修饰键。</param>
    public TerminalKey(TerminalKeyKind kind, string? text = null, KeyModifiers modifiers = KeyModifiers.None)
    {
        Kind = kind;
        Text = text ?? string.Empty;
        Modifiers = modifiers;
    }

    /// <summary>获取按键种类。</summary>
    public TerminalKeyKind Kind { get; }

    /// <summary>获取文本。</summary>
    public string Text { get; }

    /// <summary>获取修饰键。</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary>获取是否带 Ctrl。</summary>
    public bool IsCtrl => (Modifiers & KeyModifiers.Ctrl) != 0;

    /// <summary>获取是否带 Alt。</summary>
    public bool IsAlt => (Modifiers & KeyModifiers.Alt) != 0;

    /// <summary>获取是否带 Shift。</summary>
    public bool IsShift => (Modifiers & KeyModifiers.Shift) != 0;

    /// <summary>获取是否是可打印字符。</summary>
    public bool IsPrintable => Kind == TerminalKeyKind.Character && Text.Length > 0 && !IsCtrl;

    /// <summary>创建字符按键。</summary>
    public static TerminalKey Char(string text, KeyModifiers modifiers = KeyModifiers.None)
        => new(TerminalKeyKind.Character, text, modifiers);

    /// <inheritdoc />
    public override string ToString()
    {
        var prefix = IsCtrl ? "Ctrl+" : string.Empty;
        prefix += IsAlt ? "Alt+" : string.Empty;
        prefix += IsShift ? "Shift+" : string.Empty;
        return Kind == TerminalKeyKind.Character ? prefix + "\"" + Text + "\"" : prefix + Kind;
    }
}

/// <summary>
/// 把终端输入流解析为按键。解析器是纯函数式的（只读写自己的缓冲区），
/// 因此可以用单元测试覆盖各种终端差异，而不必依赖真实终端。
/// </summary>
public sealed class KeyParser
{
    private readonly StringBuilder _buffer = new();
    private bool _inPaste;

    /// <summary>获取缓冲区中是否只有一个等待超时判定的 ESC。</summary>
    public bool HasLoneEscape => !_inPaste && _buffer.Length == 1 && _buffer[0] == '\u001b';

    /// <summary>获取是否处于括号粘贴状态。</summary>
    public bool IsInPaste => _inPaste;

    /// <summary>追加一段已解码的文本。</summary>
    /// <param name="text">来自 stdin 的文本。</param>
    public void Feed(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _buffer.Append(text);
        }
    }

    /// <summary>尝试解析一个按键。</summary>
    /// <param name="key">解析结果。</param>
    /// <returns>是否解析出一个完整按键。</returns>
    public bool TryParse(out TerminalKey key)
    {
        key = default;
        while (true)
        {
            if (_buffer.Length == 0)
            {
                return false;
            }

            if (_inPaste)
            {
                return TryParsePaste(out key);
            }

            var first = _buffer[0];
            if (first == '\u001b')
            {
                return TryParseEscape(out key);
            }

            if (first == '\r' || first == '\n')
            {
                if (first == '\r' && _buffer.Length > 1 && _buffer[1] == '\n')
                {
                    _buffer.Remove(0, 2);
                }
                else
                {
                    _buffer.Remove(0, 1);
                }

                key = new TerminalKey(TerminalKeyKind.Enter);
                return true;
            }

            if (first == '\t')
            {
                _buffer.Remove(0, 1);
                key = new TerminalKey(TerminalKeyKind.Tab);
                return true;
            }

            if (first == '\b' || first == '\u007f')
            {
                _buffer.Remove(0, 1);
                key = new TerminalKey(TerminalKeyKind.Backspace);
                return true;
            }

            if (first < ' ')
            {
                _buffer.Remove(0, 1);
                if (first == '\0')
                {
                    key = new TerminalKey(TerminalKeyKind.Character, " ", KeyModifiers.Ctrl);
                    return true;
                }

                var letter = (char)('a' + first - 1);
                key = new TerminalKey(TerminalKeyKind.Character, letter.ToString(), KeyModifiers.Ctrl);
                return true;
            }

            // 代理对：等待低位代理到达后再整体产出。
            if (char.IsHighSurrogate(first))
            {
                if (_buffer.Length < 2)
                {
                    return false;
                }

                if (!char.IsLowSurrogate(_buffer[1]))
                {
                    _buffer.Remove(0, 1);
                    key = new TerminalKey(TerminalKeyKind.Character, first.ToString());
                    return true;
                }

                var pair = _buffer.ToString(0, 2);
                _buffer.Remove(0, 2);
                key = new TerminalKey(TerminalKeyKind.Character, pair);
                return true;
            }

            _buffer.Remove(0, 1);
            key = new TerminalKey(TerminalKeyKind.Character, first.ToString());
            return true;
        }
    }

    /// <summary>把单独的 ESC 当作 Escape 键（用于用户只按了 Esc 的场景）。</summary>
    public TerminalKey FlushEscape()
    {
        if (_buffer.Length > 0 && _buffer[0] == '\u001b')
        {
            _buffer.Remove(0, 1);
        }

        return new TerminalKey(TerminalKeyKind.Escape);
    }

    /// <summary>丢弃缓冲区中的内容（例如退出时）。</summary>
    public void Reset()
    {
        _buffer.Clear();
        _inPaste = false;
    }

    private bool TryParsePaste(out TerminalKey key)
    {
        key = default;
        var end = _buffer.ToString().IndexOf("\u001b[201~", StringComparison.Ordinal);
        if (end < 0)
        {
            // 限制粘贴缓冲，避免无终止符时无限增长。
            if (_buffer.Length > 4 * 1024 * 1024)
            {
                var text = _buffer.ToString();
                _buffer.Clear();
                _inPaste = false;
                key = new TerminalKey(TerminalKeyKind.Paste, text);
                return true;
            }

            return false;
        }

        var payload = _buffer.ToString(0, end);
        _buffer.Remove(0, end + 6);
        _inPaste = false;
        key = new TerminalKey(TerminalKeyKind.Paste, payload);
        return true;
    }

    private bool TryParseEscape(out TerminalKey key)
    {
        key = default;
        if (_buffer.Length == 1)
        {
            return false;
        }

        var second = _buffer[1];
        if (second == '[')
        {
            return TryParseCsi(out key);
        }

        if (second == 'O')
        {
            if (_buffer.Length < 3)
            {
                return false;
            }

            var final = _buffer[2];
            var mapped = final switch
            {
                'A' => TerminalKeyKind.ArrowUp,
                'B' => TerminalKeyKind.ArrowDown,
                'C' => TerminalKeyKind.ArrowRight,
                'D' => TerminalKeyKind.ArrowLeft,
                'H' => TerminalKeyKind.Home,
                'F' => TerminalKeyKind.End,
                'P' => TerminalKeyKind.F1,
                'Q' => TerminalKeyKind.F2,
                'R' => TerminalKeyKind.F3,
                'S' => TerminalKeyKind.F4,
                _ => (TerminalKeyKind?)null
            };

            if (mapped == null)
            {
                _buffer.Remove(0, 3);
                return TryParse(out key);
            }

            _buffer.Remove(0, 3);
            key = new TerminalKey(mapped.Value);
            return true;
        }

        if (second == '\u001b')
        {
            // 连续 ESC：第一个当作 Escape，剩余继续解析。
            _buffer.Remove(0, 1);
            key = new TerminalKey(TerminalKeyKind.Escape);
            return true;
        }

        // Alt + 字符（Alt+Enter 常见于"插入换行"）。
        if (second == '\r' || second == '\n')
        {
            _buffer.Remove(0, 2);
            key = new TerminalKey(TerminalKeyKind.Enter, modifiers: KeyModifiers.Alt);
            return true;
        }

        var character = char.ConvertFromUtf32(char.ConvertToUtf32(_buffer.ToString(), 1));
        var length = character.Length;
        _buffer.Remove(0, 1 + length);
        key = new TerminalKey(TerminalKeyKind.Character, character, KeyModifiers.Alt);
        return true;
    }

    private bool TryParseCsi(out TerminalKey key)
    {
        key = default;
        var finalIndex = -1;
        for (var index = 2; index < _buffer.Length; index++)
        {
            var code = _buffer[index];
            if (code >= 0x40 && code <= 0x7E)
            {
                finalIndex = index;
                break;
            }
        }

        if (finalIndex < 0)
        {
            // 异常长序列（鼠标、终端能力探测等）：丢弃 ESC[ 防止卡死。
            if (_buffer.Length > 64)
            {
                _buffer.Remove(0, 2);
                return TryParse(out key);
            }

            return false;
        }

        var parameters = _buffer.ToString(2, finalIndex - 2);
        var final = _buffer[finalIndex];

        if (final == '~')
        {
            var number = ParseFirstNumber(parameters, out var modifier);
            if (number == 200)
            {
                _buffer.Remove(0, finalIndex + 1);
                _inPaste = true;
                return TryParse(out key);
            }

            if (number == 201)
            {
                _buffer.Remove(0, finalIndex + 1);
                return TryParse(out key);
            }

            TerminalKeyKind? kind = number switch
            {
                1 or 7 => TerminalKeyKind.Home,
                2 => TerminalKeyKind.Insert,
                3 => TerminalKeyKind.Delete,
                4 or 8 => TerminalKeyKind.End,
                5 => TerminalKeyKind.PageUp,
                6 => TerminalKeyKind.PageDown,
                11 => TerminalKeyKind.F1,
                12 => TerminalKeyKind.F2,
                13 => TerminalKeyKind.F3,
                14 => TerminalKeyKind.F4,
                15 => TerminalKeyKind.F5,
                17 => TerminalKeyKind.F6,
                18 => TerminalKeyKind.F7,
                19 => TerminalKeyKind.F8,
                20 => TerminalKeyKind.F9,
                21 => TerminalKeyKind.F10,
                23 => TerminalKeyKind.F11,
                24 => TerminalKeyKind.F12,
                _ => null
            };

            _buffer.Remove(0, finalIndex + 1);
            if (kind == null)
            {
                return TryParse(out key);
            }

            key = new TerminalKey(kind.Value, modifiers: modifier);
            return true;
        }

        var parsedModifier = ParseModifier(parameters);
        TerminalKeyKind? arrow = final switch
        {
            'A' => TerminalKeyKind.ArrowUp,
            'B' => TerminalKeyKind.ArrowDown,
            'C' => TerminalKeyKind.ArrowRight,
            'D' => TerminalKeyKind.ArrowLeft,
            'H' => TerminalKeyKind.Home,
            'F' => TerminalKeyKind.End,
            'Z' => TerminalKeyKind.Tab,
            'P' => TerminalKeyKind.F1,
            'Q' => TerminalKeyKind.F2,
            'R' => TerminalKeyKind.F3,
            'S' => TerminalKeyKind.F4,
            _ => null
        };

        _buffer.Remove(0, finalIndex + 1);
        if (arrow == null)
        {
            return TryParse(out key);
        }

        if (final == 'Z')
        {
            parsedModifier |= KeyModifiers.Shift;
        }

        key = new TerminalKey(arrow.Value, modifiers: parsedModifier);
        return true;
    }

    private static int ParseFirstNumber(string parameters, out KeyModifiers modifier)
    {
        modifier = KeyModifiers.None;
        if (string.IsNullOrEmpty(parameters))
        {
            return -1;
        }

        var segments = parameters.Split(';');
        var number = int.TryParse(segments[0], out var value) ? value : -1;
        if (segments.Length > 1)
        {
            modifier = DecodeModifier(segments[1]);
        }

        return number;
    }

    private static KeyModifiers ParseModifier(string parameters)
    {
        if (string.IsNullOrEmpty(parameters))
        {
            return KeyModifiers.None;
        }

        var segments = parameters.Split(';');
        return segments.Length > 1 ? DecodeModifier(segments[1]) : KeyModifiers.None;
    }

    private static KeyModifiers DecodeModifier(string value)
    {
        if (!int.TryParse(value, out var mask) || mask <= 1)
        {
            return KeyModifiers.None;
        }

        mask -= 1;
        var modifier = KeyModifiers.None;
        if ((mask & 1) != 0)
        {
            modifier |= KeyModifiers.Shift;
        }

        if ((mask & 2) != 0)
        {
            modifier |= KeyModifiers.Alt;
        }

        if ((mask & 4) != 0)
        {
            modifier |= KeyModifiers.Ctrl;
        }

        return modifier;
    }
}
