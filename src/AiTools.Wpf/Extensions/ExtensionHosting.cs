using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AiTools.Abstractions;
using AiTools.Core.Http;
using AiTools.Extensions;
using AiTools.Configuration;

namespace AiTools.Wpf.Extensions;

/// <summary>
/// WPF 宿主的扩展界面实现：通知与状态栏直接复用对话页状态，
/// 对话框使用模态 <see cref="Window"/>。无窗口（未初始化 UI）时退化为非交互行为。
/// </summary>
public sealed class WpfExtensionUi : IExtensionUi, IExtensionChoiceUi
{
    private readonly Action<string, ExtensionNoticeKind>? _notify;
    private readonly Action<string?>? _setStatus;
    private readonly Func<Window?>? _ownerResolver;
    private readonly Asking.IAskPresenter? _askPresenter;

    /// <summary>创建 UI 桥。</summary>
    /// <param name="notify">显示通知。</param>
    /// <param name="setStatus">更新状态栏（null 清除）。</param>
    /// <param name="ownerResolver">返回对话框的属主窗口。</param>
    /// <param name="askPresenter">
    /// 带说明选项的呈现策略。为 null 时使用模态窗口（<see cref="ExtensionChoiceWindow"/>）；
    /// 传入 <see cref="Asking.InlineAskPresenter"/> 可改为输入框上方的内联面板。
    /// </param>
    public WpfExtensionUi(
        Action<string, ExtensionNoticeKind>? notify = null,
        Action<string?>? setStatus = null,
        Func<Window?>? ownerResolver = null,
        Asking.IAskPresenter? askPresenter = null)
    {
        _notify = notify;
        _setStatus = setStatus;
        _ownerResolver = ownerResolver;
        _askPresenter = askPresenter;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 配置了呈现策略时恒为 true：内联面板由视图绘制，不依赖 WPF <see cref="Application"/>
    /// （无窗口的测试宿主也能交互）。其余能力仍然需要窗口。
    /// </remarks>
    public bool HasDialogs => _askPresenter != null || Application.Current != null;

    /// <inheritdoc />
    public void Notify(string message, ExtensionNoticeKind kind = ExtensionNoticeKind.Info) => _notify?.Invoke(message, kind);

    /// <inheritdoc />
    public void SetStatus(string? text) => _setStatus?.Invoke(text);

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, bool defaultValue = false, CancellationToken cancellationToken = default)
    {
        if (!HasDialogs)
        {
            return Task.FromResult(defaultValue);
        }

        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => ConfirmAsync(title, message, defaultValue, cancellationToken)).Task.Unwrap();
        }

        var answer = MessageBox.Show(
            _ownerResolver?.Invoke() ?? Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            defaultValue ? MessageBoxResult.Yes : MessageBoxResult.No);

        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    /// <inheritdoc />
    public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default)
    {
        if (!HasDialogs)
        {
            return Task.FromResult<string?>(null);
        }

        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => InputAsync(title, placeholder, cancellationToken)).Task.Unwrap();
        }

        var window = new ExtensionInputWindow(title, placeholder ?? string.Empty);
        var owner = _ownerResolver?.Invoke() ?? Application.Current.MainWindow;
        if (owner != null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return Task.FromResult(window.ShowDialog() == true ? window.Value : null);
    }

    /// <inheritdoc />
    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default)
    {
        if (!HasDialogs || options == null || options.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => SelectAsync(title, options, cancellationToken)).Task.Unwrap();
        }

        var window = new ExtensionSelectWindow(title, options);
        var owner = _ownerResolver?.Invoke() ?? Application.Current.MainWindow;
        if (owner != null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return Task.FromResult(window.ShowDialog() == true ? window.SelectedOption : null);
    }

    /// <inheritdoc />
    public async Task<ExtensionChoiceResult?> ChooseAsync(
        string title,
        string? message,
        IReadOnlyList<ExtensionChoice> choices,
        CancellationToken cancellationToken = default)
    {
        if (choices == null || choices.Count == 0)
        {
            return null;
        }

        // 宿主配置了呈现策略时交给它：内联面板不需要 WPF Application，
        // 也不阻塞调用线程（面板由视图绘制，答案来自用户点击）。
        if (_askPresenter != null)
        {
            var prompt = new Asking.AskPrompt(
                title,
                message,
                choices.Select((choice, index) => new Asking.AskPromptOption(choice.Label, choice.Description, index)).ToArray());

            var response = await _askPresenter.AskAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (response.IsCancelled || response.SelectedIndex is not { } index || index < 0 || index >= choices.Count)
            {
                return null;
            }

            return new ExtensionChoiceResult
            {
                Index = index,
                Label = choices[index].Label,
                Description = choices[index].Description
            };
        }

        if (!HasDialogs)
        {
            return null;
        }

        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            return await dispatcher.InvokeAsync(() => ChooseAsync(title, message, choices, cancellationToken)).Task.Unwrap().ConfigureAwait(false);
        }

        var window = new ExtensionChoiceWindow(title, message, choices);
        var owner = _ownerResolver?.Invoke() ?? Application.Current.MainWindow;
        if (owner != null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        if (window.ShowDialog() != true || window.SelectedIndex < 0)
        {
            return null;
        }

        var selected = choices[window.SelectedIndex];
        return new ExtensionChoiceResult
        {
            Index = window.SelectedIndex,
            Label = selected.Label,
            Description = selected.Description
        };
    }
}

