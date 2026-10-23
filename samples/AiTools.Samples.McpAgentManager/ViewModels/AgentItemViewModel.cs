using System;
using AiTools.Samples.McpAgents;

namespace AiTools.Samples.McpAgentManager.ViewModels;

/// <summary>左栏中单个智能体卡片的 ViewModel。</summary>
public sealed class AgentItemViewModel : ObservableObject
{
    private readonly string _defaultConfigPath;
    private AgentAdapter _adapter;
    private AgentProbe _probe;
    private AgentMcpState _state;
    private bool _isEditingPath;
    private string _pathEditor;

    /// <summary>根据探测结果和配置快照创建智能体卡片。</summary>
    /// <param name="adapter">解析后的智能体适配器（包含用户路径覆盖）。</param>
    /// <param name="probe">适配器的探测结果。</param>
    /// <param name="state">读取到的适配器配置快照。</param>
    /// <param name="defaultConfigPath">最初检测到的配置路径，用于“恢复默认”。</param>
    public AgentItemViewModel(AgentAdapter adapter, AgentProbe probe, AgentMcpState state, string? defaultConfigPath = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _defaultConfigPath = string.IsNullOrWhiteSpace(defaultConfigPath) ? adapter.ConfigPath : defaultConfigPath;
        _pathEditor = adapter.ConfigPath;
    }

    /// <summary>获取稳定的智能体标识符。</summary>
    public string Id => _adapter.Id;

    /// <summary>获取当前适配器（包含用户路径覆盖）。</summary>
    public AgentAdapter Adapter => _adapter;

    /// <summary>获取当前探测结果。</summary>
    public AgentProbe Probe => _probe;

    /// <summary>获取当前配置快照。</summary>
    public AgentMcpState State => _state;

    /// <summary>获取显示名称。</summary>
    public string DisplayName => _adapter.DisplayName;

    /// <summary>获取解析后的配置路径。</summary>
    public string ConfigPath => _adapter.ConfigPath;

    /// <summary>获取最初检测到的配置路径。</summary>
    public string DefaultConfigPath => _defaultConfigPath;

    /// <summary>获取智能体配置类型。</summary>
    public AgentKind Kind => _adapter.Kind;

    /// <summary>获取智能体配置是否可由本应用写入。</summary>
    public bool Managed => _adapter.Kind != AgentKind.Unsupported;

    /// <summary>获取智能体是否仅支持导出 JSON。</summary>
    public bool IsUnsupported => !Managed;

    /// <summary>获取配置文件当前是否存在。</summary>
    public bool ConfigExists => _probe.ConfigExists;

    /// <summary>获取是否在 PATH 中找到智能体 CLI。</summary>
    public bool CliInstalled => _probe.CliInstalled;

    /// <summary>获取 CLI 徽章文本。</summary>
    public string CliBadgeText => _probe.CliInstalled ? "已检测到 CLI" : "未检测到";

    /// <summary>获取已安装的 MCP 服务器数量，包括预设目录之外的服务器。</summary>
    public int InstalledCount => _state.InstalledServers.Count;

    /// <summary>获取由 <see cref="AgentProbe.GetStatusLabel(int)"/> 生成的状态标签。</summary>
    public string StatusText => _probe.GetStatusLabel(InstalledCount);

    /// <summary>获取配置读取错误（若有）。</summary>
    public string? ErrorText => _state.Error;

    /// <summary>获取上次配置读取是否失败。</summary>
    public bool HasError => !string.IsNullOrEmpty(_state.Error);

    /// <summary>获取或设置内联路径编辑器是否可见。</summary>
    public bool IsEditingPath
    {
        get => _isEditingPath;
        set => SetProperty(ref _isEditingPath, value);
    }

    /// <summary>获取或设置内联路径编辑器中显示的文本。</summary>
    public string PathEditor
    {
        get => _pathEditor;
        set => SetProperty(ref _pathEditor, value);
    }

    /// <summary>返回当前配置中是否存在指定预设 ID。</summary>
    /// <param name="presetId">预设标识符。</param>
    /// <returns>当预设已安装时返回 <see langword="true"/>。</returns>
    public bool HasPreset(string presetId) => _state.Has(presetId);

    /// <summary>显示以当前路径初始化的内联路径编辑器。</summary>
    public void BeginPathEdit()
    {
        PathEditor = ConfigPath;
        IsEditingPath = true;
    }

    /// <summary>隐藏内联路径编辑器并恢复当前路径文本。</summary>
    public void CancelPathEdit()
    {
        PathEditor = ConfigPath;
        IsEditingPath = false;
    }

    /// <summary>应用更新后的适配器、探测结果和配置快照。</summary>
    /// <param name="adapter">解析后的适配器。</param>
    /// <param name="probe">最新探测结果。</param>
    /// <param name="state">最新配置快照。</param>
    public void Apply(AgentAdapter adapter, AgentProbe probe, AgentMcpState state)
    {
        _adapter = adapter;
        _probe = probe;
        _state = state;

        if (!IsEditingPath)
        {
            _pathEditor = adapter.ConfigPath;
            OnPropertyChanged(nameof(PathEditor));
        }

        OnPropertyChanged(nameof(Adapter));
        OnPropertyChanged(nameof(Probe));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ConfigPath));
        OnPropertyChanged(nameof(DefaultConfigPath));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Managed));
        OnPropertyChanged(nameof(IsUnsupported));
        OnPropertyChanged(nameof(ConfigExists));
        OnPropertyChanged(nameof(CliInstalled));
        OnPropertyChanged(nameof(CliBadgeText));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
    }
}
