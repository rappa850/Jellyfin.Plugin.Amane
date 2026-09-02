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

namespace Jellyfin.Plugin.Amane.Providers;

/// <summary>
/// Amane 图片提供器：封面/背景图以 URL 形式交给 Jellyfin 自行下载缓存。
/// </summary>
public class AmaneImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly AmaneClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="AmaneImageProvider"/> class.
    /// </summary>
    /// <param name="client">Instance of <see cref="AmaneClient"/>.</param>
    public AmaneImageProvider(AmaneClient client)
    {
        _client = client;
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
            return Enumerable.Empty<RemoteImageInfo>();
        }

        var images = new List<RemoteImageInfo>();
        if (!string.IsNullOrWhiteSpace(metadata.PosterUrl))
        {
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Url = metadata.PosterUrl,
                Type = ImageType.Primary
            });
        }

        var backdrops = new[] { metadata.ThumbUrl }
            .Concat(metadata.ExtraFanart ?? Enumerable.Empty<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u));

        images.AddRange(backdrops.Select(url => new RemoteImageInfo
        {
            ProviderName = Name,
            Url = url!,
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
