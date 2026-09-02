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

        if (string.IsNullOrWhiteSpace(info.Name))
        {
            return result;
        }

        var actor = await _client.LookupActorAsync(info.Name, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            _logger.LogDebug("Amane 演员未命中: {Name}", info.Name);
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

        var imageUrl = actor.ImageUrls?.FirstOrDefault();
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
        if (string.IsNullOrWhiteSpace(searchInfo.Name))
        {
            return Enumerable.Empty<RemoteSearchResult>();
        }

        var actor = await _client.LookupActorAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return Enumerable.Empty<RemoteSearchResult>();
        }

        var searchResult = new RemoteSearchResult
        {
            Name = actor.Name ?? searchInfo.Name,
            SearchProviderName = Name,
            ImageUrl = actor.ImageUrls?.FirstOrDefault()
        };
        searchResult.ProviderIds[AmaneMovieProvider.ProviderIdName] = actor.Id.ToString(CultureInfo.InvariantCulture);

        return new[] { searchResult };
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _client.GetImageAsync(url, cancellationToken);
    }
}
