using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Amane.Tests;

/// <summary>
/// AmaneClient 演员缓存行为测试：TTL 内命中、禁用缓存、清空缓存后强制回源。
/// 均通过 stub HttpMessageHandler + 伪 IHttpClientFactory 注入，不发起真实网络请求。
/// </summary>
public class AmaneClientCacheTests
{
    private const string ActorListJson =
        """{"items":[{"id":7,"name":"田中","image_urls":["http://img/1.jpg"]}],"total":1}""";

    private const string ActorDetailJson =
        """{"id":7,"name":"田中","image_urls":["http://img/1.jpg"],"aliases":["たなか"]}""";

    [Fact]
    public async Task LookupActor_WithinTtl_SecondCallServedFromCache()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonOk(ActorListJson)));
        var client = CreateClient(handler, actorCacheTtl: TimeSpan.FromMinutes(10));

        var first = await client.LookupActorAsync("田中", CancellationToken.None);
        var second = await client.LookupActorAsync("田中", CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(first, second);
        Assert.Equal(7, first?.Id);
    }

    [Fact]
    public async Task LookupActor_CacheDisabled_AlwaysHitsBackend()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonOk(ActorListJson)));
        var client = CreateClient(handler, actorCacheTtl: TimeSpan.Zero);

        await client.LookupActorAsync("田中", CancellationToken.None);
        await client.LookupActorAsync("田中", CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task LookupActor_TtlExpired_RefetchesFromBackend()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(JsonOk(ActorListJson)));
        var client = CreateClient(handler, actorCacheTtl: TimeSpan.FromMilliseconds(150));

        await client.LookupActorAsync("田中", CancellationToken.None);
        await Task.Delay(300);
        await client.LookupActorAsync("田中", CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ClearActorCache_ClearsNameAndIdKeys_ForcesRefetch()
    {
        var handler = new StubHandler((request, _) =>
            Task.FromResult(JsonOk(request.RequestUri!.AbsolutePath.Contains("/api/actors/") ? ActorDetailJson : ActorListJson)));
        var client = CreateClient(handler, actorCacheTtl: TimeSpan.FromMinutes(10));

        // 名字查询写入 name + id:N 双键
        await client.LookupActorAsync("田中", CancellationToken.None);
        await client.GetActorByIdAsync(7, CancellationToken.None);
        Assert.Equal(1, handler.CallCount);

        var cleared = client.ClearActorCache();
        Assert.Equal(2, cleared);

        // 清空后名字查询强制回源；回源结果重新写入双键，id 直取再次命中缓存
        await client.LookupActorAsync("田中", CancellationToken.None);
        Assert.Equal(2, handler.CallCount);

        await client.GetActorByIdAsync(7, CancellationToken.None);
        Assert.Equal(2, handler.CallCount);
    }

    private static HttpResponseMessage JsonOk(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static AmaneClient CreateClient(StubHandler handler, TimeSpan actorCacheTtl)
    {
        return new AmaneClient(
            new StubHttpClientFactory(handler),
            NullLogger<AmaneClient>.Instance,
            maxConcurrency: null,
            requestTimeout: TimeSpan.FromSeconds(10),
            circuitFailureThreshold: 1000,
            circuitCooldown: TimeSpan.FromMinutes(1),
            apiToken: null,
            actorCacheTtl: actorCacheTtl);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
        private int _callCount;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
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
