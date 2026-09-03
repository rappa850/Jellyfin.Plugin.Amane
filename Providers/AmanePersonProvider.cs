using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Amane.Providers;

/// <summary>
/// Amane 人物元数据提供器：Jellyfin 刷新演员（Person）时按名字查询 /api/actors。
/// </summary>
public class AmanePersonProvider : IRemoteMetadataProvider<Person, PersonLookupInfo>, IHasOrder
{
    private readonly AmaneClient _client;
    private readonly ILogger<AmanePersonProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AmanePersonProvider"/> class.
    /// </summary>
    /// <param name="client">Instance of <see cref="AmaneClient"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AmanePersonProvider}"/> interface.</param>
    public AmanePersonProvider(AmaneClient client, ILogger<AmanePersonProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Amane";

    /// <inheritdoc />
    public int Order => 1;

    /// <inheritdoc />
    public async Task<MetadataResult<Person>> GetMetadata(PersonLookupInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Person>();

        // 统一解析：Amane 外部 ID（数字直取/演员名）→ 名称兜底
        var actor = await _client.ResolveActorAsync(info.ProviderIds, info.Name, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            _logger.LogInformation("Amane 演员未识别: {Name}", info.Name);
            return result;
        }

        var person = new Person
        {
            Name = actor.Name ?? info.Name,
            Overview = actor.Overview
        };

        if (DateTime.TryParse(actor.Birthday, CultureInfo.InvariantCulture, DateTimeStyles.None, out var birthday))
        {
            person.PremiereDate = birthday;
            person.ProductionYear = birthday.Year;
        }

        // 人物主图由 Jellyfin 裸 HttpClient 下载（ConvertImageToLocal，无法带 token）：外源 URL 直出，Amane 本地资源不设
        var imageUrl = _client.ToDirectImageUrl(actor.ImageUrls?.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            person.ImageInfos = new[]
            {
                new ItemImageInfo { Path = imageUrl, Type = ImageType.Primary }
            };
        }

        person.SetProviderId(AmaneMovieProvider.ProviderIdName, actor.Id.ToString(CultureInfo.InvariantCulture));

        result.HasMetadata = true;
        result.Item = person;
        result.ResultLanguage = "zh";
        return result;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(PersonLookupInfo searchInfo, CancellationToken cancellationToken)
    {
        // 外部 ID 框值容忍 "Amane:" 前缀；数字 id 精确直取，只回一个结果
        var amaneValue = AmaneClient.NormalizeIdValue(
            searchInfo.ProviderIds.TryGetValue(AmaneMovieProvider.ProviderIdName, out var raw) ? raw : null);

        if (AmaneClient.TryParseInternalId(amaneValue, out var internalId))
        {
            var byId = await _client.GetActorByIdAsync(internalId, cancellationToken).ConfigureAwait(false);
            if (byId is not null)
            {
                return new[] { ToSearchResult(byId, searchInfo.Name) };
            }
        }

        var query = !string.IsNullOrWhiteSpace(amaneValue) ? amaneValue : searchInfo.Name;
        if (string.IsNullOrWhiteSpace(query))
        {
            return Enumerable.Empty<RemoteSearchResult>();
        }

        var actors = await _client.SearchActorsAsync(query, 5, cancellationToken).ConfigureAwait(false);
        return actors.Select(actor => ToSearchResult(actor, searchInfo.Name));
    }

    private RemoteSearchResult ToSearchResult(AmaneActor actor, string? fallbackName)
    {
        var searchResult = new RemoteSearchResult
        {
            Name = actor.Name ?? fallbackName ?? string.Empty,
            SearchProviderName = Name,
            // 搜索弹窗缩略图由浏览器直连（无法带 token）：外源 URL 直出，Amane 本地资源为 null
            ImageUrl = _client.ToDirectImageUrl(actor.ImageUrls?.FirstOrDefault())
        };

        if (actor.Id > 0)
        {
            searchResult.ProviderIds[AmaneMovieProvider.ProviderIdName] = actor.Id.ToString(CultureInfo.InvariantCulture);
        }

        return searchResult;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _client.GetImageAsync(url, cancellationToken);
    }
}
