using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AiTools.Samples.McpExplorer.ViewModels;

namespace AiTools.Samples.McpExplorer;

/// <summary>应用程序主窗口。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _autoRefreshTimer;
    private bool _shutdownCompleted;

    /// <summary>创建主窗口。</summary>
    /// <param name="viewModel">窗口 ViewModel。</param>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;

        _autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _autoRefreshTimer.Tick += OnAutoRefreshTick;

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (_viewModel.AutoRefresh)
            {
                _autoRefreshTimer.Start();
            }

            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _viewModel.ReportUnexpectedError(ex);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.AutoRefresh))
        {
            return;
        }

        if (_viewModel.AutoRefresh)
        {
            _autoRefreshTimer.Start();
        }
        else
        {
            _autoRefreshTimer.Stop();
        }
    }

    private void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        try
        {
            if (_viewModel.IsBusy)
            {
                return;
            }

            if (_viewModel.RefreshCommand.CanExecute(null))
            {
                _viewModel.RefreshCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            _viewModel.ReportUnexpectedError(ex);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_viewModel.InvokeToolCommand.CanExecute(null))
            {
                _viewModel.InvokeToolCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        // 延迟关闭窗口，直到客户端和传输完成释放，避免后台读取循环继续存活。
        e.Cancel = true;
        try
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _autoRefreshTimer.Stop();
            _autoRefreshTimer.Tick -= OnAutoRefreshTick;
            await _viewModel.ShutdownAsync();
        }
        catch (Exception)
        {
            // 关闭流程绝不能阻止窗口关闭。
        }
        finally
        {
            _shutdownCompleted = true;
            Close();
        }
    }
}
