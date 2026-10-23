using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AiTools.Samples.McpAgents;

namespace AiTools.Samples.McpAgentManager.ViewModels;

/// <summary>单个 MCP 预设卡片的 ViewModel。</summary>
public sealed class PresetItemViewModel : ObservableObject
{
    private bool _isInstalled;
    private bool _isEditorOpen;

    /// <summary>创建预设卡片及其内联环境变量编辑行。</summary>
    /// <param name="preset">预设元数据。</param>
    public PresetItemViewModel(McpPreset preset)
    {
        Preset = preset ?? throw new ArgumentNullException(nameof(preset));
        RequiredVariables = new ObservableCollection<EnvironmentVariableViewModel>();
        foreach (var pair in preset.RequiredEnvironment)
        {
            RequiredVariables.Add(new EnvironmentVariableViewModel(pair.Key, pair.Value));
        }
    }

    /// <summary>获取预设元数据。</summary>
    public McpPreset Preset { get; }

    /// <summary>获取预设标识符。</summary>
    public string Id => Preset.Id;

    /// <summary>获取显示名称。</summary>
    public string DisplayName => Preset.DisplayName;

    /// <summary>获取简短描述。</summary>
    public string Description => Preset.Description;

    /// <summary>获取传输方式标签。</summary>
    public string TransportText => Preset.Transport;

    /// <summary>获取预设是否使用 stdio。</summary>
    public bool IsStdio => Preset.IsStdio;

    /// <summary>获取预设是否需要用户提供环境变量值。</summary>
    public bool RequiresUserInput => Preset.RequiresUserInput;

    /// <summary>获取详情窗格中显示的命令（stdio）或 URL（http/sse）。</summary>
    public string TargetText => Preset.IsStdio
        ? (Preset.Command + " " + string.Join(" ", Preset.Arguments)).Trim()
        : Preset.Url ?? string.Empty;

    /// <summary>获取 <see cref="TargetText"/> 的标签。</summary>
    public string TargetLabel => Preset.IsStdio ? "命令" : "URL";

    /// <summary>获取预设是否至少需要一个环境变量。</summary>
    public bool HasRequiredEnvironment => RequiredVariables.Count > 0;

    /// <summary>获取所需环境变量的可读列表。</summary>
    public string RequiredEnvironmentSummary => RequiredVariables.Count == 0
        ? "无"
        : string.Join(Environment.NewLine, RequiredVariables.Select(variable => "• " + variable.Key + " — " + variable.Description));

    /// <summary>获取可编辑的环境变量行。</summary>
    public ObservableCollection<EnvironmentVariableViewModel> RequiredVariables { get; }

    /// <summary>获取或设置预设是否已为选中智能体配置。</summary>
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(ActionText));
                OnPropertyChanged(nameof(ActionTooltip));
                OnPropertyChanged(nameof(HasBadges));
                OnPropertyChanged(nameof(ShowInputHint));
            }
        }
    }

    /// <summary>获取切换按钮的标签。</summary>
    public string ActionText => _isInstalled ? "移除" : "添加";

    /// <summary>获取切换按钮的 ToolTip 文本。</summary>
    public string ActionTooltip => _isInstalled ? "从当前智能体中移除此 MCP" : "安装到当前智能体";

    /// <summary>获取卡片是否显示已安装或需要输入徽章。</summary>
    public bool HasBadges => _isInstalled || RequiresUserInput;

    /// <summary>获取或设置内联环境变量编辑器是否可见。</summary>
    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        set
        {
            if (SetProperty(ref _isEditorOpen, value))
            {
                OnPropertyChanged(nameof(ShowCardDescription));
                OnPropertyChanged(nameof(ShowInputHint));
                OnPropertyChanged(nameof(ShowInstallEditor));
            }
        }
    }

    /// <summary>获取是否显示卡片描述（编辑器打开时隐藏，以保持卡片紧凑）。</summary>
    public bool ShowCardDescription => !_isEditorOpen;

    /// <summary>获取是否显示“需要环境变量”提示。</summary>
    public bool ShowInputHint => RequiresUserInput && !_isEditorOpen && !_isInstalled;

    /// <summary>获取内联安装编辑器块是否可见。</summary>
    public bool ShowInstallEditor => _isEditorOpen;

    /// <summary>显示内联环境变量编辑器。</summary>
    public void OpenEditor() => IsEditorOpen = true;

    /// <summary>隐藏内联环境变量编辑器。</summary>
    public void CloseEditor() => IsEditorOpen = false;

    /// <summary>收集用户输入的非空环境变量值。</summary>
    /// <returns>以环境变量名为键的可变字典。</returns>
    public Dictionary<string, string> BuildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in RequiredVariables)
        {
            if (!string.IsNullOrWhiteSpace(variable.Value))
            {
                environment[variable.Key] = variable.Value;
            }
        }

        return environment;
    }

    /// <summary>获取是否每个必需的环境变量都有值。</summary>
    public bool HasCompleteEnvironment => McpEntryFormatter.HasRequiredEnvironment(Preset, BuildEnvironment());
}

/// <summary>内联安装编辑器中的一行必需环境变量。</summary>
public sealed class EnvironmentVariableViewModel : ObservableObject
{
    private string _value = string.Empty;
    private bool _isRevealed;

    /// <summary>创建变量行。</summary>
    /// <param name="key">环境变量名。</param>
    /// <param name="description">显示在名称下方的描述。</param>
    public EnvironmentVariableViewModel(string key, string description)
    {
        Key = key;
        Description = description;
        ToggleRevealCommand = new RelayCommand(_ => IsRevealed = !IsRevealed);
    }

    /// <summary>获取用于切换密码遮罩的 Command。</summary>
    public RelayCommand ToggleRevealCommand { get; }

    /// <summary>获取环境变量名。</summary>
    public string Key { get; }

    /// <summary>获取期望值的描述。</summary>
    public string Description { get; }

    /// <summary>获取或设置输入的值。</summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>获取或设置值是否以明文显示。</summary>
    public bool IsRevealed
    {
        get => _isRevealed;
        set
        {
            if (SetProperty(ref _isRevealed, value))
            {
                OnPropertyChanged(nameof(IsMasked));
                OnPropertyChanged(nameof(RevealButtonText));
            }
        }
    }

    /// <summary>获取值是否以密码框遮罩显示。</summary>
    public bool IsMasked => !_isRevealed;

    /// <summary>获取遮罩切换按钮的标签。</summary>
    public string RevealButtonText => _isRevealed ? "隐藏" : "显示";
}
