using System.Globalization;

namespace Amane.Core;

/// <summary>共用的字段映射规则，宿主适配器只构造自己的契约对象。</summary>
public sealed class AmaneMetadataView
{
    private AmaneMetadataView() { }
    public string? DisplayName { get; private init; }
    public string? OriginalTitle { get; private init; }
    public string? Overview { get; private init; }
    public DateTime? PremiereDate { get; private init; }
    public int? ProductionYear => PremiereDate?.Year;
    public string[] Studios { get; private init; } = [];
    public string[] Genres { get; private init; } = [];
    public long? RunTimeTicks { get; private init; }
    public float? CommunityRating { get; private init; }

    /// <summary>标题、日期、时长与五分制评分的唯一映射入口。</summary>
    public static AmaneMetadataView From(AmaneMetadata metadata) => new()
    {
        DisplayName = FormatDisplayName(metadata),
        OriginalTitle = metadata.GetOriginalTitle(),
        Overview = metadata.Plot,
        PremiereDate = DateTime.TryParse(metadata.Release, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
        Studios = string.IsNullOrWhiteSpace(metadata.Studio) ? [] : [metadata.Studio],
        Genres = metadata.Tags?.ToArray() ?? [],
        RunTimeTicks = metadata.Runtime is > 0 ? TimeSpan.FromMinutes(metadata.Runtime.Value).Ticks : null,
        CommunityRating = metadata.Score is > 0 ? Math.Min(metadata.Score.Value * 2f, 10f) : null
    };

    public static string? FormatDisplayName(AmaneMetadata metadata) =>
        !string.IsNullOrWhiteSpace(metadata.Number) && !string.IsNullOrWhiteSpace(metadata.Title)
            ? $"{metadata.Number} {metadata.Title}" : metadata.Title ?? metadata.Number;
}