/// <summary>极简文本输入对话框。</summary>
internal sealed class ExtensionInputWindow : Window
{
    private readonly System.Windows.Controls.TextBox _input;

    public ExtensionInputWindow(string title, string placeholder)
    {
        Title = title;
        Width = 460;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        _input = new System.Windows.Controls.TextBox
        {
            Text = placeholder,
            Margin = new Thickness(16, 16, 16, 8),
            Padding = new Thickness(8, 6, 8, 6)
        };

        var ok = new System.Windows.Controls.Button { Content = "确定", Width = 88, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 88, IsCancel = true };

        ok.Click += (_, _) =>
        {
            Value = _input.Text;
            DialogResult = true;
        };

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(_input);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    /// <summary>获取用户输入。</summary>
    public string Value { get; private set; } = string.Empty;
}

/// <summary>极简选项对话框。</summary>
internal sealed class ExtensionSelectWindow : Window
{
    public ExtensionSelectWindow(string title, IReadOnlyList<string> options)
    {
        Title = title;
        Width = 460;
        Height = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = options,
            Margin = new Thickness(16, 16, 16, 8)
        };

        var ok = new System.Windows.Controls.Button { Content = "确定", Width = 88, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 88, IsCancel = true };

        ok.Click += (_, _) =>
        {
            SelectedOption = list.SelectedItem as string ?? options[0];
            DialogResult = true;
        };

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        Content = panel;
        Loaded += (_, _) => list.SelectedIndex = 0;
    }

    /// <summary>获取选中的选项。</summary>
    public string? SelectedOption { get; private set; }
}

/// <summary>带说明文本的选项对话框：每条选项是一个卡片，标题在上、说明在下。</summary>
internal sealed class ExtensionChoiceWindow : Window
{
    public ExtensionChoiceWindow(string title, string? message, IReadOnlyList<ExtensionChoice> choices)
    {
        Title = title;
        Width = 560;
        Height = Math.Min(620, 220 + (choices.Count * 74));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceBrush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont");

        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = choices,
            Margin = new Thickness(16, message == null ? 16 : 0, 16, 8),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = BuildItemStyle()
        };

