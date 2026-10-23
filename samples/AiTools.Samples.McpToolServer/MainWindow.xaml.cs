using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AiTools.Samples.McpToolServer.ViewModels;

namespace AiTools.Samples.McpToolServer;

/// <summary>交互式 MCP 工具服务演示的主窗口。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closingHandled;

    /// <summary>创建主窗口。</summary>
    /// <param name="viewModel">驱动服务与工具自测的 ViewModel。</param>
    internal MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.Logs.CollectionChanged += (_, _) =>
        {
            if (_viewModel.IsLogAutoScroll)
            {
                ScrollLogToEnd();
            }
        };

        Closing += OnWindowClosing;
    }

    private async void OnToggleServiceClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel.IsRunning)
            {
                await _viewModel.StopAsync();
            }
            else
            {
                await _viewModel.StartAsync();
            }
        }
        catch (Exception ex)
        {
            _viewModel.ReportUiError("切换服务状态失败：" + ex.Message);
        }
    }

    private async void OnInvokeToolClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InvokeSelectedToolAsync();
        }
        catch (Exception ex)
        {
            _viewModel.ReportUiError("执行内置自测失败：" + ex.Message);
        }
    }

    private void OnClearLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.ClearLogs();
        }
        catch (Exception ex)
        {
            _viewModel.ReportUiError("清空日志失败：" + ex.Message);
        }
    }

    private void OnCopyEndpointClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_viewModel.IsRunning)
            {
                _viewModel.ReportStatus("服务未运行，暂无可复制的端点。");
                return;
            }

            Clipboard.SetText(_viewModel.Endpoint);
            _viewModel.ReportStatus("端点已复制到剪贴板：" + _viewModel.Endpoint);
        }
        catch (Exception ex)
        {
            _viewModel.ReportUiError("复制端点失败：" + ex.Message);
        }
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingHandled)
        {
            return;
        }

        _closingHandled = true;
        e.Cancel = true;

        try
        {
            await _viewModel.StopAsync();
        }
        catch (Exception ex)
        {
            _viewModel.ReportUiError("关闭窗口时停止服务失败：" + ex.Message);
        }
        finally
        {
            Closing -= OnWindowClosing;
            Close();
        }
    }

    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count == 0)
        {
            return;
        }

        try
        {
            LogList.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    if (LogList.Items.Count > 0)
                    {
                        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
                    }
                }));
        }
        catch (Exception)
        {
            // 滚动只是视觉优化，绝不能因此导致 UI 失败。
        }
    }
}
