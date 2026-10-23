using System.IO;
using System.Text;
using System.Text.Json;
using AiTools.Core.Sse;
using Xunit;

namespace AiTools.Tests.Core;

public class SseParserTests
{
    private static Stream ToStream(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static async Task<List<SseEvent>> ParseAllAsync(string content)
    {
        var events = new List<SseEvent>();
        using var stream = ToStream(content);
        await foreach (var evt in SseParser.ParseAsync(stream))
        {
            events.Add(evt);
        }

        return events;
    }

    [Fact]
    public async Task ParseAsync_ReadsDataEvents()
    {
        var events = await ParseAllAsync("data: {\"a\":1}\n\ndata: {\"b\":2}\n\n");

        Assert.Equal(2, events.Count);
        Assert.Equal("{\"a\":1}", events[0].Data);
        Assert.Equal("{\"b\":2}", events[1].Data);
    }

    [Fact]
    public async Task ParseAsync_MergesMultiLineData()
    {
        var events = await ParseAllAsync("data: line1\ndata: line2\n\n");

        Assert.Single(events);
        Assert.Equal("line1\nline2", events[0].Data);
    }

    [Fact]
    public async Task ParseAsync_StopsAtDoneMarker()
    {
        var events = await ParseAllAsync("data: {\"a\":1}\n\ndata: [DONE]\n\ndata: {\"ignored\":true}\n\n");

        Assert.Equal(2, events.Count);
        Assert.True(events[1].IsDone);
    }

    [Fact]
    public async Task ParseAsync_ReadsEventType()
    {
        var events = await ParseAllAsync("event: content_block_delta\ndata: {\"type\":\"content_block_delta\"}\n\n");

        Assert.Single(events);
        Assert.Equal("content_block_delta", events[0].EventType);
    }

    [Fact]
    public async Task ParseAsync_IgnoresCommentsAndUnknownFields()
    {
        var events = await ParseAllAsync(": keep-alive\nid: 42\nretry: 1000\ndata: ok\n\n");

        Assert.Single(events);
        Assert.Equal("ok", events[0].Data);
    }

    [Fact]
    public async Task ParseAsync_EmitsTrailingEventWithoutBlankLine()
    {
        var events = await ParseAllAsync("data: last");

        Assert.Single(events);
        Assert.Equal("last", events[0].Data);
    }

    [Fact]
    public void TryParseJson_ClonesElement()
    {
        var evt = new SseEvent { Data = "{\"x\":1}" };

        Assert.True(SseParser.TryParseJson(evt, out var element));
        Assert.Equal(1, element.GetProperty("x").GetInt32());
    }

    [Theory]
    [InlineData("[DONE]")]
    [InlineData("not json")]
    [InlineData("")]
    public void TryParseJson_RejectsNonObjectPayloads(string data)
    {
        var evt = new SseEvent { Data = data };

        Assert.False(SseParser.TryParseJson(evt, out _));
    }

    [Fact]
    public async Task ParseNdJsonAsync_ReadsOneObjectPerLine()
    {
        using var stream = ToStream("{\"a\":1}\n\n{\"b\":2}\nnot-json\n");
        var items = new List<JsonElement>();

        await foreach (var item in SseParser.ParseNdJsonAsync(stream))
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count);
        Assert.Equal(2, items[1].GetProperty("b").GetInt32());
    }
}
