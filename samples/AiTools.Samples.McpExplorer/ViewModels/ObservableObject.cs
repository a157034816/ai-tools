using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiTools.Samples.McpExplorer.ViewModels;

/// <summary>引发变更通知的 ViewModel 基类。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>设置字段，并在值发生变化时引发 <see cref="PropertyChanged"/>。</summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">支持字段。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">调用方提供的属性名称。</param>
    /// <returns>值已赋值时返回 <see langword="true"/>。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>引发 <see cref="PropertyChanged"/>。</summary>
    /// <param name="propertyName">已更改属性的名称。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
