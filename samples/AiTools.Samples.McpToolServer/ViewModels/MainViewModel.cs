using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AiTools.Samples.McpDiscovery;
using AiTools.Samples.McpToolServer.ViewModels;

namespace AiTools.Samples.McpToolServer;

/// <summary>内嵌 MCP HTTP 服务的生命周期状态。</summary>
public enum ServerState
{
    /// <summary>服务未运行。</summary>
    Stopped,

    /// <summary>服务正在启动或停止。</summary>
    Busy,

    /// <summary>服务正在监听并已注册。</summary>
    Running,

    /// <summary>服务无法启动，例如端口不可用。</summary>
    Failed
}

/// <summary>
/// 驱动 WPF 窗口：拥有后台 HTTP 服务的生命周期，对外提供工具目录、实时请求日志
/// 以及内置的工具自测。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 500;

    private readonly CommandLineOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private HttpMcpServer? _server;
    private RegistryRegistration? _registration;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private ServerState _state = ServerState.Stopped;
    private string _statusDetail = "服务尚未启动。";
    private string _endpoint = "—";
    private string _listenAddress = "—";
    private int _pid;
    private int _requestCount;
    private bool _isLogAutoScroll = true;
    private bool _stopRequested;
    private ToolCardViewModel? _selectedTool;
    private string _toolResultText = "选择一个工具并填写参数，然后点击“调用工具”查看结果。";
    private string _toolResultStatus = "等待调用";
    private Brush _toolResultBrush = UiTheme.TextSecondary;
    private bool _isInvoking;

    /// <summary>创建 ViewModel。</summary>
    /// <param name="options">控制主机、端口与服务器名称的命令行选项。</param>
    public MainViewModel(CommandLineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        Tools = new ObservableCollection<ToolCardViewModel>();
        foreach (var tool in ToolCatalog.Tools)
        {
            Tools.Add(new ToolCardViewModel(tool));
        }

        Logs = new ObservableCollection<RequestLogEntry>();
        SelectedTool = Tools.Count > 0 ? Tools[0] : null;
    }

    /// <summary>获取左侧显示的工具目录。</summary>
    public ObservableCollection<ToolCardViewModel> Tools { get; }

    /// <summary>获取实时请求日志，最多保留 500 条。</summary>
    public ObservableCollection<RequestLogEntry> Logs { get; }

    /// <summary>获取标题区域显示的服务器名称。</summary>
    public string ServerName => _options.Name;

    /// <summary>获取目录中的工具数量。</summary>
    public int ToolCount => Tools.Count;

    /// <summary>获取状态栏显示的本地 MCP 注册表路径。</summary>
    public string RegistryPath => McpRegistry.RegistryPath;

    /// <summary>获取配置的主机。</summary>
    public string Host => _options.Host;

    /// <summary>获取配置的端口，其中 0 表示“自动选择”。</summary>
    public int RequestedPort => _options.Port;

    /// <summary>获取当前服务状态。</summary>
    public ServerState State => _state;

    /// <summary>获取徽章中使用的简短中文状态标签。</summary>
    public string StatusText => _state switch
    {
        ServerState.Running => "运行中",
        ServerState.Busy => "处理中",
        ServerState.Failed => "启动失败",
        _ => "已停止"
    };

    /// <summary>获取与 <see cref="StatusText"/> 匹配的画刷。</summary>
    public Brush StatusBrush => _state switch
    {
        ServerState.Running => UiTheme.Success,
        ServerState.Busy => UiTheme.Warning,
        ServerState.Failed => UiTheme.Danger,
        _ => UiTheme.Danger
    };

    /// <summary>获取与 <see cref="StatusText"/> 匹配的淡色背景画刷。</summary>
    public Brush StatusBadgeBackground => _state switch
    {
        ServerState.Running => UiTheme.SuccessSubtle,
        ServerState.Busy => UiTheme.WarningSubtle,
        _ => UiTheme.DangerSubtle
    };

    /// <summary>获取底部状态栏显示的较长状态行。</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    /// <summary>获取顶栏显示的当前 MCP 端点。</summary>
    public string Endpoint
    {
        get => _endpoint;
        private set => SetProperty(ref _endpoint, value);
    }

    /// <summary>获取监听地址，例如 <c>127.0.0.1:5220</c>。</summary>
    public string ListenAddress
    {
        get => _listenAddress;
        private set => SetProperty(ref _listenAddress, value);
    }

    /// <summary>获取服务器进程 ID。</summary>
    public int Pid
    {
        get => _pid;
        private set
        {
            if (SetProperty(ref _pid, value))
            {
                OnPropertyChanged(nameof(PidText));
            }
        }
    }

    /// <summary>获取格式化后的进程 ID；服务停止时使用长破折号。</summary>
    public string PidText => _pid <= 0 ? "—" : _pid.ToString(CultureInfo.InvariantCulture);

    /// <summary>获取自启动以来已处理的 HTTP 请求数。</summary>
    public int RequestCount
    {
        get => _requestCount;
        private set => SetProperty(ref _requestCount, value);
    }

    /// <summary>获取服务是否正在运行。</summary>
    public bool IsRunning => _state == ServerState.Running;

    /// <summary>获取启动或停止操作是否正在进行。</summary>
    public bool IsBusy => _state == ServerState.Busy;

    /// <summary>获取启动/停止按钮的标签。</summary>
    public string PrimaryActionText => IsRunning ? "停止服务" : "启动服务";

    /// <summary>获取启动/停止按钮是否可用。</summary>
    public bool CanToggleService => !IsBusy;

    /// <summary>获取自测控件是否可用。</summary>
    public bool CanInvokeTool => !_isInvoking;

    /// <summary>获取或设置日志列表是否自动滚动到最新条目。</summary>
    public bool IsLogAutoScroll
    {
        get => _isLogAutoScroll;
        set => SetProperty(ref _isLogAutoScroll, value);
    }

    /// <summary>获取或设置自测面板中选中的工具。</summary>
    public ToolCardViewModel? SelectedTool
    {
        get => _selectedTool;
        set
        {
            if (SetProperty(ref _selectedTool, value))
            {
                ToolResultText = "已选择 “" + (value?.Name ?? string.Empty) + "”，填写参数后点击“调用工具”。";
                ToolResultStatus = "等待调用";
                ToolResultBrush = UiTheme.TextSecondary;
            }
        }
    }

    /// <summary>获取自测结果文本。</summary>
    public string ToolResultText
    {
        get => _toolResultText;
        private set => SetProperty(ref _toolResultText, value);
    }

    /// <summary>获取自测状态行。</summary>
    public string ToolResultStatus
    {
        get => _toolResultStatus;
        private set => SetProperty(ref _toolResultStatus, value);
    }

    /// <summary>获取用于自测状态行的画刷。</summary>
    public Brush ToolResultBrush
    {
        get => _toolResultBrush;
        private set => SetProperty(ref _toolResultBrush, value);
    }

    /// <summary>在后台线程启动 HTTP 服务，并将其注册到本地注册表。</summary>
    /// <returns>启动完成或失败时完成的 Task。</returns>
    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_server != null)
            {
                return;
            }

            SetState(ServerState.Busy, "正在启动服务……");
            _stopRequested = false;

            var log = new ConsoleLog(null, "HTTP");
            HttpMcpServer? server = null;
            string? error = null;
            var host = _options.Host;
            var port = _options.Port;
            var name = _options.Name;

            await Task.Run(() =>
            {
                if (!HttpMcpServer.TryCreate(host, port, name, log, OnRequestLogged, out var created, out var createError))
                {
                    error = createError;
                    return;
                }

                server = created;
            }).ConfigureAwait(true);

            if (server == null)
            {
                SetState(ServerState.Failed, "启动失败：" + (error ?? "无法绑定监听端口。"));
                return;
            }

            _server = server;
            _serverCts = new CancellationTokenSource();
            _registration = await Task.Run(() => RegistryRegistration.Register(name, server.Endpoint, log))
                .ConfigureAwait(true);
            _serverTask = Task.Run(() => server.RunAsync(_serverCts.Token));

            Endpoint = server.Endpoint;
            ListenAddress = server.Host + ":" + server.Port.ToString(CultureInfo.InvariantCulture);
            Pid = Environment.ProcessId;

            var detail = "服务已启动";
            if (_options.Port != 0 && server.Port != _options.Port)
            {
                detail += "（端口 " + _options.Port + " 被占用，已改用 " + server.Port + "）";
            }

            if (!_registration.IsRegistered)
            {
                detail += "；写入注册表失败";
            }

            SetState(ServerState.Running, detail + "。");
            ObserveServerTask(server);
        }
        catch (Exception ex)
        {
            SetState(ServerState.Failed, "启动失败：" + ex.Message);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>停止 HTTP 服务，移除其注册表条目并释放监听器。</summary>
    /// <returns>关闭完成时完成的 Task。</returns>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_server == null && _serverTask == null)
            {
                if (_state != ServerState.Failed)
                {
                    SetState(ServerState.Stopped, "服务已停止。");
                }

                return;
            }

            SetState(ServerState.Busy, "正在停止服务……");
            _stopRequested = true;

            try
            {
                _serverCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 取消 Token 源已经释放。
            }

            if (_serverTask != null)
            {
                try
                {
                    await Task.WhenAny(_serverTask, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 监听器拆除期间，接收循环可能出错。
                }
            }

            if (_server != null)
            {
                try
                {
                    await _server.DisposeAsync().ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 释放操作是尽力而为。
                }
            }

            _registration?.Dispose();
            _registration = null;

            _serverCts?.Dispose();
            _serverCts = null;
            _server = null;
            _serverTask = null;

            Endpoint = "—";
            ListenAddress = "—";
            Pid = 0;
            SetState(ServerState.Stopped, "服务已停止，注册表条目已清理。");
        }
        catch (Exception ex)
        {
            _server = null;
            _registration = null;
            _serverTask = null;
            Endpoint = "—";
            ListenAddress = "—";
            SetState(ServerState.Failed, "停止服务时出错：" + ex.Message);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>清空实时请求日志。</summary>
    public void ClearLogs()
    {
        Logs.Clear();
    }

    /// <summary>
    /// 使用生成的参数编辑器中输入的值，执行自测面板中选中的工具。
    /// </summary>
    /// <returns>结果显示后完成的 Task。</returns>
    public async Task InvokeSelectedToolAsync()
    {
        if (_isInvoking)
        {
            return;
        }

        var tool = SelectedTool;
        if (tool == null)
        {
            ToolResultText = "请先选择一个工具。";
            ToolResultStatus = "调用失败";
            ToolResultBrush = UiTheme.Warning;
            return;
        }

        _isInvoking = true;
        OnPropertyChanged(nameof(CanInvokeTool));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await Task.Yield();

            JsonElement arguments;
            try
            {
                arguments = BuildArguments(tool);
            }
            catch (ToolExecutionException ex)
            {
                ShowToolError(ex.Message, stopwatch.Elapsed, tool.Name);
                return;
            }

            string text;
            try
            {
                text = ToolCatalog.Invoke(tool.Name, arguments);
            }
            catch (ToolExecutionException ex)
            {
                ShowToolError(ex.Message, stopwatch.Elapsed, tool.Name);
                return;
            }
            catch (Exception ex)
            {
                ShowToolError("工具执行失败：" + ex.Message, stopwatch.Elapsed, tool.Name);
                return;
            }

            ToolResultText = TryPrettyPrint(text);
            ToolResultStatus = "调用成功 · " + stopwatch.Elapsed.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + " ms";
            ToolResultBrush = UiTheme.Success;
            AppendLog(new RequestLogEntry(DateTime.Now, "内置自测", tool.Name, stopwatch.Elapsed, "200 自测"));
        }
        catch (Exception ex)
        {
            ToolResultText = "自测失败：" + ex.Message;
            ToolResultStatus = "调用失败";
            ToolResultBrush = UiTheme.Danger;
        }
        finally
        {
            _isInvoking = false;
            OnPropertyChanged(nameof(CanInvokeTool));
        }
    }

    /// <summary>报告来自窗口事件处理程序的错误。</summary>
    /// <param name="message">要显示的错误消息。</param>
    public void ReportUiError(string message)
    {
        StatusDetail = message;
    }

    /// <summary>更新状态栏文本，例如复制端点 URL 之后。</summary>
    /// <param name="message">要显示的消息。</param>
    public void ReportStatus(string message)
    {
        StatusDetail = message;
    }

    private static JsonElement BuildArguments(ToolCardViewModel tool)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in tool.Parameters)
        {
            if (!parameter.ShouldInclude)
            {
                continue;
            }

            switch (parameter.Kind)
            {
                case ToolParameterKind.Boolean:
                    values[parameter.Name] = parameter.BoolValue;
                    break;

                case ToolParameterKind.Enum:
                    if (string.IsNullOrEmpty(parameter.SelectedOption))
                    {
                        throw new ToolExecutionException("参数 '" + parameter.Name + "' 需要一个可选值。");
                    }

                    values[parameter.Name] = parameter.SelectedOption;
                    break;

                case ToolParameterKind.Integer:
                    if (!long.TryParse(
                            parameter.Value.Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var integer))
                    {
                        throw new ToolExecutionException("参数 '" + parameter.Name + "' 必须是整数。");
                    }

                    values[parameter.Name] = integer;
                    break;

                case ToolParameterKind.Number:
                    if (!double.TryParse(
                            parameter.Value.Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var number))
                    {
                        throw new ToolExecutionException("参数 '" + parameter.Name + "' 必须是数字。");
                    }

                    values[parameter.Name] = number;
                    break;

                default:
                    values[parameter.Name] = parameter.Value;
                    break;
            }
        }

        return JsonSerializer.SerializeToElement(values);
    }

    private void ShowToolError(string message, TimeSpan elapsed, string toolName)
    {
        ToolResultText = message;
        ToolResultStatus = "调用失败 · " + elapsed.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + " ms";
        ToolResultBrush = UiTheme.Warning;
        AppendLog(new RequestLogEntry(DateTime.Now, "内置自测", toolName, elapsed, "200 工具错误"));
    }

    private static string TryPrettyPrint(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private void OnRequestLogged(RequestLogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            AppendLog(entry);
            return;
        }

        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => AppendLog(entry)));
        }
        catch (Exception)
        {
            // 窗口可能已在关闭。
        }
    }

    private void AppendLog(RequestLogEntry entry)
    {
        Logs.Add(entry);
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(0);
        }

        if (entry.Method != "内置自测")
        {
            RequestCount++;
        }
    }

    private void SetState(ServerState state, string detail)
    {
        _state = state;
        StatusDetail = detail;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusBadgeBackground));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(CanToggleService));
    }

    private void ObserveServerTask(HttpMcpServer server)
    {
        var task = _serverTask;
        if (task == null)
        {
            return;
        }

        _ = task.ContinueWith(
            completed =>
            {
                if (_stopRequested || completed.IsCanceled)
                {
                    return;
                }

                var message = completed.Exception?.GetBaseException().Message ?? "监听循环意外结束。";
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                try
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_server == server && !_stopRequested)
                        {
                            SetState(ServerState.Failed, "服务意外停止：" + message);
                            Endpoint = "—";
                            ListenAddress = "—";
                        }
                    }));
                }
                catch (Exception)
                {
                    // 应用程序正在关闭。
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>进程退出时使用的最佳努力同步清理。</summary>
    internal void DisposeResources()
    {
        try
        {
            _serverCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放。
        }

        try
        {
            _server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 关闭期间尽力而为。
        }

        _registration?.Dispose();
        _registration = null;
    }
}
