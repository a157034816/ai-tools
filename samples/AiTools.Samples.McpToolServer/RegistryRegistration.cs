using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using AiTools.Samples.McpDiscovery;

namespace AiTools.Samples.McpToolServer;

/// <summary>
/// 在服务器运行期间将其写入共享的本地 MCP 注册表，并在关闭时移除相应条目。
/// stdio 服务器由客户端启动，因此从不注册。
/// </summary>
internal sealed class RegistryRegistration : IDisposable
{
    private readonly string? _entryId;
    private int _disposed;

    private RegistryRegistration(string? entryId)
    {
        _entryId = entryId;
    }

    /// <summary>获取服务器是否已成功写入注册表。</summary>
    internal bool IsRegistered => !string.IsNullOrEmpty(_entryId);

    /// <summary>将 HTTP 服务器注册到本地注册表。</summary>
    /// <param name="serverName">服务器名称。</param>
    /// <param name="endpoint">MCP 端点 URL。</param>
    /// <param name="log">控制台日志记录器。</param>
    /// <returns>释放时移除该条目的句柄。注册失败只会记录日志，不会导致致命错误。</returns>
    internal static RegistryRegistration Register(string serverName, string endpoint, ConsoleLog log)
    {
        string? entryId = null;
        try
        {
            var launchCommand = Environment.ProcessPath;
            var launchArguments = new List<string>();

            var isDotnetHost = string.IsNullOrEmpty(launchCommand)
                               || string.Equals(
                                   Path.GetFileNameWithoutExtension(launchCommand),
                                   "dotnet",
                                   StringComparison.OrdinalIgnoreCase);

            if (isDotnetHost)
            {
                // dotnet run / dotnet <dll>：用宿主进程加程序集路径重新以 stdio 方式启动。
                launchCommand ??= "dotnet";
                var assemblyPath = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(assemblyPath))
                {
                    launchArguments.Add(assemblyPath!);
                }

                launchArguments.Add("--stdio");
            }
            else
            {
                // apphost（AiTools.Samples.McpToolServer.exe）可直接用 --stdio 重新启动。
                launchArguments.Add("--stdio");
            }

            var entry = new McpRegistryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = serverName,
                Version = ServerMetadata.Version,
                Transport = McpTransportKinds.StreamableHttp,
                Endpoint = endpoint,
                Pid = Environment.ProcessId,
                ProcessStartedAt = GetProcessStartTimeUtc(),
                RegisteredAt = DateTime.UtcNow,
                LaunchCommand = launchCommand,
                LaunchArguments = launchArguments,
                WorkingDirectory = AppContext.BaseDirectory,
                Tools = ToolCatalog.ToolNames.ToList()
            };

            McpRegistry.Upsert(entry);
            entryId = entry.Id;
            log.Info("已注册到本地 MCP 注册表（" + McpRegistry.RegistryPath + "）。");

            var registration = new RegistryRegistration(entry.Id);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => registration.Dispose();
            return registration;
        }
        catch (Exception ex)
        {
            log.Warn("写入本地 MCP 注册表失败（不影响服务器运行）：" + ex.Message);
            return new RegistryRegistration(null);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (string.IsNullOrEmpty(_entryId))
        {
            return;
        }

        try
        {
            McpRegistry.Remove(_entryId!);
        }
        catch (Exception)
        {
            // 退出清理是尽力而为，不能阻止进程结束。
        }
    }

    private static DateTime GetProcessStartTimeUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            return DateTime.UtcNow;
        }
    }
}
