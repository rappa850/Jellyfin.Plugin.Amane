using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amane.Core.Tests;

public class HostCompatibilityTests
{
    [Theory]
    [InlineData("http://127.0.0.1:18000.evil.example/image.jpg")]
    [InlineData("http://127.0.0.1:180001/image.jpg")]
    [InlineData("http://127.0.0.1:18000@evil.example/image.jpg")]
    public void ImageUrl_PrefixSpoofIsNeverAmaneLocal(string url)
    {
        var client = new AmaneClient(new ClientProvider(), NullLogger<AmaneClient>.Instance, new AmaneSettings());
        Assert.Equal(url, client.ToDirectImageUrl(url));
        Assert.Contains("/api/resources/proxy?url=", client.ToProxyImageUrl(url));
    }

    [Fact]
    public async Task SharedHttpClient_SupportsRepeatedRequestsAndLiveConfiguration()
    {
        var settings = new AmaneSettings { ApiToken = "first" };
        var provider = new ClientProvider();
        var client = new AmaneClient(provider, NullLogger<AmaneClient>.Instance, settings);
        await client.SearchAsync("first", 1, CancellationToken.None);
        settings.ApiToken = "second";
        await client.SearchAsync("second", 1, CancellationToken.None);
        Assert.Equal(new[] { "first", "second" }, provider.Handler.Tokens);
    }

    [Fact]
    public async Task ActorSummaryCache_DoesNotHideProfileDetails()
    {
        var provider = new ClientProvider();
        var client = new AmaneClient(provider, NullLogger<AmaneClient>.Instance, new AmaneSettings());
        await client.LookupActorAsync("演员", CancellationToken.None);
        var actor = await client.ResolveActorAsync(new Dictionary<string, string>(), "演员", CancellationToken.None);
        Assert.Equal("完整简介", actor?.Overview);
        Assert.Equal(2, provider.Handler.Tokens.Count);
        await client.ResolveActorAsync(new Dictionary<string, string> { ["Amane"] = "7" }, "演员", CancellationToken.None);
        Assert.Equal(2, provider.Handler.Tokens.Count);
    }

    private sealed class ClientProvider : IAmaneHttpClientProvider
    {
        public Handler Handler { get; } = new();
        private readonly HttpClient _client;
        public ClientProvider() => _client = new(Handler);
        public HttpClient CreateClient() => _client;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string?> Tokens { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tokens.Add(request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            var json = path == "/api/actors/7" ? """{"id":7,"name":"演员","overview":"完整简介"}"""
                : path == "/api/actors" ? """{"items":[{"id":7,"name":"演员"}],"total":1}"""
                : """{"items":[],"total":0}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
