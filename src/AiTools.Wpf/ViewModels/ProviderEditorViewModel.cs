using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using AiTools.Wpf.Configuration;
using AiTools.Wpf.Mvvm;

namespace AiTools.Wpf.ViewModels;

/// <summary>服务商连接测试的状态。</summary>
public enum ConnectionTestState
{
    /// <summary>尚未运行测试。</summary>
    Idle,

    /// <summary>测试正在运行。</summary>
    Running,

    /// <summary>上次测试成功完成。</summary>
    Succeeded,

    /// <summary>上次测试失败。</summary>
    Failed
}

/// <summary>
/// 单个服务商的可编辑 ViewModel。值从 <see cref="ProviderConfig"/> 复制而来，
/// 因此编辑页面不会修改 <see cref="AiToolsWpf"/> 缓存的设置。
/// </summary>
public sealed class ProviderEditorViewModel : ObservableObject
{
    private readonly ObservableCollection<ModelRowViewModel> _models;
    private string _id;
    private string _displayName;
    private string _protocol;
    private string _baseUrl;
    private string _apiKey;
    private bool _enabled;
    private bool _isApiKeyVisible;
    private string _testStatus = string.Empty;
    private ConnectionTestState _testState = ConnectionTestState.Idle;

    /// <summary>从服务商配置创建编辑器。配置会被复制，而不是保留引用。</summary>
    /// <param name="config">要编辑的服务商配置。</param>
    public ProviderEditorViewModel(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _id = config.Id ?? string.Empty;
        _displayName = config.DisplayName ?? string.Empty;
        _protocol = string.IsNullOrWhiteSpace(config.Protocol) ? ProviderProtocols.OpenAi : config.Protocol;
        _baseUrl = config.BaseUrl ?? string.Empty;
        _apiKey = config.ApiKey ?? string.Empty;
        _enabled = config.Enabled;

        _models = new ObservableCollection<ModelRowViewModel>(
            (config.Models ?? new List<ModelConfig>()).Select(model => new ModelRowViewModel(this, model)));
        _models.CollectionChanged += OnModelsCollectionChanged;
        foreach (var model in _models)
        {
            model.Changed += OnModelChanged;
        }
    }

    /// <summary>当可编辑的值或 model 集合发生变化时引发。</summary>
    public event EventHandler? Changed;

    /// <summary>获取稳定的服务商标识符。</summary>
    public string Id => _id;

    /// <summary>获取或设置面向用户的服务商名称。</summary>
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HeaderText));
                OnChanged();
            }
        }
    }

    /// <summary>获取或设置通信协议；参见 <see cref="ProviderProtocols"/>。</summary>
    public string Protocol
    {
        get => _protocol;
        set
        {
            if (SetProperty(ref _protocol, value ?? string.Empty))
            {
                OnChanged();
            }
        }
    }

    /// <summary>获取或设置 API base URL。</summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetProperty(ref _baseUrl, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(StatusText));
                OnChanged();
            }
        }
    }

    /// <summary>获取或设置 API Key。设置存储在持久化时会对其加密。</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(MaskedApiKey));
                OnPropertyChanged(nameof(StatusText));
                OnChanged();
            }
        }
    }

    /// <summary>获取或设置是否向对话页提供此服务商。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnChanged();
            }
        }
    }

    /// <summary>获取或设置是否以明文显示 API Key。</summary>
    public bool IsApiKeyVisible
    {
        get => _isApiKeyVisible;
        set
        {
            if (SetProperty(ref _isApiKeyVisible, value))
            {
                OnPropertyChanged(nameof(MaskedApiKey));
            }
        }
    }

    /// <summary>获取适合显示的 API Key；除非设置了 <see cref="IsApiKeyVisible"/>，否则进行掩码处理。</summary>
    public string MaskedApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                return "未设置";
            }

            if (_isApiKeyVisible)
            {
                return _apiKey;
            }

            return _apiKey.Length <= 4
                ? new string('•', 8)
                : "••••••••" + _apiKey[^4..];
        }
    }

    /// <summary>获取已配置的模型。</summary>
    public ObservableCollection<ModelRowViewModel> Models => _models;

    /// <summary>获取内置服务商工厂支持的协议。</summary>
    public IReadOnlyList<string> ProtocolOptions => ProviderProtocols.All;

    /// <summary>获取协议选择器中可用的协议。是 <see cref="ProtocolOptions"/> 的别名。</summary>
    public IReadOnlyList<string> AvailableProtocols => ProtocolOptions;

    /// <summary>获取是否可以删除此服务商。所有服务商均可删除。</summary>
    public bool CanDelete => true;

    /// <summary>获取服务商列表中显示的标题。</summary>
    public string HeaderText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_displayName))
            {
                return _displayName;
            }

            return string.IsNullOrWhiteSpace(_id) ? "未命名服务商" : _id;
        }
    }

    /// <summary>获取在服务商列表中显示的服务商状态简短摘要。</summary>
    public string StatusText
    {
        get
        {
            var parts = new List<string> { _enabled ? "已启用" : "已停用" };
            var enabledModels = _models.Count(model => model.Enabled);
            parts.Add(enabledModels == 0 ? "无可用模型" : $"{enabledModels} 个模型");

            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                parts.Add("未配置 Base URL");
            }
            else if (string.IsNullOrWhiteSpace(_apiKey) && !ProviderConfig.IsLocalEndpoint(_baseUrl))
            {
                parts.Add("未配置 API Key");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>获取描述上次连接测试的文本。</summary>
    public string TestStatus => _testStatus;

    /// <summary>获取上次连接测试的状态。</summary>
    public ConnectionTestState TestState => _testState;

    /// <summary>创建当前编辑器值的深拷贝。</summary>
    public ProviderConfig CreateProviderConfig()
    {
        var config = new ProviderConfig
        {
            Id = _id.Trim(),
            DisplayName = _displayName.Trim(),
            Protocol = _protocol.Trim(),
            BaseUrl = _baseUrl.Trim(),
            ApiKey = _apiKey.Trim(),
            Enabled = _enabled
        };

        config.Models.AddRange(_models.Select(model => model.CreateModelConfig()));
        return config;
    }

    internal void SetId(string id)
    {
        if (string.Equals(_id, id, StringComparison.Ordinal))
        {
            return;
        }

        _id = id;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(HeaderText));
    }

    internal ModelRowViewModel AddModel()
    {
        var row = new ModelRowViewModel(this, new ModelConfig { Enabled = true });
        _models.Add(row);
        return row;
    }

    internal bool RemoveModel(ModelRowViewModel row) => _models.Remove(row);

    internal void SetTestStatus(ConnectionTestState state, string status)
    {
        _testState = state;
        _testStatus = status ?? string.Empty;
        OnPropertyChanged(nameof(TestState));
        OnPropertyChanged(nameof(TestStatus));
    }

    internal void ClearTestStatus()
    {
        if (_testState == ConnectionTestState.Idle && _testStatus.Length == 0)
        {
            return;
        }

        SetTestStatus(ConnectionTestState.Idle, string.Empty);
    }

    private void OnModelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (ModelRowViewModel row in e.OldItems)
            {
                row.Changed -= OnModelChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (ModelRowViewModel row in e.NewItems)
            {
                row.Owner = this;
                row.Changed += OnModelChanged;
            }
        }

        OnPropertyChanged(nameof(StatusText));
        OnChanged();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelRowViewModel.Enabled) or nameof(ModelRowViewModel.Id))
        {
            OnPropertyChanged(nameof(StatusText));
        }

        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
