using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AiTools.Samples.McpAgentManager.ViewModels;

/// <summary>轻量级 Command，将执行和可用性检查委托给回调。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>创建接收 Command 参数的 Command。</summary>
    /// <param name="execute">执行回调。</param>
    /// <param name="canExecute">可选的可用性回调。</param>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>触发 <see cref="CanExecuteChanged"/>，使绑定的按钮刷新状态。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 一种 <see cref="ICommand"/>，会等待异步回调、防止重入，并通过可选的错误回调报告失败，
/// 而不是留下未处理的 <c>async void</c> 异常。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Action<Exception>? _onError;
    private bool _isRunning;

    /// <summary>创建不带参数的 Command。</summary>
    /// <param name="execute">异步执行回调。</param>
    /// <param name="onError">可选的意外异常回调。</param>
    public AsyncRelayCommand(Func<Task> execute, Action<Exception>? onError = null)
        : this(_ => execute(), onError)
    {
    }

    /// <summary>创建接收 Command 参数的 Command。</summary>
    /// <param name="execute">异步执行回调。</param>
    /// <param name="onError">可选的意外异常回调。</param>
    public AsyncRelayCommand(Func<object?, Task> execute, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _onError = onError;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>获取 Command 当前是否正在等待其回调完成。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>触发 <see cref="CanExecuteChanged"/>，使绑定的按钮刷新状态。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_isRunning;

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }
}
