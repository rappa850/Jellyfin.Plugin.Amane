# Emby 元数据/图片/人物 Provider API 实测取证（task-2）

> 调查人：`emby-provider-api`　调查对象：**Emby Server 4.10.0.40**（官方发布二进制，net8.0）
> 方法：真实二进制反编译 + 反射 dump + 真实 `dotnet build` 编译验证。**未使用「Jellyfin 源自 Emby 所以接口类似」的推断**；凡属推断均已标注。

---

## 0. 最重要的 8 条结论（先读这里）

| # | 结论 | 证据等级 |
|---|------|---------|
| 1 | `MediaBrowser.Controller.Providers.IRemoteMetadataProvider<TItemType, TLookupInfoType>` **存在**，泛型参数就是这 2 个。它同时继承 `IRemoteSearchProvider<TLookupInfoType>`，因此**必须实现 4 个成员**：`Name`、`GetMetadata`、`GetSearchResults`、`GetImageResponse` | 反射 + 编译通过 |
| 2 | Emby **没有** `PersonKind`；人物类型枚举是 `MediaBrowser.Model.Entities.PersonType`（`Actor = 0` …），注意命名空间与 Jellyfin 不同 | 反射（全量 dump 中 `PersonKind` 0 命中） |
| 3 | `IExternalId` 的成员是 `Key` / `Name` / `UrlFormatString` / `Supports`。**没有 `ProviderName`，也没有 `Type`**（Jellyfin 的 `IExternalId` 有 `ProviderName` 和 `Type`） | 反射 + 反编译 |
| 4 | **Emby 不会给 ProviderId 自动拼 `"Id"` 后缀。** 全量反编译（Emby.Providers / Emby.Api / Emby.Server.Implementations）中不存在 `Key + "Id"` 这类拼接；`ExternalIdInfo.Key` 原样就是 `ProviderIds` 字典的键，网页端也用 `idInfo.Key` 直接作键 | 反编译（3 个程序集）+ Emby 网页端源码 |
| 5 | Emby **没有** `IPluginServiceRegistrator`（Jellyfin 有）。配置页用 `MediaBrowser.Model.Plugins.IHasWebPages` + `PluginPageInfo`，插件基类是 `MediaBrowser.Common.Plugins.BasePlugin` / `BasePlugin<TConfigurationType>` | 反射 |
| 6 | 优先级机制是 `IHasOrder { int Order { get; } }`，被 `Emby.Providers.Manager.ProviderManager` 真实消费；另有库级配置顺序 `LibraryOptions.MetadataFetcherOrder` / `ImageFetcherOrder` / `LocalMetadataReaderOrder` | 反射 + 反编译 |
| 7 | **图片链路不是单一入口**：自动刷新与 Identify 缩略图会回调插件的 `GetImageResponse`（可带 token）；但图片选择器的**预览**与**手动下载**由 Emby 用自己的 HTTP 客户端直连 URL，**不经过插件** → 这两条路径拿不到插件附加的 Bearer 头。详见 §6.3 | 反编译（决定性） |
| 8 | Emby 的 Identify 流程与 Jellyfin **不同**：搜索缩略图是 `GET /Items/RemoteSearch/Image?imageUrl=…&ProviderName=…&api_key=…`（服务端代理，会给插件机会加 auth），而**不是**把 `ImageUrl` 直接塞进 `<img>`。所以 Jellyfin 那边的「图片 URL 双轨制」问题在 Emby 上**不是同一个问题** | 反编译 + Emby 网页端源码 |

---

## 1. 取证环境（重要：本机初始无 .NET SDK）

任务开始时本机**没有** `dotnet`：

```
Get-Command dotnet            -> 未识别
C:\Program Files\dotnet       -> 不存在
C:\Users\rappa\.dotnet        -> 不存在
HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions -> 空
C:\Program Files (x86)\dotnet -> 不存在
（C: / D: 全盘搜 dotnet.exe 均无结果）
```

因此本次调查**自行搭建**了工具链（全部落在仓库之外）：

| 组件 | 版本 / 路径 | 用途 |
|------|-------------|------|
| .NET SDK | **10.0.401** → `C:\Users\rappa\AppData\Local\Temp\amane-emby-poc-tools\dotnet\dotnet.exe`（`dotnet-install.ps1 -Channel 10.0`） | 编译 + 反射 |
| ilspycmd | **11.1.0.9782** → 同目录 `tools\ilspycmd.exe` | 反编译真实 DLL |
| Emby Server | **4.10.0.40** netcore 包（219,448,236 B，`embyserver-netcore_4.10.0.40.zip`） | 真实服务器二进制 |
| NuGet 包 | `MediaBrowser.Common` / `MediaBrowser.Server.Core` **4.10.0.24-beta2** | 官方 SDK 参考程序集 |

调用方式（每次开新 shell 都要设）：

```powershell
$env:DOTNET_ROOT = 'C:\Users\rappa\AppData\Local\Temp\amane-emby-poc-tools\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --version    # 10.0.401
```

> 本机 `HTTP_PROXY=http://127.0.0.1:7897`，NuGet / GitHub / aka.ms 均可达。

---

## 2. 参考程序集来源（含哈希，可复现）

### 2.1 主源：Emby Server 4.10.0.40 官方发布二进制

来源：`https://github.com/MediaBrowser/Emby.Releases/releases/download/4.10.0.40/embyserver-netcore_4.10.0.40.zip`
（GitHub API `MediaBrowser/Emby.Releases` → `releases/latest`，`tag_name = 4.10.0.40`）
运行时：解压后 `system/EmbyServer.runtimeconfig.json` → `"tfm": "net8.0"`，依赖 `Microsoft.NETCore.App 8.0.0` + `Microsoft.AspNetCore.App 8.0.0`。

| 文件 | 大小 | AssemblyVersion | SHA256 |
|------|------|-----------------|--------|
| `system\MediaBrowser.Controller.dll` | 600,240 | 4.10.0.40 | `60FB4EEA2247FAFE65706E842A4703FCDF5F8DF4EBA545B6F2ABF4213EED2B58` |
| `system\MediaBrowser.Model.dll` | 501,936 | 4.10.0.40 | `0770D919359C38F286BC5EAD2682B040329DDE8A413E3ECBAEBD9F270B1684E2` |
| `system\MediaBrowser.Common.dll` | 63,664 | 4.10.0.40 | `50B5FF542B5394C5F618082D96045448F9C9C36CDFF1332A5A211135ED39D86B` |

### 2.2 次源：官方 NuGet 参考程序集（4.10.0.24-beta2，netstandard2.0）

**导航要点**：nuget.org 搜索 `MediaBrowser.Controller` 返回 **0 命中**——`MediaBrowser.Controller.dll` 并不单独发包，它**打包在 `MediaBrowser.Server.Core` 里**。`MediaBrowser.Model` 也不是独立包，它在 `MediaBrowser.Common` 里。

- `MediaBrowser.Common` 包内容：`lib/netstandard2.0/{MediaBrowser.Common.dll, MediaBrowser.Model.dll, Emby.Media.Model.dll, Emby.Web.GenericEdit.dll}`，**每个 dll 都附带同名 `.xml` 文档**。
- `MediaBrowser.Server.Core` 包内容：`lib/netstandard2.0/{MediaBrowser.Controller.dll, Emby.Naming.dll}` + `.xml`。

| 文件 | AssemblyVersion | SHA256 |
|------|-----------------|--------|
| `mediabrowser.server.core.4.10.0.24-beta2\lib\netstandard2.0\MediaBrowser.Controller.dll` | 4.10.0.24 | `4A18EED85AA2446897EBA191DCA75010389C99E82D0517C948467071581FAB7D` |
| `mediabrowser.common.4.10.0.24-beta2\lib\netstandard2.0\MediaBrowser.Common.dll` | 4.10.0.24 | `F49B677C15B8038E0BBF512127300CC786D7FE07C24C71DA3B80040AADB4C747` |
| `mediabrowser.common.4.10.0.24-beta2\lib\netstandard2.0\MediaBrowser.Model.dll` | 4.10.0.24 | `36A5B33F8F5BCADAC8316CECF8CC3094B7FEB275BBB134705E0D40BEA220F44E` |

### 2.3 ⚠️ 重要方法论陷阱：XML 文档不是完整 API 面

`MediaBrowser.Controller.xml` 只有 **950 条 member**，其 `T:` 条目里**根本没有** `IRemoteMetadataProvider<TItemType,TLookupInfoType>`、`IRemoteSearchProvider`、`IExternalId`、`IMetadataProvider<TItemType>`。

这**不代表**这些类型不存在——它们确实存在（见 §3）。结论：**「XML 文档里没有」不能推断「类型不存在」，必须反射真实 DLL。**

