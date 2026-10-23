using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AiTools.Abstractions;
using AiTools.Core.Chat;
using AiTools.Core.Sessions;
using AiTools.Wpf.Configuration;
using AiTools.Wpf.Mvvm;
using AiTools.Wpf.ViewModels;

namespace AiTools.Wpf.ViewModels;

/// <summary><see cref="ChatViewModel.StatusMessage"/> 的严重级别。</summary>
public enum StatusKind
{
    /// <summary>中性信息。</summary>
    Info,

    /// <summary>操作成功完成。</summary>
    Success,

    /// <summary>可恢复的问题，例如缺少配置。</summary>
    Warning,

    /// <summary>操作失败。</summary>
    Error
}

/// <summary><see cref="ProviderChoice"/> 中可选择的 model。</summary>
public sealed class ModelChoice
{
    /// <summary>为已配置的 model 创建选项。</summary>
    /// <param name="config">model 配置。</param>
    public ModelChoice(ModelConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        DisplayName = config.GetDisplayName();
    }

    /// <summary>获取底层的 model 配置。</summary>
    public ModelConfig Config { get; }

    /// <summary>获取 model 标识符。</summary>
    public string Id => Config.Id;

    /// <summary>获取 model 选择器中显示的名称。</summary>
    public string DisplayName { get; }
}

/// <summary>可用的服务商及其已启用的模型，显示在对话页的选择器中。</summary>
public sealed class ProviderChoice
{
    /// <summary>从服务商配置创建选项。模型会筛选为已启用的条目。</summary>
    /// <param name="config">服务商配置。</param>
    public ProviderChoice(ProviderConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? config.Id : config.DisplayName;
        Models = config.Models
            .Where(model => model.Enabled && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new ModelChoice(model))
            .ToArray();
    }

    /// <summary>获取底层的服务商配置。</summary>
    public ProviderConfig Config { get; }

    /// <summary>获取服务商选择器中显示的名称。</summary>
    public string DisplayName { get; }

    /// <summary>获取此服务商已启用的模型。</summary>
    public IReadOnlyList<ModelChoice> Models { get; }
}

/// <summary>表示 /resume 选择器中的一个已保存对话。</summary>
public sealed class ConversationItemViewModel : ObservableObject
{
    /// <summary>从持久化摘要创建条目。</summary>
    public ConversationItemViewModel(ConversationSummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    /// <summary>获取底层的摘要。</summary>
    public ConversationSummary Summary { get; }

    /// <summary>获取对话 ID。</summary>
    public string Id => Summary.Id;

    /// <summary>获取标题。</summary>
    public string Title => Summary.Title;

    /// <summary>获取相对更新时间，例如 <c>3 小时前</c>。</summary>
    public string RelativeTime => Summary.GetRelativeTime();

    /// <summary>获取消息数量。</summary>
    public int MessageCount => Summary.MessageCount;

    /// <summary>获取服务商与 model 的标签（若可用）。</summary>
    public string ModelLabel => string.IsNullOrWhiteSpace(Summary.ModelId) ? string.Empty : Summary.ModelId!;

    /// <summary>获取此对话是否由分叉另一对话创建。</summary>
    public bool IsFork => !string.IsNullOrWhiteSpace(Summary.ParentId);

    /// <summary>获取选择器中显示的说明文字。</summary>
    public string Caption => $"{MessageCount} 条消息 · {RelativeTime}" + (IsFork ? " · 分叉" : string.Empty);
}

/// <summary>
/// 对话页的 ViewModel。无参数构造函数可安全地在 XAML 设计器中运行；
/// 请在页面加载完成且 <see cref="AiToolsWpf.Initialize(AiToolsWpfOptions)"/> 执行后调用一次 <see cref="InitializeAsync"/>。
/// </summary>
public sealed class ChatViewModel : ObservableObject
{
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SynchronizationContext? _syncContext;
    private readonly Dispatcher? _dispatcher;
    private AppSettings? _settings;
    private CancellationTokenSource? _cts;
    private bool _suppressPersist;
    private bool _lastTurnRolledBack;
    private bool _isInitializing;
    private bool _isBusy;
    private bool _hasUsableProvider;
    private string _inputText = string.Empty;
    private string _statusMessage = string.Empty;
    private StatusKind _statusKind = StatusKind.Info;
    private ProviderChoice? _selectedProvider;
    private ModelChoice? _selectedModel;
    private ConversationRecord? _currentConversation;
    private bool _isResumePickerOpen;
    private bool _isLoadingConversations;
    private string _conversationTitle = "新对话";
    private int _baselineMessageCount;

