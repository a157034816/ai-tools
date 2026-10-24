using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using AiTools.Extensions.Packages;
using Acornima;

namespace AiTools.Extensions.Scripting;

internal static class ScriptModuleCompiler
{
    public static bool RequiresBundle(string entryPath, IReadOnlyList<string> requiredPackages)
    {
        var source = File.ReadAllText(entryPath);
        var tokens = ScriptLexer.Read(source);
        return tokens.Any(token =>
        {
            if (token.Text != "require") return false;
            var specifier = ScriptLexer.NextCallString(source, tokens, token.Start);
            return specifier != null &&
                   (specifier.StartsWith(".", StringComparison.Ordinal) || requiredPackages.Contains(specifier, StringComparer.OrdinalIgnoreCase));
        });
    }
    public static void ValidateJavaScript(string path)
    {
        var parserOptions = new ParserOptions
        {
            EcmaVersion = EcmaVersion.Latest,
            AllowAwaitOutsideFunction = false,
            AllowReturnOutsideFunction = false
        };
        try
        {
            new Parser(parserOptions).ParseScript(File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"{path}: JavaScript 入口语法错误：{ex.Message}", ex);
        }
    }

    public static string BuildBundle(string entryPath, string packageRoot, ExtensionPackageManifest manifest, CancellationToken cancellationToken)
    {
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        var requires = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageNames = new HashSet<string>(manifest.Require, StringComparer.OrdinalIgnoreCase);
        var siblingEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Visit(string path, string? forcedId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            var id = forcedId ?? (IsWithin(packageRoot, fullPath)
                ? Path.GetRelativePath(packageRoot, fullPath).Replace('\\', '/')
                : "@" + Path.GetFileName(Path.GetDirectoryName(fullPath)) + "/" + Path.GetFileName(fullPath));
            if (modules.ContainsKey(id)) return id;
            if (!visiting.Add(fullPath)) return id;

            var raw = File.ReadAllText(fullPath);
            var extension = Path.GetExtension(fullPath);
            var transformed = string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
                ? "module.exports = " + raw + ";"
                : string.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase)
                    ? TypeScriptTranspiler.Transpile(raw, fullPath)
                    : raw;
            var tokens = ScriptLexer.Read(transformed);
            var local = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                if (token.Text != "require") continue;
                var specifier = ScriptLexer.NextCallString(transformed, tokens, token.Start);
                if (specifier == null) continue;
                if (specifier.StartsWith(".", StringComparison.Ordinal))
                {
                    var childPath = ResolveModule(fullPath, specifier, packageRoot);
                    if (childPath == null)
                    {
                        throw new TypeScriptTranspileException(fullPath, token.Line,
                            "unsupported module path: local require '" + specifier + "' resolves outside the package directory");
                    }

                    var childId = Visit(childPath);
                    local[specifier] = childId;
                }
                else if (packageNames.Contains(specifier))
                {
                    if (!siblingEntries.TryGetValue(specifier, out var siblingEntry))
                    {
                        siblingEntry = ResolveSiblingEntry(packageRoot, specifier);
                        if (siblingEntry == null)
                        {
                            throw new TypeScriptTranspileException(fullPath, token.Line,
                                "required sibling package '" + specifier + "' has no readable script entry in the workspace");
                        }
                        siblingEntries[specifier] = siblingEntry;
                    }

                    var childId = Visit(siblingEntry, "@" + specifier + "/" + Path.GetFileName(siblingEntry));
                    local[specifier] = childId;
                }
            }

