using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AiTools.Extensions;

/// <summary>自定义命令的参数解析结果。</summary>
public sealed class CommandArguments
{
    private static readonly Regex TokenPattern = new(
        "\"([^\"]*)\"|'([^']*)'|(\\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<string> _positional = new();

    private CommandArguments(string raw)
    {
        Raw = raw;
    }

    /// <summary>获取调用者输入的原始参数字符串（命令名之后、已去掉首尾空白）。</summary>
    public string Raw { get; }

    /// <summary>获取按 shell 风格拆分的位置参数（支持单双引号包裹多词）。</summary>
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>获取是否没有传入任何参数。</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Raw);

    /// <summary>按位置取参数，缺失时返回 null。</summary>
    public string? Get(int index) => index >= 0 && index < _positional.Count ? _positional[index] : null;

    /// <summary>解析斜杠命令后的参数文本。</summary>
    public static CommandArguments Parse(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var result = new CommandArguments(text);
        if (text.Length == 0)
        {
            return result;
        }

        foreach (Match match in TokenPattern.Matches(text))
        {
            if (match.Groups[1].Success)
            {
                result._positional.Add(match.Groups[1].Value);
            }
            else if (match.Groups[2].Success)
            {
                result._positional.Add(match.Groups[2].Value);
            }
            else
            {
                result._positional.Add(match.Groups[3].Value);
            }
        }

        return result;
    }

    /// <summary>构建按空格分隔的展示文本，必要时加引号。</summary>
    public string ToDisplayString()
    {
        if (_positional.Count == 0)
        {
            return Raw;
        }

        var builder = new StringBuilder();
        foreach (var item in _positional)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            if (item.IndexOf(' ') >= 0)
            {
                builder.Append('"').Append(item).Append('"');
            }
            else
            {
                builder.Append(item);
            }
        }

        return builder.ToString();
    }
}
