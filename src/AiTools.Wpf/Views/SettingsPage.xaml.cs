using System;
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
    /// <summary>创建设置页及其 ViewModel。</summary>
    public SettingsPage()
    {
        ViewModel = new SettingsViewModel();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnLoaded;
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
}
