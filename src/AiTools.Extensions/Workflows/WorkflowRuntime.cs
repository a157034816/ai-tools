using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
using JsJsonParser = Jint.Native.Json.JsonParser;
using JsJsonSerializer = Jint.Native.Json.JsonSerializer;
using StjJson = System.Text.Json.JsonSerializer;

namespace AiTools.Extensions.Workflows;

/// <summary>执行工作流所需的宿主能力。所有能力都可缺席：缺席时脚本里的对应全局会在调用时失败。</summary>
public sealed class WorkflowRuntimeOptions
{
    /// <summary>获取或设置模型客户端（<c>agent</c> 的后端）。为 null 时 <c>agent</c> 调用失败。</summary>
    public IChatClient? Chat { get; set; }

    /// <summary>获取或设置模型名；为空时由客户端选择默认模型。</summary>
    public string? Model { get; set; }

    /// <summary>获取或设置单次运行的总超时，同时也是 JS 引擎的执行上限（默认 60 秒）。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>获取或设置 <c>agent</c> 与 <c>tool</c> 调用总次数上限（默认 64；小于等于 0 表示不限制）。</summary>
    public int MaxSteps { get; set; } = 64;

    /// <summary>获取或设置 <c>tool(name, args)</c> 的后端：参数为工具名与 JSON 参数，返回结果文本。</summary>
    public Func<string, string, Task<string>>? ToolInvoker { get; set; }

    /// <summary>
    /// 获取或设置 <c>tool</c> 的权限策略。为 null 时使用 <see cref="PermissionPolicy.Default"/>：
    /// 它不含任何规则，因此每次调用都要求确认；未设置 <see cref="Confirm"/> 时一律拒绝（fail closed）。
    /// </summary>
    public PermissionPolicy? Policy { get; set; }

    /// <summary>
    /// 获取或设置权限策略判定为 <see cref="PermissionDecision.Ask"/> 时的确认回调，参数为提示文本。
    /// 为 null 时 <see cref="PermissionDecision.Ask"/> 按拒绝处理。
    /// </summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>获取或设置 <c>readSkill(name)</c> 的后端；返回 null 表示 skill 不存在。</summary>
    public Func<string, string?>? ReadSkillFile { get; set; }

    /// <summary>获取或设置日志回调；<c>log</c> 与 <c>console.*</c> 都走这里。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>获取或设置 <c>readFile</c> 的沙箱根目录；为空时使用当前目录。</summary>
    public string? WorkingDirectory { get; set; }
}

/// <summary>一次工作流运行的结果。</summary>
public sealed class WorkflowRunResult
{
    /// <summary>获取运行是否成功。脚本抛错、超时或超出步骤上限时为 false。</summary>
    public bool Ok { get; init; }

    /// <summary>获取脚本返回值的 JSON 文本；脚本没有返回值时为 null。</summary>
    public string? Output { get; init; }

    /// <summary>获取失败原因；成功时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>获取运行耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>获取 <c>agent</c> 调用次数（含失败调用）。</summary>
    public int AgentCalls { get; init; }

    /// <summary>获取 <c>tool</c> 调用次数（含失败调用）。</summary>
    public int ToolCalls { get; init; }
}

