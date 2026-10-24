using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Acornima;
using Acornima.Ast;
using AiTools.Abstractions;
using Jint;
using Jint.Native;
using Jint.Runtime;
using JsJsonSerializer = Jint.Native.Json.JsonSerializer;

namespace AiTools.Extensions;

/// <summary>内置的 <c>codemode</c> 工具：在一次性 JavaScript 沙箱中执行脚本，并经事件链调用其它工具。</summary>
public static class CodemodeTool
{
    /// <summary>工具名。</summary>
    public const string Name = "codemode";

    /// <summary>单次脚本允许的最大嵌套调用数。</summary>
    public const int MaxNestedCalls = 64;

    /// <summary>嵌套调用记录保留参数的最大 UTF-8 字节数。</summary>
    public const int MaxArgumentBytes = 8192;

    /// <summary>嵌套调用结果记入完整性判定的最大 UTF-8 字节数。</summary>
    public const int MaxResultBytes = 32768;

    /// <summary>单次脚本运行的默认及最大时长。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private const int MaxRecursionDepth = 512;

    private const string Prelude = @"
(function () {
  function unwrap(raw) {
    var envelope = JSON.parse(raw);
    if (!envelope.ok) {
      var error = new Error(envelope.error || 'codemode call failed');
      error.name = envelope.name || 'CodemodeError';
      if (envelope.failedCall !== undefined && envelope.failedCall !== null) {
        error.failedCall = envelope.failedCall;
      }
      throw error;
    }
    return envelope.value;
  }

  globalThis.tools = JSON.parse(globalThis.__codemode_tools_json);
  globalThis.executeTool = async function (name, args) {
    if (name === undefined || name === null) { throw new TypeError('executeTool(name, args) requires a tool name'); }
    var payload = (args === undefined || args === null) ? '{}' : (typeof args === 'string' ? args : JSON.stringify(args));
    if (payload === undefined) { payload = '{}'; }
    return unwrap(await globalThis.__codemode_execute(String(name), payload));
  };
  globalThis.describeNamespace = function (name) {
    if (name === undefined || name === null) { return ''; }
    return unwrap(globalThis.__codemode_describe_namespace(String(name)));
  };
  globalThis.log = function (message) {
    try {
      var text;
      if (message === undefined) { text = 'undefined'; }
      else if (message === null) { text = 'null'; }
      else if (typeof message === 'object') { text = JSON.stringify(message); }
      else { text = String(message); }
      globalThis.__codemode_log(text);
    } catch (_) { }
  };
})();
";

    /// <summary>构建工具定义。</summary>
    /// <param name="runtime">所属运行时。</param>
    /// <param name="reachesTools">本次调用可达范围的上限；null 时按脚本文本推导。</param>
    /// <param name="name">工具名；默认为 <c>codemode</c>。</param>
    public static ExtensionTool Create(ExtensionRuntime runtime, ToolReach? reachesTools = null, string name = Name)
    {
        if (runtime == null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A tool name is required.", nameof(name));
        }

        return new ExtensionTool
        {
            Definition = ToolDefinition.FromJsonSchema(
                name,
                "Runs a JavaScript script. The final expression or top-level return value becomes the result.",
                """
                {
                  "type": "object",
                  "properties": {
                    "script": {
                      "type": "string",
                      "description": "JavaScript to execute. Its final expression or top-level return value becomes the result. Use executeTool(name, args) to call tools."
                    },
                    "timeoutMs": {
                      "type": "integer",
                      "minimum": 1,
                      "description": "Optional execution timeout in milliseconds, capped at 60000."
                    }
                  },
                  "required": ["script"]
                }
                """),
            ExtensionName = ExtensionRuntime.RuntimeOwner,
            Exposure = ToolExposure.ModelOnly,
            ReachesTools = reachesTools,
            Execute = (call, context, token) => ExecuteAsync(runtime, name, reachesTools, call, context, token)
        };
    }

