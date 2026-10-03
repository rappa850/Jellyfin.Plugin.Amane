using Amane.Core;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Logging;

namespace Amane.Emby;

/// <summary>桥接 Emby 原生日志，避免依赖宿主容器注册 ILogger。</summary>
internal sealed class EmbyLogger<T>(ILogManager manager) : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly MediaBrowser.Model.Logging.ILogger _logger = manager.GetLogger("Amane." + typeof(T).Name);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level != LogLevel.None;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (exception is not null) message += "\n" + exception;
        switch (level)
        {
            case LogLevel.Trace:
            case LogLevel.Debug: _logger.Debug("{0}", message); break;
            case LogLevel.Information: _logger.Info("{0}", message); break;
            case LogLevel.Warning: _logger.Warn("{0}", message); break;
            case LogLevel.Error:
            case LogLevel.Critical: _logger.Error("{0}", message); break;
        }
    }
}

/// <summary>复用连接，Timeout 只设置一次，热配置使用每请求 CTS。</summary>
internal sealed class EmbyHttpClientProvider : IAmaneHttpClientProvider
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    public HttpClient CreateClient() => Client;
}

/// <summary>配置保存会替换对象，每次读取当前实例。</summary>
internal sealed class EmbyAmaneSettings : IAmaneSettings
{
    private Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;
    public string ServerUrl => Config.ServerUrl;
    public string ApiToken => Config.ApiToken;
    public int TimeoutSeconds => Config.TimeoutSeconds;
    public int MaxConcurrentRequests => Config.MaxConcurrentRequests;
    public int ActorCacheMinutes => Config.ActorCacheMinutes;
}
