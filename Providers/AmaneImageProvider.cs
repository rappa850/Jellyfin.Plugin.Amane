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

namespace Jellyfin.Plugin.Amane.Providers;

/// <summary>
/// Amane 图片提供器：封面/背景图 URL 统一改写为 Amane 代理地址后交给 Jellyfin 下载缓存。
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
    public AmaneImageProvider(AmaneClient client, ILogger<AmaneImageProvider> logger)
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
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        // 统一解析：AmaneId 数字直取 → 识别框值（番号/数字/带前缀）→ 名称兜底
        var metadata = await _client.ResolveMetadataAsync(item.ProviderIds, item.Name, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            _logger.LogInformation("Amane 图片解析未识别: {Name}", item.Name);
            return Enumerable.Empty<RemoteImageInfo>();
        }

        var images = new List<RemoteImageInfo>();
        if (!string.IsNullOrWhiteSpace(metadata.PosterUrl))
        {
            // Url 走代理（下载经 GetImageResponse 可带 token）；ThumbnailUrl 给浏览器预览用，只能直出外源 URL
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Url = _client.ToProxyImageUrl(metadata.PosterUrl)!,
                ThumbnailUrl = _client.ToDirectImageUrl(metadata.PosterUrl),
                Type = ImageType.Primary
            });
        }

        var backdrops = new[] { metadata.ThumbUrl }
            .Concat(metadata.ExtraFanart ?? Enumerable.Empty<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u));

        images.AddRange(backdrops.Select(url => new RemoteImageInfo
        {
            ProviderName = Name,
            Url = _client.ToProxyImageUrl(url)!,
            ThumbnailUrl = _client.ToDirectImageUrl(url),
            Type = ImageType.Backdrop
        }));

        return images;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _client.GetImageAsync(url, cancellationToken);
    }
}
