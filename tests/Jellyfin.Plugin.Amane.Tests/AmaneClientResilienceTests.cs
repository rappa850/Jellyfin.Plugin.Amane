using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Amane.Tests;

/// <summary>
/// AmaneClient 弹性行为测试：每请求超时快速失败、连续失败熔断、并发信号量背压。
/// 均通过 stub HttpMessageHandler + 伪 IHttpClientFactory 注入，不发起真实网络请求。
/// </summary>
public class AmaneClientResilienceTests
{
    [Fact]
    public async Task RequestTimeout_HangingBackend_FailsFastWithNull()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            // 模拟后端 LLM 刮削长时间不返回
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = CreateClient(handler, requestTimeout: TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        var result = await client.LookupAsync("IPZZ-822", CancellationToken.None);
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"超时兜底未生效，耗时 {sw.Elapsed}");
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CircuitBreaker_ConsecutiveFailures_ShortCircuitsThenRecovers()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var client = CreateClient(
            handler,
            circuitFailureThreshold: 3,
            circuitCooldown: TimeSpan.FromMilliseconds(300));

        // 连续失败达到阈值（3 次真实请求）
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await client.GetByIdAsync(1, CancellationToken.None));
        }

        Assert.Equal(3, handler.CallCount);

        // 熔断打开：不再发请求，直接快速失败
        Assert.Null(await client.GetByIdAsync(1, CancellationToken.None));
        Assert.Equal(3, handler.CallCount);

        // 冷却结束后恢复放行
        await Task.Delay(400);
        Assert.Null(await client.GetByIdAsync(1, CancellationToken.None));
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task ConcurrencyLimit_ParallelLookups_CappedBySemaphore()
    {
        var inFlight = 0;
        var maxInFlight = 0;

        var handler = new StubHandler(async (_, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            int snapshot;
            while ((snapshot = maxInFlight) < now)
            {
                Interlocked.CompareExchange(ref maxInFlight, now, snapshot);
            }

            await Task.Delay(150, ct);
            Interlocked.Decrement(ref inFlight);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        });

        var client = CreateClient(handler, maxConcurrency: 2);

        await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.GetByIdAsync(1, CancellationToken.None)));

        Assert.Equal(2, maxInFlight);
    }

    private static AmaneClient CreateClient(
        StubHandler handler,
        int? maxConcurrency = null,
        TimeSpan? requestTimeout = null,
        int circuitFailureThreshold = 1000,
        TimeSpan? circuitCooldown = null)
    {
        return new AmaneClient(
            new StubHttpClientFactory(handler),
            NullLogger<AmaneClient>.Instance,
            maxConcurrency,
            requestTimeout ?? TimeSpan.FromSeconds(10),
            circuitFailureThreshold,
            circuitCooldown ?? TimeSpan.FromMinutes(1));
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
