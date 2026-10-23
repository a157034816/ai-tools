using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AiTools.Samples.McpExplorer.ViewModels;

/// <summary>把 <see langword="true"/> 转换为 <see cref="Visibility.Collapsed"/>，反之亦然。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool flag && flag ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility visibility && visibility != Visibility.Visible;
    }
}

/// <summary>把非空字符串转换为 <see cref="Visibility.Visible"/>。</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>把空字符串转换为 <see cref="Visibility.Visible"/>（与 <see cref="StringNotEmptyToVisibilityConverter"/> 相反）。</summary>
public sealed class InverseStringToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string text && !string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>把布尔值转换为 <see cref="Visibility"/>。</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool flag && flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility visibility && visibility == Visibility.Visible;
    }
}

/// <summary>仅当整数为零时将其转换为 <see cref="Visibility.Visible"/>。</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>供 ViewModel 共享的冻结画刷，使 Binding 能为状态指示器着色。</summary>
public static class UiBrushes
{
    /// <summary>主文本颜色（#E6E8EB）。</summary>
    public static readonly Brush Text = Create(0xE6, 0xE8, 0xEB);

    /// <summary>次级文本颜色（#9CA3AF）。</summary>
    public static readonly Brush TextSecondary = Create(0x9C, 0xA3, 0xAF);

    /// <summary>强调色（#6EA8FE）。</summary>
    public static readonly Brush Accent = Create(0x6E, 0xA8, 0xFE);

    /// <summary>成功色（#46A758）。</summary>
    public static readonly Brush Success = Create(0x46, 0xA7, 0x58);

    /// <summary>危险色（#E5484D）。</summary>
    public static readonly Brush Danger = Create(0xE5, 0x48, 0x4D);

    /// <summary>警告色（#F5A524）。</summary>
    public static readonly Brush Warning = Create(0xF5, 0xA5, 0x24);

    private static Brush Create(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
