using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AiTools.Samples.McpAgentManager.ViewModels;
using Microsoft.Win32;

namespace AiTools.Samples.McpAgentManager;

/// <summary>MCP 智能体管理器示例的主窗口。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>创建主窗口并启动初始智能体探测。</summary>
    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.ActivityLog.CollectionChanged += (_, _) =>
        {
            if (_viewModel.IsLogAutoScroll)
            {
                ScrollLogToEnd();
            }
        };

        foreach (var preset in _viewModel.Presets)
        {
            preset.PropertyChanged += OnPresetPropertyChanged;
        }

        Loaded += OnLoaded;
    }

    private void OnPresetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PresetItemViewModel.IsEditorOpen) ||
            sender is not PresetItemViewModel { IsEditorOpen: true } preset)
        {
            return;
        }

        // 内联编辑器会使卡片变高，布局完成后确保其完全可见。
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                try
                {
                    PresetList.ScrollIntoView(preset);
                }
                catch (Exception)
                {
                    // 滚动仅为界面效果。
                }
            }));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Loaded -= OnLoaded;
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _viewModel.ReportException("启动时探测智能体失败", ex);
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出 MCP JSON",
                Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = BuildDefaultFileName()
            };

            if (dialog.ShowDialog(this) != true)
            {
                _viewModel.ReportStatus("已取消导出。");
                return;
            }

            var written = await _viewModel.ExportAsync(dialog.FileName);
            if (!written)
            {
                return;
            }

            try
            {
                Clipboard.SetText(_viewModel.ExportPreview);
                _viewModel.ReportStatus("已导出并复制到剪贴板：" + dialog.FileName);
            }
            catch (Exception ex)
            {
                _viewModel.ReportStatus("已导出文件，但复制到剪贴板失败：" + ex.Message);
            }
        }
        catch (Exception ex)
        {
            _viewModel.ReportException("导出 JSON 失败", ex);
        }
    }

    private void OnCopyJsonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.ExportPreview);
            _viewModel.ReportStatus("已复制到剪贴板：" + _viewModel.ExportScopeText);
        }
        catch (Exception ex)
        {
            _viewModel.ReportException("复制 JSON 失败", ex);
        }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.ActivityLog.Clear();
            _viewModel.ReportStatus("活动日志已清空。");
        }
        catch (Exception ex)
        {
            _viewModel.ReportException("清空日志失败", ex);
        }
    }

    private string BuildDefaultFileName()
    {
        var agent = _viewModel.SelectedAgent;
        var preset = _viewModel.SelectedPreset;
        var baseName = "mcp-servers";
        if (agent != null)
        {
            baseName = agent.Id + "-mcp";
        }

        if (preset != null)
        {
            baseName += "-" + preset.Id;
        }

        return baseName + ".json";
    }

    private void ScrollLogToEnd()
    {
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
            // 滚动仅为界面效果，绝不能导致 UI 出错。
        }
    }
}
