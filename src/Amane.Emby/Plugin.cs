using Amane.Core;
using System;
using System.Collections.Generic;
using Amane.Emby.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Amane.Emby;

/// <summary>
/// Amane 元数据插件：作为本地 Amane 服务的透明 HTTP 代理客户端。
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// 插件固定 GUID。
    /// </summary>
    public const string PluginGuid = "e4c1a8a9-023d-4a7a-978b-eec21d66c059";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, MediaBrowser.Model.Logging.ILogManager logManager)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        // 构造时宿主尚未设置 AssemblyFilePath，读取 Configuration 会失败。
        _client = new Lazy<AmaneClient>(() => new AmaneClient(
            new EmbyHttpClientProvider(), new EmbyLogger<AmaneClient>(logManager), new EmbyAmaneSettings()));
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>全体 Provider 共用的进程内客户端。</summary>
    private readonly Lazy<AmaneClient> _client;
    internal AmaneClient Client => _client.Value;

    /// <inheritdoc />
    public override string Name => "Amane";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                IsMainConfigPage = true
            },
            new PluginPageInfo { Name = "amaneConfig", EmbeddedResourcePath = "Amane.Emby.Configuration.configPage.js" },
            // 兼容已经被浏览器缓存的旧配置页。
            new PluginPageInfo { Name = "amanejs", EmbeddedResourcePath = "Amane.Emby.Configuration.configPage.js" }
        };
    }
}
