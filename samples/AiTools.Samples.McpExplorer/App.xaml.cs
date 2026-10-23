using System;
using System.Windows;
using System.Windows.Threading;
using AiTools.Samples.McpDiscovery;
using AiTools.Samples.McpExplorer.ViewModels;

namespace AiTools.Samples.McpExplorer;

/// <summary>WPF 应用程序入口点。</summary>
public partial class App : Application
{
    /// <summary>获取命令行提供的注册表路径（如果有）。</summary>
    public string? RegistryPath { get; private set; }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegistryPath = ParseRegistryPath(e.Args);
        if (!string.IsNullOrWhiteSpace(RegistryPath))
        {
            McpRegistry.OverridePath(RegistryPath);
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        var window = new MainWindow(new MainViewModel(RegistryPath ?? McpRegistry.RegistryPath));
        MainWindow = window;
        window.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 绝不能让未预期的异常在无提示的情况下关闭窗口。
        if (MainWindow is MainWindow window && window.DataContext is MainViewModel viewModel)
        {
            viewModel.ReportUnexpectedError(e.Exception);
        }

        e.Handled = true;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("Unhandled exception: " + e.ExceptionObject);
    }

    private static string? ParseRegistryPath(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--registry", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    return args[index + 1];
                }

                return null;
            }

            if (argument.StartsWith("--registry=", StringComparison.OrdinalIgnoreCase))
            {
                var value = argument["--registry=".Length..];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}
