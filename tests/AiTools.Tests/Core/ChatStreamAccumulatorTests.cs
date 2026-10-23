using AiTools.Abstractions;
using AiTools.Core.Chat;
using Xunit;

namespace AiTools.Tests.Core;

public class ChatStreamAccumulatorTests
{
    [Fact]
    public void Build_CombinesTextDeltas()
    {
        var accumulator = new ChatStreamAccumulator();
        accumulator.Add(new ChatStreamUpdate { TextDelta = "Hello, " });
        accumulator.Add(new ChatStreamUpdate { TextDelta = "world!" });

        var response = accumulator.Build();

        Assert.Equal("Hello, world!", response.GetText());
        Assert.Equal("Hello, world!", accumulator.Text);
        Assert.Equal(ChatFinishReason.None, response.FinishReason);
    }

    [Fact]
    public void Build_CombinesToolCallFragmentsByIndex()
    {
        var accumulator = new ChatStreamAccumulator();
        accumulator.Add(new ChatStreamUpdate
        {
            ToolCallDelta = new ToolCallDelta { Index = 0, Id = "call_1", Name = "get_weather" }
        });
        accumulator.Add(new ChatStreamUpdate
        {
            ToolCallDelta = new ToolCallDelta { Index = 0, ArgumentsDelta = "{\"city\":" }
        });
        accumulator.Add(new ChatStreamUpdate
        {
            ToolCallDelta = new ToolCallDelta { Index = 0, ArgumentsDelta = "\"Paris\"}" }
        });
        accumulator.Add(new ChatStreamUpdate { FinishReason = ChatFinishReason.ToolCalls });

        var response = accumulator.Build();

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("{\"city\":\"Paris\"}", call.ArgumentsJson);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
    }

    [Fact]
    public void Build_DefaultsArgumentsToEmptyObject()
    {
        var accumulator = new ChatStreamAccumulator();
        accumulator.Add(new ChatStreamUpdate
        {
            ToolCallDelta = new ToolCallDelta { Index = 2, Id = "call_x", Name = "no_args" }
        });

        var call = Assert.Single(accumulator.Build().ToolCalls);
        Assert.Equal("{}", call.ArgumentsJson);
    }

    [Fact]
    public void Build_CapturesUsageAndMetadata()
    {
        var accumulator = new ChatStreamAccumulator();
        accumulator.Add(new ChatStreamUpdate
        {
            ResponseId = "resp_1",
            Model = "model-x",
            Usage = new Usage { InputTokens = 10, OutputTokens = 5, TotalTokens = 15 },
            FinishReason = ChatFinishReason.Stop
        });

        var response = accumulator.Build();

        Assert.Equal("resp_1", response.Id);
        Assert.Equal("model-x", response.Model);
        Assert.Equal(15, response.Usage!.TotalTokens);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }
}