        var itemTemplate = new DataTemplate(typeof(ExtensionChoice));
        var stack = new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel));
        stack.SetValue(System.Windows.Controls.StackPanel.MarginProperty, new Thickness(10, 8, 10, 8));

        var label = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        label.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ExtensionChoice.Label)));
        label.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 13.5);
        label.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.SemiBold);
        label.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, Application.Current.FindResource("TextBrush"));
        label.SetValue(System.Windows.Controls.TextBlock.TextWrappingProperty, TextWrapping.Wrap);

        var description = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        description.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ExtensionChoice.Description)));
        description.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 12.0);
        description.SetValue(System.Windows.Controls.TextBlock.ForegroundProperty, Application.Current.FindResource("TextSecondaryBrush"));
        description.SetValue(System.Windows.Controls.TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        description.SetValue(System.Windows.Controls.TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));

        stack.AppendChild(label);
        stack.AppendChild(description);
        itemTemplate.VisualTree = stack;
        list.ItemTemplate = itemTemplate;

        var ok = new System.Windows.Controls.Button
        {
            Content = "确定",
            Width = 88,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0),
            Style = Application.Current.FindResource("PrimaryButton") as Style
        };

        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 88,
            IsCancel = true,
            Style = Application.Current.FindResource("SecondaryButton") as Style
        };

        ok.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0)
            {
                list.SelectedIndex = 0;
            }

            SelectedIndex = list.SelectedIndex;
            DialogResult = true;
        };

        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
            {
                SelectedIndex = list.SelectedIndex;
                DialogResult = true;
            }
        };

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        panel.Children.Add(buttons);

        if (!string.IsNullOrWhiteSpace(message))
        {
            var messageBlock = new System.Windows.Controls.TextBlock
            {
                Text = message,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextSecondaryBrush"),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 16, 16, 10)
            };
            System.Windows.Controls.DockPanel.SetDock(messageBlock, System.Windows.Controls.Dock.Top);
            panel.Children.Add(messageBlock);
        }

        panel.Children.Add(list);
        Content = panel;
        Loaded += (_, _) =>
        {
            list.SelectedIndex = 0;
            list.Focus();
        };
    }

    /// <summary>获取用户选中的选项索引；未选择时为 -1。</summary>
    public int SelectedIndex { get; private set; } = -1;

    private static Style BuildItemStyle()
    {
        var style = new Style(typeof(System.Windows.Controls.ListBoxItem));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.MarginProperty, new Thickness(0, 0, 0, 6)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, Application.Current.FindResource("BackgroundBrush")));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, Application.Current.FindResource("BorderSubtleBrush")));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.CursorProperty, System.Windows.Input.Cursors.Hand));

        var template = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.ListBoxItem));
        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetValue(System.Windows.Controls.Border.BackgroundProperty,
            new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.Background))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
        border.SetValue(System.Windows.Controls.Border.BorderBrushProperty,
            new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.BorderBrush))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
        border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty,
            new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.BorderThickness))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
        border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(System.Windows.Controls.Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
        presenter.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hover = new Trigger { Property = System.Windows.Controls.Control.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, Application.Current.FindResource("AccentBrush")));
        template.Triggers.Add(hover);

        var selected = new Trigger { Property = System.Windows.Controls.ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(System.Windows.Controls.Control.BackgroundProperty, Application.Current.FindResource("AccentSubtleBrush")));
        selected.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, Application.Current.FindResource("AccentBrush")));
        template.Triggers.Add(selected);

        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));
        return style;
    }
}

/// <summary>
/// 把扩展注册的 provider 与虚拟模型接入 WPF 的 provider 工厂：
/// 虚拟模型把请求路由到实际模型，扩展 provider 用内置协议客户端实现。
/// </summary>
public sealed class ExtensionProviderRouter
{
    private readonly BuiltInProviderFactory _inner = new();
    private readonly ExtensionRuntime _runtime;
    private readonly IProviderFactory? _fallback;

    /// <summary>创建路由。</summary>
    /// <param name="runtime">扩展运行时。</param>
    /// <param name="fallback">扩展未处理时使用的工厂；默认为内置工厂。</param>
    public ExtensionProviderRouter(ExtensionRuntime runtime, IProviderFactory? fallback = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _fallback = fallback;
    }

    /// <summary>获取扩展注册的 provider 名称。</summary>
    public IReadOnlyList<string> ProviderNames => _runtime.Providers.Select(provider => provider.Name).ToArray();

    /// <summary>创建聊天客户端：优先匹配扩展 provider，其次匹配虚拟模型路由。</summary>
    /// <param name="provider">设置页中的服务商配置。</param>
    /// <param name="model">请求的模型名（可能为虚拟模型）。</param>
    /// <param name="observer">可选的 HTTP 观察器。</param>
    public IChatClient CreateChatClient(ProviderConfig provider, string? model, IAiHttpObserver? observer = null)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        var virtualModel = _runtime.ResolveVirtualModel(model ?? provider.Models.FirstOrDefault()?.Id);
        if (virtualModel != null)
        {
            var targetProvider = provider;
            if (!string.IsNullOrWhiteSpace(virtualModel.Provider) &&
                !string.Equals(virtualModel.Provider, provider.Id, StringComparison.OrdinalIgnoreCase))
            {
                var registered = _runtime.Providers.FirstOrDefault(item =>
                    string.Equals(item.Name, virtualModel.Provider, StringComparison.OrdinalIgnoreCase));
                if (registered != null)
                {
                    targetProvider = new ProviderConfig
                    {
                        Id = registered.Name,
                        DisplayName = registered.Name,
                        Protocol = registered.Protocol,
                        BaseUrl = registered.BaseAddress?.ToString() ?? provider.BaseUrl,
                        ApiKey = registered.ApiKey ?? provider.ApiKey,
                        Models = new List<ModelConfig> { new() { Id = virtualModel.TargetModel, Enabled = true } }
                    };
                }
            }

            var client = CreateClientFor(targetProvider, observer);
            return new VirtualModelChatClient(client, virtualModel);
        }

