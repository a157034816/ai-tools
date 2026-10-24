using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Extensions.Packages;

namespace AiTools.Extensions.BuiltIn;

/// <summary><c>/extension</c>：查看和管理磁盘扩展包及其 Git 源码。</summary>
public sealed class ExtensionManagerExtension : IExtension
{
    private readonly ExtensionWorkspace? _workspace;

    /// <summary>创建扩展管理命令。</summary>
    /// <param name="workspace">扩展工作区；null 时仅显示未配置状态。</param>
    public ExtensionManagerExtension(ExtensionWorkspace? workspace)
    {
        _workspace = workspace;
    }

    /// <inheritdoc />
    public string Name => "extension-manager";

    /// <inheritdoc />
    public string? Description => "Lists, installs, updates, removes, builds and reloads extension packages.";

    /// <inheritdoc />
    public void Activate(IExtensionApi api)
    {
        if (api == null) throw new ArgumentNullException(nameof(api));
        api.RegisterCommand("extension", "管理磁盘扩展包", HandleAsync, "list|install|update|remove|reload|build|scan");
    }

    private Task<CommandResult> HandleAsync(CommandContext context, CommandArguments arguments, CancellationToken cancellationToken)
        => ExecuteAsync(_workspace, context, arguments, cancellationToken);

    private static async Task<CommandResult> ExecuteAsync(ExtensionWorkspace? workspace, CommandContext context, CommandArguments arguments, CancellationToken cancellationToken)
    {
        if (workspace == null)
        {
            return CommandResult.Display("no workspace");
        }

        var command = arguments.Get(0) ?? "list";
        switch (command.ToLowerInvariant())
        {
            case "list":
                return CommandResult.Display(FormatPackages(workspace));
            case "scan":
            {
                await workspace.ScanAsync(cancellationToken).ConfigureAwait(false);
                return CommandResult.Display(FormatPackages(workspace));
            }
            case "install":
            {
                var gitUrl = arguments.Get(1);
                if (string.IsNullOrWhiteSpace(gitUrl)) return CommandResult.Display("Usage: /extension install <git-url> [name]");
                var name = arguments.Get(2);
                var result = await workspace.Sources.InstallAsync(gitUrl!, name, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.Succeeded) await workspace.ScanAsync(cancellationToken).ConfigureAwait(false);
                return CommandResult.Display(FormatResult(result.Diagnostics, result.Succeeded ? "Installed " + result.Path : "Install failed"));
            }
            case "update":
            {
                if (!await ConfirmDestructiveAsync(context, arguments.Positional.Contains("--yes", StringComparer.OrdinalIgnoreCase), "Update extension packages", cancellationToken).ConfigureAwait(false))
                    return CommandResult.Display(context.Ui?.HasDialogs == true ? "Update cancelled." : "Update refused: interactive confirmation is unavailable; pass --yes to confirm.");
                var target = arguments.Get(1);
                if (string.Equals(target, "--all", StringComparison.OrdinalIgnoreCase))
                {
                    var sources = await workspace.Sources.ListAsync(cancellationToken).ConfigureAwait(false);
                    var lines = new List<string>();
                    foreach (var source in sources.Where(item => item.IsGitRepository))
                    {
                        var update = await workspace.Sources.UpdateAsync(source.Name!, cancellationToken).ConfigureAwait(false);
                        lines.AddRange(update.Diagnostics.Select(item => item.ToString()));
                        if (update.Succeeded) lines.Add("Updated " + source.Name);
                    }
                    await workspace.ScanAsync(cancellationToken).ConfigureAwait(false);
                    return CommandResult.Display(lines.Count == 0 ? "No Git-backed extensions to update." : string.Join(Environment.NewLine, lines));
                }
                if (string.IsNullOrWhiteSpace(target)) return CommandResult.Display("Usage: /extension update <name>|--all");
                var result = await workspace.Sources.UpdateAsync(target!, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded) await workspace.ReloadPackageAsync(target!, cancellationToken).ConfigureAwait(false);
                return CommandResult.Display(FormatResult(result.Diagnostics, result.Succeeded ? "Updated " + target : "Update failed"));
            }
            case "remove":
            {
                var target = arguments.Get(1);
                if (string.IsNullOrWhiteSpace(target)) return CommandResult.Display("Usage: /extension remove <name>");
                if (!await ConfirmDestructiveAsync(context, arguments.Positional.Contains("--yes", StringComparer.OrdinalIgnoreCase), "Remove extension " + target, cancellationToken).ConfigureAwait(false))
                    return CommandResult.Display(context.Ui?.HasDialogs == true ? "Remove cancelled." : "Remove refused: interactive confirmation is unavailable; pass --yes to confirm.");
                var result = await workspace.Sources.RemoveAsync(target!).ConfigureAwait(false);
                if (result.Succeeded) await workspace.ScanAsync(cancellationToken).ConfigureAwait(false);
                return CommandResult.Display(FormatResult(result.Diagnostics, result.Succeeded ? "Removed " + target : "Remove failed"));
            }
            case "reload":
            {
                var target = arguments.Get(1);
                if (string.Equals(target, "--all", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(target))
                {
                    await workspace.ReloadAllAsync(cancellationToken).ConfigureAwait(false);
                    return CommandResult.Display(FormatPackages(workspace));
                }
                var package = await workspace.ReloadPackageAsync(target!, cancellationToken).ConfigureAwait(false);
                return CommandResult.Display(package == null ? "Unknown extension package: " + target : FormatPackage(package));
            }
            case "build":
            {
                var target = arguments.Get(1);
                if (string.IsNullOrWhiteSpace(target)) return CommandResult.Display("Usage: /extension build <name>");
                var package = await workspace.BuildPackageAsync(target!, cancellationToken).ConfigureAwait(false);
                return CommandResult.Display(package == null ? "Unknown extension package: " + target : FormatPackage(package));
            }
            default:
                return CommandResult.Display("Usage: /extension list|install <git-url> [name]|update <name>|--all|remove <name>|reload [name]|--all|build <name>|scan");
        }
    }

    private static async Task<bool> ConfirmDestructiveAsync(CommandContext context, bool explicitlyConfirmed, string title, CancellationToken cancellationToken)
    {
        if (explicitlyConfirmed) return true;
        if (context.Ui?.HasDialogs != true) return false;
        return await context.Ui.ConfirmAsync(title, "This operation changes files in an extension Git repository. Continue?", false, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatPackages(ExtensionWorkspace workspace)
    {
        if (workspace.Packages.Count == 0)
        {
            var diagnostics = workspace.Diagnostics.Count;
            return diagnostics == 0 ? "No extension packages found." : "No extension packages found (diagnostics: " + diagnostics + ").";
        }
        return string.Join(Environment.NewLine, workspace.Packages.Select(FormatPackage));
    }

    private static string FormatPackage(ExtensionPackage package)
        => package.Manifest.Name + " | " + package.Manifest.Kind + " | " + (package.Manifest.Version ?? "unknown") + " | " + (package.IsLoaded ? "loaded" : "not loaded") + " | diagnostics: " + package.Diagnostics.Count;

    private static string FormatResult(IReadOnlyList<PluginDiagnostic> diagnostics, string summary)
        => diagnostics.Count == 0 ? summary : summary + Environment.NewLine + string.Join(Environment.NewLine, diagnostics.Select(item => item.ToString()));
}