/// <summary>
/// 在一次性 JS 沙箱里执行工作流。每次运行都新建 <see cref="Engine"/>，全局状态不跨运行泄漏。
/// </summary>
/// <remarks>
/// <para>暴露给脚本的全局（刻意保持很小）：</para>
/// <list type="bullet">
/// <item><c>agent(prompt, options?)</c>：调用一次模型并返回助手文本。<c>options.schema</c> 存在时要求 JSON 输出并校验，返回解析后的对象；<c>options.label</c> 只用于日志。模型失败时抛出可在脚本里 catch 的错误。</item>
/// <item><c>pipeline(items, fn)</c>：按顺序对每项执行 <c>await fn(item, index)</c>，丢弃 null/undefined，返回结果数组（串行，无并发）。</item>
/// <item><c>tool(name, args)</c>：调用宿主注册的扩展工具并返回结果文本；未配置后端时抛错。</item>
/// <item><c>readFile(path)</c>：同步读取工作目录内的文本文件，缺失时返回空串；拒绝含 <c>..</c> 的路径与解析后落到工作目录之外的文件。</item>
/// <item><c>readSkill(name)</c>：读取 skill 正文；未配置或不存在时返回空串。</item>
/// <item><c>log(message)</c> 与 <c>console.log/info/warn/error</c>：写入 <see cref="WorkflowRuntimeOptions.Log"/>，不写 stdout。</item>
/// <item><c>meta</c>：工作流元数据对象（name、description、parameters、plugin、source）。</item>
/// <item><c>arguments</c>：调用者参数文本（适配器已渲染 <c>$ARGUMENTS</c>/<c>$1</c> 占位符）。</item>
/// </list>
/// <para>
/// 沙箱边界：禁用 <c>eval</c>/<c>Function</c> 动态编译、限制递归深度、总时长与步数。
/// 它不是安全边界——脚本仍可通过 <c>tool</c>/<c>readFile</c> 触达宿主能力。
/// </para>
/// </remarks>
public sealed class WorkflowRuntime
{
    private const int MaxRecursionDepth = 512;

    /// <summary>解析链接时允许的最大跳数；超过即放弃解析并按字面路径处理。</summary>
    private const int MaxLinkHops = 64;

    private const string Prelude = @"
globalThis.__wf_unwrap = function (raw) {
  var envelope = JSON.parse(raw);
  if (!envelope.ok) {
    var error = new Error(envelope.error || 'workflow call failed');
    error.name = envelope.name || 'WorkflowError';
    throw error;
  }
  return envelope.value;
};

globalThis.agent = async function (prompt, options) {
  if (prompt === undefined || prompt === null) { throw new TypeError('agent(prompt) requires a prompt'); }
  var payload = (options === undefined || options === null) ? null : JSON.stringify(options);
  var wantsSchema = options !== undefined && options !== null && options.schema !== undefined && options.schema !== null;
  var value = globalThis.__wf_unwrap(await globalThis.__wf_agent(String(prompt), payload));
  return wantsSchema ? JSON.parse(value) : value;
};

globalThis.pipeline = async function (items, fn) {
  if (items === undefined || items === null) { throw new TypeError('pipeline(items, fn) requires items'); }
  if (typeof fn !== 'function') { throw new TypeError('pipeline(items, fn) requires a function'); }
  var list = Array.prototype.slice.call(items);
  var results = [];
  for (var index = 0; index < list.length; index++) {
    var value = await fn(list[index], index);
    if (value !== null && value !== undefined) { results.push(value); }
  }
  return results;
};

globalThis.tool = async function (name, args) {
  if (name === undefined || name === null) { throw new TypeError('tool(name, args) requires a tool name'); }
  var payload = (args === undefined || args === null) ? '{}' : (typeof args === 'string' ? args : JSON.stringify(args));
  return globalThis.__wf_unwrap(await globalThis.__wf_tool(String(name), payload));
};

globalThis.readFile = function (path) {
  if (path === undefined || path === null) { throw new TypeError('readFile(path) requires a path'); }
  return globalThis.__wf_unwrap(globalThis.__wf_read_file(String(path)));
};

globalThis.readSkill = function (name) {
  if (name === undefined || name === null) { return ''; }
  return globalThis.__wf_unwrap(globalThis.__wf_read_skill(String(name)));
};

globalThis.log = function (message) {
  var text;
  if (message === undefined) { text = 'undefined'; }
  else if (message === null) { text = 'null'; }
  else if (typeof message === 'object') { text = JSON.stringify(message); }
  else { text = String(message); }
  globalThis.__wf_log(text);
};

globalThis.console = {
  log: function (message) { globalThis.log(message); },
  info: function (message) { globalThis.log(message); },
  debug: function (message) { globalThis.log(message); },
  warn: function (message) { globalThis.log(message); },
  error: function (message) { globalThis.log(message); }
};
";

