using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Amane.Tests;

/// <summary>
/// AmaneClient 图片链路测试：代理 URL 改写、下载失败抛异常（不缓存坏图）、Bearer 只发 Amane 域内。
/// 均通过 stub HttpMessageHandler + 伪 IHttpClientFactory 注入，不发起真实网络请求。
/// 测试环境无 Plugin.Instance，ServerUrl 回退默认值 http://127.0.0.1:18000。
/// </summary>
public class AmaneClientImageTests
{
    private const string ExternalUrl = "https://awsimgsrc.dmm.co.jp/pics_dig/digital/video/ipzz00822/ipzz00822ps.jpg";

    [Fact]
    public void ToProxyImageUrl_ExternalUrl_RewritesToAmaneProxy()
    {
        var client = CreateClient(new StubHandler((_, _) => Task.FromResult(ImageOk())));

        var proxied = client.ToProxyImageUrl(ExternalUrl);

        Assert.Equal(
            "http://127.0.0.1:18000/api/resources/proxy?url=" + Uri.EscapeDataString(ExternalUrl),
            proxied);
    }

    [Fact]
    public void ToProxyImageUrl_RelativeAmaneResource_PrependsServerUrlWithoutProxy()
    {
        var client = CreateClient(new StubHandler((_, _) => Task.FromResult(ImageOk())));

        // 裁切海报等本地资源是相对路径，代理端点不接受相对 URL（400），需直接补全主机
        Assert.Equal(
            "http://127.0.0.1:18000/api/resources/eb8035bb5c6ccb49",
            client.ToProxyImageUrl("/api/resources/eb8035bb5c6ccb49"));
    }

    [Fact]
    public void ToProxyImageUrl_AmaneHostUrl_ReturnsAsIs()
    {
        var client = CreateClient(new StubHandler((_, _) => Task.FromResult(ImageOk())));
        var amaneUrl = "http://127.0.0.1:18000/api/resources/proxy?url=x";

        Assert.Equal(amaneUrl, client.ToProxyImageUrl(amaneUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToProxyImageUrl_NullOrBlank_PassesThrough(string? url)
    {
        var client = CreateClient(new StubHandler((_, _) => Task.FromResult(ImageOk())));

        Assert.Equal(url, client.ToProxyImageUrl(url));
    }

    [Fact]
    public void ToDirectImageUrl_ExternalUrl_ReturnsAsIs()
    {
        var client = CreateClient(new StubHandler((_, _) => Task.FromResult(ImageOk())));

        Assert.Equal(ExternalUrl, client.ToDirectImageUrl(ExternalUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/api/resources/eb8035bb5c6ccb49")] // 相对路径本地资源：浏览器/裸客户端拿不到（401）
    [InlineData("http://127.0.0.1:18000/api/resources/eb8035bb5c6ccb49")] // 域内绝对 URL 同理
    public void ToDirectImageUrl_AmaneLocalOrBlank_ReturnsNull(string? url)
    {
        var client = CreateClient(new StubHandler((_, _) => Task.FromResult(ImageOk())));

        Assert.Null(client.ToDirectImageUrl(url));
    }

    [Fact]
    public async Task GetImage_Success_ReturnsBufferedResponse()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(ImageOk()));
        var client = CreateClient(handler);

        using var response = await client.GetImageAsync(ExternalUrl, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        // 带浏览器 UA 反爬保险
        Assert.Contains("Mozilla/5.0", handler.LastRequest!.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task GetImage_HttpError_ThrowsInsteadOfReturningErrorBody()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("forbidden")
            }));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetImageAsync(ExternalUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetImage_NonImageContentType_Throws()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>", System.Text.Encoding.UTF8, "text/html")
            }));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetImageAsync(ExternalUrl, CancellationToken.None));
    }

    [Fact]
    public async Task GetImage_AmaneHostUrl_AttachesBearerToken()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(ImageOk()));
        var client = CreateClient(handler, apiToken: "test-token");

        using var response = await client.GetImageAsync(
            "http://127.0.0.1:18000/api/resources/proxy?url=x", CancellationToken.None);

        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-token"), handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task GetImage_ExternalUrl_NeverLeaksBearerToken()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(ImageOk()));
        var client = CreateClient(handler, apiToken: "test-token");

        using var response = await client.GetImageAsync(ExternalUrl, CancellationToken.None);

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    private static HttpResponseMessage ImageOk()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF })
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") }
            }
        };
    }

    private static AmaneClient CreateClient(StubHandler handler, string? apiToken = null)
    {
        return new AmaneClient(
            new StubHttpClientFactory(handler),
            NullLogger<AmaneClient>.Instance,
            maxConcurrency: null,
            requestTimeout: TimeSpan.FromSeconds(10),
            circuitFailureThreshold: 1000,
            circuitCooldown: TimeSpan.FromMinutes(1),
            apiToken: apiToken);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _responder(request, cancellationToken);
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
