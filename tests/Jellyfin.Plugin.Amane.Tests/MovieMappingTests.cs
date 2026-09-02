using System.Text.Json;
using Jellyfin.Plugin.Amane.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Amane.Tests;

/// <summary>
/// T2 字段映射测试：Amane DTO → Jellyfin Movie 契约对象。
/// </summary>
public class MovieMappingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static AmaneMetadata LoadMetadataSample()
    {
        var json = File.ReadAllText("amane-response.sample.json");
        return JsonSerializer.Deserialize<AmaneListResponse>(json, JsonOptions)!.Items[0];
    }

    [Fact]
    public void MapToMovie_MapsCoreFields()
    {
        var movie = AmaneMovieProvider.MapToMovie(LoadMetadataSample());

        Assert.False(string.IsNullOrWhiteSpace(movie.Name));
        Assert.False(string.IsNullOrWhiteSpace(movie.Overview));
        Assert.False(string.IsNullOrWhiteSpace(movie.OriginalTitle));
        Assert.Equal(new DateTime(2026, 3, 5), movie.PremiereDate);
        Assert.Equal(2026, movie.ProductionYear);
        Assert.Contains("アイデアポケット", movie.Studios);
        Assert.Equal(TimeSpan.FromMinutes(116).Ticks, movie.RunTimeTicks);
    }

    [Fact]
    public void MapToMovie_MapsGenresAndProviderId()
    {
        var movie = AmaneMovieProvider.MapToMovie(LoadMetadataSample());

        Assert.NotEmpty(movie.Genres);
        Assert.Equal("IPZZ-822", movie.GetProviderId(AmaneMovieProvider.ProviderIdName));
        Assert.Equal("22", movie.GetProviderId(AmaneMovieProvider.InternalIdProviderIdName));
    }

    [Fact]
    public void MapToMovie_NameIsNumberPrefixed()
    {
        var movie = AmaneMovieProvider.MapToMovie(LoadMetadataSample());

        Assert.StartsWith("IPZZ-822 ", movie.Name);
    }

    [Fact]
    public void MapToMovie_ConvertsScoreFromFiveToTenScale()
    {
        var item = LoadMetadataSample();
        var movie = AmaneMovieProvider.MapToMovie(item);

        Assert.NotNull(movie.CommunityRating);
        Assert.Equal(Math.Min(item.Score!.Value * 2f, 10f), movie.CommunityRating.Value, 3);
    }

    [Fact]
    public void MapToMovie_ToleratesMissingOptionalFields()
    {
        var movie = AmaneMovieProvider.MapToMovie(new AmaneMetadata { Number = "ABC-001" });

        Assert.Equal("ABC-001", movie.Name);
        Assert.Null(movie.PremiereDate);
        Assert.Null(movie.CommunityRating);
        Assert.Null(movie.RunTimeTicks);
    }
}
