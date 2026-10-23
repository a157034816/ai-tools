using System.Windows;

namespace AiTools.Wpf.Demo;

/// <summary>演示主窗口：在对话页与设置页之间切换。</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 对话页请求打开设置时，自动切换到设置标签。
        ChatView.ViewModel.SettingsRequested += (_, _) => SettingsTab.IsChecked = true;
    }

    private void ChatTab_Checked(object sender, RoutedEventArgs e)
    {
        if (ChatView == null || SettingsView == null)
        {
            return;
        }

        ChatView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
    }

    private void SettingsTab_Checked(object sender, RoutedEventArgs e)
    {
        if (ChatView == null || SettingsView == null)
        {
            return;
        }

        ChatView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }
}
