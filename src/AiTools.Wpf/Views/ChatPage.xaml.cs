using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using AiTools.Wpf.ViewModels;

namespace AiTools.Wpf.Views;

/// <summary>
/// 可复用的对话页。显示服务商/model 选择器、streaming 消息列表和多行输入框。
/// 在 <see cref="AiToolsWpf.Initialize(AiToolsWpfOptions)"/> 之后调用 <see cref="InitializeAsync"/> 以加载已配置的服务商。
/// </summary>
public partial class ChatPage : UserControl
{
    /// <summary>使用全新的 ViewModel 创建对话页。</summary>
    public ChatPage()
    {
        InitializeComponent();
        ViewModel = new ChatViewModel();
        DataContext = ViewModel;
        ViewModel.ScrollToEndRequested += OnScrollToEndRequested;
        Loaded += OnLoaded;
    }

    /// <summary>获取页面 ViewModel。</summary>
    public ChatViewModel ViewModel { get; }

    /// <summary>加载设置并填充服务商和 model 选择器。永不抛出异常。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception)
        {
            // InitializeAsync 会通过 StatusMessage 呈现失败信息；绝不让异常从此处逸出。
        }
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception)
        {
            // InitializeAsync 已处理自身错误；任何异常都不能从 async void 处理器逸出。
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    MessagesScrollViewer.ScrollToEnd();
                }
                catch (Exception)
                {
                }
            }));
        }
        catch (Exception)
        {
        }
    }

    private async void SendButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await SendAsync();
    }

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        await SendAsync();
    }

    private async Task SendAsync()
    {
        InputBox.Focus();
        try
        {
            await ViewModel.SendAsync();
        }
        catch (Exception)
        {
            // 错误会通过 ViewModel 的状态呈现；绝不让异常从 async void 调用方逸出。
        }
        finally
        {
            InputBox.Focus();
        }
    }
}