    /// <summary>从 codemode 可调用工具中按脚本文本推导本次调用范围。</summary>
    /// <param name="script">JavaScript 正文。</param>
    /// <param name="callable">当前可调用工具。</param>
    internal static ToolReach DeriveReach(string script, IReadOnlyList<ExtensionTool> callable)
        => DeriveReach(script, callable, namespaceResolver: null);

    private static ToolReach DeriveReach(
        string script,
        IReadOnlyList<ExtensionTool> callable,
        Func<string, ToolNamespace?>? namespaceResolver)
    {
        var tokens = ReadTokens(script ?? string.Empty);
        var identifiers = new HashSet<string>(tokens.Where(token => token.IsIdentifier).Select(token => token.Value), StringComparer.OrdinalIgnoreCase);
        var literals = new HashSet<string>(tokens.Where(token => !token.IsIdentifier).Select(token => token.Value), StringComparer.OrdinalIgnoreCase);

        if (identifiers.Contains("tools") || identifiers.Contains(ToolSearchTool.Name) || literals.Contains(ToolSearchTool.Name))
        {
            return ToolReach.All();
        }

        var namespaceAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in callable)
        {
            if (string.IsNullOrWhiteSpace(tool.Namespace))
            {
                continue;
            }

            namespaceAliases.Add(tool.Namespace!);
            var declared = namespaceResolver?.Invoke(tool.Namespace!);
            if (declared != null)
            {
                namespaceAliases.Add(declared.Name);
                foreach (var alias in declared.Aliases)
                {
                    namespaceAliases.Add(alias);
                }
            }
        }

        for (var index = 0; index + 2 < tokens.Count; index++)
        {
            if (!tokens[index].IsIdentifier || !namespaceAliases.Contains(tokens[index].Value) || tokens[index + 1].Value != ".")
            {
                continue;
            }

            if (tokens[index + 2].IsIdentifier && IsListingWord(tokens[index + 2].Value))
            {
                return ToolReach.All();
            }
        }

        var words = new List<string>();
        foreach (var tool in callable)
        {
            if (MatchesScriptValue(tokens, tool.Definition.Name))
            {
                words.Add(tool.Definition.Name);
            }

            if (!string.IsNullOrWhiteSpace(tool.Namespace) && MatchesScriptValue(tokens, tool.Namespace!))
            {
                words.Add(tool.Namespace!);
            }

            var registered = string.IsNullOrWhiteSpace(tool.Namespace) ? null : namespaceResolver?.Invoke(tool.Namespace!);
            if (registered != null &&
                (MatchesScriptValue(tokens, registered.Name) || registered.Aliases.Any(alias => MatchesScriptValue(tokens, alias))))
            {
                words.Add(tool.Namespace!);
            }
        }

