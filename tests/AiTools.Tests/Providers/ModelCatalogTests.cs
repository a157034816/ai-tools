using System;
using System.Linq;
using System.Text.Json;
using AiTools.Abstractions;
using AiTools.Anthropic;
using AiTools.Gemini;
using AiTools.OpenAI;
using AiTools.Tests.Support;
using Xunit;

namespace AiTools.Tests.Providers;

public class ModelCatalogTests
{
    // ---------- OpenAI ----------

    [Fact]
    public async Task OpenAi_ListModels_ParsesDataArrayAndSendsAuth()
    {
        const string responseJson = """
        {"object":"list","data":[
          {"id":"gpt-4.1","object":"model","created":1715367049,"owned_by":"openai"},
          {"id":"o3","object":"model","created":1744823457,"owned_by":"openai-internal"}
        ]}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new OpenAiModelClient(new OpenAiOptions { ApiKey = "sk-test" }, handler.CreateClient());

        var models = await client.ListModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.Equal("gpt-4.1", models[0].Id);
        Assert.Equal("openai", models[0].OwnedBy);
        Assert.NotNull(models[0].CreatedAt);
        Assert.Equal("o3", models[1].Id);
        Assert.Equal("models", handler.LastRequestUri!.AbsolutePath.Split('/').Last());
        Assert.Equal("Bearer sk-test", handler.LastHeaders["Authorization"][0]);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Fact]
    public async Task OpenAi_ListModels_AcceptsBareArrayAndSkipsInvalidEntries()
    {
        const string responseJson = """
        [{"id":"a-model"},{"id":""},{"object":"model"},{"id":"b-model"}]
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new OpenAiModelClient(new OpenAiOptions { ApiKey = "k" }, handler.CreateClient());

        var models = await client.ListModelsAsync();

        Assert.Equal(new[] { "a-model", "b-model" }, models.Select(m => m.Id));
        Assert.Equal("a-model", models[0].GetDisplayName());
    }

