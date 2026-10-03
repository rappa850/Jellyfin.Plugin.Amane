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
    public async Task Identify_CanUseAuthenticatedLocalPosterViaPluginCallback()
    {
        var provider = new AmaneMovieProvider(CreateClient(), NullLogger<AmaneMovieProvider>.Instance);
        var item = Assert.Single(await provider.GetSearchResults(new MovieInfo { Name = "TEST-001" }, CancellationToken.None));
        Assert.Equal("http://127.0.0.1:18000/api/resources/hash", item.ImageUrl);
    }

    private static AmaneClient CreateClient() => new(new ClientProvider(), NullLogger<AmaneClient>.Instance, new AmaneSettings());

    private sealed class ClientProvider : IAmaneHttpClientProvider
    {
        private readonly HttpClient _client = new(new Handler());
        public HttpClient CreateClient() => _client;
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.StartsWith("/api/actors")
                ? """{"items":[{"id":7,"name":"演员"}],"total":1}"""
                : """{"items":[{"id":22,"number":"TEST-001","title":"测试","actors":["演员"],"directors":["导演"],"poster_url":"/api/resources/hash","thumb_url":"https://images.example/thumb.jpg","extrafanart":["/api/resources/other","https://images.example/backdrop.jpg"]}],"total":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
