using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace AiTools.Extensions.Scripting;

internal static class TypeScriptTranspiler
{
    public static string Transpile(string source, string path)
    {
        RejectUnsupported(source, path);
        source = StripTypeDeclarations(source);
        source = RewriteEnums(source, path);
        source = RewriteModules(source, path);
        source = StripTypeSyntax(source, path);
        return source;
    }

    private static void RejectUnsupported(string source, string path)
    {
        var tokens = ScriptLexer.Read(source);
        var rejected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["namespace"] = "namespace declarations",
            ["implements"] = "class implements clauses",
            ["abstract"] = "abstract declarations",
            ["readonly"] = "readonly modifiers",
            ["satisfies"] = "satisfies expressions",
            ["keyof"] = "keyof type operators",
            ["infer"] = "infer type operators",
            ["unique"] = "unique symbol types",
            ["declare global"] = "global declaration blocks"
        };
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Text == "@")
            {
                throw Unsupported(path, token.Line, "decorators");
            }
            if (token.Text == "declare" && index + 1 < tokens.Count && tokens[index + 1].Text == "global")
            {
                throw Unsupported(path, token.Line, rejected["declare global"]);
            }
            if (token.Text == "module" && index + 2 < tokens.Count && tokens[index + 1].Text != "." && tokens[index + 2].Text == "{")
                throw Unsupported(path, token.Line, "module declarations");
            if (token.Text == "<" && index > 0 && tokens[index - 1].Kind == 1 && index + 1 < tokens.Count && tokens[index + 1].Kind == 1)
            {
                var genericClose = FindAngleClose(tokens, index);
                var isDeclaration = index > 1 && tokens[index - 2].Text is "function" or "class";
                var isMember = IsTypeDeclarationMember(tokens, index);
                if (!isDeclaration && !isMember && genericClose > index && genericClose + 1 < tokens.Count && tokens[genericClose + 1].Text == "(")
                    throw Unsupported(path, token.Line, "call-site generic arguments");
            }
            if (rejected.TryGetValue(token.Text, out var construct))
            {
                throw Unsupported(path, token.Line, construct);
            }
            if (token.Text == "<" && index + 1 < tokens.Count && tokens[index + 1].Kind == 1 &&
                (index == 0 || tokens[index - 1].Text is "return" or "=>" or "="))
            {
                throw Unsupported(path, token.Line, "JSX syntax");
            }
        }
    }
    private static bool IsTypeDeclarationMember(IReadOnlyList<ScriptLexer.Token> tokens, int index)
    {
        var openBraces = new Stack<int>();
        for (var cursor = 0; cursor < index; cursor++)
        {
            if (tokens[cursor].Text == "{") openBraces.Push(cursor);
            else if (tokens[cursor].Text == "}" && openBraces.Count > 0) openBraces.Pop();
        }
        if (openBraces.Count == 0) return false;
        var open = openBraces.Peek();
        for (var cursor = open - 1; cursor >= 0 && tokens[cursor].Text is not ("{" or "}" or ";"); cursor--)
        {
            if (tokens[cursor].Text is "interface" or "class") return true;
        }
        return false;
    }

    private static string StripTypeDeclarations(string source)
    {
        var tokens = ScriptLexer.Read(source);
        var edits = new List<Edit>();
        var braceDepth = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Text == "{") braceDepth++;
            if (token.Text == "}") braceDepth = Math.Max(0, braceDepth - 1);
            var keywordIndex = token.Text == "declare" ? index + 1 : index;
            if (keywordIndex >= tokens.Count) continue;
            var keyword = tokens[keywordIndex].Text;
            if (keyword is not ("interface" or "type")) continue;
            if (braceDepth > 0 && token.Text != "declare") continue;

            var endIndex = keyword == "interface"
                ? FindBlockDeclarationEnd(tokens, keywordIndex)
                : FindSimpleDeclarationEnd(source, tokens, keywordIndex);
            if (endIndex < keywordIndex) continue;
            var startIndex = index;
            if (startIndex > 0 && tokens[startIndex - 1].Text == "export") startIndex--;
            if (startIndex > 1 && tokens[startIndex - 2].Text == "export" && tokens[startIndex - 1].Text == "declare") startIndex -= 2;
            var end = tokens[endIndex].End;
            if (endIndex + 1 < tokens.Count && tokens[endIndex + 1].Text == ";") end = tokens[endIndex + 1].End;
            edits.Add(new Edit(tokens[startIndex].Start, end, string.Empty));
            index = endIndex;
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text != "declare") continue;
            var next = index + 1;
            if (next >= tokens.Count || tokens[next].Text is "interface" or "type") continue;
            var endIndex = FindSimpleDeclarationEnd(source, tokens, next);
            if (endIndex < next) continue;
            var end = tokens[endIndex].End;
            if (endIndex + 1 < tokens.Count && tokens[endIndex + 1].Text == ";") end = tokens[endIndex + 1].End;
            edits.Add(new Edit(tokens[index].Start, end, string.Empty));
            index = endIndex;
        }

        return Apply(source, edits);
    }

    private static int FindBlockDeclarationEnd(IReadOnlyList<ScriptLexer.Token> tokens, int start)
    {
        var brace = start;
        while (brace < tokens.Count && tokens[brace].Text != "{") brace++;
        if (brace >= tokens.Count) return FindSimpleDeclarationEnd(string.Empty, tokens, start);
        var depth = 0;
        for (var index = brace; index < tokens.Count; index++)
        {
            if (tokens[index].Text == "{") depth++;
            if (tokens[index].Text == "}" && --depth == 0) return index;
        }
        return tokens.Count - 1;
    }

    private static int FindSimpleDeclarationEnd(string source, IReadOnlyList<ScriptLexer.Token> tokens, int start)
    {
        var round = 0;
        var square = 0;
        var curly = 0;
        var angle = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            var text = tokens[index].Text;
            if (text == "(" ) round++;
            else if (text == ")") round--;
            else if (text == "[") square++;
            else if (text == "]") square--;
            else if (text == "{") curly++;
            else if (text == "}")
            {
                if (curly == 0) return index - 1;
                curly--;
            }
            else if (text == "<") angle++;
            else if (text == ">" && angle > 0) angle--;

            if (text == ";" && round == 0 && square == 0 && curly == 0 && angle == 0) return index;
            if (index > start && round == 0 && square == 0 && curly == 0 && angle == 0 &&
                tokens[index].Line > tokens[index - 1].Line && IsDeclarationStart(text)) return index - 1;
        }
        return tokens.Count - 1;
    }

    private static bool IsDeclarationStart(string text)
        => text is "export" or "import" or "const" or "let" or "var" or "function" or "class" or "interface" or "type" or "enum" or "declare";

    private static string RewriteModules(string source, string path)
    {
        var tokens = ScriptLexer.Read(source);
        var edits = new List<Edit>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text == "import")
            {
                if (index + 1 < tokens.Count && tokens[index + 1].Text == "(")
                    throw Unsupported(path, tokens[index].Line, "dynamic import");
                var endIndex = FindModuleStatementEnd(tokens, index);
                if (endIndex < index) throw Unsupported(path, tokens[index].Line, "non-literal or dynamic import");
                var statement = tokens.Skip(index).Take(endIndex - index + 1).ToArray();
                if (index + 1 < tokens.Count && tokens[index + 1].Text == "type")
                {
                    edits.Add(new Edit(tokens[index].Start, tokens[endIndex].End, string.Empty));
                    index = endIndex;
                    continue;
                }
                var specifierToken = statement.LastOrDefault(token => token.Kind == 2);
                if (specifierToken == null) throw Unsupported(path, tokens[index].Line, "non-literal import source");
                var specifier = ScriptLexer.StringValue(specifierToken.Text);
                var fromIndex = Array.FindIndex(statement, token => token.Text == "from");
                if (fromIndex < 0 && statement.Length > 2 && statement[1].Kind == 2)
                {
                    edits.Add(new Edit(tokens[index].Start, tokens[endIndex].End,
                        "require(" + JsonSerializer.Serialize(specifier) + ");"));
                    index = endIndex;
                    continue;
                }
                if (fromIndex < 0) throw Unsupported(path, tokens[index].Line, "unsupported import declaration");

                var importBindings = statement.Skip(1).Take(fromIndex - 1).ToArray();
                var declarations = new List<string>();
                var braceStart = Array.FindIndex(importBindings, token => token.Text == "{");
                var braceEnd = Array.FindIndex(importBindings, token => token.Text == "}");
                if (braceStart >= 0 && braceEnd > braceStart)
                {
                    var cursor = braceStart + 1;
                    while (cursor < braceEnd)
                    {
                        if (importBindings[cursor].Text == ",") { cursor++; continue; }
                        var imported = importBindings[cursor++];
                        if (imported.Kind != 1) throw Unsupported(path, imported.Line, "complex named imports");
                        var localName = imported.Text;
                        if (cursor < braceEnd && importBindings[cursor].Text == "as")
                        {
                            cursor++;
                            if (cursor >= braceEnd || importBindings[cursor].Kind != 1)
                                throw Unsupported(path, imported.Line, "invalid named import alias");
                            localName = importBindings[cursor++].Text;
                        }
                        declarations.Add("const " + localName + " = require(" + JsonSerializer.Serialize(specifier) + ")[" + JsonSerializer.Serialize(imported.Text) + "];");
                        if (cursor < braceEnd && importBindings[cursor].Text != ",")
                            throw Unsupported(path, importBindings[cursor].Line, "complex named imports");
                    }
                }
                var namespaceToken = Array.FindIndex(importBindings, token => token.Text == "*");
                if (namespaceToken >= 0)
                {
                    var asIndex = Array.FindIndex(importBindings, namespaceToken, token => token.Text == "as");
                    if (asIndex < 0 || asIndex + 1 >= importBindings.Length) throw Unsupported(path, tokens[index].Line, "namespace import without a local name");
                    declarations.Add("const " + importBindings[asIndex + 1].Text + " = require(" + JsonSerializer.Serialize(specifier) + ");");
                }
                var defaultName = importBindings.Length > 0 && importBindings[0].Kind == 1 ? importBindings[0].Text : null;
                if (defaultName != null)
                    declarations.Insert(0, "const " + defaultName + " = require(" + JsonSerializer.Serialize(specifier) + ")[\"default\"];");
                edits.Add(new Edit(tokens[index].Start, tokens[endIndex].End, string.Join(" ", declarations)));
                index = endIndex;
                continue;
            }

            if (tokens[index].Text != "export") continue;
            if (index + 1 >= tokens.Count) continue;
            var next = tokens[index + 1].Text;
            if (next is "interface" or "type") continue;
            if (next == "=") throw Unsupported(path, tokens[index].Line, "export = assignment; use module.exports = value instead");
            if (next == "*") throw Unsupported(path, tokens[index].Line, "export * re-export");
            if (next == "{")
            {
                var close = FindMatching(tokens, index + 1, "{", "}");
                if (close < 0) throw Unsupported(path, tokens[index].Line, "malformed named export");
                var from = close + 1 < tokens.Count && tokens[close + 1].Text == "from";
                if (from)
                {
                    if (close + 2 >= tokens.Count || tokens[close + 2].Kind != 2) throw Unsupported(path, tokens[index].Line, "non-literal re-export source");
                    var specifier = ScriptLexer.StringValue(tokens[close + 2].Text);
                    var statements = ParseExportList(tokens, index + 2, close, path);
                    edits.Add(new Edit(tokens[index].Start, tokens[close + 2].End,
                        string.Join(" ", statements.Select(pair => "exports." + pair.ExportName + " = require(" + JsonSerializer.Serialize(specifier) + ")." + pair.LocalName + ";"))));
                    index = close + 2;
                }
                else
                {
                    var end = close + (close + 1 < tokens.Count && tokens[close + 1].Text == ";" ? 1 : 0);
                    var statements = ParseExportList(tokens, index + 2, close, path);
                    edits.Add(new Edit(tokens[index].Start, tokens[end].End,
                        string.Join(" ", statements.Select(pair => "exports." + pair.ExportName + " = " + pair.LocalName + ";"))));
                    index = end;
                }
                continue;
            }
            if (next == "default")
            {
                if (index + 2 < tokens.Count && tokens[index + 2].Text == "function")
                {
                    var nameIndex = index + 3 < tokens.Count && tokens[index + 3].Kind == 1 ? index + 3 : -1;
                    if (nameIndex >= 0)
                    {
                        var end = FindDeclarationEnd(tokens, index + 2, "function");
                        edits.Add(new Edit(tokens[index].Start, tokens[index + 1].End, string.Empty));
                        edits.Add(new Edit(tokens[end].End, tokens[end].End, "\nexports.default = " + tokens[nameIndex].Text + ";"));
                    }
                    else edits.Add(new Edit(tokens[index].Start, tokens[index + 1].End, "exports.default ="));
                }
                else
                {
                    edits.Add(new Edit(tokens[index].Start, tokens[index + 1].End, "exports.default ="));
                }
                continue;
            }
            if (next is "function" or "class" or "enum" or "const" or "let" or "var" || next == "async" && index + 2 < tokens.Count && tokens[index + 2].Text == "function")
            {
                var declarationStart = index + (next == "async" ? 2 : 1);
                var declarationKind = tokens[declarationStart].Text;
                var nameIndex = declarationStart + 1;
                while (nameIndex < tokens.Count && tokens[nameIndex].Text is "*" or "<") nameIndex++;
                if (nameIndex >= tokens.Count || tokens[nameIndex].Kind != 1)
                    throw Unsupported(path, tokens[index].Line, "exported declaration without a simple name");
                var name = tokens[nameIndex].Text;
                var end = FindDeclarationEnd(tokens, declarationStart, declarationKind);
                edits.Add(new Edit(tokens[index].Start, tokens[index].End, string.Empty));
                edits.Add(new Edit(tokens[end].End, tokens[end].End, "\nexports." + name + " = " + name + ";"));
                index = declarationStart;
            }
        }
        return Apply(source, edits);
    }

    private static List<(string LocalName, string ExportName)> ParseExportList(IReadOnlyList<ScriptLexer.Token> tokens, int start, int end, string path)
    {
        var result = new List<(string LocalName, string ExportName)>();
        var cursor = start;
        while (cursor < end)
        {
            if (tokens[cursor].Text == ",") { cursor++; continue; }
            if (tokens[cursor].Kind != 1) throw Unsupported(path, tokens[cursor].Line, "complex export list");
            var local = tokens[cursor++].Text;
            var export = local;
            if (cursor < end && tokens[cursor].Text == "as")
            {
                cursor++;
                if (cursor >= end || tokens[cursor].Kind != 1) throw Unsupported(path, tokens[cursor - 1].Line, "invalid export alias");
                export = tokens[cursor++].Text;
            }
            result.Add((local, export));
        }
        return result;
    }

    private static int FindModuleStatementEnd(IReadOnlyList<ScriptLexer.Token> tokens, int start)
    {
        for (var index = start + 1; index < tokens.Count; index++)
        {
            if (tokens[index].Text == ";") return index;
            if (tokens[index].Line > tokens[index - 1].Line && IsDeclarationStart(tokens[index].Text)) return index - 1;
        }
        return tokens.Count - 1;
    }

    private static int FindDeclarationEnd(IReadOnlyList<ScriptLexer.Token> tokens, int start, string kind)
    {
        if (kind is "function" or "class" or "enum")
        {
            var brace = start;
            while (brace < tokens.Count && tokens[brace].Text != "{") brace++;
            if (brace < tokens.Count)
            {
                var close = FindMatching(tokens, brace, "{", "}");
                if (close >= 0) return close;
            }
        }
        var round = 0; var square = 0; var curly = 0; var angle = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            var text = tokens[index].Text;
            if (text == "(") round++;
            else if (text == ")") round--;
            else if (text == "[") square++;
            else if (text == "]") square--;
            else if (text == "{") curly++;
            else if (text == "}") { if (curly == 0) return index - 1; curly--; }
            else if (text == "<") angle++;
            else if (text == ">" && angle > 0) angle--;
            if (text == ";" && round == 0 && square == 0 && curly == 0 && angle == 0) return index;
            if (index > start && round == 0 && square == 0 && curly == 0 && angle == 0 && tokens[index].Line > tokens[index - 1].Line && IsDeclarationStart(text)) return index - 1;
        }
        return tokens.Count - 1;
    }

    private static string RewriteEnums(string source, string path)
    {
        var tokens = ScriptLexer.Read(source);
        var edits = new List<Edit>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text != "enum") continue;
            if (index + 2 >= tokens.Count || tokens[index + 1].Kind != 1 || tokens[index + 2].Text != "{")
                throw Unsupported(path, tokens[index].Line, "enum without a simple name/body");
            var name = tokens[index + 1].Text;
            var close = FindMatching(tokens, index + 2, "{", "}");
            if (close < 0) throw Unsupported(path, tokens[index].Line, "unterminated enum declaration");
            var members = new List<(string Name, string? Literal)>();
            var cursor = index + 3;
            var implicitValue = 0d;
            while (cursor < close)
            {
                if (tokens[cursor].Text == ",") { cursor++; continue; }
                var memberToken = tokens[cursor++];
                if (memberToken.Kind is not (1 or 2)) throw Unsupported(path, memberToken.Line, "computed enum member name");
                var member = memberToken.Kind == 2 ? ScriptLexer.StringValue(memberToken.Text) : memberToken.Text;
                string? literal = null;
                if (cursor < close && tokens[cursor].Text == "=")
                {
                    cursor++;
                    var valueIndex = cursor;
                    var negative = valueIndex < close && tokens[valueIndex].Text == "-";
                    if (negative) valueIndex++;
                    if (valueIndex >= close || tokens[valueIndex].Kind is not (2 or 4) || negative && tokens[valueIndex].Kind != 4)
                        throw Unsupported(path, memberToken.Line, "enum initializer must be a string or numeric literal");
                    var nextIndex = valueIndex + 1;
                    if (nextIndex < close && tokens[nextIndex].Text != ",")
                        throw Unsupported(path, memberToken.Line, "enum initializer must be a string or numeric literal");
                    var value = (negative ? "-" : string.Empty) + tokens[valueIndex].Text;
                    literal = tokens[valueIndex].Kind == 2 ? JsonSerializer.Serialize(ScriptLexer.StringValue(value)) : value;
                    cursor = nextIndex;
                    if (tokens[valueIndex].Kind == 2)
                    {
                        implicitValue = double.NaN;
                    }
                    else if (TryParseEnumNumber(value, out var number))
                    {
                        implicitValue = number + 1;
                    }
                    else
                    {
                        throw Unsupported(path, memberToken.Line, "enum initializer must be a string or numeric literal");
                    }
                }
                else if (double.IsNaN(implicitValue))
                {
                    throw Unsupported(path, memberToken.Line, "numeric enum member after a string initializer must specify a value");
                }
                if (literal == null)
                {
                    literal = implicitValue.ToString(CultureInfo.InvariantCulture);
                    implicitValue++;
                }
                members.Add((member, literal));
                while (cursor < close && tokens[cursor].Text != ",") cursor++;
            }
            var body = new StringBuilder("var ").Append(name).Append(" = (function (e) { e = e || {}; ");
            foreach (var member in members)
            {
                var memberKey = JsonSerializer.Serialize(member.Name);
                if (member.Literal!.StartsWith("\"", StringComparison.Ordinal))
                {
                    body.Append("e[").Append(memberKey).Append("] = ").Append(member.Literal).Append("; ");
                }
                else
                {
                    body.Append("e[e[").Append(memberKey).Append("] = ").Append(member.Literal).Append("] = ").Append(memberKey).Append("; ");
                }
            }
            body.Append("return e; })(").Append(name).Append(" || ({}));");
            edits.Add(new Edit(tokens[index].Start, tokens[close].End, body.ToString()));
            index = close;
        }
        return Apply(source, edits);
    }

    private static bool TryParseEnumNumber(string value, out double number)
    {
        var negative = value.StartsWith("-", StringComparison.Ordinal);
        var unsigned = negative ? value.Substring(1) : value;
        var radix = unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 :
            unsigned.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? 2 :
            unsigned.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? 8 : 0;
        if (radix != 0)
        {
            var digits = unsigned.Substring(2);
            if (digits.Length == 0) { number = 0; return false; }
            var parsed = BigInteger.Zero;
            var previousUnderscore = false;
            foreach (var character in digits)
            {
                if (character == '_')
                {
                    if (previousUnderscore || parsed == BigInteger.Zero && digits[0] == '_') { number = 0; return false; }
                    previousUnderscore = true;
                    continue;
                }
                var normalized = char.ToLowerInvariant(character);
                var digit = normalized is >= '0' and <= '9' ? normalized - '0' : normalized is >= 'a' and <= 'f' ? normalized - 'a' + 10 : -1;
                if (digit < 0 || digit >= radix) { number = 0; return false; }
                parsed = parsed * radix + digit;
                previousUnderscore = false;
            }
            if (previousUnderscore) { number = 0; return false; }
            number = (double)parsed;
            if (negative) number = -number;
            return true;
        }

        for (var index = 0; index < unsigned.Length; index++)
        {
            if (unsigned[index] == '_' && (index == 0 || index == unsigned.Length - 1 ||
                !char.IsDigit(unsigned[index - 1]) || !char.IsDigit(unsigned[index + 1])))
            {
                number = 0;
                return false;
            }
        }
        return double.TryParse(value.Replace("_", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static string StripTypeSyntax(string source, string path)
    {
        var tokens = ScriptLexer.Read(source);
        var edits = new List<Edit>();
        var pairs = BuildPairs(tokens);

        // Generic parameters on function and class declarations.
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text != "<") continue;
            var prior = index - 1;
            var isGenericDeclaration = prior >= 0 && tokens[prior].Kind == 1 && prior > 0 && tokens[prior - 1].Text is "function" or "class";
            if (!isGenericDeclaration && prior >= 0 && tokens[prior].Text is "function" or "class") isGenericDeclaration = true;
            if (!isGenericDeclaration) continue;
            var close = FindAngleClose(tokens, index);
            if (close < 0) throw Unsupported(path, tokens[index].Line, "malformed generic declaration");
            edits.Add(new Edit(tokens[index].Start, tokens[close].End, string.Empty));
            index = close;
        }

        var parameterPairs = new List<(int Open, int Close)>();
        foreach (var pair in pairs)
        {
            if (pair.Key.Item1 == "(" && IsParameterList(tokens, pair.Value)) parameterPairs.Add((pair.Key.Item2, pair.Value));
        }
        var typeAnnotationRanges = new List<(int Start, int End)>();
        bool InTypeAnnotation(int tokenIndex)
            => typeAnnotationRanges.Any(range => tokenIndex >= range.Start && tokenIndex <= range.End);
        bool InParameters(int tokenIndex)
            => parameterPairs.Any(pair => tokenIndex > pair.Open && tokenIndex < pair.Close);

        // Parameter, variable, and field annotations.
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text != ":") continue;
            if (InTypeAnnotation(index)) continue;
            var isParameter = InParameters(index);
            var isVariable = IsVariableAnnotation(tokens, index);
            var isField = IsClassFieldAnnotation(tokens, index, pairs);
            if (!isParameter && !isVariable && !isField) continue;
            var stop = isParameter ? new[] { ",", ")", "=" } : new[] { "=", ";", "," };
            var end = FindTypeEnd(tokens, index + 1, stop, stopAtBody: false);
            if (end < index + 1) continue;
            edits.Add(new Edit(tokens[index].Start, tokens[end].End, string.Empty));
            typeAnnotationRanges.Add((index, end));
            index = end;
        }

        // Optional markers before annotations are stripped, while optional object-type members remain inside their annotation range.
        for (var index = 0; index < tokens.Count; index++)
        {
            if (InParameters(index) && !InTypeAnnotation(index) && tokens[index].Text == "?" &&
                index + 1 < tokens.Count && tokens[index + 1].Text is ":" or "," or ")")
                edits.Add(new Edit(tokens[index].Start, tokens[index].End, string.Empty));
        }

        // Function and arrow-function return annotations (the colon directly after a parameter list).
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text != ")" || InTypeAnnotation(index) || !pairs.TryGetValue((")", index), out _)) continue;
            var open = FindMatchingOpen(tokens, index, "(", ")");
            if (open < 0 || !IsParameterList(tokens, index)) continue;
            if (index + 1 >= tokens.Count || tokens[index + 1].Text != ":") continue;
            var end = FindTypeEnd(tokens, index + 2, new[] { "{", "=>", ";" }, stopAtBody: true);
            if (end >= index + 2)
            {
                edits.Add(new Edit(tokens[index + 1].Start, tokens[end].End, string.Empty));
                typeAnnotationRanges.Add((index + 1, end));
            }
        }

        // Type assertions and non-null assertions.
        for (var index = 0; index < tokens.Count; index++)
        {
            if (InTypeAnnotation(index)) continue;
            if (tokens[index].Text == "as")
            {
                var end = FindTypeEnd(tokens, index + 1, new[] { ",", ";", ")", "]", "}", "?", ":", "+", "-", "*", "/", "&&", "||", "??" }, stopAtBody: false);
                if (end < index + 1) throw Unsupported(path, tokens[index].Line, "as cast without a type");
                edits.Add(new Edit(tokens[index].Start, tokens[end].End, string.Empty));
                index = end;
                continue;
            }
            if (tokens[index].Text == "!" && index > 0 && index + 1 < tokens.Count &&
                tokens[index - 1].Kind == 1 && tokens[index + 1].Text is "." or "[" or "(" or ";" or "," or ")" or "?" or ":" or "}")
            {
                edits.Add(new Edit(tokens[index].Start, tokens[index].End, string.Empty));
            }
        }

        return Apply(source, edits);
    }

    private static bool IsVariableAnnotation(IReadOnlyList<ScriptLexer.Token> tokens, int colon)
    {
        var start = colon;
        while (start > 0 && tokens[start - 1].Text is not (";" or "{" or "}")) start--;
        var declaration = -1;
        for (var index = start; index < colon; index++)
        {
            if (tokens[index].Text is "const" or "let" or "var") declaration = index;
            if (declaration >= 0 && tokens[index].Text == "=") return false;
        }
        return declaration >= 0 && colon > declaration + 1;
    }

    private static bool IsClassFieldAnnotation(IReadOnlyList<ScriptLexer.Token> tokens, int colon, IReadOnlyDictionary<(string, int), int> pairs)
    {
        for (var index = colon - 1; index >= 0; index--)
        {
            if (tokens[index].Text != "{") continue;
            var close = pairs.TryGetValue(("{", index), out var matched) ? matched : -1;
            if (close < colon) continue;
            var classIndex = index - 1;
            while (classIndex >= 0 && tokens[classIndex].Text is not ("class" or "{" or "}" or ";")) classIndex--;
            if (classIndex < 0 || tokens[classIndex].Text != "class") continue;
            var depth = 0;
            for (var between = index + 1; between < colon; between++)
            {
                if (tokens[between].Text == "{") depth++;
                if (tokens[between].Text == "}") depth--;
            }
            if (depth != 0) return false;
            var lineStart = colon - 1;
            while (lineStart > index && tokens[lineStart - 1].Line == tokens[colon].Line && tokens[lineStart - 1].Text is not (";" or "}")) lineStart--;
            return !tokens.Skip(lineStart).Take(colon - lineStart).Any(token => token.Text == "=");
        }
        return false;
    }

    private static int FindTypeEnd(IReadOnlyList<ScriptLexer.Token> tokens, int start, IReadOnlyCollection<string> stopTokens, bool stopAtBody)
    {
        var round = 0; var square = 0; var curly = 0; var angle = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            var text = tokens[index].Text;
            if (round == 0 && square == 0 && curly == 0 && angle == 0 && stopTokens.Contains(text)) return index - 1;
            if (text == "(") round++;
            else if (text == ")") { if (round == 0) return index - 1; round--; }
            else if (text == "[") square++;
            else if (text == "]") { if (square == 0) return index - 1; square--; }
            else if (text == "{")
            {
                if (stopAtBody && round == 0 && square == 0 && curly == 0 && angle == 0)
                {
                    var close = FindMatching(tokens, index, "{", "}");
                    var following = close + 1 < tokens.Count ? tokens[close + 1].Text : string.Empty;
                    if (close < 0 || following is not ("=>" or "{")) return index - 1;
                }
                curly++;
            }
            else if (text == "}") { if (curly == 0) return index - 1; curly--; }
            else if (text == "<") angle++;
            else if (text == ">") { if (angle > 0) angle--; }
        }
        return tokens.Count - 1;
    }

    private static bool IsParameterList(IReadOnlyList<ScriptLexer.Token> tokens, int close)
    {
        if (close < 0) return false;
        var open = FindMatchingOpen(tokens, close, "(", ")");
        if (open < 0) return false;
        if (open == 0) return false;
        var before = tokens[open - 1].Text;
        var after = close + 1 < tokens.Count ? tokens[close + 1].Text : string.Empty;
        if (after is ":" or "=>" or "{") return true;
        return false;
    }

    private static IReadOnlyDictionary<(string, int), int> BuildPairs(IReadOnlyList<ScriptLexer.Token> tokens)
    {
        var result = new Dictionary<(string, int), int>();
        var stacks = new Dictionary<string, Stack<int>> { ["("] = new(), ["["] = new(), ["{"] = new() };
        foreach (var index in Enumerable.Range(0, tokens.Count))
        {
            var text = tokens[index].Text;
            if (stacks.TryGetValue(text, out var stack)) stack.Push(index);
            else if (text is ")" or "]" or "}")
            {
                var opener = text == ")" ? "(" : text == "]" ? "[" : "{";
                if (stacks[opener].Count > 0)
                {
                    var open = stacks[opener].Pop();
                    result[(opener, open)] = index;
                    result[(text, index)] = open;
                }
            }
        }
        return result;
    }

    private static int FindMatchingOpen(IReadOnlyList<ScriptLexer.Token> tokens, int close, string openText, string closeText)
    {
        var depth = 0;
        for (var index = close; index >= 0; index--)
        {
            if (tokens[index].Text == closeText) depth++;
            else if (tokens[index].Text == openText && --depth == 0) return index;
        }
        return -1;
    }

    private static int FindMatching(IReadOnlyList<ScriptLexer.Token> tokens, int open, string openText, string closeText)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (tokens[index].Text == openText) depth++;
            else if (tokens[index].Text == closeText && --depth == 0) return index;
        }
        return -1;
    }

    private static int FindAngleClose(IReadOnlyList<ScriptLexer.Token> tokens, int open)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (tokens[index].Text == "<") depth++;
            else if (tokens[index].Text == ">" && --depth == 0) return index;
            if (tokens[index].Text is ";" or "{") return -1;
        }
        return -1;
    }

    private static TypeScriptTranspileException Unsupported(string path, int line, string construct)
        => new(path, line, "unsupported TypeScript construct: " + construct);

    private static string Apply(string source, List<Edit> edits)
    {
        if (edits.Count == 0) return source;
        edits.Sort((left, right) => right.Start.CompareTo(left.Start));
        var builder = new StringBuilder(source);
        var boundary = source.Length + 1;
        foreach (var edit in edits)
        {
            if (edit.Start < 0 || edit.End < edit.Start || edit.End > source.Length) continue;
            if (edit.End > boundary) continue;
            builder.Remove(edit.Start, edit.End - edit.Start);
            builder.Insert(edit.Start, edit.Text);
            boundary = edit.Start;
        }
        return builder.ToString();
    }

    private sealed record Edit(int Start, int End, string Text);
}