    [Fact]
    public async Task OpenAi_ListModels_InvalidJson_ThrowsServiceException()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json("not json"));
        var client = new OpenAiModelClient(new OpenAiOptions { ApiKey = "k" }, handler.CreateClient());

        var ex = await Assert.ThrowsAsync<AiServiceException>(() => client.ListModelsAsync());
        Assert.Equal("openai", ex.ProviderName);
        Assert.Equal("not json", ex.ResponseBody);
    }

    [Fact]
    public async Task OpenAi_ListModels_ErrorStatus_ThrowsMappedException()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(
            """{"error":{"message":"bad key","code":"invalid_api_key"}}""",
            System.Net.HttpStatusCode.Unauthorized));
        var client = new OpenAiModelClient(new OpenAiOptions { ApiKey = "k" }, handler.CreateClient());

        var ex = await Assert.ThrowsAsync<AiAuthenticationException>(() => client.ListModelsAsync());
        Assert.Contains("bad key", ex.Message);
    }

    // ---------- Anthropic ----------

    [Fact]
    public async Task Anthropic_ListModels_ParsesDisplayNameAndSendsHeaders()
    {
        const string responseJson = """
        {"data":[
          {"type":"model","id":"claude-sonnet-4-5","display_name":"Claude Sonnet 4.5","created_at":"2025-09-29T00:00:00Z"}
        ],"has_more":false,"last_id":"claude-sonnet-4-5"}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new AnthropicModelClient(
            new AnthropicOptions { ApiKey = "test-key" },
            handler.CreateClient());

        var models = await client.ListModelsAsync();

        var model = Assert.Single(models);
        Assert.Equal("claude-sonnet-4-5", model.Id);
        Assert.Equal("Claude Sonnet 4.5", model.DisplayName);
        Assert.NotNull(model.CreatedAt);
        Assert.Equal("test-key", handler.LastHeaders["x-api-key"][0]);
        Assert.Equal("2023-06-01", handler.LastHeaders["anthropic-version"][0]);
        Assert.Contains("/models", handler.LastRequestUri!.AbsolutePath);
        Assert.Contains("limit=", handler.LastRequestUri.Query);
    }

    [Fact]
    public async Task Anthropic_ListModels_FollowsPagination()
    {
        var page = 0;
        var handler = new ScriptedHttpHandler((_, _) =>
        {
            page++;
            return page == 1
                ? ScriptedHttpHandler.Json("""{"data":[{"id":"m1"}],"has_more":true,"last_id":"m1"}""")
                : ScriptedHttpHandler.Json("""{"data":[{"id":"m2"}],"has_more":false,"last_id":"m2"}""");
        });
        var client = new AnthropicModelClient(new AnthropicOptions { ApiKey = "k" }, handler.CreateClient());

        var models = await client.ListModelsAsync();

        Assert.Equal(new[] { "m1", "m2" }, models.Select(m => m.Id));
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("after_id=m1", handler.RequestUris[1].Query);
    }

    [Fact]
    public async Task Anthropic_ListModels_StopsWhenPageIsRepeated()
    {
        var handler = new ScriptedHttpHandler((_, _) =>
            ScriptedHttpHandler.Json("""{"data":[{"id":"m1"}],"has_more":true,"last_id":"same"}"""));

        var client = new AnthropicModelClient(new AnthropicOptions { ApiKey = "k" }, handler.CreateClient());
        var models = await client.ListModelsAsync();

        // has_more 一直为真但 last_id 不变：第二页被识别为重复并丢弃，不会无限循环。
        Assert.Single(models);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("after_id=same", handler.RequestUris[1].Query);
    }

    // ---------- Gemini ----------

    [Fact]
    public async Task Gemini_ListModels_StripsModelsPrefixAndUsesHeaderKey()
    {
        const string responseJson = """
        {"models":[
          {"name":"models/gemini-2.5-flash","displayName":"Gemini 2.5 Flash"},
          {"name":"models/gemini-2.5-pro","displayName":"Gemini 2.5 Pro"},
          {"displayName":"no name"}
        ]}
        """;

        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json(responseJson));
        var client = new GeminiModelClient(new GeminiOptions { ApiKey = "gemini-key" }, handler.CreateClient());

        var models = await client.ListModelsAsync();

        Assert.Equal(new[] { "gemini-2.5-flash", "gemini-2.5-pro" }, models.Select(m => m.Id));
        Assert.Equal("Gemini 2.5 Flash", models[0].DisplayName);
        Assert.Equal("gemini-key", handler.LastHeaders["x-goog-api-key"][0]);
        Assert.Contains("/v1beta/models", handler.LastRequestUri!.AbsolutePath);
        Assert.Contains("pageSize=", handler.LastRequestUri.Query);
    }

    [Fact]
    public async Task Gemini_ListModels_FollowsPageToken()
    {
        var page = 0;
        var handler = new ScriptedHttpHandler((_, _) =>
        {
            page++;
            return page == 1
                ? ScriptedHttpHandler.Json("""{"models":[{"name":"models/a"}],"nextPageToken":"t1"}""")
                : ScriptedHttpHandler.Json("""{"models":[{"name":"models/b"}]}""");
        });
        var client = new GeminiModelClient(new GeminiOptions { ApiKey = "k" }, handler.CreateClient());

        var models = await client.ListModelsAsync();

        Assert.Equal(new[] { "a", "b" }, models.Select(m => m.Id));
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("pageToken=t1", handler.RequestUris[1].Query);
    }

    [Fact]
    public async Task Gemini_ListModels_UseQueryStringApiKey_AppendsKey()
    {
        var handler = new ScriptedHttpHandler((_, _) => ScriptedHttpHandler.Json("""{"models":[]}"""));
        var client = new GeminiModelClient(
            new GeminiOptions { ApiKey = "gemini key", UseQueryStringApiKey = true },
            handler.CreateClient());

        await client.ListModelsAsync();

        Assert.Contains("key=gemini%20key", handler.LastRequestUri!.Query);
        Assert.False(handler.LastHeaders.ContainsKey("x-goog-api-key"));
    }

    [Fact]
    public async Task Gemini_ListModels_StopsWhenPageTokenIsRepeated()
    {
        var handler = new ScriptedHttpHandler((_, _) =>
            ScriptedHttpHandler.Json("""{"models":[{"name":"models/a"}],"nextPageToken":"same"}"""));

        var client = new GeminiModelClient(new GeminiOptions { ApiKey = "k" }, handler.CreateClient());
        var models = await client.ListModelsAsync();

        Assert.Single(models);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("pageToken=same", handler.RequestUris[1].Query);
    }

    [Fact]
    public void AggregateClients_ExposeModelCatalog()
    {
        var openAi = new OpenAiClient(new OpenAiOptions { ApiKey = "k" });
        Assert.Equal("openai", openAi.Models.ProviderName);

        var anthropic = new AnthropicClient(new AnthropicOptions { ApiKey = "k" });
        Assert.Equal("anthropic", anthropic.Models.ProviderName);

        var gemini = new GeminiClient(new GeminiOptions { ApiKey = "k" });
        Assert.Equal("gemini", gemini.Models.ProviderName);

        gemini.Dispose();
    }

    [Fact]
    public async Task ModelInfo_GetDisplayName_FallsBackToId()
    {
        Assert.Equal("m", new ModelInfo { Id = "m" }.GetDisplayName());
        Assert.Equal("Name", new ModelInfo { Id = "m", DisplayName = "Name" }.GetDisplayName());
    }
}