    /// <summary>创建对话 ViewModel。在 <see cref="InitializeAsync"/> 运行之前不会访问全局服务。</summary>
    public ChatViewModel()
    {
        _syncContext = SynchronizationContext.Current;
        _dispatcher = Application.Current?.Dispatcher;

        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        StopCommand = new RelayCommand(Stop, () => IsBusy);
        ClearCommand = new RelayCommand(Clear, () => !IsBusy);
        NewConversationCommand = new RelayCommand(NewConversation, () => !IsBusy);
        RetryCommand = new RelayCommand(Retry, () => CanRetry);
        CopyLastCommand = new RelayCommand(CopyLast, () => HasAssistantText);
        OpenSettingsCommand = new RelayCommand(RequestOpenSettings);
        ResumeCommand = new AsyncRelayCommand(OpenResumePickerAsync, () => !IsBusy && CanPersist);
        ForkCommand = new AsyncRelayCommand(ForkAsync, () => !IsBusy && CanFork);
        LoadConversationCommand = new AsyncRelayCommand(parameter => LoadConversationAsync(parameter as ConversationItemViewModel), _ => !IsBusy);
        DeleteConversationCommand = new AsyncRelayCommand(parameter => DeleteConversationAsync(parameter as ConversationItemViewModel), _ => !IsBusy);
        CloseResumeCommand = new RelayCommand(CloseResumePicker);

        Messages.CollectionChanged += (_, _) => RaiseCommandStates();

        if (AiToolsWpf.IsInitialized)
        {
            SetStatus("就绪。", StatusKind.Info);
        }
        else
        {
            SetStatus("AiToolsWpf 尚未初始化。请在应用启动时调用 AiToolsWpf.Initialize(options)。", StatusKind.Warning);
        }
    }

    /// <summary>当视图应将消息列表滚动到末尾时引发。</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>当宿主应导航到设置页时引发。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary><see cref="SettingsRequested"/> 的别名，供偏好动作式命名的宿主使用。</summary>
    public event EventHandler? OpenSettingsRequested
    {
        add => SettingsRequested += value;
        remove => SettingsRequested -= value;
    }

    /// <summary>获取按配置顺序排列的可用服务商。</summary>
    public ObservableCollection<ProviderChoice> Providers { get; } = new();

    /// <summary>获取显示的对话消息。</summary>
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    /// <summary>获取或设置所选服务商。更改后会选中该服务商的默认 model。</summary>
    public ProviderChoice? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            var previousSuppression = _suppressPersist;
            _suppressPersist = true;
            try
            {
                if (!SetProperty(ref _selectedProvider, value))
                {
                    return;
                }

                var preferredModelId = _settings?.SelectedModelId;
                SelectedModel = value?.Models.FirstOrDefault(model =>
                                   string.Equals(model.Config.Id, preferredModelId, StringComparison.OrdinalIgnoreCase))
                               ?? value?.Models.FirstOrDefault();
            }
            finally
            {
                _suppressPersist = previousSuppression;
            }