---

## 3. 真实签名（反射 dump，非推断）

dump 工具：自写 `ApiDump`（`MetadataLoadContext` + `PathAssemblyResolver`，只读元数据、不执行代码），
覆盖 `MediaBrowser.Controller` / `MediaBrowser.Model` / `MediaBrowser.Common` / `Emby.Media.Model` 全部 385 个探测程序集。
输出：`C:\Users\rappa\AppData\Local\Temp\amane-emby-poc\api-dump-real-emby-4.10.0.40.txt`（920,261 B / 14,579 行）。

### 3.1 元数据提供接口族（`MediaBrowser.Controller.Providers`）

```csharp
public interface IMetadataProvider
{
    string Name { get; }
}

public interface IMetadataProvider<TItemType> : IMetadataProvider { }        // 标记接口，无成员

public interface IRemoteMetadataProvider : IMetadataProvider { }             // 标记接口，无成员

public interface IRemoteSearchProvider : IMetadataProvider
{
    Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken);
}

public interface IRemoteSearchProvider<TLookupInfoType>
    : IMetadataProvider, IRemoteSearchProvider
{
    Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        TLookupInfoType searchInfo, CancellationToken cancellationToken);
}

public interface IRemoteMetadataProvider<TItemType, TLookupInfoType>
    : IMetadataProvider,
      IMetadataProvider<TItemType>,
      IRemoteMetadataProvider,
      IRemoteSearchProvider,
      IRemoteSearchProvider<TLookupInfoType>
{
    Task<MetadataResult<TItemType>> GetMetadata(
        TLookupInfoType info, CancellationToken cancellationToken);
}
```

**因此实现 `IRemoteMetadataProvider<Movie, MovieInfo>` 必须写全 4 个成员**：

```csharp
public string Name { get; }                                              // 来自 IMetadataProvider
public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken ct);
public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info, CancellationToken ct);
public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken ct);
```

> 注意第 4 个：`GetImageResponse` 声明在**非泛型** `IRemoteSearchProvider` 上，但被泛型接口继承，所以**必须实现**。
> 这一点与「只需要 GetMetadata + GetSearchResults」的直觉不同，是最容易漏掉/编译不过的成员。

另有「带 options」的变体（4.10 新增，非必需）：

```csharp
public interface IRemoteMetadataProviderWithOptions<TItemType, TLookupInfoType>
    : IMetadataProvider, IMetadataProvider<TItemType>, IRemoteMetadataProvider,
      IRemoteMetadataProvider<TItemType, TLookupInfoType>,
      IRemoteSearchProvider, IRemoteSearchProvider<TLookupInfoType>
{
    Task<MetadataResult<TItemType>> GetMetadata(
        RemoteMetadataFetchOptions<TLookupInfoType> options, CancellationToken cancellationToken);
}

public sealed class RemoteMetadataFetchOptions<TLookupInfoType>
{
    public RemoteMetadataFetchOptions();
    public IDirectoryService DirectoryService { get; set; }
    public TLookupInfoType SearchInfo { get; set; }
}
```

专题接口（按需，非必需）：`ISeriesMetadataProvider.GetAllEpisodes(SeriesInfo, ct)`、
`IBoxSetMetadataProvider.GetAllItems(ItemLookupInfo, ct)`、`IPersonMetadataProvider.GetCredits(PersonLookupInfo, ct)`。

### 3.2 `MetadataResult<T>` / `BaseMetadataResult`

```csharp
public abstract class BaseMetadataResult
{
    protected BaseMetadataResult();

    public void AddPerson(PersonInfo p);
    public void ClearCachedMediaSource();
    public FileSystemMetadata GetFileSystemInfo(IDirectoryService directoryService);
    public MediaSourceInfo GetMediaSource(BaseItem[] collectionFolders, LibraryOptions libraryOptions);
    public void ResetPeople();
    public RemoteSearchResult ToRemoteSearchResult(string searchProviderName);

    protected abstract BaseItem BaseItem { get; }

    public List<ChapterInfo> Chapters { get; set; }
    public List<MediaStream> DetectedExternalAudioTracks { get; set; }
    public List<MediaStream> DetectedExternalSubtitles { get; set; }
    public bool HasMetadata { get; set; }                       // ✅ 存在于基类
    public List<LocalImageInfo> Images { get; set; }
    public LinkedChild[] ListItems { get; set; }
    public MediaStream[] MediaStreams { get; set; }
    public List<PersonInfo> People { get; set; }                // ✅ 可 Add 到 People
    public string Provider { get; set; }
    public bool QueriedById { get; set; }
    public string ResultLanguage { get; set; }                  // ✅
    public string SearchImageUrl { get; set; }
    public string ThumbnailUrl { get; set; }
    public List<UserItemData> UserDataList { get; set; }
}

public sealed class MetadataResult<T> : BaseMetadataResult
{
    public MetadataResult();
    protected override BaseItem BaseItem { get; }   // 实现基类抽象成员
    public T Item { get; set; }                     // ✅
}
```

- `HasMetadata` / `Item` / `ResultLanguage` / `People` / `AddPerson` ✅ **全部存在**。
- `MetadataResult<T>` 上**没有** `RemoteSearchResults` 成员（整个 `BaseMetadataResult` + `MetadataResult<T>` 都没有）→ 该成员**不存在**。
- `Item` 是 `{ get; set; }`；`BaseItem` 是**只读**、且是 `protected`（反射看到 `protected abstract`，`MetadataResult<T>` 里 override 后仍 `get` only）。

### 3.3 优先级机制

```csharp
public interface IHasOrder
{
    int Order { get; }          // 默认语义：无实现时按 0 处理
}

public enum MetadataProviderPriority
{
    First = 1, Second = 2, Third = 3, Fourth = 4, Fifth = 5, Last = 999
}
```

- `IHasOrder` 由 `Emby.Providers.Manager.ProviderManager` 真实消费：

```csharp
// Emby.Providers.Manager.ProviderManager（反编译原文）
private int GetOrder(IImageProvider provider)
{
    if (!(provider is IHasOrder hasOrder)) { return 0; }
    return hasOrder.Order;
}

private int GetDefaultOrder(IMetadataProvider provider)
{
    if (provider is IHasOrder hasOrder) { return hasOrder.Order; }
    return 0;
}
```

- `MetadataProviderPriority` 在 4.10 里只被 `IImageEnhancer.Priority` 使用（反射可见 `abstract MetadataProviderPriority Priority { get; }` 在 `IImageEnhancer` 上），**不是**元数据 provider 的排序手段。
- 另有**库级配置顺序**（比 `IHasOrder` 更强，用户在库里可改）：

```csharp
private static int GetConfiguredOrder(string itemTypeName, IMetadataProvider provider, LibraryOptions libraryOptions)
{
    if (provider is ILocalMetadataProvider)
    {
        int num = Array.IndexOf(libraryOptions.LocalMetadataReaderOrder ?? Array.Empty<string>(), provider.Name);
        if (num != -1) { return num; }
    }
    if (provider is IRemoteMetadataProvider)
    {
        if (string.Equals(provider.Name, "MusicBrainz", StringComparison.OrdinalIgnoreCase)) { return -1; }
        int num2 = Array.IndexOf(libraryOptions.GetTypeOptions(itemTypeName)?.MetadataFetcherOrder ?? Array.Empty<string>(), provider.Name);
        if (num2 != -1) { return num2; }
    }
    return 100;   // 未列出 = 100
}
```

### 3.4 图片提供

```csharp
public interface IImageProvider
{
    bool Supports(BaseItem item);
    string Name { get; }
}

public interface IRemoteImageProvider : IImageProvider
{
    Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken);
    Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken);
    IEnumerable<ImageType> GetSupportedImages(BaseItem item);
}

// 4.10 新增的 options 变体（可选实现）
public interface IRemoteImageProviderWithOptions : IImageProvider, IRemoteImageProvider
{
    Task<IEnumerable<RemoteImageInfo>> GetImages(
        RemoteImageFetchOptions options, CancellationToken cancellationToken);
}
```

**`IRemoteImageProvider` 存在**，4 个成员如上（`Supports`、`Name`、`GetSupportedImages`、`GetImages`、`GetImageResponse` 共 5 个）。

