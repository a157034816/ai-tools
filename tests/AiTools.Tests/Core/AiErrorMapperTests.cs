using System.Net;
using AiTools.Abstractions;
using AiTools.Core.Http;
using Xunit;

namespace AiTools.Tests.Core;

public class AiErrorMapperTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Create_MapsAuthFailures(HttpStatusCode status)
    {
        var ex = AiErrorMapper.Create("openai", status, "{\"error\":{\"message\":\"bad key\",\"code\":\"invalid_api_key\"}}");

        var auth = Assert.IsType<AiAuthenticationException>(ex);
        Assert.Equal("openai", auth.ProviderName);
        Assert.Equal((int)status, auth.StatusCode);
        Assert.Equal("invalid_api_key", auth.ErrorCode);
        Assert.Equal("bad key", auth.Message);
    }

    [Fact]
    public void Create_MapsRateLimitWithRetryAfter()
    {
        var ex = AiErrorMapper.Create("anthropic", HttpStatusCode.TooManyRequests, "{\"error\":{\"type\":\"rate_limit_error\",\"message\":\"slow down\"}}", TimeSpan.FromSeconds(3));

        var rate = Assert.IsType<AiRateLimitException>(ex);
        Assert.Equal(3, rate.RetryAfter!.Value.TotalSeconds);
        Assert.Equal("rate_limit_error", rate.ErrorCode);
        Assert.True(rate.IsTransient);
    }

    [Fact]
    public void Create_MapsServerErrors()
    {
        var ex = AiErrorMapper.Create("gemini", HttpStatusCode.BadGateway, null);

        var server = Assert.IsType<AiServerException>(ex);
        Assert.True(server.IsTransient);
        Assert.Null(server.ErrorCode);
    }

    [Fact]
    public void Create_MapsInvalidRequests()
    {
        var ex = AiErrorMapper.Create("openai", HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"model not found\"}}");

        var invalid = Assert.IsType<AiInvalidRequestException>(ex);
        Assert.Equal("model not found", invalid.Message);
        Assert.False(invalid.IsTransient);
    }

    [Fact]
    public void Create_ParsesGeminiStyleError()
    {
        var body = "{\"error\":{\"code\":400,\"message\":\"API key not valid\",\"status\":\"INVALID_ARGUMENT\"}}";

        var ex = AiErrorMapper.Create("gemini", HttpStatusCode.BadRequest, body);

        Assert.Equal("API key not valid", ex.Message);
        Assert.Equal("INVALID_ARGUMENT", ex.ErrorCode);
    }

    [Theory]
    [InlineData("not json", null)]
    [InlineData("{\"error\":\"string error\"}", "string error")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Create_FallsBackToSyntheticMessages(string? body, string? expectedMessage)
    {
        var ex = AiErrorMapper.Create("openai", HttpStatusCode.BadRequest, body);

        if (expectedMessage != null)
        {
            Assert.Equal(expectedMessage, ex.Message);
        }
        else
        {
            Assert.Contains("openai", ex.Message);
        }
    }
}
