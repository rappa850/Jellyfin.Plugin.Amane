using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Amane.Tests;

/// <summary>
/// AmaneClient.CheckHealthAsync 测试：探活 /api/health + 经 /api/openapi.json 验证 Token。
/// 均通过 stub HttpMessageHandler + 伪 IHttpClientFactory 注入，不发起真实网络请求。
/// </summary>
public class AmaneClientHealthCheckTests
{
    [Fact]
    public async Task HealthOk_AuthOk_ReturnsReachableWithVersion()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/health")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"ok","version":"0.6.2"}""")
                };
            }

            // /api/openapi.json：带正确 Bearer 才放行
            return request.Headers.Authorization?.Parameter == "test-token"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var client = CreateClient(handler);
        var result = await client.CheckHealthAsync(CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Equal("0.6.2", result.Version);
        // 测试环境无 Plugin.Instance，Token 未配置 → notConfigured
        Assert.Equal("notConfigured", result.AuthStatus);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task HealthOk_AuthRejected_ReturnsUnauthorized()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/health"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"ok","version":"0.6.2"}""")
                }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var client = CreateClient(handler, token: "bad-token");
        var result = await client.CheckHealthAsync(CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Equal("unauthorized", result.AuthStatus);
    }

    [Fact]
    public async Task HealthUnreachable_ReturnsNotReachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var client = CreateClient(handler);
        var result = await client.CheckHealthAsync(CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.NotNull(result.Error);
    }

    private static AmaneClient CreateClient(StubHandler handler, string? token = null)
    {
        return new AmaneClient(
            new StubHttpClientFactory(handler),
            NullLogger<AmaneClient>.Instance,
            null,
            TimeSpan.FromSeconds(10),
            1000,
            TimeSpan.FromMinutes(1),
            token);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
