using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Extensions;

/// <summary>
/// 扩展注册表与事件分发器。扩展在激活时登记命令、工具与事件处理器；
/// 事件按注册顺序执行，转换类事件链式传递，拦截类事件 fail-closed。
/// </summary>
/// <remarks>
/// 该类型不做持久化、不持有 UI，也不感知 WPF；宿主通过它拿到命令表、工具表与事件流。
/// 工具的可达性由「激活集合 + 曝光级别 + 装载准备器」共同决定，见 <see cref="BuildLoadout"/>。
/// </remarks>
public sealed class ExtensionRuntime
{
    /// <summary>内置编排工具（<c>tool_search</c>、<c>codemode</c>）的归属名。</summary>
    public const string RuntimeOwner = "runtime";

    private readonly List<IExtension> _extensions = new();
    private readonly List<ExtensionCommand> _commands = new();
    private readonly List<ExtensionTool> _tools = new();
    private readonly List<EventSubscription> _subscriptions = new();
    private readonly Dictionary<string, bool> _activationState = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Func<ToolRenderContext, string?>> _toolRenderers = new();
    private readonly Dictionary<string, List<Func<ToolRenderContext, string?>>> _toolRenderersByOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExtensionProviderRegistration> _providers = new();
    private readonly List<VirtualModelRegistration> _virtualModels = new();
    private readonly List<PluginMcpServer> _mcpServers = new();
    private readonly List<Func<Task>> _mcpCleanup = new();
    private readonly Dictionary<string, List<Func<Task>>> _mcpCleanupByOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NamespaceRegistration> _namespaces = new();
    private readonly List<SkillRegistration> _skills = new();
    private readonly List<LoadoutPreparerRegistration> _loadoutPreparers = new();
    private int _skillsVersion;
    private readonly ExtensionTool _toolSearchTool;
    private readonly ExtensionTool _codemodeTool;
    [ThreadStatic]
    private static ExtensionRuntime? _buildingLoadout;
    private readonly object _gate = new();

    /// <summary>创建运行时。</summary>
    /// <param name="state">扩展状态存储；为 null 时新建。</param>
    public ExtensionRuntime(ExtensionStateStore? state = null)
    {
        State = state ?? new ExtensionStateStore();
        _toolSearchTool = ToolSearchTool.Create(this);
        _codemodeTool = CodemodeTool.Create(this);
        CodemodeTool.Register(this, new ExtensionApi(this, RuntimeOwner));
    }

    /// <summary>获取扩展状态存储。</summary>
    public ExtensionStateStore State { get; }

    /// <summary>获取已注册的扩展，按激活顺序。</summary>
    public IReadOnlyList<IExtension> Extensions
    {
        get
        {
            lock (_gate)
            {
                return _extensions.ToArray();
            }
        }
    }