            modules[id] = transformed;
            requires[id] = local;
            visiting.Remove(fullPath);
            return id;
        }

        var entryId = Visit(entryPath, "entry");
        var builder = new StringBuilder();
        builder.AppendLine("(function(){");
        builder.AppendLine("'use strict';");
        builder.AppendLine("var __defs = Object.create(null);");
        foreach (var pair in modules)
        {
            builder.Append("__defs[").Append(JsonSerializer.Serialize(pair.Key)).Append("] = function(module, exports, require){\n")
                .Append(pair.Value).AppendLine("\n};");
        }
        builder.AppendLine("var __maps = " + JsonSerializer.Serialize(requires) + ";");
        builder.AppendLine("var __cache = Object.create(null);");
        builder.AppendLine("function __load(id){ if (__cache[id]) return __cache[id].exports; if (!__defs[id]) throw new Error('Script module not found: '+id); var m={exports:{}}; __cache[id]=m; var map=__maps[id]||{}; function req(name){ if (Object.prototype.hasOwnProperty.call(map,name)) return __load(map[name]); return globalThis.__script_external_require(String(name)); } __defs[id](m,m.exports,req); return m.exports; }");
        builder.Append("globalThis.__script_extension_module = __load(").Append(JsonSerializer.Serialize(entryId)).AppendLine(");");
        builder.AppendLine("})();");
        var bundle = builder.ToString();
        ValidateJavaScriptText(bundle, entryPath + ".bundle.js");
        return bundle;
    }

    public static string ResolveLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        if (root.Length == 0) return full;
        var segments = full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { FullName.Length: > 0 } target)
                {
                    current = target.FullName;
                }
            }
            catch (IOException) { return full; }
            catch (UnauthorizedAccessException) { return full; }
        }
        return Path.GetFullPath(current);
    }

    private static string? ResolveModule(string parentFile, string specifier, string packageRoot)
    {
        var basePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(parentFile)!, specifier));
        var extensions = new[] { "", ".ts", ".js", ".cjs", ".mjs", ".json" };
        var candidates = extensions.Select(extension => basePath + extension)
            .Concat(new[] { "index.ts", "index.js", "index.cjs" }.Select(name => Path.Combine(basePath, name)));
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            var full = Path.GetFullPath(candidate);
            if (!IsWithin(packageRoot, full) || !IsWithin(ResolveLinks(packageRoot), ResolveLinks(full))) return null;
            if (string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase))
            {
                // JSON modules are package-local data, parsed as JavaScript object literals.
                return full;
            }
            return full;
        }
        throw new FileNotFoundException("Local script module '" + specifier + "' was not found beside " + parentFile + ".", basePath);
    }

    private static string? ResolveSiblingEntry(string packageRoot, string packageName)
    {
        var parent = Directory.GetParent(packageRoot)?.FullName;
        if (parent == null) return null;
        var candidates = new[]
        {
            Path.Combine(parent, packageName),
            Path.Combine(Directory.GetParent(parent)?.FullName ?? parent, packageName)
        };
        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(directory, "extension.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!document.RootElement.TryGetProperty("entry", out var entry) || entry.ValueKind != JsonValueKind.String) continue;
                var entryPath = Path.GetFullPath(Path.Combine(directory, entry.GetString()!));
                if (IsWithin(directory, entryPath) && File.Exists(entryPath)) return entryPath;
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static void ValidateJavaScriptText(string source, string path)
    {
        try
        {
            new Parser(new ParserOptions { EcmaVersion = EcmaVersion.Latest }).ParseScript(source, path);
        }
        catch (Exception ex)
        {
            throw new TypeScriptTranspileException(path, 1, "transpiled JavaScript is invalid: " + ex.Message, ex);
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        return string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class TypeScriptTranspileException : Exception
{
    public TypeScriptTranspileException(string path, int line, string message, Exception? inner = null)
        : base(path + ":" + line.ToString(CultureInfo.InvariantCulture) + ": " + message, inner)
    {
        Path = path;
        Line = line;
    }

    public string Path { get; }
    public int Line { get; }
}

internal static class ScriptLexer
{
    internal sealed record Token(string Text, int Start, int End, int Line, int Kind);

    public static List<Token> Read(string source)
    {
        var tokens = new List<Token>();
        var index = 0;
        var line = 1;
        while (index < source.Length)
        {
            var c = source[index];
            if (char.IsWhiteSpace(c))
            {
                if (c == '\n') line++;
                index++;
                continue;
            }
            if (c == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] != '\n') index++;
                continue;
            }
            if (c == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                {
                    if (source[index] == '\n') line++;
                    index++;
                }
                index = Math.Min(source.Length, index + 2);
                continue;
            }
            var start = index;
            var tokenLine = line;
            if (c == '\'' || c == '"' || c == '`')
            {
                var quote = c;
                index++;
                while (index < source.Length)
                {
                    if (source[index] == '\\') { index = Math.Min(source.Length, index + 2); continue; }
                    if (source[index] == '\n') line++;
                    if (source[index++] == quote) break;
                }
                tokens.Add(new Token(source.Substring(start, index - start), start, index, tokenLine, quote == '`' ? 3 : 2));
                continue;
            }
            if (IsIdentifierStart(c))
            {
                index++;
                while (index < source.Length && IsIdentifierPart(source[index])) index++;
                tokens.Add(new Token(source.Substring(start, index - start), start, index, tokenLine, 1));
                continue;
            }
            if (char.IsDigit(c) || c == '.' && index + 1 < source.Length && char.IsDigit(source[index + 1]))
            {
                var isRadixLiteral = c == '0' && index + 1 < source.Length && source[index + 1] is ('x' or 'X' or 'b' or 'B' or 'o' or 'O');
                index++;
                while (index < source.Length)
                {
                    var next = source[index];
                    if (char.IsLetterOrDigit(next) || next is '.' or '_') { index++; continue; }
                    if ((next == '+' || next == '-') && !isRadixLiteral && index > start && source[index - 1] is 'e' or 'E') { index++; continue; }
                    break;
                }
                tokens.Add(new Token(source.Substring(start, index - start), start, index, tokenLine, 4));
                continue;
            }
            var length = index + 2 < source.Length && source.Substring(index, 3) is "..." or "===" or "!==" or "=>?" or "??=" or "&&=" or "||=" ? 3 :
                index + 1 < source.Length && source.Substring(index, 2) is "=>" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "??" or "?." or "++" or "--" or "**" or "<<" or ">>" or "+=" or "-=" or "&=" or "|=" or "??" ? 2 : 1;
            index += length;
            tokens.Add(new Token(source.Substring(start, length), start, index, tokenLine, 0));
        }
        return tokens;
    }

    public static string? NextCallString(string source, IReadOnlyList<Token> tokens, int start)
    {
        var index = 0;
        while (index < tokens.Count && tokens[index].Start != start) index++;
        if (index + 3 >= tokens.Count || tokens[index + 1].Text != "(" || tokens[index + 2].Kind != 2 || tokens[index + 3].Text != ")") return null;
        return StringValue(tokens[index + 2].Text);
    }

    public static string StringValue(string raw)
    {
        if (raw.Length < 2) return string.Empty;
        if (raw[0] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(raw) ?? string.Empty; }
            catch (JsonException) { return raw.Substring(1, raw.Length - 2); }
        }
        var builder = new StringBuilder();
        for (var i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length - 1)
            {
                var next = raw[++i];
                builder.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => next });
            }
            else builder.Append(raw[i]);
        }
        return builder.ToString();
    }

    private static bool IsIdentifierStart(char c) => c == '_' || c == '$' || char.IsLetter(c);
    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || char.IsDigit(c);
}
