using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Extensions.Packages;
using Jint;
using Jint.Native;
using JsJsonParser = Jint.Native.Json.JsonParser;
using JsJsonSerializer = Jint.Native.Json.JsonSerializer;
using Jint.Runtime;

namespace AiTools.Extensions.Scripting;

/// <summary>一个 JavaScript 模块定义的扩展。</summary>
/// <remarks>每个包扩展持有独立 Jint 引擎；会话关闭时释放该引擎。</remarks>
public sealed class ScriptExtension : IExtension, IDisposable
{
    private readonly ExtensionPackageManifest _manifest;
    private readonly string _artifactPath;
    private readonly IExtensionFactoryContext _factoryContext;
    private readonly TimeSpan _timeout;
    private readonly Action<PluginDiagnostic> _report;
    private readonly object _gate = new();
    private readonly System.Threading.SemaphoreSlim _invocationGate = new(1, 1);
    private Engine? _engine;
    private bool _disposed;
    private string _name;
    private string? _description;

    internal ScriptExtension(
        ExtensionPackageManifest manifest,
        string artifactPath,
        IExtensionFactoryContext factoryContext,
        TimeSpan timeout,
        Action<PluginDiagnostic> report)
    {
        _manifest = manifest;
        _artifactPath = artifactPath;
        _factoryContext = factoryContext;
        _timeout = timeout;
        _report = report;
        _name = manifest.Name;
        _description = manifest.Description;
    }

    /// <inheritdoc />
    public string Name => _name;

    /// <inheritdoc />
    public string? Description => _description;