        return ToolReach.Words(words.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>注册 codemode 的动态模型描述。</summary>
    /// <param name="runtime">所属运行时。</param>
    /// <param name="api">运行时注册 API。</param>
    public static void Register(ExtensionRuntime runtime, IExtensionApi api)
    {
        if (runtime == null)
        {
            throw new ArgumentNullException(nameof(runtime));
        }

        if (api == null)
        {
            throw new ArgumentNullException(nameof(api));
        }

        api.RegisterLoadoutPreparer(loadout =>
        {
            var names = loadout.All
                .Where(tool => tool.Exposure == ToolExposure.Codemode)
                .OrderBy(tool => tool.Namespace ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(tool => tool.Definition.Name, StringComparer.Ordinal)
                .Select(tool => DisplayName(runtime, tool))
                .ToArray();
            loadout.Descriptions[Name] = "Runs a JavaScript script that can call these tools: " +
                                         (names.Length == 0 ? "none" : string.Join(", ", names)) + ".";
            return loadout;
        });
    }

    private static async Task<string> ExecuteAsync(
        ExtensionRuntime runtime,
        string name,
        ToolReach? configuredReach,
        ToolCall call,
        ToolContext toolContext,
        CancellationToken cancellationToken)
    {
        string script;
        int? requestedTimeout = null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("script", out var scriptElement) ||
                scriptElement.ValueKind != JsonValueKind.String)
            {
                return Failure("script must be a string", failedCall: null, new RunState());
            }

            script = scriptElement.GetString() ?? string.Empty;
            if (document.RootElement.TryGetProperty("timeoutMs", out var timeoutElement))
            {
                if (timeoutElement.ValueKind != JsonValueKind.Number || !timeoutElement.TryGetInt32(out var milliseconds))
                {
                    return Failure("timeoutMs must be an integer", failedCall: null, new RunState());
                }

                requestedTimeout = milliseconds;
            }
        }
        catch (JsonException exception)
        {
            return Failure("Invalid JSON arguments: " + exception.Message, failedCall: null, new RunState());
        }

        var timeout = requestedTimeout.HasValue
            ? TimeSpan.FromMilliseconds(Math.Max(1, Math.Min((int)DefaultTimeout.TotalMilliseconds, requestedTimeout.Value)))
            : DefaultTimeout;
        var state = new RunState();
        var callable = runtime.BuildLoadout().Callable;
        var parentReach = string.IsNullOrWhiteSpace(toolContext.ParentToolName)
            ? null
            : runtime.ToolReachOf(toolContext.ParentToolName);
        var derivedReach = DeriveReach(script, callable, runtime.FindNamespace);
        var commandContext = toolContext.CallContext ?? new CommandContext
        {
            Runtime = runtime,
            State = runtime.State,
            SessionId = toolContext.SessionId,
            Ui = toolContext.Ui,
            EntryStore = toolContext.EntryStore,
            WorkingDirectory = Environment.CurrentDirectory
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var runToken = timeoutSource.Token;
        var stopwatch = Stopwatch.StartNew();
        var engine = new Engine(options =>
        {
            options.Culture = CultureInfo.InvariantCulture;
            options.LimitRecursion(MaxRecursionDepth);
            options.Host.StringCompilationAllowed = false;
            options.CancellationToken(runToken);
            options.TimeoutInterval(timeout);
            options.Constraints.PromiseTimeout = timeout;
        });

        try
        {
            var listedTools = callable
                .Where(tool => IsReachable(runtime, tool, derivedReach, configuredReach, parentReach))
                .Select(tool => new
                {
                    name = tool.Definition.Name,
                    @namespace = tool.Namespace,
                    description = tool.Definition.Description ?? string.Empty
                })
                .ToArray();
            engine.SetValue("__codemode_tools_json", JsonSerializer.Serialize(listedTools));
            engine.SetValue("__codemode_execute", new Func<string, string, Task<string>>(
                (toolName, argumentsJson) => ExecuteNestedAsync(
                    runtime, name, toolName, argumentsJson, callable, derivedReach, configuredReach, parentReach,
                    commandContext, state, runToken)));
            engine.SetValue("__codemode_describe_namespace", new Func<string, string>(namespaceName =>
                EnvelopeOk(runtime.FindNamespace(namespaceName)?.Instructions ?? string.Empty)));
            engine.SetValue("__codemode_log", new Action<string>(_ => { }));

            var source = BuildSource(script, name);
            var value = await engine.EvaluateAsync(Prelude + source, "codemode://" + name + ".js", runToken)
                .ConfigureAwait(false);

            if (state.AbortError != null)
            {
                return Failure(state.AbortError, state.FailedCall, state);
            }

            var result = SerializeResult(engine, value);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                result,
                nestedCalls = state.Records.Select(record => record.ToDictionary()).ToArray(),
                complete = state.Complete
            });
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var error = state.AbortError ?? Describe(exception, timeout);
            return Failure(error, state.FailedCall, state);
        }
    }

    private static async Task<string> ExecuteNestedAsync(
        ExtensionRuntime runtime,
        string parentName,
        string toolName,
        string? argumentsJson,
        IReadOnlyList<ExtensionTool> callable,
        ToolReach derivedReach,
        ToolReach? configuredReach,
        ToolReach? parentReach,
        CommandContext commandContext,
        RunState state,
        CancellationToken cancellationToken)
    {
        var callIndex = state.CallCount;
        if (state.CallCount >= MaxNestedCalls)
        {
            state.FailedCall = callIndex;
            state.AbortError = "Maximum nested calls (" + MaxNestedCalls.ToString(CultureInfo.InvariantCulture) +
                               ") exceeded at call index " + callIndex.ToString(CultureInfo.InvariantCulture) + " for '" + toolName + "'.";
            return EnvelopeFail(state.AbortError, callIndex);
        }

        state.CallCount++;
        var payload = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson!;
        var callTimer = Stopwatch.StartNew();
        var target = callable.FirstOrDefault(tool => string.Equals(tool.Definition.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (target == null || !IsReachable(runtime, target, derivedReach, configuredReach, parentReach))
        {
            var message = target == null
                ? "Unknown or non-callable tool: " + toolName
                : "Tool '" + toolName + "' is outside the reach of '" + parentName + "'.";
            AddRecord(state, toolName, payload, "error", callTimer.Elapsed.TotalMilliseconds, message, resultText: null);
            state.FailedCall = callIndex;
            return EnvelopeFail(message, callIndex);
        }

        ExtensionToolResult result;
        try
        {
            result = await runtime.ExecuteToolAsync(
                    toolName,
                    payload,
                    commandContext,
                    parentToolName: parentName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = exception.Message;
            callTimer.Stop();
            AddRecord(state, toolName, payload, "error", callTimer.Elapsed.TotalMilliseconds, message, resultText: null);
            state.FailedCall = callIndex;
            return EnvelopeFail(message, callIndex);
        }

        callTimer.Stop();
        var errorText = result.IsError ? result.Text : FindErrorEnvelope(result.Text);
        AddRecord(state, toolName, payload, errorText == null ? "success" : "error",
            callTimer.Elapsed.TotalMilliseconds, errorText, result.Text);
        if (errorText != null)
        {
            state.FailedCall = callIndex;
            return EnvelopeFail(errorText, callIndex);
        }

        return EnvelopeOk(result.Text);
    }

    private static void AddRecord(
        RunState state,
        string name,
        string argumentsJson,
        string status,
        double durationMs,
        string? error,
        string? resultText)
    {
        if (Encoding.UTF8.GetByteCount(argumentsJson) > MaxArgumentBytes)
        {
            state.Complete = false;
        }

        if (resultText != null && Encoding.UTF8.GetByteCount(resultText) > MaxResultBytes)
        {
            state.Complete = false;
        }
        if (error != null && Encoding.UTF8.GetByteCount(error) > MaxResultBytes)
        {
            state.Complete = false;
            error = null;
        }

        object? arguments = null;
        var includeArguments = Encoding.UTF8.GetByteCount(argumentsJson) <= MaxArgumentBytes;
        if (includeArguments)
        {
            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                arguments = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                arguments = argumentsJson;
            }
        }

        state.Records.Add(new NestedCallRecord(name, arguments, includeArguments, status, durationMs, error));
    }

    private static bool IsReachable(
        ExtensionRuntime runtime,
        ExtensionTool tool,
        ToolReach derivedReach,
        ToolReach? configuredReach,
        ToolReach? parentReach)
        => runtime.Allows(derivedReach, tool) &&
           (configuredReach == null || runtime.Allows(configuredReach, tool)) &&
           (parentReach == null || runtime.Allows(parentReach, tool));

    private static string? FindErrorEnvelope(string resultText)
    {
        try
        {
            using var document = JsonDocument.Parse(resultText);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                return root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : "Nested tool returned an error envelope.";
            }

            if (root.TryGetProperty("error", out var envelopeError))
            {
                return envelopeError.ValueKind == JsonValueKind.String
                    ? envelopeError.GetString()
                    : envelopeError.ToString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string BuildSource(string body, string name)
    {
        var parser = new Parser(new ParserOptions
        {
            AllowReturnOutsideFunction = true,
            AllowAwaitOutsideFunction = true,
            AllowImportExportEverywhere = true,
            EcmaVersion = EcmaVersion.Latest
        });
        var script = parser.ParseScript(body, "codemode://" + name + ".js");
        var builder = new StringBuilder(body.Length + 64);
        builder.Append("(async () => {\n");
        var cursor = 0;
        var statements = script.Body;
        var lastIndex = statements.Count - 1;
        for (var index = 0; index <= lastIndex; index++)
        {
            var statement = statements[index];
            builder.Append(body, cursor, statement.Start - cursor);

            if (statement is ExportNamedDeclaration named && named.Declaration != null)
            {
                builder.Append(body, named.Declaration.Start, statement.End - named.Declaration.Start);
            }
            else if (statement is ExportDefaultDeclaration defaultExport && defaultExport.Declaration != null)
            {
                builder.Append("return (");
                builder.Append(body, defaultExport.Declaration.Start, defaultExport.Declaration.End - defaultExport.Declaration.Start);
                builder.Append(");");
            }
            else if (statement is ImportDeclaration || statement is ExportAllDeclaration)
            {
                // 沙箱不提供模块加载器。
            }
            else if (index == lastIndex && statement is ExpressionStatement expression)
            {
                builder.Append("return (");
                builder.Append(body, expression.Expression.Start, expression.Expression.End - expression.Expression.Start);
                builder.Append(");");
            }
            else
            {
                builder.Append(body, statement.Start, statement.End - statement.Start);
            }

            cursor = statement.End;
        }

        builder.Append(body, cursor, body.Length - cursor);
        builder.Append("\n})()");
        return builder.ToString();
    }

    private static JsonElement SerializeResult(Engine engine, JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            using var nullDocument = JsonDocument.Parse("null");
            return nullDocument.RootElement.Clone();
        }

        var serialized = new JsJsonSerializer(engine).Serialize(value);
        if (serialized is JsString text)
        {
            using var document = JsonDocument.Parse(text.ToString());
            return document.RootElement.Clone();
        }

        using var emptyDocument = JsonDocument.Parse("null");
        return emptyDocument.RootElement.Clone();
    }

    private static List<ScriptToken> ReadTokens(string script)
    {
        var tokens = new List<ScriptToken>();
        for (var index = 0; index < script.Length;)
        {
            var character = script[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '/' && index + 1 < script.Length && script[index + 1] == '/')
            {
                index += 2;
                while (index < script.Length && script[index] != '\n') index++;
                continue;
            }

            if (character == '/' && index + 1 < script.Length && script[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < script.Length && !(script[index] == '*' && script[index + 1] == '/')) index++;
                index = Math.Min(script.Length, index + 2);
                continue;
            }

            if (character == '\'' || character == '"')
            {
                var quote = character;
                index++;
                var literal = new StringBuilder();
                while (index < script.Length && script[index] != quote)
                {
                    if (script[index] == '\\' && index + 1 < script.Length)
                    {
                        index++;
                        literal.Append(Unescape(script[index++]));
                    }
                    else
                    {
                        literal.Append(script[index++]);
                    }
                }

                if (index < script.Length) index++;
                tokens.Add(new ScriptToken(literal.ToString(), isIdentifier: false));
                continue;
            }

            if (IsIdentifierStart(character))
            {
                var start = index++;
                while (index < script.Length && IsIdentifierPart(script[index])) index++;
                tokens.Add(new ScriptToken(script.Substring(start, index - start), isIdentifier: true));
                continue;
            }

            tokens.Add(new ScriptToken(character.ToString(), isIdentifier: false));
            index++;
        }

        return tokens;
    }

    private static char Unescape(char character)
        => character switch
        {
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            'b' => '\b',
            'f' => '\f',
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            _ => character
        };

    private static bool IsIdentifierStart(char value)
        => value == '_' || value == '$' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value)
        => IsIdentifierStart(value) || char.IsDigit(value);

    private static bool MatchesScriptValue(IEnumerable<ScriptToken> tokens, string value)
    {
        var canonical = ToolNames.Canonicalize(value);
        return tokens.Any(token => ToolNames.Canonicalize(token.Value) == canonical);
    }

    private static bool IsListingWord(string value)
        => string.Equals(value, "list", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "search", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "find", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "tools", StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(ExtensionRuntime runtime, ExtensionTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Namespace))
        {
            return tool.Definition.Name;
        }

        var namespaceName = runtime.FindNamespace(tool.Namespace)?.Name ?? tool.Namespace!;
        var prefix = namespaceName + ".";
        return tool.Definition.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? tool.Definition.Name
            : prefix + tool.Definition.Name;
    }

    private static string Failure(string error, int? failedCall, RunState state)
        => JsonSerializer.Serialize(new
        {
            ok = false,
            error,
            failedCall,
            nestedCalls = state.Records.Select(record => record.ToDictionary()).ToArray(),
            complete = state.Complete
        });

    private static string EnvelopeOk(string? value)
        => JsonSerializer.Serialize(new { ok = true, value = value ?? string.Empty });

    private static string EnvelopeFail(string message, int? failedCall = null)
        => JsonSerializer.Serialize(new { ok = false, error = message, failedCall });

    private static string Describe(Exception exception, TimeSpan timeout)
    {
        switch (exception)
        {
            case TimeoutException:
            case ExecutionCanceledException:
            case OperationCanceledException:
                return "JavaScript script timed out after " + timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms.";
            case PromiseRejectedException rejected:
                return "JavaScript script failed: " + DescribeRejected(rejected);
            case JavaScriptException javascript:
                return "JavaScript script failed: " + javascript.Message;
            case RecursionDepthOverflowException:
                return "JavaScript script recursion depth exceeded; execution was stopped.";
            case StatementsCountOverflowException:
                return "JavaScript script exceeded the statement limit; execution was stopped.";
            default:
                return "JavaScript script failed: " + exception.Message;
        }
    }

    private static string DescribeRejected(PromiseRejectedException exception)
    {
        var value = exception.RejectedValue;
        if (value != null && value.IsObject())
        {
            var target = value.AsObject();
            var name = target.Get("name");
            var message = target.Get("message");
            var text = message.IsUndefined() ? target.ToString() : message.ToString();
            var prefix = name.IsUndefined() ? "Error" : name.ToString();
            return prefix + ": " + text;
        }

        return value?.ToString() ?? exception.Message;
    }

    private sealed class RunState
    {
        public int CallCount { get; set; }
        public int? FailedCall { get; set; }
        public string? AbortError { get; set; }
        public bool Complete { get; set; } = true;
        public List<NestedCallRecord> Records { get; } = new();
    }

    private sealed class NestedCallRecord
    {
        public NestedCallRecord(string name, object? arguments, bool includeArguments, string status, double durationMs, string? error)
        {
            Name = name;
            Arguments = arguments;
            IncludeArguments = includeArguments;
            Status = status;
            DurationMs = durationMs;
            Error = error;
        }

        public string Name { get; }
        public object? Arguments { get; }
        public bool IncludeArguments { get; }
        public string Status { get; }
        public double DurationMs { get; }
        public string? Error { get; }

        public Dictionary<string, object?> ToDictionary()
        {
            var result = new Dictionary<string, object?>
            {
                ["name"] = Name,
                ["status"] = Status,
                ["durationMs"] = DurationMs
            };
            if (IncludeArguments)
            {
                result["arguments"] = Arguments;
            }

            if (Error != null)
            {
                result["error"] = Error;
            }

            return result;
        }
    }

    private sealed class ScriptToken
    {
        public ScriptToken(string value, bool isIdentifier)
        {
            Value = value;
            IsIdentifier = isIdentifier;
        }

        public string Value { get; }
        public bool IsIdentifier { get; }
    }
}
