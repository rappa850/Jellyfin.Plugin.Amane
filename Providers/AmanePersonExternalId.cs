using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Amane.Providers;

/// <summary>
/// "Amane 人物 Id" 外部 ID：让 Jellyfin 人物编辑对话框出现该输入框。
/// 框内可填 Amane 内部数字 id（精确直取）或演员名（走搜索）。人物没有识别对话框，此处为手动绑定入口。
/// </summary>
public class AmanePersonExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Amane";

    /// <inheritdoc />
    public string Key => AmaneMovieProvider.ProviderIdName;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => ExternalIdMediaType.Person;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Person;
}