            RaiseCommandStates();
            if (!_isInitializing && !_suppressPersist)
            {
                PersistSelection();
            }
        }
    }

    /// <summary>获取或设置所选 model。</summary>
    public ModelChoice? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetProperty(ref _selectedModel, value))
            {
                return;
            }

            RaiseCommandStates();
            if (!_isInitializing && !_suppressPersist)
            {
                PersistSelection();
            }
        }
    }

    /// <summary>获取是否至少有一个带已启用 model 的服务商可用。</summary>
    public bool HasUsableProvider
    {
        get => _hasUsableProvider;
        private set
        {
            if (SetProperty(ref _hasUsableProvider, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>获取或设置输入框中的文本。</summary>
    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value ?? string.Empty))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>获取当前是否有一轮请求正在 streaming。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>获取是否没有正在运行的请求。适用于 <c>DataTrigger</c> Binding。</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>获取工具栏中显示的当前状态文本。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>获取 <see cref="StatusMessage"/> 的严重级别。</summary>
    public StatusKind StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    /// <summary>获取当前状态是否可以发送请求。</summary>
    public bool CanSend =>
        !IsBusy
        && HasUsableProvider
        && SelectedProvider != null
        && SelectedModel != null
        && !string.IsNullOrWhiteSpace(InputText);

    /// <summary>获取是否可以重试最后一条用户消息。</summary>
    public bool CanRetry => !IsBusy && Messages.Any(message => message.IsUser);

    /// <summary>获取是否存在包含文本的助手消息。</summary>
    public bool HasAssistantText => Messages.Any(message => !message.IsUser && !string.IsNullOrWhiteSpace(message.Text));

    /// <summary>获取发送 <see cref="InputText"/> 的 Command。CanExecute 要求消息非空。</summary>
    public AsyncRelayCommand SendCommand { get; }

    /// <summary>获取取消当前请求的 Command。仅在忙碌时启用。</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>获取清空对话的 Command。</summary>
    public RelayCommand ClearCommand { get; }

    /// <summary>获取开始新对话的 Command。</summary>
    public RelayCommand NewConversationCommand { get; }

    /// <summary>获取重新发送最后一条用户消息的 Command。</summary>
    public RelayCommand RetryCommand { get; }

    /// <summary>获取将最后一条助手回复复制到剪贴板的 Command。</summary>
    public RelayCommand CopyLastCommand { get; }

    /// <summary>获取引发 <see cref="SettingsRequested"/> 的 Command。</summary>
    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>获取打开 /resume 对话选择器的 Command。</summary>
    public AsyncRelayCommand ResumeCommand { get; }

    /// <summary>获取将当前对话分叉为新对话的 Command（/fork）。</summary>
    public AsyncRelayCommand ForkCommand { get; }

    /// <summary>获取加载 resume 选择器中选定对话的 Command。</summary>
    public AsyncRelayCommand LoadConversationCommand { get; }

    /// <summary>获取从 resume 选择器中删除对话的 Command。</summary>
    public AsyncRelayCommand DeleteConversationCommand { get; }

    /// <summary>获取关闭 resume 选择器的 Command。</summary>
    public RelayCommand CloseResumeCommand { get; }

    /// <summary>获取 resume 选择器中显示的已保存对话。</summary>
    public ObservableCollection<ConversationItemViewModel> SavedConversations { get; } = new();

    /// <summary>获取或设置 resume 选择器是否处于打开状态。</summary>
    public bool IsResumePickerOpen
    {
        get => _isResumePickerOpen;
        private set => SetProperty(ref _isResumePickerOpen, value);
    }

    /// <summary>获取对话列表是否正在加载。</summary>
    public bool IsLoadingConversations
    {
        get => _isLoadingConversations;
        private set => SetProperty(ref _isLoadingConversations, value);
    }

    /// <summary>获取或设置当前对话的标题。</summary>
    public string ConversationTitle
    {
        get => _conversationTitle;
        private set => SetProperty(ref _conversationTitle, value ?? "新对话");
    }

    /// <summary>获取当前对话 ID（尚未保存的全新对话为 null）。</summary>
    public string? CurrentConversationId => _currentConversation?.Id;

    /// <summary>获取是否有可用的会话存储。</summary>
    private bool CanPersist => AiToolsWpf.IsInitialized && AiToolsWpf.Options.ConversationStore != null;

    /// <summary>获取当前对话是否有可分叉的内容。</summary>
    public bool CanFork => CanPersist && _history.Count > 0;

    /// <summary>
    /// 加载设置，重新构建服务商和 model 选择器，并恢复之前的选择。
    /// 设置变更后可安全地再次调用。永不抛出异常。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!AiToolsWpf.IsInitialized)
        {
            HasUsableProvider = false;
            SetStatus("AiToolsWpf 尚未初始化。请在应用启动时调用 AiToolsWpf.Initialize(options)。", StatusKind.Warning);
            return;
        }

        try
        {
            var settings = await AiToolsWpf.GetSettingsAsync();
            _settings = settings;

            var previousProviderId = settings.SelectedProviderId;
            var previousModelId = settings.SelectedModelId;

            _isInitializing = true;
            try
            {
                var choices = settings.Providers
                    .Where(provider => provider.IsUsable())
                    .Select(provider => new ProviderChoice(provider))
                    .Where(choice => choice.Models.Count > 0)
                    .ToList();

                Providers.Clear();
                foreach (var choice in choices)
                {
                    Providers.Add(choice);
                }

                HasUsableProvider = Providers.Count > 0;

                var provider = Providers.FirstOrDefault(choice =>
                                   string.Equals(choice.Config.Id, previousProviderId, StringComparison.OrdinalIgnoreCase))
                               ?? Providers.FirstOrDefault();
                SelectedProvider = provider;

                if (provider != null)
                {
                    SelectedModel = provider.Models.FirstOrDefault(model =>
                                        string.Equals(model.Config.Id, previousModelId, StringComparison.OrdinalIgnoreCase))
                                    ?? provider.Models.FirstOrDefault();
                }
                else
                {
                    SelectedModel = null;
                }
            }
            finally
            {
                _isInitializing = false;
            }

            RaiseCommandStates();

            if (Providers.Count == 0)
            {
                SetStatus("尚未配置可用的服务商，请先打开设置添加。", StatusKind.Warning);
            }
            else
            {
                SetStatus($"就绪 · {Providers.Count} 个服务商可用", StatusKind.Info);

                if (!string.Equals(previousProviderId, SelectedProvider?.Config.Id, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(previousModelId, SelectedModel?.Config.Id, StringComparison.OrdinalIgnoreCase))
                {
                    PersistSelection();
                }
            }
        }
        catch (Exception ex)
        {
            HasUsableProvider = false;
            SetStatus($"加载配置失败：{ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>将 <see cref="InputText"/> 作为新的用户轮次发送。输入为空或忙碌时不执行任何操作。</summary>
    public async Task SendAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var text = InputText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!AiToolsWpf.IsInitialized)
        {
            SetStatus("AiToolsWpf 尚未初始化，无法发送消息。", StatusKind.Warning);
            return;
        }

        if (SelectedProvider == null || SelectedModel == null)
        {
            SetStatus("请先选择可用的服务商和模型。", StatusKind.Warning);
            return;
        }

        InputText = string.Empty;
        try
        {
            await SendCoreAsync(text);
        }
        catch (Exception ex)
        {
            SetStatus($"发送失败：{ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>取消当前请求。</summary>
    public void Stop()
    {
        if (!IsBusy)
        {
            return;
        }

        SetStatus("正在停止…", StatusKind.Warning);
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
        }
    }

    /// <summary>清空显示的对话和请求历史。</summary>
    public void Clear()
    {
        if (IsBusy)
        {
            return;
        }

        _ = ClearAsync();
    }

    private async Task ClearAsync()
    {
        await PersistCurrentConversationAsync().ConfigureAwait(false);
        OnUi(() =>
        {
            Messages.Clear();
            _history.Clear();
            _baselineMessageCount = 0;
            _lastTurnRolledBack = false;
            RaiseCommandStates();
            SetStatus(HasUsableProvider ? "对话已清空。" : "尚未配置可用的服务商，请先打开设置添加。",
                HasUsableProvider ? StatusKind.Info : StatusKind.Warning);
        });
    }

    /// <summary>通过清空所有消息来开始新对话。</summary>
    public void NewConversation()
    {
        if (IsBusy)
        {
            return;
        }

        _ = StartNewConversationAsync();
    }

    private async Task StartNewConversationAsync()
    {
        // 保存当前会话后再开启新会话，避免丢失未落盘的内容。
        await PersistCurrentConversationAsync().ConfigureAwait(false);
        OnUi(() =>
        {
            Messages.Clear();
            _history.Clear();
            _baselineMessageCount = 0;
            _lastTurnRolledBack = false;
            _currentConversation = null;
            ConversationTitle = "新对话";
            IsResumePickerOpen = false;
            RaiseCommandStates();
            SetStatus(HasUsableProvider ? "已开始新对话。" : "尚未配置可用的服务商，请先打开设置添加。",
                HasUsableProvider ? StatusKind.Info : StatusKind.Warning);
        });
    }

    /// <summary>打开 /resume 选择器并刷新已保存的对话列表。</summary>
    public async Task OpenResumePickerAsync()
    {
        if (!AiToolsWpf.IsInitialized || AiToolsWpf.Options.ConversationStore == null)
        {
            SetStatus("未配置会话存储，无法恢复会话。", StatusKind.Warning);
            return;
        }

        IsResumePickerOpen = true;
        IsLoadingConversations = true;
        try
        {
            var store = AiToolsWpf.ConversationStore;
            var summaries = await store.ListAsync().ConfigureAwait(false);
            OnUi(() =>
            {
                SavedConversations.Clear();
                foreach (var summary in summaries)
                {
                    SavedConversations.Add(new ConversationItemViewModel(summary));
                }

                SetStatus(SavedConversations.Count == 0
                    ? "还没有已保存的对话。"
                    : $"共 {SavedConversations.Count} 个已保存对话。", StatusKind.Info);
            });
        }
        catch (Exception ex)
        {
            SetStatus($"读取会话列表失败：{ex.Message}", StatusKind.Error);
        }
        finally
        {
            OnUi(() => IsLoadingConversations = false);
        }
    }

    /// <summary>关闭 /resume 选择器。</summary>
    public void CloseResumePicker()
    {
        IsResumePickerOpen = false;
        SetStatus("已取消恢复。", StatusKind.Info);
    }

    /// <summary>加载已保存的对话并替换当前对话。</summary>
    public async Task LoadConversationAsync(ConversationItemViewModel? item)
    {
        if (item == null || IsBusy)
        {
            return;
        }

        try
        {
            var store = AiToolsWpf.ConversationStore;
            var record = await store.LoadAsync(item.Id).ConfigureAwait(false);
            if (record == null)
            {
                SetStatus("该对话已不存在。", StatusKind.Warning);
                await OpenResumePickerAsync().ConfigureAwait(false);
                return;
            }

            await PersistCurrentConversationAsync().ConfigureAwait(false);

            OnUi(() =>
            {
                ApplyConversation(record);
                IsResumePickerOpen = false;
                SetStatus($"已恢复对话「{record.Title}」（{record.Messages.Count} 条消息）。", StatusKind.Success);
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            });

            RestoreSelection(record);
        }
        catch (Exception ex)
        {
            SetStatus($"恢复对话失败：{ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>从 resume 选择器中删除已保存的对话。</summary>
    public async Task DeleteConversationAsync(ConversationItemViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        try
        {
            var store = AiToolsWpf.ConversationStore;
            await store.DeleteAsync(item.Id).ConfigureAwait(false);
            OnUi(() => SavedConversations.Remove(item));
            SetStatus($"已删除对话「{item.Title}」。", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"删除对话失败：{ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>
    /// 分叉当前对话：先持久化，然后使用新的 ID 创建副本并切换到该副本。
    /// 原对话保持不变。
    /// </summary>
    public async Task ForkAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!AiToolsWpf.IsInitialized || AiToolsWpf.Options.ConversationStore == null)
        {
            SetStatus("未配置会话存储，无法分叉会话。", StatusKind.Warning);
            return;
        }

        if (_history.Count == 0)
        {
            SetStatus("当前对话为空，无法分叉。", StatusKind.Warning);
            return;
        }

        try
        {
            var store = AiToolsWpf.ConversationStore;
            await PersistCurrentConversationAsync().ConfigureAwait(false);

            var source = _currentConversation;
            if (source == null)
            {
                SetStatus("当前对话尚未保存，无法分叉。", StatusKind.Warning);
                return;
            }

            var fork = await store.ForkAsync(source).ConfigureAwait(false);
            OnUi(() =>
            {
                ApplyConversation(fork);
                SetStatus($"已从「{source.Title}」分叉出新对话「{fork.Title}」。", StatusKind.Success);
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            });

            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            SetStatus($"分叉对话失败：{ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>引发 <see cref="SettingsRequested"/>，以便宿主导航到设置页。</summary>
    public void RequestOpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private async Task SendCoreAsync(string userText)
    {
        if (IsBusy)
        {
            return;
        }

        if (!AiToolsWpf.IsInitialized)
        {
            SetStatus("AiToolsWpf 尚未初始化，无法发送消息。", StatusKind.Warning);
            return;
        }

        var providerChoice = SelectedProvider;
        var modelChoice = SelectedModel;
        if (providerChoice == null || modelChoice == null)
        {
            SetStatus("请先选择可用的服务商和模型。", StatusKind.Warning);
            return;
        }

        var provider = providerChoice.Config;
        var model = modelChoice.Config;
        var userMessage = ChatMessage.User(userText);
        _history.Add(userMessage);
        _lastTurnRolledBack = false;

        var userBubble = new ChatMessageViewModel(true, userText);
        var assistantBubble = new ChatMessageViewModel(false);
        Messages.Add(userBubble);
        Messages.Add(assistantBubble);

        var buffer = new StreamBuffer();
        var stopwatch = Stopwatch.StartNew();

        void ScheduleFlush()
        {
            if (Interlocked.Exchange(ref buffer.FlushPending, 1) != 0)
            {
                return;
            }

            OnUi(() =>
            {
                Interlocked.Exchange(ref buffer.FlushPending, 0);
                if (!assistantBubble.IsStreaming)
                {
                    return;
                }

                var text = buffer.SnapshotText();
                var reasoning = buffer.SnapshotReasoning();
                assistantBubble.Text = text;
                if (reasoning.Length > 0)
                {
                    assistantBubble.Reasoning = reasoning;
                }

                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            });
        }

        void AppendDelta(string? delta, bool isReasoning)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            buffer.Append(delta, isReasoning);
            ScheduleFlush();
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        assistantBubble.IsStreaming = true;
        SetStatus("正在生成…", StatusKind.Info);
        OnUi(() => ScrollToEndRequested?.Invoke(this, EventArgs.Empty));

        try
        {
            var options = AiToolsWpf.Options;
            var registrations = BuildToolRegistrations(options);
            var tools = registrations.Select(registration => registration.Definition).ToArray();

            var systemPrompt = string.IsNullOrWhiteSpace(_settings?.SystemPrompt)
                ? options.DefaultSystemPrompt
                : _settings!.SystemPrompt;

            var historySnapshot = _history.ToArray();
            ChatRequest request = new()
            {
                Model = model.Id,
                SystemPrompt = systemPrompt,
                Messages = historySnapshot,
                Tools = tools,
                ToolChoice = tools.Length > 0 ? ToolChoice.Auto : null
            };

            if (options.RequestBuilder != null)
            {
                var custom = options.RequestBuilder(new ChatRequestContext
                {
                    Provider = provider,
                    Model = model,
                    Messages = historySnapshot,
                    Tools = tools
                });

                if (custom != null)
                {
                    request = custom;
                }
            }

            var client = AiToolsWpf.CreateChatClient(provider);

            async Task<string> InvokeToolAsync(ToolCall call, CancellationToken cancellationToken)
            {
                AddToolChip(assistantBubble, call);

                var registration = registrations.FirstOrDefault(item =>
                    string.Equals(item.Definition.Name, call.Name, StringComparison.OrdinalIgnoreCase));
                if (registration == null)
                {
                    return JsonSerializer.Serialize(new { error = $"未知工具：{call.Name}" });
                }

                try
                {
                    var result = await registration.Provider.InvokeAsync(call, cancellationToken).ConfigureAwait(false);
                    return result ?? string.Empty;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }
            }

            ChatResponse response;
            if (tools.Length > 0)
            {
                response = await client.StreamWithToolsExactAsync(
                    request,
                    InvokeToolAsync,
                    onText: delta => AppendDelta(delta, isReasoning: false),
                    onReasoning: delta => AppendDelta(delta, isReasoning: true),
                    maxToolRounds: Math.Max(0, options.MaxToolRounds),
                    cancellationToken: token);
            }
            else
            {
                var turn = await client.StreamTurnAsync(
                    request,
                    update =>
                    {
                        AppendDelta(update.TextDelta, isReasoning: false);
                        AppendDelta(update.ReasoningDelta, isReasoning: true);
                    },
                    token);

                response = new ChatResponse
                {
                    Message = turn.Message,
                    FinishReason = turn.FinishReason,
                    Usage = turn.Usage
                };
            }

            var streamedText = buffer.SnapshotText();
            var streamedReasoning = buffer.SnapshotReasoning();

            var fallbackText = response.GetText();
            var visibleText = streamedText.Length > 0 ? streamedText : fallbackText;
            var historyText = visibleText;
            var usageText = FormatUsage(response.Usage);

            if (!string.IsNullOrWhiteSpace(historyText))
            {
                _history.Add(ChatMessage.Assistant(historyText));
            }

            OnUi(() =>
            {
                assistantBubble.Text = visibleText;
                if (streamedReasoning.Length > 0)
                {
                    assistantBubble.Reasoning = streamedReasoning;
                }

                assistantBubble.UsageText = usageText ?? string.Empty;
                assistantBubble.IsStreaming = false;
                SetStatus(usageText == null ? "完成。" : $"完成 · {usageText}", StatusKind.Success);
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            });

            try
            {
                options.OnTurnCompleted?.Invoke(new ChatTurnCompletedContext
                {
                    Provider = provider,
                    Model = model,
                    Response = response,
                    Duration = stopwatch.Elapsed
                });
            }
            catch (Exception)
            {
            }
        }
        catch (OperationCanceledException)
        {
            RemoveLastUserTurnFromHistory();
            _lastTurnRolledBack = true;
            var partialText = buffer.SnapshotText();
            var partialReasoning = buffer.SnapshotReasoning();
            OnUi(() =>
            {
                var current = string.IsNullOrEmpty(assistantBubble.Text) ? partialText : assistantBubble.Text;
                assistantBubble.Text = current.Length == 0 ? "(已停止)" : current + "\n\n(已停止)";
                if (partialReasoning.Length > 0)
                {
                    assistantBubble.Reasoning = partialReasoning;
                }

                assistantBubble.IsStreaming = false;
                SetStatus("已停止生成。", StatusKind.Warning);
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (AiServiceException ex)
        {
            HandleTurnError(assistantBubble, DescribeServiceException(ex), buffer);
        }
        catch (Exception ex)
        {
            HandleTurnError(assistantBubble, ex.Message, buffer);
        }
        finally
        {
            stopwatch.Stop();
            try
            {
                _cts?.Dispose();
            }
            catch (Exception)
            {
            }

            _cts = null;

            OnUi(() =>
            {
                assistantBubble.IsStreaming = false;
                IsBusy = false;
                RaiseCommandStates();
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            });

            // 每轮结束后自动保存（失败/取消的轮次已回滚，不会写入历史）。
            await PersistCurrentConversationAsync().ConfigureAwait(false);
        }
    }

    private void HandleTurnError(
        ChatMessageViewModel assistantBubble,
        string error,
        StreamBuffer buffer)
    {
        RemoveLastUserTurnFromHistory();
        _lastTurnRolledBack = true;
        var partialText = buffer.SnapshotText();
        var partialReasoning = buffer.SnapshotReasoning();
        var message = string.IsNullOrWhiteSpace(error) ? "请求失败。" : error;

        OnUi(() =>
        {
            if (!string.IsNullOrEmpty(partialText))
            {
                assistantBubble.Text = partialText;
            }

            if (partialReasoning.Length > 0)
            {
                assistantBubble.Reasoning = partialReasoning;
            }

            assistantBubble.IsStreaming = false;
            assistantBubble.ErrorText = message;
            SetStatus($"请求失败：{message}", StatusKind.Error);
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Retry()
    {
        _ = RetryAsync();
    }

    private async Task RetryAsync()
    {
        try
        {
            if (IsBusy)
            {
                return;
            }

            if (!AiToolsWpf.IsInitialized || SelectedProvider == null || SelectedModel == null)
            {
                SetStatus("请先选择可用的服务商和模型。", StatusKind.Warning);
                return;
            }

            var lastUserIndex = -1;
            for (var index = Messages.Count - 1; index >= 0; index--)
            {
                if (Messages[index].IsUser)
                {
                    lastUserIndex = index;
                    break;
                }
            }

            if (lastUserIndex < 0)
            {
                return;
            }

            var text = Messages[lastUserIndex].Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            while (Messages.Count > lastUserIndex)
            {
                Messages.RemoveAt(Messages.Count - 1);
            }

            if (!_lastTurnRolledBack)
            {
                for (var index = _history.Count - 1; index >= 0; index--)
                {
                    if (_history[index].Role == ChatRole.User)
                    {
                        _history.RemoveRange(index, _history.Count - index);
                        break;
                    }
                }
            }

            await SendCoreAsync(text);
        }
        catch (Exception ex)
        {
            SetStatus($"重试失败：{ex.Message}", StatusKind.Error);
        }
    }

    private void CopyLast()
    {
        try
        {
            var message = Messages.LastOrDefault(item => !item.IsUser && !string.IsNullOrWhiteSpace(item.Text));
            if (message == null)
            {
                return;
            }

            Clipboard.SetText(message.Text);
            SetStatus("已复制最后一条回复。", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"复制失败：{ex.Message}", StatusKind.Warning);
        }
    }

    private void PersistSelection()
    {
        var settings = _settings;
        if (settings == null || !AiToolsWpf.IsInitialized)
        {
            return;
        }

        settings.SelectedProviderId = SelectedProvider?.Config.Id;
        settings.SelectedModelId = SelectedModel?.Config.Id;
        _ = PersistSelectionAsync(settings);
    }

    private async Task PersistSelectionAsync(AppSettings settings)
    {
        try
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await AiToolsWpf.SaveSettingsAsync(settings).ConfigureAwait(false);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (Exception ex)
        {
            OnUi(() => SetStatus($"保存选择失败：{ex.Message}", StatusKind.Warning));
        }
    }

    private static List<ToolRegistration> BuildToolRegistrations(AiToolsWpfOptions options)
    {
        var registrations = new List<ToolRegistration>();
        if (!options.EnableTools)
        {
            return registrations;
        }

        foreach (var provider in AiToolsWpf.GetToolProviders())
        {
            IReadOnlyList<ToolDefinition> definitions;
            try
            {
                definitions = provider.GetTools();
            }
            catch (Exception)
            {
                continue;
            }

            if (definitions == null)
            {
                continue;
            }

            foreach (var definition in definitions)
            {
                if (string.IsNullOrWhiteSpace(definition.Name)
                    || registrations.Any(item => string.Equals(item.Definition.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                registrations.Add(new ToolRegistration(definition, provider));
            }
        }

        return registrations;
    }

    private void AddToolChip(ChatMessageViewModel message, ToolCall call)
    {
        var arguments = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson.Trim();
        arguments = arguments.Replace("\r", " ").Replace("\n", " ");
        if (arguments.Length > 180)
        {
            arguments = arguments.Substring(0, 180) + "…";
        }

        var display = $"🔧 {call.Name}({arguments})";
        OnUi(() =>
        {
            message.AddToolCall(display);
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private static string? FormatUsage(Usage? usage)
    {
        if (usage == null)
        {
            return null;
        }

        if (usage.InputTokens.HasValue && usage.OutputTokens.HasValue)
        {
            return $"{usage.InputTokens.Value}→{usage.OutputTokens.Value} tokens";
        }

        if (usage.InputTokens.HasValue)
        {
            return $"输入 {usage.InputTokens.Value} tokens";
        }

        if (usage.OutputTokens.HasValue)
        {
            return $"输出 {usage.OutputTokens.Value} tokens";
        }

        return usage.TotalTokens.HasValue ? $"共 {usage.TotalTokens.Value} tokens" : null;
    }

    private static string DescribeServiceException(AiServiceException exception)
    {
        var details = new List<string>();
        if (exception.StatusCode.HasValue)
        {
            details.Add($"HTTP {exception.StatusCode.Value}");
        }

        if (!string.IsNullOrWhiteSpace(exception.ErrorCode))
        {
            details.Add($"错误码 {exception.ErrorCode}");
        }

        if (exception is AiRateLimitException rateLimit && rateLimit.RetryAfter.HasValue)
        {
            details.Add($"建议 {Math.Max(1, (int)Math.Ceiling(rateLimit.RetryAfter.Value.TotalSeconds))} 秒后重试");
        }

        var message = string.IsNullOrWhiteSpace(exception.Message) ? "服务商返回错误。" : exception.Message;
        if (details.Count > 0)
        {
            message += $"（{string.Join("，", details)}）";
        }

        return string.IsNullOrWhiteSpace(exception.ProviderName)
            ? message
            : $"{exception.ProviderName}：{message}";
    }

    private void RemoveLastUserTurnFromHistory()
    {
        for (var index = _history.Count - 1; index >= 0; index--)
        {
            if (_history[index].Role == ChatRole.User)
            {
                _history.RemoveRange(index, _history.Count - index);
                return;
            }
        }
    }

    /// <summary>使用持久化记录替换显示的对话。</summary>
    private void ApplyConversation(ConversationRecord record)
    {
        _history.Clear();
        Messages.Clear();

        foreach (var message in record.Messages)
        {
            _history.Add(message.ToChatMessage());

            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bubble = new ChatMessageViewModel(
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase),
                message.Text);
            Messages.Add(bubble);
        }

        _currentConversation = record;
        _baselineMessageCount = _history.Count;
        ConversationTitle = string.IsNullOrWhiteSpace(record.Title) ? "新对话" : record.Title;
        _lastTurnRolledBack = false;
        RaiseCommandStates();
    }

    /// <summary>当对话所保存的服务商/model 仍在配置中时，选中它们。</summary>
    private void RestoreSelection(ConversationRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ProviderId))
        {
            return;
        }

        var provider = Providers.FirstOrDefault(choice =>
            string.Equals(choice.Config.Id, record.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider == null)
        {
            SetStatus($"对话使用的服务商「{record.ProviderId}」已不存在，继续使用当前选择。", StatusKind.Warning);
            return;
        }

        _suppressPersist = true;
        try
        {
            SelectedProvider = provider;
            if (!string.IsNullOrWhiteSpace(record.ModelId))
            {
                var model = provider.Models.FirstOrDefault(choice =>
                    string.Equals(choice.Config.Id, record.ModelId, StringComparison.OrdinalIgnoreCase));
                if (model != null)
                {
                    SelectedModel = model;
                }
            }
        }
        finally
        {
            _suppressPersist = false;
        }
    }

    /// <summary>在启用自动保存且有内容可保存时，保存当前对话。</summary>
    private async Task PersistCurrentConversationAsync()
    {
        if (!AiToolsWpf.IsInitialized || AiToolsWpf.Options.ConversationStore == null || !AiToolsWpf.Options.AutoSaveConversations)
        {
            return;
        }

        if (_history.Count == 0)
        {
            return;
        }

        try
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var record = _currentConversation ?? new ConversationRecord();
                record.Messages = _history.Select(message => ConversationMessage.FromChatMessage(message)).ToList();
                record.ProviderId = SelectedProvider?.Config.Id ?? record.ProviderId;
                record.ModelId = SelectedModel?.Config.Id ?? record.ModelId;
                record.Title = string.IsNullOrWhiteSpace(record.Title) || record.Title == "新对话"
                    ? ConversationRecord.DeriveTitle(record.Messages)
                    : record.Title;

                await AiToolsWpf.ConversationStore.SaveAsync(record).ConfigureAwait(false);

                _currentConversation = record;
                OnUi(() => ConversationTitle = record.Title);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch (Exception ex)
        {
            OnUi(() => SetStatus($"保存对话失败：{ex.Message}", StatusKind.Warning));
        }
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(HasAssistantText));
        OnPropertyChanged(nameof(CanFork));
        SendCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        NewConversationCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        CopyLastCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        ForkCommand.RaiseCanExecuteChanged();
        LoadConversationCommand.RaiseCanExecuteChanged();
        DeleteConversationCommand.RaiseCanExecuteChanged();
        CloseResumeCommand.RaiseCanExecuteChanged();
    }

    private void SetStatus(string message, StatusKind kind)
        => OnUi(() =>
        {
            StatusMessage = message;
            StatusKind = kind;
        });

    private void OnUi(Action action)
    {
        try
        {
            if (_dispatcher != null)
            {
                if (_dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    _dispatcher.BeginInvoke(action);
                }

                return;
            }

            if (_syncContext != null && SynchronizationContext.Current != _syncContext)
            {
                _syncContext.Post(_ => action(), null);
                return;
            }

            action();
        }
        catch (Exception)
        {
            // Dispatcher 可能已在关闭中；UI 更新可以安全地丢弃。
        }
    }

    private sealed class ToolRegistration
    {
        public ToolRegistration(ToolDefinition definition, IChatToolProvider provider)
        {
            Definition = definition;
            Provider = provider;
        }

        public ToolDefinition Definition { get; }

        public IChatToolProvider Provider { get; }
    }

    private sealed class StreamBuffer
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();

        public int FlushPending;

        public void Append(string delta, bool isReasoning)
        {
            lock (_gate)
            {
                (isReasoning ? _reasoning : _text).Append(delta);
            }
        }

        public string SnapshotText()
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }

        public string SnapshotReasoning()
        {
            lock (_gate)
            {
                return _reasoning.ToString();
            }
        }
    }
}
