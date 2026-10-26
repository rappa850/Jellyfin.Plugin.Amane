namespace Amane.Core;

/// <summary>宿主配置读取契约；适配器每次读取当前配置，保留热更新语义。</summary>
public interface IAmaneSettings
{
    string ServerUrl { get; }
    string ApiToken { get; }
    /// <summary>Amane 图片 Token（0.23.0+），仅用于无法带请求头的图片访问，经 query token 传递。</summary>
    string ImageToken { get; }
    int TimeoutSeconds { get; }
    int MaxConcurrentRequests { get; }
    int ActorCacheMinutes { get; }
}

/// <summary>默认配置，也供无宿主的测试使用。</summary>
public sealed class AmaneSettings : IAmaneSettings
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:18000";
    public string ApiToken { get; set; } = string.Empty;
    public string ImageToken { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 5;
    public int MaxConcurrentRequests { get; set; } = 4;
    public int ActorCacheMinutes { get; set; } = 360;
}

/// <summary>提供宿主管理的客户端；调用方不得释放或修改其 Timeout。</summary>
public interface IAmaneHttpClientProvider
{
    HttpClient CreateClient();
}

/// <summary>两侧平台共用的元数据绑定键。</summary>
public static class AmaneProviderIds
{
    public const string ProviderIdName = "Amane";
    public const string InternalIdProviderIdName = "AmaneId";
}
