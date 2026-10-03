# 真实世界 Emby 插件生态取证（元数据/图片/人物类）

> 调查员：teammate `emby-ecosystem` ｜ 任务：task-3 ｜ 日期：本机取证会话
> 方法：GitHub REST API（repos / trees / releases / commits / issues）+ `raw.githubusercontent.com` 抓原文 + 下载真实 Release 资产解包 + NuGet 包 `MediaBrowser.*` 程序集字节取证。
> **本文所有代码/文档片段均为原文摘录**，逐条附 URL。凡推断而非原文可得者，显式标注「推断」或「需要实机验证」。

---

## 0. 结论速览（TL;DR）

| # | 结论 | 关键证据 |
|---|------|----------|
| 1 | **Emby 第三方插件只能手动安装**：Emby 没有 Jellyfin 那种「填一个 manifest.json URL 就能订阅」的机制。官方插件目录（Catalog）是 Emby 服务端内置的，第三方插件一律「下载 dll → 放进 `<数据目录>/plugins/` → 重启」。 | [Emby 官方文档 Plugins Overview](https://emby.media/support/articles/Plugins.html)、Emby 团队成员 Luke 论坛回复、[ThePornDB README](https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/blob/main/README.md) 明写 `Repository (Jellyfin only)` |
| 2 | **Emby SDK 是公开 NuGet 包**，`MediaBrowser.Server.Core` / `MediaBrowser.Common`（4.9.1.90 当前 stable）直接 `PackageReference` 即可，**不需要 HintPath 指向服务器目录**。主流项目全部走 NuGet，只有个别项目额外提供「本机 Emby 目录优先」的可选 HintPath 分支。 | [Emby.SDK 官方模板 csproj](https://github.com/MediaBrowser/Emby.SDK/blob/master/SampleCode/Templates/EmbyPluginMinimalTemplate/EmbyPluginMinimalTemplate.csproj)、[Pronium csproj](https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/blob/dev/Jellyfin.Plugin.Pronium/Pronium.csproj)、[bangumi csproj](https://github.com/Whereis-Alice/emby-plugin-bangumi/blob/main/src/Emby.Plugins.Bangumi/Emby.Plugins.Bangumi.csproj) |
| 3 | **Jellyfin+Emby 双平台的主流做法是「同一个仓库、同一份源码、两套 MSBuild Configuration + 两套 TFM + `__EMBY__`/`__JELLYFIN__` 条件编译」**，而不是两个独立项目、也不是运行时反射适配。 | Pronium / ThePornDB / JavScraper 三家的 csproj 与源码；详见 §8 |
| 4 | **发布产物一律只放自己的 dll**（+ pdb）；第三方依赖用 ILRepack 合并进主 dll；**从不打包 `MediaBrowser.*.dll`**。已下载真实 Release zip 逐字节验证。 | [Pronium release.yml](https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/blob/dev/.github/workflows/release.yml)、[ThePornDB release.yml](https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/blob/main/.github/workflows/release.yml)、§7 实测解包 |
| 5 | **Emby 版本升级破坏插件是常态且有实据**：Emby 4.8→4.9 改 `LibraryOptions` 参数、4.9.x 小版本改人物页行为；主流项目用「README 写死支持区间 + `Version.json` min/maxEmbyVersion 门控自动更新」应对。 | [MediaInfoKeeper README/Version.json](https://github.com/honue/MediaInfoKeeper/blob/master/Version.json)、[ThePornDB #123](https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/issues/123)、[#112](https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/issues/112) |
| 6 | **人物头像的正确做法：Provider 只提供 `RemoteImageInfo`，把 `PersonInfo.ImageUrl` 一并写好，让 Emby/Jellyfin 自己下载**；`IRemoteImageProvider.Supports(item) => item is Person`。Emby 分支的 `GetImages` 多一个 `LibraryOptions` 参数。 | §5.4；[bangumi BangumiPersonProvider.cs](https://github.com/Whereis-Alice/emby-plugin-bangumi/blob/main/src/Emby.Plugins.Bangumi/Providers/BangumiPersonProvider.cs) |
| 7 | **人物头像/图片下载接口在两平台签名不同**：Emby 用 `IHttpClient.GetResponse(HttpRequestOptions)` 返回 `HttpResponseInfo`；Jellyfin 用 `IHttpClientFactory.CreateClient().SendAsync()` 返回 `HttpResponseMessage`。 | §5.5；[Pronium Helper.cs#L341-L365](https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/blob/dev/Jellyfin.Plugin.Pronium/Helpers/Helper.cs) |
| 8 | **本项目的现实选择**（对 Amane 项目）：若要同时支持 Emby，走 Pronium/ThePornDB 模式在现有 csproj 上**加一个 `Release.Emby` 配置**（+ 第二个 TFM + `__EMBY__` 条件编译），比另起一个仓库便宜得多；但必须接受 §6 列出的 ABI 脆弱性。 | §8 / §9 |

---

## 1. 候选筛选方法与判定口径

### 1.1 检索

- GitHub 仓库搜索：`https://api.github.com/search/repositories?q=emby+plugin+metadata&sort=updated&per_page=20`
- 官方 Org 全量列举：`https://api.github.com/orgs/MediaBrowser/repos?per_page=100&sort=updated&type=public`
- 维护状态判定字段：`pushed_at`、`stargazers_count`、`/releases`、`/commits?per_page=1`、`open_issues_count`。

### 1.2 「有真实用户」的可证伪口径

- stars ≥ 25 或（Emby 官方 Org `MediaBrowser/` 下）；
- 近 12 个月内有 Release 或 push；
- issue 区有真实用户报障（不是空仓库）。

> 注：`Whereis-Alice/emby-plugin-bangumi` stars = 0，**不满足「有真实用户」**，本文只把它作为「**Emby Provider 完整用法 + 人物头像 + 单 dll 打包**的最佳代码样本」引用，其产品化程度不作为生态成熟度证据。

### 1.3 淘汰/降权的仓库

| 仓库 | 状态 | 处置 |
|---|---|---|
| [xjasonlyu/jellyfin-plugin-avdc](https://github.com/xjasonlyu/jellyfin-plugin-avdc) | 294 stars，**`archived=true`**，pushed_at `2025-06-07` | 只作为「双平台维护成本高→弃坑」的反面证据 |
| [JavScraper/Emby.Plugins.JavScraper](https://github.com/JavScraper/Emby.Plugins.JavScraper) | 3796 stars，但 **master 最后提交 2021-06-22、最新 Release 2021-06-22**（`pushed_at` 显示 2024-07-08，为其它分支/引用推送） | 作为「历史事实标准 + 反面证据」保留，不作为唯一依据 |
| 各类 0–2 stars 新仓库（`bespokedb/BespokeDB-Plugin`、`hbuckle/JsonMetadata`、`tobias-tengler/Emby.Plugins.AnimeKai` 等） | 无 Release / 无用户 | 剔除 |

---

## 2. 事实表

时间均为 GitHub API 原样返回（`Z` 时区 UTC）。

| 仓库 URL | 最近提交（默认分支 HEAD） | 仓库 `pushed_at` | Stars | 有 Release | 是否声明支持 Emby 版本 | TargetFramework | 依赖方式 |
|---|---|---|---|---|---|---|---|
| [jellyfin-adult/Jellyfin.Plugin.Pronium](https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium) | `2026-09-27T20:30:49Z`「chore: update manifest file」 | `2026-09-27T20:33:15Z` | 31 | ✅ `2.2.0.153`（含 Emby+Jellyfin 双 zip）、`nightly` | README 只写安装方式（Emby/Synology），**未写版本区间**；csproj 以 `MediaBrowser.Server.Core 4.9.1.90` 为编译基准 | Emby 配置 `netstandard2.1`；Jellyfin 配置 `net10.0` | **NuGet `PackageReference`，无 HintPath** |
| [ThePornDatabase/Jellyfin.Plugin.ThePornDB](https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB) | `2026-09-10T00:27:50Z`「update to jellyfin 12」 | `2026-09-10T00:31:25Z` | 583 | ✅ `1.6.0.11`、`latest`（双 zip） | README 无版本声明；csproj 以 `MediaBrowser.Server.Core 4.9.1.90` 为编译基准 | Emby `netstandard2.1`；Jellyfin `net10.0` | **NuGet，无 HintPath** |
| [Whereis-Alice/emby-plugin-bangumi](https://github.com/Whereis-Alice/emby-plugin-bangumi) | `2026-09-22T21:25:01Z` | `2026-09-22T21:25:01Z` | 0 ⚠️ | ✅（tag 触发，产物为裸 dll + `.sha256`） | README：**「针对 Emby 4.9.x–4.10.x / .NET 8 编写，CI 编译目标 ABI `4.9.1.90`，并在 `4.10.0.40` 实机验证」** | `net8.0`（单一） | **三级：`-p:EmbySystemDir` → `EMBY_SYSTEM_DIR` → NuGet `MediaBrowser.Common`/`MediaBrowser.Server.Core 4.9.1.90`** |
| [honue/MediaInfoKeeper](https://github.com/honue/MediaInfoKeeper) | `2026-09-26T09:53:55Z` | `2026-09-26T09:53:55Z` | 372 | ✅ | README：**当前 `latest` 适配 Emby `4.10.0.40`；不支持 `4.8` 系列；`4.9.5.0` 最后支持 `v1.7.5.3`**；`Version.json` 限定 `minEmbyVersion/maxEmbyVersion` | `net8.0;net6.0`（双 TFM 对应不同 Emby 运行时） | NuGet `MediaBrowser.Server.Core 4.9.1.90` + **一处分发 DLL 的 HintPath**（`SQLitePCLRawEx.core.dll`） |
| [JavScraper/Emby.Plugins.JavScraper](https://github.com/JavScraper/Emby.Plugins.JavScraper) | `master` `2021-06-22T14:53:01Z`「增加图片URL」 | `2024-07-08T09:05:51Z` ⚠️ 与 HEAD 不符 | 3796 | ✅ 最新 `v1.2021.0622.2145`（2021） | 无 | `netstandard2.1`（**两个平台同 TFM**） | NuGet `MediaBrowser.Server.Core 4.6.0.50-*`（Emby）/ `Jellyfin.Controller 10.4-*`（Jellyfin），**带浮动版本号** |
| [MediaBrowser/NfoMetadata](https://github.com/MediaBrowser/NfoMetadata)（Emby 官方 Org） | `2026-07-21T19:41:00Z` | `2026-07-21T19:41:00Z` | 11 | ❌ 无 GitHub Release | 无（随服务器发版） | `netstandard2.0;` | NuGet `mediabrowser.server.core 4.8.2` |
| [MediaBrowser/Emby.Plugins.Anime](https://github.com/MediaBrowser/Emby.Plugins.Anime)（官方 Org） | `2026-06-02T17:27:35Z` | `2026-06-02T17:27:35Z` | 25 | ❌ | 无 | `netstandard2.0;` | NuGet `mediabrowser.server.core 4.8.11` |
| [MediaBrowser/Emby.SDK](https://github.com/MediaBrowser/Emby.SDK)（官方 SDK + 模板） | `2026-09-26T23:08:17Z` | `2026-09-26T23:08:17Z` | 58 | ❌ | 模板基准 `4.9.1.90` | `netstandard2.0` | NuGet `MediaBrowser.Server.Core 4.9.1.90` |

参考：`MediaBrowser/Emby.Plugins.MyAnimeList`（官方 Org，`netstandard2.0`，`mediabrowser.server.core 4.8.11`）、`MediaBrowser/Douban`（官方 Org，`netstandard2.0`，`MediaBrowser.Server.Core 4.6.0.50`）。

---

## 3. 逐个仓库取证

### 3.1 jellyfin-adult/Jellyfin.Plugin.Pronium（双平台同仓库的当前事实标准）

- 仓库：<https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium>
- `.csproj`：<https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/blob/dev/Jellyfin.Plugin.Pronium/Pronium.csproj>
- 入口：<https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/blob/dev/Jellyfin.Plugin.Pronium/Plugin.cs>

**`.csproj` 原文摘录（关键行）**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'">net10.0</TargetFramework>
    <TargetFramework Condition="'$(Configuration)'=='Debug.Emby' or '$(Configuration)'=='Release.Emby'">netstandard2.1</TargetFramework>
    <RootNamespace>Pronium</RootNamespace>
    <AssemblyVersion>2.2.0.156</AssemblyVersion>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <Configurations>Debug;Release;Release.Emby;Debug.Emby</Configurations>
    <LangVersion>8</LangVersion>
  </PropertyGroup>
  <PropertyGroup Condition="'$(Configuration)'=='Release.Emby'">
    <DefineConstants>__EMBY__</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FlareSolverrSharp" Version="4.0.0" />
    <PackageReference Include="HtmlAgilityPack" Version="1.13.0" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
  </ItemGroup>
  <ItemGroup Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'">
    <PackageReference Include="Jellyfin.Controller" Version="12.1.0" />
    <PackageReference Include="SkiaSharp" Version="2.88.6" />
  </ItemGroup>
  <ItemGroup Condition="'$(Configuration)'=='Debug.Emby' or '$(Configuration)'=='Release.Emby'">
    <PackageReference Include="MediaBrowser.Server.Core" Version="4.9.1.90" />
    <PackageReference Include="SkiaSharp" Version="2.88.6" />
  </ItemGroup>
  <!-- 每个平台各嵌一份配置页 HTML（同一个 LogicalName） -->
  <ItemGroup Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'">
    <EmbeddedResource Include="Configuration\configPage-jellyfin.html">
      <LogicalName>Pronium.Configuration.configPage.html</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
  <ItemGroup Condition="'$(Configuration)'=='Debug.Emby' or '$(Configuration)'=='Release.Emby'">
    <EmbeddedResource Include="Configuration\configPage-emby.html">
      <LogicalName>Pronium.Configuration.configPage.html</LogicalName>
    </EmbeddedResource>
    <EmbeddedResource Include="Configuration\configPage.js" />
  </ItemGroup>
</Project>
```

> 要点：**没有任何 HintPath**；Emby 侧就是 `PackageReference Include="MediaBrowser.Server.Core" Version="4.9.1.90"`。
> 配置页 HTML 也用配置名区分（Emby 版多一个 `configPage.js`）。

**插件入口类原文（`Plugin.cs`）**

```csharp
#if __EMBY__
using System.IO;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Drawing;
#else
using System.Net.Http;
using Microsoft.Extensions.Logging;
#endif
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Pronium
{
#if __EMBY__
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
#else
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
#endif
    {
#if __EMBY__
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IHttpClient http, ILogManager logger)
#else
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IHttpClientFactory http, ILogger<Plugin> logger)
#endif
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            Http = http;
        }

#if __EMBY__
        public static IHttpClient Http { get; set; }
#else
        public static IHttpClientFactory Http { get; set; }
#endif
        ...
    }
}
```

> Emby 侧 `BasePlugin<T>` 构造参数是 `IHttpClient`（`MediaBrowser.Common.Net`）+ `ILogManager`（`MediaBrowser.Model.Logging`）；
> Jellyfin 侧是 `IHttpClientFactory` + `ILogger<Plugin>`。

**Provider 接口实现原文（`Providers/MovieProvider.cs`）**

```csharp
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Pronium.Helpers;

#if __EMBY__
using MediaBrowser.Common.Net;
#else
using System.IO;
using System.Net.Http;
#endif

namespace Pronium.Providers
{
    public class MovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>
    {
        public string Name => Plugin.Instance?.Name ?? "Pronium";

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        { ... }

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        { ... }

#if __EMBY__
        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
#else
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
#endif
        {
            return Helper.GetImageResponse(url, cancellationToken);
        }
    }
}
```

`MovieImageProvider`（`IRemoteImageProvider`）里的平台签名差异——**Emby 多一个 `LibraryOptions` 形参**：

```csharp
#if __EMBY__
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Configuration;
#else
using System.Net.Http;
#endif

namespace Pronium.Providers
{
    public class MovieImageProvider : IRemoteImageProvider
    {
        public bool Supports(BaseItem item) => item is Movie;

#if __EMBY__
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
#else
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
#endif
        { ... }

#if __EMBY__
        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
#else
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
#endif
        { return Helper.GetImageResponse(url, cancellationToken); }
    }
}
```

人物 Provider（`Providers/ActorProvider.cs`）：

```csharp
public class ActorProvider : IRemoteMetadataProvider<Person, PersonLookupInfo>
```

### 3.2 ThePornDatabase/Jellyfin.Plugin.ThePornDB（583 stars，最成熟的同款架构）

- 仓库：<https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB>
- `.csproj`：<https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/blob/main/Jellyfin.Plugin.ThePornDB/ThePornDB.csproj>
- 入口：<https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/blob/main/Jellyfin.Plugin.ThePornDB/Plugin.cs>

**`.csproj` 原文摘录（关键行）**

```xml
<TargetFramework Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'">net10.0</TargetFramework>
<TargetFramework Condition="'$(Configuration)'=='Debug.Emby' or '$(Configuration)'=='Release.Emby'">netstandard2.1</TargetFramework>
<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
<Configurations>Debug;Release;Release.Emby;Debug.Emby</Configurations>
...
<PropertyGroup Condition="'$(Configuration)'=='Release.Emby'">
  <DefineConstants>__EMBY__</DefineConstants>
</PropertyGroup>
...
<ItemGroup Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'">
  <PackageReference Include="Jellyfin.Controller" Version="12.0.0" />
</ItemGroup>

<ItemGroup Condition="'$(Configuration)'=='Debug.Emby' or '$(Configuration)'=='Release.Emby'">
  <PackageReference Include="MediaBrowser.Server.Core" Version="4.9.1.90" />
  <PackageReference Include="StandardSocketsHttpHandler" Version="2.2.0.10" />
</ItemGroup>
```

**插件入口类原文（`Plugin.cs`）——Emby 侧走 `BasePluginSimpleUI<T>`**

```csharp
#if __EMBY__
using System.IO;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
#else
using System.Net.Http;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
#endif

namespace ThePornDB
{
#if __EMBY__
    public class Plugin : BasePluginSimpleUI<PluginConfiguration>, IHasThumbImage
    {
        public Plugin(IApplicationHost applicationHost, IHttpClient http, ILogManager logger)
            : base(applicationHost)
#else
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IHttpClientFactory http, ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
#endif
        { ... }

#if __EMBY__
        public static IHttpClient Http { get; set; }
#else
        public static IHttpClientFactory Http { get; set; }
#endif
        ...
#if __EMBY__
        public PluginConfiguration Configuration => this.GetOptions();
        public ImageFormat ThumbImageFormat => ImageFormat.Png;
        public Stream GetThumbImage() => this.GetType().Assembly.GetManifestResourceStream($"{this.GetType().Namespace}.Resources.logo.png");
#else
#endif
    }
}
```

> **注意**：Emby 的 `BasePluginSimpleUI<T>` 与 Jellyfin 的 `BasePlugin<T> + IHasWebPages` **是两条完全不同的配置 UI 路线**（Emby 走 `Emby.Web.GenericEdit` 自动生成配置页，Jellyfin 走内嵌 HTML）。

**`IExternalId` 的命名空间/成员差异（原文）**

`ExternalIds/Scenes.cs`：
```csharp
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

#if __EMBY__
#else
using MediaBrowser.Model.Providers;
#endif

namespace ThePornDB.ExternalIds
{
#if __EMBY__
    public class Scenes : IExternalId, IHasWebsite
#else
    public class Scenes : IExternalId
#endif
    {
#if __EMBY__
        public string Name => Plugin.Instance.Name;
#else
        public string ProviderName => Plugin.Instance.Name;
        public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;
#endif

        public string Key => Plugin.Instance.Name;

#if __EMBY__
        public string UrlFormatString => Consts.BaseURL + "/{0}";
        public string Website => Consts.BaseURL;
#endif

        public bool Supports(IHasProviderIds item) => item is Movie;
    }
}
```

`Pronium` 的等价文件更直白地暴露了命名空间差异（`ExternalIds/MovieID.cs`）：

```csharp
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

#if __EMBY__
#else
using MediaBrowser.Model.Providers;   // ← 只有 Jellyfin 需要
#endif
```

**程序集级证据**（本机下载 NuGet 包后对 DLL 做字符串堆扫描，见 §7.4）：
`IExternalId`、`IHasWebsite`、`IRemoteMetadataProvider`、`IRemoteImageProvider`、`BasePluginSimpleUI`、`IHasSupportedExternalIdentifiers` 只出现在 **`MediaBrowser.Controller.dll`**；
`IHasWebPages`、`PersonType`、`ILogManager`、`LibraryOptions` 在 **`MediaBrowser.Model.dll`**；
`IHasThumbImage`、`IHttpClient` 在 **`MediaBrowser.Common.dll`**（命名空间 `MediaBrowser.Common.Net`）。
→ 与上面对 `using` 的分支完全吻合：**Emby 的 `IExternalId`/`IHasWebsite` 来自 `MediaBrowser.Controller.Providers`**。

### 3.3 Whereis-Alice/emby-plugin-bangumi（Emby-only，代码质量最高的 Provider 样本）

- 仓库：<https://github.com/Whereis-Alice/emby-plugin-bangumi>
- `.csproj`：<https://github.com/Whereis-Alice/emby-plugin-bangumi/blob/main/src/Emby.Plugins.Bangumi/Emby.Plugins.Bangumi.csproj>
- 人物 Provider：<https://github.com/Whereis-Alice/emby-plugin-bangumi/blob/main/src/Emby.Plugins.Bangumi/Providers/BangumiPersonProvider.cs>

**`.csproj` 原文摘录（关键行）——三级引用解析 + 单 dll 打包**

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  <GenerateDependencyFile>false</GenerateDependencyFile>
  <Version>1.3.1.0</Version>
</PropertyGroup>

<!--
  Reference resolution, in priority order:
    1. -p:EmbySystemDir=<path>  (MSBuild property)
    2. EMBY_SYSTEM_DIR          (environment variable, auto-imported by MSBuild)
    3. NuGet packages           (MediaBrowser.Server.Core + MediaBrowser.Common)

  Referencing the DLLs of the exact server you deploy to guarantees ABI match.
  The NuGet path exists so that CI can build without an Emby install; it may
  lag behind the newest server release.
-->
<PropertyGroup Condition=" '$(EmbySystemDir)' == '' ">
  <EmbySystemDir>$(EMBY_SYSTEM_DIR)</EmbySystemDir>
</PropertyGroup>
<PropertyGroup>
  <EmbyRefsFromDisk>false</EmbyRefsFromDisk>
  <EmbyRefsFromDisk Condition=" '$(EmbySystemDir)' != '' AND Exists('$(EmbySystemDir)/MediaBrowser.Controller.dll') ">true</EmbyRefsFromDisk>
</PropertyGroup>

<ItemGroup Condition=" '$(EmbyRefsFromDisk)' == 'true' ">
  <Reference Include="MediaBrowser.Common">
    <HintPath>$(EmbySystemDir)/MediaBrowser.Common.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="MediaBrowser.Controller">
    <HintPath>$(EmbySystemDir)/MediaBrowser.Controller.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="MediaBrowser.Model">
    <HintPath>$(EmbySystemDir)/MediaBrowser.Model.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="Emby.Web.GenericEdit">
    <HintPath>$(EmbySystemDir)/Emby.Web.GenericEdit.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="Emby.Media.Model">
    <HintPath>$(EmbySystemDir)/Emby.Media.Model.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>

<ItemGroup Condition=" '$(EmbyRefsFromDisk)' != 'true' ">
  <!-- MediaBrowser.Common ships MediaBrowser.Model + Emby.Web.GenericEdit + Emby.Media.Model -->
  <PackageReference Include="MediaBrowser.Common" Version="4.9.1.90" PrivateAssets="all" ExcludeAssets="runtime" />
  <PackageReference Include="MediaBrowser.Server.Core" Version="4.9.1.90" PrivateAssets="all" ExcludeAssets="runtime" />
</ItemGroup>

<Target Name="ReportEmbyRefs" BeforeTargets="ResolveReferences">
  <Message Importance="high" Condition=" '$(EmbyRefsFromDisk)' == 'true' " Text="Emby refs: local server at $(EmbySystemDir)" />
  <Message Importance="high" Condition=" '$(EmbyRefsFromDisk)' != 'true' " Text="Emby refs: NuGet 4.9.1.90 (set EmbySystemDir / EMBY_SYSTEM_DIR for an exact ABI match)" />
</Target>
```

> **这是「依赖方式」的关键中间态**：默认 NuGet（CI 可用、不依赖本机 Emby），但允许用 `-p:EmbySystemDir=<Emby根>/system` 切到**实机 DLL 精确 ABI**。
> 注意 `PrivateAssets="all" ExcludeAssets="runtime"` → **服务器程序集不会进输出目录**。

**插件入口（README 项目结构原文）**

```
src/Emby.Plugins.Bangumi/
├── Plugin.cs                  BasePluginSimpleUI 入口，持有 API 客户端
├── PluginOptions.cs           GenericEdit 配置模型
```

**Provider 接口使用原文（`Providers/BangumiPersonProvider.cs`，同一文件内两个类）**

```csharp
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;   // LibraryOptions
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;          // ILogManager
using MediaBrowser.Model.Providers;        // RemoteImageInfo / RemoteSearchResult

namespace Emby.Plugins.Bangumi.Providers
{
    public class BangumiPersonProvider : BangumiProviderBase,
        IRemoteMetadataProvider<Person, PersonLookupInfo>, IHasOrder
    {
        public BangumiPersonProvider(ILogManager logManager) : base(logManager) { }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            PersonLookupInfo searchInfo, CancellationToken cancellationToken) { ... }

        public async Task<MetadataResult<Person>> GetMetadata(
            PersonLookupInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Person>
            {
                Item = new Person(),
                HasMetadata = false,
                Provider = BangumiConstants.PluginName,
                ResultLanguage = options.PreferChineseTitle ? "zh" : "ja",
            };
            ...
            if (detail.Images != null) result.SearchImageUrl = detail.Images.Best();
            result.Item.ProviderIds[BangumiConstants.PersonProviderId] =
                detail.Id.ToString(CultureInfo.InvariantCulture);
            result.HasMetadata = true;
            ...
        }
    }

    /// <summary>Portraits for person pages. Bangumi stores exactly one picture per person.</summary>
    public class BangumiPersonImageProvider : BangumiProviderBase, IRemoteImageProvider, IHasOrder
    {
        public bool Supports(BaseItem item) => item is Person;

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
            => new[] { ImageType.Primary };

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var images = new List<RemoteImageInfo>();
            if (item == null) return images;
            ...
            images.Add(new RemoteImageInfo
            {
                ProviderName = BangumiConstants.PluginName,
                Type = ImageType.Primary,
                Url = url,
                ThumbnailUrl = source.Thumbnail(),
            });
            return images;
        }
    }
}
```

**`IExternalId` 在 Emby 下的形态（`Providers/BangumiExternalId.cs` 原文）**

```csharp
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

public class BangumiPersonExternalId : IExternalId
{
    public string Name            { get { return BangumiConstants.PluginName; } }   // ← Emby 用 Name
    public string Key             { get { return BangumiConstants.PersonProviderId; } }
    public string UrlFormatString { get { return BangumiConstants.PersonUrlFormat; } }
    public bool Supports(IHasProviderIds item) { return item is Person; }
}
```

> 对比 Jellyfin 的 `IExternalId`：属性名是 `ProviderName`（不是 `Name`），并且多一个 `ExternalIdMediaType? Type`。
> **这是本项目（Jellyfin 插件）若加 Emby 分支必须处理的一处硬差异。**

### 3.4 JavScraper/Emby.Plugins.JavScraper（历史事实标准，已停更）

- 仓库：<https://github.com/JavScraper/Emby.Plugins.JavScraper>
- `.csproj`：<https://github.com/JavScraper/Emby.Plugins.JavScraper/blob/master/Emby.Plugins.JavScraper/Emby.Plugins.JavScraper.csproj>
- Jellyfin 兼容层：<https://github.com/JavScraper/Emby.Plugins.JavScraper/blob/master/Emby.Plugins.JavScraper/Extensions/JellyfinExtensions.cs>

**`.csproj` 原文摘录（关键行）——注意是「Emby 为默认、`__JELLYFIN__` 为分支」**

```xml
<PropertyGroup>
  <TargetFramework>netstandard2.1</TargetFramework>
  <AssemblyName>JavScraper</AssemblyName>
  <Configurations>Debug;Release;Debug.Jellyfin;Release.Jellyfin</Configurations>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>

<PropertyGroup Condition="'$(Configuration)'=='Release.Jellyfin'">
  <DefineConstants>__JELLYFIN__</DefineConstants>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="HtmlAgilityPack" Version="1.11.33" GeneratePathProperty="true" />
  <PackageReference Include="ILRepack" Version="2.0.18" />
  <PackageReference Include="LiteDB" Version="5.0.10" />
  <PackageReference Include="MediaBrowser.Server.Core" Version="4.6.0.50-*" Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'" />
  <PackageReference Include="Jellyfin.Controller" Version="10.4-*" Condition="'$(Configuration)'=='Debug.Jellyfin' or '$(Configuration)'=='Release.Jellyfin'" />
</ItemGroup>

<!--合并外部程序集-->
<Target Name="ILRepack" AfterTargets="PostBuildEvent">
  <Exec Command="&quot;$(ILRepack)&quot; /out:$(AssemblyName).dll $(AssemblyName).dll HtmlAgilityPack.dll MihaZupan.HttpToSocks5Proxy.dll LiteDB.dll" WorkingDirectory="$(OutputPath)" />
</Target>

<!--打包-->
<Target Name="Zip" AfterTargets="ILRepack" Condition="'$(Configuration)'=='Release' or '$(Configuration)'=='Release.Jellyfin'">
  <ItemGroup>
    <TempZipDirectory Include="$(OutputPath)output" />
  </ItemGroup>
  <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll" DestinationFolder="@(TempZipDirectory)" />
  <ZipDirectory SourceDirectory="@(TempZipDirectory)" DestinationFile="$(BaseOutputPath)Emby.JavScraper@v$(Version).zip"  Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'" />
  <ZipDirectory SourceDirectory="@(TempZipDirectory)" DestinationFile="$(BaseOutputPath)Jellyfin.JavScraper@v$(Version).zip" Condition="'$(Configuration)'=='Debug.Jellyfin' or '$(Configuration)'=='Release.Jellyfin'" />
  <RemoveDir Directories="@(TempZipDirectory)" />
</Target>

<!--复制到 Emby 的补丁目录-->
<Target Name="EmbyPlugin" AfterTargets="ILRepack" Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'">
  <!--<Copy SourceFiles="$(TargetPath)" DestinationFolder="D:\emby\programdata\plugins\" OverwriteReadOnlyFiles="true" />-->
</Target>
```

> 关键：**两个平台共用 `netstandard2.1` 单一 TFM**，只切换 PackageReference 与 `__JELLYFIN__` 宏。
> zip 里**只放 `JavScraper.dll`**（ILRepack 已把依赖合进去）。

**「编译期兼容层」模式（`Extensions/JellyfinExtensions.cs`，整个文件被 `#if __JELLYFIN__` 包住）**

```csharp
#if __JELLYFIN__

using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Emby.Plugins.JavScraper
{
    public static class JellyfinExtensions
    {
        public static bool IsSubtitleFile(this ILibraryManager _, string path) { ... }

        public static void UpdateToRepository(this BaseItem item, ItemUpdateType type)
            => item.UpdateToRepository(type, default);

        public static string GetImageCachePath(this IApplicationPaths appPaths)
            => appPaths.ImageCachePath;

        public static bool DirectoryExists(this IFileSystem fileSystem, string path)
            => Directory.Exists(path);
        ...
        public static void Debug(this ILogger logger, string msg) => logger.LogDebug(msg);
        public static void Info(this ILogger logger, string msg)  => logger.LogInformation(msg);
        public static void Warn(this ILogger logger, string msg)  => logger.LogWarning(msg);
        public static void Error(this ILogger logger, string msg) => logger.LogError(msg);
    }
}

#endif
```

> 即：「**主代码按 Emby 的 API 形状写，Jellyfin 侧用扩展方法补齐差异**」。这是一个**已经过时**的样本——它假定 Jellyfin 仍是 10.4 时代的 Emby 形状 API。

**同一个 Provider 里的平台差异（`JavMovieProvider.cs` 原文）**

```csharp
public JavMovieProvider(
#if __JELLYFIN__
    ILoggerFactory logManager,
#else
    ILogManager logManager,
    TranslationService translationService,
    ImageProxyService imageProxyService,
    Gfriends gfriends,
#endif
    IProviderManager providerManager, IJsonSerializer jsonSerializer, IApplicationPaths appPaths)
{
    _logger = logManager.CreateLogger<JavMovieProvider>();
#if __JELLYFIN__
    translationService = Plugin.Instance.TranslationService;
    imageProxyService = Plugin.Instance.ImageProxyService;
#else
    this.translationService = translationService;
    this.imageProxyService = imageProxyService;
#endif
}

public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
    => imageProxyService.GetImageResponse(url, ImageType.Backdrop, cancellationToken);
```

```csharp
if (!string.IsNullOrWhiteSpace(m.Set))
    metadataResult.Item.AddCollection(m.Set);            // #if !__JELLYFIN__
else
    metadataResult.Item.CollectionName = m.Set;          // #if __JELLYFIN__

async Task AddPerson(string personName,
#if __JELLYFIN__
    string
#else
    PersonType
#endif
    personType)
{
    var person = new PersonInfo { Name = personName, Type = personType };
    var url = await Gfriends.FindAsync(person.Name, cancellationToken);
    if (url.IsWebUrl())
        person.ImageUrl = await imageProxyService.GetLocalUrl(url, person_image_type);
    metadataResult.AddPerson(person);
}
```

> `AddPerson` 的 `Type` 形参在 **Emby 是 `PersonType`、Jellyfin 是 `string`** —— 与 §5.3 的 `PersonType.Actor` / `PersonKind.Actor` 对应同一处断裂带。

### 3.5 honue/MediaInfoKeeper（Emby-only，活跃、372 stars）

- 仓库：<https://github.com/honue/MediaInfoKeeper>
- `.csproj`：<https://github.com/honue/MediaInfoKeeper/blob/master/MediaInfoKeeper.csproj>
- 版本门控：<https://github.com/honue/MediaInfoKeeper/blob/master/Version.json>

```xml
<PropertyGroup>
  <TargetFrameworks>net8.0;net6.0</TargetFrameworks>
  <Description>MediaInfoKeeper Emby plugin</Description>
  <AssemblyVersion>1.7.5.5</AssemblyVersion>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>
...
<ItemGroup>
  <PackageReference Include="DiscUtils.Udf" Version="0.16.13"/>
  <PackageReference Include="MediaBrowser.Server.Core" Version="4.9.1.90"/>
  <PackageReference Include="Lib.Harmony" Version="2.4.2"/>
  <PackageReference Include="SQLitePCL.pretty.core" Version="1.2.2"/>
  <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="8.0.1" Condition="'$(TargetFramework)' == 'net8.0'"/>
  <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="6.0.2" Condition="'$(TargetFramework)' == 'net6.0'"/>
</ItemGroup>
<ItemGroup>
  <Reference Include="SQLitePCLRawEx.core">
    <HintPath>Resources\dll\SQLitePCLRawEx.core.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
...
<!-- DiscUtils.Udf brings these runtime assemblies transitively; merge them to avoid Emby plugin load failures. -->
<IlRepackInput Include="$(TargetDir)DiscUtils.Core.dll" ... />
<Exec Command="$(IlRepackExe) /out:$(TargetDir)$(TargetFileName) $(TargetPath) @(IlRepackInput->'%(Identity)', ' ') /lib:$(TargetDir)"/>
```

`Version.json` 原文：

```json
{
  "latest": { "minEmbyVersion": "4.10.0.40", "maxEmbyVersion": "4.10.99.99" },
  "beta":   { "minEmbyVersion": "4.10.0.40", "maxEmbyVersion": "4.10.99.99" }
}
```

> **本仓库唯一的 HintPath 是第三方库 `SQLitePCLRawEx.core.dll`（仓库自带 `Resources/dll/`），不是服务器 DLL。**

### 3.6 Emby 官方 Org（MediaBrowser/）的三个样本

**`MediaBrowser/NfoMetadata`，官方维护的 NFO 元数据插件**（`.csproj` 原文）：

```xml
<PropertyGroup>
  <TargetFrameworks>netstandard2.0;</TargetFrameworks>
  <AssemblyVersion>1.0.86.0</AssemblyVersion>
  <FileVersion>1.0.86.0</FileVersion>
</PropertyGroup>
...
<ItemGroup>
  <PackageReference Include="mediabrowser.server.core" Version="4.8.2" />
  <PackageReference Include="System.Memory" Version="4.5.5" />
</ItemGroup>
```

入口（`Plugin.cs`）：
```csharp
public class Plugin : BasePlugin, IHasThumbImage, IHasWebPages, IHasTranslations
{
    private Guid _id = new Guid("E610BA80-9750-47BC-979D-3F0FC86E0081");
    public ImageFormat ThumbImageFormat => ImageFormat.Png;
    public Stream GetThumbImage() { var type = GetType(); return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png"); }
    public IEnumerable<PluginPageInfo> GetPages() { ... }
    public TranslationInfo[] GetTranslations() { ... }
}
```

服务入口（`EntryPoint.cs`）：
```csharp
using MediaBrowser.Controller.Plugins;
public class EntryPoint : IServerEntryPoint
{
    public EntryPoint(IUserDataManager userDataManager, ILibraryManager libraryManager, ILogger logger,
                      IProviderManager providerManager, IConfigurationManager config)
    { ... }

    public void Run() { _userDataManager.UserDataSaved += _userDataManager_UserDataSaved; }
    public void Dispose() { _userDataManager.UserDataSaved -= _userDataManager_UserDataSaved; }
}
```

> 官方插件用 **`netstandard2.0` + `mediabrowser.server.core 4.8.2`**，明显比第三方新项目（4.9.1.90 / net8.0）保守。

**`MediaBrowser/Emby.Plugins.Anime`**（官方动画元数据 Provider）：
```xml
<TargetFrameworks>netstandard2.0;</TargetFrameworks>
<PackageReference Include="mediabrowser.server.core" Version="4.8.11" />
<PackageReference Include="System.Memory" Version="4.5.5" />
```
其 Provider 目录含 `Providers/AniDB/AniDbExternalId.cs`、`AniDbSeriesProvider.cs`、`AniDbSeriesImagesProvider.cs` —— 与 Jellyfin 的「MetadataProvider / ImageProvider / ExternalId」三件套结构一致。

**`MediaBrowser/Douban`**：`netstandard2.0` + `MediaBrowser.Server.Core 4.6.0.50`。
**`MediaBrowser/Emby.Plugins.MyAnimeList`**：`netstandard2.0` + `mediabrowser.server.core 4.8.11`。

### 3.7 Emby 官方 SDK / 模板（`MediaBrowser/Emby.SDK`）

- 仓库：<https://github.com/MediaBrowser/Emby.SDK>（ReadMe 仅一行：`See https://dev.emby.media`）
- 模板：<https://github.com/MediaBrowser/Emby.SDK/tree/master/SampleCode/Templates>

`EmbyPluginMinimalTemplate.csproj` 原文（全文件）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <Description>Emby Simple Plugin Template</Description>
    <PackageTags>emby;plugin;pms;media;server;</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <None Remove="ThumbImage.png" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="ThumbImage.png" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="MediaBrowser.Server.Core" Version="4.9.1.90" />
  </ItemGroup>

  <Target Name="PostBuild" AfterTargets="PostBuildEvent">
    <Exec Command="copy $(TargetPath) %AppData%\Emby-Server\programdata\plugins\" />
  </Target>
</Project>
```

`Plugin.cs` 原文（骨架）：

```csharp
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;

public class Plugin : BasePlugin, IHasThumbImage
{
    private readonly Guid id = new Guid("00000000-0000-0000-0000-000000000000"); // << Generate one: Tools >> Create GUID
    private readonly ILogger logger;

    public Plugin(ILogManager logManager)
    {
        this.logger = logManager.GetLogger(this.Name);
        this.logger.Info("My sample plugin is loaded", 0);
    }

    public override string Description => "XVXVXVXVXVXVX";
    public override Guid Id => this.id;
    public override sealed string Name => "´XVXVXVXVXVXVX";
    public ImageFormat ThumbImageFormat => ImageFormat.Png;
    public Stream GetThumbImage() { var type = this.GetType(); return type.Assembly.GetManifestResourceStream(type.Namespace + ".ThumbImage.png"); }
    public override void OnUninstalling() { ... }
}
```

`Properties/launchSettings.json` 原文——**官方给出的调试方式就是「直接起 EmbyServer.exe」**：

```json
{
  "profiles": {
    "EmbyPluginSimpleUI": { "commandName": "Project" },
    "EmbyServer": {
      "commandName": "Executable",
      "executablePath": "%AppData%\\Emby-Server\\system\\EmbyServer.exe",
      "commandLineArgs": "-nointerface",
      "workingDirectory": "%AppData%\\Emby-Server\\system"
    }
  }
}
```

---

## 4. 安装方式原文（Emby vs Jellyfin）

### 4.1 Emby：**只有「手动放 plugins 目录」一种方式**（第三方）

**Emby 官方文档原文**（<https://emby.media/support/articles/Plugins.html>）：

> ## Installing Plugins
> To install a plugin, click on one within the plugin catalog. …
> ### Manual Install of Plugins
> Sometimes you may need to install a plugin manually. It could be because there is a new pre-release version made available in the forum or a plugin that is not in the catalogue.
>
> The installed plugins are stored in the "plugins" directory which is below the Emby Server Data Folder.
>
> … If the plugin that is to be installed is in a zip file, unzip it and locate the dll file. Then copy the dll file, eg "Emby.WebStreams.Plugin.dll" to the "plugins" directory.
> Now you can launch Emby Server.

**Emby 团队成员 Luke 的论坛回复原文**（<https://emby.media/community/topic/142301-emby-running-in-docker-how-to-add-catalogrepositories/#findComment-1467730>）：

> You can copy the DLL into your plugins folder manually.

**同帖中另一位资深用户 Neminem 的原文**：

> TBH I have never seen a way to import a external source into Emby.
> That Being said, you can upload plugins to Emby plugin server folder.

→ **结论：Emby 插件目录（Catalog）不能像 Jellyfin 那样填第三方 manifest URL 订阅。** 第三方 Emby 插件只能手动投放 dll。

**插件目录（Data Folder 下的 `plugins/`）真实路径**（<https://emby.media/support/articles/Server-Data-Folder.html>）：

| 平台 | Data Folder | 插件目录 |
|---|---|---|
| Windows 安装版 | `C:\Users\{user}\AppData\Roaming\Emby-Server\programdata` | `…\programdata\plugins\` |
| Windows 便携版 | （Emby 根目录） | `<Emby 根目录>\programdata\plugins\` |
| Linux | `/var/lib/emby` | `/var/lib/emby/plugins/` |
| Docker | `/config` | `/config/plugins/` |
| Synology DSM 7 | `/volume1/@appdata/EmbyServer` | `…/plugins` |
| QNAP | `/share/CACHEDEV1_DATA/.qpkg/EmbyServer/programdata` | `…/plugins` |
| macOS | `/Users/{user}/emby-server` | `…/plugins` |

### 4.2 各项目 README 原文

**ThePornDB README**（<https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/blob/main/README.md>）：

```
## Install
- Repository (Jellyfin only):
  - Add to the list this URL `https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/manifest.json`
- Manual:
  - Download Archive from [Latest Release](https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/releases/latest)
  - Follow the [Instruction](https://jellyfin.org/docs/general/server/plugins/index.html)
```

> **「Repository (Jellyfin only)」是决定性原文**：连仓库作者自己都明确标注「URL 订阅只是 Jellyfin 的功能」。

**Pronium README 原文**（<https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/blob/dev/README.md>）：

```
## Install

### Jellyfin
- Repository:
  - Add to the list this URL `https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/master/manifest.json`
- Manual:
  - Download Archive from [Latest Release](https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/releases/latest)
  - Follow the [Instruction](https://jellyfin.org/docs/general/server/plugins/index.html)

### Emby (Synology)
1. Download an archive from [Latest Release](https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/releases/latest)
1. Stop EMBY
1. Copy DLL file to any location on your NAS (e.g. `home`)
1. Run script to copy file with permissions

  ```zsh
  sudo -i

  cp --no-preserve=mode /volume1/home/Emby.Plugins.Pronium.dll /volume1/@appdata/EmbyServer/plugins

  chmod 777 /volume1/@appdata/EmbyServer/plugins/Emby.Plugins.Pronium.dll
  ```
```

> **Emby 段落里完全没有「Repository / Add to the list this URL」**，只有手动 copy + chmod。

**JavScraper README 原文**（<https://github.com/JavScraper/Emby.Plugins.JavScraper/blob/master/README.md>）：

```
## 插件安装
- [点击这里下载最新的插件文件](https://github.com/JavScraper/Emby.Plugins.JavScraper/releases)，解压出里面的 **JavScraper.dll** 文件，通过ssh等方式拷贝到 Emby 的插件目录
- 常见的插件目录如下：
  - 群晖
    - /volume1/Emby/plugins
    - /var/packages/EmbyServer/var/plugins
    - /volume1/@appdata/EmbyServer/plugins
  - Windows
    - emby\programdata\plugins
- 需要**重启Emby服务**，插件才生效。

## 插件更新
- 打开 **JavScraper** 配置页面的时，会自动检查更新（在页面的最下方）。
- 如果有更新，则点击**立即更新**，并在**重启 Emby Server** 后生效。
```

> JavScraper 用**自建更新服务**代替 URL 订阅：`Services/UpdateService.cs` 里注册了 `[Route("/emby/Plugins/JavScraper/Update", "GET")]` 的 `IService`，配置页按钮点击时下载并替换 dll。

**MediaInfoKeeper README 原文**（<https://github.com/honue/MediaInfoKeeper/blob/master/README.md>）：

```
⏬ 安装
1. 下载 dll 文件：[Releases](https://github.com/honue/MediaInfoKeeper/releases)
   不带后缀的是通用版本，[最新通用版本](https://github.com/honue/MediaInfoKeeper/releases/latest/download/MediaInfoKeeper.dll)。
2. 放入 Emby 配置目录中的 `plugins` 目录。
3. 务必重命名为 `MediaInfoKeeper.dll`，否则后续自动更新会有两个 dll 存在。
4. 重启 Emby，在插件页面完成配置。
```

**emby-plugin-bangumi README 原文**（<https://github.com/Whereis-Alice/emby-plugin-bangumi/blob/main/README.md#L137>）：

```
## 安装

### 从 Release 安装

1. 从 [Releases](...) 下载 `Emby.Plugins.Bangumi.dll`（同时提供 `.sha256`，由 `release.yml` 在打 `v*` tag 时自动构建上传）。
2. 放进 Emby 的插件目录：
   - Windows 便携版：`<Emby 根目录>\programdata\plugins\`
   - Windows 安装版：`%AppData%\Emby-Server\programdata\plugins\`
   - Linux：`/var/lib/emby/plugins/`
   - Docker：`/config/plugins/`
3. **重启 Emby Server**（Emby 只在启动时加载插件程序集）。
4. 控制台 → 插件 → 「Bangumi 番组计划」，按需填写代理地址。
5. 媒体库 → 编辑 → 「元数据下载器」里勾选 Bangumi，并调整顺序。
```

**Emby 官方 wiki（legacy）**（<https://github.com/MediaBrowser/Emby/wiki/How-to-build-a-Server-Plugin>）：

> ## Create a Post-Build Event
> Right click the project -> Properties. Create a post-build event that will copy the assembly to the server's plugins directory. For example:
> `xcopy "$(TargetPath)" "%AppData%\Emby-Server\Plugins\" /y`

### 4.3 结论

| 维度 | Jellyfin | Emby |
|---|---|---|
| URL 订阅（manifest.json） | ✅ 内置「添加仓库 URL」 | ❌ **无** |
| 内置 Catalog | 需先加仓库 | ✅ 有，但源由 Emby 服务端控制，第三方进不去 |
| 手动安装 | 放 `plugins/<Name>/`（可带依赖 dll） | 放 `<data>/plugins/`，**单个 dll**（重名需先改名） |
| 依赖处理 | 可整目录拷贝 | 惯例：ILRepack 合并成单 dll |
| 更新 | manifest 自动检测 | 手动替换 / 自建更新端点 / 项目自带后台更新任务 |

---

## 5. ProviderIds / ExternalId / 人物头像 的真实处理方式

### 5.1 `ProviderIds` 键名与写入时机

**Pronium `MovieProvider.GetMetadata`**（<https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/blob/dev/Jellyfin.Plugin.Pronium/Providers/MovieProvider.cs>）：

```csharp
var sceneID = info.ProviderIds;
if (sceneID.TryGetValue(this.Name, out var externalID))
{
    curID = externalID.Split('#');
}

if ((!sceneID.ContainsKey(this.Name) || curID == null || curID.Length < 3) && !Plugin.Instance.Configuration.DisableAutoIdentify)
{
    var searchResults = await this.GetSearchResults(info, cancellationToken).ConfigureAwait(false);
    if (searchResults.Any())
    {
        var first = searchResults.First();
        sceneID = first.ProviderIds;
        sceneID.TryGetValue(this.Name, out externalID);
        curID = externalID.Split('#');
    }
}
...
result.Item.ProviderIds.Update(this.Name, sceneID[this.Name]);
...
if (!string.IsNullOrEmpty(result.Item.ExternalId))
{
    result.Item.ProviderIds.Update(this.Name + "URL", result.Item.ExternalId);   // 第二个键：<Name>URL
}
```

搜索阶段就会把内部 ID **打包进 `RemoteSearchResult.ProviderIds[this.Name]`**，并用 `#` 拼多段：

```csharp
scene.ProviderIds[this.Name] = $"{site.siteNum[0]}#{site.siteNum[1]}#" + scene.ProviderIds[this.Name];
```

**ThePornDB `Providers/Base.cs`**——识别对话框里输入的值也会先落进 `ProviderIds`：

```csharp
var curID = searchInfo.Name.GetAttributeValue("theporndbid") ?? searchInfo.Name.GetAttributeValue("TPDBID");
if (string.IsNullOrEmpty(curID))
{
    searchInfo.ProviderIds.TryGetValue(Plugin.Instance.Name, out curID);
}
else
{
    curID = prefixID + curID;      // prefixID 平台相关：Emby "scenes\\" / Jellyfin "scenes/"
}
...
if (result.HasMetadata)
{
    result.Item.ProviderIds.Add(Plugin.Instance.Name, curID);
    result.Item.OfficialRating = "XXX";
}
```

**同文件里的平台路径分隔符差异（非常有代表性）**：

```csharp
case SceneType.Scene:
#if __EMBY__
    prefixID = "scenes\\";
#else
    prefixID = "scenes/";
#endif
```

**bangumi 的做法（多 ID 空间，每个给一个独立 ExternalId 类）**：

```csharp
result.Item.ProviderIds[BangumiConstants.PersonProviderId] = detail.Id.ToString(CultureInfo.InvariantCulture);
```
```csharp
// README 字段映射表原文
| `ProviderIds` | `Bangumi` / `BangumiEpisode` / `BangumiPerson` / `BangumiCharacter` |
```
> README 补充原文：**「角色 ID 与人物 ID 是两套命名空间。`/v0/characters/42` 和 `/v0/persons/42` 是毫无关系的两个东西，所以角色用独立的 `BangumiCharacter` 键，不能混进 `BangumiPerson`。」**

### 5.2 `IExternalId` 两平台实现差异（**本项目必须注意**）

| 成员 | Emby（`MediaBrowser.Controller.Providers.IExternalId`） | Jellyfin（`MediaBrowser.Controller.Providers.IExternalId`） |
|---|---|---|
| 显示名 | `string Name` | `string ProviderName` |
| 类型 | —（无） | `ExternalIdMediaType? Type` |
| 键 | `string Key` | `string Key` |
| URL 模板 | `string UrlFormatString`（+ 可选 `IHasWebsite.Website`） | `string UrlFormatString` |
| 支持判定 | `bool Supports(IHasProviderIds item)` | `bool Supports(IHasProviderIds item)` |

原文对照见 §3.1（Pronium `MovieID.cs` / `ActorID.cs`）与 §3.3（bangumi `BangumiExternalId.cs`）。

此外 ThePornDB 在 Emby 分支额外实现 `IHasSupportedExternalIdentifiers`：

```csharp
#if __EMBY__
    public class Peoples : IRemoteMetadataProvider<Person, PersonLookupInfo>, IHasSupportedExternalIdentifiers
#else
    public class Peoples : IRemoteMetadataProvider<Person, PersonLookupInfo>
#endif
...
#if __EMBY__
        public string[] GetSupportedExternalIdentifiers()
        {
            return new[] { Plugin.Instance.Name };
        }
#endif
```
（`IHasSupportedExternalIdentifiers` 经程序集扫描确认只存在于 `MediaBrowser.Controller.dll`。）

### 5.3 `PersonKind` vs `PersonType`（**本项目必须注意**）

ThePornDB `Providers/MetadataAPI.cs` 原文：

```csharp
#if __EMBY__
#else
using Jellyfin.Data.Enums;
#endif
...
var actor = new PersonInfo
{
    Name = name,
#if __EMBY__
    Type = PersonType.Actor,     // MediaBrowser.Model.Entities.PersonType
#else
    Type = PersonKind.Actor,     // Jellyfin.Data.Enums.PersonKind
#endif
};

switch (Plugin.Instance.Configuration.ActorsImage)
{
    case ActorsImageStyle.Face:
        actor.ImageUrl = face;
        break;
    case ActorsImageStyle.Poster:
        actor.ImageUrl = image;
        break;
}

if (!string.IsNullOrEmpty(curID))
{
    actor.ProviderIds.Add(Plugin.Instance.Name, curID);
}
...
result.AddPerson(actor);
```

Pronium `Helpers/Actors.cs` 原文：

```csharp
#if __EMBY__
#else
using Jellyfin.Data.Enums;
#endif
...
var newPeople = new PersonInfo
{
    Name = people.Name,
    Type = people.Type,
    ImageUrl = people.ImageUrl,
};
...
#if __EMBY__
#else
    newPeople.Type = PersonKind.Actor;
#endif
```

JavScraper `JavMovieProvider.cs` 原文（`PersonType` 直接当形参类型，Jellyfin 侧退化成 `string`）：

```csharp
async Task AddPerson(string personName,
#if __JELLYFIN__
    string
#else
    PersonType
#endif
    personType)
```

### 5.4 **人物头像的两种真实做法**

**做法 A（推荐、最少代码）：Provider 里直接给 `PersonInfo.ImageUrl`，让宿主自己下载。**

- ThePornDB：`actor.ImageUrl = face;` / `actor.ImageUrl = image;`（见上）
- Pronium：`Actors.Cleanup` 里 `ImageUrl = people.ImageUrl` 原样透传
- JavScraper：`person.ImageUrl = await imageProxyService.GetLocalUrl(url, person_image_type);`（**改写成本机代理地址**）

**做法 B：独立的 Person 图片 Provider**（bangumi 的做法，与做法 A 并存）：

```csharp
public class BangumiPersonImageProvider : BangumiProviderBase, IRemoteImageProvider, IHasOrder
{
    public bool Supports(BaseItem item) => item is Person;
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)   // ← Emby 签名
    {
        ...
        images.Add(new RemoteImageInfo
        {
            ProviderName = BangumiConstants.PluginName,
            Type = ImageType.Primary,
            Url = url,                       // 全尺寸
            ThumbnailUrl = source.Thumbnail() // 搜索/缩略
        });
        return images;
    }
}
```

**人物搜索结果的头像单独走 `RemoteSearchResult.ImageUrl`**：
```csharp
var result = new RemoteSearchResult
{
    Name = ...,
    SearchProviderName = BangumiConstants.PluginName,
    Overview = person.Summary,
    ImageUrl = person.Images == null ? null : person.Images.Thumbnail(),
    PremiereDate = person.BirthDate(),
};
```
以及 `MetadataResult<Person>.SearchImageUrl = detail.Images.Best();`

JavScraper 的搜索缩略图同理（`JavMovieProvider.cs`）：
```csharp
ImageUrl = await imageProxyService.GetLocalUrl(m.Cover, with_api_url: false),
```

> ⚠️ **注意与 Amane 现状的对应关系**：本项目的「图片 URL 双轨制」（`ToProxyImageUrl` 用于下载路径 / `ToDirectImageUrl` 用于直出路径）在生态里**没有现成同样方案**；Pronium/JavScraper 都把 `PersonInfo.ImageUrl` 改写成「本机代理地址」（`imageProxyService.GetLocalUrl`），即 **Amane 的 `ToDirectImageUrl` 思路**。

### 5.5 图片下载路径（`GetImageResponse`）的真实实现

**Pronium `Helpers/Helper.cs` 原文**（<https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/blob/dev/Jellyfin.Plugin.Pronium/Helpers/Helper.cs#L341>）：

```csharp
#if __EMBY__
        public static Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return Plugin.Http.GetResponse(new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url,
                EnableDefaultUserAgent = false,
                UserAgent = HTTP.GetUserAgent(),
            });
        }
#else
        public static Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", HTTP.GetUserAgent());

            return Plugin.Http.CreateClient().SendAsync(request, cancellationToken);
        }
#endif
```

`HttpRequestOptions` / `HttpResponseInfo` 来自 **`MediaBrowser.Common.Net`**（Emby 分支的 using）。

**ThePornDB**：把同样的事收进 `Helpers/UGetImageResponse.SendAsync(url, cancellationToken)`，签名按 `#if __EMBY__` 分支（见 §3.2 的 `Providers/Peoples.cs`）。

**bangumi（Emby-only）**：`Web/BangumiUiService.cs` 自建图片代理端点，README 原文：
> | 代理角色 / 人物图片 | 开 | 图片走插件代理，避免浏览器直连 Bangumi 图床（无代理环境下会全是破图）|

> 与 Amane 的 `ToProxyImageUrl` 目的完全一致。

---

## 6. 兼容性风险的真实证据（issue / commit / README）

### 6.1 Emby 4.8 → 4.9 breaking：`GetImages` 增加 `LibraryOptions`

**证据 A（源码）**：所有双平台项目在 Emby 分支都多一个形参——
`Pronium` 的 `MovieImageProvider`/`ActorImageProvider`、`ThePornDB` 的 `MovieImageProvider`、`bangumi` 的 `BangumiPersonImageProvider` 全部是：

```csharp
#if __EMBY__
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken)
#else
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
#endif
```

**证据 B（issue）**：[ThePornDB #95「Can the author adapt to a higher version of emby?」](https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/issues/95)（2025-01-05）：

> Hello author, I don't know if this plugin can be upgraded to be compatible with higher versions of emby above 4.8. Currently, the options cannot be configured on higher versions of emby. Thanks

维护者回复只是给了 `releases/tag/latest` 的地址。

**证据 C（issue）**：[ThePornDB #112「emby 4.9.1.90 isn't working」](https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/issues/112)（2025-11-16，closed）：

> Hey , emby 4.9.1.90 isn't working. Can a admin please fix it?

用户追加：
> try dev build a085901 + emby 4.9.1.90 still isn't working
> `2025-11-17 23:42:01.591 Error ThePornDB: Request error: The operation has timed out.`
> `2025-11-17 23:42:01.592 Error ThePornDB: API error: "Unknown error"`
> **but old version 1.5.0.10 + emby 4.8.10.0 all good.**

维护者回复：
> works for me, and doesn't work and timeout error it's not the same

→ 最终用户放弃（"I will continue to wait upgrade"）。

**证据 D（issue，**仍 open**）**：[ThePornDB #123「Blank actor page after updating to Emby 4.9.3.0」](https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/issues/123)（2026-05-18，0 评论）：

> Emby version: 4.9.3.0
> Plugin version: Development build
> Hi, since updating to this new version of Emby, whenever I click on an actor, the page stays blank and nothing loads.

→ **Emby 4.9.x 的小版本升级会直接把「人物页」打坏**，这正是 §5.4 里人物头像/人物 Provider 所在的位置。

### 6.2 双平台项目「一边升级、另一边掉队」的实证

- ThePornDB HEAD commit：`2026-09-10T00:27:50Z` **「update to jellyfin 12」**，但 issue #123（Emby 4.9.3.0 人物页空白）仍 open 未修 → **Jellyfin 侧优先，Emby 侧滞后**。
- Pronium `manifest.json` 里所有版本的 `targetAbi` 都是 **`10.8.0.0`**，即使 changelog 已写「Add support for Jellyfin v12 API」——**Jellyfin 侧的兼容声明常年不更新**（原文见 §3.1 抓取的 manifest 片段）。
- JavScraper `manifest.json` 里 `targetAbi` 停在 **`10.6.0.0` / `10.7.0.0`**，最新 Release 时间 `2021-06-22` → **五年未发版**，3796 stars 的项目实质停摆。

### 6.3 Jellyfin 侧同类断裂（对 Amane 直接相关）

**Pronium issue #121**（<https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/issues/121>，2024-11-02）：

> **Jellyfin or Emby**：10.10.10 ｜ **Plugin Version**：2.1.5.94
>
> ```
> System.MissingMethodException: Method not found: 'System.String MediaBrowser.Controller.Entities.PersonInfo.get_Type()'.
>    at Pronium.Helpers.Actors.Cleanup(MetadataResult`1 scene)
> ```

维护者 2024-11-03 回复：

> It looks like it's related to Jellyfin new version 10.10. I'll try to update dependencies and fix this issue.
> https://github.com/jellyfin/jellyfin/pull/9487

→ **Jellyfin 10.10 把 `PersonInfo.Type` 从 `string` 改成 `PersonKind`，老插件直接 `MissingMethodException`**。这与 §5.3 的 `PersonKind` / `PersonType` 是同一处 ABI 断点，也是本项目（Jellyfin 10.11 / net9.0）必须盯住的地方。

### 6.4 项目方的主动防御做法

**MediaInfoKeeper README 原文**（<https://github.com/honue/MediaInfoKeeper/blob/master/README.md>）：

```
🧩 兼容性
- 版本说明：本仓库最新代码始终以支持 Emby 最新 release 为目标，开发过程中可能出现阶段性兼容问题。
- 当前插件 `latest` 版本适配Emby `4.10.0.40`，全平台支持。
- 更新限制：插件自动更新任务已按 [版本区间](Version.json) 限制，不会更新到当前 Emby 不支持的插件版本。
- 不支持：`4.8` 系列，`4.9.5.0` 最后支持版本 `v1.7.5.3`。
```

`Version.json` 原文：
```json
{
  "latest": { "minEmbyVersion": "4.10.0.40", "maxEmbyVersion": "4.10.99.99" },
  "beta":   { "minEmbyVersion": "4.10.0.40", "maxEmbyVersion": "4.10.99.99" }
}
```

**bangumi README 原文**：
> 针对 Emby 4.9.x–4.10.x / .NET 8 编写，CI 编译目标 ABI `4.9.1.90`，并在 `4.10.0.40` 实机验证。

**bangumi README「已知限制」原文（前端注入类改动会被宿主升级覆盖）**：
> - 角色 / 制作栏依赖 `.peopleSection`，选集依赖 `.seriesItemsSection`、`trackListSection` 或 `moreFromSeasonSection` 定位插入点。**Emby 前端改版会让对应增强失效**（原生页面仍保留，不会报错）。
> - Emby 会**覆写插件设置的 `Cache-Control`**，实际返回 `no-store`，所以图片代理的浏览器缓存没生效……

**bangumi README 关于 `ReplaceAllMetadata` 覆盖编号的原文**（对任何 Provider 都成立）：
> Emby 的 `MergeBaseItemData` 在 `ReplaceAllMetadata=true` 时会**无条件**用 provider 的返回值覆盖这三个字段。provider 把它们留在默认的 `null` 上不等于「尊重文件名」，而是**把编号清空**。

**Emby 官方 Org 自己也很保守**：`NfoMetadata` 仍停留在 `mediabrowser.server.core 4.8.2`、`Emby.Plugins.Anime`/`MyAnimeList` 停在 `4.8.11`，而第三方新项目已经用 `4.9.1.90`。

### 6.5 弃坑证据

[xjasonlyu/jellyfin-plugin-avdc](https://github.com/xjasonlyu/jellyfin-plugin-avdc)：描述 `Metadata Provider Plugin for Jellyfin/Emby.`，294 stars，**`archived: true`**，`pushed_at 2025-06-07`。

---

## 7. 打包结构（zip 内容）——**实测解包**

### 7.1 CI 定义原文

**Pronium `.github/workflows/release.yml`**（<https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/blob/dev/.github/workflows/release.yml>）：

```yaml
  jellyfin:
    steps:
      - name: Build app for release
        run: MSBuild -t:Restore,Build -p:RestorePackagesConfig=true -property:Configuration=Release
      - name: Rename Files
        run: ren Jellyfin.Plugin.Pronium/bin/Release/net10.0/Pronium.dll Jellyfin.Plugin.Pronium.dll && ren ...Pronium.pdb Jellyfin.Plugin.Pronium.pdb
      - uses: vimtor/action-zip@v1
        with:
          files: .../net10.0/Jellyfin.Plugin.Pronium.pdb .../net10.0/Jellyfin.Plugin.Pronium.dll
          dest: Jellyfin.Plugin.Pronium.zip
  emby:
    steps:
      - name: Build app for release
        run: MSBuild -t:Restore,Build -p:RestorePackagesConfig=true -property:Configuration=Release.Emby
      - name: Rename Files
        run: ren .../Release.Emby/netstandard2.1/Pronium.dll ./Emby.Plugins.Pronium.dll && ren ...Pronium.pdb ./Emby.Plugins.Pronium.pdb
      - uses: vimtor/action-zip@v1
        with:
          files: .../Release.Emby/netstandard2.1/Emby.Plugins.Pronium.pdb .../Emby.Plugins.Pronium.dll
          dest: Emby.Plugins.Pronium.zip
```

**ThePornDB `release.yml`** 同构，仅版本/路径不同；`deploy` job 把两个 zip 一起发到 `latest` tag。

**bangumi `release.yml`**（<https://github.com/Whereis-Alice/emby-plugin-bangumi/blob/main/.github/workflows/release.yml>）：

```yaml
      - name: Collect artifacts
        run: |
          mkdir -p dist
          cp src/Emby.Plugins.Bangumi/bin/Release/Emby.Plugins.Bangumi.dll dist/
          cd dist
          sha256sum Emby.Plugins.Bangumi.dll > Emby.Plugins.Bangumi.dll.sha256
```

**JavScraper `.csproj` 的 `Zip` target**：`<ZipDirectory SourceDirectory="@(TempZipDirectory)" …>`，而 `TempZipDirectory` 里只 `Copy` 了 `$(AssemblyName).dll`。

### 7.2 **实测解包结果**（本机下载真实 Release 资产）

| 资产 URL | zip 大小 | 内容 |
|---|---|---|
| `https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/releases/download/latest/Emby.Plugins.ThePornDB.zip` | 603,713 B | `Emby.Plugins.ThePornDB.dll` (1,171,456 B) + `Emby.Plugins.ThePornDB.pdb` (221,664 B) |
| `https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/releases/download/latest/Jellyfin.Plugin.ThePornDB.zip` | 80,863 B | `Jellyfin.Plugin.ThePornDB.dll` (169,984 B) + `Jellyfin.Plugin.ThePornDB.pdb` |
| `https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/releases/download/2.2.0.153/Emby.Plugins.Pronium.zip` | 1,184,921 B | `Emby.Plugins.Pronium.dll` (1,945,088 B) + `Emby.Plugins.Pronium.pdb` |

### 7.3 结论

1. **zip 里只有「自己的一份 dll + pdb」，从不包含 `MediaBrowser.*.dll`**（3 个资产全部验证）。
2. 第三方依赖（HtmlAgilityPack / Newtonsoft.Json / LiteDB / HttpClient.Caching / RateLimiter / DiscUtils / 0Harmony / TinyPinyin / StandardSocketsHttpHandler …）用 **ILRepack 合并进主 dll**，所以 Emby 版 dll 明显更大（1.17–1.95 MB）。
3. ILRepack 的配置形态有两种：
   - 独立 `ILRepack.targets` + `ILRepack.Lib.MSBuild.Task` NuGet 包（Pronium / ThePornDB）；
   - csproj 内 `<Exec>` 调 `ilrepack` 工具（JavScraper 用 `ILRepack` package + `$(ILRepack)`；MediaInfoKeeper 用 `~/.dotnet/tools/ilrepack`）。
4. **Emby 侧必须合并依赖**的直接原因，MediaInfoKeeper csproj 里有一句原文注释：
   > `<!-- DiscUtils.Udf brings these runtime assemblies transitively; merge them to avoid Emby plugin load failures. -->`

### 7.4 附带：`MediaBrowser.Server.Core 4.9.1.90` 包的构成（程序集级取证）

`https://api.nuget.org/v3-flatcontainer/mediabrowser.common/4.9.1.90/mediabrowser.common.4.9.1.90.nupkg` 解包后：

```
lib\netstandard2.0\MediaBrowser.Common.dll   (50,688 B)
lib\netstandard2.0\MediaBrowser.Model.dll    (481,280 B)
lib\netstandard2.0\Emby.Media.Model.dll      (218,112 B)
lib\netstandard2.0\Emby.Web.GenericEdit.dll  (190,976 B)
```

`https://api.nuget.org/v3-flatcontainer/mediabrowser.server.core/4.9.1.90/mediabrowser.server.core.4.9.1.90.nupkg`：

```
lib\netstandard2.0\MediaBrowser.Controller.dll (575,488 B)
lib\netstandard2.0\Emby.Naming.dll             (56,832 B)
```

对 DLL 做字符串堆扫描（UTF-8 名字表）命中：

| 程序集 | 命中的公开 API 名 |
|---|---|
| `MediaBrowser.Controller.dll` | `IExternalId`、`IHasWebsite`、`IRemoteMetadataProvider`、`IRemoteImageProvider`、`BasePluginSimpleUI`、`IHasSupportedExternalIdentifiers`、`MediaBrowser.Controller.Providers`、`HttpResponseInfo` |
| `MediaBrowser.Model.dll` | `IHasWebPages`、`PersonType`、`ILogManager`、`LibraryOptions`、`MediaBrowser.Model.Providers` |
| `MediaBrowser.Common.dll` | `IHasThumbImage`、`IHttpClient`、`HttpResponseInfo`、`MediaBrowser.Common.Net` |

NuGet 版本时间线（`https://api.nuget.org/v3-flatcontainer/mediabrowser.server.core/index.json`，末几个）：
`4.8.11 → 4.9.0.6-beta → … → 4.9.1.80 → 4.9.1.90 → 4.10.0.1-beta → 4.10.0.24-beta → 4.10.0.24-beta2`
→ **当前 stable 就是 `4.9.1.90`；4.10 只有 beta**。这解释了为什么所有活跃项目都钉 `4.9.1.90`。

---

## 8. 【重点】Jellyfin + Emby 双平台支持的三种架构模式与原文证据

### 模式 A：**同一份源码 + 两个 MSBuild Configuration + 两个 TFM + 条件编译**（主流）

**使用者**：Pronium、ThePornDB。
**原文证据**：§3.1 / §3.2 的 csproj（`Configurations>Debug;Release;Release.Emby;Debug.Emby<`、`TargetFramework Condition`、`DefineConstants __EMBY__`）。

- 优点：一套业务逻辑、一套数据模型、CI 两个 job 并行出两个 zip；共享比例极高（只有 API 形状差异处加 `#if`）。
- 代价：源码里散布 `#if __EMBY__`（Pronium `MovieProvider.cs` 单文件就有十几处）；两个 TFM 让 C# 语言版本被较低那个约束（Pronium 被迫 `LangVersion 8`）；每次 Jellyfin/Emby 改 API 都要动代码。
- 有趣细节：**两个平台各自一套配置页 HTML**，用同一个 `<LogicalName>` 覆盖，使 `GetPages()` 代码可以完全共用。

### 模式 B：**同 TFM + 两个 Configuration + `__JELLYFIN__` 反向宏 + 编译期扩展方法兼容层**

**使用者**：JavScraper。
**原文证据**：§3.4。`<TargetFramework>netstandard2.1</TargetFramework>`（无 Condition），只切换 PackageReference（`MediaBrowser.Server.Core 4.6.0.50-*` vs `Jellyfin.Controller 10.4-*`）+ `__JELLYFIN__`；主体代码按 Emby 形状写，Jellyfin 差异全部塞进 `Extensions/JellyfinExtensions.cs`。

- 优点：主代码最干净。
- **致命缺陷（已被时间验证）**：兼容层是为「某一个 Jellyfin 版本」写死的。Jellyfin 10.4 仍有 `HttpResponseInfo`、`ILogManager`、`IFileSystem.DirectoryExists` 这类 Emby 形状 API；后续版本移除后，兼容层失效而**主代码不受影响，所以报错位置极难定位**。该项目 2021 年后停更即是代价的体现。
- 另注：它把「Emby 作为默认、Jellyfin 作为变体」，与 Pronium/ThePornDB 的选择相反——**说明这不是「必须这样」，而是历史包袱选择**。

### 模式 C：**两个独立仓库 / 两个独立项目**

**未在存活项目中找到实例**（检索到的都是要么双平台同仓、要么单平台）。
唯一近似的是 `Whereis-Alice/emby-plugin-bangumi` 明确 Emby-only，并在 README 里点名它的 Jellyfin 对照实现 [kookxiang/jellyfin-plugin-bangumi](https://github.com/kookxiang/jellyfin-plugin-bangumi)：
> [`kookxiang/jellyfin-plugin-bangumi`](https://github.com/kookxiang/jellyfin-plugin-bangumi) —— Jellyfin 侧的同类实现，`ProviderId` 的 key 沿用了它的 `"Bangumi"` 以便迁移

→ 即「**同一功能、两个仓库、两个作者**」，靠约定 `ProviderId` 键名一致来互相迁移。这是模式 C 在真实世界里的形态：**不是被刻意选择的架构，而是社区自然分裂的结果**。

### 模式 D：**运行时反射适配**

**在本次取证范围内没有找到实例。** 所有项目的平台差异都在**编译期**用 `#if` 解决。唯一接近「运行时」的是 `Retrofit`：
- MediaInfoKeeper 用 **Lib.Harmony（运行时方法补丁）** 修改 Emby 行为（但它是 Emby-only，不是跨平台适配）；
- bangumi 用 `IServerEntryPoint` + 目录探测（运行时找 `dashboard-ui/index.html`），但那是宿主差异探测，不是 API 适配。

→ **结论：主流共识是编译期条件编译，不是运行时反射。** 运行时反射做跨平台适配在 Emby/Jellyfin 生态里没有可引用的先例。

### 8.1 面向本项目的架构建议

本项目（`Jellyfin.Plugin.Amane`）现状：**根目录单个 csproj，`net9.0`，`Jellyfin.Controller`/`Jellyfin.Model` + `PrivateAssets=all`，`BasePlugin<T> + IHasWebPages`，`IRemoteMetadataProvider<Movie, MovieInfo>` / `IRemoteMetadataProvider<Person, PersonLookupInfo>` / `IRemoteImageProvider` / `IExternalId`×2，`IApiController` 诊断端点，`IPluginServiceRegistrator`。**

对照生态证据，若要新增 Emby 支持，**证据支持**的最小改动是 Pronium/ThePornDB 模式 A 的裁剪版：

1. csproj 加 4 个 Configuration：`Debug;Release;Debug.Emby;Release.Emby`；加 Emby 条件 TFM（生态用 `netstandard2.1`；本项目业务代码是纯 HTTP + DTO，`netstandard2.1` 预计可行——**需要实机验证**）。
2. Emby 分支引用 `MediaBrowser.Server.Core 4.9.1.90` + `MediaBrowser.Common 4.9.1.90`（`PrivateAssets="all" ExcludeAssets="runtime"`）。
3. 需要 `#if __EMBY__` 的位置（按生态实测清单，共 8 处）：
   - `Plugin.cs`：`BasePlugin<T> + IHasWebPages` vs `BasePluginSimpleUI<T>`；ctor 参数 `IHttpClientFactory`+`ILogger<T>` vs `IHttpClient`+`ILogManager`。
   - 所有 `IRemoteImageProvider.GetImages`：加 `LibraryOptions` 形参。
   - 所有 `GetImageResponse`：返回 `Task<HttpResponseMessage>` vs `Task<HttpResponseInfo>`（`MediaBrowser.Common.Net`）。
   - 所有 `IExternalId`：`ProviderName`+`Type` vs `Name`(+可选 `IHasWebsite`/`UrlFormatString`)。
   - `PersonInfo.Type`：`PersonKind.Actor` vs `PersonType.Actor`。
   - `PersonInfo` 类型位置、`PremiereDate` 是 `DateTime?`（Emby）还是 `DateTimeOffset?`（Jellyfin）→ 生态到处是 `#if __EMBY__ searchDateObj = info.PremiereDate.Value.DateTime; #else ... #endif`。
   - 路径分隔符字面量（`"scenes\\"` vs `"scenes/"`）。
   - `IHasSupportedExternalIdentifiers.GetSupportedExternalIdentifiers()`（Emby 独有）。
4. **`Api/AmaneDiagnosticsController.cs` 是最大不确定项**：本项目用 ASP.NET Core `[ApiController]`，而 Emby 4.9 是自研 `IService`/`IReturn<T>` 路由体系（JavScraper 的 `UpdateService` 就是 `IService + [Route(...)]`）。**Emby 分支需要另写一套端点，或砍掉诊断端点——需要实机验证。**

---

## 9. 需要实机验证清单（无法从公开文件确认的事项）

| # | 待验证项 | 为什么无法从公开证据确认 |
|---|---|---|
| V1 | Emby 4.9.x/4.10 是否真的**无法**添加第三方插件目录 URL（含 Emby Premiere / 企业版） | 官方文档只说「manual install」，论坛回复是用户经验；未查 Emby Server 反编译/配置项 |
| V2 | `netstandard2.1` 能否承载本项目现有代码（`SemaphoreSlim` 背压、`HttpClient`、`System.Text.Json`/`[JsonPropertyName]`） | 生态里 Emby 侧都编过了，但用的是 `Newtonsoft.Json`；本项目用 `System.Text.Json`，`netstandard2.1` 下需验证（**Amane 侧应有可用包，但需实机编译**） |
| V3 | Emby 4.9.1.90 的 `IRemoteMetadataProvider<Movie, MovieInfo>` 泛型约束与 Jellyfin 是否一致 | 只做了程序集名字扫描，未做真实编译 |
| V4 | Emby 里 `PluginPageInfo.EnableInMainMenu`/`MenuSection`/`MenuIcon`/`DisplayName` 的可用性 | JavScraper 用了，但其 Emby 目标是 4.6；4.9 下需验证 |
| V5 | Emby 4.9 的自研 REST（`IService`/`IReturn<T>`）能否承载本项目 `[ApiController]` 风格端点 | 需要在真实 Emby 上跑 |
| V6 | Emby 是否支持 `IHasWebPages`（本项目配置页依赖 `IHasWebPages`） | 官方模板用 `BasePluginSimpleUI`；Pronium 的 Emby 分支确实写了 `IHasWebPages` 且能编译，但**运行时是否渲染**未知 |
| V7 | Emby 4.9 人物页（Person entity）是否支持 `PersonInfo.ImageUrl` 自动下载 | 多个 Provider 都这么写（ThePornDB/Pronium/JavScraper），但 **ThePornDB #123「人物页空白」尚 open**，需实机确认 |
| V8 | 本项目「图片 URL 双轨制」（`ToProxyImageUrl`/`ToDirectImageUrl`）在 Emby 下的对应行为 | Emby 的图片消费路径未取证；生态项目全部用「改写成本机代理地址」一种做法，无直出/下载双轨先例 |
| V9 | `MediaBrowser.Server.Core` 是否有 4.10 stable | NuGet 上只见 `4.10.0.24-beta2`；Emby 4.10 正式版对应的包是否已发布需复查（本机查询时间点） |
| V10 | `JavScraper` `pushed_at 2024-07-08` 与 master HEAD `2021-06-22` 的差异来源 | GitHub API 被限流，未能枚举分支 |

---

## 10. 全部引用 URL 清单

### 仓库 / 源码（raw 或 blob）
- https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/Jellyfin.Plugin.Pronium/Pronium.csproj
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/Jellyfin.Plugin.Pronium/Plugin.cs
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/Jellyfin.Plugin.Pronium/ExternalIds/MovieID.cs
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/Jellyfin.Plugin.Pronium/Providers/MovieProvider.cs
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/Jellyfin.Plugin.Pronium/Providers/MovieImageProvider.cs
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/Jellyfin.Plugin.Pronium/Providers/ActorImageProvider.cs
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/Jellyfin.Plugin.Pronium/Helpers/Helper.cs
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/Jellyfin.Plugin.Pronium/Helpers/Actors.cs
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/Jellyfin.Plugin.Pronium/ILRepack.targets
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/.github/workflows/release.yml
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/manifest.json
- https://raw.githubusercontent.com/jellyfin-adult/Jellyfin.Plugin.Pronium/dev/README.md
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/Jellyfin.Plugin.ThePornDB/ThePornDB.csproj
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/Jellyfin.Plugin.ThePornDB/Plugin.cs
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/Jellyfin.Plugin.ThePornDB/Providers/Base.cs
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/Jellyfin.Plugin.ThePornDB/Providers/MetadataAPI.cs
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/Jellyfin.Plugin.ThePornDB/Providers/Peoples.cs
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/Jellyfin.Plugin.ThePornDB/ExternalIds/Scenes.cs
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/Jellyfin.Plugin.ThePornDB/ILRepack.targets
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/build.yaml
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/.github/workflows/release.yml
- https://raw.githubusercontent.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/main/README.md
- https://raw.githubusercontent.com/Whereis-Alice/emby-plugin-bangumi/main/src/Emby.Plugins.Bangumi/Emby.Plugins.Bangumi.csproj
- https://raw.githubusercontent.com/Whereis-Alice/emby-plugin-bangumi/main/src/Emby.Plugins.Bangumi/Providers/BangumiPersonProvider.cs
- https://raw.githubusercontent.com/Whereis-Alice/emby-plugin-bangumi/main/src/Emby.Plugins.Bangumi/Providers/BangumiExternalId.cs
- https://raw.githubusercontent.com/Whereis-Alice/emby-plugin-bangumi/main/.github/workflows/release.yml
- https://raw.githubusercontent.com/Whereis-Alice/emby-plugin-bangumi/main/README.md
- https://raw.githubusercontent.com/JavScraper/Emby.Plugins.JavScraper/master/Emby.Plugins.JavScraper/Emby.Plugins.JavScraper.csproj
- https://raw.githubusercontent.com/JavScraper/Emby.Plugins.JavScraper/master/Emby.Plugins.JavScraper/Plugin.cs
- https://raw.githubusercontent.com/JavScraper/Emby.Plugins.JavScraper/master/Emby.Plugins.JavScraper/JavMovieProvider.cs
- https://raw.githubusercontent.com/JavScraper/Emby.Plugins.JavScraper/master/Emby.Plugins.JavScraper/Extensions/JellyfinExtensions.cs
- https://raw.githubusercontent.com/JavScraper/Emby.Plugins.JavScraper/master/Emby.Plugins.JavScraper/Services/UpdateService.cs
- https://raw.githubusercontent.com/JavScraper/Emby.Plugins.JavScraper/master/manifest.json
- https://raw.githubusercontent.com/JavScraper/Emby.Plugins.JavScraper/master/README.md
- https://raw.githubusercontent.com/honue/MediaInfoKeeper/master/MediaInfoKeeper.csproj
- https://raw.githubusercontent.com/honue/MediaInfoKeeper/master/Version.json
- https://raw.githubusercontent.com/honue/MediaInfoKeeper/master/README.md
- https://raw.githubusercontent.com/MediaBrowser/NfoMetadata/master/NfoMetadata/NfoMetadata.csproj
- https://raw.githubusercontent.com/MediaBrowser/NfoMetadata/master/NfoMetadata/Plugin.cs
- https://raw.githubusercontent.com/MediaBrowser/NfoMetadata/master/NfoMetadata/EntryPoint.cs
- https://raw.githubusercontent.com/MediaBrowser/Emby.Plugins.Anime/master/MediaBrowser.Plugins.Anime/MediaBrowser.Plugins.Anime.csproj
- https://raw.githubusercontent.com/MediaBrowser/Emby.Plugins.Anime/master/README.md
- https://raw.githubusercontent.com/MediaBrowser/Douban/master/Douban/Douban.csproj
- https://raw.githubusercontent.com/MediaBrowser/Emby.Plugins.MyAnimeList/master/Emby.Plugins.MyAnimeList/Emby.Plugins.MyAnimeList.csproj
- https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/master/SampleCode/Templates/EmbyPluginMinimalTemplate/EmbyPluginMinimalTemplate.csproj
- https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/master/SampleCode/Templates/EmbyPluginMinimalTemplate/Plugin.cs
- https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/master/SampleCode/Templates/EmbyPluginSimpleUiTemplate/EmbyPluginSimpleUiTemplate.csproj
- https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/master/SampleCode/Templates/EmbyPluginUiTemplate/EmbyPluginUiTemplate.csproj
- https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/master/SampleCode/Templates/EmbyPluginMinimalTemplate/Properties/launchSettings.json
- https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/master/ReadMe.md
- https://raw.githubusercontent.com/MediaBrowser/Emby.SDK/master/Resources/OpenApi/openapi_v3.json
- https://raw.githubusercontent.com/wiki/MediaBrowser/Emby/How-to-build-a-Server-Plugin.md

### GitHub API
- https://api.github.com/search/repositories?q=emby+plugin+metadata&sort=updated&per_page=20
- https://api.github.com/orgs/MediaBrowser/repos?per_page=100&sort=updated&type=public
- https://api.github.com/repos/{owner}/{repo}（Pronium / ThePornDB / JavScraper / NfoMetadata / Emby.Plugins.Anime / Emby.SDK / Douban / MyAnimeList / MediaInfoKeeper / bangumi / avdc）
- `…/git/trees/HEAD?recursive=1`、`…/releases?per_page=3`、`…/commits?per_page=1`、`…/issues/{n}`、`…/issues/{n}/comments`

### Issue（兼容性证据）
- https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/issues/123 （Emby 4.9.3.0 人物页空白，**open**）
- https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/issues/112 （Emby 4.9.1.90 不工作）
- https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/issues/95 （能否适配 4.8 以上）
- https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/issues/97 （Emby 4.8.10 演员匹配错）
- https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/issues/121 （Jellyfin 10.10 `MissingMethodException: PersonInfo.get_Type()`）
- https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/issues/145 （演员照片请求报错）

### Emby 官方文档 / 论坛
- https://emby.media/support/articles/Plugins.html
- https://emby.media/support/articles/Server-Data-Folder.html
- https://emby.media/community/topic/142301-emby-running-in-docker-how-to-add-catalogrepositories/
- https://github.com/MediaBrowser/Emby/wiki/How-to-build-a-Server-Plugin
- https://github.com/EmbySupport/Emby.Docs/blob/master/Plugins.md
- https://dev.emby.media

### Release 资产（实测下载解包）
- https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/releases/download/latest/Emby.Plugins.ThePornDB.zip
- https://github.com/ThePornDatabase/Jellyfin.Plugin.ThePornDB/releases/download/latest/Jellyfin.Plugin.ThePornDB.zip
- https://github.com/jellyfin-adult/Jellyfin.Plugin.Pronium/releases/download/2.2.0.153/Emby.Plugins.Pronium.zip

### NuGet
- https://api.nuget.org/v3-flatcontainer/mediabrowser.server.core/index.json
- https://api.nuget.org/v3-flatcontainer/mediabrowser.server.core/4.9.1.90/mediabrowser.server.core.4.9.1.90.nupkg
- https://api.nuget.org/v3-flatcontainer/mediabrowser.common/4.9.1.90/mediabrowser.common.4.9.1.90.nupkg
- https://api.nuget.org/v3-flatcontainer/jellyfin.controller/index.json
