using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace AiTools.Samples.McpAgentManager;

/// <summary>将 <see cref="AiTools.Samples.McpAgents.AgentKind"/> 转换为智能体卡片上显示的简短格式标签。</summary>
public sealed class AgentKindToStringConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            AiTools.Samples.McpAgents.AgentKind.JsonMcpServers => "JSON · mcpServers",
            AiTools.Samples.McpAgents.AgentKind.JsonMcpObject => "JSON · mcp",
            AiTools.Samples.McpAgents.AgentKind.TomlMcpServers => "TOML · mcp_servers",
            AiTools.Samples.McpAgents.AgentKind.YamlMcpServers => "YAML · mcp_servers",
            AiTools.Samples.McpAgents.AgentKind.Unsupported => "仅导出",
            _ => "未知"
        };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>当绑定的布尔值为 true 时返回传入的参数画刷，否则返回弱化画刷。</summary>
public sealed class BooleanToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true)
        {
            return parameter as Brush
                ?? (Application.Current?.TryFindResource("AccentBrush") ?? Brushes.DeepSkyBlue);
        }

        return Application.Current?.TryFindResource("TextMutedBrush") ?? Brushes.Gray;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 该附加属性将 <see cref="PasswordBox.Password"/> 暴露为可双向绑定的值，
/// 使内联密钥编辑器无需代码隐藏即可由 ViewModel 驱动。
/// </summary>
public static class PasswordBoxAssist
{
    /// <summary>标识可绑定的密码附加属性。</summary>
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword",
        typeof(string),
        typeof(PasswordBoxAssist),
        new FrameworkPropertyMetadata(
            defaultValue: null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnBoundPasswordChanged));

    private static readonly DependencyProperty UpdatingPasswordProperty = DependencyProperty.RegisterAttached(
        "UpdatingPassword",
        typeof(bool),
        typeof(PasswordBoxAssist),
        new PropertyMetadata(false));

    /// <summary>获取可绑定的密码值。</summary>
    /// <param name="element">目标密码框。</param>
    /// <returns>绑定的密码值。</returns>
    public static string GetBoundPassword(DependencyObject element)
        => (string)element.GetValue(BoundPasswordProperty);

    /// <summary>设置可绑定的密码值。</summary>
    /// <param name="element">目标密码框。</param>
    /// <param name="value">新密码值。</param>
    public static void SetBoundPassword(DependencyObject element, string value)
        => element.SetValue(BoundPasswordProperty, value);

    private static bool GetUpdatingPassword(DependencyObject element)
        => (bool)element.GetValue(UpdatingPasswordProperty);

    private static void SetUpdatingPassword(DependencyObject element, bool value)
        => element.SetValue(UpdatingPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not PasswordBox passwordBox || GetUpdatingPassword(passwordBox))
        {
            return;
        }

        passwordBox.PasswordChanged -= OnPasswordChanged;
        passwordBox.Password = args.NewValue as string ?? string.Empty;
        passwordBox.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        SetUpdatingPassword(passwordBox, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        SetUpdatingPassword(passwordBox, false);
    }
}
