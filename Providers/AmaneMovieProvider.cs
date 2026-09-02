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

    /// <summary>
    /// Amane 内部数字 id 在 Jellyfin ProviderIds 中的键名（识别成功后自动写入，用于精确直取）。
    /// </summary>
    public const string InternalIdProviderIdName = "AmaneId";

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

        // 统一解析：AmaneId 数字直取 → 识别框值（番号/数字/带前缀）→ 名称兜底
        var metadata = await _client.ResolveMetadataAsync(info.ProviderIds, info.Name, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            _logger.LogInformation("Amane 未识别: {Name}", info.Name);
            return result;
        }

        _logger.LogDebug("Amane 命中: {Name} -> {Number} (id {Id})", info.Name, metadata.Number, metadata.Id);

        result.HasMetadata = true;
        result.Item = MapToMovie(metadata);
        result.ResultLanguage = "zh";

        foreach (var actor in metadata.Actors ?? Enumerable.Empty<string>())
        {
            // 内联补头像：经 AmaneClient 的演员缓存（TTL 可配置），重复演员不会重复请求
            var actorInfo = await _client.LookupActorAsync(actor, cancellationToken).ConfigureAwait(false);
            var personInfo = new PersonInfo
            {
                Name = actor,
                Type = PersonKind.Actor,
                ImageUrl = _client.ToProxyImageUrl(actorInfo?.ImageUrls?.FirstOrDefault())
            };

            // 扫库自动绑定：演员命中时把 Amane 演员 id 随 PersonInfo 写入，人物条目创建即带外部 ID
            if (actorInfo is { Id: > 0 })
            {
                personInfo.ProviderIds[ProviderIdName] = actorInfo.Id.ToString(CultureInfo.InvariantCulture);
            }

            result.AddPerson(personInfo);
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
        // 识别框值容忍 "Amane:" 前缀；数字 id 精确直取，只回一个结果
        var amaneValue = AmaneClient.NormalizeIdValue(
            searchInfo.ProviderIds.TryGetValue(ProviderIdName, out var raw) ? raw : null);

        if (AmaneClient.TryParseInternalId(amaneValue, out var internalId))
        {
            var byId = await _client.GetByIdAsync(internalId, cancellationToken).ConfigureAwait(false);
            if (byId is not null)
            {
                return new[] { ToSearchResult(byId) };
            }
        }

        var query = !string.IsNullOrWhiteSpace(amaneValue) ? amaneValue : searchInfo.Name;

        if (string.IsNullOrWhiteSpace(query))
        {
            return Enumerable.Empty<RemoteSearchResult>();
        }

        var items = await _client.SearchAsync(query, 10, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            _logger.LogInformation("Amane 搜索无结果: {Query}", query);
        }

        return items.Select(ToSearchResult);
    }

    private RemoteSearchResult ToSearchResult(AmaneMetadata item)
    {
        var searchResult = new RemoteSearchResult
        {
            Name = FormatDisplayName(item),
            SearchProviderName = Name,
            ImageUrl = _client.ToProxyImageUrl(item.PosterUrl)
        };

        if (DateTime.TryParse(item.Release, CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            searchResult.ProductionYear = releaseDate.Year;
        }

        if (!string.IsNullOrWhiteSpace(item.Number))
        {
            searchResult.ProviderIds[ProviderIdName] = item.Number;
        }

        if (item.Id > 0)
        {
            searchResult.ProviderIds[InternalIdProviderIdName] = item.Id.ToString(CultureInfo.InvariantCulture);
        }

        return searchResult;
    }

    /// <summary>
    /// 显示名统一格式：番号 + 空格 + 润色标题，如 "IPZZ-822 纯真可怜的…"；缺番号或标题时回退单值。
    /// 搜索结果（识别对话框）与入库标题共用此格式。
    /// </summary>
    internal static string? FormatDisplayName(AmaneMetadata metadata)
    {
        return !string.IsNullOrWhiteSpace(metadata.Number) && !string.IsNullOrWhiteSpace(metadata.Title)
            ? $"{metadata.Number} {metadata.Title}"
            : metadata.Title ?? metadata.Number;
    }

    internal static Movie MapToMovie(AmaneMetadata metadata)
    {
        var movie = new Movie
        {
            Name = FormatDisplayName(metadata),
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

        // 双键存储：番号（稳定可读，识别框显示值）+ 内部数字 id（精确直取快速路径）
        if (!string.IsNullOrWhiteSpace(metadata.Number))
        {
            movie.SetProviderId(ProviderIdName, metadata.Number);
        }

        if (metadata.Id > 0)
        {
            movie.SetProviderId(InternalIdProviderIdName, metadata.Id.ToString(CultureInfo.InvariantCulture));
        }

        return movie;
    }
}
