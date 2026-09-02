using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Amane.Providers;

/// <summary>
/// Amane 影片元数据提供器：把 Jellyfin 的名称/番号转发给 Amane，原样映射返回字段。
/// </summary>
public class AmaneMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    /// <summary>
    /// Amane 外部 id 在 Jellyfin ProviderIds 中的键名。
    /// </summary>
    public const string ProviderIdName = "Amane";

    private readonly AmaneClient _client;
    private readonly ILogger<AmaneMovieProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AmaneMovieProvider"/> class.
    /// </summary>
    /// <param name="client">Instance of <see cref="AmaneClient"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AmaneMovieProvider}"/> interface.</param>
    public AmaneMovieProvider(AmaneClient client, ILogger<AmaneMovieProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Amane";

    /// <inheritdoc />
    public int Order => 1;

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _client.GetImageAsync(url, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Movie>();

        var query = info.ProviderIds.TryGetValue(ProviderIdName, out var number) && !string.IsNullOrWhiteSpace(number)
            ? number
            : info.Name;

        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var metadata = await _client.LookupAsync(query, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            _logger.LogDebug("Amane 未命中: {Query}", query);
            return result;
        }

        result.HasMetadata = true;
        result.Item = MapToMovie(metadata);
        result.ResultLanguage = "zh";

        foreach (var actor in metadata.Actors ?? Enumerable.Empty<string>())
        {
            // 内联补头像：经 AmaneClient 的 6 小时缓存，重复演员不会重复请求
            var actorInfo = await _client.LookupActorAsync(actor, cancellationToken).ConfigureAwait(false);
            result.AddPerson(new PersonInfo
            {
                Name = actor,
                Type = PersonKind.Actor,
                ImageUrl = actorInfo?.ImageUrls?.FirstOrDefault()
            });
        }

        foreach (var director in metadata.Directors ?? Enumerable.Empty<string>())
        {
            result.AddPerson(new PersonInfo { Name = director, Type = PersonKind.Director });
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        var query = searchInfo.ProviderIds.TryGetValue(ProviderIdName, out var number) && !string.IsNullOrWhiteSpace(number)
            ? number
            : searchInfo.Name;

        if (string.IsNullOrWhiteSpace(query))
        {
            return Enumerable.Empty<RemoteSearchResult>();
        }

        var items = await _client.SearchAsync(query, 10, cancellationToken).ConfigureAwait(false);
        return items.Select(item =>
        {
            var searchResult = new RemoteSearchResult
            {
                Name = item.Title ?? item.Number ?? string.Empty,
                SearchProviderName = Name,
                ImageUrl = item.PosterUrl
            };

            if (DateTime.TryParse(item.Release, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
            {
                searchResult.ProductionYear = releaseDate.Year;
            }

            if (!string.IsNullOrWhiteSpace(item.Number))
            {
                searchResult.ProviderIds[ProviderIdName] = item.Number;
            }

            return searchResult;
        });
    }

    internal static Movie MapToMovie(AmaneMetadata metadata)
    {
        var movie = new Movie
        {
            Name = metadata.Title ?? metadata.Number,
            OriginalTitle = metadata.GetOriginalTitle(),
            Overview = metadata.Plot
        };

        if (DateTime.TryParse(metadata.Release, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            movie.PremiereDate = releaseDate;
            movie.ProductionYear = releaseDate.Year;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Studio))
        {
            movie.SetStudios(new[] { metadata.Studio });
        }

        if (metadata.Tags is { Count: > 0 })
        {
            movie.Genres = metadata.Tags.ToArray();
        }

        if (metadata.Runtime is > 0)
        {
            movie.RunTimeTicks = TimeSpan.FromMinutes(metadata.Runtime.Value).Ticks;
        }

        // 来源站为 5 分制，换算到 Jellyfin 的 10 分制
        if (metadata.Score is > 0)
        {
            movie.CommunityRating = Math.Min(metadata.Score.Value * 2f, 10f);
        }

        if (!string.IsNullOrWhiteSpace(metadata.Number))
        {
            movie.SetProviderId(ProviderIdName, metadata.Number);
        }

        return movie;
    }
}
