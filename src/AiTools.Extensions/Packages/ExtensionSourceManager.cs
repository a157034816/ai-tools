using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AiTools.Extensions.Packages;

/// <summary>扩展源码仓库条目。</summary>
public sealed class ExtensionSourceInfo
{
    /// <summary>获取扩展包名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>获取仓库目录。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>获取目录是否含有自己的 .git 元数据。</summary>
    public bool IsGitRepository { get; init; }

    /// <summary>获取最近提交主题。</summary>
    public string? LastCommitSubject { get; init; }

    /// <summary>获取检查仓库时的诊断。</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; init; } = Array.Empty<PluginDiagnostic>();
}

/// <summary>扩展源码操作结果。</summary>
public sealed class ExtensionSourceResult
{
    /// <summary>获取操作是否成功。</summary>
    public bool Succeeded { get; init; }

    /// <summary>获取操作涉及的目录。</summary>
    public string? Path { get; init; }

    /// <summary>获取操作诊断。</summary>
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; init; } = Array.Empty<PluginDiagnostic>();
}

/// <summary>管理以独立 Git 仓库存放的扩展源码包。</summary>
public sealed class ExtensionSourceManager
{
    private static readonly Regex NamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _rootDirectory;
    private readonly TimeSpan _timeout;

    /// <summary>创建源码管理器。</summary>
    /// <param name="rootDirectory">安装扩展仓库的工作区根目录。</param>
    /// <param name="timeout">Git 命令超时；null 时使用 120 秒。</param>
    public ExtensionSourceManager(string rootDirectory, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("A workspace root is required.", nameof(rootDirectory));
        _rootDirectory = System.IO.Path.GetFullPath(rootDirectory);
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>克隆一个扩展仓库到工作区。</summary>
    /// <param name="gitUrl">Git 仓库 URL。</param>
    /// <param name="name">可选目录名；未提供时从 URL 推导。</param>
    /// <param name="reference">可选分支、标签或提交引用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>克隆结果。</returns>
    public async Task<ExtensionSourceResult> InstallAsync(string gitUrl, string? name = null, string? reference = null, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<PluginDiagnostic>();
        if (string.IsNullOrWhiteSpace(gitUrl))
        {
            diagnostics.Add(Diagnostic("A Git URL is required.", _rootDirectory));
            return Result(false, null, diagnostics);
        }
        var packageName = string.IsNullOrWhiteSpace(name) ? InferName(gitUrl!) : name!.Trim();
        if (!IsValidName(packageName))
        {
            diagnostics.Add(Diagnostic("Extension directory name must be kebab-case and at most 64 characters.", packageName));
            return Result(false, null, diagnostics);
        }
        var directory = System.IO.Path.Combine(_rootDirectory, packageName);
        try
        {
            if (Directory.Exists(directory) || File.Exists(directory))
            {
                diagnostics.Add(Diagnostic("Install target already exists; refusing to overwrite it.", directory, packageName));
                return Result(false, directory, diagnostics);
            }
            Directory.CreateDirectory(_rootDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            await RunGitAsync("git clone --depth 1 " + Quote(gitUrl) + " " + Quote(directory), _rootDirectory, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reference))
            {
                await RunGitAsync("git -C " + Quote(directory) + " checkout " + Quote(reference!), directory, cancellationToken).ConfigureAwait(false);
            }
            return Result(true, directory, diagnostics);
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(Diagnostic("Git install was cancelled.", directory, packageName, PluginDiagnosticKind.Warning));
            return Result(false, directory, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic("Git clone failed: " + ex.Message, directory, packageName));
            return Result(false, directory, diagnostics);
        }
    }

    /// <summary>对指定的扩展仓库执行仅快进更新。</summary>
    /// <param name="name">扩展仓库目录名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新结果。</returns>
    public async Task<ExtensionSourceResult> UpdateAsync(string name, CancellationToken cancellationToken = default)
    {
        var directory = ResolveDirectory(name);
        var diagnostics = new List<PluginDiagnostic>();
        if (directory == null)
        {
            diagnostics.Add(Diagnostic("A valid extension name is required.", _rootDirectory, name));
            return Result(false, null, diagnostics);
        }
        if (!IsGitWorkingTree(directory, diagnostics, name)) return Result(false, directory, diagnostics);
        try
        {
            await RunGitAsync("git -C " + Quote(directory) + " pull --ff-only", directory, cancellationToken).ConfigureAwait(false);
            return Result(true, directory, diagnostics);
        }
        catch (OperationCanceledException)
        {
            diagnostics.Add(Diagnostic("Git update was cancelled.", directory, name, PluginDiagnosticKind.Warning));
            return Result(false, directory, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic("Git pull failed: " + ex.Message, directory, name));
            return Result(false, directory, diagnostics);
        }
    }

    /// <summary>删除指定扩展仓库；非 Git 工作树会被拒绝删除。</summary>
    /// <param name="name">扩展仓库目录名。</param>
    /// <returns>删除结果。</returns>
    public Task<ExtensionSourceResult> RemoveAsync(string name)
    {
        var directory = ResolveDirectory(name);
        var diagnostics = new List<PluginDiagnostic>();
        if (directory == null)
        {
            diagnostics.Add(Diagnostic("A valid extension name is required.", _rootDirectory, name));
            return Task.FromResult(Result(false, null, diagnostics));
        }
        if (!Directory.Exists(directory))
        {
            diagnostics.Add(Diagnostic("Extension directory was not found.", directory, name));
            return Task.FromResult(Result(false, directory, diagnostics));
        }
        if (!IsGitWorkingTree(directory, diagnostics, name)) return Task.FromResult(Result(false, directory, diagnostics));
        try
        {
            Directory.Delete(directory, recursive: true);
            return Task.FromResult(Result(true, directory, diagnostics));
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic("Failed to remove extension repository: " + ex.Message, directory, name));
            return Task.FromResult(Result(false, directory, diagnostics));
        }
    }

