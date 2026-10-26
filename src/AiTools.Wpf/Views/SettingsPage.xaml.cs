using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AiTools.Wpf.ViewModels;

namespace AiTools.Wpf.Views;

/// <summary>
/// 用于管理服务商、模型和默认 system prompt 的设置页。
/// 在 <see cref="AiToolsWpf.Initialize(AiToolsWpfOptions?)"/> 执行前创建此页面是安全的。
/// </summary>
public partial class SettingsPage : UserControl
{
    private ProviderEditorViewModel? _observedProvider;

    /// <summary>创建设置页及其 ViewModel。</summary>
    public SettingsPage()
    {
        ViewModel = new SettingsViewModel();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnLoaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>获取支撑此页面的 ViewModel。</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    /// 将设置加载到页面。必须在 UI 线程上调用。可安全地多次调用；
    /// 仅在首次成功调用时加载设置。宿主可以显式调用此方法，而不必等待 <c>Loaded</c>。
    /// </summary>
    public Task InitializeAsync() => ViewModel.InitializeAsync();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 初始化在视图模型内部已捕获可预期异常，这里兜底，避免 UI 线程出现未处理异常。
            System.Diagnostics.Debug.WriteLine($"[AiTools] 设置页初始化失败：{ex}");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.SelectedProvider))
        {
            return;
        }

        if (_observedProvider != null)
        {
            _observedProvider.PropertyChanged -= OnProviderPropertyChanged;
        }

        _observedProvider = ViewModel.SelectedProvider;

        if (_observedProvider != null)
        {
            _observedProvider.PropertyChanged += OnProviderPropertyChanged;
        }
    }

    private void OnProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProviderEditorViewModel.IsModelPickerOpen) ||
            sender is not ProviderEditorViewModel editor ||
            !editor.IsModelPickerOpen)
        {
            return;
        }

        // 面板展开后可能落在可视区外（它位于模型列表上方较远处），滚动到可见位置。
        Dispatcher.BeginInvoke(
            new Action(() => ModelPickerPanel.BringIntoView()),
            System.Windows.Threading.DispatcherPriority.Background);
    }
}
