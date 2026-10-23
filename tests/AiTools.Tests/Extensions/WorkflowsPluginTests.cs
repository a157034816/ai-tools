using AiTools.Extensions;
using Xunit;

namespace AiTools.Tests.Extensions;

/// <summary>验证仓库内的 plugins/workflows 插件确实提供 /goal 与 /plan。</summary>
public sealed class WorkflowsPluginTests
{
    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AiTools.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }

    private static ExtensionHost CreateHost()
    {
        var pluginsRoot = Path.Combine(RepositoryRoot, "plugins");
        var catalog = ExtensionCatalog.LoadFromDirectory(pluginsRoot);
        return new ExtensionHost(catalog, new ExtensionHostOptions
        {
            EnableShellExecution = false,
            ProjectDirectory = RepositoryRoot
        });
    }

    [Fact]
    public void Catalog_LoadsGoalAndPlan()
    {
        var host = CreateHost();

        var plugin = Assert.Single(host.Catalog.Plugins);
        Assert.Equal("workflows", plugin.Name);
        Assert.Equal(2, plugin.Commands.Count);
        Assert.Contains(plugin.Commands, item => item.ShortName == "goal");
        Assert.Contains(plugin.Commands, item => item.ShortName == "plan");
    }

    [Fact]
    public void Goal_ExpandsArgumentsIntoPrompt()
    {
        var host = CreateHost();

        Assert.True(host.TryResolve("/goal 实现插件框架", out var command, out var arguments));
        var invocation = host.Render(command!, arguments);

        Assert.Contains("实现插件框架", invocation.Text);
        Assert.Contains("Success criteria", invocation.Text);
        Assert.DoesNotContain("$ARGUMENTS", invocation.Text);
        Assert.DoesNotContain("---", invocation.Text.Split('\n')[0]);
    }

    [Fact]
    public void Goal_WithNoArguments_KeepsPlaceholderFreeAndAvoidsInventingGoal()
    {
        var host = CreateHost();

        Assert.True(host.TryResolve("/goal", out var command, out var arguments));
        var invocation = host.Render(command!, arguments);

        Assert.Contains("这次会话的目标是什么", invocation.Text);
        Assert.DoesNotContain("$ARGUMENTS", invocation.Text);
        Assert.DoesNotContain("ARGUMENTS:", invocation.Text);
    }

    [Fact]
    public void Plan_ExpandsArgumentsAndKeepsEvidenceRequirement()
    {
        var host = CreateHost();

        Assert.True(host.TryResolve("/plan 重构会话层", out var command, out var arguments));
        var invocation = host.Render(command!, arguments);

        Assert.Contains("重构会话层", invocation.Text);
        Assert.Contains("Verify:", invocation.Text);
        Assert.Contains("Out of scope", invocation.Text);
        Assert.DoesNotContain("$ARGUMENTS", invocation.Text);
    }

    [Fact]
    public void Commands_AreReachableByQualifiedName()
    {
        var host = CreateHost();

        Assert.True(host.TryResolve("/workflows:goal 目标", out var command, out _));
        Assert.Equal("workflows:goal", command!.Name);
        Assert.Equal("workflows", command.PluginName);
    }

    [Fact]
    public void Manifest_DoesNotDeclareUnsupportedComponents()
    {
        var host = CreateHost();
        var plugin = host.Catalog.TryGet("workflows")!;

        Assert.DoesNotContain(plugin.Diagnostics, item => item.Kind == ExtensionDiagnosticKind.Warning);
        Assert.DoesNotContain(plugin.Diagnostics, item => item.Kind == ExtensionDiagnosticKind.Error);
    }
}
