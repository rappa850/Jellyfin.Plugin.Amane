using System.Net;
using Amane.Core;
using Amane.Emby.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amane.Emby.Tests;

public class ProviderTests
{
    [Fact]
    public void Plugin_UsesEmbyAssemblyNameAndEmbeddedPngIcon()
    {
        var assembly = typeof(Plugin).Assembly;
        Assert.Equal("Emby.Plugin.Amane", assembly.GetName().Name);
        Assert.True(typeof(MediaBrowser.Common.Plugins.IHasThumbImage).IsAssignableFrom(typeof(Plugin)));
        var plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        using var image = plugin.GetThumbImage();
        var signature = new byte[8];
        image.ReadExactly(signature);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature);
        Assert.Equal(MediaBrowser.Model.Drawing.ImageFormat.Png, plugin.ThumbImageFormat);
    }

    [Fact]
    public void ExternalIds_AreScopedToItemType()
    {
        var movie = new AmaneMovieExternalId();
        var person = new AmanePersonExternalId();
        Assert.Equal("Amane", movie.Key);
        Assert.Equal("Amane", person.Key);
        Assert.True(movie.Supports(new Movie()));
        Assert.False(movie.Supports(new Person()));
        Assert.True(person.Supports(new Person()));
        Assert.False(person.Supports(new Movie()));
    }

    [Fact]
    public async Task MovieMetadata_BindsPeopleAndBothMovieIds()
    {
        var client = CreateClient();
        var provider = new AmaneMovieProvider(client, NullLogger<AmaneMovieProvider>.Instance);
        var result = await provider.GetMetadata(new MovieInfo { Name = "TEST-001" }, CancellationToken.None);
        Assert.True(result.HasMetadata);
        Assert.Equal("TEST-001 测试", result.Item.Name);
        Assert.Equal("22", result.Item.GetProviderId("AmaneId"));
        var actor = Assert.Single(result.People, p => p.Type == PersonType.Actor);
        Assert.Equal("7", actor.GetProviderId("Amane"));
        Assert.Single(result.People, p => p.Type == PersonType.Director);
    }

    [Fact]
    public async Task Images_IncludeAuthenticatedLocalPosterForDownloadCallback()
    {
        var provider = new AmaneImageProvider(CreateClient(), NullLogger<AmaneImageProvider>.Instance);
        var images = (await provider.GetImages(new Movie { Name = "TEST-001" }, new LibraryOptions(), CancellationToken.None)).ToArray();
        Assert.Contains(images, i => i.Type == ImageType.Primary && i.Url == "http://127.0.0.1:18000/api/resources/hash");
        Assert.All(images, i => Assert.StartsWith("http://127.0.0.1:18000/api/resources/", i.Url));
    }

    [Fact]
    public async Task Identify_LocalPoster_UsesImageTokenQueryWithoutApiToken()
    {
        const string apiToken = "api-secret";
        const string imageToken = "img secret";
        var client = CreateClient(new AmaneSettings { ApiToken = apiToken, ImageToken = imageToken });
        var provider = new AmaneMovieProvider(client, NullLogger<AmaneMovieProvider>.Instance);

        var item = Assert.Single(await provider.GetSearchResults(new MovieInfo { Name = "TEST-001" }, CancellationToken.None));

        // /Images/Remote 是裸 GET：本地裁切海报必须带图片 Token，且不能改写成需 Bearer 的代理地址。
        Assert.Equal("http://127.0.0.1:18000/api/resources/hash?token=img%20secret", item.ImageUrl);
        Assert.DoesNotContain(apiToken, item.ImageUrl);
        Assert.DoesNotContain("/api/resources/proxy", item.ImageUrl!);
    }

    [Fact]
    public async Task Identify_WithoutImageToken_SkipsLocalPosterForAnonymousUrl()
    {
        var provider = new AmaneMovieProvider(
            CreateClient(new AmaneSettings { ApiToken = "api-secret" }),
            NullLogger<AmaneMovieProvider>.Instance);

        var item = Assert.Single(await provider.GetSearchResults(new MovieInfo { Name = "TEST-001" }, CancellationToken.None));

        Assert.Equal("https://images.example/thumb.jpg", item.ImageUrl);
        Assert.DoesNotContain("api-secret", item.ImageUrl);
        Assert.DoesNotContain("token=", item.ImageUrl);
    }

    [Fact]
    public async Task Identify_LocalOnlyWithoutImageToken_OmitsUnauthenticatedResourceUrl()
    {
        var client = CreateClient(
            new AmaneSettings { ApiToken = "api-secret" },
            new Handler(movieJson: """{"items":[{"id":22,"number":"TEST-001","title":"测试","poster_url":"/api/resources/hash"}],"total":1}"""));
        var provider = new AmaneMovieProvider(client, NullLogger<AmaneMovieProvider>.Instance);

        var item = Assert.Single(await provider.GetSearchResults(new MovieInfo { Name = "TEST-001" }, CancellationToken.None));

        Assert.Null(item.ImageUrl);
    }

    [Fact]
    public async Task Identify_ExternalPoster_StaysAnonymousWhileDownloadUsesProxy()
    {
        const string apiToken = "api-secret";
        const string imageToken = "img-secret";
        var client = CreateClient(
            new AmaneSettings { ApiToken = apiToken, ImageToken = imageToken },
            new Handler(movieJson: """{"items":[{"id":22,"number":"TEST-001","title":"测试","poster_url":"https://images.example/poster.jpg"}],"total":1}"""));
        var search = Assert.Single(await new AmaneMovieProvider(client, NullLogger<AmaneMovieProvider>.Instance)
            .GetSearchResults(new MovieInfo { Name = "TEST-001" }, CancellationToken.None));
        var images = (await new AmaneImageProvider(client, NullLogger<AmaneImageProvider>.Instance)
            .GetImages(new Movie { Name = "TEST-001" }, new LibraryOptions(), CancellationToken.None)).ToArray();

        Assert.Equal("https://images.example/poster.jpg", search.ImageUrl);
        Assert.DoesNotContain(apiToken, search.ImageUrl);
        Assert.DoesNotContain(imageToken, search.ImageUrl);
        var primary = Assert.Single(images, i => i.Type == ImageType.Primary);
        Assert.StartsWith("http://127.0.0.1:18000/api/resources/proxy?url=", primary.Url);
        Assert.DoesNotContain("token=", primary.Url);
        Assert.DoesNotContain(apiToken, primary.Url);
        Assert.DoesNotContain(imageToken, primary.Url);
    }

    [Fact]
    public async Task DownloadUrl_IgnoresImageTokenThatSearchPreviewRequires()
    {
        const string apiToken = "api-secret";
        const string imageToken = "img-secret";
        var client = CreateClient(new AmaneSettings { ApiToken = apiToken, ImageToken = imageToken });
        var search = Assert.Single(await new AmaneMovieProvider(client, NullLogger<AmaneMovieProvider>.Instance)
            .GetSearchResults(new MovieInfo { Name = "TEST-001" }, CancellationToken.None));
        var images = (await new AmaneImageProvider(client, NullLogger<AmaneImageProvider>.Instance)
            .GetImages(new Movie { Name = "TEST-001" }, new LibraryOptions(), CancellationToken.None)).ToArray();

        Assert.Equal("http://127.0.0.1:18000/api/resources/hash?token=img-secret", search.ImageUrl);
        var primary = Assert.Single(images, i => i.Type == ImageType.Primary);
        Assert.Equal("http://127.0.0.1:18000/api/resources/hash", primary.Url);
        Assert.Equal(search.ImageUrl, primary.ThumbnailUrl);
        Assert.All(images, image =>
        {
            Assert.DoesNotContain("token=", image.Url);
            Assert.DoesNotContain(apiToken, image.Url);
            Assert.DoesNotContain(imageToken, image.Url);
        });
    }

    [Fact]
    public async Task PersonIdentify_LocalAvatar_UsesImageTokenQueryWithoutApiToken()
    {
        const string apiToken = "api-secret";
        var client = CreateClient(
            new AmaneSettings { ApiToken = apiToken, ImageToken = "img-secret" },
            new Handler(actorJson: """{"items":[{"id":7,"name":"演员","image_urls":["/api/resources/actor"]}],"total":1}"""));
        var provider = new AmanePersonProvider(client, NullLogger<AmanePersonProvider>.Instance);

        var item = Assert.Single(await provider.GetSearchResults(new PersonLookupInfo { Name = "演员" }, CancellationToken.None));

        Assert.Equal("http://127.0.0.1:18000/api/resources/actor?token=img-secret", item.ImageUrl);
        Assert.DoesNotContain(apiToken, item.ImageUrl);
        Assert.DoesNotContain("/api/resources/proxy", item.ImageUrl!);
    }

    [Fact]
    public async Task PersonIdentify_WithoutImageToken_OmitsLocalAvatar()
    {
        var client = CreateClient(
            new AmaneSettings { ApiToken = "api-secret" },
            new Handler(actorJson: """{"items":[{"id":7,"name":"演员","image_urls":["/api/resources/actor","https://images.example/actor.jpg"]}],"total":1}"""));
        var provider = new AmanePersonProvider(client, NullLogger<AmanePersonProvider>.Instance);

        var item = Assert.Single(await provider.GetSearchResults(new PersonLookupInfo { Name = "演员" }, CancellationToken.None));

        // 演员搜索只取首张头像；本地资源未配置图片 Token 时不回退成会 401 的裸地址。
        Assert.Null(item.ImageUrl);
    }

    private static AmaneClient CreateClient(AmaneSettings? settings = null, HttpMessageHandler? handler = null)
        => new(new ClientProvider(handler ?? new Handler()), NullLogger<AmaneClient>.Instance, settings ?? new AmaneSettings());

    private sealed class ClientProvider : IAmaneHttpClientProvider
    {
        private readonly HttpClient _client;

        public ClientProvider(HttpMessageHandler handler) => _client = new HttpClient(handler);

        public HttpClient CreateClient() => _client;
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly string _movieJson;
        private readonly string _actorJson;

        public Handler(string? movieJson = null, string? actorJson = null)
        {
            _movieJson = movieJson ?? """{"items":[{"id":22,"number":"TEST-001","title":"测试","actors":["演员"],"directors":["导演"],"poster_url":"/api/resources/hash","thumb_url":"https://images.example/thumb.jpg","extrafanart":["/api/resources/other","https://images.example/backdrop.jpg"]}],"total":1}""";
            _actorJson = actorJson ?? """{"items":[{"id":7,"name":"演员"}],"total":1}""";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.StartsWith("/api/actors", StringComparison.Ordinal)
                ? _actorJson
                : _movieJson;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
