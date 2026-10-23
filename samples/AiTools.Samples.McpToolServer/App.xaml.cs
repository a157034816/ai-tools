using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace AiTools.Samples.McpToolServer;

/// <summary>承载交互式 MCP 工具服务演示窗口的 WPF 应用程序。</summary>
public partial class App : Application
{
    private MainViewModel? _viewModel;

    /// <summary>获取或设置用于配置服务的已解析命令行选项。</summary>
    internal CommandLineOptions? Options { get; set; }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = Options ?? new CommandLineOptions();
        _viewModel = new MainViewModel(options);

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        StartServiceAsync(_viewModel);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.DisposeResources();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[MCP] 退出清理失败：" + ex.Message);
        }

        base.OnExit(e);
    }

    private static async void StartServiceAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.StartAsync();
        }
        catch (Exception ex)
        {
            // StartAsync 内部已处理预期异常；这里兜底，确保 UI 不会出现未处理的 async void 异常。
            viewModel.ReportUiError("启动服务时发生未预期错误：" + ex.Message);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine("[MCP] UI 线程未处理异常：" + e.Exception);
        try
        {
            _viewModel?.ReportUiError("界面发生异常：" + e.Exception.Message);
        }
        catch (Exception)
        {
            // 状态栏更新失败时仍然阻止应用崩溃。
        }

        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Debug.WriteLine("[MCP] 未处理异常：" + e.ExceptionObject);
    }
}
