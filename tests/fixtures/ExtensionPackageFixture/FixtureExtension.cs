using System.Threading;
using System.Threading.Tasks;
using AiTools.Extensions;

namespace FixtureExtension;

public sealed class FixtureExtension : IExtension
{
    public string Name => "fixture-extension-command";
    public string? Description => "End-to-end package fixture.";

    public void Activate(IExtensionApi api)
    {
        api.RegisterCommand("fixture-say", "Return the fixture response", (_, _, _) =>
            Task.FromResult(CommandResult.Display("fixture response v1")));
        api.RegisterTool(AiTools.Abstractions.ToolDefinition.Create("fixture_tool"), (_, _, _) =>
            Task.FromResult("fixture tool"));
    }
}
