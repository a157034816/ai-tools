using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Gemini;
using AiTools.Tests.Support;
using Xunit;

namespace AiTools.Tests.Providers;

public class GeminiOtherClientsTests
{
    private static GeminiOptions Options() => new() { ApiKey = "gemini-key" };

    [Fact]
    public async Task EmbedAsync_SingleInput_UsesEmbedContent()
    {
        const string responseJson = """
        {"embedding":{"values":[0.1,0.2,0.3]}}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new GeminiEmbeddingClient(Options(), handler.CreateClient());

        var response = await client.EmbedAsync(new EmbeddingRequest
        {
            Model = "gemini-embedding-001",
            Inputs = new[] { "hello" },
            Dimensions = 3,
            InputType = "query"
        });

        var embedding = Assert.Single(response.Data);
        Assert.Equal(3, embedding.Vector.Length);
        Assert.Equal(0.2f, embedding.Vector[1], 5);

        Assert.Contains(":embedContent", handler.LastRequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("models/gemini-embedding-001", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("outputDimensionality").GetInt32());
        Assert.Equal("RETRIEVAL_QUERY", body.RootElement.GetProperty("taskType").GetString());
    }

    [Fact]
    public async Task EmbedAsync_MultipleInputs_UsesBatchEmbedContents()
    {
        const string responseJson = """
        {"embeddings":[{"values":[1.0,2.0]},{"values":[3.0,4.0]}]}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new GeminiEmbeddingClient(Options(), handler.CreateClient());

        var response = await client.EmbedAsync(new EmbeddingRequest
        {
            Model = "gemini-embedding-001",
            Inputs = new[] { "a", "b" }
        });

        Assert.Equal(2, response.Data.Count);
        Assert.Equal(4.0f, response.Data[1].Vector[1], 5);

        Assert.Contains(":batchEmbedContents", handler.LastRequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var requests = body.RootElement.GetProperty("requests");
        Assert.Equal(2, requests.GetArrayLength());
        Assert.Equal("models/gemini-embedding-001", requests[0].GetProperty("model").GetString());
        Assert.Equal("a", requests[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task EmbedAsync_EmptyInputsReturnsEmpty()
    {
        var handler = new ScriptedHttpHandler((_, _) => throw new InvalidOperationException("should not be called"));
        var client = new GeminiEmbeddingClient(Options(), handler.CreateClient());

        var response = await client.EmbedAsync(new EmbeddingRequest { Model = "m", Inputs = Array.Empty<string>() });

        Assert.Empty(response.Data);
    }

    [Fact]
    public async Task GenerateAsync_ExtractsInlineImageBytes()
    {
        var png = new byte[] { 137, 80, 78, 71 };
        var responseJson = $$$"""
        {"candidates":[{"content":{"role":"model","parts":[
          {"text":"Here you go"},
          {"inlineData":{"mimeType":"image/png","data":"{{{Convert.ToBase64String(png)}}}"}}
        ]}}]}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new GeminiImageClient(Options(), handler.CreateClient());

        var response = await client.GenerateAsync(new ImageGenerationRequest
        {
            Model = "gemini-2.5-flash-image",
            Prompt = "a cat"
        });

        var image = Assert.Single(response.Images);
        Assert.Equal(png, image.Bytes);
        Assert.Equal("image/png", image.MediaType);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var modalities = body.RootElement.GetProperty("generationConfig").GetProperty("responseModalities");
        Assert.Contains("IMAGE", modalities.EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task SynthesizeAsync_ExtractsInlineAudioBytes()
    {
        var wav = new byte[] { 1, 2, 3, 4 };
        var responseJson = $$$"""
        {"candidates":[{"content":{"role":"model","parts":[
          {"inlineData":{"mimeType":"audio/wav","data":"{{{Convert.ToBase64String(wav)}}}"}}
        ]}}]}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new GeminiAudioClient(Options(), handler.CreateClient());

        var response = await client.SynthesizeAsync(new SpeechRequest
        {
            Model = "gemini-2.5-flash-preview-tts",
            Input = "hello",
            Voice = "Kore"
        });

        Assert.Equal(wav, response.Audio);
        Assert.Equal("audio/wav", response.MediaType);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var config = body.RootElement.GetProperty("generationConfig");
        Assert.Contains("AUDIO", config.GetProperty("responseModalities").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Kore", config.GetProperty("speechConfig").GetProperty("voiceConfig")
            .GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
    }

    [Fact]
    public async Task TranscribeAsync_ParsesStructuredJson()
    {
        const string responseJson = """
        {"candidates":[{"content":{"role":"model","parts":[
          {"text":"{\"text\":\"hello world\",\"language\":\"en\",\"segments\":[{\"start\":0.0,\"end\":1.5,\"text\":\"hello\"}]}"}
        ]},"finishReason":"STOP"}]}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new GeminiAudioClient(Options(), handler.CreateClient());

        var response = await client.TranscribeAsync(new TranscriptionRequest
        {
            Model = "gemini-2.5-flash",
            Audio = new byte[] { 1, 2, 3 },
            MediaType = "audio/wav",
            IncludeSegments = true
        });

        Assert.Equal("hello world", response.Text);
        Assert.Equal("en", response.Language);
        var segment = Assert.Single(response.Segments);
        Assert.Equal(1.5, segment.End);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        // 第一个 part 是转录提示词，第二个是音频内联数据。
        Assert.Equal("audio/wav", parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
    }
}
