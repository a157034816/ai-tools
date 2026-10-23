using System;
using System.Collections.ObjectModel;
using AiTools.Wpf.Mvvm;

namespace AiTools.Wpf.ViewModels;

/// <summary>表示对话页上的单条消息气泡。</summary>
public sealed class ChatMessageViewModel : ObservableObject
{
    private string _text;
    private string _reasoning = string.Empty;
    private string _errorText = string.Empty;
    private string _usageText = string.Empty;
    private bool _isStreaming;

    /// <summary>创建消息 ViewModel。</summary>
    /// <param name="isUser">消息是否由用户撰写。</param>
    /// <param name="text">初始文本内容。</param>
    public ChatMessageViewModel(bool isUser, string text = "")
    {
        IsUser = isUser;
        Role = isUser ? "user" : "assistant";
        _text = text ?? string.Empty;
        Timestamp = DateTimeOffset.Now;
    }

    /// <summary>获取消息角色：<c>user</c> 或 <c>assistant</c>。</summary>
    public string Role { get; }

    /// <summary>获取消息是否由用户撰写。</summary>
    public bool IsUser { get; }

    /// <summary>获取创建时间。</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>获取格式化后用于显示的创建时间。</summary>
    public string TimestampDisplay => Timestamp.LocalDateTime.ToString("HH:mm");

    /// <summary>获取或设置流式输出的推理/思考文本。</summary>
    public string Reasoning
    {
        get => _reasoning;
        set
        {
            if (SetProperty(ref _reasoning, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasReasoning));
            }
        }
    }

    /// <summary>获取是否有可用的推理文本。</summary>
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);

    /// <summary>获取或设置消息文本。</summary>
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? string.Empty);
    }

    /// <summary>
    /// 获取或设置实际发送给模型的提示词。斜杠命令的气泡只显示命令名，
    /// 这里保存展开后的完整提示词，供重试复用。为 null 时模型收到的是 <see cref="Text"/>。
    /// </summary>
    public string? PromptText { get; set; }

    /// <summary>获取或设置在消息下方显示的错误文本。</summary>
    public string ErrorText
    {
        get => _errorText;
        set
        {
            if (SetProperty(ref _errorText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>获取是否存在错误。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>获取或设置消息是否仍在 streaming。</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    /// <summary>获取或设置 Token 用量说明，例如 <c>12→34 tokens</c>。</summary>
    public string UsageText
    {
        get => _usageText;
        set
        {
            if (SetProperty(ref _usageText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasUsage));
            }
        }
    }

    /// <summary>获取是否有可用的 Token 用量说明。</summary>
    public bool HasUsage => !string.IsNullOrWhiteSpace(UsageText);

    /// <summary>获取显示在消息正文上方的 Tool 调用标签。</summary>
    public ObservableCollection<string> ToolCalls { get; } = new();

    /// <summary>获取是否存在任何 Tool 调用标签。</summary>
    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>向消息添加一个 Tool 调用标签。</summary>
    /// <param name="display">预先格式化好的标签文本。</param>
    public void AddToolCall(string display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return;
        }

        ToolCalls.Add(display);
        OnPropertyChanged(nameof(HasToolCalls));
    }
}