```csharp
public class RemoteImageInfo
{
    public RemoteImageInfo();
    public double? CommunityRating { get; set; }
    public string DisplayLanguage { get; set; }
    public int? Height { get; set; }
    public string Language { get; set; }
    public string ProviderName { get; set; }
    public RatingType RatingType { get; set; }
    public string ThumbnailUrl { get; set; }
    public ImageType Type { get; set; }
    public string Url { get; set; }
    public int? VoteCount { get; set; }
    public int? Width { get; set; }
}

public class ImageProviderInfo
{
    public ImageProviderInfo();
    public string Name { get; set; }
    public ImageType[] SupportedImages { get; set; }
}

public class RemoteImageQuery
{
    public bool EnableSeriesImages { get; set; }
    public ImageType? ImageType { get; set; }
    public bool IncludeAllLanguages { get; set; }
    public bool IncludeDisabledProviders { get; set; }
    public string ProviderName { get; set; }
}

public enum ImageType      // MediaBrowser.Model.Entities
{
    Primary = 0, Art = 1, Backdrop = 2, Banner = 3, Logo = 4, Thumb = 5,
    Disc = 6, Box = 7, Screenshot = 8, Menu = 9, Chapter = 10,
    BoxRear = 11, Thumbnail = 12, LogoLight = 13, LogoLightColor = 14
}
```

图片下载回调返回类型：

```csharp
public sealed class HttpResponseInfo : IDisposable      // MediaBrowser.Common.Net
{
    public HttpResponseInfo(IDisposable[] disposables);
    public HttpResponseInfo();
    public void Dispose();
    public Stream Content { get; set; }
    public long? ContentLength { get; set; }
    public string ContentType { get; set; }
    public Dictionary<string, string> Headers { get; set; }
    public string ResponseUrl { get; set; }
    public HttpStatusCode StatusCode { get; set; }
    public string TempFilePath { get; set; }
}
```

> 与 Jellyfin 的差异：Emby 用 `IRemoteImageProvider.GetImageResponse` 返回 `HttpResponseInfo`（Jellyfin 返回 `HttpResponseMessage`）。

### 3.5 External ID

```csharp
public interface IExternalId
{
    bool Supports(IHasProviderIds item);
    string Key { get; }              // = ProviderIds 字典的键（原样，不拼后缀）
    string Name { get; }             // 显示名（Jellyfin 叫 ProviderName）
    string UrlFormatString { get; }
}

// 可选附加接口
public interface IHasWebsite { string Website { get; } }
public interface IHasSupportedExternalIdentifiers { string[] GetSupportedExternalIdentifiers(); }
```

**真实实现（Emby 自带，反编译原文）**——这是「Key 到底是什么」最硬的证据：

```csharp
// Emby.Providers.ExternalIds.ImdbExternalId
public sealed class ImdbExternalId : IExternalId, IHasWebsite
{
    public string Name => "IMDb";
    public string Key => MetadataProviders.Imdb.ToString();      // => "Imdb"
    public string UrlFormatString => "https://www.imdb.com/title/{0}";
    public string Website => "https://www.imdb.com";
    public bool Supports(IHasProviderIds item) { /* Movie/Series/Episode/Trailer/LiveTvProgram */ }
}

// Emby.Providers.ExternalIds.ImdbPersonExternalId
public sealed class ImdbPersonExternalId : IExternalId, IHasWebsite
{
    public string Name => "IMDb";
    public string Key => MetadataProviders.Imdb.ToString();      // => "Imdb"
    public string Website => "https://www.imdb.com";
    public string UrlFormatString => "https://www.imdb.com/name/{0}";
    public bool Supports(IHasProviderIds item) => item is Person;
}
```

→ `Name` = 人类可读标签，`Key` = ProviderIds 键。**两者可以不同**（"IMDb" vs "Imdb"）。

`ExternalIdInfo`（`/Items/{Id}/ExternalIdInfos` 的返回元素）：

```csharp
public class ExternalIdInfo
{
    public bool IsSupportedAsIdentifier { get; set; }
    public string Key { get; set; }
    public string Name { get; set; }
    public string UrlFormatString { get; set; }
    public string Website { get; set; }
}
```

ProviderIds 容器与扩展方法：

```csharp
public interface IHasProviderIds
{
    ProviderIdDictionary ProviderIds { get; set; }
}

public class ProviderIdDictionary : Dictionary<string, string>   // MediaBrowser.Model.Entities
{
    public ProviderIdDictionary();
    public ProviderIdDictionary(Dictionary<string, string> source);
    public ProviderIdDictionary(IEnumerable<KeyValuePair<string, string>> source);
    public void Import(Dictionary<string, string> source);
    public void Import(IEnumerable<KeyValuePair<string, string>> source);
}

// 扩展方法类（不是接口成员！）
public static class ProviderIdsExtensions
{
    public static string   GetProviderId(this IHasProviderIds instance, MetadataProviders provider);
    public static string   GetProviderId(this IHasProviderIds instance, string name);
    public static string[] GetProviderIds(this IHasProviderIds instance, string name);
    public static bool     HasProviderId(this IHasProviderIds instance, MetadataProviders provider);
    public static bool     HasProviderId(this IHasProviderIds instance, string provider);
    public static void     SetProviderId(this IHasProviderIds instance, string name, string value);
    public static void     SetProviderId(this IHasProviderIds instance, string name, string[] values);
    public static void     SetProviderId(this IHasProviderIds instance, MetadataProviders provider, string value);
    public static void     SetProviderId(this IHasProviderIds instance, MetadataProviders provider, string[] value);
}
```

`MetadataProviders` 枚举**没有**通用/自定义项，只有内置源（`Gamesdb=1, Imdb=2, Tmdb=3, Tvdb=4, …`）。
→ 自定义插件必须走 **字符串重载** `SetProviderId(item, "Amane", value)`。
另注：`BaseItem` 还有 `SetProviderIds(ProviderIdDictionary dict)` 与 `ClearProviderIds()`。

**是否自动拼 `"Id"` 后缀？→ 不拼。** 三条独立证据：

1. 反编译 `ProviderManager.GetExternalIdInfos` 原文，`Key` 原样映射：

```csharp
// Emby.Providers.Manager.ProviderManager
return from i in GetExternalIds(item)
       select new ExternalIdInfo
       {
           Name = i.Name,
           Key  = i.Key,                     // ← 原样，无拼接
           UrlFormatString = i.UrlFormatString,
           IsSupportedAsIdentifier = (baseItem == null || Enumerable.Contains(supportedExternalIds, i.Key, StringComparer.OrdinalIgnoreCase)),
           Website = (i as IHasWebsite)?.Website?.TrimEnd('/')
       };
```

2. 反编译 `ProviderManager` 里用 `RemoteSearchResult.ProviderIds` 的键时也是原样：

```csharp
RemoteSearchResult remoteSearchResult = resultList.FirstOrDefault((RemoteSearchResult i) =>
    i.ProviderIds.Any((KeyValuePair<string, string> p) =>
        IsUniquelyIdentifiableProviderId(p.Key, typeof(TItemType))
        && string.Equals(result.GetProviderId(p.Key), p.Value, StringComparison.OrdinalIgnoreCase)));
```

3. Identify 落库路径 `ApplySearchResult` 也是原样拷贝，且**全量反编译 3 个程序集未出现任何 `Key + "Id"` 形式拼接**：

```csharp
// Emby.Providers.Manager.MetadataService
private static void ApplySearchResult(ItemLookupInfo lookupInfo, RemoteSearchResult result)
{
    lookupInfo.ProviderIds = result.ProviderIds;   // ← 原样
    lookupInfo.Name = result.Name;
    lookupInfo.Year = result.ProductionYear;
}
```

4. Emby 自带网页端（`dashboard-ui/modules/itemidentifier/itemidentifier.js`）构建 ProviderIds 请求体时用的就是 `idInfo.Key`：

```js
html += '<input is="emby-input" class="txtLookupId" data-providerkey="' + idInfo.Key + '" label="' + idLabel + '"/>';
...
lookupInfo.ProviderIds[txtLookupId[i].getAttribute("data-providerkey")] = value;
```

> **对 Amane 的直接含义**：现有 Jellyfin 插件写 `Amane` + `AmaneId` 双键是因为 Jellyfin 的机制；在 Emby 上**不需要**双键——`IExternalId.Key` 写 `"Amane"`，ProviderIds 就存 `"Amane"`，Identify 框和 `GetMetadata` 都用 `"Amane"` 取值。

### 3.6 Person / PersonInfo / 头像字段

```csharp
// MediaBrowser.Controller.Entities（注意：不在 Model.Entities）
public sealed class PersonInfo : IHasProviderIds
{
    public PersonInfo();
    public bool IsType(PersonType type);
    public LinkedItemInfo ToLinkedItemInfo();
    public override string ToString();

    public Guid Guid { get; set; }
    public long Id { get; set; }
    public ItemImageInfo[] ImageInfos { get; set; }
    public string ImageUrl { get; set; }                 // ✅ 存在，类型 string
    public long ItemId { get; set; }
    public string Name { get; set; }
    public ProviderIdDictionary ProviderIds { get; set; } // ✅
    public string Role { get; set; }
    public PersonType Type { get; set; }                  // ✅ 是 PersonType，不是 PersonKind
}

public sealed class Person : BaseItem, IHasFolderGrouping, IItemByName,
    IHasLookupInfo<ItemLookupInfo>, IHasLookupInfo<PersonLookupInfo>, IHasProviderIds
{
    public Person();
    // …（Person 是 BaseItem 子类，不是 PersonInfo 子类）
}

// 人物类型枚举
public enum PersonType     // MediaBrowser.Model.Entities
{
    Actor = 0, Director = 1, Writer = 2, Producer = 3,
    GuestStar = 4, Composer = 5, Conductor = 6, Lyricist = 7
}
```