    private readonly WorkflowRuntimeOptions _options;

    /// <summary>创建运行时。</summary>
    /// <param name="options">宿主能力与限制；不可为 null。</param>
    public WorkflowRuntime(WorkflowRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>执行一个工作流。脚本错误以 <see cref="WorkflowRunResult.Ok"/> 为 false 返回，不抛出。</summary>
    /// <param name="definition">工作流定义。</param>
    /// <param name="arguments">调用参数文本，脚本里可读作 <c>arguments</c>。</param>
    /// <param name="cancellationToken">取消令牌；宿主取消时抛出 <see cref="OperationCanceledException"/>。</param>
    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition definition,
        string? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        var state = new RunState(arguments ?? string.Empty);
        var stopwatch = Stopwatch.StartNew();
        var timeout = _options.Timeout;
        var timed = timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timed)
        {
            cts.CancelAfter(timeout);
        }

        // 引擎每次运行都新建：脚本之间不共享任何全局状态。
        var engine = new Engine(options =>
        {
            options.Culture = CultureInfo.InvariantCulture;
            options.LimitRecursion(MaxRecursionDepth);
            options.Host.StringCompilationAllowed = false;
            // 宿主取消也要能打断纯同步的无限循环：约束在每个语句边界检查令牌。
            options.CancellationToken(cts.Token);
            if (timed)
            {
                // 单次执行的最长时间（含等待 .NET 任务）；`while(true)` 因此抛出 TimeoutException。
                options.TimeoutInterval(timeout);
                options.Constraints.PromiseTimeout = timeout;
            }
        });

