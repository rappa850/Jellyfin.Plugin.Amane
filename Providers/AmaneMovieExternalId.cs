using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Amane.Providers;

/// <summary>
/// "Amane 电影 Id" 外部 ID：让 Jellyfin 识别对话框出现该输入框。
/// 框内可填 Amane 内部数字 id（精确直取）或番号（走搜索）。
/// </summary>
public class AmaneMovieExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Amane";

    /// <inheritdoc />
    public string Key => AmaneMovieProvider.ProviderIdName;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Movie;
}