- `PersonKind`：**全量 dump 0 命中** → Emby 4.10 **没有** `PersonKind`（Jellyfin 有）。
- `PersonInfo.ImageUrl` 是 `string`，**被 Emby 自身代码消费**（反编译）：

```csharp
// Emby.Providers.Manager.ProviderUtils — 合并 people 时搬 ImageUrl
if (string.IsNullOrWhiteSpace(item.ImageUrl)) { item.ImageUrl = personInfo.ImageUrl; }

// Emby.Providers.Manager.MetadataService — 本地演员子目录图片会写入 ImageUrl（此处是文件路径语义）
person.ImageUrl = fileSystemMetadata.FullName;

// Emby.Server.Implementations.Data.SqliteItemRepository — 落库时把 ImageUrl 变成 Person 的 Primary 图
long personId = GetPersonId(personInfo.ToLinkedItemInfo(), db, personInfo.ProviderIds, personInfo.ImageUrl, null);
//   → GetPersonId(...) => CreateItemByNameId<Person>(db, itemInfo, ..., imageUrl, ...)
//   → UpdateValuesIfNeeded(...) { if (!string.IsNullOrEmpty(imageUrl)) list.Add("Images"); ... }
```

→ `PersonInfo.ImageUrl` 会被持久化为 Person 实体的 Primary 图引用。**具体抓取时机与鉴权行为未经实机验证**（见 §7）。

### 3.7 插件基类 / 配置页

```csharp
public abstract class BasePlugin : IPlugin, IPluginAssembly      // MediaBrowser.Common.Plugins
{
    protected BasePlugin();
    public virtual PluginInfo GetPluginInfo();
    public string GetPluginPageUrl(string name);
    public virtual void OnUninstalling();
    public void SetAttributes(string assemblyFilePath, string dataFolderPath, Version assemblyVersion);
    public void SetId(Guid assemblyId);

    public string AssemblyFilePath { get; }
    public string DataFolderPath { get; }
    public string Description { get; }        // virtual
    public Guid Id { get; }                   // virtual
    public abstract string Name { get; }      // ← 唯一 abstract
    public Version Version { get; }
}

public abstract class BasePlugin<TConfigurationType> : BasePlugin, IHasPluginConfiguration
{
    protected BasePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer);
    public virtual PluginInfo GetPluginInfo();
    public virtual void SaveConfiguration();
    public void SetStartupInfo(Action<string> directoryCreateFn);
    public virtual void UpdateConfiguration(BasePluginConfiguration configuration);

    public IApplicationPaths ApplicationPaths { get; }
    public string AssemblyFileName { get; }
    public TConfigurationType Configuration { get; set; }
    public string ConfigurationFileName { get; }
    public string ConfigurationFilePath { get; }
    public Type ConfigurationType { get; }
    public bool IsFirstRun { get; }
    public IXmlSerializer XmlSerializer { get; }
}

public interface IPlugin
{
    PluginInfo GetPluginInfo();
    void OnUninstalling();
    string AssemblyFilePath { get; }
    string DataFolderPath { get; }
    string Description { get; }
    Guid Id { get; }
    string Name { get; }
    Version Version { get; }
}
```

配置页机制（Emby 侧）：

```csharp
public interface IHasWebPages           // MediaBrowser.Model.Plugins —— 与 Jellyfin 同名
{
    IEnumerable<PluginPageInfo> GetPages();
}

public class PluginPageInfo
{
    public string DisplayName { get; set; }
    public string EmbeddedResourcePath { get; set; }
    public bool EnableInMainMenu { get; set; }
    public bool EnableInUserMenu { get; set; }
    public string FeatureId { get; set; }
    public bool IsMainConfigPage { get; set; }
    public string MenuIcon { get; set; }
    public string MenuSection { get; set; }
    public string Name { get; set; }
}

// 旧式自定义配置页（Controller 层）
public interface IPluginConfigurationPage
{
    Stream GetHtmlStream();
    ConfigurationPageType ConfigurationPageType { get; }   // PluginConfiguration = 0, None = 1
    string Name { get; }
    IPlugin Plugin { get; }
}

// 4.10 新式「简单 UI」插件基类（声明式配置页，无需写 html/js）
public abstract class BasePluginSimpleUI<TOptionType> : BasePlugin, IPlugin, IPluginAssembly, IHasUIPages
{
    protected BasePluginSimpleUI(IApplicationHost applicationHost);
    protected TOptionType GetOptions();
    public virtual PluginInfo GetPluginInfo();
    protected virtual TOptionType OnBeforeShowUI(TOptionType options);
    protected virtual void OnCreatePageInfo(PluginPageInfo pageInfo);
    protected virtual void OnOptionsSaved(TOptionType options);
    protected virtual bool OnOptionsSaving(TOptionType options);
    protected void SaveOptions(TOptionType options);
}

public interface IServerEntryPoint : IDisposable      // MediaBrowser.Controller.Plugins，插件入口点
{
    // 见官方文档 §8
}
```

**关键差异：Emby 没有 `IPluginServiceRegistrator`**（全量 dump 0 命中；`IRequiresRegistration` 是付费插件注册，语义不同）。

### 3.8 搜索 / Identify 相关 DTO 与 API

```csharp
public class RemoteSearchResult : IHasProviderIds
{
    public RemoteSearchResult();
    public RemoteSearchResult AlbumArtist { get; set; }
    public RemoteSearchResult[] Artists { get; set; }
    public string DisambiguationComment { get; set; }
    public DateTimeOffset? EndDate { get; set; }
    public string GameSystem { get; set; }
    public string ImageUrl { get; set; }                 // ✅ string
    public int? IndexNumber { get; set; }
    public int? IndexNumberEnd { get; set; }
    public string Name { get; set; }
    public string OriginalTitle { get; set; }
    public string Overview { get; set; }
    public int? ParentIndexNumber { get; set; }
    public PersonType? PersonType { get; set; }
    public DateTimeOffset? PremiereDate { get; set; }
    public int? ProductionYear { get; set; }
    public ProviderIdDictionary ProviderIds { get; set; }
    public string Role { get; set; }
    public string SearchProviderName { get; set; }       // ← Emby 会覆盖它，见下
    public int? SortIndexNumber { get; set; }
    public int? SortParentIndexNumber { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public string ThumbnailUrl { get; set; }
    public string Type { get; set; }
}

public class RemoteSearchQuery<T>
{
    public bool IncludeDisabledProviders { get; set; }
    public long ItemId { get; set; }
    public string[] Providers { get; set; }
    public T SearchInfo { get; set; }
    public string SearchProviderName { get; set; }
}

public class ItemLookupInfo : IHasProviderIds
{
    public ItemLookupInfo();
    public ItemLookupInfo(ItemLookupInfo clone);
    public bool EnableAdultMetadata { get; set; }
    public int? IndexNumber { get; set; }
    public bool IsAutomated { get; set; }
    public string MetadataCountryCode { get; set; }
    public string MetadataLanguage { get; set; }
    public CultureDto[] MetadataLanguages { get; set; }
    public string Name { get; set; }
    public int? ParentIndexNumber { get; set; }
    public string Path { get; set; }
    public DateTimeOffset? PremiereDate { get; set; }
    public ProviderIdDictionary ProviderIds { get; set; }
    public int? Year { get; set; }
}

public sealed class MovieInfo     : ItemLookupInfo, IHasProviderIds { public MovieInfo(); }
public sealed class PersonLookupInfo : ItemLookupInfo, IHasProviderIds { public PersonLookupInfo(); }
```

⚠️ **`SearchProviderName` 由 Emby 覆盖，插件不要自己设**（反编译原文）：

```csharp
// Emby.Providers.Manager.ProviderManager
private static async Task<IEnumerable<RemoteSearchResult>> GetSearchResults<TLookupType>(
    IRemoteSearchProvider<TLookupType> provider, TLookupType searchInfo, CancellationToken cancellationToken)
    where TLookupType : ItemLookupInfo
{
    RemoteSearchResult[] array = (await provider.GetSearchResults(searchInfo, cancellationToken)).ToArray();
    foreach (RemoteSearchResult r in array) { r.SearchProviderName = provider.Name; }   // ← 无条件覆盖为插件 Name
    return array;
}
```

