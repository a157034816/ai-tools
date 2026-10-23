using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace AiTools.Samples.McpAgentManager;

/// <summary>承载 MCP 智能体管理器窗口的 WPF 应用程序。</summary>
public partial class App : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine("[McpAgentManager] UI 线程未处理异常：" + e.Exception);
        try
        {
            if (MainWindow?.DataContext is ViewModels.MainViewModel viewModel)
            {
                viewModel.ReportException("界面发生异常", e.Exception);
            }
        }
        catch (Exception)
        {
            // 处理异常时，状态更新绝不能再次抛出异常。
        }

        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => Debug.WriteLine("[McpAgentManager] 未处理异常：" + e.ExceptionObject);
}
