using System.Text.Json;
using Xunit;

namespace Amane.Core.Tests;

public class CurrentApiContractTests
{
    [Fact]
    public void CurrentServerSample_MapsWithoutHostSdk()
    {
        var response = JsonSerializer.Deserialize<AmaneListResponse>(File.ReadAllText("current/metadata.sample.json"));
        var metadata = Assert.Single(response!.Items);
        var view = AmaneMetadataView.From(metadata);
        Assert.Equal("AMANE-TEST-001 Amane Compatibility Test", view.DisplayName);
        Assert.Equal(2026, view.ProductionYear);
        Assert.Equal(8.4f, view.CommunityRating);
        Assert.Equal(TimeSpan.FromMinutes(95).Ticks, view.RunTimeTicks);
        Assert.NotNull(metadata.PosterUrls);
        Assert.NotNull(metadata.ThumbUrls);
        Assert.NotNull(metadata.ExtraFanartUrls);
    }

    [Fact]
    public void MultipleImageUrls_PreserveOrderAndRemoveDuplicates()
    {
        var metadata = new AmaneMetadata
        {
            PosterUrl = "/api/resources/local",
            PosterUrls = ["/api/resources/local", "https://images.example/poster"],
            ThumbUrl = "https://images.example/thumb",
            ThumbUrls = ["https://images.example/thumb", "https://images.example/other"],
            ExtraFanart = ["https://images.example/backdrop"],
            ExtraFanartUrls = new() { ["source"] = ["https://images.example/backdrop", "https://images.example/extra"] }
        };
        Assert.Equal(new[] { "/api/resources/local", "https://images.example/poster" }, metadata.GetPosterUrls());
        Assert.Equal(new[] { "https://images.example/thumb", "https://images.example/other", "https://images.example/backdrop", "https://images.example/extra" }, metadata.GetBackdropUrls());
    }
}