> 后果：`GetSearchResults` 返回的结果里 `SearchProviderName` 会被改成 `provider.Name`。Identify 弹窗随后拿 `SearchProviderName` 去反向定位 provider（见 §6.2），所以**插件 `Name` 必须与 `GetImageResponse` 的 provider 匹配**，`Name` 是全局唯一标识，改它等于改协议。

### 3.9 媒体库扫描 / 刷新相关（`IProviderManager` 真实签名）

```csharp
public interface IProviderManager
{
    void CacheImage(BaseItem item, ItemImageInfo image, LibraryOptions libraryOptions, IDirectoryService directoryService);
    void CacheImages(BaseItem item);
    void DeleteCachedImages(BaseItem item, ItemImageInfo[] images, LibraryOptions libraryOptions, IDirectoryService directoryService);
    void DequeueRefresh(long itemId);
    Task<RemoteSearchResult[]> GetAllEpisodes(Series series, LibraryOptions libraryOptions, CancellationToken cancellationToken);
    Task<RemoteSearchResult[]> GetAllItems(BoxSet boxSet, LibraryOptions libraryOptions, CancellationToken cancellationToken);
    MetadataPluginSummary[] GetAllMetadataPlugins();
    Task<IEnumerable<RemoteImageInfo>> GetAvailableRemoteImages(BaseItem item, LibraryOptions libraryOptions, RemoteImageQuery query, CancellationToken cancellationToken);
    Task<IEnumerable<RemoteImageInfo>> GetAvailableRemoteImages(BaseItem item, LibraryOptions libraryOptions, RemoteImageQuery query, IDirectoryService directoryService, CancellationToken cancellationToken);
    Task<RemoteSearchResult[]> GetCredits(Person item, CancellationToken cancellationToken);
    LibraryOptions GetDefaultLibraryOptions(string contentType);
    IMetadataProvider[] GetEnabledMetadataProviders(BaseItem item, LibraryOptions libraryOptions);
    IEnumerable<ExternalIdInfo> GetExternalIdInfos(IHasProviderIds item);
    IEnumerable<ExternalUrl> GetExternalUrls(BaseItem item);
    LibraryOptionsResult GetLibraryOptionsInfo(string contentType);
    double? GetRefreshProgress(long id);
    List<Tuple<long, MetadataRefreshOptions>> GetRefreshQueue();
    IEnumerable<ImageProviderInfo> GetRemoteImageProviderInfo(BaseItem item, LibraryOptions libraryOptions);
    Task<IEnumerable<RemoteSearchResult>> GetRemoteSearchResults<TItemType, TLookupType>(RemoteSearchQuery<TLookupType> searchInfo, CancellationToken cancellationToken);
    Task<IEnumerable<RemoteSearchResult>> GetRemoteSearchResults<TItemType, TLookupType>(RemoteSearchQuery<TLookupType> searchInfo, BaseItem referenceItem, CancellationToken cancellationToken);
    Task<HttpResponseInfo> GetSearchImage(string providerName, string url, CancellationToken cancellationToken);
    bool IsUniquelyIdentifiableProviderId(string name, BaseItem item);
    bool IsUniquelyIdentifiableProviderId(string name);
    string NormalizeNameForMetadataSearch(string name);
    void OnRefreshComplete(BaseItem item, BaseItem[] collectionFolders);
    void OnRefreshProgress(BaseItem item, double progress, BaseItem[] collectionFolders);
    void OnRefreshStart(BaseItem item, BaseItem[] collectionFolders);
    void QueueRefresh(long itemId, MetadataRefreshOptions options, RefreshPriority priority);
    void QueueRefresh(long itemId, MetadataRefreshOptions options, RefreshPriority priority, bool dequeueIfAlreadyQueued);
    Task RefreshFullItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken);
    Task<ItemUpdateType> RefreshSingleItem(BaseItem item, MetadataRefreshOptions options, BaseItem[] collectionFolders, LibraryOptions libraryOptions, CancellationToken cancellationToken);
    Task SaveImage(BaseItem item, LibraryOptions libraryOptions, string url, ImageType type, int? imageIndex, long[] generatedFromItemIds, IDirectoryService directoryService, bool updateImageCache, CancellationToken cancellationToken);
    Task SaveImage(BaseItem item, LibraryOptions libraryOptions, Stream source, ReadOnlyMemory<char> mimeType, ImageType type, int? imageIndex, long[] generatedFromItemIds, IDirectoryService directoryService, bool updateImageCache, CancellationToken cancellationToken);
    Task SaveImage(BaseItem item, LibraryOptions libraryOptions, string source, ReadOnlyMemory<char> mimeType, ImageType type, int? imageIndex, bool? saveLocallyWithMedia, long[] generatedFromItemIds, IDirectoryService directoryService, bool updateImageCache, CancellationToken cancellationToken);
    Task SaveMetadata(BaseItem item, ItemUpdateType updateType);
    Task SaveMetadata(BaseItem item, LibraryOptions libraryOptions, ItemUpdateType updateType);
    Task SaveMetadata(BaseItem item, ItemUpdateType updateType, IEnumerable<string> savers);
    Task WaitForRefreshQueue(IProgress<double> progress, CancellationToken cancellationToken);

    IImageProvider[] ImageProviders { get; }
    bool IsProcessingRefreshQueue { get; }

    event EventHandler<GenericEventArgs<RefreshProgressInfo>> RefreshStarted;
    event EventHandler<GenericEventArgs<RefreshProgressInfo>> RefreshCompleted;
    event EventHandler<GenericEventArgs<RefreshProgressInfo>> RefreshProgress;
}
```

`LibraryOptions`（`MediaBrowser.Model.Configuration`）里的顺序配置字段（Emby 网页端"元数据下载器"排序 UI 的落点，未逐字段全 dump，仅记录已被 `GetConfiguredOrder` 读取的成员）：`LocalMetadataReaderOrder`、`MetadataFetcherOrder`、`ImageFetcherOrder`（经 `GetTypeOptions(itemTypeName)` 取每类型配置）。

---

## 4. PoC：真实编译验证

### 4.1 路径（仓库之外，符合隔离要求）

```
C:\Users\rappa\AppData\Local\Temp\amane-emby-poc\
├─ plugin-src\                     # 共享源码
│   ├─ Plugin.cs                   # BasePlugin + IHasWebPages 入口类
│   ├─ AmaneMovieProvider.cs       # IRemoteMetadataProvider<Movie, MovieInfo> + IHasOrder
│   ├─ AmanePersonProvider.cs      # IRemoteMetadataProvider<Person, PersonLookupInfo>
│   ├─ AmaneImageProvider.cs       # IRemoteImageProvider
│   └─ AmaneExternalId.cs          # IExternalId
├─ Amane.Emby.Poc.Net8\            # 变体 A：net8.0 + 真实服务器 DLL
├─ Amane.Emby.Poc.NetStandard\     # 变体 B：netstandard2.0 + 官方 NuGet 参考程序集
├─ ApiDump\                        # 反射 dump 工具
├─ api-dump-real-emby-4.10.0.40.txt
├─ build-variantA.log
└─ build-variantB.log
```

> 仓库内**零新增文件**（除本笔记）。仓库根 `Jellyfin.Plugin.Amane.csproj` 的默认通配会扫到任何 `*.cs`，所以 PoC 一律建在 `%TEMP%` 下。

### 4.2 变体 A csproj（net8.0 ← 真实 Emby Server 4.10.0.40 DLL）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <Nullable>disable</Nullable>
    <LangVersion>latest</LangVersion>
    <AssemblyName>Amane.Emby.Poc.Net8</AssemblyName>
    <RootNamespace>Amane.Emby.Poc</RootNamespace>
    <EmbyRefDir Condition="'$(EmbyRefDir)' == ''">C:\Users\rappa\AppData\Local\Temp\amane-emby-poc-tools\dl\emby-server-4.10.0.40\system</EmbyRefDir>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="..\plugin-src\**\*.cs" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="MediaBrowser.Controller">
      <HintPath>$(EmbyRefDir)\MediaBrowser.Controller.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="MediaBrowser.Model">
      <HintPath>$(EmbyRefDir)\MediaBrowser.Model.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="MediaBrowser.Common">
      <HintPath>$(EmbyRefDir)\MediaBrowser.Common.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

### 4.3 插件入口类（真实可编译源码）

