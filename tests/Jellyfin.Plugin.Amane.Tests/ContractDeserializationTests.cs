using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.Amane.Tests;

/// <summary>
/// T1 契约反序列化测试：用真实保存的 Amane 响应样本校验 DTO 字段映射。
/// </summary>
public class ContractDeserializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static AmaneMetadata LoadMetadataSample()
    {
        var json = File.ReadAllText("amane-response.sample.json");
        var list = JsonSerializer.Deserialize<AmaneListResponse>(json, JsonOptions);
        Assert.NotNull(list);
        Assert.NotEmpty(list.Items);
        return list.Items[0];
    }

    [Fact]
    public void MetadataSample_CoreFields_Deserialize()
    {
        var item = LoadMetadataSample();

        Assert.Equal("IPZZ-822", item.Number);
        Assert.False(string.IsNullOrWhiteSpace(item.Title));
        Assert.False(string.IsNullOrWhiteSpace(item.Plot));
        Assert.Equal("2026-03-05", item.Release);
        Assert.Equal(116, item.Runtime);
        Assert.False(string.IsNullOrWhiteSpace(item.Studio));
        Assert.True(item.Score > 0);
    }

    [Fact]
    public void MetadataSample_Collections_Deserialize()
    {
        var item = LoadMetadataSample();

        Assert.NotNull(item.Tags);
        Assert.NotEmpty(item.Tags);
        Assert.NotNull(item.Actors);
        Assert.Contains("林芽依", item.Actors);
        Assert.NotNull(item.Directors);
        Assert.NotEmpty(item.Directors);
    }

    [Fact]
    public void MetadataSample_ImageUrls_Deserialize()
    {
        var item = LoadMetadataSample();

        Assert.StartsWith("http", item.PosterUrl, StringComparison.Ordinal);
        Assert.StartsWith("http", item.ThumbUrl, StringComparison.Ordinal);
        Assert.NotNull(item.ExtraFanart);
        Assert.NotEmpty(item.ExtraFanart);
        Assert.All(item.ExtraFanart, url => Assert.StartsWith("http", url, StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataSample_OriginalTitle_ExtractedFromRaw()
    {
        var item = LoadMetadataSample();

        var originalTitle = item.GetOriginalTitle();
        Assert.False(string.IsNullOrWhiteSpace(originalTitle));
        // 日文原标题，与润色后的中文标题不同
        Assert.NotEqual(item.Title, originalTitle);
    }

    [Fact]
    public void ActorSample_Deserializes()
    {
        var json = File.ReadAllText("amane-actor.sample.json");
        var list = JsonSerializer.Deserialize<AmaneActorListResponse>(json, JsonOptions);

        Assert.NotNull(list);
        Assert.NotEmpty(list.Items);
        var actor = list.Items[0];
        Assert.Equal("林芽依", actor.Name);
        Assert.True(actor.Id > 0);
        Assert.Equal("2004-08-18", actor.Birthday);
        Assert.NotNull(actor.ImageUrls);
    }

    [Fact]
    public void HealthSample_Deserializes()
    {
        var json = File.ReadAllText("amane-health.sample.json");
        var health = JsonSerializer.Deserialize<AmaneHealthResponse>(json, JsonOptions);

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.False(string.IsNullOrWhiteSpace(health.Version));
    }

    [Fact]
    public void EmptyPayload_DeserializesWithoutThrowing()
    {
        var metadata = JsonSerializer.Deserialize<AmaneMetadata>("{}", JsonOptions);
        Assert.NotNull(metadata);
        Assert.Null(metadata.GetOriginalTitle());

        var actor = JsonSerializer.Deserialize<AmaneActor>("{}", JsonOptions);
        Assert.NotNull(actor);
    }
}
