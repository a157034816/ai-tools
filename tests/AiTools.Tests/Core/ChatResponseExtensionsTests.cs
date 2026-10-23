using System.Text.Json;
using AiTools.Abstractions;
using Xunit;

namespace AiTools.Tests.Core;

public class ChatResponseExtensionsTests
{
    private sealed record Person(string Name, int Age);

    [Fact]
    public void GetJson_DeserializesCaseInsensitively()
    {
        var response = new ChatResponse
        {
            Message = ChatMessage.Assistant("{\"name\":\"张三\",\"age\":28}")
        };

        var person = response.GetJson<Person>();

        Assert.NotNull(person);
        Assert.Equal("张三", person!.Name);
        Assert.Equal(28, person.Age);
    }

    [Fact]
    public void GetJson_ReturnsDefaultForEmptyText()
    {
        var response = new ChatResponse { Message = ChatMessage.Assistant("   ") };

        Assert.Null(response.GetJson<Person>());
    }

    [Fact]
    public void GetJson_ThrowsOnMalformedJson()
    {
        var response = new ChatResponse { Message = ChatMessage.Assistant("not json") };

        Assert.Throws<JsonException>(() => response.GetJson<Person>());
    }

    [Fact]
    public void TryGetJson_ReturnsFalseOnMalformedJson()
    {
        var response = new ChatResponse { Message = ChatMessage.Assistant("not json") };

        Assert.False(response.TryGetJson<Person>(out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryGetJson_ReturnsTrueOnSuccess()
    {
        var response = new ChatResponse { Message = ChatMessage.Assistant("{\"name\":\"李四\",\"age\":30}") };

        Assert.True(response.TryGetJson<Person>(out var value));
        Assert.Equal("李四", value!.Name);
    }
}