        var registeredProvider = _runtime.Providers.FirstOrDefault(item =>
            string.Equals(item.Name, provider.Id, StringComparison.OrdinalIgnoreCase));
        if (registeredProvider != null)
        {
            var config = new ProviderConfig
            {
                Id = registeredProvider.Name,
                DisplayName = registeredProvider.Name,
                Protocol = registeredProvider.Protocol,
                BaseUrl = registeredProvider.BaseAddress?.ToString() ?? provider.BaseUrl,
                ApiKey = registeredProvider.ApiKey ?? provider.ApiKey,
                Models = new List<ModelConfig>
                {
                    new()
                    {
                        Id = registeredProvider.DefaultModel ?? provider.Models.FirstOrDefault()?.Id ?? string.Empty,
                        Enabled = true
                    }
                }
            };
            return CreateClientFor(config, observer);
        }

        return CreateClientFor(provider, observer);
    }

    private IChatClient CreateClientFor(ProviderConfig provider, IAiHttpObserver? observer)
    {
        // 宿主自定义的工厂永远优先：它可能代理、记账或替换实现，
        // 观察器只在工厂是内置实现时才由这里注入。
        if (_fallback != null)
        {
            return _fallback.CreateChatClient(provider);
        }

        return observer == null
            ? _inner.CreateChatClient(provider)
            : BuiltInProviderFactoryWithObserver.Create(provider, observer);
    }
}

/// <summary>可注入 HTTP 观察器的客户端构造器。</summary>
internal static class BuiltInProviderFactoryWithObserver
{
    public static IChatClient Create(ProviderConfig provider, IAiHttpObserver observer)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new AiToolsWpfException($"服务商 {provider.DisplayName} 未配置 Base URL。");
        }

        var baseAddress = new Uri(provider.BaseUrl, UriKind.Absolute);
        switch (provider.Protocol)
        {
            case ProviderProtocols.Anthropic:
                return new AiTools.Anthropic.AnthropicChatClient(new AiTools.Anthropic.AnthropicOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = baseAddress,
                    Observer = observer
                });

            case ProviderProtocols.Gemini:
                return new AiTools.Gemini.GeminiChatClient(new AiTools.Gemini.GeminiOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = baseAddress,
                    Observer = observer
                });

            default:
                return new AiTools.OpenAI.OpenAiChatClient(new AiTools.OpenAI.OpenAiOptions
                {
                    ApiKey = provider.ApiKey,
                    BaseAddress = baseAddress,
                    ProviderName = string.IsNullOrWhiteSpace(provider.Id) ? "openai" : provider.Id,
                    Observer = observer
                });
        }
    }
}

/// <summary>把对虚拟模型的请求改写成真实模型，并让响应看起来仍来自虚拟模型。</summary>
internal sealed class VirtualModelChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly VirtualModelRegistration _registration;

    public VirtualModelChatClient(IChatClient inner, VirtualModelRegistration registration)
    {
        _inner = inner;
        _registration = registration;
    }

    public string ProviderName => _inner.ProviderName;

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => _inner.CompleteAsync(Rewrite(request), cancellationToken);

    public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _inner.StreamAsync(Rewrite(request), cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private ChatRequest Rewrite(ChatRequest request)
    {
        var rewritten = new ChatRequest
        {
            Model = _registration.TargetModel,
            SystemPrompt = request.SystemPrompt,
            Messages = request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = request.Temperature,
            TopP = request.TopP,
            TopK = request.TopK,
            MaxOutputTokens = request.MaxOutputTokens,
            StopSequences = request.StopSequences,
            ResponseFormat = request.ResponseFormat,
            AdditionalParameters = request.AdditionalParameters
        };

        return _registration.Rewrite?.Invoke(rewritten) ?? rewritten;
    }
}