```csharp
using System;
using System.Collections.Generic;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;

namespace Amane.Emby.Poc
{
    /// <summary>
    /// Emby 插件入口。基类型：MediaBrowser.Common.Plugins.BasePlugin
    /// 配置页机制：MediaBrowser.Model.Plugins.IHasWebPages（与 Jellyfin 同名）
    /// </summary>
    public class AmanePlugin : BasePlugin, IHasWebPages
    {
        public override string Name => "Amane";

        public override Guid Id => Guid.Parse("9f2e4a6b-7c1d-4e3f-8a5b-0d9c2e1f4a7b");

        public override string Description => "Amane metadata proxy (PoC)";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "Amane",
                    DisplayName = "Amane",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    EnableInMainMenu = false,
                    IsMainConfigPage = false,
                    MenuSection = "server",
                    MenuIcon = "settings"
                }
            };
        }
    }
}
```

### 4.4 电影远程元数据提供器（关键成员，全部真实 API）

```csharp
public class AmaneMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    public string Name => "Amane";
    public int Order => 1;

    public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Movie>
        {
            HasMetadata = true,
            ResultLanguage = "ja",
            Item = new Movie { Name = "ABC-123 Title", Overview = "plot from Amane", ProductionYear = 2024 }
        };

        var movie = result.Item;
        movie.SetProviderId("Amane", "ABC-123");     // 扩展方法，字符串重载
        movie.SetProviderId("AmaneId", "4242");

        result.AddPerson(new PersonInfo
        {
            Name = "Actress Name",
            Type = PersonType.Actor,
            Role = "Role Name",
            ImageUrl = "http://127.0.0.1:18000/api/resources/proxy?url=x"
        });
        result.People.Add(new PersonInfo { Name = "Second", Type = PersonType.Director });

        return Task.FromResult(result);
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        IEnumerable<RemoteSearchResult> results = new[]
        {
            new RemoteSearchResult
            {
                Name = "ABC-123 Title",
                ImageUrl = "http://127.0.0.1:18000/api/resources/x",
                Overview = "plot",
                ProductionYear = 2024,
                SearchProviderName = Name,           // 会被 Emby 覆盖为 Name，实测确认
                ProviderIds = new ProviderIdDictionary { { "Amane", "ABC-123" } }
            }
        };
        return Task.FromResult(results);
    }

    // 声明在非泛型 IRemoteSearchProvider 上，但被泛型接口继承 → 必须实现
    public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseInfo
        {
            ContentType = "image/jpeg",
            StatusCode = System.Net.HttpStatusCode.OK
        });
}
```

其余源码见 `plugin-src\AmaneImageProvider.cs`、`AmanePersonProvider.cs`、`AmaneExternalId.cs`。

### 4.5 `dotnet build` 原文输出

**变体 A（net8.0 ← 真实 Emby Server 4.10.0.40 DLL）**

```
> dotnet build "C:\Users\rappa\AppData\Local\Temp\amane-emby-poc\Amane.Emby.Poc.Net8\Amane.Emby.Poc.Net8.csproj" -c Release -v m --no-incremental

  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
  Amane.Emby.Poc.Net8 -> C:\Users\rappa\AppData\Local\Temp\amane-emby-poc\Amane.Emby.Poc.Net8\bin\Release\net8.0\Amane.Emby.Poc.Net8.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:01.68
```

**变体 B（netstandard2.0 ← 官方 NuGet `MediaBrowser.Common` + `MediaBrowser.Server.Core` 4.10.0.24-beta2）**

```
> dotnet build "C:\Users\rappa\AppData\Local\Temp\amane-emby-poc\Amane.Emby.Poc.NetStandard\Amane.Emby.Poc.NetStandard.csproj" -c Release -v m --no-incremental

  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
  Amane.Emby.Poc.NetStandard -> C:\Users\rappa\AppData\Local\Temp\amane-emby-poc\Amane.Emby.Poc.NetStandard\bin\Release\netstandard2.0\Amane.Emby.Poc.NetStandard.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:01.71
```

**结论**：两种 TFM（`net8.0` 与 `netstandard2.0`）**都能编译通过**，且 `net8.0` 变体是**一次成功、零编译错误**——这本身就是对 §3 全部签名正确性的强校验（接口成员集合、属性类型、命名空间全名全部被编译器核对过）。

编译器实际使用的引用（变体 A，`-v n` 摘录）：

```
/reference:…\emby-server-4.10.0.40\system\MediaBrowser.Common.dll
/reference:…\emby-server-4.10.0.40\system\MediaBrowser.Controller.dll
/reference:…\emby-server-4.10.0.40\system\MediaBrowser.Model.dll
/reference:C:\Users\rappa\.nuget\packages\microsoft.netcore.app.ref\8.0.31\ref\net8.0\…
```

> 注意：SDK 10 编译 `net8.0` 时自动从 NuGet 拉取了 `Microsoft.NETCore.App.Ref 8.0.31` 目标包——这也是「本机只需要 .NET 10 SDK 即可编 net8.0 插件」的实证。

---

## 5. 反射 dump 命令（可复现）

```powershell
$tools = 'C:\Users\rappa\AppData\Local\Temp\amane-emby-poc-tools'
$env:DOTNET_ROOT = "$tools\dotnet"; $env:PATH = "$tools\dotnet;$env:PATH"

dotnet "$tools\..\amane-emby-poc\ApiDump\bin\Release\net10.0\ApiDump.dll" `
  "$tools\dl\emby-server-4.10.0.40\system" `
  "$tools\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.12\ref\net10.0" `
  "$tools\dotnet\packs\Microsoft.AspNetCore.App.Ref\10.0.12\ref\net10.0" | Out-File api-dump.txt -Encoding utf8
```

`ApiDump` 用 `MetadataLoadContext`（NuGet `System.Reflection.MetadataLoadContext 9.0.0`）+ 385 个探测程序集，
**只读元数据不执行代码**，因此对 net8.0 目标程序集无运行时依赖问题。
`Assembly.LoadFrom` 方案未采用（避免加载/执行风险与依赖解析噪声）。

反编译命令：

```powershell
& "$tools\tools\ilspycmd.exe" -o out\Emby.Providers        -p "$sys\Emby.Providers.dll"
& "$tools\tools\ilspycmd.exe" -o out\Emby.Api              -p "$sys\Emby.Api.dll"
& "$tools\tools\ilspycmd.exe" -o out\Emby.Server.Impl      -p "$sys\Emby.Server.Implementations.dll"
& "$tools\tools\ilspycmd.exe" -t Emby.Providers.Manager.ProviderManager "$sys\Emby.Providers.dll"
```

---

## 6. 行为取证（反编译真实实现）

### 6.1 优先级 / 选中逻辑

- `IHasOrder.Order`：无实现按 `0`；`GetOrder`（图片 provider）/ `GetDefaultOrder`（元数据 provider）两处消费。
- `LibraryOptions.LocalMetadataReaderOrder` / `MetadataFetcherOrder`：命中的下标即顺序，未命中返回 `100`；`MusicBrainz` 特例返回 `-1`。
- `IMetadataProvider` 的注册是**自动类型发现**：Emby 扫描插件程序集里实现 `IMetadataProvider` / `IImageProvider` 的类型（`GetEnabledMetadataProviders` 按 `LibraryOptions` 过滤，`IsMetadataFetcherEnabled(libraryOptions, name)` 以 `Name` 为准）。

### 6.2 Identify（识别）流程 — 真实端点与代码路径

| 步骤 | 端点 | 证据 |
|------|------|------|
| 打开识别框，列外部 ID 输入框 | `GET /Items/{Id}/ExternalIdInfos?IsSupportedAsIdentifier=true` | `Emby.Api.GetExternalIdInfos`：`[Route("/Items/{Id}/ExternalIdInfos", "GET")] [Authenticated(Roles="Admin")]` |
| 搜索 | `POST /Items/RemoteSearch/Movie`（body = `RemoteSearchQuery<MovieInfo>`）→ `List<RemoteSearchResult>` | `Emby.Api.GetMovieRemoteSearchResults`；人物 `POST /Items/RemoteSearch/Person`（body `RemoteSearchQuery<PersonLookupInfo>`），另有 Book/BoxSet/Game/MusicAlbum/MusicArtist/MusicVideo/Series/Trailer 共 10 个 |
| 搜索结果缩略图 | `GET /Items/RemoteSearch/Image?imageUrl=…&ProviderName=…` | `Emby.Api.GetRemoteSearchImage`；网页端 `itemidentifier.js` |
| 应用识别结果 | `POST /Items/RemoteSearch/Apply/{Id}`（body = `ApplySearchCriteria : RemoteSearchResult`，query `ReplaceAllImages`） | `Emby.Api.ApplySearchCriteria`：`[Route("/Items/RemoteSearch/Apply/{Id}", "POST", Summary = "Applies search criteria to an item and refreshes metadata")] [Authenticated(Roles="Admin")]` |

`ProviderManager.GetSearchImage`（服务端代理缩略图的实现，**决定了插件能否带鉴权头**）：

```csharp
public Task<HttpResponseInfo> GetSearchImage(string providerName, string url, CancellationToken cancellationToken)
{
    IRemoteSearchProvider remoteSearchProvider = null;
    IMetadataProvider[] metadataProviders = _metadataProviders;
    for (int i = 0; i < metadataProviders.Length; i++)
    {
        if (metadataProviders[i] is IRemoteSearchProvider p
            && string.Equals(providerName, p.Name, StringComparison.OrdinalIgnoreCase))
        {
            remoteSearchProvider = p;
            break;
        }
    }
    if (remoteSearchProvider == null) { throw new ArgumentException("Search provider not found."); }
    return remoteSearchProvider.GetImageResponse(url, cancellationToken);   // ← 回调插件，可加 Bearer
}
```

Emby 网页端（`dashboard-ui/modules/itemidentifier/itemidentifier.js`，压缩原文摘录）：

```js
identifyResult.ImageUrl && (identifyOptionsForm =
  '<div class="flex align-items-center"><img src="' +
  apiClient.getUrl("Items/RemoteSearch/Image",
      { imageUrl: url, ProviderName: provider, api_key: apiClient.accessToken() }) +
  '" style="max-height:240px;" />' + …);