    /// <inheritdoc />
    public void Activate(IExtensionApi api)
    {
        if (api == null) throw new ArgumentNullException(nameof(api));
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScriptExtension));
            if (_engine != null) return;
            var engine = new Engine(options =>
            {
                options.Culture = CultureInfo.InvariantCulture;
                options.LimitRecursion(512);
                options.Host.StringCompilationAllowed = false;
                options.TimeoutInterval(_timeout);
                options.Constraints.PromiseTimeout = _timeout;
            });
            _engine = engine;
            var moduleContext = new ModuleContext(_manifest, _artifactPath, _factoryContext, _report);
            RegisterGlobals(engine, moduleContext);
            try
            {
                if (Path.GetExtension(_artifactPath).Equals(".ts", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The prepared artifact must be executable JavaScript, not a TypeScript source file.");
                if (IsBundled(_artifactPath))
                {
                    engine.Execute(File.ReadAllText(_artifactPath), _artifactPath);
                    engine.Execute("globalThis.__script_extension_module");
                    var module = engine.GetValue("__script_extension_module");
                    ReadExports(module);
                }
                else
                {
                    var source = File.ReadAllText(_artifactPath);
                    engine.Execute("var module = { exports: {} }; var exports = module.exports;\n" + source + "\nglobalThis.__script_extension_module = module.exports;", _artifactPath);
                    ReadExports(engine.GetValue("__script_extension_module"));
                }
                var exportsValue = engine.GetValue("__script_extension_module");
                var activate = exportsValue.IsObject() ? exportsValue.AsObject().Get("activate") : JsValue.Undefined;
                if (activate.IsUndefined() || !activate.IsCallable())
                    throw new InvalidOperationException("Script extension must export an activate(api) function.");
                var apiObject = CreateApiObject(engine, api, moduleContext);
                var returned = CallFunction(engine, activate, exportsValue, apiObject, "activation");
                if (returned.IsPromise())
                    throw new InvalidOperationException("activate(api) must be synchronous and return quickly; asynchronous activation is not supported.");
                api.On(ExtensionEvent.SessionShutdown, (_, _, _) =>
                {
                    Dispose();
                    return Task.CompletedTask;
                });
            }
            catch (Exception ex)
            {
                ReportError(_artifactPath, "JavaScript extension activation failed: " + Describe(ex));
                engine.Dispose();
                _engine = null;
                throw;
            }
        }
    }

    /// <summary>释放该扩展的 JavaScript 引擎。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _engine?.Dispose();
            _engine = null;
        }
    }

    private static bool IsBundled(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.ai-tools/build/", StringComparison.OrdinalIgnoreCase);
    }

    private void ReadExports(JsValue exports)
    {
        if (!exports.IsObject()) throw new InvalidOperationException("Script extension module.exports must be an object.");
        var moduleObject = exports.AsObject();
        var name = moduleObject.Get("name");
        if (!name.IsUndefined() && !name.IsNull() && name.ToString().Length > 0) _name = name.ToString();
        var description = moduleObject.Get("description");
        if (!description.IsUndefined() && !description.IsNull()) _description = description.ToString();
    }

    private JsValue CallFunction(Engine engine, JsValue function, JsValue thisValue, params object?[] args)
    {
        var values = new JsValue[args.Length];
        for (var index = 0; index < args.Length; index++)
            values[index] = args[index] as JsValue ?? JsValue.FromObject(engine, args[index]);
        return function.AsObject().Call(thisValue, values);
    }

    private void RegisterGlobals(Engine engine, ModuleContext moduleContext)
    {
        engine.SetValue("__script_external_require", new Func<string, string>(moduleContext.ExternalRequire));
        engine.SetValue("__script_log", new Action<string, string>((level, text) => _report(new PluginDiagnostic
        {
            Kind = PluginDiagnosticKind.Info,
            PluginName = _manifest.Name,
            Path = _artifactPath,
            Message = "console." + level + ": " + text
        })));
        engine.Execute(@"
            globalThis.require = function(name) { return globalThis.__script_external_require(String(name)); };
            globalThis.console = {
              log: function(){ globalThis.__script_log('log', Array.prototype.join.call(arguments, ' ')); },
              info: function(){ globalThis.__script_log('info', Array.prototype.join.call(arguments, ' ')); },
              warn: function(){ globalThis.__script_log('warn', Array.prototype.join.call(arguments, ' ')); },
              error: function(){ globalThis.__script_log('error', Array.prototype.join.call(arguments, ' ')); },
              debug: function(){ globalThis.__script_log('debug', Array.prototype.join.call(arguments, ' ')); }
            };
            globalThis.process = undefined;
            globalThis.Buffer = undefined;
            globalThis.__script_is_error = function(value) { return value instanceof Error; };
        ");
    }

    private JsValue CreateApiObject(Engine engine, IExtensionApi api, ModuleContext moduleContext)
    {
        var apiObject = new JsJsonParser(engine).Parse("{}").AsObject();
        var stateObject = new JsJsonParser(engine).Parse("{}").AsObject();
        stateObject.FastAddProperty("get", new Func<string, JsValue, JsValue>((key, fallback) =>
        {
            var stored = api.State.Get(api.Name, key);
            return stored.HasValue ? ConvertJson(engine, stored.Value.GetRawText()) : (fallback.IsUndefined() ? JsValue.Undefined : fallback);
        }), true, false, true);
        stateObject.FastAddProperty("set", new Action<string, JsValue>((key, value) =>
        {
            var json = SerializeValue(engine, value);
            if (json == null) api.State.Remove(api.Name, key);
            else api.State.Set(api.Name, key, JsonDocument.Parse(json).RootElement.Clone());
        }), true, false, true);
        apiObject.FastAddProperty("name", api.Name, true, false, true);
        apiObject.FastAddProperty("state", stateObject, true, false, true);
        apiObject.FastAddProperty("registerCommand", new Action<string, string, JsValue, string?>((name, description, handler, hint) =>
        {
            if (!handler.IsCallable()) throw new InvalidOperationException("registerCommand handler must be a function.");
            api.RegisterCommand(name, description ?? string.Empty, async (context, args, token) =>
            {
                try
                {
                    var jsContext = CreateContext(engine, context, moduleContext, token);
                    var jsArgs = ToJs(engine, new { raw = args.Raw, positional = args.Positional, isEmpty = args.IsEmpty });
                    var value = await InvokeAsync(engine, handler, JsValue.Undefined, new object?[] { jsContext, jsArgs }, token).ConfigureAwait(false);
                    if (value.IsUndefined() || value.IsNull()) return CommandResult.Noop;
                    if (value.IsString()) return CommandResult.Prompt(value.AsString());
                    if (value.IsObject() && value.AsObject().Get("display") is { } display && !display.IsUndefined() && !display.IsNull())
                        return CommandResult.Display(display.ToString());
                    return CommandResult.Prompt(SerializeValue(engine, value) ?? value.ToString());
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(Describe(ex));
                }
            }, hint);
        }), true, false, true);
        apiObject.FastAddProperty("registerTool", new Action<JsValue, JsValue, JsValue>((definition, execute, options) =>
        {
            if (!execute.IsCallable()) throw new InvalidOperationException("registerTool execute callback must be a function.");
            var definitionObject = definition.IsObject() ? definition.AsObject() : throw new InvalidOperationException("registerTool requires a definition object.");
            var toolName = definitionObject.Get("name").ToString();
            var descriptionValue = definitionObject.Get("description");
            var schemaValue = definitionObject.Get("parameters");
            var schema = schemaValue.IsUndefined() || schemaValue.IsNull() ? "{\"type\":\"object\",\"properties\":{}}" : SerializeValue(engine, schemaValue)!;
            var exposure = ReadOption(options, "exposure", ToolExposure.Direct);
            var readOnly = ReadOption(options, "readOnlyHint", false);
            var namespaceName = ReadOption<string?>(options, "namespace", null);
            ToolReach? reach = null;
            var reachWords = ReadOption<JsValue>(options, "reachesTools", JsValue.Undefined);
            if (!reachWords.IsUndefined() && !reachWords.IsNull())
            {
                if (reachWords.ToString() == "*") reach = ToolReach.All();
                else if (reachWords.IsArray()) reach = ToolReach.Words(reachWords.AsArray().ToArray().Select(value => value.ToString()).ToArray());
                else reach = ToolReach.Words(reachWords.ToString());
            }
            api.RegisterTool(ToolDefinition.FromJsonSchema(toolName, descriptionValue.IsUndefined() ? null : descriptionValue.ToString(), schema), async (call, context, token) =>
            {
                try
                {
                    var jsContext = CreateContext(engine, context, moduleContext, token);
                    var jsArgs = ConvertJson(engine, call.ArgumentsJson);
                    var value = await InvokeAsync(engine, execute, JsValue.Undefined, new object?[] { jsArgs, jsContext }, token).ConfigureAwait(false);
                    if (value.IsUndefined() || value.IsNull()) return string.Empty;
                    return value.IsString() ? value.AsString() : SerializeValue(engine, value) ?? value.ToString();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { throw new InvalidOperationException(Describe(ex)); }
            }, exposure, readOnly, namespaceName, reach);
        }), true, false, true);
        apiObject.FastAddProperty("on", new Func<string, JsValue, Action>((eventName, handler) =>
        {
            if (!handler.IsCallable()) throw new InvalidOperationException("on handler must be a function.");
            if (!TryParseEvent(eventName, out var extensionEvent)) throw new InvalidOperationException("Unknown extension event '" + eventName + "'.");
            return api.On(extensionEvent, async (payload, context, token) =>
            {
                try
                {
                    var eventObject = CreateEventObject(engine, extensionEvent, payload);
                    var jsContext = CreateContext(engine, context, moduleContext, token);
                    var value = await InvokeAsync(engine, handler, JsValue.Undefined, new object?[] { eventObject, jsContext }, token).ConfigureAwait(false);
                    ApplyEventResult(extensionEvent, payload, eventObject, value);

                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (extensionEvent is ExtensionEvent.ToolCall or ExtensionEvent.BeforeProviderRequest)
                        throw new InvalidOperationException(Describe(ex));
                    _report(new PluginDiagnostic { Kind = PluginDiagnosticKind.Warning, PluginName = _manifest.Name, Path = _artifactPath, Message = "Extension event handler failed: " + Describe(ex) });

                }
            });
        }), true, false, true);
        apiObject.FastAddProperty("registerProvider", new Action<JsValue>(registration => api.RegisterProvider(ToProvider(engine, registration))), true, false, true);
        apiObject.FastAddProperty("registerVirtualModel", new Action<JsValue>(registration => api.RegisterVirtualModel(ToVirtualModel(engine, registration))), true, false, true);
        apiObject.FastAddProperty("registerMcpServer", new Func<JsValue, JsValue>(server =>
        {
            var unregister = api.RegisterMcpServer(ToMcpServer(engine, server));
            return JsValue.FromObject(engine, unregister);
        }), true, false, true);
        apiObject.FastAddProperty("registerNamespace", new Action<JsValue>(value => api.RegisterNamespace(ToNamespace(engine, value))), true, false, true);
        apiObject.FastAddProperty("registerSkill", new Action<JsValue>(value => api.RegisterSkill(ToSkill(engine, moduleContext, value))), true, false, true);
        apiObject.FastAddProperty("registerLoadoutPreparer", new Action<JsValue>(handler =>
        {
            if (!handler.IsCallable()) throw new InvalidOperationException("registerLoadoutPreparer requires a function.");
            api.RegisterLoadoutPreparer(loadout =>
            {
                var input = ToJs(engine, new
                {
                    declared = loadout.Declared.Select(tool => new { name = tool.Definition.Name, description = tool.Definition.Description }).ToArray(),
                    callable = loadout.Callable.Select(tool => tool.Definition.Name).ToArray(),
                    all = loadout.All.Select(tool => tool.Definition.Name).ToArray(),
                    descriptions = loadout.Descriptions,
                    hiddenDeclarations = loadout.HiddenDeclarations.ToArray()
                });
                var returned = CallFunction(engine, handler, JsValue.Undefined, input);
                if (returned.IsPromise()) throw new InvalidOperationException("Loadout preparers must be synchronous.");
                if (returned.IsObject())
                {
                    var obj = returned.AsObject();
                    var descriptions = obj.Get("descriptions");
                    if (descriptions.IsObject())
                    {
                        foreach (var key in descriptions.AsObject().GetOwnPropertyKeys())
                            loadout.Descriptions[key.ToString()] = descriptions.AsObject().Get(key).ToString();
                    }
                    var hidden = obj.Get("hiddenDeclarations");
                    if (hidden.IsArray())
                        foreach (var item in hidden.AsArray().ToArray()) loadout.HiddenDeclarations.Add(item.ToString());
                }
                return loadout;
            });
        }), true, false, true);
        apiObject.FastAddProperty("registerToolRenderer", new Action<JsValue>(handler =>
        {
            if (!handler.IsCallable()) throw new InvalidOperationException("registerToolRenderer requires a function.");
            api.RegisterToolRenderer(context =>
            {
                var jsContext = ToJs(engine, new { toolName = context.ToolName, argumentsJson = context.ArgumentsJson, isResult = context.IsResult, resultText = context.ResultText, isError = context.IsError });
                var value = CallFunction(engine, handler, JsValue.Undefined, jsContext);
                return value.IsUndefined() || value.IsNull() ? null : value.ToString();
            });
        }), true, false, true);
        engine.SetValue("__script_api", apiObject);
        engine.Execute(@"
          (function(api) {
            var registerCommand = api.registerCommand;
            api.registerCommand = function(name, description, handler, hint) {
              return registerCommand(name, description, handler, hint === undefined ? null : hint);
            };
            var registerTool = api.registerTool;
            api.registerTool = function(definition, execute, options) {
              return registerTool(definition, execute, options === undefined ? {} : options);
            };
          })(globalThis.__script_api);
        ");
        return engine.GetValue("__script_api");
    }
    private JsValue CreateContext(Engine engine, CommandContext context, ModuleContext moduleContext, CancellationToken token)
        => CreateContextCore(engine, context.State, context.SessionId, context.WorkingDirectory, context.Ui, context.EntryStore,
            kind => context.ReadEntry(kind), context.NotifyUser, (name, json) => context.ExecuteToolAsync(name, json, token), token);

    private JsValue CreateContext(Engine engine, ToolContext context, ModuleContext moduleContext, CancellationToken token)
        => CreateContextCore(engine, context.State, context.SessionId, context.WorkingDirectory, context.Ui, context.EntryStore,
            kind => context.EntryStore?.Invoke(kind, null),
            (message, kind) => context.Ui?.Notify(message, kind),
            (name, json) => context.ExecuteToolAsync(name, json, token), token);

    private JsValue CreateContextCore(
        Engine engine,
        ExtensionStateStore stateStore,
        string? sessionId,
        string workingDirectory,
        IExtensionUi? ui,
        Func<string, object?, object?>? entryStore,
        Func<string, object?> readEntry,
        Action<string, ExtensionNoticeKind>? notify,
        Func<string, string, Task<string>> executeTool,
        CancellationToken token)
    {
        var result = new JsJsonParser(engine).Parse("{}").AsObject();
        var state = new JsJsonParser(engine).Parse("{}").AsObject();
        state.FastAddProperty("get", new Func<string, JsValue, JsValue>((key, fallback) =>
        {
            var value = stateStore.Get(_name, key);
            return value.HasValue ? ConvertJson(engine, value.Value.GetRawText()) : (fallback.IsUndefined() ? JsValue.Undefined : fallback);
        }), true, false, true);
        state.FastAddProperty("set", new Action<string, JsValue>((key, value) =>
        {
            var json = SerializeValue(engine, value);
            if (json == null) stateStore.Remove(_manifest.Name, key);
            else stateStore.Set(_manifest.Name, key, JsonDocument.Parse(json).RootElement.Clone());
        }), true, false, true);
        result.FastAddProperty("state", state, true, false, true);
        result.FastAddProperty("sessionId", sessionId, true, false, true);
        result.FastAddProperty("workingDirectory", workingDirectory, true, false, true);
        result.FastAddProperty("appendEntry", new Action<string, JsValue>((kind, value) =>
        {
            var json = SerializeValue(engine, value);
            entryStore?.Invoke(kind, json == null ? null : JsonDocument.Parse(json).RootElement.Clone());
        }), true, false, true);
        result.FastAddProperty("readEntry", new Func<string, JsValue>(kind => ToJs(engine, readEntry(kind))), true, false, true);
        result.FastAddProperty("notify", new Action<string, string?>((message, kind) => notify?.Invoke(message, ParseNoticeKind(kind))), true, false, true);
        result.FastAddProperty("executeTool", new Func<string, string, Task<string>>(executeTool), true, false, true);
        result.FastAddProperty("ui", BuildUi(engine, ui, token), true, false, true);
        return result;
    }

    private static JsValue BuildUi(Engine engine, IExtensionUi? ui, CancellationToken token)
    {
        if (ui == null) return JsValue.Null;
        var obj = new JsJsonParser(engine).Parse("{}").AsObject();
        obj.FastAddProperty("hasDialogs", ui.HasDialogs, true, false, true);
        obj.FastAddProperty("confirm", new Func<string, string, bool, Task<bool>>((title, message, defaultValue) => ui.ConfirmAsync(title, message, defaultValue, token)), true, false, true);
        obj.FastAddProperty("input", new Func<string, string?, Task<string?>>((title, placeholder) => ui.InputAsync(title, placeholder, token)), true, false, true);
        obj.FastAddProperty("select", new Func<string, JsValue, Task<string?>>((title, choices) => ui.SelectAsync(title, choices.AsArray().ToArray().Select(item => item.ToString()).ToArray(), token)), true, false, true);
        obj.FastAddProperty("choose", new Func<string, string?, JsValue, Task<JsValue>>((title, message, choices) =>
            ChooseAsync(engine, ui, title, message, choices, token)), true, false, true);
        obj.FastAddProperty("notify", new Action<string, string?>((message, kind) => ui.Notify(message, ParseNoticeKind(kind))), true, false, true);
        obj.FastAddProperty("setStatus", new Action<string?>(ui.SetStatus), true, false, true);
        return obj;
    }

    /// <summary>
    /// 脚本侧的带说明选项：参数是 <c>{ label, description }</c> 数组，
    /// 返回 <c>{ index, label, description }</c>，取消时返回 null。
    /// 宿主不支持富选项时退化为 <see cref="IExtensionUi.SelectAsync"/>（说明拼进标签）。
    /// </summary>
    private static async Task<JsValue> ChooseAsync(
        Engine engine,
        IExtensionUi ui,
        string title,
        string? message,
        JsValue choicesValue,
        CancellationToken token)
    {
        var labels = new List<string>();
        var descriptions = new List<string>();
        foreach (var item in choicesValue.AsArray().ToArray())
        {
            var label = item.IsObject() ? item.AsObject().Get("label").ToString() : item.ToString();
            var description = item.IsObject() ? item.AsObject().Get("description").ToString() : string.Empty;
            labels.Add(label);
            descriptions.Add(description);
        }

        if (labels.Count == 0)
        {
            return JsValue.Null;
        }

        ExtensionChoiceResult? result = null;
        if (ui is IExtensionChoiceUi choiceUi)
        {
            var choices = new List<ExtensionChoice>(labels.Count);
            for (var index = 0; index < labels.Count; index++)
            {
                choices.Add(new ExtensionChoice { Label = labels[index], Description = descriptions[index] });
            }

            result = await choiceUi.ChooseAsync(title, message, choices, token).ConfigureAwait(false);
        }
        else
        {
            var decorated = new List<string>(labels.Count);
            for (var index = 0; index < labels.Count; index++)
            {
                decorated.Add(descriptions[index].Length == 0 ? labels[index] : labels[index] + " — " + descriptions[index]);
            }

            var selected = await ui.SelectAsync(title, decorated, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                var index = decorated.IndexOf(selected!);
                result = index >= 0
                    ? new ExtensionChoiceResult { Index = index, Label = labels[index], Description = descriptions[index] }
                    : new ExtensionChoiceResult { Index = -1, Label = selected! };
            }
        }

        if (result == null)
        {
            return JsValue.Null;
        }

        return ToJs(engine, new { index = result.Index, label = result.Label, description = result.Description });
    }

    private static JsValue CreateEventObject(Engine engine, ExtensionEvent extensionEvent, object payload)
    {
        var json = payload switch
        {
            BeforeAgentStartEvent e => JsonSerializer.Serialize(new
            {
                systemPrompt = e.SystemPrompt,
                promptSections = e.PromptSections,
                prependMessages = ProjectMessages(e.PrependMessages),
                model = e.Model
            }),
            ToolCallEvent e => JsonSerializer.Serialize(new { toolName = e.ToolName, argumentsJson = e.ArgumentsJson, blockReason = e.BlockReason }),
            ToolResultEvent e => JsonSerializer.Serialize(new { toolName = e.ToolName, text = e.Text, isError = e.IsError }),
            InputEvent e => JsonSerializer.Serialize(new { text = e.Text, suppress = e.Suppress }),
            TurnEndEvent e => JsonSerializer.Serialize(new { text = e.Text, toolCallCount = e.ToolCallCount, @continue = e.Continue, continuationPrompt = e.ContinuationPrompt }),
            ContextEvent e => JsonSerializer.Serialize(new { messages = ProjectMessages(e.Messages), systemPrompt = e.SystemPrompt, model = e.Model }),
            ProviderHeadersEvent e => JsonSerializer.Serialize(new { providerName = e.ProviderName, headers = e.Headers }),
            ProviderRequestEvent e => JsonSerializer.Serialize(new { providerName = e.ProviderName, operation = e.Operation, bodyJson = e.BodyJson, isStreaming = e.IsStreaming, blockReason = e.BlockReason }),
            ProviderResponseEvent e => JsonSerializer.Serialize(new { providerName = e.ProviderName, statusCode = e.StatusCode, contentLength = e.ContentLength, durationMs = e.Duration.TotalMilliseconds }),
            ProviderStreamEvent e => JsonSerializer.Serialize(new { providerName = e.ProviderName, data = e.Data, eventType = e.EventType }),
            ToolExecutionEvent e => JsonSerializer.Serialize(new { toolName = e.ToolName, toolCallId = e.ToolCallId, argumentsJson = e.ArgumentsJson, partialOutput = e.PartialOutput, resultText = e.ResultText, isError = e.IsError, durationMs = e.Duration.TotalMilliseconds }),
            SessionStartEvent e => JsonSerializer.Serialize(new { sessionId = e.SessionId, workingDirectory = e.WorkingDirectory }),
            _ => "{}"
        };
        var eventObject = new JsJsonParser(engine).Parse(json).AsObject();
        if (payload is ToolCallEvent toolCall)
        {
            eventObject.FastAddProperty("block", new Action<string>(reason =>
            {
                toolCall.Block(reason);
                eventObject.FastAddProperty("blockReason", reason, true, true, true);
            }), true, false, true);
        }
        if (payload is ContextEvent)
        {
            var messages = eventObject.Get("messages");
            eventObject.FastAddProperty("__scriptOriginalMessages", new JsJsonSerializer(engine).Serialize(messages).ToString(), false, false, true);
        }
        return eventObject;
    }

    private static object[] ProjectMessages(System.Collections.Generic.IReadOnlyList<ChatMessage> messages)
        => messages.Select(message => (object)new
        {
            role = message.Role.ToString().ToLowerInvariant(),
            text = message.GetText(),
            name = message.Name
        }).ToArray();

    private static ChatMessage[] ReadMessages(JsValue value)
    {
        if (!value.IsArray()) return Array.Empty<ChatMessage>();
        return value.AsArray().ToArray().Where(item => item.IsObject()).Select(item =>
        {
            var message = item.AsObject();
            var roleValue = JsStringOrNull(message.Get("role"));
            var role = Enum.TryParse<ChatRole>(roleValue, true, out var parsedRole) ? parsedRole : ChatRole.User;
            var text = JsStringOrNull(message.Get("text")) ?? string.Empty;
            var name = JsStringOrNull(message.Get("name"));
            return new ChatMessage
            {
                Role = role,
                Name = name,
                Content = new ContentPart[] { ContentPart.FromText(text) }
            };
        }).ToArray();
    }

    private static void ApplyEventResult(ExtensionEvent extensionEvent, object payload, JsValue eventObject, JsValue result)
    {
        var value = eventObject.AsObject();
        switch (payload)
        {
            case BeforeAgentStartEvent before:
                before.SystemPrompt = JsStringOrNull(value.Get("systemPrompt"));
                before.PromptSections.Clear();
                var sections = value.Get("promptSections");
                if (sections.IsArray()) before.PromptSections.AddRange(sections.AsArray().ToArray().Select(item => item.ToString()));
                before.PrependMessages.Clear();
                before.PrependMessages.AddRange(ReadMessages(value.Get("prependMessages")));
                break;
            case ToolCallEvent toolCall:
                toolCall.ArgumentsJson = JsStringOrNull(value.Get("argumentsJson")) ?? "{}";
                toolCall.BlockReason = JsStringOrNull(value.Get("blockReason"));
                if (extensionEvent == ExtensionEvent.ToolCall && result.IsBoolean() && !result.AsBoolean()) toolCall.BlockReason = null;
                if (extensionEvent == ExtensionEvent.ToolCall && result.IsObject() && !result.AsObject().Get("blockReason").IsUndefined())
                    toolCall.BlockReason = result.AsObject().Get("blockReason").ToString();
                break;
            case ToolResultEvent toolResult:
                toolResult.Text = JsStringOrNull(value.Get("text")) ?? string.Empty;
                break;
            case InputEvent input:
                input.Text = JsStringOrNull(value.Get("text")) ?? string.Empty;
                input.Suppress = value.Get("suppress").IsBoolean() && value.Get("suppress").AsBoolean();
                break;
            case TurnEndEvent turnEnd:
                turnEnd.Continue = value.Get("continue").IsBoolean() && value.Get("continue").AsBoolean();
                turnEnd.ContinuationPrompt = JsStringOrNull(value.Get("continuationPrompt"));
                break;
            case ContextEvent context:
                context.SystemPrompt = JsStringOrNull(value.Get("systemPrompt"));
                var messages = value.Get("messages");
                var currentMessagesJson = new JsJsonSerializer(eventObject.AsObject().Engine).Serialize(messages).ToString();
                if (!string.Equals(currentMessagesJson, JsStringOrNull(value.Get("__scriptOriginalMessages")), StringComparison.Ordinal))
                    context.Messages = ReadMessages(messages);
                break;
            case ProviderRequestEvent providerRequest:
                providerRequest.BodyJson = JsStringOrNull(value.Get("bodyJson")) ?? providerRequest.BodyJson;
                providerRequest.BlockReason = JsStringOrNull(value.Get("blockReason"));
                break;
            case ProviderHeadersEvent headers:
                var headerObject = value.Get("headers");
                if (headerObject.IsObject())
                {
                    headers.Headers.Clear();
                    foreach (var key in headerObject.AsObject().GetOwnPropertyKeys()) headers.Headers[key.ToString()] = headerObject.AsObject().Get(key).ToString();
                }
                break;
        }
    }

    private async Task<JsValue> InvokeAsync(Engine engine, JsValue function, JsValue thisValue, object?[] args, CancellationToken cancellationToken)
    {
        await _invocationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        var token = timeoutSource.Token;
        try
        {
            engine.SetValue("__script_handler", function);
            engine.SetValue("__script_this", thisValue);
            var argumentNames = new string[args.Length];
            for (var index = 0; index < args.Length; index++)
            {
                var name = "__script_arg" + index.ToString(CultureInfo.InvariantCulture);
                var argument = args[index] as JsValue ?? JsValue.FromObject(engine, args[index]);
                engine.SetValue(name, argument);
                argumentNames[index] = name;
            }

            var call = "globalThis.__script_handler.call(globalThis.__script_this" +
                       (argumentNames.Length == 0 ? string.Empty : ", " + string.Join(", ", argumentNames)) + ");";
            return await engine.EvaluateAsync(call, "script://extension-handler", token).ConfigureAwait(false);
        }
        catch (Exception ex) when (token.IsCancellationRequested || ex is TimeoutException or ExecutionCanceledException)
        {
            throw new TimeoutException("JavaScript handler timed out after " + _timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " seconds.", ex);
        }
        finally
        {
            _invocationGate.Release();
        }
    }

    private static JsValue ToJs(Engine engine, object? value)
        => value == null ? JsValue.Null : new JsJsonParser(engine).Parse(JsonSerializer.Serialize(value));

    private static JsValue ConvertJson(Engine engine, string json)
        => new JsJsonParser(engine).Parse(json);

    private static object? ConvertValue(JsValue value)
        => value.IsUndefined() || value.IsNull() ? null : value.IsString() ? value.AsString() : value.IsBoolean() ? value.AsBoolean() : value.ToString();

    private static string? SerializeValue(Engine engine, JsValue value)
        => value.IsUndefined() ? null : new JsJsonSerializer(engine).Serialize(value) is JsString text ? text.ToString() : null;

    private static string? JsStringOrNull(JsValue value) => value.IsUndefined() || value.IsNull() ? null : value.ToString();


    private static string ReadOption(JsValue options, string name, string fallback)
        => options.IsObject() && !options.AsObject().Get(name).IsUndefined() ? options.AsObject().Get(name).ToString() : fallback;

    private static T ReadOption<T>(JsValue options, string name, T fallback)
    {
        if (!options.IsObject()) return fallback;
        var value = options.AsObject().Get(name);
        if (value.IsUndefined() || value.IsNull()) return fallback;
        if (typeof(T) == typeof(ToolExposure) && Enum.TryParse<ToolExposure>(value.ToString(), true, out var exposure)) return (T)(object)exposure;
        if (typeof(T) == typeof(bool) && value.IsBoolean()) return (T)(object)value.AsBoolean();
        if (typeof(T) == typeof(string)) return (T)(object)value.ToString();
        if (typeof(T) == typeof(JsValue)) return (T)(object)value;
        return fallback;
    }

    private static bool TryParseEvent(string value, out ExtensionEvent extensionEvent)
    {
        var normalized = value.Replace('-', '_').ToLowerInvariant();
        foreach (var item in Enum.GetValues<ExtensionEvent>())
        {
            if (string.Equals(ToSnakeCase(item.ToString()), normalized, StringComparison.OrdinalIgnoreCase))
            {
                extensionEvent = item;
                return true;
            }
        }
        extensionEvent = default;
        return false;
    }

    private static string ToSnakeCase(string value)
    {
        var chars = new List<char>();
        for (var index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index])) chars.Add('_');
            chars.Add(char.ToLowerInvariant(value[index]));
        }
        return new string(chars.ToArray());
    }

    private static ExtensionProviderRegistration ToProvider(Engine engine, JsValue value)
    {
        var obj = value.AsObject();
        return new ExtensionProviderRegistration
        {
            Name = obj.Get("name").ToString(),
            Protocol = obj.Get("protocol").ToString(),
            BaseAddress = Uri.TryCreate(JsStringOrNull(obj.Get("baseAddress")), UriKind.Absolute, out var uri) ? uri : null,
            ApiKey = JsStringOrNull(obj.Get("apiKey")),
            DefaultModel = JsStringOrNull(obj.Get("defaultModel"))
        };
    }

    private static VirtualModelRegistration ToVirtualModel(Engine engine, JsValue value)
    {
        var obj = value.AsObject();
        return new VirtualModelRegistration
        {
            VirtualModelName = obj.Get("virtualModelName").ToString(),
            Provider = obj.Get("provider").ToString(),
            TargetModel = obj.Get("targetModel").ToString(),
            Description = JsStringOrNull(obj.Get("description"))
        };
    }

    private static PluginMcpServer ToMcpServer(Engine engine, JsValue value)
    {
        var obj = value.AsObject();
        var server = new PluginMcpServer
        {
            Name = obj.Get("name").ToString(),
            Command = JsStringOrNull(obj.Get("command")) ?? string.Empty,
            Transport = Enum.TryParse<PluginMcpTransportKind>(JsStringOrNull(obj.Get("transport")), true, out var transport) ? transport : PluginMcpTransportKind.Stdio
        };
        return server;
    }

    private static ToolNamespace ToNamespace(Engine engine, JsValue value)
    {
        var obj = value.AsObject();
        var aliases = obj.Get("aliases");
        return new ToolNamespace
        {
            Name = obj.Get("name").ToString(),
            Aliases = aliases.IsArray() ? aliases.AsArray().ToArray().Select(item => item.ToString()).ToArray() : Array.Empty<string>(),
            Description = JsStringOrNull(obj.Get("description")),
            Instructions = JsStringOrNull(obj.Get("instructions"))
        };
    }

    /// <summary>
    /// 把脚本侧的技能声明转成 <see cref="AiTools.Skills.SkillDefinition"/>。
    /// <c>directory</c> 可省略：默认为包根，此时随包资源以包根为基准解析。
    /// </summary>
    private static AiTools.Skills.SkillDefinition ToSkill(Engine engine, ModuleContext moduleContext, JsValue value)
    {
        if (!value.IsObject())
        {
            throw new InvalidOperationException("registerSkill requires a skill object.");
        }

        var obj = value.AsObject();
        var name = JsStringOrNull(obj.Get("name"))?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("registerSkill requires a skill name.");
        }

        var description = JsStringOrNull(obj.Get("description"))?.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("registerSkill requires a skill description for '" + name + "'.");
        }

        var directory = JsStringOrNull(obj.Get("directory"));
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = moduleContext.PackageDirectory;
        }
        else if (!Path.IsPathRooted(directory))
        {
            directory = Path.GetFullPath(Path.Combine(moduleContext.PackageDirectory, directory!));
        }

        var instructions = JsStringOrNull(obj.Get("instructions"))
                           ?? JsStringOrNull(obj.Get("body"))
                           ?? string.Empty;

        var allowedTools = new List<string>();
        var allowedValue = obj.Get("allowedTools");
        if (allowedValue.IsArray())
        {
            foreach (var item in allowedValue.AsArray().ToArray())
            {
                var tool = JsStringOrNull(item);
                if (!string.IsNullOrWhiteSpace(tool))
                {
                    allowedTools.Add(tool!);
                }
            }
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadataValue = obj.Get("metadata");
        if (metadataValue.IsObject())
        {
            foreach (var key in metadataValue.AsObject().GetOwnPropertyKeys())
            {
                var text = JsStringOrNull(metadataValue.AsObject().Get(key));
                if (text != null)
                {
                    metadata[key.ToString()] = text;
                }
            }
        }

        return new AiTools.Skills.SkillDefinition
        {
            Name = name!,
            Description = description!,
            Instructions = instructions,
            DirectoryPath = Path.GetFullPath(directory!),
            SkillFilePath = Path.Combine(Path.GetFullPath(directory!), "SKILL.md"),
            License = JsStringOrNull(obj.Get("license")),
            Version = JsStringOrNull(obj.Get("version")),
            Compatibility = JsStringOrNull(obj.Get("compatibility")),
            AllowedTools = allowedTools,
            Metadata = metadata
        };
    }

    private static ExtensionNoticeKind ParseNoticeKind(string? kind)
        => Enum.TryParse<ExtensionNoticeKind>(kind, true, out var parsed) ? parsed : ExtensionNoticeKind.Info;

    private static string Describe(Exception exception)
        => exception switch
        {
            PromiseRejectedException rejected => "JavaScript promise rejected: " + rejected.Message,
            JavaScriptException javascript => javascript.Message,
            TimeoutException or ExecutionCanceledException => "JavaScript handler timed out.",
            RecursionDepthOverflowException => "JavaScript recursion depth exceeded.",
            _ => exception.Message
        };

    private void ReportError(string path, string message)
        => _report(new PluginDiagnostic { Kind = PluginDiagnosticKind.Error, PluginName = _manifest.Name, Path = path, Message = message });

    private sealed class ModuleContext
    {
        private readonly ExtensionPackageManifest _manifest;
        private readonly string _artifactPath;
        private readonly IExtensionFactoryContext _factoryContext;
        private readonly Action<PluginDiagnostic> _report;
        private readonly string _root;

        public ModuleContext(ExtensionPackageManifest manifest, string artifactPath, IExtensionFactoryContext factoryContext, Action<PluginDiagnostic> report)
        {
            _manifest = manifest;
            _artifactPath = artifactPath;
            _factoryContext = factoryContext;
            _report = report;
            _root = Path.GetFullPath(factoryContext.PackageDirectory);
        }

        public string ExternalRequire(string specifier)
        {
            if (specifier is "fs" or "node:fs" or "child_process" or "node:child_process" or "path" or "node:path" or "os" or "node:os" or "process" or "node:process")
                throw new InvalidOperationException("Node.js module '" + specifier + "' is unavailable in the script sandbox; filesystem and process APIs are disabled.");
            if (_manifest.Require.Contains(specifier, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sibling package module '" + specifier + "' is only available inside the prepared bundle.");
            throw new InvalidOperationException("External module '" + specifier + "' is not permitted; require() is package-local only.");
        }

        /// <summary>获取扩展包根目录；脚本技能的资源默认相对于它解析。</summary>
        public string PackageDirectory => _root;
    }
}
