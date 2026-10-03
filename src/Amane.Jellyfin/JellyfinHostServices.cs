using Amane.Core;

namespace Jellyfin.Plugin.Amane;

/// <summary>每次访问读取当前配置，配置保存后无需替换 Core 客户端。</summary>
public sealed class JellyfinAmaneSettings : IAmaneSettings
{
    public string ServerUrl => Plugin.Instance?.Configuration.ServerUrl ?? "http://127.0.0.1:18000";
    public string ApiToken => Plugin.Instance?.Configuration.ApiToken ?? string.Empty;
    public int TimeoutSeconds => Plugin.Instance?.Configuration.TimeoutSeconds ?? 5;
    public int MaxConcurrentRequests => Plugin.Instance?.Configuration.MaxConcurrentRequests ?? 4;
    public int ActorCacheMinutes => Plugin.Instance?.Configuration.ActorCacheMinutes ?? 360;
}

/// <summary>Jellyfin HTTP 工厂适配；显式 CTS 是主要超时机制。</summary>
public sealed class JellyfinHttpClientProvider(IHttpClientFactory factory) : IAmaneHttpClientProvider
{
    public HttpClient CreateClient()
    {
        var client = factory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }
}
