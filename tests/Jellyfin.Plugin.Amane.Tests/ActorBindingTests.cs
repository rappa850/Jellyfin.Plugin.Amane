using System.Text.Json;
using Jellyfin.Plugin.Amane.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Providers;
using Xunit;

namespace Jellyfin.Plugin.Amane.Tests;

/// <summary>
/// 演员 ID 绑定相关测试：演员详情 DTO（无包装直返）、人物外部 ID 契约。
/// </summary>
public class ActorBindingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void ActorDetailSample_DeserializesUnwrapped()
    {
        var json = File.ReadAllText("amane-actor-detail.sample.json");
        var actor = JsonSerializer.Deserialize<AmaneActor>(json, JsonOptions);

        Assert.NotNull(actor);
        Assert.Equal(6, actor.Id);
        Assert.Equal("林芽依", actor.Name);
        Assert.Contains("Mei Hayashi", actor.Aliases);
    }

    [Fact]
    public void PersonExternalId_Contract()
    {
        var externalId = new AmanePersonExternalId();

        Assert.Equal("Amane", externalId.ProviderName);
        Assert.Equal(AmaneMovieProvider.ProviderIdName, externalId.Key);
        Assert.Equal(ExternalIdMediaType.Person, externalId.Type);
        Assert.True(externalId.Supports(new Person { Name = "林芽依" }));
        Assert.False(externalId.Supports(new Movie { Name = "IPZZ-822" }));
    }

    [Fact]
    public void MovieExternalId_Contract()
    {
        var externalId = new AmaneMovieExternalId();

        Assert.Equal("Amane", externalId.ProviderName);
        Assert.Equal(AmaneMovieProvider.ProviderIdName, externalId.Key);
        Assert.Equal(ExternalIdMediaType.Movie, externalId.Type);
        Assert.True(externalId.Supports(new Movie { Name = "IPZZ-822" }));
        Assert.False(externalId.Supports(new Person { Name = "林芽依" }));
    }
}
