using System.Net;
using System.Net.Http;
using AiTools.Abstractions;
using AiTools.Core.Http;
using Xunit;

namespace AiTools.Tests.Core;

public class RetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_RetriesTransientFailuresThenSucceeds()
    {
        var attempts = 0;

        var result = await RetryPolicy.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new AiServerException("boom", "test", 503);
                }

                return Task.FromResult("ok");
            },
            "test",
            maxRetries: 3,
            baseDelay: TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_HonorsRetryAfterFromRateLimit()
    {
        var attempts = 0;
        var start = DateTime.UtcNow;

        await RetryPolicy.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new AiRateLimitException("slow down", "test", retryAfter: TimeSpan.FromMilliseconds(50));
                }

                return Task.FromResult(true);
            },
            "test",
            maxRetries: 2,
            baseDelay: TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(45));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryNonTransientErrors()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<AiInvalidRequestException>(() => RetryPolicy.ExecuteAsync<bool>(
            _ =>
            {
                attempts++;
                throw new AiInvalidRequestException("bad request", "test", 400);
            },
            "test",
            maxRetries: 5,
            baseDelay: TimeSpan.FromMilliseconds(1),
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_GivesUpAfterMaxRetries()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<AiServerException>(() => RetryPolicy.ExecuteAsync<bool>(
            _ =>
            {
                attempts++;
                throw new AiServerException("boom", "test", 500);
            },
            "test",
            maxRetries: 2,
            baseDelay: TimeSpan.FromMilliseconds(1),
            CancellationToken.None));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RetryPolicy.ExecuteAsync(
            ct => Task.FromResult(1),
            "test",
            maxRetries: 3,
            baseDelay: TimeSpan.FromMilliseconds(1),
            cts.Token));
    }
}

public class AiHttpClientExtensionsTests
{
    [Fact]
    public async Task SendAndEnsureAsync_ThrowsMappedExceptionWithBody()
    {
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"message\":\"quota exceeded\"}}")
        }));

        var ex = await Assert.ThrowsAsync<AiRateLimitException>(() =>
            http.SendAndEnsureAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.test"), "test", CancellationToken.None));

        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("quota exceeded", ex.Message);
    }

    [Fact]
    public async Task SendAndEnsureAsync_ReturnsSuccessfulResponse()
    {
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok")
        }));

        using var response = await http.SendAndEnsureAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test"),
            "test",
            CancellationToken.None);

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
