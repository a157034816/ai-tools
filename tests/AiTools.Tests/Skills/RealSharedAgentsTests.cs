using System.Linq;
using AiTools.Skills;
using Xunit;

namespace AiTools.Tests.Skills;

/// <summary>
/// 用开发者本机真实的 <c>~/.agents</c> 做一次只读冒烟检查。
/// 目录不存在时跳过断言，避免在 CI 上失败。
/// </summary>
public sealed class RealSharedAgentsTests
{
    [Fact]
    public void Discover_AgainstTheRealUserDirectory()
    {
        var userRoot = SharedAgents.GetUserDirectory();
        if (!Directory.Exists(userRoot))
        {
            return;
        }

        var catalog = SharedAgents.Discover(null, includeUser: true);

        Assert.True(catalog.Skills.Count > 0, "真实 ~/.agents/skills 应能发现技能");
        Assert.All(catalog.Skills.Skills, skill => Assert.False(string.IsNullOrWhiteSpace(skill.Name)));
        Assert.All(catalog.Skills.Skills, skill => Assert.False(string.IsNullOrWhiteSpace(skill.Description)));
        Assert.All(catalog.Skills.Skills, skill => Directory.Exists(skill.DirectoryPath));

        // 每个技能都必须能读出正文，否则 load_skill 会拿到空内容。
        Assert.All(catalog.Skills.Skills, skill => Assert.False(string.IsNullOrWhiteSpace(skill.Instructions)));
    }
}
