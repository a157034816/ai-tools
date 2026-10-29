using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AiTools.Samples.McpAgents;

namespace AiTools.Samples.McpAgentManager.ViewModels;

/// <summary>
/// 驱动 MCP 智能体管理器窗口。它会探测本机上的智能体、读取其 MCP 配置，
/// 通过 <see cref="AgentConfigEditor"/> 安装或移除预设，并将每次操作记录到活动日志中。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 300;
    private const string OpenClawAgentId = "openclaw";

    private readonly AgentProbeService _probeService = new();
    private readonly AgentConfigEditor _editor;
    private readonly string _overridesPath;
    private readonly IReadOnlyList<AgentAdapter> _baseAdapters;
    private readonly Dictionary<string, AgentAdapter> _baseAdaptersById;
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    private AgentItemViewModel? _selectedAgent;
    private PresetItemViewModel? _selectedPreset;
    private string _exportPreview = string.Empty;
    private string _exportScopeText = string.Empty;
    private string _statusMessage = "就绪。";
    private string _lastOperationText = "最后操作：无";
    private bool _isBusy;
    private bool _isLogAutoScroll = true;

    /// <summary>创建 ViewModel，解析智能体目录根路径并构建预设列表。</summary>
    public MainViewModel()
    {
        var appDataDirectory = AppPaths.ResolveAppDataDirectory();
        _editor = new AgentConfigEditor(Path.Combine(appDataDirectory, "backups"));
        _overridesPath = Path.Combine(appDataDirectory, "overrides.json");
        _baseAdapters = AppPaths.CreateAdapters();
        _baseAdaptersById = _baseAdapters.ToDictionary(adapter => adapter.Id, StringComparer.OrdinalIgnoreCase);

        Agents = new ObservableCollection<AgentItemViewModel>();
        ActivityLog = new ObservableCollection<ActivityLogEntry>();
        Presets = new ObservableCollection<PresetItemViewModel>(
            McpPresetCatalog.Presets.Select(preset => new PresetItemViewModel(preset)));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, ex => ReportException("刷新智能体失败", ex));
        TogglePresetCommand = new AsyncRelayCommand(
            parameter => TogglePresetAsync(parameter as PresetItemViewModel),
            ex => ReportException("切换 MCP 状态失败", ex));
        ConfirmInstallCommand = new AsyncRelayCommand(
            parameter => ConfirmInstallAsync(parameter as PresetItemViewModel),
            ex => ReportException("安装 MCP 失败", ex));
        CancelEditorCommand = new RelayCommand(parameter => (parameter as PresetItemViewModel)?.CloseEditor());
        AddAllCommand = new AsyncRelayCommand(AddAllAsync, ex => ReportException("批量添加失败", ex));
        RemoveAllCommand = new AsyncRelayCommand(RemoveAllAsync, ex => ReportException("批量移除失败", ex));
        OpenConfigCommand = new AsyncRelayCommand(OpenConfigAsync, ex => ReportException("打开配置文件失败", ex));
        BeginPathEditCommand = new RelayCommand(parameter => (parameter as AgentItemViewModel)?.BeginPathEdit());
        CancelPathCommand = new RelayCommand(parameter => (parameter as AgentItemViewModel)?.CancelPathEdit());
        SavePathCommand = new AsyncRelayCommand(
            parameter => SavePathAsync(parameter as AgentItemViewModel),
            ex => ReportException("保存配置路径失败", ex));
        ClearLogCommand = new RelayCommand(_ => ActivityLog.Clear());

        UpdateExportPreview();
    }

    /// <summary>获取左栏显示的智能体卡片。</summary>
    public ObservableCollection<AgentItemViewModel> Agents { get; }

    /// <summary>获取右侧网格中显示的 MCP 预设。</summary>
    public ObservableCollection<PresetItemViewModel> Presets { get; }

    /// <summary>获取底部显示的带时间戳的活动日志。</summary>
    public ObservableCollection<ActivityLogEntry> ActivityLog { get; }

    /// <summary>获取用于重新探测所有智能体的 Command。</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>获取为当前选中智能体安装或移除预设的 Command。</summary>
    public AsyncRelayCommand TogglePresetCommand { get; }

    /// <summary>获取在填写必需环境变量后确认安装的 Command。</summary>
    public AsyncRelayCommand ConfirmInstallCommand { get; }

    /// <summary>获取关闭内联环境变量编辑器的 Command。</summary>
    public RelayCommand CancelEditorCommand { get; }

    /// <summary>获取安装所有无需用户输入的预设的 Command。</summary>
    public AsyncRelayCommand AddAllCommand { get; }

    /// <summary>获取从选中智能体移除所有目录预设的 Command。</summary>
    public AsyncRelayCommand RemoveAllCommand { get; }

    /// <summary>获取打开选中智能体配置文件的 Command。</summary>
    public AsyncRelayCommand OpenConfigCommand { get; }

    /// <summary>获取显示内联配置路径编辑器的 Command。</summary>
    public RelayCommand BeginPathEditCommand { get; }

    /// <summary>获取放弃内联路径编辑的 Command。</summary>
    public RelayCommand CancelPathCommand { get; }

    /// <summary>获取持久化内联路径编辑结果的 Command。</summary>
    public AsyncRelayCommand SavePathCommand { get; }

    /// <summary>获取清空活动日志的 Command。</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>获取或设置左栏中当前选中的智能体。</summary>
    public AgentItemViewModel? SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            if (!SetProperty(ref _selectedAgent, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedAgent));
            OnPropertyChanged(nameof(ShowPresetManager));
            OnPropertyChanged(nameof(ShowExportOnly));
            OnPropertyChanged(nameof(ShowPresetDetails));
            OnPropertyChanged(nameof(CanModifyCurrentAgent));
            OnPropertyChanged(nameof(CanOpenConfigFile));
            UpdatePresetsFromAgent();
            SelectedPreset = null;
            UpdateExportPreview();
        }
    }

    /// <summary>获取或设置右侧网格中当前高亮的预设。</summary>
    public PresetItemViewModel? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                OnPropertyChanged(nameof(HasSelectedPreset));
                OnPropertyChanged(nameof(ShowPresetDetails));
                UpdateExportPreview();
            }
        }
    }

    /// <summary>获取是否已选中智能体。</summary>
    public bool HasSelectedAgent => _selectedAgent != null;

    /// <summary>获取详情窗格中是否已选中预设。</summary>
    public bool HasSelectedPreset => _selectedPreset != null;

    /// <summary>获取当前智能体模式下是否显示选中预设的详情。</summary>
    public bool ShowPresetDetails => ShowPresetManager && _selectedPreset != null;

    /// <summary>获取是否应显示预设管理视图。</summary>
    public bool ShowPresetManager => _selectedAgent is { Managed: true } && !ShowExportOnly;

    /// <summary>获取选中智能体是否仅支持导出 JSON（dsh，或无配置文件的 OpenClaw）。</summary>
    public bool ShowExportOnly => _selectedAgent != null && IsExportOnly(_selectedAgent);

    /// <summary>获取是否有任何长时间运行的操作正在进行。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanModifyCurrentAgent));
                OnPropertyChanged(nameof(CanOpenConfigFile));
            }
        }
    }

    /// <summary>获取 <see cref="IsBusy"/> 的相反值，用于禁用工具栏按钮。</summary>
    public bool IsNotBusy => !_isBusy;

    /// <summary>获取当前选择是否允许执行预设操作。</summary>
    public bool CanModifyCurrentAgent => _selectedAgent is { Managed: true } && !ShowExportOnly && !_isBusy;

    /// <summary>获取选中智能体是否有可打开的配置文件。</summary>
    public bool CanOpenConfigFile => _selectedAgent is { ConfigExists: true } && !_isBusy;

    /// <summary>获取或设置活动日志是否滚动到最新条目。</summary>
    public bool IsLogAutoScroll
    {
        get => _isLogAutoScroll;
        set => SetProperty(ref _isLogAutoScroll, value);
    }

    /// <summary>获取当前在导出预览中显示的 JSON。</summary>
    public string ExportPreview
    {
        get => _exportPreview;
        private set => SetProperty(ref _exportPreview, value);
    }

    /// <summary>获取导出范围描述，例如 <c>全部预设：11 个</c>。</summary>
    public string ExportScopeText
    {
        get => _exportScopeText;
        private set => SetProperty(ref _exportScopeText, value);
    }

    /// <summary>获取状态栏中显示的当前状态消息。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>获取状态栏中显示的最后操作摘要。</summary>
    public string LastOperationText
    {
        get => _lastOperationText;
        private set => SetProperty(ref _lastOperationText, value);
    }

    /// <summary>获取状态栏中显示的智能体汇总数量。</summary>
    public string AgentsSummary =>
        "发现智能体 " + Agents.Count.ToString(CultureInfo.InvariantCulture) +
        " 个 · 可管理 " + Agents.Count(agent => agent.Managed).ToString(CultureInfo.InvariantCulture) +
        " 个 · 已配置 " + Agents.Count(agent => agent.ConfigExists).ToString(CultureInfo.InvariantCulture) + " 个";

    /// <summary>获取已发现的智能体数量文本，用于左面板徽章。</summary>
    public string AgentCountText => Agents.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>获取预设定义数量文本，用于工具栏徽章。</summary>
    public string PresetCountText => Presets.Count.ToString(CultureInfo.InvariantCulture);

    /// <summary>加载已持久化的路径覆盖并探测所有智能体。</summary>
    /// <returns>初始探测完成时结束的 Task。</returns>
    public async Task InitializeAsync()
    {
        try
        {
            SetBusy(true);
            ReportStatus("正在加载路径覆盖并探测智能体……");

            var loaded = await Task.Run(() => LoadOverridesFile(_overridesPath));
            _overrides.Clear();
            foreach (var pair in loaded.Values)
            {
                if (_baseAdaptersById.ContainsKey(pair.Key))
                {
                    _overrides[pair.Key] = pair.Value;
                }
            }

            if (loaded.Error != null)
            {
                AddLog(new ActivityLogEntry(
                    DateTime.Now,
                    "系统",
                    "路径覆盖",
                    "加载",
                    loaded.Error,
                    false));
            }

            await RefreshCoreAsync();
            ReportStatus("就绪：选择左侧智能体开始管理 MCP。");
        }
        catch (Exception ex)
        {
            ReportException("初始化失败", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>重新探测所有智能体并重新读取其配置。</summary>
    /// <returns>刷新完成时结束的 Task。</returns>
    public async Task RefreshAsync()
    {
        try
        {
            SetBusy(true);
            ReportStatus("正在刷新智能体配置……");
            await RefreshCoreAsync();
            ReportStatus("刷新完成。");
        }
        catch (Exception ex)
        {
            ReportException("刷新智能体失败", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>在后台线程将当前导出预览写入文件。</summary>
    /// <param name="filePath">用户选择的目标 JSON 文件路径。</param>
    /// <returns>文件写入成功时返回 <see langword="true"/>。</returns>
    public async Task<bool> ExportAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            ReportStatus("导出路径无效。");
            return false;
        }

        var json = ExportPreview;
        var agentLabel = SelectedAgent?.DisplayName ?? "全部智能体";
        var presetLabel = SelectedPreset?.DisplayName ?? "全部预设";

        try
        {
            await Task.Run(() =>
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(filePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            });

            AddLog(new ActivityLogEntry(
                DateTime.Now,
                agentLabel,
                presetLabel,
                "导出",
                "已导出 MCP JSON。",
                true,
                filePath: filePath));
            ReportStatus("已导出 JSON：" + filePath);
            return true;
        }
        catch (Exception ex)
        {
            AddLog(new ActivityLogEntry(
                DateTime.Now,
                agentLabel,
                presetLabel,
                "导出",
                "导出失败：" + ex.Message,
                false,
                filePath: filePath));
            ReportStatus("导出失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>更新状态栏文本。</summary>
    /// <param name="message">要显示的消息。</param>
    public void ReportStatus(string message)
        => StatusMessage = string.IsNullOrWhiteSpace(message) ? "就绪。" : message;

    /// <summary>将意外异常记录到活动日志和状态栏中，而不是让它逃逸。</summary>
    /// <param name="context">失败操作的简短描述。</param>
    /// <param name="exception">要报告的异常。</param>
    public void ReportException(string context, Exception exception)
    {
        try
        {
            var message = context + "：" + exception.Message;
            AddLog(new ActivityLogEntry(
                DateTime.Now,
                SelectedAgent?.DisplayName ?? "系统",
                "—",
                "错误",
                message,
                false));
            ReportStatus(message);
        }
        catch (Exception)
        {
            // 报告异常时绝不能从异常处理程序中再次抛出。
        }
    }

    private void SetBusy(bool value)
    {
        IsBusy = value;
    }

    private async Task RefreshCoreAsync()
    {
        var adapters = BuildResolvedAdapters();
        var snapshots = await Task.Run(() =>
        {
            var probes = _probeService.ProbeAll(adapters);
            var results = new List<(AgentProbe Probe, AgentMcpState State)>(probes.Count);
            foreach (var probe in probes)
            {
                results.Add((probe, _editor.Read(probe.Adapter, probe.Adapter.ConfigPath)));
            }

            return results;
        });

        var selectedId = SelectedAgent?.Id;
        Agents.Clear();
        foreach (var snapshot in snapshots)
        {
            var defaultPath = _baseAdaptersById.TryGetValue(snapshot.Probe.Adapter.Id, out var baseAdapter)
                ? baseAdapter.ConfigPath
                : snapshot.Probe.Adapter.ConfigPath;
            Agents.Add(new AgentItemViewModel(snapshot.Probe.Adapter, snapshot.Probe, snapshot.State, defaultPath));
        }

        UpdateCounts();
        SelectedAgent = Agents.FirstOrDefault(agent => string.Equals(agent.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? Agents.FirstOrDefault();
        UpdatePresetsFromAgent();
        UpdateExportPreview();
    }

    private IReadOnlyList<AgentAdapter> BuildResolvedAdapters()
        => _baseAdapters
            .Select(adapter => _overrides.TryGetValue(adapter.Id, out var path) && !string.IsNullOrWhiteSpace(path)
                ? adapter.WithConfigPath(path)
                : adapter)
            .ToArray();

    private Task TogglePresetAsync(PresetItemViewModel? preset)
    {
        if (preset == null)
        {
            return Task.CompletedTask;
        }

        var agent = SelectedAgent;
        if (agent == null)
        {
            ReportStatus("请先选择一个智能体。");
            return Task.CompletedTask;
        }

        if (!EnsureEditable(agent, preset.DisplayName))
        {
            return Task.CompletedTask;
        }

        if (preset.IsInstalled)
        {
            return RemovePresetCoreAsync(agent, preset);
        }

        if (preset.RequiresUserInput && !preset.HasCompleteEnvironment)
        {
            preset.OpenEditor();
            SelectedPreset = preset;
            ReportStatus("「" + preset.DisplayName + "」需要先填写环境变量。");
            return Task.CompletedTask;
        }

        return InstallPresetCoreAsync(agent, preset);
    }

    private Task ConfirmInstallAsync(PresetItemViewModel? preset)
    {
        if (preset == null)
        {
            return Task.CompletedTask;
        }

        var agent = SelectedAgent;
        if (agent == null || !EnsureEditable(agent, preset.DisplayName))
        {
            return Task.CompletedTask;
        }

        var missing = McpEntryFormatter.GetMissingEnvironment(preset.Preset, preset.BuildEnvironment());
        if (missing.Count > 0)
        {
            preset.OpenEditor();
            ReportStatus("请填写以下环境变量：" + string.Join("、", missing));
            return Task.CompletedTask;
        }

        return InstallPresetCoreAsync(agent, preset);
    }

    private async Task InstallPresetCoreAsync(AgentItemViewModel agent, PresetItemViewModel preset)
    {
        var environment = preset.BuildEnvironment();
        var adapter = agent.Adapter;
        var configPath = agent.ConfigPath;

        SetBusy(true);
        try
        {
            var result = await Task.Run(() => _editor.Install(adapter, configPath, preset.Preset, environment));
            LogOperation(agent, preset.DisplayName, "添加", result);
            ReportStatus(DescribeResult(result));
            if (result.Success)
            {
                preset.CloseEditor();
            }

            await RefreshAgentStateAsync(agent);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RemovePresetCoreAsync(AgentItemViewModel agent, PresetItemViewModel preset)
    {
        var adapter = agent.Adapter;
        var configPath = agent.ConfigPath;

        SetBusy(true);
        try
        {
            var result = await Task.Run(() => _editor.Remove(adapter, configPath, preset.Id));
            LogOperation(agent, preset.DisplayName, "移除", result);
            ReportStatus(DescribeResult(result));
            await RefreshAgentStateAsync(agent);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task AddAllAsync()
    {
        var agent = SelectedAgent;
        if (agent == null)
        {
            ReportStatus("请先选择一个智能体。");
            return;
        }

        if (!EnsureEditable(agent, "全部预设"))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var adapter = agent.Adapter;
            var configPath = agent.ConfigPath;
            var jobs = new List<(PresetItemViewModel Preset, Dictionary<string, string> Environment)>();
            var skipped = new List<PresetItemViewModel>();

            foreach (var preset in Presets)
            {
                if (preset.IsInstalled)
                {
                    continue;
                }

                if (preset.RequiresUserInput && !preset.HasCompleteEnvironment)
                {
                    skipped.Add(preset);
                    continue;
                }

                jobs.Add((preset, preset.BuildEnvironment()));
            }

            if (jobs.Count == 0 && skipped.Count == 0)
            {
                ReportStatus("所有预设均已安装。");
                return;
            }

            var results = await Task.Run(() =>
                jobs.Select(job => _editor.Install(adapter, configPath, job.Preset.Preset, job.Environment)).ToList());

            var succeeded = 0;
            var failed = 0;
            for (var index = 0; index < jobs.Count; index++)
            {
                var result = results[index];
                LogOperation(agent, jobs[index].Preset.DisplayName, "全部添加", result);
                if (result.Success)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }

            foreach (var preset in skipped)
            {
                AddLog(new ActivityLogEntry(
                    DateTime.Now,
                    agent.DisplayName,
                    preset.DisplayName,
                    "全部添加",
                    "已跳过：需要先填写环境变量。",
                    false,
                    isSkipped: true));
            }

            await RefreshAgentStateAsync(agent);

            var summary = "批量添加：成功 " + succeeded.ToString(CultureInfo.InvariantCulture) + " 个";
            if (failed > 0)
            {
                summary += "，失败 " + failed.ToString(CultureInfo.InvariantCulture) + " 个";
            }

            if (skipped.Count > 0)
            {
                summary += "，跳过 " + skipped.Count.ToString(CultureInfo.InvariantCulture) + " 个（需要环境变量）";
            }

            summary += "。";
            AddLog(new ActivityLogEntry(
                DateTime.Now,
                agent.DisplayName,
                "全部预设",
                "全部添加",
                summary,
                failed == 0));
            ReportStatus(summary);
        }
        catch (Exception ex)
        {
            ReportException("批量添加失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveAllAsync()
    {
        var agent = SelectedAgent;
        if (agent == null)
        {
            ReportStatus("请先选择一个智能体。");
            return;
        }

        if (!EnsureEditable(agent, "全部预设"))
        {
            return;
        }

        var ids = McpPresetCatalog.Presets
            .Where(preset => agent.HasPreset(preset.Id))
            .Select(preset => preset.Id)
            .ToList();

        if (ids.Count == 0)
        {
            ReportStatus("当前智能体没有可移除的预设 MCP。");
            return;
        }

        IsBusy = true;
        try
        {
            var adapter = agent.Adapter;
            var configPath = agent.ConfigPath;
            var results = await Task.Run(() =>
                ids.Select(id => (Id: id, Result: _editor.Remove(adapter, configPath, id))).ToList());

            var succeeded = 0;
            var failed = 0;
            foreach (var item in results)
            {
                var label = McpPresetCatalog.Find(item.Id)?.DisplayName ?? item.Id;
                LogOperation(agent, label, "全部移除", item.Result);
                if (item.Result.Success)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }

            await RefreshAgentStateAsync(agent);

            var summary = "批量移除：成功 " + succeeded.ToString(CultureInfo.InvariantCulture) + " 个";
            if (failed > 0)
            {
                summary += "，失败 " + failed.ToString(CultureInfo.InvariantCulture) + " 个";
            }

            summary += "。";
            AddLog(new ActivityLogEntry(
                DateTime.Now,
                agent.DisplayName,
                "全部预设",
                "全部移除",
                summary,
                failed == 0));
            ReportStatus(summary);
        }
        catch (Exception ex)
        {
            ReportException("批量移除失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SavePathAsync(AgentItemViewModel? agent)
    {
        if (agent == null)
        {
            return;
        }

        if (IsBusy)
        {
            return;
        }

        var raw = (agent.PathEditor ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            ReportStatus("配置路径不能为空。");
            return;
        }

        string fullPath;
        try
        {
            if (raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                ReportStatus("路径包含非法字符，请重新输入。");
                return;
            }

            fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
        }
        catch (Exception ex)
        {
            ReportStatus("路径无效：" + ex.Message);
            return;
        }

        if (Directory.Exists(fullPath))
        {
            ReportStatus("配置路径必须是文件路径，而不是目录。");
            return;
        }

        if (!IsValidFilePath(fullPath))
        {
            ReportStatus("路径格式无效，请检查是否包含非法字符。");
            return;
        }

        if (!_baseAdaptersById.TryGetValue(agent.Id, out var baseAdapter))
        {
            ReportStatus("未知智能体：" + agent.Id);
            return;
        }

        var adapter = baseAdapter.WithConfigPath(fullPath);
        var isDefault = string.Equals(
            NormalizePath(agent.DefaultConfigPath),
            NormalizePath(fullPath),
            StringComparison.OrdinalIgnoreCase);

        if (isDefault)
        {
            _overrides.Remove(agent.Id);
        }
        else
        {
            _overrides[agent.Id] = fullPath;
        }

        IsBusy = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var probe = _probeService.ProbeAll(new[] { adapter })[0];
                var state = _editor.Read(adapter, adapter.ConfigPath);
                return (Probe: probe, State: state);
            });

            agent.Apply(adapter, snapshot.Probe, snapshot.State);
            agent.IsEditingPath = false;

            await Task.Run(SaveOverridesFile);
            AddLog(new ActivityLogEntry(
                DateTime.Now,
                agent.DisplayName,
                "配置路径",
                "保存",
                "配置路径已更新为：" + fullPath,
                true,
                filePath: fullPath));
            ReportStatus("配置路径已保存。");

            if (ReferenceEquals(agent, SelectedAgent))
            {
                NotifySelectedAgentDerived();
                UpdatePresetsFromAgent();
            }

            UpdateCounts();
        }
        catch (Exception ex)
        {
            ReportException("保存配置路径失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenConfigAsync()
    {
        var agent = SelectedAgent;
        if (agent == null)
        {
            ReportStatus("请先选择一个智能体。");
            return;
        }

        var path = agent.ConfigPath;
        try
        {
            var exists = await Task.Run(() => File.Exists(path));
            if (!exists)
            {
                AddLog(new ActivityLogEntry(
                    DateTime.Now,
                    agent.DisplayName,
                    "配置文件",
                    "打开",
                    "配置文件不存在：" + path,
                    false,
                    filePath: path));
                ReportStatus("配置文件不存在：" + path);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });

            AddLog(new ActivityLogEntry(
                DateTime.Now,
                agent.DisplayName,
                "配置文件",
                "打开",
                "已使用系统默认程序打开配置文件。",
                true,
                filePath: path));
            ReportStatus("已打开配置文件：" + path);
        }
        catch (Exception ex)
        {
            AddLog(new ActivityLogEntry(
                DateTime.Now,
                agent.DisplayName,
                "配置文件",
                "打开",
                "打开失败：" + ex.Message,
                false,
                filePath: path));
            ReportStatus("打开配置文件失败：" + ex.Message);
        }
    }

    private async Task RefreshAgentStateAsync(AgentItemViewModel agent)
    {
        var adapter = agent.Adapter;
        var snapshot = await Task.Run(() =>
        {
            var probe = _probeService.ProbeAll(new[] { adapter })[0];
            var state = _editor.Read(adapter, adapter.ConfigPath);
            return (Probe: probe, State: state);
        });

        agent.Apply(adapter, snapshot.Probe, snapshot.State);
        UpdateCounts();

        if (ReferenceEquals(agent, SelectedAgent))
        {
            NotifySelectedAgentDerived();
            UpdatePresetsFromAgent();
        }
    }

    private void NotifySelectedAgentDerived()
    {
        OnPropertyChanged(nameof(HasSelectedAgent));
        OnPropertyChanged(nameof(ShowPresetManager));
        OnPropertyChanged(nameof(ShowExportOnly));
        OnPropertyChanged(nameof(ShowPresetDetails));
        OnPropertyChanged(nameof(CanModifyCurrentAgent));
        OnPropertyChanged(nameof(CanOpenConfigFile));
        UpdateExportPreview();
    }

    private bool EnsureEditable(AgentItemViewModel agent, string presetLabel)
    {
        if (!IsExportOnly(agent))
        {
            return true;
        }

        var reason = agent.IsUnsupported
            ? "「" + agent.DisplayName + "」不支持自动写入配置，请使用导出 JSON。"
            : "「" + agent.DisplayName + "」尚未检测到配置文件，当前仅支持导出 JSON。";
        AddLog(new ActivityLogEntry(
            DateTime.Now,
            agent.DisplayName,
            presetLabel,
            "操作",
            reason,
            false));
        ReportStatus(reason);
        return false;
    }

    private static bool IsExportOnly(AgentItemViewModel agent)
        => agent.IsUnsupported ||
           (string.Equals(agent.Id, OpenClawAgentId, StringComparison.OrdinalIgnoreCase) && !agent.ConfigExists);

    private void UpdatePresetsFromAgent()
    {
        var agent = SelectedAgent;
        foreach (var preset in Presets)
        {
            preset.IsInstalled = agent?.HasPreset(preset.Id) ?? false;
        }
    }

    private void UpdateExportPreview()
    {
        if (SelectedPreset is { } selected)
        {
            ExportPreview = McpEntryFormatter.BuildPortableJson(new[] { selected.Preset });
            ExportScopeText = "当前预设：" + selected.DisplayName;
        }
        else
        {
            ExportPreview = McpEntryFormatter.BuildPortableJson(McpPresetCatalog.Presets);
            ExportScopeText = "全部预设：" + McpPresetCatalog.Presets.Count.ToString(CultureInfo.InvariantCulture) + " 个";
        }
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(AgentsSummary));
        OnPropertyChanged(nameof(AgentCountText));
        OnPropertyChanged(nameof(PresetCountText));
    }

    private void LogOperation(AgentItemViewModel agent, string presetLabel, string action, AgentOperationResult result)
    {
        AddLog(new ActivityLogEntry(
            DateTime.Now,
            agent.DisplayName,
            presetLabel,
            action,
            result.Message,
            result.Success,
            result.BackupPath,
            result.Path));
    }

    private void AddLog(ActivityLogEntry entry)
    {
        ActivityLog.Add(entry);
        while (ActivityLog.Count > MaxLogEntries)
        {
            ActivityLog.RemoveAt(0);
        }

        LastOperationText = "最后操作：" + entry.TimeText + " " + entry.Action + " " + entry.PresetName + "（" + entry.ResultText + "）";
    }

    private static string DescribeResult(AgentOperationResult result)
    {
        if (!result.Success)
        {
            return result.Message;
        }

        return string.IsNullOrWhiteSpace(result.BackupPath)
            ? result.Message
            : result.Message + " 备份：" + result.BackupPath;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>
    /// 逐段验证完全限定的文件路径。.NET 不再将 ':' 报告为非法路径字符，
    /// 因此类似 <c>::bad::</c> 的格式错误值否则会被接受并持久化。
    /// </summary>
    private static bool IsValidFilePath(string fullPath)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var remainder = fullPath.Substring(root.Length);

        foreach (var segment in remainder.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.IndexOfAny(invalidChars) >= 0 || segment is "." or "..")
            {
                return false;
            }
        }

        var fileName = Path.GetFileName(fullPath);
        return fileName.Length > 0 && fileName.IndexOfAny(invalidChars) < 0;
    }

    private static (Dictionary<string, string> Values, string? Error) LoadOverridesFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path))
            {
                return (values, null);
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return (values, null);
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(text, new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                PropertyNameCaseInsensitive = true
            });

            if (parsed != null)
            {
                foreach (var pair in parsed)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        values[pair.Key] = pair.Value;
                    }
                }
            }

            return (values, null);
        }
        catch (Exception ex)
        {
            return (values, "读取路径覆盖失败：" + ex.Message);
        }
    }

    private void SaveOverridesFile()
    {
        var directory = Path.GetDirectoryName(_overridesPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sorted = new SortedDictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase);
        var json = JsonSerializer.Serialize(sorted, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(_overridesPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

/// <summary>解析应用数据文件夹和智能体目录根路径，并支持可选的沙盒覆盖。</summary>
internal static class AppPaths
{
    private const string AppDataOverrideVariable = "AITOOLS_MCP_AGENT_MANAGER_APPDATA";
    private const string HomeOverrideVariable = "AITOOLS_MCP_AGENT_MANAGER_HOME";
    private const string LocalAppDataOverrideVariable = "AITOOLS_MCP_AGENT_MANAGER_LOCALAPPDATA";

    /// <summary>获取存储备份和 <c>overrides.json</c> 的文件夹。</summary>
    internal static string ResolveAppDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(AppDataOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ai-tools",
            "mcp-agent-manager");
    }

    /// <summary>
    /// 使用当前用户配置文件构建智能体适配器，或使用通过
    /// <c>AITOOLS_MCP_AGENT_MANAGER_HOME</c> 和 <c>AITOOLS_MCP_AGENT_MANAGER_LOCALAPPDATA</c> 提供的临时根路径进行安全的手动测试。
    /// </summary>
    internal static IReadOnlyList<AgentAdapter> CreateAdapters()
    {
        var home = Environment.GetEnvironmentVariable(HomeOverrideVariable);
        if (string.IsNullOrWhiteSpace(home))
        {
            return AgentCatalog.CreateAdapters();
        }

        var localAppData = Environment.GetEnvironmentVariable(LocalAppDataOverrideVariable);
        return AgentCatalog.CreateAdapters(
            home,
            string.IsNullOrWhiteSpace(localAppData) ? null : localAppData);
    }
}
