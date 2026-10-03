using Amane.Core;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Amane.Emby.Api;

/// <summary>服务端探活，仅管理员可调用。</summary>
[Route("/Amane/Health", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class GetAmaneHealth : IReturn<AmaneHealthCheckResult> { }

/// <summary>清除演员缓存，仅管理员可调用。</summary>
[Route("/Amane/ClearCache", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class ClearAmaneCache : IReturn<ClearCacheResult> { }

/// <summary>清除缓存的响应。</summary>
public sealed class ClearCacheResult
{
    public int Cleared { get; set; }
}

/// <summary>Emby 自动发现 IService，业务逻辑共用 Core 单例。</summary>
public sealed class AmaneDiagnosticsService : IService
{
    // Emby 服务分派器仅识别 Task<object>，Task<T> 会被当成无响应体（204）。
    public async Task<object> Get(GetAmaneHealth request) => await Plugin.Instance!.Client.CheckHealthAsync(CancellationToken.None).ConfigureAwait(false);
    public ClearCacheResult Post(ClearAmaneCache request) => new() { Cleared = Plugin.Instance!.Client.ClearActorCache() };
}