        try
        {
            RegisterGlobals(engine, definition, state, cts.Token);

            var source = BuildSource(definition);
            var value = await engine.EvaluateAsync(source, "workflow://" + definition.Name + ".js", cts.Token)
                .ConfigureAwait(false);

            if (state.AbortError != null)
            {
                return Failed(state.AbortError, stopwatch, state);
            }

            return new WorkflowRunResult
            {
                Ok = true,
                Output = SerializeResult(engine, value),
                Duration = stopwatch.Elapsed,
                AgentCalls = state.AgentCalls,
                ToolCalls = state.ToolCalls
            };
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var message = state.AbortError ?? Describe(ex, timeout);
            return Failed(message, stopwatch, state);
        }
    }

    /// <summary>把脚本正文包装成 async 箭头函数，并把顶层表达式/return 作为结果。</summary>
    private static string BuildSource(WorkflowDefinition definition)
    {
        var body = definition.Script ?? string.Empty;
        var script = ParseBody(body, definition.SourcePath);
        var builder = new StringBuilder(body.Length + 64);
        builder.Append("(async () => {\n");

        var cursor = 0;
        var statements = script.Body;
        var lastIndex = statements.Count - 1;
        for (var index = 0; index <= lastIndex; index++)
        {
            var statement = statements[index];
            builder.Append(body, cursor, statement.Start - cursor);

            if (statement is ExportNamedDeclaration exported)
            {
                // `export const x = …` 在函数体内非法：只保留声明本身。
                if (exported.Declaration != null)
                {
                    builder.Append(body, exported.Declaration.Start, statement.End - exported.Declaration.Start);
                }
            }
            else if (statement is ExportDefaultDeclaration defaultExport && defaultExport.Declaration != null)
            {
                // `export default expr` 等价于返回该值。
                builder.Append("return (");
                builder.Append(body, defaultExport.Declaration.Start, defaultExport.Declaration.End - defaultExport.Declaration.Start);
                builder.Append(");");
            }
            else if (statement is ImportDeclaration || statement is ExportAllDeclaration)
            {
                // 沙箱没有模块加载；加载器已拒绝这类脚本，这里只是兜底。
            }
            else if (index == lastIndex && statement is ExpressionStatement expression)
            {
                // 顶层最后一个表达式就是工作流的返回值。
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

    private void RegisterGlobals(
        Engine engine,
        WorkflowDefinition definition,
        RunState state,
        CancellationToken runToken)
    {
        engine.SetValue("__wf_agent", new Func<string, string?, Task<string>>(
            (prompt, optionsJson) => InvokeAgentAsync(prompt, optionsJson, state, runToken)));

        engine.SetValue("__wf_tool", new Func<string, string?, Task<string>>(
            (name, argumentsJson) => InvokeToolAsync(name, argumentsJson, state, runToken)));

        engine.SetValue("__wf_read_file", new Func<string, string>(path => ReadFile(path)));

        engine.SetValue("__wf_read_skill", new Func<string, string>(
            name => EnvelopeOk(_options.ReadSkillFile?.Invoke(name) ?? string.Empty)));

        engine.SetValue("__wf_log", new Action<string>(message => _options.Log?.Invoke(message)));

        engine.SetValue("meta", new JsJsonParser(engine).Parse(BuildMetaJson(definition)));
        engine.SetValue("arguments", state.Arguments);
        engine.Execute(Prelude);
    }

    private async Task<string> InvokeAgentAsync(
        string prompt,
        string? optionsJson,
        RunState state,
        CancellationToken token)
    {
        if (!TryConsumeStep(state))
        {
            throw new InvalidOperationException(state.AbortError);
        }

        state.AgentCalls++;
        string? label = null;
        string? schemaName = null;
        JsonElement schema = default;
        var hasSchema = false;

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson!);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String)
                {
                    label = labelElement.GetString();
                }

                if (root.TryGetProperty("schema", out var schemaElement) && schemaElement.ValueKind == JsonValueKind.Object)
                {
                    schema = schemaElement.Clone();
                    hasSchema = true;
                    schemaName = string.IsNullOrWhiteSpace(label) ? "workflow_step" : label!;
                }
            }
        }
        catch (JsonException ex)
        {
            return EnvelopeFail("agent 的 options 不是合法 JSON：" + ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            _options.Log?.Invoke("agent: " + label);
        }

        var chat = _options.Chat;
        if (chat == null)
        {
            return EnvelopeFail("未配置模型客户端（WorkflowRuntimeOptions.Chat），agent 不可用。");
        }

        var request = new ChatRequest
        {
            Model = _options.Model ?? string.Empty,
            Messages = new[] { ChatMessage.User(prompt) },
            ResponseFormat = hasSchema ? ResponseFormat.JsonSchema(schemaName!, schema) : ResponseFormat.Text
        };

        ChatResponse response;
        try
        {
            response = await chat.CompleteAsync(request, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EnvelopeFail("agent 调用失败：" + ex.Message);
        }

        var text = response.GetText();
        if (!hasSchema)
        {
            return EnvelopeOk(text);
        }

        try
        {
            using var parsed = JsonDocument.Parse(text);
            // 保留模型原始 JSON 文本：脚本端再做 JSON.parse 得到对象。
            return EnvelopeOk(text);
        }
        catch (JsonException)
        {
            return EnvelopeFail("agent 返回的内容不是合法 JSON（要求 schema 输出）。");
        }
    }

    private async Task<string> InvokeToolAsync(string name, string? argumentsJson, RunState state, CancellationToken token)
    {
        if (!TryConsumeStep(state))
        {
            throw new InvalidOperationException(state.AbortError);
        }

        state.ToolCalls++;
        var invoker = _options.ToolInvoker;
        if (invoker == null)
        {
            return EnvelopeFail("未配置工具调用后端（WorkflowRuntimeOptions.ToolInvoker），tool 不可用。");
        }

        var payload = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson!;

        try
        {
            // 策略判定在 PermissionGate 内完成：Deny 与未获批准的 Ask 都返回错误 JSON 而不触碰后端，
            // 与宿主其它工具调用的约定一致（脚本可直接检查结果里的 error 字段）。
            var result = await PermissionGate.GuardAsync(
                    _options.Policy ?? PermissionPolicy.Default,
                    name,
                    payload,
                    () => invoker(name, payload),
                    _options.Confirm)
                .ConfigureAwait(false);

            return EnvelopeOk(result ?? string.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EnvelopeFail("tool '" + name + "' 调用失败：" + ex.Message);
        }
    }

    private bool TryConsumeStep(RunState state)
    {
        if (state.AbortError != null)
        {
            return false;
        }

        var limit = _options.MaxSteps;
        if (limit <= 0)
        {
            return true;
        }

        if (state.Steps >= limit)
        {
            state.AbortError = "工作流超过最大步骤数 " + limit.ToString(CultureInfo.InvariantCulture) + "（agent + tool 调用总数）。";
            return false;
        }

        state.Steps++;
        return true;
    }

    /// <summary>
    /// 读取沙箱根目录内的文本文件。
    /// </summary>
    /// <remarks>
    /// 两层防护：先按路径段拒绝 <c>..</c>（因此 <c>a..b.txt</c> 这类文件名合法），
    /// 再把两侧路径的链接目标解析成最终物理位置后重新做包含检查。
    /// 只做词法检查不够：根目录内的 junction/symlink 可以指向根之外，
    /// 而 <see cref="Path.GetFullPath(string)"/> 不会跟随链接。
    /// 链接解析依赖 <c>File.ResolveLinkTarget</c>（.NET 6+）；netstandard2.0 目标下只有词法检查，
    /// 该目标下的宿主必须自己保证<see cref="WorkflowRuntimeOptions.WorkingDirectory"/>内没有逃逸链接。
    /// </remarks>
    private string ReadFile(string path)
    {
        var root = string.IsNullOrWhiteSpace(_options.WorkingDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(_options.WorkingDirectory!);

        if (string.IsNullOrWhiteSpace(path))
        {
            return EnvelopeFail("readFile 需要一个相对路径。");
        }

        if (HasParentSegment(path!))
        {
            return EnvelopeFail("readFile 拒绝包含 '..' 路径段的路径：" + path);
        }

        string full;
        try
        {
            full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));
        }
        catch (Exception ex)
        {
            return EnvelopeFail("readFile 路径无效：" + ex.Message);
        }

        if (!IsWithin(root, full))
        {
            return EnvelopeFail("readFile 拒绝工作目录之外的路径：" + path);
        }

        // 词法检查通过后，再解析链接目标：根内的 junction 可以把读取引到根外。
        var resolvedRoot = ResolveLinkTargets(root);
        var resolvedFull = ResolveLinkTargets(full);
        if (!IsWithin(resolvedRoot, resolvedFull))
        {
            return EnvelopeFail("readFile 拒绝经由链接逃逸到工作目录之外的路径：" + path);
        }

        try
        {
            return EnvelopeOk(File.Exists(resolvedFull) ? File.ReadAllText(resolvedFull, Encoding.UTF8) : string.Empty);
        }
        catch (Exception ex)
        {
            return EnvelopeFail("readFile 读取失败：" + ex.Message);
        }
    }

    /// <summary>判断路径是否含有独立的 <c>..</c> 段（而不是名字里恰好出现两个点）。</summary>
    private static bool HasParentSegment(string path)
    {
        var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>判断 <paramref name="candidate"/> 是否等于 <paramref name="root"/> 或位于其下。</summary>
    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 逐段解析路径中的链接（含中间目录的 junction），返回最终的物理路径。
    /// 不存在的段按字面拼接；解析失败时保留原样。
    /// </summary>
    private static string ResolveLinkTargets(string path)
    {
#if NET6_0_OR_GREATER
        try
        {
            var root = Path.GetPathRoot(path) ?? string.Empty;
            if (root.Length == 0)
            {
                return path;
            }

            var segments = path.Substring(root.Length)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            var current = root;
            var hops = 0;
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);

                // 只有真实存在的项才可能有链接目标；每一跳都算一次，用于防止链接环。
                if (++hops > MaxLinkHops)
                {
                    return path;
                }

                try
                {
                    var info = Directory.Exists(current)
                        ? (FileSystemInfo)new DirectoryInfo(current)
                        : new FileInfo(current);

                    if (!info.Exists)
                    {
                        continue;
                    }

                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null && !string.IsNullOrEmpty(target.FullName))
                    {
                        current = target.FullName;
                    }
                }
                catch (IOException)
                {
                    // 链接损坏或不可读：停在词法路径上，后面的包含检查仍会照常执行。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return current;
        }
        catch (Exception)
        {
            return path;
        }
#else
        return path;
#endif
    }

    /// <summary>合成脚本可见的 <c>meta</c>：作者写的字段在前，宿主补充的字段覆盖同名键。</summary>
    private static string BuildMetaJson(WorkflowDefinition definition)
    {
        var host = new Dictionary<string, object?>
        {
            ["name"] = definition.Name,
            ["description"] = definition.Description,
            ["parameters"] = definition.Parameters,
            ["plugin"] = definition.PluginName,
            ["source"] = definition.SourcePath
        };

        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(definition.MetaJson))
        {
            try
            {
                using var document = JsonDocument.Parse(definition.MetaJson!);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        merged[property.Name] = property.Value.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                // 加载器已校验过；这里只在定义被手工构造时兜底。
            }
        }

        foreach (var pair in host)
        {
            merged[pair.Key] = pair.Value;
        }

        return StjJson.Serialize(merged);
    }

    private static Script ParseBody(string body, string sourcePath)
    {
        var options = new ParserOptions
        {
            AllowReturnOutsideFunction = true,
            AllowAwaitOutsideFunction = true,
            AllowImportExportEverywhere = true,
            EcmaVersion = EcmaVersion.Latest
        };

        return new Parser(options).ParseScript(body, sourcePath);
    }

    private static string? SerializeResult(Engine engine, JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        var serialized = new JsJsonSerializer(engine).Serialize(value);
        return serialized is JsString text ? text.ToString() : null;
    }

    private static string EnvelopeOk(string? value)
        => StjJson.Serialize(new { ok = true, value = value ?? string.Empty });

    private static string EnvelopeFail(string message)
        => StjJson.Serialize(new { ok = false, name = "WorkflowError", error = message });

    private static WorkflowRunResult Failed(string message, Stopwatch stopwatch, RunState state) => new()
    {
        Ok = false,
        Error = message,
        Duration = stopwatch.Elapsed,
        AgentCalls = state.AgentCalls,
        ToolCalls = state.ToolCalls
    };

    private static string Describe(Exception exception, TimeSpan timeout)
    {
        switch (exception)
        {
            case TimeoutException:
            case ExecutionCanceledException:
            case OperationCanceledException:
                return "工作流执行超时（" + DescribeTimeout(timeout) + "）。";
            case PromiseRejectedException rejected:
                return DescribeRejected(rejected);
            case JavaScriptException javascript:
                return "工作流脚本抛出错误：" + javascript.Message;
            case RecursionDepthOverflowException:
                return "工作流脚本递归过深，已中止。";
            case StatementsCountOverflowException:
                return "工作流脚本语句数超过上限，已中止。";
            default:
                return "工作流执行失败：" + exception.Message;
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
            var prefix = name.IsUndefined() ? "WorkflowError" : name.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return prefix + ": " + text;
            }
        }

        return "工作流脚本抛出异常：" + (value?.ToString() ?? exception.Message);
    }

    private static string DescribeTimeout(TimeSpan timeout)
        => timeout > TimeSpan.Zero
            ? timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " 秒"
            : "无限制";

    private sealed class RunState
    {
        public RunState(string arguments) => Arguments = arguments;

        public string Arguments { get; }

        public int Steps { get; set; }

        public int AgentCalls { get; set; }

        public int ToolCalls { get; set; }

        public string? AbortError { get; set; }
    }
}
