using System.Net;
using System.Text;
using System.Text.Json;
using AiTools.Abstractions;
using AiTools.OpenAI;
using AiTools.Tests.Support;
using Xunit;

namespace AiTools.Tests.Providers;

public class OpenAiOtherClientsTests
{
    private static OpenAiOptions Options() => new() { ApiKey = "sk-test" };

    [Fact]
    public async Task EmbedAsync_MapsSingleAndMultipleInputs()
    {
        const string responseJson = """
        {"object":"list","model":"text-embedding-3-small",
         "data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]},{"object":"embedding","index":1,"embedding":[0.3,0.4]}],
         "usage":{"prompt_tokens":4,"total_tokens":4}}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new OpenAiEmbeddingClient(Options(), handler.CreateClient());

        var response = await client.EmbedAsync(new EmbeddingRequest
        {
            Model = "text-embedding-3-small",
            Inputs = new[] { "a", "b" },
            Dimensions = 256
        });

        Assert.Equal(2, response.Data.Count);
        Assert.Equal(0.1f, response.Data[0].Vector[0], 5);
        Assert.Equal(0.4f, response.Data[1].Vector[1], 5);
        Assert.Equal(4, response.Usage!.TotalTokens);

        Assert.Equal("https://api.openai.com/v1/embeddings", handler.LastRequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("input").ValueKind);
        Assert.Equal(256, body.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal("float", body.RootElement.GetProperty("encoding_format").GetString());
    }

    [Fact]
    public async Task GenerateAsync_ParsesUrlAndBase64Images()
    {
        var png = new byte[] { 1, 2, 3 };
        var responseJson = $$"""
        {"created":123,"data":[
          {"url":"https://example.com/a.png","revised_prompt":"A cat"},
          {"b64_json":"{{Convert.ToBase64String(png)}}"}
        ]}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new OpenAiImageClient(Options(), handler.CreateClient());

        var response = await client.GenerateAsync(new ImageGenerationRequest
        {
            Model = "gpt-image-1",
            Prompt = "a cat",
            Size = "1024x1024",
            Count = 2,
            ResponseFormat = ImageResponseFormat.Base64,
            OutputFormat = "png"
        });

        Assert.Equal(2, response.Images.Count);
        Assert.Equal("https://example.com/a.png", response.Images[0].Url);
        Assert.Equal("A cat", response.Images[0].RevisedPrompt);
        Assert.Equal(png, response.Images[1].Bytes);
        Assert.Equal("image/png", response.Images[1].MediaType);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("b64_json", body.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("n").GetInt32());
        Assert.Equal("png", body.RootElement.GetProperty("output_format").GetString());
    }

    [Fact]
    public async Task SynthesizeAsync_ReturnsAudioBytes()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Bytes(new byte[] { 9, 9, 9 }));
        var client = new OpenAiAudioClient(Options(), handler.CreateClient());

        var response = await client.SynthesizeAsync(new SpeechRequest
        {
            Model = "gpt-4o-mini-tts",
            Input = "hello",
            Voice = "alloy",
            ResponseFormat = "mp3",
            Speed = 1.2
        });

        Assert.Equal(new byte[] { 9, 9, 9 }, response.Audio);
        Assert.Equal("mp3", response.MediaType);

        Assert.Equal("https://api.openai.com/v1/audio/speech", handler.LastRequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("hello", body.RootElement.GetProperty("input").GetString());
        Assert.Equal("alloy", body.RootElement.GetProperty("voice").GetString());
        Assert.Equal(1.2, body.RootElement.GetProperty("speed").GetDouble(), 5);
    }

    [Fact]
    public async Task TranscribeAsync_SendsMultipartAndParsesSegments()
    {
        const string responseJson = """
        {"text":"hello world","language":"en","duration":2.5,
         "segments":[{"id":0,"start":0.0,"end":1.2,"text":"hello"},{"id":1,"start":1.2,"end":2.5,"text":"world"}]}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new OpenAiAudioClient(Options(), handler.CreateClient());

        var response = await client.TranscribeAsync(new TranscriptionRequest
        {
            Model = "whisper-1",
            Audio = Encoding.UTF8.GetBytes("fake-audio"),
            FileName = "clip.mp3",
            MediaType = "audio/mpeg",
            Language = "en",
            IncludeSegments = true
        });

        Assert.Equal("hello world", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal(2.5, response.DurationSeconds);
        Assert.Equal(2, response.Segments.Count);
        Assert.Equal("world", response.Segments[1].Text);

        var body = handler.LastRequestBody!;
        Assert.Contains("multipart/form-data", handler.LastHeaders["Content-Type"][0]);
        Assert.Contains("name=model", body);
        Assert.Contains("whisper-1", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("verbose_json", body);
        Assert.Contains("name=language", body);
    }
}
