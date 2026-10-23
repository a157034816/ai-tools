using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;
using AiTools.Wpf.Configuration;
using AiTools.Wpf.Mvvm;

namespace AiTools.Wpf.ViewModels;

/// <summary>
/// 支撑设置页：加载并保存 <see cref="AppSettings"/>、编辑服务商配置，
/// 并在保存时保留对话页的选择。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ObservableCollection<ProviderEditorViewModel> _providers = new();
    private IReadOnlyList<ProviderDescriptor> _descriptors;
    private string _defaultSystemPrompt;
    private bool _isAvailable;
    private ProviderDescriptor? _selectedDescriptor;
    private ProviderEditorViewModel? _selectedProvider;
    private ProviderEditorViewModel? _pendingDelete;
    private string _systemPrompt = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isSaving;
    private bool _isLoading;
    private bool _hasUnsavedChanges;
    private bool _hasLoaded;
    private DateTime? _lastSavedAt;
    private AppSettings? _loadedSettings;
    private string? _selectedProviderId;
    private string? _selectedModelId;

    /// <summary>
    /// 创建 ViewModel。无参数构造函数在 <see cref="AiToolsWpf.Initialize(AiToolsWpfOptions?)"/>
    /// 执行前也可安全使用，因此 XAML 设计器和宿主预览不会崩溃。
    /// </summary>
    public SettingsViewModel()
    {
        _isAvailable = AiToolsWpf.IsInitialized;
        _descriptors = GetAvailableDescriptors();
        _defaultSystemPrompt = GetDefaultSystemPrompt();
        _selectedDescriptor = _descriptors.FirstOrDefault();

        if (!_isAvailable)
        {
            _statusMessage = "AiToolsWpf 尚未初始化，当前为只读预览模式。";
        }

        _providers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProviders));
            OnPropertyChanged(nameof(ProviderCountText));
        };

        AddSelectedDescriptorCommand = new RelayCommand(AddSelectedDescriptor, () => CanEdit);
        RemoveProviderCommand = new RelayCommand(parameter => BeginDelete(parameter as ProviderEditorViewModel), _ => CanEdit);
        AddModelCommand = new RelayCommand(parameter => AddModel(parameter as ProviderEditorViewModel), _ => CanEdit);
        RemoveModelCommand = new RelayCommand(parameter => RemoveModel(parameter as ModelRowViewModel), _ => CanEdit);
        MoveUpCommand = new RelayCommand(parameter => Move(parameter as ProviderEditorViewModel, -1), _ => CanEdit);
        MoveDownCommand = new RelayCommand(parameter => Move(parameter as ProviderEditorViewModel, 1), _ => CanEdit);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanEdit);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => _isAvailable && !_isLoading && !_isSaving);
        TestConnectionCommand = new AsyncRelayCommand(
            parameter => TestConnectionAsync(parameter as ProviderEditorViewModel),
            _ => CanEdit);
        ResetSystemPromptCommand = new RelayCommand(ResetSystemPrompt, () => CanEdit);
        ConfirmDeleteCommand = new RelayCommand(ConfirmDelete, () => CanEdit && _pendingDelete != null);
        CancelDeleteCommand = new RelayCommand(CancelDelete);
    }

    /// <summary>获取由 XAML 设计器使用的 ViewModel 实例。它从不读取或写入设置存储。</summary>
    public static SettingsViewModel? DesignInstance { get; } = new();

    /// <summary>设置成功保存后引发。</summary>
    public event EventHandler? SettingsSaved;

    /// <summary>获取服务商列表中显示的可编辑服务商。</summary>
    public ObservableCollection<ProviderEditorViewModel> Providers => _providers;

    /// <summary>获取是否至少有一个服务商正在编辑。</summary>
    public bool HasProviders => _providers.Count > 0;

    /// <summary>获取描述服务商数量的徽章文本。</summary>
    public string ProviderCountText => _providers.Count == 0 ? "未配置" : $"{_providers.Count} 个";

    /// <summary>获取宿主提供的服务商模板。始终包含一个自定义模板。</summary>
    public IReadOnlyList<ProviderDescriptor> Descriptors => _descriptors;

    /// <summary>获取 AiToolsWpf 是否已初始化。未初始化时编辑 Command 会被禁用。</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                OnBusyStateChanged();
            }
        }
    }

    /// <summary>获取编辑器是否可以修改设置：已初始化、已加载且不忙碌。</summary>
    public bool CanEdit => _isAvailable && _hasLoaded && !_isLoading && !_isSaving;

    /// <summary>获取或设置添加服务商选择器中选中的模板。</summary>
    public ProviderDescriptor? SelectedDescriptor
    {
        get => _selectedDescriptor;
        set
        {
            if (SetProperty(ref _selectedDescriptor, value))
            {
                OnPropertyChanged(nameof(SelectedDescriptorDescription));
            }
        }
    }

    /// <summary>获取 <see cref="SelectedDescriptor"/> 的描述，或空字符串。</summary>
    public string SelectedDescriptorDescription => _selectedDescriptor?.Description ?? string.Empty;

    /// <summary>获取或设置当前正在编辑的服务商。</summary>
    public ProviderEditorViewModel? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(HasSelectedProvider));
            }
        }
    }

    /// <summary>获取当前是否选中了服务商。</summary>
    public bool HasSelectedProvider => _selectedProvider != null;

    /// <summary>
    /// 获取或设置新会话使用的 system prompt。值为空时回退到
    /// <see cref="AiToolsWpfOptions.DefaultSystemPrompt"/>。
    /// </summary>
    public string SystemPrompt
    {
        get => _systemPrompt;
        set
        {
            if (SetProperty(ref _systemPrompt, value ?? string.Empty))
            {
                MarkDirty();
            }
        }
    }

    /// <summary>获取宿主提供的默认 system prompt，或空字符串。</summary>
    public string DefaultSystemPrompt => _defaultSystemPrompt;

    /// <summary>获取说明空 system prompt 回退规则的提示。</summary>
    public string DefaultSystemPromptHint => string.IsNullOrWhiteSpace(_defaultSystemPrompt)
        ? "留空时使用宿主应用的默认提示词（当前未配置全局默认提示词）。"
        : $"留空时使用宿主默认提示词：{_defaultSystemPrompt}";

    /// <summary>获取页面上显示的当前状态或错误消息。</summary>
    public string StatusMessage => _statusMessage;

    /// <summary>获取保存操作是否正在进行。</summary>
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                OnBusyStateChanged();
            }
        }
    }

    /// <summary>获取设置是否正在加载或重新加载。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnBusyStateChanged();
            }
        }
    }

    /// <summary>获取编辑器是否有未保存的更改。</summary>
    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    /// <summary>获取本会话中上次成功保存的时间。</summary>
    public DateTime? LastSavedAt => _lastSavedAt;

    /// <summary>获取等待删除确认的服务商。</summary>
    public ProviderEditorViewModel? PendingDelete
    {
        get => _pendingDelete;
        private set
        {
            if (SetProperty(ref _pendingDelete, value))
            {
                OnPropertyChanged(nameof(HasPendingDelete));
                OnPropertyChanged(nameof(PendingDeleteName));
                ConfirmDeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>获取是否有待确认的删除操作。</summary>
    public bool HasPendingDelete => _pendingDelete != null;

    /// <summary>获取等待删除确认的服务商显示名称。</summary>
    public string PendingDeleteName => _pendingDelete?.HeaderText ?? string.Empty;

    /// <summary>获取添加由 <see cref="SelectedDescriptor"/> 构建的服务商的 Command。</summary>
    public RelayCommand AddSelectedDescriptorCommand { get; }

    /// <summary>获取请求删除服务商的 Command。参数为 <see cref="ProviderEditorViewModel"/>。</summary>
    public RelayCommand RemoveProviderCommand { get; }

    /// <summary>获取添加空 model 行的 Command。参数为 <see cref="ProviderEditorViewModel"/>。</summary>
    public RelayCommand AddModelCommand { get; }

    /// <summary>获取移除 model 行的 Command。参数为 <see cref="ModelRowViewModel"/>。</summary>
    public RelayCommand RemoveModelCommand { get; }

    /// <summary>获取上移服务商的 Command。参数为 <see cref="ProviderEditorViewModel"/>。</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>获取下移服务商的 Command。参数为 <see cref="ProviderEditorViewModel"/>。</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>获取校验并保存设置的 Command。</summary>
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>获取放弃编辑并重新加载已保存设置的 Command。</summary>
    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>获取测试服务商连接的 Command。参数为 <see cref="ProviderEditorViewModel"/>。</summary>
    public AsyncRelayCommand TestConnectionCommand { get; }

    /// <summary>获取将 <see cref="DefaultSystemPrompt"/> 恢复到 <see cref="SystemPrompt"/> 的 Command。</summary>
    public RelayCommand ResetSystemPromptCommand { get; }

    /// <summary>获取移除 <see cref="PendingDelete"/> 的 Command。</summary>
    public RelayCommand ConfirmDeleteCommand { get; }

    /// <summary>获取取消待确认删除的 Command。</summary>
    public RelayCommand CancelDeleteCommand { get; }

    /// <summary>
    /// 将设置加载到编辑器中。可安全地多次调用；仅在首次调用时读取存储。
    /// 使用 <see cref="ReloadCommand"/> 可显式放弃编辑。必须在 UI 线程上调用。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_hasLoaded && _isAvailable)
        {
            return;
        }

        if (!_isAvailable && AiToolsWpf.IsInitialized)
        {
            // 页面可能早于 Initialize 创建（例如设计器或宿主延迟初始化），此时补读全局配置。
            IsAvailable = true;
            _descriptors = GetAvailableDescriptors();
            _defaultSystemPrompt = GetDefaultSystemPrompt();
            SelectedDescriptor = _descriptors.FirstOrDefault();
            OnPropertyChanged(nameof(Descriptors));
            OnPropertyChanged(nameof(DefaultSystemPrompt));
            OnPropertyChanged(nameof(DefaultSystemPromptHint));
            OnPropertyChanged(nameof(SelectedDescriptorDescription));
            SetStatus("已连接 AiToolsWpf，正在载入设置…");
        }

        if (!_isAvailable || _hasLoaded || _isLoading)
        {
            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 校验编辑器内容，并通过 <see cref="AiToolsWpf.SaveSettingsAsync"/> 保存。
    /// 必须在 UI 线程上调用。
    /// </summary>
    public async Task SaveAsync()
    {
        if (!_isAvailable)
        {
            SetStatus("AiToolsWpf 尚未初始化，无法保存设置。");
            return;
        }

        if (_isSaving)
        {
            return;
        }

        IsSaving = true;
        try
        {
            var errors = ValidateEditors();
            if (errors.Count > 0)
            {
                SetStatus("无法保存，请修正：\n" + string.Join("\n", errors.Select(error => "· " + error)));
                return;
            }

            // 对话页可能在设置页打开期间更新了选择，保存前读取最新的选择，避免覆盖。
            try
            {
                var latest = await AiToolsWpf.GetSettingsAsync().ConfigureAwait(true);
                _selectedProviderId = latest.SelectedProviderId;
                _selectedModelId = latest.SelectedModelId;
            }
            catch (AiToolsWpfException)
            {
                // 读取失败时沿用载入时的选择。
            }

            var settings = BuildSettings();
            await AiToolsWpf.SaveSettingsAsync(settings).ConfigureAwait(true);

            RemoveBlankModelRows();
            _loadedSettings = settings.Clone();
            _selectedProviderId = settings.SelectedProviderId;
            _selectedModelId = settings.SelectedModelId;
            _lastSavedAt = DateTime.Now;
            SetStatus($"已保存 {_lastSavedAt:HH:mm:ss}");
            HasUnsavedChanges = false;
            PendingDelete = null;
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (AiToolsWpfException ex)
        {
            SetStatus("保存失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + DescribeError(ex));
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task LoadAsync()
    {
        if (!_isAvailable)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var settings = await AiToolsWpf.GetSettingsAsync().ConfigureAwait(true);
            ApplySettings(settings);
            _hasLoaded = true;
            OnBusyStateChanged();
            SetStatus(settings.Version == 0
                ? "尚未保存过设置，请添加服务商并填写 API Key。"
                : "已载入设置。");
        }
        catch (AiToolsWpfException ex)
        {
            SetStatus("载入设置失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            SetStatus("载入设置失败：" + DescribeError(ex));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ReloadAsync()
    {
        if (!_isAvailable || _isLoading)
        {
            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    private void ApplySettings(AppSettings settings)
    {
        var previousSelectionId = _selectedProvider?.Id;

        foreach (var editor in _providers)
        {
            editor.Changed -= OnEditorChanged;
        }

        _providers.Clear();
        _loadedSettings = settings.Clone();
        _selectedProviderId = settings.SelectedProviderId;
        _selectedModelId = settings.SelectedModelId;

        foreach (var provider in settings.Providers)
        {
            AddEditor(new ProviderEditorViewModel(provider));
        }

        _systemPrompt = settings.SystemPrompt ?? string.Empty;
        OnPropertyChanged(nameof(SystemPrompt));
        PendingDelete = null;
        SelectedProvider = _providers.FirstOrDefault(editor =>
                              string.Equals(editor.Id, previousSelectionId, StringComparison.OrdinalIgnoreCase))
                          ?? _providers.FirstOrDefault();
        SelectedDescriptor = _descriptors.FirstOrDefault();
        HasUnsavedChanges = false;
    }

    private void AddSelectedDescriptor()
    {
        var descriptor = SelectedDescriptor;
        if (descriptor == null)
        {
            SetStatus("请先选择要添加的服务商模板。");
            return;
        }

        var (id, suffix) = CreateUniqueProviderId(descriptor.Id);
        var displayName = suffix <= 1 || string.IsNullOrWhiteSpace(descriptor.DisplayName)
            ? descriptor.DisplayName
            : $"{descriptor.DisplayName} {suffix}";

        var config = new ProviderConfig
        {
            Id = id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? descriptor.Id : displayName,
            Protocol = string.IsNullOrWhiteSpace(descriptor.Protocol) ? ProviderProtocols.OpenAi : descriptor.Protocol,
            BaseUrl = descriptor.DefaultBaseUrl ?? string.Empty,
            ApiKey = string.Empty,
            Enabled = true,
            Models = descriptor.DefaultModels.Select(modelId => new ModelConfig
            {
                Id = modelId,
                Enabled = true
            }).ToList()
        };

        var editor = AddEditor(new ProviderEditorViewModel(config));
        SelectedProvider = editor;
        MarkDirty();
        SetStatus($"已添加「{editor.HeaderText}」，请填写 API Key 后保存。");
    }

    private ProviderEditorViewModel AddEditor(ProviderEditorViewModel editor)
    {
        editor.Changed += OnEditorChanged;
        _providers.Add(editor);
        return editor;
    }

    private void BeginDelete(ProviderEditorViewModel? editor)
    {
        if (editor == null)
        {
            return;
        }

        PendingDelete = editor;
        SetStatus($"确认删除「{editor.HeaderText}」？删除将在保存后生效。");
    }

    private void ConfirmDelete()
    {
        var editor = _pendingDelete;
        if (editor == null)
        {
            return;
        }

        editor.Changed -= OnEditorChanged;
        _providers.Remove(editor);
        PendingDelete = null;

        if (ReferenceEquals(SelectedProvider, editor))
        {
            SelectedProvider = _providers.FirstOrDefault();
        }

        MarkDirty();
        SetStatus("已从列表移除，保存后生效。");
    }

    private void CancelDelete()
    {
        PendingDelete = null;
        SetStatus(string.Empty);
    }

    private void AddModel(ProviderEditorViewModel? editor)
    {
        if (editor == null)
        {
            return;
        }

        editor.AddModel();
        MarkDirty();
        SetStatus($"已为「{editor.HeaderText}」添加模型，请填写模型 ID。");
    }

    private void RemoveModel(ModelRowViewModel? row)
    {
        if (row == null || !row.Owner.RemoveModel(row))
        {
            return;
        }

        MarkDirty();
        SetStatus("已移除模型，保存后生效。");
    }

    private void Move(ProviderEditorViewModel? editor, int offset)
    {
        if (editor == null)
        {
            return;
        }

        var index = _providers.IndexOf(editor);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _providers.Count)
        {
            return;
        }

        _providers.Move(index, target);
        SelectedProvider = editor;
        MarkDirty();
    }

    private void ResetSystemPrompt()
    {
        SystemPrompt = _defaultSystemPrompt;
        SetStatus(string.IsNullOrWhiteSpace(_defaultSystemPrompt)
            ? "宿主未配置默认提示词，已清空系统提示词。"
            : "已恢复宿主默认系统提示词，保存后生效。");
    }

    private async Task TestConnectionAsync(ProviderEditorViewModel? editor)
    {
        if (editor == null)
        {
            return;
        }

        if (!_isAvailable)
        {
            editor.SetTestStatus(ConnectionTestState.Failed, "AiToolsWpf 尚未初始化，无法测试连接。");
            return;
        }

        var config = editor.CreateProviderConfig();
        var errors = ValidateProviderForTest(config);
        if (errors.Count > 0)
        {
            editor.SetTestStatus(ConnectionTestState.Failed, "无法测试：" + string.Join("；", errors));
            return;
        }

        var modelId = editor.Models
            .Where(model => model.Enabled && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => model.Id)
            .FirstOrDefault()
            ?? editor.Models
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .Select(model => model.Id)
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(modelId))
        {
            editor.SetTestStatus(ConnectionTestState.Failed, "无法测试：请先添加至少一个模型。");
            return;
        }

        editor.SetTestStatus(ConnectionTestState.Running, "正在测试连接…");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var client = AiToolsWpf.CreateChatClient(config);
            var response = await client.CompleteAsync(ChatRequest.FromUser(modelId, "ping"), timeout.Token)
                .ConfigureAwait(true);
            stopwatch.Stop();

            var reply = response.GetText();
            var summary = $"连接成功 · {modelId} · {stopwatch.ElapsedMilliseconds} ms";
            if (!string.IsNullOrWhiteSpace(reply))
            {
                summary += " · " + Truncate(reply.Trim(), 40);
            }

            editor.SetTestStatus(ConnectionTestState.Succeeded, summary);
        }
        catch (OperationCanceledException)
        {
            editor.SetTestStatus(ConnectionTestState.Failed, "连接超时（20 秒），请检查 Base URL 与网络。");
        }
        catch (AiToolsWpfException ex)
        {
            editor.SetTestStatus(ConnectionTestState.Failed, "连接失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            editor.SetTestStatus(ConnectionTestState.Failed, "连接失败：" + DescribeError(ex));
        }
    }

    private List<string> ValidateEditors()
    {
        var errors = new List<string>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var editor in _providers)
        {
            if (string.IsNullOrWhiteSpace(editor.DisplayName))
            {
                editor.DisplayName = GetProtocolDisplayName(editor.Protocol);
            }

            if (string.IsNullOrWhiteSpace(editor.BaseUrl) == false)
            {
                editor.BaseUrl = editor.BaseUrl.Trim();
            }

            if (!ProviderProtocols.All.Contains(editor.Protocol, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"服务商「{editor.HeaderText}」的协议无效：{editor.Protocol}。");
            }
            else if (!string.Equals(editor.Protocol, NormalizeProtocol(editor.Protocol), StringComparison.Ordinal))
            {
                editor.Protocol = NormalizeProtocol(editor.Protocol);
            }

            if (string.IsNullOrWhiteSpace(editor.Id) || usedIds.Contains(editor.Id))
            {
                var source = string.IsNullOrWhiteSpace(editor.Id) ? editor.Protocol : editor.Id;
                var (id, _) = CreateUniqueProviderId(source, usedIds);
                editor.SetId(id);
            }

            usedIds.Add(editor.Id);

            if (!editor.Enabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(editor.BaseUrl))
            {
                errors.Add($"服务商「{editor.HeaderText}」已启用，但未配置 Base URL。");
            }
            else if (!IsValidHttpUrl(editor.BaseUrl))
            {
                errors.Add($"服务商「{editor.HeaderText}」的 Base URL 必须是 http/https 绝对地址。");
            }

            if (!editor.Models.Any(model => !string.IsNullOrWhiteSpace(model.Id)))
            {
                errors.Add($"服务商「{editor.HeaderText}」已启用，但没有配置任何模型 ID。");
            }
        }

        return errors;
    }

    private static List<string> ValidateProviderForTest(ProviderConfig provider)
    {
        var errors = new List<string>();
        if (!ProviderProtocols.All.Contains(provider.Protocol, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"协议 {provider.Protocol} 不受支持");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            errors.Add("未配置 Base URL");
        }
        else if (!IsValidHttpUrl(provider.BaseUrl))
        {
            errors.Add("Base URL 必须是 http/https 绝对地址");
        }

        if (string.IsNullOrWhiteSpace(provider.ApiKey) && !ProviderConfig.IsLocalEndpoint(provider.BaseUrl))
        {
            errors.Add("未配置 API Key");
        }

        return errors;
    }

    private AppSettings BuildSettings()
    {
        var settings = _loadedSettings?.Clone() ?? new AppSettings();
        settings.Providers = _providers
            .Select(editor => editor.CreateProviderConfig())
            .ToList();

        foreach (var provider in settings.Providers)
        {
            provider.Protocol = NormalizeProtocol(provider.Protocol);
            provider.Models = provider.Models
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .ToList();
        }

        settings.SystemPrompt = string.IsNullOrWhiteSpace(_systemPrompt) ? null : _systemPrompt;
        ApplySelection(settings);
        return settings;
    }

    /// <summary>移除 ID 为空的 model 行，使编辑器与已保存的设置保持一致。</summary>
    private void RemoveBlankModelRows()
    {
        foreach (var editor in _providers)
        {
            var blankRows = editor.Models
                .Where(model => string.IsNullOrWhiteSpace(model.Id))
                .ToList();

            foreach (var row in blankRows)
            {
                editor.RemoveModel(row);
            }
        }
    }

    private void ApplySelection(AppSettings settings)
    {
        var provider = settings.FindProvider(_selectedProviderId);
        if (provider == null)
        {
            provider = settings.Providers.FirstOrDefault(candidate => candidate.Enabled && candidate.IsUsable());
        }

        if (provider == null)
        {
            settings.SelectedProviderId = null;
            settings.SelectedModelId = null;
            return;
        }

        settings.SelectedProviderId = provider.Id;

        var model = provider.FindModel(_selectedModelId);
        model ??= provider.Models.FirstOrDefault(candidate => candidate.Enabled);
        model ??= provider.Models.FirstOrDefault();
        settings.SelectedModelId = model?.Id;
    }

    private (string Id, int Suffix) CreateUniqueProviderId(string source)
    {
        var used = new HashSet<string>(_providers.Select(provider => provider.Id), StringComparer.OrdinalIgnoreCase);
        return CreateUniqueProviderId(source, used);
    }

    private static (string Id, int Suffix) CreateUniqueProviderId(string source, HashSet<string> used)
    {
        var slug = Slugify(source);
        if (slug.Length == 0)
        {
            slug = "provider";
        }

        var candidate = slug;
        var suffix = 1;
        while (used.Contains(candidate))
        {
            suffix++;
            candidate = $"{slug}-{suffix}";
        }

        return (candidate, suffix);
    }

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(ch);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.ToString();
    }

    private static string NormalizeProtocol(string protocol)
    {
        return ProviderProtocols.All.FirstOrDefault(candidate =>
                   string.Equals(candidate, protocol, StringComparison.OrdinalIgnoreCase))
               ?? protocol;
    }

    private static bool IsValidHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static string GetProtocolDisplayName(string protocol)
    {
        if (string.Equals(protocol, ProviderProtocols.Anthropic, StringComparison.OrdinalIgnoreCase))
        {
            return "Anthropic";
        }

        if (string.Equals(protocol, ProviderProtocols.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            return "Google Gemini";
        }

        return "OpenAI";
    }

    private static string DescribeError(Exception exception)
    {
        var current = exception;
        while (current.InnerException != null)
        {
            current = current.InnerException;
        }

        return string.IsNullOrWhiteSpace(current.Message) ? exception.GetType().Name : current.Message;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static IReadOnlyList<ProviderDescriptor> GetAvailableDescriptors()
    {
        IReadOnlyList<ProviderDescriptor> descriptors;
        try
        {
            descriptors = AiToolsWpf.IsInitialized
                ? AiToolsWpf.ProviderDescriptors
                : ProviderDescriptor.BuiltIns;
        }
        catch (AiToolsWpfException)
        {
            descriptors = ProviderDescriptor.BuiltIns;
        }

        var result = descriptors.ToList();
        if (result.All(descriptor => !string.Equals(descriptor.Id, "custom", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(new ProviderDescriptor
            {
                Id = "custom",
                DisplayName = "自定义服务商",
                Protocol = ProviderProtocols.OpenAi,
                Description = "手动填写 Base URL 的 OpenAI 兼容端点"
            });
        }

        return result;
    }

    private static string GetDefaultSystemPrompt()
    {
        try
        {
            return AiToolsWpf.IsInitialized ? AiToolsWpf.Options.DefaultSystemPrompt ?? string.Empty : string.Empty;
        }
        catch (AiToolsWpfException)
        {
            return string.Empty;
        }
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        if (sender is ProviderEditorViewModel editor)
        {
            editor.ClearTestStatus();
        }

        MarkDirty();
    }

    private void MarkDirty() => HasUnsavedChanges = true;

    private void SetStatus(string message)
    {
        _statusMessage = message ?? string.Empty;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private void OnBusyStateChanged()
    {
        OnPropertyChanged(nameof(CanEdit));

        AddSelectedDescriptorCommand.RaiseCanExecuteChanged();
        RemoveProviderCommand.RaiseCanExecuteChanged();
        AddModelCommand.RaiseCanExecuteChanged();
        RemoveModelCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        TestConnectionCommand.RaiseCanExecuteChanged();
        ResetSystemPromptCommand.RaiseCanExecuteChanged();
        ConfirmDeleteCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
    }
}
