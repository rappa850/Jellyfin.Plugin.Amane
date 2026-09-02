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
    /// Gets or sets the 单次 HTTP 请求超时时间（秒）。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;
}
