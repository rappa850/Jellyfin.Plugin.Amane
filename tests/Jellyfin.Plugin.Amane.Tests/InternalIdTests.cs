using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.Amane.Tests;

/// <summary>
/// "Amane 电影 Id" 绑定相关测试：详情响应 DTO、内部 id 解析。
/// </summary>
public class InternalIdTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void DetailSample_Deserializes()
    {
        var json = File.ReadAllText("amane-detail.sample.json");
        var detail = JsonSerializer.Deserialize<AmaneMetadataDetailResponse>(json, JsonOptions);

        Assert.NotNull(detail?.Metadata);
        Assert.Equal("IPZZ-822", detail.Metadata.Number);
        Assert.True(detail.Metadata.Id > 0);
        Assert.False(string.IsNullOrWhiteSpace(detail.Metadata.Title));
    }

    [Theory]
    [InlineData("22", 22)]
    [InlineData("1042", 1042)]
    public void TryParseInternalId_AcceptsPositiveInteger(string value, int expected)
    {
        Assert.True(AmaneClient.TryParseInternalId(value, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("Amane:IPZZ-822", "IPZZ-822")]
    [InlineData("amane: 22 ", "22")]
    [InlineData("IPZZ-822", "IPZZ-822")]
    public void NormalizeIdValue_StripsPrefixAndWhitespace(string input, string expected)
    {
        Assert.Equal(expected, AmaneClient.NormalizeIdValue(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeIdValue_EmptyToNull(string? input)
    {
        Assert.Null(AmaneClient.NormalizeIdValue(input));
    }

    [Theory]
    [InlineData("IPZZ-822")]   // 番号不是数字 id
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseInternalId_RejectsNonId(string? value)
    {
        Assert.False(AmaneClient.TryParseInternalId(value, out _));
    }
}
