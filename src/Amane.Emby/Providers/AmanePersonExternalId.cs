using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Amane.Core;
namespace Amane.Emby.Providers;
/// <summary>Emby 外部 ID 绑定入口，按条目类型限定。</summary>
public sealed class AmanePersonExternalId : IExternalId
{
    public string Name => "Amane";
    public string Key => AmaneProviderIds.ProviderIdName;
    public string UrlFormatString => string.Empty;
    public bool Supports(IHasProviderIds item) => item is Person;
}
