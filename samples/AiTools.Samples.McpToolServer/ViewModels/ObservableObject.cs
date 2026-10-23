using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiTools.Samples.McpToolServer.ViewModels;

/// <summary>示例 ViewModel 使用的最小 <see cref="INotifyPropertyChanged"/> 基类。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>为指定的属性引发 <see cref="PropertyChanged"/>。</summary>
    /// <param name="propertyName">属性名称；由编译器自动填充。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>为支持字段赋值，并在值发生变化时引发 <see cref="PropertyChanged"/>。</summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">支持字段。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名称；由编译器自动填充。</param>
    /// <returns>值发生变化时返回 <see langword="true"/>。</returns>
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
}
