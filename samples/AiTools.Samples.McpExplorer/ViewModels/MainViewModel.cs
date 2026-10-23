using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Mcp;
using AiTools.Samples.McpDiscovery;
using AiTools.Samples.McpExplorer.ViewModels;

namespace AiTools.Samples.McpExplorer;

/// <summary>主窗口状态：注册表发现、连接管理和工具调用。</summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 300;

    private McpClient? _client;
    private IMcpTransport? _transport;
    private ServerItemViewModel? _selectedServer;
    private ServerItemViewModel? _connectedServer;
    private ToolItemViewModel? _selectedTool;
    private ToolItemViewModel? _activeTool;
    private bool _isConnecting;
    private bool _isInvoking;
    private bool _isRefreshing;
    private bool _autoRefresh = true;
    private bool _isRawJsonMode;
    private string _rawArgumentsJson = "{}";
    private string _statusText = "就绪";
    private string _connectionStatus = "未连接";
    private string _connectionDetail = "尚未连接任何 MCP 服务器";
    private string _serverInfo = string.Empty;
    private string _serverInfoDetail = string.Empty;
    private bool _serverInfoVisible;
    private string _toolsEmptyText = "连接服务器后显示工具目录";
    private string _resultStatus = string.Empty;
    private string _resultSummary = string.Empty;
    private string _rawResultJson = string.Empty;
    private string _resultError = string.Empty;
    private bool _hasResult;
    private bool _resultIsError;
    private string _invokeButtonText = "调用";
    private string _resolvedEndpoint = string.Empty;
    private string _resourceDetail = string.Empty;
    private string _resourceDetailTitle = string.Empty;
    private McpResource? _selectedResource;
    private McpPrompt? _selectedPrompt;
    private string _promptResultText = string.Empty;
    private string _promptResultTitle = string.Empty;
    private readonly Dictionary<string, string> _promptArgumentValues = new(StringComparer.Ordinal);
    private string _readResourceUri = string.Empty;

    /// <summary>创建 ViewModel。</summary>
    /// <param name="registryPath">状态栏中显示的注册表文件路径。</param>
    public MainViewModel(string registryPath)
    {
        RegistryPath = registryPath;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ConnectOrDisconnectCommand = new AsyncRelayCommand(ConnectOrDisconnectAsync, CanConnectOrDisconnect);
        InvokeToolCommand = new AsyncRelayCommand(InvokeToolAsync, () => !IsBusy && _client != null && _activeTool != null);
        ToggleRawJsonCommand = new RelayCommand(ToggleRawJson);
        LoadResourcesCommand = new AsyncRelayCommand(LoadResourcesAsync, () => !IsBusy && _client != null);
        ReadResourceCommand = new AsyncRelayCommand(
            ReadSelectedResourceAsync,
            () => !IsBusy && _client != null && (SelectedResource != null || !string.IsNullOrWhiteSpace(ReadResourceUri)));
        LoadPromptsCommand = new AsyncRelayCommand(LoadPromptsAsync, () => !IsBusy && _client != null);
        GetPromptCommand = new AsyncRelayCommand(GetSelectedPromptAsync, () => !IsBusy && _client != null && SelectedPrompt != null);
        ClearLogCommand = new RelayCommand(() => Logs.Clear());
    }

    /// <summary>获取状态栏中显示的注册表文件路径。</summary>
    public string RegistryPath { get; }

    /// <summary>获取注册表中发现的所有服务器。</summary>
    public ObservableCollection<ServerItemViewModel> Servers { get; } = new();

    /// <summary>获取已连接服务器的工具。</summary>
    public ObservableCollection<ToolItemViewModel> Tools { get; } = new();

    /// <summary>获取已连接服务器的资源。</summary>
    public ObservableCollection<McpResource> Resources { get; } = new();

    /// <summary>获取已连接服务器的提示词。</summary>
    public ObservableCollection<McpPrompt> Prompts { get; } = new();

    /// <summary>获取当前活动工具的架构驱动参数表单。</summary>
    public ObservableCollection<ToolParameterViewModel> Parameters { get; } = new();

    /// <summary>获取提示词参数表单。</summary>
    public ObservableCollection<PromptArgumentViewModel> PromptArguments { get; } = new();

    /// <summary>获取已渲染的结果块。</summary>
    public ObservableCollection<ResultBlockViewModel> ResultBlocks { get; } = new();

    /// <summary>获取活动日志，最新的在前。</summary>
    public ObservableCollection<ActivityLogEntry> Logs { get; } = new();

    /// <summary>获取刷新命令。</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>获取连接/断开命令。</summary>
    public AsyncRelayCommand ConnectOrDisconnectCommand { get; }

    /// <summary>获取工具调用命令。</summary>
    public AsyncRelayCommand InvokeToolCommand { get; }

    /// <summary>获取原始 JSON 模式切换命令。</summary>
    public RelayCommand ToggleRawJsonCommand { get; }

    /// <summary>获取重新加载资源的命令。</summary>
    public AsyncRelayCommand LoadResourcesCommand { get; }

    /// <summary>获取读取所选资源的命令。</summary>
    public AsyncRelayCommand ReadResourceCommand { get; }

    /// <summary>获取重新加载提示词的命令。</summary>
    public AsyncRelayCommand LoadPromptsCommand { get; }

    /// <summary>获取渲染所选提示词的命令。</summary>
    public AsyncRelayCommand GetPromptCommand { get; }

    /// <summary>获取清空活动日志的命令。</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>获取或设置注册表中选中的服务器。</summary>
    public ServerItemViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (SetProperty(ref _selectedServer, value))
            {
                OnPropertyChanged(nameof(HasSelectedServer));
                OnPropertyChanged(nameof(ConnectButtonText));
                ConnectOrDisconnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>获取是否选中了注册表服务器。</summary>
    public bool HasSelectedServer => SelectedServer != null;

    /// <summary>获取连接/断开按钮的标签。</summary>
    public string ConnectButtonText => IsConnected ? "断开" : "连接";

    /// <summary>获取当前连接的注册表服务器（当连接来自注册表时）。</summary>
    public ServerItemViewModel? ConnectedServer => _connectedServer;

    /// <summary>获取当前是否已连接客户端。</summary>
    public bool IsConnected => _client != null && _client.IsInitialized;

    /// <summary>获取资源管理器是否正在连接。</summary>
    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                OnPropertyChanged(nameof(ConnectionStatusBrush));
                NotifyBusyChanged();
            }
        }
    }

    /// <summary>获取资源管理器是否正在刷新注册表。</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                NotifyBusyChanged();
            }
        }
    }

    /// <summary>获取资源管理器是否正在调用工具。</summary>
    public bool IsInvoking
    {
        get => _isInvoking;
        private set
        {
            if (SetProperty(ref _isInvoking, value))
            {
                InvokeButtonText = value ? "调用中…" : "调用";
                NotifyBusyChanged();
            }
        }
    }

    /// <summary>获取是否有任何操作正在进行。</summary>
    public bool IsBusy => _isConnecting || _isRefreshing || _isInvoking;

    /// <summary>获取或设置注册表列表是否每三秒刷新一次。</summary>
    public bool AutoRefresh
    {
        get => _autoRefresh;
        set => SetProperty(ref _autoRefresh, value);
    }

    /// <summary>获取调用按钮的文本。</summary>
    public string InvokeButtonText
    {
        get => _invokeButtonText;
        private set => SetProperty(ref _invokeButtonText, value);
    }

    /// <summary>获取连接状态标签。</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (SetProperty(ref _connectionStatus, value))
            {
                OnPropertyChanged(nameof(ConnectionStatusBrush));
                OnPropertyChanged(nameof(IsConnected));
            }
        }
    }

    /// <summary>获取用于连接状态指示器的画刷。</summary>
    public System.Windows.Media.Brush ConnectionStatusBrush =>
        IsConnecting ? UiBrushes.Warning : IsConnected ? UiBrushes.Success : UiBrushes.TextSecondary;

    /// <summary>获取额外的连接信息（服务器名称/版本）。</summary>
    public string ConnectionDetail
    {
        get => _connectionDetail;
        private set => SetProperty(ref _connectionDetail, value);
    }

    /// <summary>获取工具列表上方显示的简短服务器信息行。</summary>
    public string ServerInfo
    {
        get => _serverInfo;
        private set => SetProperty(ref _serverInfo, value);
    }

    /// <summary>获取服务器信息详情行（协议、传输方式、端点）。</summary>
    public string ServerInfoDetail
    {
        get => _serverInfoDetail;
        private set => SetProperty(ref _serverInfoDetail, value);
    }

    /// <summary>获取是否应显示服务器信息。</summary>
    public bool ServerInfoVisible
    {
        get => _serverInfoVisible;
        private set => SetProperty(ref _serverInfoVisible, value);
    }

    /// <summary>获取工具列表为空时显示的占位文本。</summary>
    public string ToolsEmptyText
    {
        get => _toolsEmptyText;
        private set => SetProperty(ref _toolsEmptyText, value);
    }

    /// <summary>获取工具目录中选中的工具。</summary>
    public ToolItemViewModel? SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (SetProperty(ref _selectedTool, value))
            {
                SelectTool(value);
            }
        }
    }

    /// <summary>获取调用面板中当前显示的工具。</summary>
    public ToolItemViewModel? ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (SetProperty(ref _activeTool, value))
            {
                OnPropertyChanged(nameof(HasActiveTool));
                OnPropertyChanged(nameof(ActiveToolDescription));
                OnPropertyChanged(nameof(ActiveToolSchemaJson));
                InvokeToolCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>获取是否有活动工具。</summary>
    public bool HasActiveTool => ActiveTool != null;

    /// <summary>获取活动工具的描述。</summary>
    public string ActiveToolDescription => ActiveTool?.Description ?? string.Empty;

    /// <summary>获取活动工具的输入架构（格式化为 JSON）。</summary>
    public string ActiveToolSchemaJson => ActiveTool?.InputSchemaJson ?? string.Empty;

    /// <summary>获取或设置是否以原始 JSON 编辑参数。</summary>
    public bool IsRawJsonMode
    {
        get => _isRawJsonMode;
        private set
        {
            if (SetProperty(ref _isRawJsonMode, value))
            {
                OnPropertyChanged(nameof(IsFormMode));
                OnPropertyChanged(nameof(RawJsonToggleText));
            }
        }
    }

    /// <summary>获取架构驱动的表单是否处于活动状态。</summary>
    public bool IsFormMode => !IsRawJsonMode;

    /// <summary>获取原始 JSON 切换标签。</summary>
    public string RawJsonToggleText => IsRawJsonMode ? "以表单编辑" : "以 JSON 编辑";

    /// <summary>获取或设置原始 JSON 参数文本。</summary>
    public string RawArgumentsJson
    {
        get => _rawArgumentsJson;
        set => SetProperty(ref _rawArgumentsJson, value ?? string.Empty);
    }

    /// <summary>获取是否已有工具调用结果。</summary>
    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    /// <summary>获取上一次调用是否报告了错误。</summary>
    public bool ResultIsError
    {
        get => _resultIsError;
        private set
        {
            if (SetProperty(ref _resultIsError, value))
            {
                OnPropertyChanged(nameof(ResultBadgeText));
                OnPropertyChanged(nameof(ResultBadgeBrush));
            }
        }
    }

    /// <summary>获取结果徽章标签。</summary>
    public string ResultBadgeText => ResultIsError ? "调用失败" : "调用成功";

    /// <summary>获取结果徽章画刷。</summary>
    public System.Windows.Media.Brush ResultBadgeBrush => ResultIsError ? UiBrushes.Danger : UiBrushes.Success;

    /// <summary>获取结果摘要（耗时和内容块数量）。</summary>
    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    /// <summary>获取调用失败时的错误文本（如果有）。</summary>
    public string ResultError
    {
        get => _resultError;
        private set
        {
            if (SetProperty(ref _resultError, value))
            {
                OnPropertyChanged(nameof(HasResultError));
            }
        }
    }

    /// <summary>获取是否存在错误文本。</summary>
    public bool HasResultError => ResultError.Length > 0;

    /// <summary>获取上次结果的原始 JSON。</summary>
    public string RawResultJson
    {
        get => _rawResultJson;
        private set => SetProperty(ref _rawResultJson, value);
    }

    /// <summary>获取所选服务器解析后的端点或启动命令。</summary>
    public string ResolvedEndpoint
    {
        get => _resolvedEndpoint;
        private set => SetProperty(ref _resolvedEndpoint, value);
    }

    /// <summary>获取或设置状态栏文本。</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>获取或设置选中的资源。</summary>
    public McpResource? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (SetProperty(ref _selectedResource, value))
            {
                ReadResourceCommand.RaiseCanExecuteChanged();
                if (value != null)
                {
                    ReadResourceUri = value.Uri;
                }
            }
        }
    }

    /// <summary>获取或设置要手动读取的资源 URI。</summary>
    public string ReadResourceUri
    {
        get => _readResourceUri;
        set
        {
            if (SetProperty(ref _readResourceUri, value ?? string.Empty))
            {
                ReadResourceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>获取资源详情标题。</summary>
    public string ResourceDetailTitle
    {
        get => _resourceDetailTitle;
        private set => SetProperty(ref _resourceDetailTitle, value);
    }

    /// <summary>获取资源详情文本。</summary>
    public string ResourceDetail
    {
        get => _resourceDetail;
        private set => SetProperty(ref _resourceDetail, value);
    }

    /// <summary>获取或设置选中的提示词。</summary>
    public McpPrompt? SelectedPrompt
    {
        get => _selectedPrompt;
        set
        {
            if (SetProperty(ref _selectedPrompt, value))
            {
                RebuildPromptArguments();
                GetPromptCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>获取提示词渲染结果的标题。</summary>
    public string PromptResultTitle
    {
        get => _promptResultTitle;
        private set => SetProperty(ref _promptResultTitle, value);
    }

    /// <summary>获取提示词渲染结果的文本。</summary>
    public string PromptResultText
    {
        get => _promptResultText;
        private set => SetProperty(ref _promptResultText, value);
    }

    /// <summary>获取在线服务器的数量。</summary>
    public string ServerCountText =>
        Servers.Count == 0 ? "未发现服务" : $"{Servers.Count} 个服务 · {Servers.Count(server => server.IsAlive)} 在线";

    /// <summary>在不阻塞 UI 的情况下加载注册表。</summary>
    /// <returns>初始扫描完成时结束的 Task。</returns>
    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>报告被应用程序级安全网捕获的异常。</summary>
    /// <param name="exception">未预期的异常。</param>
    public void ReportUnexpectedError(Exception exception)
    {
        Log(ActivityKind.Error, "未处理异常", ToolInvocation.DescribeError(exception));
        StatusText = "发生未处理异常：" + exception.Message;
    }

    /// <summary>释放活动的客户端和传输。在失败后调用也是安全的。</summary>
    /// <returns>两者均完成释放时结束的 Task。</returns>
    public async Task ShutdownAsync()
    {
        await DisconnectAsync(updateStatus: false).ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        var selectedId = SelectedServer?.Signature;
        try
        {
            var removed = await Task.Run(() =>
            {
                _ = McpRegistry.ReadAll();
                var stale = McpRegistry.RemoveStale();
                return (stale, McpRegistry.ReadAll());
            }).ConfigureAwait(true);

            var entries = removed.Item2;
            Servers.Clear();
            foreach (var entry in entries.OrderByDescending(entry => entry.RegisteredAt))
            {
                Servers.Add(new ServerItemViewModel(entry));
            }

            OnPropertyChanged(nameof(ServerCountText));
            SelectedServer = selectedId == null
                ? Servers.FirstOrDefault()
                : Servers.FirstOrDefault(server => server.Signature == selectedId) ?? Servers.FirstOrDefault();

            if (_connectedServer != null)
            {
                _connectedServer = Servers.FirstOrDefault(server => server.Signature == _connectedServer.Signature)
                                   ?? _connectedServer;
            }

            ConnectOrDisconnectCommand.RaiseCanExecuteChanged();
            StatusText = removed.stale > 0
                ? $"注册表已刷新：清理 {removed.stale} 个失效项，当前 {Servers.Count} 个服务。"
                : $"注册表已刷新：当前 {Servers.Count} 个服务。";

            if (removed.stale > 0)
            {
                Log(ActivityKind.Info, "刷新注册表", $"清理 {removed.stale} 个失效项，剩余 {Servers.Count} 个服务。");
            }
        }
        catch (Exception ex)
        {
            StatusText = "刷新注册表失败：" + ToolInvocation.DescribeError(ex);
            Log(ActivityKind.Error, "刷新注册表", ToolInvocation.DescribeError(ex));
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private bool CanConnectOrDisconnect()
    {
        if (IsBusy)
        {
            return false;
        }

        return IsConnected || SelectedServer != null;
    }

    private async Task ConnectOrDisconnectAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync(updateStatus: true).ConfigureAwait(true);
            return;
        }

        var server = SelectedServer ?? throw new InvalidOperationException("请先选择要连接的服务器。");
        await ConnectAsync(server).ConfigureAwait(true);
    }

    private async Task ConnectAsync(ServerItemViewModel server)
    {
        if (IsBusy)
        {
            return;
        }

        IsConnecting = true;
        ConnectionStatus = "连接中";
        ConnectionDetail = "正在连接 " + server.DisplayLabel + " …";
        StatusText = "正在连接 " + server.DisplayLabel + " …";

        // 在建立新连接之前先断开上一个服务器。
        await DisconnectAsync(updateStatus: false).ConfigureAwait(true);

        ConnectionStatus = "连接中";
        ConnectionDetail = "正在连接 " + server.DisplayLabel + " …";

        IMcpTransport? transport = null;
        McpClient? client = null;
        try
        {
            transport = CreateTransport(server);
            client = new McpClient(transport, new McpClientOptions
            {
                ClientName = "AiTools.McpExplorer",
                ClientVersion = "0.2.0"
            });

            await client.InitializeAsync().ConfigureAwait(true);
            var tools = await client.ListToolsAsync().ConfigureAwait(true);

            _client = client;
            _transport = transport;
            _connectedServer = server;
            _resolvedEndpoint = DescribeTarget(server);

            Tools.Clear();
            foreach (var tool in tools)
            {
                Tools.Add(new ToolItemViewModel(tool));
            }

            ToolsEmptyText = tools.Count == 0 ? "服务器没有提供工具" : string.Empty;
            SelectedTool = Tools.FirstOrDefault();            ServerInfo = $"{client.ServerName ?? server.Name} · v{client.ServerVersion ?? server.Version}";
            ServerInfoDetail =
                $"协议 {client.NegotiatedProtocolVersion ?? "未知"} · {server.TransportLabel} · {_resolvedEndpoint}";
            ServerInfoVisible = true;
            ConnectionStatus = "已连接";
            ConnectionDetail = $"{client.ServerName ?? server.Name} · {tools.Count} 个工具";
            StatusText = $"已连接 {client.ServerName ?? server.Name}（{tools.Count} 个工具）。";
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(ConnectedServer));
            Log(ActivityKind.Success, "连接", $"{server.DisplayLabel} · 协议 {client.NegotiatedProtocolVersion ?? "未知"} · {tools.Count} 个工具。");

            // 加载资源和提示词，即使失败也不视为致命错误。
            await LoadResourcesCoreAsync(client).ConfigureAwait(true);
            await LoadPromptsCoreAsync(client).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (client != null)
            {
                await DisposeClientQuietlyAsync(client).ConfigureAwait(true);
            }
            else if (transport != null)
            {
                await DisposeTransportQuietlyAsync(transport).ConfigureAwait(true);
            }

            _client = null;
            _transport = null;
            _connectedServer = null;
            _resolvedEndpoint = string.Empty;
            ResetConnectionUi();
            ConnectionStatus = "未连接";
            ConnectionDetail = "连接失败：" + ToolInvocation.DescribeError(ex);
            StatusText = "连接失败：" + ToolInvocation.DescribeError(ex);
            Log(ActivityKind.Error, "连接", $"{server.DisplayLabel} → {ToolInvocation.DescribeError(ex)}");
        }
        finally
        {
            IsConnecting = false;
            NotifyBusyChanged();
        }
    }

    private IMcpTransport CreateTransport(ServerItemViewModel server)
    {
        var entry = server.Entry;
        if (server.UsesStdio)
        {
            return new StdioMcpTransport(new StdioTransportOptions
            {
                Command = entry.LaunchCommand!,
                Arguments = entry.LaunchArguments ?? new List<string>(),
                WorkingDirectory = entry.WorkingDirectory
            });
        }

        if (string.IsNullOrWhiteSpace(entry.Endpoint))
        {
            throw new InvalidOperationException(
                $"服务器 '{server.Name}' 既没有 HTTP 端点，也没有可用的启动命令。");
        }

        if (!Uri.TryCreate(entry.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("服务器端点无效：" + entry.Endpoint);
        }

        return new StreamableHttpMcpTransport(new StreamableHttpTransportOptions
        {
            Endpoint = endpoint
        });
    }

    private static string DescribeTarget(ServerItemViewModel server)
    {
        if (server.UsesStdio)
        {
            return "stdio · " + server.LaunchCommandText;
        }

        return server.Endpoint ?? "未知端点";
    }

    private async Task DisconnectAsync(bool updateStatus)
    {
        var client = _client;
        var transport = _transport;
        _client = null;
        _transport = null;
        _connectedServer = null;
        _resolvedEndpoint = string.Empty;
        _activeTool = null;

        if (client != null)
        {
            await DisposeClientQuietlyAsync(client).ConfigureAwait(true);
        }

        if (transport != null)
        {
            await DisposeTransportQuietlyAsync(transport).ConfigureAwait(true);
        }

        Tools.Clear();
        Parameters.Clear();
        Resources.Clear();
        Prompts.Clear();
        PromptArguments.Clear();
        ResultBlocks.Clear();
        HasResult = false;
        RawResultJson = string.Empty;
        ResultError = string.Empty;
        ResultSummary = string.Empty;
        ResourceDetail = string.Empty;
        ResourceDetailTitle = string.Empty;
        PromptResultText = string.Empty;
        PromptResultTitle = string.Empty;
        ResetConnectionUi();

        if (updateStatus)
        {
            ConnectionStatus = "未连接";
            ConnectionDetail = "尚未连接任何 MCP 服务器";
            StatusText = "已断开连接。";
            Log(ActivityKind.Info, "断开", "已释放 MCP 客户端与传输。");
        }

        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectedServer));
        ConnectOrDisconnectCommand.RaiseCanExecuteChanged();
        InvokeToolCommand.RaiseCanExecuteChanged();
        LoadResourcesCommand.RaiseCanExecuteChanged();
        LoadPromptsCommand.RaiseCanExecuteChanged();
        ReadResourceCommand.RaiseCanExecuteChanged();
        GetPromptCommand.RaiseCanExecuteChanged();
    }

    private void ResetConnectionUi()
    {
        ServerInfo = string.Empty;
        ServerInfoDetail = string.Empty;
        ServerInfoVisible = false;
        ToolsEmptyText = "连接服务器后显示工具目录";
        ActiveTool = null;
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(ConnectedServer));
    }

    private void SelectTool(ToolItemViewModel? tool)
    {
        ActiveTool = tool;
        Parameters.Clear();

        if (tool == null)
        {
            RawArgumentsJson = "{}";
            return;
        }

        foreach (var parameter in tool.Parameters)
        {
            Parameters.Add(parameter);
        }

        // 切换工具时总是先进入表单模式，并预填架构默认值。
        IsRawJsonMode = false;
        RawArgumentsJson = BuildArgumentsJson().ToJsonString();
        ResultBlocks.Clear();
        HasResult = false;
        RawResultJson = string.Empty;
        ResultError = string.Empty;
        ResultSummary = string.Empty;
        ResourceDetail = string.Empty;
        ResourceDetailTitle = string.Empty;
        PromptResultText = string.Empty;
        PromptResultTitle = string.Empty;
    }

    private JsonObject BuildArgumentsJson()
    {
        var result = new JsonObject();
        foreach (var parameter in Parameters)
        {
            var node = parameter.ToJsonNode();
            if (node != null)
            {
                result[parameter.Name] = node;
            }
        }

        return result;
    }

    private void ToggleRawJson()
    {
        if (ActiveTool == null)
        {
            return;
        }

        if (!IsRawJsonMode)
        {
            // 表单 → 原始 JSON：序列化当前值。
            RawArgumentsJson = ToolInvocation.PrettyJson(BuildArgumentsJson());
            IsRawJsonMode = true;
            return;
        }

        // 原始 JSON → 表单：尽力从 JSON 对象填充表单。
        try
        {
            if (ToolInvocation.TryParseArgumentsObject(RawArgumentsJson, out var parsed, out var error))
            {
                ApplyJsonToForm(parsed);
                RawArgumentsJson = ToolInvocation.PrettyJson(parsed);
                IsRawJsonMode = false;
                StatusText = "已把 JSON 参数同步到表单。";
            }
            else
            {
                StatusText = "JSON 参数无效：" + error;
                Log(ActivityKind.Error, "切换 JSON 模式", error ?? "JSON 无效");
            }
        }
        catch (Exception ex)
        {
            StatusText = "切换 JSON 模式失败：" + ToolInvocation.DescribeError(ex);
            Log(ActivityKind.Error, "切换 JSON 模式", ToolInvocation.DescribeError(ex));
        }
    }

    private void ApplyJsonToForm(JsonElement parsed)
    {
        foreach (var parameter in Parameters)
        {
            if (!parsed.TryGetProperty(parameter.Name, out var value))
            {
                parameter.SetFromJson(null);
                continue;
            }

            try
            {
                parameter.SetFromJson(JsonNode.Parse(value.GetRawText()));
            }
            catch (JsonException)
            {
                parameter.SetFromJson(JsonValue.Create(value.GetRawText()));
            }
        }
    }

    private async Task InvokeToolAsync()
    {
        var client = _client;
        var tool = ActiveTool;
        if (client == null || tool == null)
        {
            StatusText = "尚未连接服务器或未选择工具。";
            return;
        }

        if (!IsRawJsonMode)
        {
            var invalid = Parameters.Where(parameter => !parameter.Validate()).ToList();
            if (invalid.Count > 0)
            {
                var first = invalid[0];
                StatusText = $"参数 '{first.Name}' 无效：{first.ValidationError}";
                ResultError = StatusText;
                HasResult = true;
                ResultIsError = true;
                ResultSummary = "参数校验失败，未发送请求。";
                ResultBlocks.Clear();
                RawResultJson = string.Empty;
                Log(ActivityKind.Error, "调用 " + tool.Name, StatusText);
                return;
            }

            RawArgumentsJson = ToolInvocation.PrettyJson(BuildArgumentsJson());
        }

        if (!ToolInvocation.TryParseArgumentsObject(RawArgumentsJson, out var arguments, out var parseError))
        {
            StatusText = "参数无效：" + parseError;
            ResultError = StatusText;
            HasResult = true;
            ResultIsError = true;
            ResultSummary = "参数解析失败，未发送请求。";
            ResultBlocks.Clear();
            RawResultJson = string.Empty;
            Log(ActivityKind.Error, "调用 " + tool.Name, StatusText);
            return;
        }

        IsInvoking = true;
        ResultBlocks.Clear();
        HasResult = false;
        ResultError = string.Empty;
        ResultSummary = "正在调用…";
        RawResultJson = string.Empty;
        StatusText = "正在调用 " + tool.Name + " …";
        Log(ActivityKind.Info, "调用 " + tool.Name, "请求已发送。");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await client.CallToolAsync(tool.Name, arguments, CancellationToken.None).ConfigureAwait(true);
            stopwatch.Stop();

            foreach (var block in ToolInvocation.BuildResultBlocks(result))
            {
                ResultBlocks.Add(block);
            }

            RawResultJson = ToolInvocation.BuildRawResultJson(result, stopwatch.ElapsedMilliseconds);
            ResultIsError = result.IsError;
            ResultSummary = $"耗时 {stopwatch.ElapsedMilliseconds} ms · {result.Content.Count} 个内容块";
            HasResult = true;
            StatusText = result.IsError
                ? $"工具 {tool.Name} 返回错误（{stopwatch.ElapsedMilliseconds} ms）。"
                : $"工具 {tool.Name} 调用成功（{stopwatch.ElapsedMilliseconds} ms）。";
            Log(
                result.IsError ? ActivityKind.Error : ActivityKind.Success,
                "调用 " + tool.Name,
                (result.IsError ? "工具报告错误" : "成功") + $" · {stopwatch.ElapsedMilliseconds} ms · {result.Content.Count} 个内容块。");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            ResultIsError = true;
            ResultSummary = "调用已取消。";
            ResultError = "操作已取消。";
            HasResult = true;
            StatusText = "调用已取消。";
            Log(ActivityKind.Error, "调用 " + tool.Name, "操作已取消。");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var message = ToolInvocation.DescribeError(ex);
            ResultIsError = true;
            ResultSummary = $"调用失败 · 耗时 {stopwatch.ElapsedMilliseconds} ms";
            ResultError = message;
            HasResult = true;
            StatusText = "调用失败：" + message;
            Log(ActivityKind.Error, "调用 " + tool.Name, message);
        }
        finally
        {
            IsInvoking = false;
        }
    }

    private async Task LoadResourcesAsync()
    {
        if (_client == null)
        {
            return;
        }

        await LoadResourcesCoreAsync(_client).ConfigureAwait(true);
    }

    private async Task LoadResourcesCoreAsync(McpClient client)
    {
        try
        {
            var resources = await client.ListResourcesAsync().ConfigureAwait(true);
            Resources.Clear();
            foreach (var resource in resources)
            {
                Resources.Add(resource);
            }

            Log(ActivityKind.Info, "资源", $"载入 {resources.Count} 个资源。");
        }
        catch (Exception ex)
        {
            Log(ActivityKind.Error, "资源", "载入失败：" + ToolInvocation.DescribeError(ex));
        }
    }

    private async Task ReadSelectedResourceAsync()
    {
        var client = _client;
        if (client == null)
        {
            return;
        }

        var uri = !string.IsNullOrWhiteSpace(ReadResourceUri)
            ? ReadResourceUri.Trim()
            : SelectedResource?.Uri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            StatusText = "请选择资源或输入资源 URI。";
            return;
        }

        try
        {
            var result = await client.ReadResourceAsync(uri).ConfigureAwait(true);
            ResourceDetailTitle = uri;
            if (result.Contents.Count == 0)
            {
                ResourceDetail = "（资源没有内容）";
            }
            else
            {
                var parts = new List<string>();
                foreach (var content in result.Contents)
                {
                    var header = content.Uri +
                                 (string.IsNullOrWhiteSpace(content.MimeType) ? string.Empty : $" ({content.MimeType})");
                    var body = !string.IsNullOrEmpty(content.Text)
                        ? ToolInvocation.Truncate(content.Text)
                        : !string.IsNullOrEmpty(content.Blob)
                            ? $"blob: base64 {content.Blob!.Length} 字符"
                            : "（空）";
                    parts.Add(header + Environment.NewLine + body);
                }

                ResourceDetail = string.Join(Environment.NewLine + Environment.NewLine, parts);
            }

            StatusText = $"已读取资源 {uri}。";
            Log(ActivityKind.Success, "读取资源", uri);
        }
        catch (Exception ex)
        {
            var message = ToolInvocation.DescribeError(ex);
            ResourceDetailTitle = uri;
            ResourceDetail = "读取失败：" + message;
            StatusText = "读取资源失败：" + message;
            Log(ActivityKind.Error, "读取资源", uri + " → " + message);
        }
    }

    private async Task LoadPromptsAsync()
    {
        if (_client == null)
        {
            return;
        }

        await LoadPromptsCoreAsync(_client).ConfigureAwait(true);
    }

    private async Task LoadPromptsCoreAsync(McpClient client)
    {
        try
        {
            var prompts = await client.ListPromptsAsync().ConfigureAwait(true);
            Prompts.Clear();
            foreach (var prompt in prompts)
            {
                Prompts.Add(prompt);
            }

            SelectedPrompt = Prompts.FirstOrDefault();
            Log(ActivityKind.Info, "提示词", $"载入 {prompts.Count} 个提示词。");
        }
        catch (Exception ex)
        {
            Log(ActivityKind.Error, "提示词", "载入失败：" + ToolInvocation.DescribeError(ex));
        }
    }

    private void RebuildPromptArguments()
    {
        PromptArguments.Clear();
        if (SelectedPrompt?.Arguments == null)
        {
            return;
        }

        foreach (var argument in SelectedPrompt.Arguments)
        {
            _promptArgumentValues.TryGetValue(argument.Name, out var existing);
            PromptArguments.Add(new PromptArgumentViewModel(argument.Name, argument.Description, argument.Required, existing));
        }
    }

    private async Task GetSelectedPromptAsync()
    {
        var client = _client;
        var prompt = SelectedPrompt;
        if (client == null || prompt == null)
        {
            return;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in PromptArguments)
        {
            if (argument.IsRequired && string.IsNullOrWhiteSpace(argument.Value))
            {
                StatusText = $"提示词参数 '{argument.Name}' 不能为空。";
                Log(ActivityKind.Error, "获取提示词", StatusText);
                return;
            }

            if (!string.IsNullOrEmpty(argument.Value))
            {
                arguments[argument.Name] = argument.Value;
                _promptArgumentValues[argument.Name] = argument.Value;
            }
        }

        try
        {
            var result = await client.GetPromptAsync(prompt.Name, arguments).ConfigureAwait(true);
            PromptResultTitle = prompt.Name + (string.IsNullOrWhiteSpace(result.Description) ? string.Empty : " · " + result.Description);
            if (result.Messages.Count == 0)
            {
                PromptResultText = "（没有消息）";
            }
            else
            {
                var parts = new List<string>();
                foreach (var message in result.Messages)
                {
                    var text = message.Content switch
                    {
                        McpTextContent content => content.Text,
                        McpResourceContent content => content.Text ?? content.Uri,
                        McpEmbeddedResourceContent content => content.Resource.Text ?? content.Resource.Uri,
                        McpImageContent content => $"[图片 {content.MimeType}]",
                        McpAudioContent content => $"[音频 {content.MimeType}]",
                        McpUnknownContent content => $"[未知内容 {content.Type}]",
                        _ => message.Content.GetType().Name
                    };
                    parts.Add($"[{message.Role}]" + Environment.NewLine + ToolInvocation.Truncate(text ?? string.Empty));
                }

                PromptResultText = string.Join(Environment.NewLine + Environment.NewLine, parts);
            }

            StatusText = $"已获取提示词 {prompt.Name}。";
            Log(ActivityKind.Success, "获取提示词", prompt.Name);
        }
        catch (Exception ex)
        {
            var message = ToolInvocation.DescribeError(ex);
            PromptResultTitle = prompt.Name;
            PromptResultText = "获取失败：" + message;
            StatusText = "获取提示词失败：" + message;
            Log(ActivityKind.Error, "获取提示词", prompt.Name + " → " + message);
        }
    }

    private async Task DisposeClientQuietlyAsync(McpClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log(ActivityKind.Error, "释放客户端", ToolInvocation.DescribeError(ex));
        }
    }

    private static async Task DisposeTransportQuietlyAsync(IMcpTransport transport)
    {
        try
        {
            await transport.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 客户端通常负责释放传输；这里只是安全网。
        }
    }

    private void NotifyBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        RefreshCommand.RaiseCanExecuteChanged();
        ConnectOrDisconnectCommand.RaiseCanExecuteChanged();
        InvokeToolCommand.RaiseCanExecuteChanged();
        LoadResourcesCommand.RaiseCanExecuteChanged();
        ReadResourceCommand.RaiseCanExecuteChanged();
        LoadPromptsCommand.RaiseCanExecuteChanged();
        GetPromptCommand.RaiseCanExecuteChanged();
    }

    private void Log(ActivityKind kind, string operation, string detail)
    {
        Logs.Insert(0, new ActivityLogEntry(kind, operation, detail));
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }
    }
}

/// <summary>提示词的可编辑参数。</summary>
public sealed class PromptArgumentViewModel : ObservableObject
{
    private string _value;

    /// <summary>创建提示词参数。</summary>
    /// <param name="name">参数名称。</param>
    /// <param name="description">可选描述。</param>
    /// <param name="required">参数是否为必填。</param>
    /// <param name="value">初始值。</param>
    public PromptArgumentViewModel(string name, string? description, bool required, string? value)
    {
        Name = name;
        Description = description ?? string.Empty;
        IsRequired = required;
        Label = required ? name + " *" : name;
        _value = value ?? string.Empty;
    }

    /// <summary>获取参数名称。</summary>
    public string Name { get; }

    /// <summary>获取参数标签（必填参数带星号）。</summary>
    public string Label { get; }

    /// <summary>获取可选的描述。</summary>
    public string Description { get; }

    /// <summary>获取参数是否为必填。</summary>
    public bool IsRequired { get; }

    /// <summary>获取是否应显示描述。</summary>
    public bool HasDescription => Description.Length > 0;

    /// <summary>获取或设置参数值。</summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }
}
