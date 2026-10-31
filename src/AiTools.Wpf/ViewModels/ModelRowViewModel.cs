using System;
using System.ComponentModel;
using AiTools.Configuration;
using AiTools.Wpf.Mvvm;

namespace AiTools.Wpf.ViewModels;

/// <summary>服务商编辑器中单个 model 条目的可编辑 ViewModel。</summary>
public sealed class ModelRowViewModel : ObservableObject
{
    private string _id = string.Empty;
    private string _displayName = string.Empty;
    private bool _enabled = true;

    /// <summary>从 model 配置创建 model 行。配置会被复制，而不是保留引用。</summary>
    /// <param name="owner">拥有此行所属的 provider 编辑器。</param>
    /// <param name="config">要编辑的 model 配置。</param>
    public ModelRowViewModel(ProviderEditorViewModel owner, ModelConfig config)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(config);

        _id = config.Id ?? string.Empty;
        _displayName = config.DisplayName ?? string.Empty;
        _enabled = config.Enabled;
    }

    /// <summary>当可编辑的值发生变化时引发。</summary>
    public event EventHandler<PropertyChangedEventArgs>? Changed;

    /// <summary>获取拥有此行的 provider 编辑器。</summary>
    public ProviderEditorViewModel Owner { get; internal set; }

    /// <summary>获取或设置发送给服务商 API 的 model 标识符。</summary>
    public string Id
    {
        get => _id;
        set
        {
            if (SetProperty(ref _id, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HeaderText));
                OnChanged(nameof(Id));
            }
        }
    }

    /// <summary>获取或设置可选的显示名称。为空时回退到 <see cref="Id"/>。</summary>
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HeaderText));
                OnChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>获取或设置是否在对话页的 model 选择器中提供此 model。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnChanged(nameof(Enabled));
            }
        }
    }

    /// <summary>获取标识符字段为空时此行显示的标签。</summary>
    public string HeaderText => string.IsNullOrWhiteSpace(_displayName) ? _id : _displayName;

    /// <summary>创建当前行值的深拷贝。</summary>
    public ModelConfig CreateModelConfig() => new()
    {
        Id = _id.Trim(),
        DisplayName = _displayName.Trim(),
        Enabled = _enabled
    };

    private void OnChanged(string propertyName) => Changed?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
