using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Acornima;
using Acornima.Ast;
using Jint;
using Jint.Native;
using JsJsonSerializer = Jint.Native.Json.JsonSerializer;

namespace AiTools.Extensions.Workflows;

/// <summary>
/// 一份 JavaScript 工作流：<c>export const meta = { … }</c> 加一段可编排 <c>agent</c>/<c>tool</c> 的脚本正文。
/// 与 Pi 的 workflow 一致：脚本是数据，不是插件命令；由 <see cref="WorkflowRuntime"/> 在沙箱里执行。
/// </summary>
public sealed class WorkflowDefinition
{
    /// <summary>获取工作流名称（<c>meta.name</c>，缺失时回退为文件名）。同时用作斜杠命令名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取工作流描述（<c>meta.description</c>）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>获取来源文件路径。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>获取脚本正文：已去掉 <c>meta</c> 声明，保留其余顶层语句。</summary>
    public string Script { get; init; } = string.Empty;

    /// <summary>获取拥有该工作流的插件名；宿主直接加载时为 <c>host</c>。</summary>
    public string PluginName { get; init; } = "host";

    /// <summary>获取 <c>meta.parameters</c> 声明的具名位置参数，用于 <c>$name</c> 替换。</summary>
    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 获取 <c>meta.arguments</c> 声明的参数模板（默认 <c>$ARGUMENTS</c>）。命令调用时按
    /// <c>$ARGUMENTS</c>/<c>$1</c>/<c>$name</c> 渲染，渲染结果即脚本里的 <c>arguments</c>。
    /// </summary>
    public string ArgumentTemplate { get; init; } = "$ARGUMENTS";