    /// <summary>列出工作区直接子目录及其 Git 状态和最近提交主题。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>仓库条目及各项检查诊断。</returns>
    public async Task<IReadOnlyList<ExtensionSourceInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ExtensionSourceInfo>();
        if (!Directory.Exists(_rootDirectory)) return results;
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(_rootDirectory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            return new[]
            {
                new ExtensionSourceInfo
                {
                    Path = _rootDirectory,
                    Diagnostics = new[] { Diagnostic("Failed to list extension sources: " + ex.Message, _rootDirectory) }
                }
            };
        }
        foreach (var directory in directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(directory);
            var diagnostics = new List<PluginDiagnostic>();
            var hasGit = Directory.Exists(System.IO.Path.Combine(directory, ".git")) || File.Exists(System.IO.Path.Combine(directory, ".git"));
            string? subject = null;
            if (hasGit)
            {
                try
                {
                    await RunGitAsync("git -C " + Quote(directory) + " rev-parse --is-inside-work-tree", directory, cancellationToken).ConfigureAwait(false);
                    subject = (await RunGitAsync("git -C " + Quote(directory) + " log -1 --pretty=%s", directory, cancellationToken).ConfigureAwait(false)).Trim();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    diagnostics.Add(Diagnostic("Git repository inspection failed: " + ex.Message, directory, name));
                }
            }
            results.Add(new ExtensionSourceInfo { Name = name, Path = directory, IsGitRepository = hasGit, LastCommitSubject = subject, Diagnostics = diagnostics });
        }
        return results;
    }

    private string? ResolveDirectory(string name)
    {
        if (!IsValidName(name)) return null;
        return System.IO.Path.Combine(_rootDirectory, name);
    }

    private bool IsGitWorkingTree(string directory, ICollection<PluginDiagnostic> diagnostics, string name)
    {
        if (!Directory.Exists(directory))
        {
            diagnostics.Add(Diagnostic("Refusing to operate on a directory that is not its own Git working tree.", directory, name));
            return false;
        }
        try
        {
            RunGitAsync("git -C " + Quote(directory) + " rev-parse --is-inside-work-tree", directory, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic("Refusing to operate on a directory that is not its own Git working tree. Git stderr: " + ex.Message, directory, name));
            return false;
        }
        var gitMetadata = System.IO.Path.Combine(directory, ".git");
        if (!Directory.Exists(gitMetadata) && !File.Exists(gitMetadata))
        {
            diagnostics.Add(Diagnostic("Refusing to operate on a directory that is inside another Git working tree, not its own repository.", directory, name));
            return false;
        }
        return true;
    }

    private async Task<string> RunGitAsync(string command, string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = await Task.Run(() => ShellCommandRunner.Run(command, directory, _timeout), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private static string InferName(string gitUrl)
    {
        var value = gitUrl.TrimEnd('/', '\\');
        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        value = value.Substring(slash + 1);
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4);
        var chars = value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        return string.Join("-", new string(chars).Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name!.Length <= 64 && NamePattern.IsMatch(name);

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static PluginDiagnostic Diagnostic(string message, string? path, string? name = null, PluginDiagnosticKind kind = PluginDiagnosticKind.Error)
        => new PluginDiagnostic { Kind = kind, PluginName = name, Path = path, Message = message };

    private static ExtensionSourceResult Result(bool succeeded, string? path, IReadOnlyList<PluginDiagnostic> diagnostics)
        => new ExtensionSourceResult { Succeeded = succeeded, Path = path, Diagnostics = diagnostics };
}
