using System;
using AiTools.Abstractions;
using AiTools.Wpf.Mvvm;

namespace AiTools.Wpf.ViewModels;

/// <summary>「获取模型列表」面板中的单个候选模型。</summary>
public sealed class FetchedModelRowViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isAlreadyConfigured;

    /// <summary>从服务商返回的模型信息创建候选行。</summary>
    /// <param name="info">服务商返回的模型信息。</param>
    /// <param name="isAlreadyConfigured">服务商配置中是否已有同名模型。</param>
    public FetchedModelRowViewModel(ModelInfo info, bool isAlreadyConfigured)
    {
        ArgumentNullException.ThrowIfNull(info);

        Id = info.Id;
        DisplayName = info.DisplayName;
        _isAlreadyConfigured = isAlreadyConfigured;
    }

    /// <summary>当 <see cref="IsSelected"/> 变化时引发。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>获取模型标识符。</summary>
    public string Id { get; }

    /// <summary>获取服务商提供的显示名称；可能为空。</summary>
    public string DisplayName { get; }

    /// <summary>获取是否应显示服务商提供的名称。</summary>
    public bool HasDisplayName => !string.IsNullOrWhiteSpace(DisplayName);

    /// <summary>获取或设置是否勾选此模型。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>获取此模型是否已在服务商配置中。</summary>
    public bool IsAlreadyConfigured
    {
        get => _isAlreadyConfigured;
        internal set => SetProperty(ref _isAlreadyConfigured, value);
    }
}