    /// <summary>获取原始 <c>meta</c> 对象的 JSON 文本；脚本里的 <c>meta</c> 全局以它为底。</summary>
    public string? MetaJson { get; init; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// 从磁盘加载工作流。文件格式为一段带 <c>export const meta = { … }</c> 的 JavaScript：
/// <c>meta</c> 用真正的 JS 求值（Jint）解析，不做正则匹配。加载过程不抛出异常，
/// 问题通过返回值与 <c>error</c> 参数说明。
/// </summary>
public static class WorkflowLoader
{
    /// <summary>单个脚本的字节上限（200 KB）。超过即拒绝加载，避免把宿主内存当草稿纸。</summary>
    public const int MaxScriptBytes = 200 * 1024;

    /// <summary>加载单个工作流文件。</summary>
    /// <param name="filePath">工作流 <c>.js</c> 文件路径。</param>
    /// <param name="pluginName">拥有者插件名；null 表示宿主。</param>
    /// <param name="error">
    /// 诊断文本。返回值为 null 时表示无法加载；返回值非 null 时表示可恢复的告警（例如缺少 <c>meta</c>）。
    /// </param>
    /// <returns>加载成功时返回定义，否则返回 null。</returns>
    public static WorkflowDefinition? Load(string filePath, string? pluginName, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "工作流文件路径为空。";
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex)
        {
            error = "工作流文件路径无效：" + ex.Message;
            return null;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
        }
        catch (Exception ex)
        {
            error = "无法访问工作流文件：" + ex.Message;
            return null;
        }

        if (!info.Exists)
        {
            error = "工作流文件不存在。";
            return null;
        }

        if (info.Length > MaxScriptBytes)
        {
            error = "工作流脚本超过 " + MaxScriptBytes + " 字节上限（实际 " + info.Length + " 字节）。";
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            error = "无法读取工作流文件：" + ex.Message;
            return null;
        }

        Script script;
        try
        {
            script = ParseScript(text, fullPath);
        }
        catch (Exception ex)
        {
            error = "工作流脚本解析失败：" + ex.Message;
            return null;
        }

        var unsupported = FindModuleLoadingStatement(script);
        if (unsupported != null)
        {
            error = "工作流脚本包含沙箱不支持的模块语句（import/export … from）：不支持加载外部模块。";
            return null;
        }

        var metaStatement = FindMetaStatement(script, out var initializer);
        var fallbackName = SanitizeName(Path.GetFileNameWithoutExtension(fullPath));
        string? warning = null;
        string? name = null;
        var description = string.Empty;
        string? argumentTemplate = null;
        string? metaJson = null;
        IReadOnlyList<string> parameters = Array.Empty<string>();

        if (initializer == null)
        {
            warning = "工作流缺少 meta 声明，已回退为文件名。";
        }
        else
        {
            string? evaluated = null;
            try
            {
                evaluated = EvaluateMeta(initializer, text);
            }
            catch (Exception ex)
            {
                warning = "meta 求值失败，已回退为文件名：" + ex.Message;
            }

            if (evaluated != null)
            {
                if (TryReadMeta(evaluated, ref name, ref description, ref argumentTemplate, ref parameters, out var metaError))
                {
                    metaJson = evaluated;
                }
                else
                {
                    warning = metaError;
                }
            }
            else if (warning == null)
            {
                warning = "meta 不是可序列化的对象，已回退为文件名。";
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                warning ??= "meta 缺少 name，已回退为文件名。";
            }
        }

        var body = text;
        if (metaStatement != null)
        {
            body = text.Remove(metaStatement.Start, metaStatement.End - metaStatement.Start);
        }

        error = warning;
        return new WorkflowDefinition
        {
            Name = SanitizeName(string.IsNullOrWhiteSpace(name) ? fallbackName : name!),
            Description = description,
            SourcePath = fullPath,
            Script = body,
            PluginName = string.IsNullOrWhiteSpace(pluginName) ? "host" : pluginName!,
            Parameters = parameters,
            ArgumentTemplate = string.IsNullOrWhiteSpace(argumentTemplate) ? "$ARGUMENTS" : argumentTemplate!,
            MetaJson = metaJson
        };
    }

    /// <summary>加载目录下所有工作流（不递归）。结果按名称排序，重名时先加载者生效。</summary>
    /// <param name="directory">工作流目录。</param>
    /// <param name="pluginName">拥有者插件名；null 表示宿主。</param>
    /// <param name="diagnostics">加载过程中产生的诊断；目录不存在时为一条 Info。</param>
    public static IReadOnlyList<WorkflowDefinition> LoadDirectory(
        string directory,
        string? pluginName,
        out IReadOnlyList<PluginDiagnostic> diagnostics)
    {
        var issues = new List<PluginDiagnostic>();
        diagnostics = issues;
        var definitions = new List<WorkflowDefinition>();

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            issues.Add(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Info,
                PluginName = pluginName,
                Path = directory,
                Message = "工作流目录不存在，已跳过。"
            });
            return definitions;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.js", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            issues.Add(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Warning,
                PluginName = pluginName,
                Path = directory,
                Message = "无法枚举工作流目录：" + ex.Message
            });
            return definitions;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var definition = Load(file, pluginName, out var error);
            if (definition == null)
            {
                issues.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Error,
                    PluginName = pluginName,
                    Path = file,
                    Message = error ?? "无法加载工作流。"
                });
                continue;
            }

            if (error != null)
            {
                issues.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    PluginName = pluginName,
                    Path = file,
                    Message = error
                });
            }

            if (!seen.Add(definition.Name))
            {
                issues.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    PluginName = pluginName,
                    Path = file,
                    Message = "工作流名称 '" + definition.Name + "' 重复，先加载者生效。"
                });
                continue;
            }

            definitions.Add(definition);
        }

        definitions.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return definitions;
    }

    /// <summary>把任意字符串规范化为可用的工作流名（小写、连字符）。</summary>
    public static string SanitizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "workflow";
        }

        var builder = new StringBuilder(value!.Length);
        foreach (var character in value.Trim())
        {
            if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9'))
            {
                builder.Append(character);
            }
            else if (character >= 'A' && character <= 'Z')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append('-');
            }
        }

        var normalized = builder.ToString().Trim('-');
        while (normalized.Contains("--"))
        {
            normalized = normalized.Replace("--", "-");
        }

        return normalized.Length == 0 ? "workflow" : normalized;
    }

    private static Script ParseScript(string text, string path)
    {
        var options = new ParserOptions
        {
            AllowReturnOutsideFunction = true,
            AllowImportExportEverywhere = true,
            AllowAwaitOutsideFunction = true,
            EcmaVersion = EcmaVersion.Latest
        };

        return new Parser(options).ParseScript(text, path);
    }

    private static Statement? FindModuleLoadingStatement(Script script)
    {
        foreach (var statement in script.Body)
        {
            if (statement is ImportDeclaration || statement is ExportAllDeclaration)
            {
                return statement;
            }
        }

        return null;
    }

    private static Statement? FindMetaStatement(Script script, out Node? initializer)
    {
        initializer = null;
        foreach (var statement in script.Body)
        {
            VariableDeclaration? declaration = statement switch
            {
                ExportNamedDeclaration exported => exported.Declaration as VariableDeclaration,
                VariableDeclaration direct => direct,
                _ => null
            };

            if (declaration == null)
            {
                continue;
            }

            foreach (var declarator in declaration.Declarations)
            {
                if (declarator.Init != null
                    && declarator.Id is Identifier identifier
                    && string.Equals(identifier.Name, "meta", StringComparison.Ordinal))
                {
                    initializer = declarator.Init;
                    return statement;
                }
            }
        }

        return null;
    }

    /// <summary>用 Jint 求值 <c>meta</c> 初始化表达式，返回其 JSON 文本；不可序列化时返回 null。</summary>
    private static string? EvaluateMeta(Node initializer, string text)
    {
        var source = text.Substring(initializer.Start, initializer.End - initializer.Start);
        var engine = new Engine(options =>
        {
            options.LimitRecursion(64);
            options.TimeoutInterval(TimeSpan.FromSeconds(1));
            options.Host.StringCompilationAllowed = false;
        });

        // 括号不可省：以 `{` 开头的脚本会被解析成块语句而不是对象字面量。
        var value = engine.Evaluate("(" + source + "\n)");
        var serialized = new JsJsonSerializer(engine).Serialize(value);
        return serialized is JsString json ? json.ToString() : null;
    }

    private static bool TryReadMeta(
        string metaJson,
        ref string? name,
        ref string description,
        ref string? argumentTemplate,
        ref IReadOnlyList<string> parameters,
        out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(metaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "meta 必须是对象，已回退为文件名。";
                return false;
            }

            var root = document.RootElement;
            if (root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                name = nameElement.GetString();
            }

            if (root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
            {
                description = descriptionElement.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.String)
            {
                argumentTemplate = argumentsElement.GetString();
            }

            if (root.TryGetProperty("parameters", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Array)
            {
                var names = new List<string>();
                foreach (var item in parametersElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var parameter = item.GetString();
                        if (!string.IsNullOrWhiteSpace(parameter))
                        {
                            names.Add(parameter!);
                        }
                    }
                }

                parameters = names;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = "meta 不是合法 JSON，已回退为文件名：" + ex.Message;
            return false;
        }
    }
}