```

→ **Emby 的 Identify 缩略图是经服务端代理的，`SearchProviderName`（= 插件 `Name`）用来反查 provider 并回调 `GetImageResponse`。**
这与 Jellyfin（`RemoteSearchResult.ImageUrl` 被 jellyfin-web 原样塞进 `<img>`，浏览器直连，无法带 token）**机制不同**。

### 6.3 图片链路：4 条路径，鉴权能力不同（重要）

| 路径 | 触发 | 谁发起 HTTP | 插件能否加鉴权头 | 证据 |
|------|------|-------------|------------------|------|
| A. 元数据刷新自动下图 | `IRemoteImageProvider.GetImages` 返回的 `Url` | **插件** `GetImageResponse(url)` | ✅ **能** | `ItemImageProvider.cs:470`：`await provider.GetImageResponse(url, ct)` → `SaveImage(item, …, response.Content, response.ContentType, …)` |
| B. Identify 弹窗缩略图 | `/Items/RemoteSearch/Image` | **插件** `IRemoteSearchProvider.GetImageResponse(url)` | ✅ **能** | `ProviderManager.cs:1311-1328`（见 §6.2） |
| C. 图片选择器**预览** | `/Images/Remote?imageUrl=` | **Emby** `IHttpClient.GetResponse` | ❌ **不能** | `Emby.Api.Images.RemoteImageService.Get(GetRemoteImage)`：`await _httpClient.GetResponse(new HttpRequestOptions { Url = request.ImageUrl, … })`（插件无 hook） |
| D. 图片选择器**手动下载** | `POST /Items/{Id}/RemoteImages/Download` | **Emby** `_ioManager.GetResponse` | ❌ **不能** | `RemoteImageService.Post` → `ProviderManager.SaveImage(item, libOptions, request.ImageUrl, …)` → `SaveImage` 判定非 `MediaProtocol.File` → `SaveImageFromRemoteUrl` → `_ioManager.GetResponse(new HttpRequestOptions { Url = url, EnableDefaultUserAgent = true })` |

关键反编译原文（路径 D 的分流）：

```csharp
// Emby.Providers.Manager.ProviderManager
public Task SaveImage(BaseItem item, LibraryOptions libraryOptions, string url, ImageType type, int? imageIndex,
    long[] generatedFromItemIds, IDirectoryService directoryService, bool updateImageCache, CancellationToken cancellationToken)
{
    if (string.IsNullOrEmpty(url)) { throw new ArgumentNullException("url"); }
    if (_ioManager.GetPathProtocol(url) == MediaProtocol.File)
    {
        ReadOnlyMemory<char> mimeType = MimeTypes.GetMimeType(url, enableStreamDefault: false).AsMemory();
        return SaveImage(item, libraryOptions, url, mimeType, type, imageIndex, null, generatedFromItemIds, directoryService, updateImageCache, cancellationToken);
    }
    return SaveImageFromRemoteUrl(item, libraryOptions, url, type, imageIndex, generatedFromItemIds, directoryService, updateImageCache, cancellationToken);
}
```

Emby 网页端图片选择器（`dashboard-ui/modules/imagedownloader/imagedownloader.js`，压缩原文摘录）：

```js
function getDisplayUrl(url, apiClient) {
    return apiClient.getUrl("Images/Remote", { api_key: apiClient.accessToken(), imageUrl: url });
}
…
image.ImageUrl        = getDisplayUrl(image.ThumbnailUrl || image.Url, options);  // 预览：服务端代理，但用 Emby 自己的客户端拉
image.OriginalImageUrl = getDisplayUrl(image.Url, options);
…
downloadOptions.ImageUrl = e.Url;         // 下载：原样把远端 Url 交给 /Items/{Id}/RemoteImages/Download
downloadOptions.ProviderName = e.ProviderName;
options.downloadRemoteImage(downloadOptions);
```

**对 Amane 的直接设计含义**：
- 路径 A/B 可以靠 `GetImageResponse` 附加 `Authorization: Bearer`。
- 路径 C/D **拿不到**插件附加的头 → 若 `RemoteImageInfo.Url` / `ThumbnailUrl` 指向需要 Bearer 的 Amane 端点（`/api/resources/proxy`、`/api/resources/{hash}`），**图片选择器的预览与手动下载会 401**。
- 结论：Emby 上若要全路径可用，`Url`/`ThumbnailUrl` 需要**自鉴权**（token 放 query，例如 Amane 侧支持 `?token=` 形式），或指向无需鉴权的端点；否则须接受「只剩自动刷新 + Identify 缩略图两条路径可用」。**这一点与 Jellyfin 的「双轨制」问题形态不同但同样存在**，是本次调查对实施方案影响最大的结论。
- 附带：`RemoteImageInfo.ThumbnailUrl` 在 Emby 服务端**只被合并传递**（`ProviderManager.cs:1235-1238`），不参与自动下载；自动下载只用 `RemoteImageInfo.Url`（`ItemImageProvider.cs:460` 起）。

### 6.4 库扫描 / 刷新行为

- 刷新入口：`IProviderManager.QueueRefresh` / `RefreshFullItem` / `RefreshSingleItem`（签名见 §3.9），进度事件 `RefreshStarted` / `RefreshProgress` / `RefreshCompleted`。
- 刷新模式枚举：

```csharp
public enum MetadataRefreshMode { ValidationOnly = 1, Default = 2, FullRefresh = 3 }
```

- `MetadataRefreshOptions`（`ImageRefreshOptions` 子类）关键开关：`ReplaceAllImages`、`ReplaceThumbnailImages`、`EnableRemoteContentProbe`、`EnableSubtitleDownloading`、`EnableThumbnailImageExtraction`、`OverwriteLocalMetadataProviderIds`、`ImageRefreshMode`。
- 元数据写回：`IProviderManager.SaveMetadata(item, updateType)`；`ICustomMetadataProvider<T>.FetchAsync` 返回 `ItemUpdateType` 表示实际改动类型（只有真正改动才会触发后续写库）。
- `GetAllMetadataPlugins()` 返回 `MetadataPluginSummary[]`，Emby 用它生成每类型的「元数据下载器 / 图片下载器」列表。
- 扫描期（library scan）与刷新期（metadata refresh）的差异属于**运行时行为**，本次未做实机验证 → 见 §7。

---

## 7. 证据等级与需要实机验证清单

### 7.1 证据等级说明

| 等级 | 含义 | 本文档中的体现 |
|------|------|----------------|
| **编译通过** | 真实 `dotnet build` 成功，编译器核对过命名空间/成员/类型 | §4.5（两个变体均 0 错误）；§3 全部签名 |
| **反编译** | 对真实发布二进制 ILSpy 反编译得到的原文 | §6 全部行为结论；§3.5 的 ImdbExternalId |
| **反射** | `MetadataLoadContext` 读取真实 DLL 元数据得到的签名 | §3 全部类型/成员清单；`PersonKind` 0 命中 |
| **官方文档** | dev.emby.media / 官方仓库发布物 | §2 来源 URL、§8 |
| **需实机验证** | 本次无法确证，必须跑起来才能定论 | §7.2 |

### 7.2 需要实机验证（本次无法确证）

1. **插件发现/注册的确切时机与路径**：Emby 扫描插件程序集实现 `IMetadataProvider` / `IImageProvider` / `IExternalId` 的类型的日志与失败表现（推断为自动类型发现，但未跑服务器验证）。
2. **`PersonInfo.ImageUrl` 的抓取时机与鉴权**：已证实被写入 Person 的 Primary 图引用（`SqliteItemRepository.GetPersonId` → `CreateItemByNameId<Person>(…, imageUrl, …)` → `UpdateValuesIfNeeded` → `Images` 列），但**它是被立即下载还是延迟下载、走哪条客户端、能否带插件的 auth 头**，全部未验证。
3. **路径 C/D 的实际失败表现**：`/Images/Remote` 与 `/Items/{Id}/RemoteImages/Download` 对需要 Bearer 的 Amane URL 是否 401，以及 UI 上呈现什么错误（结论由代码推导，未跑）。
4. **`LibraryOptions` 中顺序相关字段的完整清单与默认值**：仅确证 `LocalMetadataReaderOrder` / `MetadataFetcherOrder` 被 `GetConfiguredOrder` 读取；全字段与默认值未逐项 dump。
5. **库扫描（scan）与刷新（refresh）在插件 provider 调用次数/顺序上的差异**：未实机验证。
6. **Emby 各平台插件 TFM 兼容矩阵**：官方文档称服务器运行 .NET Core 2.0+ 与 Mono 两种运行时；本机仅验证了「`net8.0` 与 `netstandard2.0` 都能对着参考程序集编译」，**Mono 运行时实跑未验证**。
7. **4.10 vs 4.9 的 API 差异**：本次只测 4.10.0.40；若需兼容 4.9.x，须对 `MediaBrowser.Server.Core 4.9.1.90` 重复 dump。
8. **`RemoteMetadataFetchOptions` / `IRemoteMetadataProviderWithOptions` / `IRemoteImageProviderWithOptions` 是否为 4.10 新增**：未与旧版本比对。

---

## 8. 官方文档 URL（引用）

- Emby 插件开发总览：https://dev.emby.media/doc/plugins/index.html
- 插件开发（含 post-build 拷到 `%AppData%\Emby-Server\programdata\plugins\`、`IServerEntryPoint`）：https://dev.emby.media/doc/plugins/dev/index.html
- 插件 UI（配置页）：https://dev.emby.media/doc/plugins/ui/index.html
- 插件 API 参考（按类型分页，如 `reference/pluginapi/MediaBrowser.Controller.Plugins.IServerEntryPoint.html`）：https://dev.emby.media/reference/pluginapi/MediaBrowser.Controller.Plugins.IServerEntryPoint.html
  - ⚠️ 该站点 **没有** `reference/pluginapi/index.html` 与 `toc.html`（实测 404），只能按类型全名直达。
  - ⚠️ 站点的 API 参考**不如本地 XML 文档全**，而本地 XML 文档本身也只是策展子集（§2.3）→ **不要**以文档判断类型存在性。
- 官方发布二进制：https://github.com/MediaBrowser/Emby.Releases/releases（`tag 4.10.0.40`）
- 官方 NuGet：
  - `MediaBrowser.Common`：https://www.nuget.org/packages/MediaBrowser.Common
  - `MediaBrowser.Server.Core`（含 `MediaBrowser.Controller.dll`）：https://www.nuget.org/packages/MediaBrowser.Server.Core

---

## 9. 与 Jellyfin 的关键差异对照（供实施方案直接使用）

| 关注点 | Jellyfin 10.11（本仓库现状） | **Emby 4.10.0.40（实测）** |
|--------|------------------------------|----------------------------|
| 远程元数据接口 | `IRemoteMetadataProvider<T,T2>`（成员：`Name`、`GetMetadata`、`GetSearchResults`） | `IRemoteMetadataProvider<T,T2>`，但**还要实现 `GetImageResponse`**（继承自 `IRemoteSearchProvider`） |
| 图片下载返回类型 | `HttpResponseMessage` | **`MediaBrowser.Common.Net.HttpResponseInfo`** |
| 人物类型枚举 | `Jellyfin.Data.Enums.PersonKind` | **`MediaBrowser.Model.Entities.PersonType`**（无 `PersonKind`） |
| `PersonInfo.ImageUrl` | 存在（官方 XML 未列出） | **存在，`string`**，且被 `ProviderUtils` / `MetadataService` / `SqliteItemRepository` 消费 |
| `IExternalId` 成员 | `ProviderName` / `Key` / `UrlFormatString` / `Type` | **`Name` / `Key` / `UrlFormatString`**（无 `ProviderName`、无 `Type`） |
| ProviderId 键是否拼 `"Id"` | 是（本仓库 AGENTS.md 记载，靠双写 `Amane` + `AmaneId` 规避） | **否，原样使用 `Key`**（全量反编译 0 命中） |
| 插件注册 | `IPluginServiceRegistrator` | **无此接口**；自动类型发现 |
| 配置页 | `MediaBrowser.Model.Plugins.IHasWebPages` | **同名同接口**；另有新式 `BasePluginSimpleUI<TOptionType>` |
| 插件基类 | `BasePlugin<PluginConfiguration>` | `BasePlugin` / `BasePlugin<TConfigurationType>`（同命名空间风格） |
| 优先级 | `IHasOrder` | **`IHasOrder`（同样存在且被消费）** + `LibraryOptions` 顺序配置 |
| Identify 缩略图 | `ImageUrl` 浏览器直连 → 无法带 token | **`/Items/RemoteSearch/Image` 服务端代理 → 插件可带 token** |
| 图片选择器预览 | 无法带 token | **同样无法带 token**（Emby 自己拉） |
| 图片选择器手动下载 | — | **无法带 token**（Emby 自己拉） |

---

## 10. 复现清单（命令级）

```powershell
# 1) 工具链（本机初始无 dotnet）
$tools = 'C:\Users\rappa\AppData\Local\Temp\amane-emby-poc-tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile "$tools\dotnet-install.ps1" -UseBasicParsing
& "$tools\dotnet-install.ps1" -Channel 10.0 -InstallDir "$tools\dotnet" -NoPath
$env:DOTNET_ROOT = "$tools\dotnet"; $env:PATH = "$tools\dotnet;$env:PATH"
dotnet tool install ilspycmd --tool-path "$tools\tools"     # 11.1.0.9782

