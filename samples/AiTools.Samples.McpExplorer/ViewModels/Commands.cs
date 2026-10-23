using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AiTools.Samples.McpExplorer.ViewModels;

/// <summary>用于同步操作的基于委托的 <see cref="ICommand"/>。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>创建命令。</summary>
    /// <param name="execute">要执行的操作。</param>
    /// <param name="canExecute">控制可用性的可选谓词。</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute();

    /// <summary>通知 WPF 可用性可能已发生变化。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>用于异步操作的基于委托的 <see cref="ICommand"/>。</summary>
/// <remarks>异常会被捕获并记录，因此未处理的命令绝不会终止应用程序。</remarks>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    /// <summary>创建异步命令。</summary>
    /// <param name="execute">要执行的异步操作。</param>
    /// <param name="canExecute">控制可用性的可选谓词。</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // ViewModel 操作会自行处理错误；这里是最后一道安全网。
            Debug.WriteLine("AsyncRelayCommand failed: " + ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>通知 WPF 可用性可能已发生变化。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