    /// <summary>获取已注册的命令，按名称排序；同名命令后注册者覆盖先注册者。</summary>
    public IReadOnlyList<ExtensionCommand> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands
                    .GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .OrderBy(command => command.Name, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>获取扩展登记的工具，按名称排序；同名工具后注册者覆盖先注册者。不含运行时内置的编排工具。</summary>
    public IReadOnlyList<ExtensionTool> Tools
    {
        get
        {
            lock (_gate)
            {
                return _tools
                    .GroupBy(tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .OrderBy(tool => tool.Definition.Name, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>获取激活集合中的工具（含恒为激活的内置编排工具），未应用装载准备器。</summary>
    public IReadOnlyList<ExtensionTool> ActiveTools
    {
        get
        {
            ExtensionTool[] registered;
            HashSet<string> active;
            lock (_gate)
            {
                registered = Deduplicate();
                active = new HashSet<string>(_activeTools, StringComparer.OrdinalIgnoreCase);
            }

            var tools = new List<ExtensionTool>();
            foreach (var tool in registered)
            {
                if (active.Contains(tool.Definition.Name))
                {
                    tools.Add(tool);
                }
            }

            foreach (var builtIn in BuiltInTools(registered))
            {
                if (!tools.Any(tool => string.Equals(tool.Definition.Name, builtIn.Definition.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    tools.Add(builtIn);
                }
            }

            tools.Sort((left, right) => string.CompareOrdinal(left.Definition.Name, right.Definition.Name));
            return tools.ToArray();
        }
    }

    /// <summary>
    /// 获取声明给模型的工具：激活集合中未被隐藏、且曝光允许声明的工具，
    /// 已应用装载准备器的描述替换与隐藏声明。
    /// </summary>
    public IReadOnlyList<ExtensionTool> DeclaredTools => BuildLoadout().Declared;

    /// <summary>获取被装载准备器隐藏声明的工具名（仍保持激活且可调用）。</summary>
    public IReadOnlyCollection<string> RegistryHiddenDeclarations => BuildLoadout().HiddenDeclarations;

    /// <summary>获取扩展注册的模型 provider。</summary>
    public IReadOnlyList<ExtensionProviderRegistration> Providers
    {
        get
        {
            lock (_gate)
            {
                return _providers.ToArray();
            }
        }
    }

    /// <summary>获取扩展注册的虚拟模型路由。</summary>
    public IReadOnlyList<VirtualModelRegistration> VirtualModels
    {
        get
        {
            lock (_gate)
            {
                return _virtualModels.ToArray();
            }
        }
    }

    /// <summary>获取扩展注册的 MCP 服务器。</summary>
    public IReadOnlyList<PluginMcpServer> McpServers
    {
        get
        {
            lock (_gate)
            {
                return _mcpServers.ToArray();
            }
        }
    }

    /// <summary>获取扩展登记的 skill，按名称排序；同名后注册者覆盖先注册者。</summary>
    public IReadOnlyList<ExtensionSkill> Skills
    {
        get
        {
            lock (_gate)
            {
                return _skills
                    .GroupBy(item => item.Skill.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        var winner = group.Last();
                        return new ExtensionSkill { ExtensionName = winner.Owner, Definition = winner.Skill };
                    })
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <summary>获取技能表的变更版本号；每次新增、替换或卸载技能自增。宿主据此决定是否重建技能目录。</summary>
    public int SkillsVersion => Volatile.Read(ref _skillsVersion);

    /// <summary>
    /// 构建本轮的装载结果：激活集合 → 声明/可调用/全部，然后按注册顺序链式应用装载准备器。
    /// 准备器抛异常时被跳过，装载回退到它之前的结果。
    /// </summary>
    public ToolLoadout BuildLoadout()
    {
        // 准备器里若回调 DeclaredTools/ActiveTools，返回未应用准备器的结果，避免无限递归。
        if (ReferenceEquals(_buildingLoadout, this))
        {
            return ComposeLoadout();
        }

        var loadout = ComposeLoadout();

        LoadoutPreparerRegistration[] preparers;
        lock (_gate)
        {
            preparers = _loadoutPreparers.ToArray();
        }

        var previous = _buildingLoadout;
        _buildingLoadout = this;
        try
        {
            foreach (var preparer in preparers)
            {
                var candidate = ToolLoadout.Copy(loadout);
                try
                {
                    // 准备器可返回新对象，也可就地改写传入的副本；抛异常时整体丢弃本次改写。
                    var next = preparer.Prepare(candidate);
                    loadout = next ?? candidate;
                }
                catch (Exception)
                {
                    // 准备器失败只影响它自己：保留它之前的装载结果。
                }
            }
        }
        finally
        {
            _buildingLoadout = previous;
        }

        // 描述替换与隐藏声明只作用于声明面；可调用面与全部工具保持原样。
        var hidden = loadout.HiddenDeclarations;
        loadout.Declared = loadout.Declared
            .Select(tool => loadout.Descriptions.TryGetValue(tool.Definition.Name, out var description)
                ? ToolLoadout.WithDescription(tool, description)
                : tool)
            .Where(tool => !hidden.Contains(tool.Definition.Name))
            .ToArray();

        return loadout;
    }

    /// <summary>整体替换激活集合；未登记的名单被忽略。返回集合是否发生变化。</summary>
    /// <param name="names">要激活的工具名。</param>
    public bool SetActiveTools(IEnumerable<string> names)
    {
        if (names == null)
        {
            return false;
        }

        lock (_gate)
        {
            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in _tools)
            {
                // 隐藏工具永远不可达，不能进入激活集合。
                if (tool.Exposure != ToolExposure.Hidden)
                {
                    registered.Add(tool.Definition.Name);
                }
            }

            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var trimmed = name.Trim();
                    if (registered.Contains(trimmed))
                    {
                        requested.Add(trimmed);
                    }
                }
            }

            if (requested.SetEquals(_activeTools))
            {
                return false;
            }

            _activeTools.Clear();
            foreach (var name in requested)
            {
                _activeTools.Add(name);
            }

            return true;
        }
    }

    /// <summary>
    /// 激活一个 <see cref="ToolExposure.Codemode"/> 或 <see cref="ToolExposure.Deferred"/> 工具。
    /// 直接曝光与仅模型工具登记即激活；隐藏工具不可达，两者调用本方法都无副作用。
    /// </summary>
    /// <param name="name">工具名。</param>
    /// <returns>激活集合是否发生变化。</returns>
    public bool ActivateTool(string name) => ChangeActive(name, activate: true);

    /// <summary>停用一个 <see cref="ToolExposure.Codemode"/> 或 <see cref="ToolExposure.Deferred"/> 工具。返回集合是否发生变化。</summary>
    /// <param name="name">工具名。</param>
    public bool DeactivateTool(string name) => ChangeActive(name, activate: false);

    /// <summary>判断工具是否在激活集合中（内置编排工具恒为激活）。</summary>
    /// <param name="name">工具名。</param>
    public bool IsActiveTool(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var tool = TryGetTool(name);
        return tool != null && (IsBuiltIn(tool) || SnapshotActive().Contains(tool.Definition.Name));
    }

    /// <summary>按名称或别名查找命名空间；<c>my-docs</c> 与 <c>my_docs</c> 等价。未登记时返回 null。</summary>
    /// <param name="name">命名空间名或别名。</param>
    public ToolNamespace? FindNamespace(string? name)
    {
        var canonical = ToolNames.Canonicalize(name);
        if (canonical.Length == 0)
        {
            return null;
        }

        NamespaceRegistration[] snapshot;
        lock (_gate)
        {
            snapshot = _namespaces.ToArray();
        }

        for (var index = snapshot.Length - 1; index >= 0; index--)
        {
            if (ToolNames.Canonicalize(snapshot[index].Value.Name) == canonical)
            {
                return snapshot[index].Value;
            }
        }

        for (var index = snapshot.Length - 1; index >= 0; index--)
        {
            if (snapshot[index].Value.Aliases.Any(alias => ToolNames.Canonicalize(alias) == canonical))
            {
                return snapshot[index].Value;
            }
        }

        return null;
    }

    /// <summary>获取可调用工具按命名空间的分组（命名空间按名称排序，未分组工具排在最后）。</summary>
    public IReadOnlyList<ToolGroup> ToolGroups()
    {
        var callable = BuildLoadout().Callable;
        var groups = new Dictionary<string, List<ExtensionTool>>(StringComparer.Ordinal);
        var unnamed = new List<ExtensionTool>();

        foreach (var tool in callable)
        {
            if (string.IsNullOrWhiteSpace(tool.Namespace))
            {
                unnamed.Add(tool);
                continue;
            }

            var canonical = ToolNames.Canonicalize(tool.Namespace);
            if (!groups.TryGetValue(canonical, out var bucket))
            {
                bucket = new List<ExtensionTool>();
                groups[canonical] = bucket;
            }

            bucket.Add(tool);
        }

        var result = new List<ToolGroup>(groups.Count + 1);
        foreach (var entry in groups.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            result.Add(new ToolGroup
            {
                Namespace = FindNamespace(entry.Key) ?? new ToolNamespace { Name = entry.Value[0].Namespace! },
                Tools = entry.Value
            });
        }

        if (unnamed.Count > 0)
        {
            result.Add(new ToolGroup { Tools = unnamed });
        }

        return result;
    }

    /// <summary>获取工具声明的可达范围（<see cref="ExtensionTool.ReachesTools"/>）；未声明时返回 null。</summary>
    /// <param name="toolName">工具名。</param>
    public ToolReach? ToolReachOf(string? toolName) => TryGetTool(toolName)?.ReachesTools;

    /// <summary>渲染工具调用/结果。没有渲染器认领时返回 null，由宿主回退到默认显示。</summary>
    public string? RenderTool(ToolRenderContext context)
    {
        Func<ToolRenderContext, string?>[] renderers;
        lock (_gate)
        {
            renderers = _toolRenderers.ToArray();
        }

        foreach (var renderer in renderers)
        {
            try
            {
                var text = renderer(context);
                if (text != null)
                {
                    return text;
                }
            }
            catch (Exception)
            {
                // 渲染失败不应影响对话。
            }
        }

        return null;
    }

    /// <summary>解析虚拟模型路由；未注册时返回 null。</summary>
    public VirtualModelRegistration? ResolveVirtualModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        lock (_gate)
        {
            return _virtualModels.FirstOrDefault(item =>
                string.Equals(item.VirtualModelName, modelName!.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>关闭扩展注册的 MCP 服务器连接（幂等）。</summary>
    public async Task CloseMcpServersAsync()
    {
        Func<Task>[] cleanup;
        lock (_gate)
        {
            cleanup = _mcpCleanup.Concat(_mcpCleanupByOwner.Values.SelectMany(actions => actions)).ToArray();
            _mcpCleanup.Clear();
            _mcpCleanupByOwner.Clear();
            _mcpServers.Clear();
        }

        for (var index = cleanup.Length - 1; index >= 0; index--)
        {
            try
            {
                await cleanup[index]().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 单个关闭失败不影响其它服务器。
            }
        }
    }

    /// <summary>激活一个扩展。激活过程抛出的异常会被捕获并记录，不影响其它扩展。</summary>
    public IReadOnlyList<PluginDiagnostic> Activate(IExtension extension)
    {
        if (extension == null)
        {
            throw new ArgumentNullException(nameof(extension));
        }

        var diagnostics = new List<PluginDiagnostic>();
        lock (_gate)
        {
            if (_activationState.ContainsKey(extension.Name))
            {
                diagnostics.Add(new PluginDiagnostic
                {
                    Kind = PluginDiagnosticKind.Warning,
                    PluginName = extension.Name,
                    Message = "Extension is already activated; the duplicate activation was ignored."
                });
                return diagnostics;
            }

            _activationState[extension.Name] = true;
            _extensions.Add(extension);
        }

        try
        {
            extension.Activate(new ExtensionApi(this, extension.Name));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new PluginDiagnostic
            {
                Kind = PluginDiagnosticKind.Error,
                PluginName = extension.Name,
                Message = "Extension failed to activate: " + ex.Message
            });
            Rollback(extension.Name);
        }

        return diagnostics;
    }

    /// <summary>卸载指定扩展并移除其注册的命令、工具、事件和 MCP 声明。</summary>
    /// <param name="name">扩展名称。</param>
    public void UnloadExtension(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An extension name is required.", nameof(name));
        Rollback(name.Trim());
    }

    /// <summary>
    /// 由宿主登记一个外部来源的工具（例如扩展注册的 MCP 服务器）。
    /// 与扩展工具共用同一张表、激活集合与曝光规则。
    /// </summary>
    public void RegisterExternalTool(
        ToolDefinition definition,
        Func<ToolCall, CancellationToken, Task<string>> execute,
        string ownerName,
        ToolExposure exposure = ToolExposure.Direct,
        string? @namespace = null,
        ToolReach? reachesTools = null)
    {
        if (definition == null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        if (execute == null)
        {
            throw new ArgumentNullException(nameof(execute));
        }

        var tool = new ExtensionTool
        {
            Definition = definition,
            ExtensionName = ownerName,
            Exposure = exposure,
            Execute = (call, _, token) => execute(call, token),
            Namespace = @namespace,
            ReachesTools = reachesTools
        };

        AddTool(tool);
    }

    /// <summary>
    /// 由宿主登记一个技能（例如扩展包随包携带的 <c>SKILL.md</c>）。
    /// 与扩展技能共用同一张表；同名后登记者胜出。
    /// </summary>
    /// <param name="skill">技能定义。</param>
    /// <param name="ownerName">归属名，伴随 <see cref="UnregisterSkills"/> 或扩展卸载一起回滚。</param>
    public void RegisterSkill(AiTools.Skills.SkillDefinition skill, string ownerName)
        => RegisterSkill(skill, ownerName, isPackage: false);

    /// <summary>
    /// 用当前包技能整体替换此前的包技能（同名技能与其它来源的同名项）：先移除全部包技能，
    /// 再登记 <paramref name="skills"/>。工作区扫描、单个包重载或热重载后调用。
    /// 内容与当前包技能完全一致时不改动注册表，也不递增 <see cref="SkillsVersion"/>。
    /// </summary>
    /// <param name="skills">当前所有被工作区接受的包携带的技能。</param>
    /// <returns>技能表是否发生变化。</returns>
    public bool ReplacePackageSkills(IEnumerable<(string PackageName, AiTools.Skills.SkillDefinition Skill)> skills)
    {
        if (skills == null)
        {
            throw new ArgumentNullException(nameof(skills));
        }

        var incoming = skills
            .Where(item => item.Skill != null && !string.IsNullOrWhiteSpace(item.Skill.Name))
            .ToArray();

        var removedAny = false;
        lock (_gate)
        {
            var existing = _skills.Where(item => item.IsPackage).ToArray();
            if (existing.Length == incoming.Length && SamePackageSkills(existing, incoming))
            {
                return false;
            }

            removedAny = _skills.RemoveAll(item => item.IsPackage) > 0;
        }

        foreach (var item in incoming)
        {
            RegisterSkill(item.Skill, item.PackageName, isPackage: true);
        }

        // 没有新技能但移除了旧包技能时，技能表同样发生了变化。
        if (incoming.Length == 0 && removedAny)
        {
            Interlocked.Increment(ref _skillsVersion);
        }

        return true;
    }

    /// <summary>比较已登记的包技能与即将登记的集合是否等价（名称、归属与内容）。</summary>
    private static bool SamePackageSkills(
        IReadOnlyList<SkillRegistration> existing,
        IReadOnlyList<(string PackageName, AiTools.Skills.SkillDefinition Skill)> incoming)
    {
        var byName = existing.ToDictionary(
            item => item.Skill.Name,
            item => item,
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in incoming)
        {
            if (!byName.TryGetValue(item.Skill.Name, out var current))
            {
                return false;
            }

            if (!string.Equals(current.Owner, item.PackageName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current.Skill.Description, item.Skill.Description, StringComparison.Ordinal) ||
                !string.Equals(current.Skill.Instructions, item.Skill.Instructions, StringComparison.Ordinal) ||
                !string.Equals(current.Skill.DirectoryPath, item.Skill.DirectoryPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void RegisterSkill(AiTools.Skills.SkillDefinition skill, string ownerName, bool isPackage)
    {
        if (skill == null)
        {
            throw new ArgumentNullException(nameof(skill));
        }

        if (string.IsNullOrWhiteSpace(skill.Name))
        {
            throw new ArgumentException("A skill name is required.", nameof(skill));
        }

        if (string.IsNullOrWhiteSpace(skill.Description))
        {
            throw new ArgumentException("A skill description is required.", nameof(skill));
        }

        var registration = new SkillRegistration(
            string.IsNullOrWhiteSpace(ownerName) ? skill.Name : ownerName,
            skill,
            isPackage);
        lock (_gate)
        {
            // 同名技能后注册者覆盖先注册者（跨来源亦然），与工具、命令的覆盖规则一致。
            _skills.RemoveAll(item => string.Equals(item.Skill.Name, skill.Name, StringComparison.OrdinalIgnoreCase));
            _skills.Add(registration);
        }

        // 新增与替换都会改变技能内容，宿主据此重建目录。
        Interlocked.Increment(ref _skillsVersion);
    }

    /// <summary>移除某个归属登记的非包技能（包技能由 <see cref="ReplacePackageSkills"/> 统一替换）。</summary>
    /// <param name="ownerName">归属名。</param>
    /// <returns>是否移除了至少一个技能。</returns>
    public bool UnregisterSkills(string ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName))
        {
            return false;
        }

        int removed;
        lock (_gate)
        {
            removed = _skills.RemoveAll(item =>
                !item.IsPackage &&
                string.Equals(item.Owner, ownerName, StringComparison.OrdinalIgnoreCase));
        }

        if (removed == 0)
        {
            return false;
        }

        Interlocked.Increment(ref _skillsVersion);
        return true;
    }

    /// <summary>清空所有注册（扩展、命令、工具、事件、provider、虚拟模型、MCP、激活集合、命名空间、装载准备器）。用于热重载。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _extensions.Clear();
            _commands.Clear();
            _tools.Clear();
            _subscriptions.Clear();
            _providers.Clear();
            _virtualModels.Clear();
            _mcpServers.Clear();
            _mcpCleanupByOwner.Clear();
            _toolRenderersByOwner.Clear();
            _toolRenderers.Clear();
            _activationState.Clear();
            _activeTools.Clear();
            _namespaces.Clear();
            _skills.Clear();
            _loadoutPreparers.Clear();
        }
        Interlocked.Increment(ref _skillsVersion);
        CodemodeTool.Register(this, new ExtensionApi(this, RuntimeOwner));
    }

    /// <summary>按名称查找命令。</summary>
    public ExtensionCommand? TryGetCommand(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Commands.FirstOrDefault(command => string.Equals(command.Name, name!.Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按名称查找工具（包含隐藏工具与内置编排工具，供程序化调用）。</summary>
    public ExtensionTool? TryGetTool(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name!.Trim();
        ExtensionTool[] registered;
        lock (_gate)
        {
            registered = _tools.ToArray();
        }

        for (var index = registered.Length - 1; index >= 0; index--)
        {
            if (string.Equals(registered[index].Definition.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return registered[index];
            }
        }

        foreach (var builtIn in BuiltInTools(registered))
        {
            if (string.Equals(builtIn.Definition.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return builtIn;
            }
        }

        return null;
    }

    /// <summary>分发通知类事件；处理器异常被捕获并忽略，避免打断对话。</summary>
    public async Task PublishAsync(ExtensionEvent @event, object payload, CommandContext context, CancellationToken cancellationToken = default)
    {
        foreach (var subscription in Snapshot(@event, context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await subscription.Handler(payload, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 通知型事件失败不应打断对话。
            }
        }
    }

    /// <summary>
    /// 分发转换/拦截类事件：按注册顺序执行，处理器可返回替换对象；
    /// 处理器异常按 fail-closed 处理并返回 <paramref name="failClosed"/> 的结果。
    /// </summary>
    public async Task<object?> DispatchAsync(
        ExtensionEvent @event,
        object payload,
        CommandContext context,
        object? failClosed = null,
        CancellationToken cancellationToken = default)
    {
        var current = payload;
        foreach (var subscription in Snapshot(@event, context))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var replacement = await subscription.Handler(current, context, cancellationToken).ConfigureAwait(false);
                if (replacement != null)
                {
                    current = replacement;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return failClosed;
            }
        }

        return current;
    }

    /// <summary>判断某个事件是否至少有一个订阅者。</summary>
    public bool HasHandlers(ExtensionEvent @event)
    {
        lock (_gate)
        {
            return _subscriptions.Any(subscription => subscription.Event == @event);
        }
    }

    /// <summary>
    /// 执行工具本体，不再分发 <c>tool_call</c>/<c>tool_result</c> 事件。
    /// 供宿主已经自己分发过事件时使用（例如 WPF 对话页对所有工具统一拦截）。
    /// </summary>
    public async Task<ExtensionToolResult> InvokeToolAsync(
        string name,
        string argumentsJson,
        ToolContext toolContext,
        CancellationToken cancellationToken = default)
    {
        var tool = TryGetTool(name);
        if (tool == null)
        {
            return new ExtensionToolResult
            {
                ToolName = name,
                Text = "{\"error\":\"Unknown tool: " + name + "\"}",
                IsError = true,
                NotFound = true
            };
        }

        if (tool.Exposure == ToolExposure.Hidden)
        {
            return new ExtensionToolResult
            {
                ToolName = name,
                Text = "{\"error\":\"Tool is not reachable: " + name + "\"}",
                IsError = true
            };
        }

        // 模型侧只能调用激活集合中的工具（未激活的 Direct/Deferred 不在声明面，也不该被执行）。
        if (!IsActiveTool(name))
        {
            return new ExtensionToolResult
            {
                ToolName = name,
                Text = "{\"error\":\"Tool is not active: " + name + "\"}",
                IsError = true
            };
        }

        try
        {
            var text = await tool.Execute(
                new ToolCall("inv-" + Guid.NewGuid().ToString("N"), name, string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson),
                toolContext,
                cancellationToken).ConfigureAwait(false);

            return new ExtensionToolResult { ToolName = name, Text = text ?? string.Empty };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExtensionToolResult
            {
                ToolName = name,
                Text = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}",
                IsError = true
            };
        }
    }

    /// <summary>
    /// 调用工具。动态调用（扩展调用扩展）与模型调用走同一条路径：
    /// 都会经过 <c>tool_call</c> 与 <c>tool_result</c> 处理器。
    /// </summary>
    /// <param name="name">工具名。</param>
    /// <param name="argumentsJson">参数 JSON。</param>
    /// <param name="context">命令上下文（用于事件分发）。</param>
    /// <param name="parentToolName">发起调用的工具名；用于检查父工具声明的可达范围。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ExtensionToolResult> ExecuteToolAsync(
        string name,
        string argumentsJson,
        CommandContext context,
        string? parentToolName = null,
        CancellationToken cancellationToken = default)
    {
        var tool = TryGetTool(name);
        if (tool == null)
        {
            return new ExtensionToolResult { ToolName = name, Text = "{\"error\":\"Unknown tool: " + name + "\"}", IsError = true, NotFound = true };
        }

        // 可调用面由曝光级别与激活集合决定：仅模型/隐藏工具不可调用，停用的 Direct 工具也不可达。
        if (!IsCallable(tool, SnapshotActive()))
        {
            return new ExtensionToolResult
            {
                ToolName = name,
                Text = "{\"error\":\"Tool is not callable: " + name + "\"}",
                IsError = true
            };
        }

        if (!string.IsNullOrWhiteSpace(parentToolName))
        {
            var reach = ToolReachOf(parentToolName);
            if (reach != null && !Allows(reach, tool))
            {
                return new ExtensionToolResult
                {
                    ToolName = name,
                    Text = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(
                        $"Tool '{name}' is outside the reach of '{parentToolName}'.") + "}",
                    IsError = true,
                    Blocked = true
                };
            }
        }

        var call = new ToolCall("ext-" + Guid.NewGuid().ToString("N"), name, string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var callEvent = new ToolCallEvent { ToolName = name, ArgumentsJson = call.ArgumentsJson };
        var dispatched = await DispatchAsync(ExtensionEvent.ToolCall, callEvent, context, failClosed: new ToolCallEvent { ToolName = name, BlockReason = "tool_call handler failed" }, cancellationToken).ConfigureAwait(false);

        var resolved = dispatched as ToolCallEvent ?? callEvent;
        if (!string.IsNullOrEmpty(resolved.BlockReason))
        {
            return new ExtensionToolResult { ToolName = name, Text = resolved.BlockReason!, IsError = true, Blocked = true };
        }

        string text;
        var isError = false;
        try
        {
            var toolContext = new ToolContext
            {
                Runtime = this,
                State = State,
                ParentToolName = parentToolName,
                SessionId = context.SessionId,
                WorkingDirectory = context.WorkingDirectory,
                Ui = context.Ui,
                EntryStore = context.EntryStore,
                CallContext = context,
                CallerToolName = name
            };

            text = await tool.Execute(
                new ToolCall(call.Id, name, resolved.ArgumentsJson),
                toolContext,
                cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            text = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}";
            isError = true;
        }

        var resultEvent = new ToolResultEvent { ToolName = name, Text = text, IsError = isError };
        var transformed = await DispatchAsync(ExtensionEvent.ToolResult, resultEvent, context, failClosed: resultEvent, cancellationToken).ConfigureAwait(false);
        var final = transformed as ToolResultEvent ?? resultEvent;

        return new ExtensionToolResult
        {
            ToolName = name,
            Text = final.Text,
            IsError = isError || final.IsError
        };
    }

    /// <summary>供命令上下文使用的简化工具调用。</summary>
    public async Task<string> ExecuteToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        var context = new CommandContext
        {
            Runtime = this,
            State = State,
            WorkingDirectory = Environment.CurrentDirectory
        };

        var result = await ExecuteToolAsync(name, argumentsJson, context, parentToolName: null, cancellationToken).ConfigureAwait(false);
        return result.Text;
    }

    /// <summary>判断词集是否命中工具（工具名或所属命名空间的名称/别名包含某个词）。</summary>
    internal bool Allows(ToolReach reach, ExtensionTool tool)
    {
        if (reach.IsAll)
        {
            return true;
        }

        var namespaceValue = FindNamespace(tool.Namespace);
        var names = new List<string> { ToolNames.Canonicalize(tool.Definition.Name) };
        if (!string.IsNullOrWhiteSpace(tool.Namespace))
        {
            names.Add(ToolNames.Canonicalize(tool.Namespace));
        }

        if (namespaceValue != null)
        {
            names.Add(ToolNames.Canonicalize(namespaceValue.Name));
            names.AddRange(namespaceValue.Aliases.Select(ToolNames.Canonicalize));
        }

        foreach (var word in reach.Items)
        {
            var canonical = ToolNames.Canonicalize(word);
            if (canonical.Length == 0)
            {
                continue;
            }

            foreach (var name in names)
            {
                if (name.Length > 0 && name.Contains(canonical))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool ChangeActive(string name, bool activate)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        lock (_gate)
        {
            // 同名工具后注册者覆盖先注册者；用快照找到实际生效的定义。
            ExtensionTool? tool = null;
            for (var index = _tools.Count - 1; index >= 0; index--)
            {
                if (string.Equals(_tools[index].Definition.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    tool = _tools[index];
                    break;
                }
            }

            if (tool == null ||
                tool.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly or ToolExposure.Hidden)
            {
                return false;
            }

            return activate ? _activeTools.Add(tool.Definition.Name) : _activeTools.Remove(tool.Definition.Name);
        }
    }

    private void AddTool(ExtensionTool tool)
    {
        lock (_gate)
        {
            _tools.Add(tool);
            // 直接曝光与仅模型工具登记即激活；其余等显式激活。
            if (tool.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly)
            {
                _activeTools.Add(tool.Definition.Name);
            }
        }
    }

    private void Rollback(string ownerName)
    {
        Func<Task>[] cleanup;
        var removedSkills = false;
        lock (_gate)
        {
            var removedTools = _tools
                .Where(tool => string.Equals(tool.ExtensionName, ownerName, StringComparison.OrdinalIgnoreCase))
                .Select(tool => tool.Definition.Name)
                .ToArray();

            _extensions.RemoveAll(extension => string.Equals(extension.Name, ownerName, StringComparison.OrdinalIgnoreCase));
            _commands.RemoveAll(command => string.Equals(command.ExtensionName, ownerName, StringComparison.OrdinalIgnoreCase));
            _tools.RemoveAll(tool => string.Equals(tool.ExtensionName, ownerName, StringComparison.OrdinalIgnoreCase));
            foreach (var name in removedTools)
            {
                _activeTools.Remove(name);
            }

            _subscriptions.RemoveAll(subscription => string.Equals(subscription.ExtensionName, ownerName, StringComparison.OrdinalIgnoreCase));
            _providers.RemoveAll(item => string.Equals(item.Name, ownerName, StringComparison.OrdinalIgnoreCase));
            _virtualModels.RemoveAll(item => string.Equals(item.Provider, ownerName, StringComparison.OrdinalIgnoreCase));
            _mcpServers.RemoveAll(item => string.Equals(item.PluginName, ownerName, StringComparison.OrdinalIgnoreCase));
            _namespaces.RemoveAll(item => string.Equals(item.Owner, ownerName, StringComparison.OrdinalIgnoreCase));
            _loadoutPreparers.RemoveAll(item => string.Equals(item.Owner, ownerName, StringComparison.OrdinalIgnoreCase));
            // 包技能由工作区整体替换，不随单个扩展卸载回滚（即便扩展名与包名相同）。
            if (_skills.RemoveAll(item =>
                    !item.IsPackage &&
                    string.Equals(item.Owner, ownerName, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                removedSkills = true;
            }
            _activationState.Remove(ownerName);
            if (_toolRenderersByOwner.TryGetValue(ownerName, out var renderers))
            {
                foreach (var renderer in renderers)
                {
                    _toolRenderers.Remove(renderer);
                }
                _toolRenderersByOwner.Remove(ownerName);
            }
            cleanup = _mcpCleanupByOwner.TryGetValue(ownerName, out var actions) ? actions.ToArray() : Array.Empty<Func<Task>>();
            _mcpCleanupByOwner.Remove(ownerName);
        }

        if (removedSkills)
        {
            Interlocked.Increment(ref _skillsVersion);
        }

        for (var index = cleanup.Length - 1; index >= 0; index--)
        {
            try
            {
                cleanup[index]().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // One owner's MCP cleanup must not prevent other registrations from being released.
            }
        }
    }

    private HashSet<string> SnapshotActive()
    {
        lock (_gate)
        {
            return new HashSet<string>(_activeTools, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>去重后的扩展工具表（同名后注册者覆盖先注册者），调用方必须持有 <see cref="_gate"/>。</summary>
    private ExtensionTool[] Deduplicate()
        => _tools
            .GroupBy(tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(tool => tool.Definition.Name, StringComparer.Ordinal)
            .ToArray();

    private static bool IsBuiltIn(ExtensionTool tool)
        => string.Equals(tool.ExtensionName, RuntimeOwner, StringComparison.Ordinal);

    /// <summary>
    /// 工具是否可被 <see cref="ExecuteToolAsync(string, string, CommandContext, string?, CancellationToken)"/> 调用：
    /// <c>Direct</c> 需在激活集合中（停用后不可达），<c>Codemode</c>/<c>Deferred</c> 登记即可调用，
    /// <c>ModelOnly</c> 与 <c>Hidden</c> 永不可调用。
    /// </summary>
    private static bool IsCallable(ExtensionTool tool, ICollection<string> active)
        => tool.Exposure switch
        {
            ToolExposure.Direct => active.Contains(tool.Definition.Name),
            ToolExposure.Codemode => true,
            ToolExposure.Deferred => true,
            _ => false
        };

    /// <summary>按当前登记计算内置编排工具：有 codemode/deferred 工具时提供 <c>tool_search</c>，有可达声明或 codemode 工具时提供 <c>codemode</c>。</summary>
    private IReadOnlyList<ExtensionTool> BuiltInTools(IEnumerable<ExtensionTool> registered)
    {
        var needsDiscovery = false;
        var needsCodemode = false;
        foreach (var tool in registered)
        {
            if (tool.Exposure is ToolExposure.Codemode or ToolExposure.Deferred)
            {
                needsDiscovery = true;
            }

            if (tool.Exposure == ToolExposure.Codemode || tool.ReachesTools != null)
            {
                needsCodemode = true;
            }
        }

        var builtIns = new List<ExtensionTool>(2);
        if (needsDiscovery)
        {
            builtIns.Add(_toolSearchTool);
        }

        if (needsCodemode)
        {
            builtIns.Add(_codemodeTool);
        }

        return builtIns;
    }

    /// <summary>
    /// 计算未应用准备器的装载结果。先登记的工具表 + 内置编排工具，
    /// 再按激活集合与曝光级别划分声明/可调用/全部。
    /// </summary>
    private ToolLoadout ComposeLoadout()
    {
        ExtensionTool[] registered;
        HashSet<string> active;
        lock (_gate)
        {
            registered = _tools
                .GroupBy(tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .OrderBy(tool => tool.Definition.Name, StringComparer.Ordinal)
                .ToArray();
            active = new HashSet<string>(_activeTools, StringComparer.OrdinalIgnoreCase);
        }

        var all = new List<ExtensionTool>(registered.Length + 2);
        all.AddRange(registered);
        foreach (var builtIn in BuiltInTools(registered))
        {
            if (!registered.Any(tool => string.Equals(tool.Definition.Name, builtIn.Definition.Name, StringComparison.OrdinalIgnoreCase)))
            {
                all.Add(builtIn);
            }
        }

        all.Sort((left, right) => string.CompareOrdinal(left.Definition.Name, right.Definition.Name));

        var declared = new List<ExtensionTool>();
        var callable = new List<ExtensionTool>();
        foreach (var tool in all)
        {
            if (IsCallable(tool, active))
            {
                callable.Add(tool);
            }

            if ((IsBuiltIn(tool) || active.Contains(tool.Definition.Name)) && tool.Exposure != ToolExposure.Hidden)
            {
                // 激活的 Deferred/Codemode 一旦激活即声明给模型；未激活则不声明。
                declared.Add(tool);
            }
        }

        return new ToolLoadout
        {
            Declared = declared.ToArray(),
            Callable = callable.ToArray(),
            All = all.ToArray()
        };
    }

    private IReadOnlyList<EventSubscription> Snapshot(ExtensionEvent @event, CommandContext context)
    {
        lock (_gate)
        {
            return _subscriptions.Where(subscription => subscription.Event == @event).ToArray();
        }
    }

    /// <summary>扩展注册 API 的实现。</summary>
    private sealed class ExtensionApi : IExtensionApi
    {
        private readonly ExtensionRuntime _runtime;

        public ExtensionApi(ExtensionRuntime runtime, string name)
        {
            _runtime = runtime;
            Name = name;
        }

        public string Name { get; }

        public ExtensionStateStore State => _runtime.State;

        public void RegisterCommand(
            string name,
            string description,
            Func<CommandContext, CommandArguments, CancellationToken, Task<CommandResult>> handler,
            string? argumentHint = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A command name is required.", nameof(name));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var command = new ExtensionCommand
            {
                Name = name.Trim().TrimStart('/'),
                Description = description ?? string.Empty,
                ArgumentHint = argumentHint,
                ExtensionName = Name,
                Handler = handler
            };

            lock (_runtime._gate)
            {
                _runtime._commands.Add(command);
            }
        }

        public void RegisterTool(
            ToolDefinition definition,
            Func<ToolCall, ToolContext, CancellationToken, Task<string>> execute,
            ToolExposure exposure = ToolExposure.Direct,
            bool readOnlyHint = false,
            string? @namespace = null,
            ToolReach? reachesTools = null)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (execute == null)
            {
                throw new ArgumentNullException(nameof(execute));
            }

            var tool = new ExtensionTool
            {
                Definition = definition,
                ExtensionName = Name,
                Exposure = exposure,
                Execute = execute,
                ReadOnlyHint = readOnlyHint,
                Namespace = string.IsNullOrWhiteSpace(@namespace) ? null : @namespace!.Trim(),
                ReachesTools = reachesTools
            };

            _runtime.AddTool(tool);
        }

        public void RegisterNamespace(ToolNamespace @namespace)
        {
            if (@namespace == null)
            {
                throw new ArgumentNullException(nameof(@namespace));
            }

            if (string.IsNullOrWhiteSpace(@namespace.Name))
            {
                throw new ArgumentException("A namespace name is required.", nameof(@namespace));
            }

            var canonical = ToolNames.Canonicalize(@namespace.Name);
            lock (_runtime._gate)
            {
                _runtime._namespaces.RemoveAll(item =>
                    string.Equals(item.Owner, Name, StringComparison.OrdinalIgnoreCase) &&
                    ToolNames.Canonicalize(item.Value.Name) == canonical);
                _runtime._namespaces.Add(new NamespaceRegistration(Name, @namespace));
            }
        }

        public void RegisterSkill(AiTools.Skills.SkillDefinition skill)
        {
            if (skill == null)
            {
                throw new ArgumentNullException(nameof(skill));
            }

            _runtime.RegisterSkill(skill, Name);
        }

        public void RegisterLoadoutPreparer(Func<ToolLoadout, ToolLoadout> preparer)
        {
            if (preparer == null)
            {
                throw new ArgumentNullException(nameof(preparer));
            }

            lock (_runtime._gate)
            {
                _runtime._loadoutPreparers.Add(new LoadoutPreparerRegistration(Name, preparer));
            }
        }

        public Action On(ExtensionEvent @event, Func<object, CommandContext, CancellationToken, Task> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var subscription = new EventSubscription(Name, @event, async (payload, context, token) =>
            {
                await handler(payload, context, token).ConfigureAwait(false);
                return null;
            });

            lock (_runtime._gate)
            {
                _runtime._subscriptions.Add(subscription);
            }

            return () =>
            {
                lock (_runtime._gate)
                {
                    _runtime._subscriptions.Remove(subscription);
                }
            };
        }

        public void RegisterToolRenderer(Func<ToolRenderContext, string?> renderer)
        {
            if (renderer == null)
            {
                throw new ArgumentNullException(nameof(renderer));
            }

            lock (_runtime._gate)
            {
                _runtime._toolRenderers.Add(renderer);
                if (!_runtime._toolRenderersByOwner.TryGetValue(Name, out var renderers))
                {
                    renderers = new List<Func<ToolRenderContext, string?>>();
                    _runtime._toolRenderersByOwner[Name] = renderers;
                }
                renderers.Add(renderer);
            }
        }

        public void RegisterProvider(ExtensionProviderRegistration registration)
        {
            if (registration == null)
            {
                throw new ArgumentNullException(nameof(registration));
            }

            if (string.IsNullOrWhiteSpace(registration.Name))
            {
                throw new ArgumentException("A provider name is required.", nameof(registration));
            }

            lock (_runtime._gate)
            {
                _runtime._providers.RemoveAll(item => string.Equals(item.Name, registration.Name, StringComparison.OrdinalIgnoreCase));
                _runtime._providers.Add(registration);
            }
        }

        public Func<Task> RegisterMcpServer(PluginMcpServer server)
        {
            if (server == null)
            {
                throw new ArgumentNullException(nameof(server));
            }

            if (string.IsNullOrWhiteSpace(server.Name))
            {
                throw new ArgumentException("An MCP server name is required.", nameof(server));
            }

            // 扩展注册的服务器以扩展名为命名空间，避免与插件服务器撞名。
            var scoped = new PluginMcpServer
            {
                Name = server.Name,
                QualifiedName = Name + ":" + server.Name,
                PluginName = Name,
                Transport = server.Transport,
                Command = server.Command,
                Arguments = server.Arguments,
                Environment = server.Environment,
                Url = server.Url,
                Headers = server.Headers,
                SourcePath = server.SourcePath
            };

            lock (_runtime._gate)
            {
                _runtime._mcpServers.RemoveAll(item =>
                    string.Equals(item.QualifiedName, scoped.QualifiedName, StringComparison.OrdinalIgnoreCase));
                _runtime._mcpServers.Add(scoped);
            }

            Func<Task> cleanup = () =>
            {
                lock (_runtime._gate)
                {
                    _runtime._mcpServers.RemoveAll(item =>
                        string.Equals(item.QualifiedName, scoped.QualifiedName, StringComparison.OrdinalIgnoreCase));
                }
                return Task.CompletedTask;
            };
            lock (_runtime._gate)
            {
                if (!_runtime._mcpCleanupByOwner.TryGetValue(Name, out var cleanupActions))
                {
                    cleanupActions = new List<Func<Task>>();
                    _runtime._mcpCleanupByOwner[Name] = cleanupActions;
                }
                cleanupActions.Add(cleanup);
            }
            return cleanup;
        }

        public void RegisterVirtualModel(VirtualModelRegistration registration)
        {
            if (registration == null)
            {
                throw new ArgumentNullException(nameof(registration));
            }

            if (string.IsNullOrWhiteSpace(registration.VirtualModelName) || string.IsNullOrWhiteSpace(registration.TargetModel))
            {
                throw new ArgumentException("Virtual models need both a name and a target model.", nameof(registration));
            }

            lock (_runtime._gate)
            {
                _runtime._virtualModels.RemoveAll(item =>
                    string.Equals(item.VirtualModelName, registration.VirtualModelName, StringComparison.OrdinalIgnoreCase));
                _runtime._virtualModels.Add(new VirtualModelRegistration
                {
                    VirtualModelName = registration.VirtualModelName,
                    Provider = string.IsNullOrWhiteSpace(registration.Provider) ? Name : registration.Provider,
                    TargetModel = registration.TargetModel,
                    Rewrite = registration.Rewrite,
                    Description = registration.Description
                });
            }
        }

        public Action On<TEvent>(ExtensionEvent @event, Func<TEvent, CommandContext, CancellationToken, Task<TEvent?>> handler)
            where TEvent : class
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var subscription = new EventSubscription(Name, @event, async (payload, context, token) =>
            {
                if (payload is not TEvent typed)
                {
                    return null;
                }

                var replacement = await handler(typed, context, token).ConfigureAwait(false);
                return replacement;
            });

            lock (_runtime._gate)
            {
                _runtime._subscriptions.Add(subscription);
            }

            return () =>
            {
                lock (_runtime._gate)
                {
                    _runtime._subscriptions.Remove(subscription);
                }
            };
        }
    }
}

/// <summary>一次工具调用的结果。</summary>
public sealed class ExtensionToolResult
{
    /// <summary>获取工具名。</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>获取结果文本（失败时为错误描述）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>获取是否失败。</summary>
    public bool IsError { get; init; }

    /// <summary>获取是否被 <c>tool_call</c> 处理器拦截（或超出父工具的可达范围）。</summary>
    public bool Blocked { get; init; }

    /// <summary>获取工具是否不存在。</summary>
    public bool NotFound { get; init; }
}
