using Amane.Core;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Amane.Emby.Providers;

/// <summary>
/// Amane 图片提供器：封面/背景图 URL 统一改写为 Amane 代理地址后交给 Emby 下载缓存。
/// </summary>
public class AmaneImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly AmaneClient _client;
    private readonly ILogger<AmaneImageProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AmaneImageProvider"/> class.
    /// </summary>
    /// <param name="client">Instance of <see cref="AmaneClient"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AmaneImageProvider}"/> interface.</param>
    public AmaneImageProvider(MediaBrowser.Model.Logging.ILogManager logManager)
        : this(Plugin.Instance!.Client, new EmbyLogger<AmaneImageProvider>(logManager)) { }

    internal AmaneImageProvider(AmaneClient client, ILogger<AmaneImageProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Amane";

    /// <inheritdoc />
    public int Order => 1;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Movie;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new[] { ImageType.Primary, ImageType.Backdrop };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, MediaBrowser.Model.Configuration.LibraryOptions libraryOptions, CancellationToken cancellationToken)
    {
        // 统一解析：AmaneId 数字直取 → 识别框值（番号/数字/带前缀）→ 名称兜底
        var metadata = await _client.ResolveMetadataAsync(item.ProviderIds, item.Name, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            _logger.LogInformation("Amane 图片解析未识别: {Name}", item.Name);
            return Enumerable.Empty<RemoteImageInfo>();
        }

        var images = new List<RemoteImageInfo>();
        void AddImages(IEnumerable<string> urls, ImageType type)
        {
            foreach (var url in urls)
            {
                var downloadUrl = _client.ToProxyImageUrl(url);
                // 自动下载经 GetImageResponse 附加鉴权；缩略图仍受宿主裸请求能力限制。
                if (string.IsNullOrWhiteSpace(downloadUrl)) continue;
                images.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = downloadUrl,
                    ThumbnailUrl = _client.ToDirectImageUrl(url),
                    Type = type
                });
            }
        }
        AddImages(metadata.GetPosterUrls(), ImageType.Primary);
        AddImages(metadata.GetBackdropUrls(), ImageType.Backdrop);

        return images;
    }

    /// <inheritdoc />
    public Task<MediaBrowser.Common.Net.HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return EmbyImageResponse.DownloadAsync(_client, _client.ToProxyImageUrl(url)!, cancellationToken);
    }
}
