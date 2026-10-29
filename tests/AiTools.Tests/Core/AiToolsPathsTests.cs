using System;
using System.IO;
using AiTools.Core;
using Xunit;

namespace AiTools.Tests.Core;

/// <summary>
/// 验证用户级数据根目录布局：默认落在 <c>~/.ai-tools</c>，
/// 可用 <c>AITOOLS_HOME</c> 覆盖，且各子路径都由同一根派生。
/// </summary>
[Collection(AiToolsPathsCollection.Name)]
public sealed class AiToolsPathsTests : IDisposable
{
    private readonly string? _originalHome;

    public AiToolsPathsTests()
    {
        _originalHome = Environment.GetEnvironmentVariable(AiToolsPaths.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(AiToolsPaths.HomeEnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AiToolsPaths.HomeEnvironmentVariable, _originalHome);
    }

    [Fact]
    public void DefaultRoot_IsDotAiToolsUnderUserProfile()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ai-tools");

        Assert.Equal(expected, AiToolsPaths.GetDefaultRootDirectory());
        Assert.Equal(expected, AiToolsPaths.GetRootDirectory());
    }

    [Fact]
    public void HomeEnvironmentVariable_OverridesRoot()
    {
        var custom = Path.Combine(Path.GetTempPath(), "aitools-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(AiToolsPaths.HomeEnvironmentVariable, custom);

        Assert.Equal(Path.GetFullPath(custom), AiToolsPaths.GetRootDirectory());
    }

    [Fact]
    public void BlankHomeEnvironmentVariable_FallsBackToDefault()
    {
        Environment.SetEnvironmentVariable(AiToolsPaths.HomeEnvironmentVariable, "   ");

        Assert.Equal(AiToolsPaths.GetDefaultRootDirectory(), AiToolsPaths.GetRootDirectory());
    }

    [Fact]
    public void DerivedPaths_AllHangOffTheSameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aitools-home-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(AiToolsPaths.HomeEnvironmentVariable, root);

        Assert.Equal(Path.Combine(root, "settings.json"), AiToolsPaths.GetSettingsFile());
        Assert.Equal(root, AiToolsPaths.GetConversationsRoot());
        Assert.Equal(Path.Combine(root, "extensions"), AiToolsPaths.GetExtensionsDirectory());
        Assert.Equal(Path.Combine(root, "extensions", "data"), AiToolsPaths.GetExtensionDataDirectory());
        Assert.Equal(Path.Combine(root, "skills"), AiToolsPaths.GetSkillsDirectory());
        Assert.Equal(Path.Combine(root, "AGENTS.md"), AiToolsPaths.GetInstructionsFile());
    }

    [Fact]
    public void DefaultPaths_StayInsideTheUserRoot()
    {
        // 这些默认路径曾经散落在 %LOCALAPPDATA%，现在必须全部收敛到数据根下。
        var root = Path.GetFullPath(AiToolsPaths.GetRootDirectory());
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var path in new[]
                 {
                     AiToolsPaths.GetSettingsFile(),
                     AiToolsPaths.GetConversationsRoot(),
                     AiToolsPaths.GetExtensionsDirectory(),
                     AiToolsPaths.GetSkillsDirectory()
                 })
        {
            Assert.StartsWith(root, Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(localAppData))
            {
                Assert.DoesNotContain(localAppData, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

/// <summary>串行执行：这些测试会改写进程级环境变量。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AiToolsPathsCollection
{
    public const string Name = "AiToolsPaths";
}