# 2) 真实 Emby 二进制（219MB）
curl.exe -L --proxy http://127.0.0.1:7897 -o "$tools\dl\embyserver-netcore_4.10.0.40.zip" `
  https://github.com/MediaBrowser/Emby.Releases/releases/download/4.10.0.40/embyserver-netcore_4.10.0.40.zip
tar.exe -xf "$tools\dl\embyserver-netcore_4.10.0.40.zip" -C "$tools\dl\emby-server-4.10.0.40"

# 3) 官方 NuGet 参考程序集
curl.exe -sSL -o "$tools\dl\mediabrowser.server.core.4.10.0.24-beta2.nupkg" `
  https://api.nuget.org/v3-flatcontainer/mediabrowser.server.core/4.10.0.24-beta2/mediabrowser.server.core.4.10.0.24-beta2.nupkg
curl.exe -sSL -o "$tools\dl\mediabrowser.common.4.10.0.24-beta2.nupkg" `
  https://api.nuget.org/v3-flatcontainer/mediabrowser.common/4.10.0.24-beta2/mediabrowser.common.4.10.0.24-beta2.nupkg

# 4) 编译两个变体
$poc = 'C:\Users\rappa\AppData\Local\Temp\amane-emby-poc'
dotnet build "$poc\Amane.Emby.Poc.Net8\Amane.Emby.Poc.Net8.csproj" -c Release
dotnet build "$poc\Amane.Emby.Poc.NetStandard\Amane.Emby.Poc.NetStandard.csproj" -c Release
```

---

## 11. 对实施方案的 5 条硬性提醒

1. **不要照搬 Jellyfin 的 `IExternalId` 形状**：Emby 是 `Name`（不是 `ProviderName`）且**没有 `Type`**；`Key` 直接作为 ProviderIds 键，不需要也不应该双写 `AmaneId`（除非另有业务理由）。
2. **`GetImageResponse` 是 `IRemoteMetadataProvider<,>` 的必需成员**，不是可选项——写 `IRemoteMetadataProvider<Movie, MovieInfo>` 就必须实现它。
3. **`PersonKind` 会直接编译不过**，必须换成 `MediaBrowser.Model.Entities.PersonType`。
4. **图片 URL 必须考虑自鉴权**：Emby 的图片选择器预览与手动下载由 Emby 自己的 HTTP 客户端直连，插件无法注入 `Authorization`。这是本次调查中风险最高的一条。
5. **插件 `Name` 是协议的一部分**：`SearchProviderName` 被 Emby 无条件覆盖为 `Name`，Identify 缩略图靠 `Name` 反查 provider。改名会同时影响 Identify 缩略图与库中 fetcher 开关匹配（`IsMetadataFetcherEnabled(libraryOptions, name)`）。
