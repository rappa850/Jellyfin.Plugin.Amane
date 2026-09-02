using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Amane.Configuration;

/// <summary>
/// Amane 插件配置。
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Amane 服务地址，例如 http://127.0.0.1:18000。
    /// </summary>
    public string ServerUrl { get; set; } = "http://127.0.0.1:18000";

    /// <summary>
    /// Gets or sets the Amane API token（以 Authorization: Bearer 头发送）。
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the 单次 HTTP 请求超时时间（秒）。冷门资源 LLM 刮削较慢时可调大。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the 同时发往 Amane 的元数据/演员查询最大并发数（图片下载不占额度）。
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 4;

    /// <summary>
    /// Gets or sets the 演员信息进程内缓存时长（分钟），0 表示禁用缓存（Amane 侧数据更新后立即可见）。
    /// </summary>
    public int ActorCacheMinutes { get; set; } = 360;
}
