# Amane 插件「增加 Emby 支持」技术可行性调查报告

- 调查对象：`Jellyfin.Plugin.Amane`（本仓库，main 分支，v1.0.7，已发布、有真实用户）
- 调查目标：在**保持同一个 GitHub 仓库**的前提下，评估同时支持 Jellyfin 与 Emby 的可行性、共享层边界、仓库结构、发布方案与实施路线
- 调查方式：以**实际代码 + 真实二进制/真实编译 + 真实第三方插件源码**为准，不以 Jellyfin/Emby 的历史同源关系推断兼容性
- 本轮**未修改**任何正式插件代码（唯一新增的是本文件与 `docs/research/` 下的证据笔记）

## 证据等级标记

本文所有结论按下列等级标注，未标注等级的推断均已在第 12 节列为「需要实机验证」：

| 标记 | 含义 |
|------|------|
| 【实测】 | 本次调查在本机真实执行并留下产物（`dotnet build` 输出、反射 dump、二进制扫描、真实 Release 解包等） |
| 【官方文档】 | Emby 官方文档 / 官方 SDK / nuget.org 官方包元数据原文 |
| 【生态取证】 | 存活第三方插件的真实源码 / csproj / Release 资产原文 |
| 【代码】 | 本仓库当前源码（行号可查） |
| 【需实机验证】 | 无法从公开资料与静态证据确认，必须在真实 Emby Server 上跑一遍 |

---

## 1. Executive Summary

**结论：技术可行，且共享层方案成立；但"能否跑起来"必须在真实 Emby Server 上验证，不能只靠编译通过。**

1. **Emby 有官方 NuGet SDK，不需要 HintPath 引用服务器 DLL**。【实测】
   `MediaBrowser.Server.Core`（含 `MediaBrowser.Controller.dll`）+ `MediaBrowser.Common`（含 `MediaBrowser.Model.dll`）由 Emby Team 发布在 nuget.org，最新 `4.10.0.24-beta2`，稳定线 `4.9.1.90`。二者均为 `lib/netstandard2.0`。**`MediaBrowser.Model` 这个单独的包不存在（nuget.org 返回 404）**，Model 走 `MediaBrowser.Common`。

2. **两家的 Provider API 形状高度相似，差异集中在 6 个点，全部可在适配器内消化**。【实测】
   真实 Emby Server 4.10.0.40 二进制反射（920 KB 签名 dump）显示：`IRemoteMetadataProvider<TItemType, TLookupInfoType>`、`IRemoteSearchProvider<TLookupInfoType>`、`IHasOrder`、`MetadataResult<T>`、`RemoteSearchResult`、`RemoteImageInfo`、`PersonInfo`、`ProviderIdDictionary`、`SetProviderId/GetProviderId`、`IExternalId`、`BasePlugin<T>`、`IHasWebPages` —— **全部存在**，且大部分签名与 Jellyfin 逐字相同。差异见第 4 节。
   更强的证据：**一份一字不改的 `.cs` 分别对 Emby SDK 与 Jellyfin SDK 编译，双双 0 错 0 警**；三个 SDK 版本（4.7.9/4.8.11/4.9.1.90）diff 显示框架层接口零变更。**"两个 SDK 仍然高度兼容"在 2025/2026 是实测结论，不是历史推断。**

3. **本项目代码天然适合拆出平台无关 Core**：`AmaneModels.cs`（206 行）零宿主依赖；`AmaneClient.cs`（668 行）只有 8 处 `Plugin.Instance?.Configuration` + 2 处 Jellyfin 命名空间常量 + `IHttpClientFactory`/`ILogger<T>` 两个宿主服务依赖。**给 Core 加两个 seam（配置读取、HttpClient 获取），约 90% 的 AmaneClient 逻辑可原样复用**；Provider 层的宿主对象映射必须各写一份。

4. **推荐 3 工程方案（Core + 2 Adapter + 1 sln），而不是生态主流的 `#if __EMBY__` 单工程方案**，理由是硬约束而非审美：Jellyfin 与 Emby 的 SDK 都提供**同名程序集** `MediaBrowser.Controller.dll` / `MediaBrowser.Model.dll` / `MediaBrowser.Common.dll`。更危险的是【实测】：在同一个项目里同时引用两侧 SDK，**`dotnet build` 会成功且 0 警告，但 Emby 那一整套程序集被 MSBuild 静默丢弃**——产物实际完全是 Jellyfin 版。也就是说"能编译"不能证明"引用生效"，必须做构建维度切分。单工程只能靠"一次编译只引用一边"的 Configuration 切换，代价是业务代码里散布条件编译（生态项目 Pronium 单文件十几处 `#if`，且被较低 TFM 拖到 `LangVersion 8`）。拆工程后 Core 不引用任何宿主 SDK，两个适配器各引用一侧，类型冲突在工程边界上被消除。【实测 + 生态取证】

5. **最大的不确定性不在编译期，而在运行时**：Emby 没有 `IHttpClientFactory`、日志是 `MediaBrowser.Model.Logging.ILogManager`、DI 是 SimpleInjector、插件 REST 是自研 `IService`（不是 ASP.NET Core MVC）、配置页官方已推声明式 `BasePluginSimpleUI`（`IHasWebPages` 是否仍在 4.10 渲染未验证）。这些全部**编译期不可见**，必须在真机验证。

6. **⚠️ 最高风险点：Emby 的图片消费存在 4 条路径，其中 2 条由 Emby 自己发起 HTTP、插件无法注入 `Authorization`。**【实测·反编译】
   Emby 4.10 反编译证据：自动刷新（`ItemImageProvider`）与 Identify 缩略图（`ProviderManager.GetSearchImage` → `remoteSearchProvider.GetImageResponse`）会**回调插件**，插件可以像现在一样附加 Bearer；但**图片选择器预览**（`/Images/Remote` → `RemoteImageService` 用自己的 `_httpClient.GetResponse`）与**手动下载**（`POST /Items/{Id}/RemoteImages/Download` → `ProviderManager.SaveImage` → `SaveImageFromRemoteUrl` → `_ioManager.GetResponse`）是**Emby 自己拉 URL**。
   对本项目的直接冲击：`/api/resources/proxy` 与 `/api/resources/{hash}` 都需要 Token，这两条路径会 **401**。应对方案见 §8.3（短期：Emby 侧 `RemoteImageInfo.Url` 直接给外源 URL；中期：请 Amane 侧支持自鉴权图片 URL）。

7. **发布侧结论：Jellyfin 现有升级路径完全不受影响，但 Emby 无法复用 manifest.json 订阅机制**。【官方文档 + 生态取证】
   Emby 第三方插件官方只提供"手动把 dll 放进 `plugins/` 目录"这一条路；内置 Catalog 由 Emby 策展。因此 Emby 版必须以 Release zip + README 手动安装说明分发，且**不应**把 Emby 条目写进现有 `manifest.json`（Jellyfin 的清单解析与 CI 回写脚本都假定单一插件条目）。

8. **建议先做 Phase 0 PoC**，最小验证集合不是"能编译"，而是：**插件能被 4.10 加载 → 配置页能保存 → 影片识别能搜到 → 扫库能写入元数据** 这四件事，外加"打包洁净度"验证（Emby 插件目录不得混入 `MediaBrowser.*` 与额外 BCL 程序集；Jellyfin 侧 bin 只多一个 `Amane.Core.dll` 且 zip 同步含它）。详见第 10 节。

9. **TFM 结论（已用 4 组真实编译定标）**：`Amane.Core = net8.0`、`Amane.Emby = net8.0`、`Amane.Jellyfin = net9.0`（不变）。
   曾经考虑过"为了覆盖 Emby 4.8（运行时 .NET 6【生态取证，待 §12 V9 类验证】）与 Mono 而改 `netstandard2.0`"，**实测否决**：当 Core 与 Emby 适配器**同为 netstandard2.0** 时，Emby 插件输出目录会从 14 个文件（10 个程序集）膨胀到 **22 个文件（18 个程序集）**，多带 `System.Text.Json.dll`、`Microsoft.Bcl.AsyncInterfaces.dll`、`System.Memory.dll` 等 8 个宿主应提供的程序集——这正好是生态里"插件自带 8.0 依赖打到 .NET 6 宿主直接 `Could not load file or assembly`"的事故模式。同时 netstandard2.0 还要改源码（`System.Range/Index` 缺失、`StartsWith(char)`、`ReadAsStreamAsync(ct)`、`IsExternalInit` 垫片、12 个可空性警告）。
   **因此"是否支持 Emby 4.8/Mono"是产品决策，不是 TFM 技术选型**：net8.0 的 Core 在 .NET 6 上根本跑不起来；要支持 4.8 就必须接受上面那 8 个程序集的分发风险。本报告建议**先只支持 4.9+/netcore（net8.0；4.9 的运行时版本待 §12 V9 确认）**，把 4.8/Mono 作为后续按需求再评估的选项。

---

## 2. 当前 Jellyfin 插件架构（以实际代码为准）

### 2.1 项目结构、Target Framework、依赖、构建

| 项 | 值 | 证据 |
|---|---|---|
| 主工程位置 | 仓库根目录 `Jellyfin.Plugin.Amane.csproj`（不是 `src/`） | 【代码】 |
| TargetFramework | `net9.0`（Jellyfin 10.11.x 要求） | 【代码】csproj:4 |
| 版本 | `1.0.7`（csproj `<Version>`，manifest 用 4 段 `1.0.7.0`） | 【代码】csproj:5 |
| NuGet 依赖 | `Jellyfin.Controller` 10.11.10、`Jellyfin.Model` 10.11.10，均 `PrivateAssets=all` | 【代码】csproj:22-27 |
| 框架引用 | `Microsoft.AspNetCore.App`（插件 API 控制器用，不拷出程序集） | 【代码】csproj:21 |
| 其它设置 | `CopyLocalLockFileAssemblies=true`、`ImplicitUsings`、`Nullable`、`EmbeddedResource configPage.html`、`InternalsVisibleTo Jellyfin.Plugin.Amane.Tests`、`Compile Remove="tests/**/*.cs"` | 【代码】csproj:6-17 |
| Release zip 内容 | **只有 `Jellyfin.Plugin.Amane.dll`**（`zip -j` 显式指定单文件） | 【代码】`scripts/build-release.sh:17`、`.github/workflows/release.yml:29-30` |
| 部署目录基线 | `bin/Release/net9.0/` 实测 **30 个 dll（32 个文件，含 `.deps.json`/`.pdb`）**：插件自身 + `MediaBrowser.*` + `Jellyfin.*` + EF Core + ICU4N(13 MB) + Polly + Jellyfin.Data 等（`CopyLocalLockFileAssemblies=true` 的传递结果）。AGENTS.md 的部署步骤是"把该目录下所有 dll 拷到 `plugins/Amane/`" | 【实测】`Get-ChildItem bin\Release\net9.0 -Filter *.dll` |
| 测试工程 | `tests/Jellyfin.Plugin.Amane.Tests`，`net10.0`，xunit 2.9.3 | 【代码】tests csproj:5-14 |

**基线验证【实测】**：本机装好 .NET SDK 10.0.401（装到 `~/.dotnet`，仓库外）后执行
`dotnet build -c Release` → **0 warning 0 error**；`dotnet test tests/Jellyfin.Plugin.Amane.Tests` → **56 passed / 0 failed**。
（注：调查开始时本机无 `dotnet`，AGENTS.md 里"本机只有 .NET 10 SDK"的描述与当前环境不符；这不是插件问题，但会影响后续本地验证的复现步骤。）

**拆分后的部署/打包红线（本轮 PoC 暴露出来的真实风险）**：Core 变成独立程序集后，`bin/Release/net9.0/` 会**恰好新增 1 个 dll `Amane.Core.dll`**（30 → 31），**不会**新增 `System.Text.Json.dll` 之类（实测两种 Core TFM 都一样，SDK 的 `PackageOverrides.txt` 会对 net9.0 剪枝框架内置包）。
- Jellyfin 侧：插件目录必须**同时**存在 `Jellyfin.Plugin.Amane.dll` 与 `Amane.Core.dll`（AGENTS.md 的"拷贝 bin 下所有 dll"步骤天然满足）；
- **Release zip 必须同步改**：现在 CI 是 `zip -j … "$DLL_NAME"` 只打一个 dll，拆分后若仍只打一个，用户装上去会直接 `FileNotFoundException: Amane.Core`。**这是本次重构唯一会直接影响 Jellyfin 用户的改动点**，必须与代码拆分在同一个 PR 内完成并实测。
- `manifest.json` 结构、GUID、`targetAbi`、zip 资产名都不变；只是 zip 内的文件数从 1 个变成 2 个（checksum 自然变化，由 CI 重新计算）。

`net9.0` 主工程能被 `net10.0` 测试工程引用并跑通，这一点对后续拆分很关键：**Core 消费方不受 TFM 限制**（net9.0 的 Jellyfin 适配器与 net10.0 的测试工程都能引用 net8.0 或 netstandard2.0 的 Core）。最终按 §6.3 的实测矩阵选择 `net8.0`。

### 2.2 插件入口与生命周期

`Plugin.cs`（55 行）：`BasePlugin<PluginConfiguration>` + `IHasWebPages`；固定 GUID `9f2e4a6b-7c1d-4e3f-8a5b-0d9c2e1f4a7b`；构造函数 `(IApplicationPaths, IXmlSerializer)`；`GetPages()` 返回单个 `PluginPageInfo{Name="Amane", EmbeddedResourcePath="…Configuration.configPage.html"}`。【代码】

生命周期完全交给 Jellyfin：插件构造 → DI 注册（`ServiceRegistrator`）→ 配置读写由基类通过 `IApplicationPaths` + `IXmlSerializer` 落盘 → 配置页由 `GetPages()` 暴露出嵌入式 HTML。

`ServiceRegistrator.cs`（17 行）：`IPluginServiceRegistrator.RegisterServices` → `AddSingleton<AmaneClient>()`。【代码】

### 2.3 当前实现的 Jellyfin Provider / Interface 清单

| 类型 | 实现的接口 | 关键成员 | 文件 |
|---|---|---|---|
| `AmaneMovieProvider` | `IRemoteMetadataProvider<Movie, MovieInfo>`、`IHasOrder` | `Order=1`、`GetMetadata`、`GetSearchResults`、`GetImageResponse` | `Providers/AmaneMovieProvider.cs` |
| `AmanePersonProvider` | `IRemoteMetadataProvider<Person, PersonLookupInfo>`、`IHasOrder` | 同上 | `Providers/AmanePersonProvider.cs` |
| `AmaneImageProvider` | `IRemoteImageProvider`、`IHasOrder` | `Supports`、`GetSupportedImages`、`GetImages`、`GetImageResponse` | `Providers/AmaneImageProvider.cs` |
| `AmaneMovieExternalId` | `IExternalId` | `ProviderName="Amane"`、`Key="Amane"`、`Type=ExternalIdMediaType.Movie` | `Providers/AmaneMovieExternalId.cs` |
| `AmanePersonExternalId` | `IExternalId` | 同上，`Type=Person` | `Providers/AmanePersonExternalId.cs` |
| `AmaneDiagnosticsController` | `[ApiController] [Route("Amane")] [Authorize]` | `GET /Amane/Health`、`POST /Amane/ClearCache` | `Api/AmaneDiagnosticsController.cs` |

### 2.4 影片搜索 / 详情 / 图片 / 演员 / External ID 的实现路径

- **搜索**（识别对话框）：`MovieInfo.Name`（或识别框值）→ `AmaneClient.SearchAsync(query, 10)` → `GET /api/metadata?search=…&limit=n` → `items.Select(ToSearchResult)`，`RemoteSearchResult.Name = "番号 标题"`，`ImageUrl = ToDirectImageUrl(poster ?? thumb)`，`ProviderIds["Amane"]=番号`、`ProviderIds["AmaneId"]=数字 id`。【代码】`AmaneMovieProvider.cs:108-165`
- **详情**（扫库/刷新）：`ResolveMetadataAsync(ProviderIds, Name)` 三级解析 → `AmaneId` 直取 → `Amane` 键（去 `Amane:` 前缀，数字直取/番号搜索）→ 名称搜索；命中后 `MapToMovie` 写入 `Name/OriginalTitle/Overview/PremiereDate/ProductionYear/Studios/Genres/RunTimeTicks/CommunityRating`（score×2）+ 双键 ProviderIds。【代码】`AmaneClient.cs:181-217`、`AmaneMovieProvider.cs:60-105,178-226`
- **演员**：影片侧对 `metadata.Actors` 逐个 `LookupActorAsync`（进程内缓存，TTL 可配置），构造 `PersonInfo{Name, Type=PersonKind.Actor, ImageUrl=ToDirectImageUrl(首个 image_url)}`，命中时写 `personInfo.ProviderIds["Amane"]=actor.Id`；人物侧独立 `IRemoteMetadataProvider<Person, PersonLookupInfo>` 写简介/生日/头像/`SetProviderId("Amane", id)`。【代码】
- **图片**：`GetImages` 产出 `RemoteImageInfo{Url=ToProxyImageUrl(...), ThumbnailUrl=ToDirectImageUrl(...)}`，`poster_url→Primary`、`thumb_url`+`extrafanart→Backdrop`；下载统一走 `GetImageAsync`（附加浏览器 UA、只对 Amane 域内 URL 加 Bearer、非 2xx/非图片抛异常防坏图缓存）。【代码】`AmaneImageProvider.cs:50-92`、`AmaneClient.cs:425-464`
- **External ID**：影片/人物各一个 `IExternalId`，`Key` 都是 `"Amane"`，靠 `Type` 区分媒体类型。【代码】

**「图片 URL 双轨制」是本插件最容易在迁移中踩坑的设计**：下载路径（`RemoteImageInfo.Url` → 插件 `GetImageResponse`）可以带 Bearer，所以走 Amane 代理；直出路径（`RemoteSearchResult.ImageUrl`、`RemoteImageInfo.ThumbnailUrl`、`PersonInfo.ImageUrl`、`Person.ImageInfos[].Path`）由浏览器或宿主裸 HttpClient 取，拿不到 token，所以只能给外源 URL 或 null。AGENTS.md 已明确记录这是机制使然。**Emby 侧这两条路径的消费行为不同，必须在真机确认（第 12 节 V8）。**

### 2.5 AmaneClient 与 Jellyfin 的耦合点（seam 清单）

| # | 位置 | 现状 | 性质 |
|---|---|---|---|
| 1 | `AmaneClient.cs:94` | `Plugin.Instance?.Configuration?.MaxConcurrentRequests` | 宿主配置单例 |
| 2 | `AmaneClient.cs:109` | `Plugin.Instance?.Configuration?.TimeoutSeconds` | 同上 |
| 3 | `AmaneClient.cs:124` | `Plugin.Instance?.Configuration?.ActorCacheMinutes` | 同上 |
| 4 | `AmaneClient.cs:385` | `Plugin.Instance?.Configuration?.ServerUrl`（`ToDirectImageUrl`） | 同上 |
| 5 | `AmaneClient.cs:405` | 同上（`ToProxyImageUrl`） | 同上 |
| 6 | `AmaneClient.cs:439` | `Plugin.Instance?.Configuration`（`GetImageAsync` 取 ServerUrl/ApiToken） | 同上 |
| 7 | `AmaneClient.cs:474` | 同上（`CheckHealthAsync`） | 同上 |
| 8 | `AmaneClient.cs:558` | 同上（`GetAsync` 取 ServerUrl/ApiToken） | 同上 |
| 9 | `AmaneClient.cs:187,198,313` | `Providers.AmaneMovieProvider.ProviderIdName` / `InternalIdProviderIdName` | Jellyfin 命名空间里的字符串常量（值本身平台无关） |
| 10 | 构造函数 | `IHttpClientFactory`（行 36/67/76） | **Emby 宿主不存在该服务**（`Microsoft.Extensions.Http.dll` 不在宿主目录）；且共享 `HttpClient` 后重复设置 `Timeout` 会抛 `InvalidOperationException` |
| 11 | 构造函数/字段 | `ILogger<AmaneClient>` | Emby 有 `Microsoft.Extensions.Logging.Abstractions` 文件，但容器是否注册 `ILogger<T>` 未验证 |
| 12 | `AmaneClient.cs:222,238` | `internal static NormalizeIdValue` / `TryParseInternalId` | 跨程序集后必须改为 `public static`（或 Core 内 `InternalsVisibleTo`） |
| 13 | 全文件 | `namespace Jellyfin.Plugin.Amane` | Core 需改为自己的命名空间（否则 Core 名字里带宿主名，语义错误） |

据此，Core 化需要的最小抽象是 **2 个**：
1. `IAmaneConfiguration`（或直接传 `AmaneSettings` POCO）——取代 8 处 `Plugin.Instance?.Configuration`；
2. `IHttpClientProvider`/`Func<HttpClient>`——取代 `IHttpClientFactory`（Jellyfin 侧用工厂实现，Emby 侧用一个进程内 `HttpClient` 或 Emby 的 `IHttpClient` 包装实现）。

**PoC 实测的改造量（可复算）**【实测】：Core 侧共 **24 处**字面替换（`AmaneClient.cs` 23 处 + `AmaneModels.cs` 1 处），其中新增一个约 70 行的 `AmaneSettings.cs`（配置 POCO + 接口 + `JellyfinAmaneSettings` 形态的适配）；Jellyfin 适配器侧 **8 处**替换、涉及 5/9 个文件，**4 个文件一字未改**。
**PoC 还发现一个必须处理的细节**：`AmaneClient` 现在对每个请求都设置 `client.Timeout`（兜底超时）。若 Core 改成共享同一个 `HttpClient` 实例，**再次设置 `Timeout` 会抛 `InvalidOperationException`**；且 Emby 宿主目录里没有 `Microsoft.Extensions.Http.dll`。因此 seam 2 的正确形态是"由宿主侧提供已配置好的 client/工厂"，而不是 Core 内部缓存一个共享实例。

ProviderId 常量（`"Amane"` / `"AmaneId"`）应上移到 Core 常量类，避免 Core 反向依赖适配器命名空间。

### 2.6 配置页、配置模型、API Token、测试连接

- 配置模型 `Configuration/PluginConfiguration.cs`（34 行）：继承 `MediaBrowser.Model.Plugins.BasePluginConfiguration`，字段 `ServerUrl` / `ApiToken` / `TimeoutSeconds` / `MaxConcurrentRequests` / `ActorCacheMinutes`。**纯 POCO，除基类外零宿主逻辑。**
- 配置页 `Configuration/configPage.html`（139 行）：嵌入式 HTML + Jellyfin 前端 JS（`ApiClient.getPluginConfiguration(pluginId)` / `updatePluginConfiguration` / `ApiClient.getJSON(ApiClient.getUrl('Amane/Health'))`），表单字段与配置模型一一对应。
- **测试连接**：不直连 Amane，而是调插件自己的服务端端点 `GET /Amane/Health` → `AmaneClient.CheckHealthAsync`：先探 `/api/health`（无 token）测延迟与版本，再用 `GET /api/metadata?limit=1` + Bearer 验证 token 有效性，返回 `{Reachable, LatencyMs, Version, AuthStatus}`。**这是"服务端代发 + 避免把 token 暴露给浏览器/绕过 CORS"的设计**，迁移到 Emby 时端点机制必须替换（见 §4 第 19 行）。
- 清缓存：`POST /Amane/ClearCache` → `AmaneClient.ClearActorCache()`。

### 2.7 manifest、版本号、打包、Release / CI

- `manifest.json`（仓库根）：Jellyfin 可订阅清单，单条目 GUID 与插件一致，`versions[]` 由 CI 在打 tag 时**回写 main 分支**。用户订阅的 URL 是 `https://raw.githubusercontent.com/rappa850/Jellyfin.Plugin.Amane/main/manifest.json`——**这个路径是既有用户的升级入口，任何重构都不得移动它**。【代码】+ README:24
- `meta.json`、`thumb.png`：仓库图标/展示元数据。
- `.github/workflows/release.yml`：`push: tags: v*` → `dotnet build -c Release` → `zip -j bin/Release/net9.0/Jellyfin.Plugin.Amane.dll` → GitHub Release 资产 `Jellyfin.Plugin.Amane.zip` → python 脚本把新版本条目插入 `manifest[0].versions` → commit 回 main。
- `scripts/build-release.sh`：本地等价打包 + md5。
- **CI 脚本的两个隐含假设**：(a) `manifest[0]` 就是 Amane 的唯一条目；(b) zip 里只有一个 dll。**新增 Emby 时若往 manifest 里加第二个条目，会直接打坏自动回写逻辑。**【代码】release.yml:51-59

### 2.8 当前测试体系

8 个 xunit 测试文件、56 个用例（本次实测全绿）：
`ContractDeserializationTests`（真实样本反序列化）、`MovieMappingTests`（DTO→Jellyfin `Movie` 字段映射）、`ActorBindingTests`（演员 DTO + `IExternalId` 契约）、`InternalIdTests`、`AmaneClientCacheTests`、`AmaneClientHealthCheckTests`、`AmaneClientImageTests`（stub `HttpMessageHandler` + 伪 `IHttpClientFactory`，含"Bearer 不外泄给第三方图床"断言）、`AmaneClientResilienceTests`（并发/超时/熔断，走 internal 构造函数注入）。

**对拆分的影响**：
- 依赖 `InternalsVisibleTo` 的用例（internal 构造函数、`MapToMovie`、`NormalizeIdValue`、`TryParseInternalId`）在拆分后需重新划定可见性（Core 自己开 `InternalsVisibleTo` 给 Core 测试程序集）。
- `MovieMappingTests`/`ActorBindingTests` 直接构造 Jellyfin `Movie`/`Person`，**属于适配器测试，必须留在 Jellyfin 侧**；契约反序列化与时序/缓存测试是可搬 Core 的。
- 好消息：测试完全不依赖网络与真实 Jellyfin 进程，拆分后 Core 测试可在任意 TFM 上跑。

### 2.9 AGENTS.md / README 中与本次改造相关的约束

- 「**不新增第三方依赖**；端点路径收敛在 `AmaneClient` 一处」——拆分后 Core 也不应引入新的**第三方**依赖（最终方案 `net8.0` 下 `System.Text.Json` 用框架内置版本，无需包引用；曾考虑的 `netstandard2.0` 路线会需要包引用，该路线已在 §6.3 被实测否决）。
- 「插件定位为 Thin Client，不做番号解析/降级/图片中转」——Emby 适配器必须保持同一职责边界，不能为了适配 Emby 而把逻辑塞进适配器。
- 「配置页文案、代码注释用中文」——Emby 版沿用。
- 「新增 Amane 字段映射时：先跑 probe 更新样本，再改 DTO + 映射 + 单测，三者同步」——拆分后 DTO 在 Core，映射在两个适配器 → 该约定需要升级为"DTO 改动必须同时更新两侧映射测试"。
- README 目前没有"平台支持"章节；增加 Emby 后必须补（第 7 节）。
- 远端已存在 `origin/feat/jellyfin-12` 分支（Jellyfin 12.1.0 / net10.0 适配，只改 csproj/TFM/CI 路径）。**这说明本仓库已预期"多宿主/多版本并行"，拆分方案必须与它兼容**（例如 `src/Amane.Jellyfin/` 用 `Directory.Build.props` 或条件 TFM 承载 10.11/12.x 两条线）。

---

## 3. Emby 插件体系调查

### 3.1 开发方式与 SDK【实测 + 官方文档】

- nuget.org 实测（`azuresearch-usnc.nuget.org`）：
  - `MediaBrowser.Server.Core` 4.9.1.90 —— 描述原文 *"Contains core components required to build plugins for Emby Server."*，累计下载 3,233,744
  - `MediaBrowser.Common` 4.9.1.90 —— *"Contains common model objects and interfaces used by all Emby solutions."*
  - 包内结构（`4.10.0.24-beta2` 实测解包）：
    - `MediaBrowser.Server.Core` → `lib/netstandard2.0/MediaBrowser.Controller.dll`（587 KB）、`Emby.Naming.dll`；依赖 `MediaBrowser.Common`
    - `MediaBrowser.Common` → `lib/netstandard2.0/MediaBrowser.Model.dll`、`MediaBrowser.Common.dll`、`Emby.Media.Model.dll`、`Emby.Web.GenericEdit.dll`
  - `MediaBrowser.Model` 作为独立包**不存在**（flatcontainer 404）。
- 官方文档等价的开发路径：官方推荐用 SDK 里的插件模板（`dev.emby.media/home/sdk/plugins/`），SDK 页面明确 *"There are no licenses attached to the code and developers are free to decide how to make use of the provided code examples."*【官方文档】
- 现实中有两种引用方式，两种都能编译通过【生态取证】：
  - ① NuGet（主流）：`<PackageReference Include="MediaBrowser.Server.Core" Version="4.9.1.90" />`（Pronium / ThePornDB / MediaInfoKeeper / Emby 官方 Org 插件）
  - ② 本地 DLL：三级回退 `-p:EmbySystemDir` → `EMBY_SYSTEM_DIR` → NuGet（`emby-plugin-bangumi`）
- **结论：不存在"必须进官方商店才能开发"的限制，也不存在"必须引用服务器 DLL 才能编译"的限制。**
- **⚠️ 方法论陷阱（本次踩到，记录备查）**：Emby NuGet 包里附带的 XML 文档只是**策展子集**（`MediaBrowser.Controller.xml` 约 950 条 member，`T:` 条目里**没有** `IRemoteMetadataProvider<T,T2>` / `IRemoteSearchProvider` / `IExternalId`）。**"XML 里没有"不能推断"类型不存在"**，必须反射真实 DLL。另注：`MediaBrowser.Controller.dll` 不在同名包内，它在 `MediaBrowser.Server.Core`；`MediaBrowser.Model.dll` 在 `MediaBrowser.Common`。
- **编译可行性已验证**【实测】：Emby 最小插件（入口类 + 影片 Provider + 图片 Provider + 人物 Provider + ExternalId）在两个 TFM 下**均一次编译通过、0 warning 0 error**：
  - 变体 A：`net8.0` + 真实 Emby Server 4.10.0.40 `system/*.dll`（`Reference` + `Private=false`）
  - 变体 B：`netstandard2.0` + 官方 NuGet `MediaBrowser.Server.Core` / `MediaBrowser.Common` 4.10.0.24-beta2

### 3.2 Target Framework：三条真实路线

| 路线 | TFM | 用户 | 覆盖范围 | 代价 |
|---|---|---|---|---|
| 最大兼容 | `netstandard2.0` | **官方 Trakt、官方 AutoOrganize、Emby 官方 Org 的 NfoMetadata(4.8.2)/Emby.Plugins.Anime(4.8.11)、danmu(4.8.5)、emby-xtream** | .NET Core 与 Mono 运行时都能加载 | 现代 API 受限，第三方依赖需随包分发 |
| 现代主流 | `netstandard2.1` | Pronium、ThePornDB（Emby 配置）、JavScraper | 覆盖 .NET Core 系 Emby | 同样是 netstandard，JSON 库选择受约束 |
| .NET 8 | `net8.0`（部分项目 `net8.0;net6.0`） | metatube（双支持项目的 Emby 配置）、Whereis-Alice/emby-plugin-bangumi、MediaInfoKeeper | Emby 4.9.x / 4.10.x 的 netcore 安装 | **丢掉 Emby 4.8（运行时是 .NET 6）** |

**官方 SDK 本身的形态**【实测】：`MediaBrowser.Server.Core` / `MediaBrowser.Common` 的 `lib/` 目录从 4.6 一直到 4.10-beta **始终是 `netstandard2.0`**；依赖树极干净（`Server.Core` → `MediaBrowser.Common` → `Microsoft.NETCore.Platforms 1.1.0`，仅 2 个传递包）；4 个 TFM（netstandard2.0/net8.0/net9.0/net10.0）实测 `dotnet add package` + build **全部 0 错 0 警**。

**Emby Server 真实运行时【实测】**：下载官方 `embyserver-netcore_4.10.0.40.zip`（219 MB）解包，`system/EmbyServer.runtimeconfig.json`：

```json
{ "runtimeOptions": { "tfm": "net8.0",
  "frameworks": [ { "name": "Microsoft.NETCore.App", "version": "8.0.0" },
                  { "name": "Microsoft.AspNetCore.App", "version": "8.0.0" } ] } }
```

`system/` 目录 78 个 dll 实测清点，与本文档结论直接相关的：
- **有**：`Microsoft.Extensions.Logging.Abstractions.dll`(8.0.1024.46610)、`Microsoft.Extensions.Logging.dll`、`Microsoft.Extensions.DependencyInjection(.Abstractions).dll`、`Microsoft.Extensions.Options/Hosting.*`、`SimpleInjector.dll`、`ServiceStack.Text.dll`
- **没有**：`Microsoft.Extensions.Http.dll`（→ **无 `IHttpClientFactory`**）、`System.Text.Json.dll`（作为文件不存在，但属 `Microsoft.NETCore.App/AspNetCore.App` 共享框架，运行时可用）、`Newtonsoft.Json.dll`

**Mono 仍在发布**【实测】：`MediaBrowser/Emby.Releases` 的近期正式版（4.8.9.0 ~ 4.11.0.4）每个版本有 90–99 个资产，其中 **约 20 个是 mono/netframework 资产**（`embyserver-netframework_*.zip`、`emby-server-*-mono_*.spk/qpkg`；4.10.0.40 为 96 个资产 / 20 个 mono）。也就是说 **4.10 仍然同时存在 .NET Core 与 Mono 两条运行时线**；官方 dev 文档也写着 *"Emby Server runs on two different runtimes: .NET Core 2.0+, and Mono."*【官方文档】

→ **影响（本报告的结论）**：Emby 适配器**选 `net8.0`**（覆盖 Emby 4.9.x/4.10.x 的 netcore 安装），**不选 `netstandard2.0`**。
原因是一条实测结论（见 §6.3 的 TFM 矩阵）：当 Core 与 Emby 适配器**同为 `netstandard2.0`** 时，Emby 插件输出目录会从 14 个文件（10 个程序集）膨胀到 **22 个文件（18 个程序集）**，多带 `System.Text.Json.dll`、`Microsoft.Bcl.AsyncInterfaces.dll`、`System.Memory.dll` 等 8 个宿主应提供的程序集——**这正是生态里已发生过的"插件自带 8.0 依赖打到 .NET 6 宿主 → `Could not load file or assembly`"事故形态**；同时 netstandard2.0 还需要改源码（`System.Range/Index` 不可用、`StartsWith(char)`、`ReadAsStreamAsync(ct)`、`IsExternalInit` 垫片、12 个可空性警告）。
代价是**放弃 Emby 4.8（.NET 6）与 Mono 资产**——但这是产品取舍：net8.0 编译的 Core 在 .NET 6 上根本跑不起来，要支持 4.8 就必须接受那 8 个程序集的分发风险。**建议先只声明支持 4.9+/netcore，把 4.8/Mono 按后续用户需求再评估。**

### 3.3 插件入口、生命周期、DI【实测 + 官方文档】

真实签名（反射 dump，`MediaBrowser.Controller` / `MediaBrowser.Common`）：

```csharp
public abstract class MediaBrowser.Common.Plugins.BasePlugin : IPlugin, IPluginAssembly {
    protected BasePlugin();
    public virtual PluginInfo GetPluginInfo();
    public string GetPluginPageUrl(string name);
    public virtual void OnUninstalling();
    public string AssemblyFilePath { get; }  public string DataFolderPath { get; }
    public Guid Id { get; }  abstract string Name { get; }  public Version Version { get; }
}

public abstract class BasePlugin<TConfigurationType> : BasePlugin, IHasPluginConfiguration, IPlugin, IPluginAssembly {
    protected BasePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer);
    public virtual void SaveConfiguration();
    public virtual void UpdateConfiguration(BasePluginConfiguration configuration);
    public void SetStartupInfo(Action<string> directoryCreateFn);
    TConfigurationType Configuration { get; set; }
    bool IsFirstRun { get; }
}
```

与 Jellyfin 的 `BasePlugin<TConfigurationType>` **结构、构造参数、`UpdateConfiguration`/`Configuration` 语义基本一致**，`Plugin.cs` 预计可直接迁移（改 using + 去 `IHasWebPages` 的风险点）。

入口/生命周期相关接口还有：`IPlugin`、`IPluginAssembly`、`IHasPluginConfiguration`、`IHasThumbImage`、`IServerEntryPoint{Run()}`（官方文档推荐的初始化入口）、`IRunBeforeStartup`、`IPluginConfigurationPage{GetHtmlStream()}`、`IHasSetupUrl`。

**依赖注入与取服务**【实测】：
```csharp
public interface MediaBrowser.Common.IApplicationHost {
    T Resolve<T>();  T TryResolve<T>();
    IEnumerable<T> GetExports<T>(bool manageLiftime = true);
    object CreateInstance(Type type);  ...
}
public interface MediaBrowser.Controller.IServerApplicationHost : IApplicationHost { ... }
```
宿主内是 **SimpleInjector**（`system/SimpleInjector.dll` 实测存在）。构造注入在 Emby 里可用（官方文档 *"Its constructor can accept any number of injected dependencies"*），但 **没有 Jellyfin 的 `IPluginServiceRegistrator` 等价物**（反射 dump 中确认不存在该类型名）；Emby 的初始化入口是 **`IServerEntryPoint.Run()`**（官方文档明确推荐）。
→ 适配器应避免依赖宿主容器注册自定义服务：用 `Lazy<T>`/静态持有 `AmaneClient` 单例，或实现 `IServerEntryPoint` 做初始化。

**框架层的兼容度比预期高得多**【实测·编译验证】：把**完全同一份、一字不改**的 `.cs`（含 `BasePlugin<TConfigurationType>`、`SaveConfiguration`、`ConfigurationFilePath`、`PluginPageInfo`、`IHasWebPages.GetPages()`）分别对 Emby SDK 与 Jellyfin SDK 编译，**双双 0 错 0 警**；逐版本 diff 显示 `IHasWebPages`、`PluginInfo`、`IPlugin`、`IPluginConfigurationPage`、`BasePlugin<T>` 在 **4.7.9 / 4.8.11 / 4.9.1.90 三个版本零变更**。
→ 结论：**框架层当前（2025/2026）确实仍然高度兼容**（这是实测，不是"历史同源"推断）；不兼容的只有 3 处：① 程序集同名（必须切分构建）；② DI 注册方式；③ 分发/版本单一事实源。

### 3.4 Metadata Provider 相关接口（真实签名）

反射 dump（`MediaBrowser.Controller.dll` 4.10.0.40）原文：

```csharp
public interface MediaBrowser.Controller.Providers.IMetadataProvider { string Name { get; } }
public interface IMetadataProvider<TItemType> : IMetadataProvider { }

public interface IRemoteMetadataProvider<TItemType, TLookupInfoType>
    : IMetadataProvider, IMetadataProvider<TItemType>, IRemoteMetadataProvider,
      IRemoteSearchProvider, IRemoteSearchProvider<TLookupInfoType>
{
    Task<MetadataResult<TItemType>> GetMetadata(TLookupInfoType info, CancellationToken cancellationToken);
}

public interface IRemoteSearchProvider<TLookupInfoType> : IMetadataProvider, IRemoteSearchProvider
{
    Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TLookupInfoType searchInfo, CancellationToken cancellationToken);
}

public interface IRemoteSearchProvider : IMetadataProvider
{
    Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken);
}

public interface IHasOrder { int Order { get; } }

public sealed class MetadataResult<T> : BaseMetadataResult { public MetadataResult(); T Item { get; set; } }

public abstract class BaseMetadataResult {
    public void AddPerson(PersonInfo p);   public void ResetPeople();
    public RemoteSearchResult ToRemoteSearchResult(string searchProviderName);
    bool HasMetadata { get; set; }  List<PersonInfo> People { get; set; }
    string ResultLanguage { get; set; }  string Provider { get; set; }
    string SearchImageUrl { get; set; }  string ThumbnailUrl { get; set; }
    bool QueriedById { get; set; }  List<LocalImageInfo> Images { get; set; }
}
```

另有 `IRemoteMetadataProviderWithOptions<TItemType,TLookupInfoType>`（新版带 options 的变体）与 `IMetadataProvider` 家族其它成员，本项目不需要。

**与 Jellyfin 的差异只有一处**：`GetImageResponse` 的返回类型是 `MediaBrowser.Common.Net.HttpResponseInfo`，不是 `HttpResponseMessage`。

`IHasOrder` 确实被宿主消费【生态取证】：Emby 的 `ProviderManager` 里有 `if (provider is IHasOrder hasOrder) return hasOrder.Order;` 的排序逻辑（队友从 4.10 程序集侧确认；本项目 `Order=1` 的语义可以保留）。

### 3.5 远程搜索 / Identify 机制

- 宿主侧入口：`IProviderManager.GetRemoteSearchResults<TItemType,TLookupType>(RemoteSearchQuery<TLookupType> searchInfo, …)`、`GetAllItems`、`GetAvailableRemoteImages(...)`、`GetExternalIdInfos(IHasProviderIds item)`、`GetSearchImage(providerName, url, ct) → Task<HttpResponseInfo>`【实测 dump】。
- `RemoteSearchQuery<T>`：`{ ItemId, SearchInfo, Providers[], SearchProviderName, IncludeDisabledProviders }`【实测 dump】。
- 插件侧只需实现 `IRemoteSearchProvider<TLookupInfoType>.GetSearchResults`，其余由宿主驱动——**与 Jellyfin 的 `IRemoteMetadataProvider.GetSearchResults` 模型一致**，业务代码（查询 + 组装 `RemoteSearchResult`）几乎可以照搬。
- 识别对话框能否在 Emby Web 里正确渲染本项目"番号/数字 id"语义的输入框 →【需实机验证】。

### 3.6 Image Provider（真实签名）

```csharp
public interface MediaBrowser.Controller.Providers.IImageProvider { bool Supports(BaseItem item); string Name { get; } }
public interface IRemoteImageProvider : IImageProvider {
    Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken);
    Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions libraryOptions, CancellationToken cancellationToken);
    IEnumerable<ImageType> GetSupportedImages(BaseItem item);
}

public class MediaBrowser.Model.Providers.RemoteImageInfo {
    string ProviderName; string Url; string ThumbnailUrl; ImageType Type;
    string Language; string DisplayLanguage; double? CommunityRating; RatingType RatingType;
    int? Width; int? Height; int? VoteCount;
}
public enum MediaBrowser.Model.Entities.ImageType { Primary, Art, Backdrop, Banner, Logo, Thumb, Disc, Box, Screenshot, Menu, Chapter, BoxRear, Thumbnail, LogoLight, LogoLightColor }
```

**差异 2 处**（`GetImages` 多一个 `LibraryOptions` 形参、`GetImageResponse` 返回 `HttpResponseInfo`），其余字段与 Jellyfin 版一致（Jellyfin 的 `RemoteImageInfo` 也是这几个字段）。生态项目 **4.8 → 4.9 的 `LibraryOptions` 变更就是一次真实的破坏性变更**【生态取证】，说明这两个接口是 Emby 侧的高风险面。

**实现提示【实测·编译验证】**：实现 `IRemoteMetadataProvider<TItemType,TLookupInfoType>` 时必须写全 **4 个**成员——`Name`、`GetMetadata`、`GetSearchResults`，以及继承自非泛型 `IRemoteSearchProvider` 的 `Task<HttpResponseInfo> GetImageResponse(string, CancellationToken)`（最易漏，因为它在 Jellyfin 侧由 `IRemoteImageProvider` 承担）。本轮的 Emby 最小 PoC 已按此签名一次性编译通过（0 warning / 0 error）。

#### Emby 侧图片消费的 4 条真实路径（**迁移前必须知道**）【实测·反编译 4.10.0.40】

| 路径 | 触发场景 | 谁来发 HTTP | 插件能否带 Bearer |
|---|---|---|---|
| A | 自动刷新图片（`ItemImageProvider.cs` 约 :470） | **插件**（`GetImageResponse`） | ✅ 能 |
| B | Identify 弹窗缩略图（`ProviderManager.cs` 约 :1311 `GetSearchImage` → `remoteSearchProvider.GetImageResponse`） | **插件**（`GetImageResponse`） | ✅ 能 |
| C | 图片选择器**预览**（`/Images/Remote` → `RemoteImageService`） | **Emby 自己**（`_httpClient.GetResponse`） | ❌ 不能 |
| D | 图片选择器**手动下载**（`POST /Items/{Id}/RemoteImages/Download` → `ProviderManager.SaveImage` → `SaveImageFromRemoteUrl` → `_ioManager.GetResponse`） | **Emby 自己** | ❌ 不能 |

对照 Jellyfin：本项目的"双轨制"之所以成立，是因为 Jellyfin 的下载路径统一经插件 `GetImageResponse`。**Emby 只覆盖了其中两条**，因此 `RemoteImageInfo.Url` 不能在 Emby 上无条件指向需要 Token 的 Amane 代理地址。这是本次调查发现的最关键差异。

`HttpResponseInfo` 真实定义【实测】：
```csharp
public sealed class MediaBrowser.Common.Net.HttpResponseInfo : IDisposable {
    Stream Content { get; set; }  string ContentType { get; set; }  long? ContentLength { get; set; }
    HttpStatusCode StatusCode { get; set; }  Dictionary<string,string> Headers { get; set; }
    string ResponseUrl { get; set; }  string TempFilePath { get; set; }
}
public interface MediaBrowser.Common.Net.IHttpClient {
    Task<Stream> Get(HttpRequestOptions options);
    Task<HttpResponseInfo> GetResponse(HttpRequestOptions options);
    Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, string httpMethod);
    ...
}
```

### 3.7 Person / Actor metadata（真实签名）

```csharp
public sealed class MediaBrowser.Controller.Entities.PersonInfo : IHasProviderIds {
    public PersonInfo(); public bool IsType(PersonType type);
    string Name { get; set; }  PersonType Type { get; set; }  string Role { get; set; }
    string ImageUrl { get; set; }  ItemImageInfo[] ImageInfos { get; set; }
    ProviderIdDictionary ProviderIds { get; set; }  Guid Guid { get; set; }  long Id { get; set; }
}

public enum MediaBrowser.Model.Entities.PersonType { Actor=0, Director=1, Writer=2, Producer=3, GuestStar=4, Composer=5, Conductor=6, Lyricist=7 }
```

- `PersonInfo.ImageUrl` **存在**（与 Jellyfin 同名同义，Jellyfin 官方 XML 文档未列出该成员，Emby 侧是公开成员）。
- 差异：`PersonKind`（Jellyfin，`Jellyfin.Data.Enums`）→ `PersonType`（Emby，`MediaBrowser.Model.Entities`）；枚举成员：本项目只用 `Actor`/`Director`，两侧都有。
- Emby 另有 `IPersonMetadataProvider.GetCredits(PersonLookupInfo, ct) → RemoteSearchResult[]`（演员"参演作品"），本项目不需要。
- 生态普遍做法：直接把外源头像 URL 写进 `PersonInfo.ImageUrl`，由宿主下载【生态取证】。**但 Emby 4.9.3 存在 open issue「更新后人物页空白」，说明人物头像下载路径在 Emby 侧存在版本相关故障史 →【需实机验证 V7】**。

### 3.8 External ID / Provider ID（真实签名）

```csharp
public interface MediaBrowser.Controller.Providers.IExternalId {
    bool Supports(IHasProviderIds item);
    string Key { get; }  string Name { get; }  string UrlFormatString { get; }
}
public class MediaBrowser.Model.Providers.ExternalIdInfo {
    string Name; string Key; string UrlFormatString; string Website; bool IsSupportedAsIdentifier;
}
public class MediaBrowser.Model.Entities.ProviderIdDictionary : Dictionary<string, string> { ... }
public static class ProviderIdsExtensions {
    string GetProviderId(IHasProviderIds, string name);
    void SetProviderId(IHasProviderIds, string name, string value);
    bool HasProviderId(IHasProviderIds, string provider);
    ...
}
```

与 Jellyfin 的差异：

| 成员 | Jellyfin | Emby |
|---|---|---|
| 显示名 | `ProviderName` | `Name` |
| 媒体类型限定 | `ExternalIdMediaType? Type` | **不存在**（靠 `Supports(IHasProviderIds)` 判定） |
| 键 | `Key` | `Key` |
| URL 模板 | `UrlFormatString` | `UrlFormatString`（另有 `IHasWebsite.Website`） |
| 取值/写值扩展 | `GetProviderId` / `SetProviderId` | 同名同签名 |

**结论**：External ID 只需各写一个约 10 行的 `IExternalId` 实现；`ProviderIds` 键名（`"Amane"` / `"AmaneId"`）与读写扩展方法**完全可平移**，这也是跨宿主迁移元数据绑定的基础（生态先例：bangumi 的 Emby 版与 Jellyfin 版刻意共用 `"Bangumi"` 键名以便迁移）。

**「Emby 是否会自动给 `Name` 拼 `Id` 后缀」已实测确认：不会。**【实测·反编译】
三重证据：(1) `ProviderManager.GetExternalIdInfos` 原文 `Key = i.Key`（无拼接）；(2) `MetadataService.ApplySearchResult` 把 `result.ProviderIds` 原样拷贝进 `lookupInfo.ProviderIds`；(3) Emby 网页端 `itemidentifier.js` 用 `data-providerkey="'+idInfo.Key+'"` 构造请求体；全量反编译 3 个程序集对 `Key + "Id"` 拼接模式 **0 命中**。
→ 含义：Emby 侧 `IExternalId.Key` 就是 ProviderIds 的键，双键（`Amane` + `AmaneId`）在 Emby 上**能原样工作**，但**不是宿主强制**；建议仍保留双键以维持两宿主间元数据一致性与 Core 的同一套解析逻辑。

**插件 `Name` 是协议的一部分**【实测·反编译】：`ProviderManager.GetSearchResults` 会无条件把结果覆盖为 `SearchProviderName = provider.Name`，Identify 缩略图再靠该 Name 反查 provider，媒体库里的 fetcher 开关也按 Name 匹配。→ **两侧插件的 `Name` 必须都叫 `"Amane"`，改名会连带破坏搜索/缩略图/库设置匹配。**

### 3.9 配置模型与配置页

- 配置基类：`MediaBrowser.Model.Plugins.BasePluginConfiguration`（两侧同名）。
- **配置页存在两套官方机制**：
  1. **旧/HTML 式**：`MediaBrowser.Model.Plugins.IHasWebPages { IEnumerable<PluginPageInfo> GetPages(); }` ——【实测】该接口在 Emby 4.10 的 `MediaBrowser.Model.dll` 中**确实存在且签名与 Jellyfin 完全相同**；`PluginPageInfo` 字段为 Jellyfin 的超集（`DisplayName`、`EmbeddedResourcePath`、`EnableInMainMenu`、`EnableInUserMenu`、`FeatureId`、`IsMainConfigPage`、`MenuIcon`、`MenuSection`、`Name`）。另有更老的 `IPluginConfigurationPage.GetHtmlStream()`。
  2. **新/声明式**（官方当前推荐）：`BasePluginSimpleUI<TOptionType>`（`MediaBrowser.Controller.Plugins`），
     ctor `(IApplicationHost)`，钩子 `GetOptions()`、`OnBeforeShowUI(options)`、`OnOptionsSaving(options)`、`OnOptionsSaved(options)`、`OnCreatePageInfo(pageInfo)`、`SaveOptions(options)`；UI 由属性的 `[DisplayName]`/`[Description]`/`[Required]`/`[EditFolderPicker]` 等特性自动生成，**不需要写 HTML/JS**。【实测 dump + 官方文档】
- 官方文档对老式 HTML 配置页的评价很直接：*"Plugins got broken quite too often, either visually or sometimes even functionally due to breaking changes in Emby Server."*【官方文档】—— 这是选择配置页机制时必须纳入的风险判断。
- **本项目当前配置页是 HTML+JS（Jellyfin 风格），Emby 侧能否 `IHasWebPages` 正常渲染 →【需实机验证 V6】**；若不可用，用 `BasePluginSimpleUI<T>` 重写配置页反而更稳，且它的 `OnOptionsSaving` 钩子可以顺带实现"保存时测试连接"，**从而完全绕开"Emby 端点机制不同"这个难点**（见 §4 第 19 行）。

### 3.10 安装、打包、分发

- **官方安装方式**【官方文档 `emby.media/support/articles/Plugins.html`】：
  - 内置 **Catalog**（分类含 Metadata）→ 详情页 Install → 重启；
  - 以及 **"Manual Install of Plugins"**：把 dll 放到 **Emby Server Data Folder 下的 `plugins/` 目录**，更新插件前先关停服务器、把旧 dll 改名 `.old`（Linux/NAS 还要处理属主与权限）。
  - **官方文档没有提供任何"添加第三方插件目录 URL"的机制**；生态取证也一致（ThePornDB README 直接写 `Repository (Jellyfin only)`）→ **Emby 侧没有 Jellyfin manifest.json 的等价物【官方文档 + 生态取证】；是否存在隐藏/未文档化的第三方目录 →【需实机验证 V1】**
- **打包实测**【生态取证】：解包 Pronium/ThePornDB/MediaInfoKeeper 的真实 Release 资产，zip 内**只有插件自己的 dll（+pdb）**，从不包含 `MediaBrowser.*.dll`。→ 我们的 Emby zip 也必须只放自己的 dll，宿主程序集一律不打包。
- **官方 Catalog 的真实门槛**【官方文档 + 生态取证】：
  - 申请要求：向 Emby 申请 **developer id**，后台在 `plugins.emby.tv/admin`（需开发者账号，未公开 JSON 端点）；
  - 上传物是**单个 DLL**（不是 zip），catalog 的 `Version` 必须与 DLL 的 AssemblyInfo 自报版本**严格一致**；
  - **`targetAbi` 的等价物是 catalog 里"版本级"的 `Required Version` 字段**——也就是说**只有走官方 catalog 才有 ABI 门控**，手动侧载的插件没有任何兼容性检查；
  - 版本号：Emby 从程序集版本读取并展示；生态项目用 4 段版本（如 `2.2.0.153`、`1.6.0.11`）。
- **手动安装没有版本门控**：装错版本不会提示，只会在运行时失败 → 这是用户体验层的真实风险（§8.2）。

### 3.11 授权与开发政策

- **NuGet 包**：`MediaBrowser.Server.Core` / `MediaBrowser.Common` 的 `.nuspec` 实测**没有 `<license>` 元素**，只有 `<projectUrl>https://github.com/MediaBrowser/Emby</projectUrl>`、`<copyright>Copyright © Emby 2023</copyright>`、`<authors>Emby Team</authors>`、`<requireLicenseAcceptance>false</requireLicenseAcceptance>`。
- **官方开发政策**【官方文档 `dev.emby.media/doc/plugins/dev/Development-Policy.html`】原文要点：
  - 面向"进入官方插件目录（catalog）"的插件：*"No plug-in shall directly violate or otherwise circumvent or cause the Emby product as a whole to violate or circumvent any laws as governed by the United States of America."*；明确点名 *"Using 'web scraping' techniques to obtain data from a web site unless that site grants consent…"*、*"Including or otherwise distributing code or libraries in a manner that violates the license terms of those particular libraries or the license terms of Emby."*；违规会被**无通知下架**。
  - **注意**：该政策约束的是"进入官方目录的插件"。我们的插件本身**不抓取任何第三方站点**，只访问用户自己部署的本地 Amane 服务；抓取行为发生在 Amane 侧。但如果未来要申请进 Emby 官方 Catalog，Amane 后端的数据来源会成为审查点（本条为风险提示，不是法律意见）。
- **SDK 示例代码**：官方 SDK 页面明确 *"There are no licenses attached to the code and developers are free to decide how to make use of the provided code examples."*
- **Emby 服务器本体**：`github.com/MediaBrowser/Emby` 仓库实测仍公开、**LICENSE = GPL-2.0**、`pushed_at = 2024-03-27`、4975 stars；但当前 4.x 的实现主体（`Emby.Server.Implementations.dll`）是闭源分发的二进制。**因此不能用 GPL 推断插件 API 的授权，也不能指望读到宿主实现源码来调试**：调试点只能靠日志 + 反编译程序集（本轮就是这样拿到全部签名的）。
- **第三方独立开发与发布**：官方文档给出完整的手动安装流程 + 官方模板 + 调试文档（`dev.emby.media/doc/plugins/dev/index.html` 教如何用 Visual Studio 附加到 Emby Server 调试），**说明第三方独立开发、独立分发是被官方支持的**；不存在"必须进商店才能安装"的限制。

### 3.12 版本兼容性风险（真实证据）

| 风险 | 证据 |
|---|---|
| Emby 4.8 → 4.9 破坏性变更：`IRemoteImageProvider.GetImages` 增加 `LibraryOptions` 形参 | 所有双平台项目都在 Emby 分支多一个形参【生态取证】 |
| 升级 Emby 打坏插件是常态，且用户侧无门控 | ThePornDB open issue #123「Blank actor page after updating to Emby 4.9.3.0」；另有用户实证同一插件在 4.8.10 正常、4.9.1.90 坏【生态取证】 |
| 双平台项目"一边升级、另一边掉队" | ThePornDB 主仓更新到 Jellyfin 12 的同时，Emby 侧仍停在旧 API 形状；JavScraper 2021 年后停更（其兼容层为 Jellyfin 10.4 写死）【生态取证】 |
| Mono/.NET Core 双运行时长期并存 | 近期正式版 90–99 个资产中约 20 个是 mono/netframework（4.10.0.40 = 96/20）【实测】 |
| 宿主 API 无稳定性承诺文档 | 官方 dev 文档明显停留在 2022 年（页脚 Copyright 2022；`Development-Policy`/`Dependency-Injection` 等链接在 `doc/plugins/` 下已 404，真实路径在 `doc/plugins/dev/`）；资料陈旧本身即是风险 |

---

## 4. Jellyfin ↔ Emby API 对照

基准：本仓库当前实际使用的能力（第 2 节）+ Emby Server 4.10.0.40 真实二进制签名（第 3 节）。**未做"为了对齐而臆造对应关系"的条目；不存在的就是不存在。**

| # | Jellyfin 当前实现 | Emby 是否存在 | Emby 对应接口 / API | 能否直接迁移 | 是否需要 Adapter | 功能缺失 | 风险 |
|---|---|---|---|---|---|---|---|
| 1 | `BasePlugin<TConfigurationType>`（`Plugin.cs`） | ✅ | `MediaBrowser.Common.Plugins.BasePlugin<T>`，ctor `(IApplicationPaths, IXmlSerializer)`，`Configuration`/`UpdateConfiguration`/`SaveConfiguration` | 基本可直接迁移（using 改名） | 否 | 无 | 低 |
| 2 | `IHasWebPages` + `PluginPageInfo`（配置页暴露） | ✅ | `MediaBrowser.Model.Plugins.IHasWebPages.GetPages()`，签名与 Jellyfin 相同；`PluginPageInfo` 字段更全 | 可迁移 | 否（但建议改用声明式 UI） | 无 | **中**（运行时是否渲染未验证：V6） |
| 3 | `IRemoteMetadataProvider<Movie, MovieInfo>.GetMetadata` | ✅ | `IRemoteMetadataProvider<TItemType,TLookupInfoType>.GetMetadata(TLookupInfoType, CancellationToken) → Task<MetadataResult<TItemType>>`（同名同形状，含 `IRemoteSearchProvider`） | **是** | 否 | 无 | 低 |
| 4 | `GetSearchResults(MovieInfo, ct) → IEnumerable<RemoteSearchResult>` | ✅ | `IRemoteSearchProvider<TLookupInfoType>.GetSearchResults`（同名同签名） | **是** | 否 | 无 | 低 |
| 5 | `IHasOrder.Order = 1` | ✅ | `IHasOrder { int Order { get; } }`，宿主 `ProviderManager` 真实消费 | **是** | 否 | 无 | 低 |
| 6 | `MetadataResult<Movie>`（`HasMetadata` / `Item` / `ResultLanguage` / `AddPerson`） | ✅ | `MetadataResult<T> : BaseMetadataResult`，成员为 Jellyfin 的超集/子集混合（有 `Provider`、`QueriedById`、`SearchImageUrl`） | **是** | 否 | 无 | 低 |
| 7 | `RemoteSearchResult`（`Name`/`SearchProviderName`/`ImageUrl`/`ProviderIds`/`ProductionYear`） | ✅ | `MediaBrowser.Model.Providers.RemoteSearchResult`，字段一致（另有 `PersonType`/`Role`/`Type`） | **是** | 否 | 无 | 低 |
| 8 | `PersonInfo` + `PersonKind.Actor` + `ImageUrl` + `ProviderIds` | ✅ | `MediaBrowser.Controller.Entities.PersonInfo`，`ImageUrl` 存在 | 是（枚举名不同） | 是（`PersonKind`→`PersonType` 小映射） | 无 | 低 |
| 9 | `Movie`/`Person` 实体属性赋值（`Name`/`Overview`/`PremiereDate`/`ProductionYear`/`RunTimeTicks`/`CommunityRating`/`Genres`/`Studios`/`ProviderIds`） | ✅ | `BaseItem` 同名属性，类型一致（`CommunityRating` 为 `float?`、`PremiereDate` 为 `DateTimeOffset?`、`Genres`/`Studios` 为 `string[]`、`SetStudios/SetGenres/SetProviderIds`） | 是（映射代码需复制一份） | **是**（宿主对象不同，必须各写映射） | 无 | 低 |
| 10 | `SetProviderId` / `GetProviderId` / `ProviderIds` 字典 | ✅ | `ProviderIdDictionary` + `MediaBrowser.Model.Entities.ProviderIdsExtensions`（同名同签名） | **是** | 否 | 无 | 低 |
| 11 | `IExternalId`（`ProviderName` + `Key` + `Type`） | ✅（成员不同） | `IExternalId{ Supports(IHasProviderIds), Key, Name, UrlFormatString }`；**无 `Type`、无 `ProviderName`，也不自动拼 "Id" 后缀**（已实测） | 否，需重写（约 10 行/个） | 是 | Emby 无"媒体类型限定"，靠 `Supports` 判定 | 低 |
| 12 | `IRemoteImageProvider.GetImages(BaseItem, ct)` | ✅（签名不同） | `GetImages(BaseItem, **LibraryOptions**, ct)` | 否 | 是 | 无 | **中**（该接口 4.8→4.9 已破坏性变更过一次） |
| 13 | `IRemoteImageProvider.GetImageResponse → Task<HttpResponseMessage>` | ✅（返回类型不同） | `Task<MediaBrowser.Common.Net.HttpResponseInfo>` | 否 | 是（Core 返回"流+内容类型+状态码"，由适配器包装） | 无 | **中** |
| 14 | `PluginConfiguration : BasePluginConfiguration` | ✅ | `MediaBrowser.Model.Plugins.BasePluginConfiguration`（同名） | 字段可直接迁移 | 视配置页方案 | 无 | 低 |
| 15 | `IHttpClientFactory.CreateClient()` | ❌ | Emby 用 `MediaBrowser.Common.Net.IHttpClient` + `HttpRequestOptions`（**`Microsoft.Extensions.Http` 不在宿主**） | 否 | **是**（Core 抽象 HttpClient 提供者） | 无（只是机制不同） | **中** |
| 16 | 图片下载返回 `HttpResponseMessage`（流由宿主读） | ⚠️ | `HttpResponseInfo{Content, ContentType, StatusCode, Headers}` | 否 | 是 | 无 | 中 |
| 17 | `ILogger<T>`（`Microsoft.Extensions.Logging`） | ⚠️ | 宿主自带 `MediaBrowser.Model.Logging.ILogManager/ILogger`；本机实测 `Microsoft.Extensions.Logging.Abstractions.dll` **在** Emby 4.10 system 目录里，但容器是否注册 `ILogger<T>` 未验证 | 否 | 是（一行日志桥接或自定义最小日志接口） | 无 | **中** |
| 18 | `IPluginServiceRegistrator`（DI 注册 `AmaneClient`） | ❌ | 无等价接口；有 `IApplicationHost.Resolve<T>/GetExports<T>` + SimpleInjector | 否 | 是（适配器内 `Lazy<T>` 单例即可） | 无 | **中** |
| 19 | `Api/AmaneDiagnosticsController`（`[ApiController]`+`[Route]`+`[Authorize]`，ASP.NET Core MVC） | ❌ | Emby 用自研 REST 体系（`MediaBrowser.Model.Services.IService` + `[Route]` + `IReturn<T>`） | 否 | 是 | 若不实现则丢"测试连接/清缓存"按钮 | **高** |
| 20 | 媒体库扫描 / 刷新（`GetMetadata` 被宿主调用，含 `MovieInfo.ProviderIds/Name`） | ⚠️ 部分 | Provider 由 `IProviderManager` 驱动；刷新携带 `LibraryOptions`/`MetadataRefreshOptions`/`IDirectoryService`；`MovieInfo`/`PersonLookupInfo` 存在且含 `Name`/`ProviderIds`/`PremiereDate`/`Year` | 业务逻辑可迁移 | 是 | 无明确缺失 | **中**（行为差异需实机） |
| 21 | 手动识别 / External ID 输入框 | ⚠️ | `RemoteSearchQuery<T>` + `GetRemoteSearchResults` + `GetExternalIdInfos`；识别弹窗渲染未验证 | 搜索逻辑可迁移 | 是 | 未验证 | **中** |
| 22 | manifest.json 订阅安装 / 自动更新 | ❌ | 无；官方只有内置 Catalog + 手动放 `plugins/` | 不适用 | 不适用 | **缺失**（体验层） | **高**（分发流程，非代码） |
| 23 | 图片 URL 双轨制（代理下载 vs 直出） | ⚠️ **部分支持** | 4 条消费路径：A 自动刷新、B Identify 缩略图 → **回调插件 `GetImageResponse`（可带 Bearer）**；C 图片选择器预览、D 手动下载 → **Emby 自己拉 URL（插件无法带 Bearer）** | 否 | 是（URL 策略需按宿主分叉） | **需 Token 的 `/api/resources/proxy`、`/api/resources/{hash}` 在 C/D 路径会 401** | **高** |
| 24 | 演员头像写入 `PersonInfo.ImageUrl` | ⚠️ | 接口支持，生态普遍使用 | 是 | 否 | Emby 4.9.3 人物页空白 issue 未关 | **中** |
| 25 | 独立诊断端点以外的能力（`GET /Amane/Health` 服务端探活） | ❌ | 需改用声明式 UI 的 `OnOptionsSaving`/`OnBeforeShowUI` 钩子，或自研 `IService` | 否 | 是 | 无 | 中 |

---

## 5. 可共享代码分析（按模块，不用百分比糊弄）

### 5.1 现有文件逐个判定

| 文件 | 行数 | 耦合等级 | 结论 | 迁移代价 |
|---|---|---|---|---|
| `AmaneModels.cs` | 206 | **完全平台无关**（`System.Text.Json` + BCL） | **整体进 Core** | 仅 namespace 变更 |
| `AmaneClient.cs` | 668 | **轻度耦合**（8 处配置单例 + 2 个常量引用 + `IHttpClientFactory`/`ILogger<T>`） | **进 Core**，加 2 个 seam | 改动集中在 11 处，其余逻辑不动 |
| `Providers/AmaneMovieProvider.cs` | 227 | **强耦合**（Jellyfin 实体/`MetadataResult`） | 拆两部分：`FormatDisplayName`（可进 Core）+ `MapToMovie` 与 Provider 类（留适配器） | 需各写一份映射，逻辑可照抄 |
| `Providers/AmanePersonProvider.cs` | 133 | 强耦合 | 同上 | 同上 |
| `Providers/AmaneImageProvider.cs` | 93 | 强耦合（宿主图片接口 + `RemoteImageInfo`） | 留适配器；URL 策略来自 Core | 小 |
| `Providers/AmaneMovieExternalId.cs` / `AmanePersonExternalId.cs` | 50 | 强耦合（接口成员不同） | 各宿主各写 | 每侧约 10 行 |
| `Plugin.cs` | 55 | 强耦合 | 各宿主各写 | 小 |
| `ServiceRegistrator.cs` | 17 | **Jellyfin 专有** | 只留 Jellyfin | — |
| `Configuration/PluginConfiguration.cs` | 34 | 轻度（基类） | Core 出 POCO 定义，适配器继承各自宿主的 `BasePluginConfiguration`（或 Core 定义接口 + 适配器持有实例） | 小 |
| `Api/AmaneDiagnosticsController.cs` | 48 | **Jellyfin 专有**（ASP.NET Core） | 只留 Jellyfin；Emby 另写或改用 UI 钩子 | 中 |
| `Configuration/configPage.html` | 139 | 中度（Jellyfin `ApiClient` JS + 嵌入式资源） | HTML/表单结构可复用，JS 的配置读写与端点调用需按宿主各写一份；或 Emby 侧改用声明式 UI 直接不共享 | 中 |

### 5.2 明确的"可以共享"清单（进 Core）

- Amane HTTP API Client 的**全部请求/反序列化逻辑**：`SearchAsync`、`GetByIdAsync`、`LookupAsync`、`SearchActorsAsync`、`GetActorByIdAsync`、`LookupActorAsync`。
- **ID 解析与归一化**：`ResolveMetadataAsync` / `ResolveActorAsync` / `NormalizeIdValue` / `TryParseInternalId`（含"数字直取 → 番号/名字搜索 → 名称兜底"的降级链）。
- **弹性策略**：`SemaphoreSlim` 并发背压、每请求 linked CTS 超时、连续失败熔断（阈值/冷却）、图片独立超时下限、浏览器 UA。
- **演员进程内缓存**（TTL 双键写入、`ClearActorCache`）。
- **鉴权与安全边界**：Bearer 只对 Amane 域内 URL 附加（"不泄露给第三方图床"这条规则必须在 Core 里，两侧行为一致）。
- **图片 URL 规范化**：`ToProxyImageUrl` / `ToDirectImageUrl`。
- **DTO + JSON 序列化选项**（`JsonPropertyName`、`JsonSerializerDefaults.Web`、大小写不敏感）、`GetOriginalTitle` 从 `raw` 提取原标题。
- **健康检查/连接测试的业务逻辑**（`CheckHealthAsync` 的两段探针与 `AuthStatus` 判定语义）。
- **错误处理哲学**：查询失败记日志返回 null、外部取消除外向上抛、图片失败抛异常防坏图——平台无关。
- **展示名规则**：`番号 + 空格 + 标题`（`FormatDisplayName`）。
- **ProviderId 键名常量**：`"Amane"` / `"AmaneId"`。

### 5.3 明确的"不能共享、必须两个适配器各写"清单

- **元数据映射到宿主对象**：`MapToMovie`、Person 实体映射、`PersonInfo` 构造（类型不同、可空语义可能不同）。
- **`MetadataResult<T>` / `RemoteSearchResult` / `RemoteImageInfo` 的组装**（`MetadataResult` 是宿主类型，虽然字段像，但它是宿主契约）。
- **图片响应对象构造**：`HttpResponseMessage`（Jellyfin）vs `HttpResponseInfo`（Emby）。
- **Provider 接口实现本身**：`GetImages` 形参不同、`GetImageResponse` 返回类型不同、`IExternalId` 成员不同。
- **配置类与配置页**：基类与前端 JS 不同（Emby 建议改用声明式 UI）。
- **DI/服务获取、日志适配、插件入口**。
- **诊断端点**（或 Emby 侧改为 UI 钩子，功能等价但实现完全不同）。

### 5.4 关于"Core 返回自己的平台无关 DTO"是否必要

**必要，但现在的代码已经天然是这个形状了**——`AmaneMetadata` / `AmaneActor` 就是平台无关 DTO，Provider 只是"把 DTO 摊平到宿主对象"。
所以不需要引入额外的抽象层（也不需要为 Emby 再造一套 DTO）。需要做的只是：

1. 把**映射代码**从 Provider 类里拆成 Core 的"纯函数"部分（可选）：例如 Core 提供 `AmaneMetadataView { DisplayName, OriginalTitle, Overview, PremiereDate?, ProductionYear?, Studio, Genres[], RuntimeMinutes?, Score10? }`，两侧适配器只做"View → 宿主实体"的机械赋值。
   - 好处：文字/评分/日期等**归一化规则只有一份**，且可以在 Core 单测里断言（现有 `MovieMappingTests` 的断言可以直接搬过去，只是断言对象从 `Movie` 换成 `View`）。
   - 代价：多一层 DTO。**只有当两侧的日期/评分/流派规则确实会漂移时才值得**——按当前规模（一次映射、两处使用）建议做，因为它同时解决了"测试复用"问题。
2. **不要**为了复用把 `MetadataResult<T>`/`PersonInfo` 抽象成 Core 类型再转换——那是过度抽象：两侧类型都是宿主契约且形状接近，硬抽象只会引入转换层和维护成本。

### 5.5 复用程度结论（按模块，不给整体百分比）

| 模块 | 复用判定 |
|---|---|
| Amane API Client（请求/反序列化/解析/弹性/缓存/URL 策略） | **可跨宿主复用，改动仅限 2 个 seam** |
| DTO / JSON / ID 解析 / 展示名规则 | **原样复用** |
| 元数据→宿主对象映射 | **规则复用（Core 出 View），赋值代码各自一份** |
| Provider 接口实现 | **不可复用**（接口形状差异） |
| 配置模型 | 字段复用，基类/页面各自一份 |
| 配置页 UI | HTML 结构可参考，JS 与端点**不可复用** |
| 测试 | 契约/缓存/弹性/URL/ID 解析类测试可搬 Core；实体映射与 `IExternalId` 契约测试留 Jellyfin |

### 5.6 拆分时必须"原样保留"的行为细节（PoC 发现，容易在重构中被无意改掉）

1. **默认值语义**：原 `Plugin.Instance?.Configuration?.MaxConcurrentRequests ?? 4` 在"配置对象为 null"与"配置存在且为 0"两种情况下分别得到 4 和 1（被 `Math.Max(1, …)` 夹到 1）。抽象成 `IAmaneSettings` 后，**兜底逻辑落在适配器侧**，必须保持一致，并把这个契约写进接口 XML 文档——否则 Jellyfin 用户会感到行为漂移。
2. **`GetAsync` 不读 `_apiTokenOverride`**：原实现里只有图片与健康检查路径使用测试注入的 token，元数据查询走插件配置。这看起来不一致，但**拆分时不要"顺手修"**，否则会改变现有行为；是否修正应作为独立决策。
3. **图片下载成功路径不释放响应**：`GetImageAsync` 成功时把 `HttpResponseMessage` 交给宿主读取，失败分支才 `Dispose`。Emby 侧对应的是 `HttpResponseInfo(IDisposable[] disposables)`（有显式释放语义），迁移时要保证"成功路径由宿主消费流"这一约定不被破坏。
4. **Bearer 只发 Amane 域内 URL**：这条规则必须在 Core 里，不能被某个适配器绕过（现有测试 `GetImage_ExternalUrl_NeverLeaksBearerToken` 必须继续通过）。

---

## 6. 推荐仓库 / Solution 架构

### 6.1 是否需要多个 csproj / sln —— **需要，3 个工程 + 1 个 sln**

硬约束（不是偏好）：两侧 SDK 都提供**同名程序集** `MediaBrowser.Controller.dll` / `MediaBrowser.Model.dll` / `MediaBrowser.Common.dll`（实测：Jellyfin.Controller 10.11.10 → `lib/net9.0/MediaBrowser.Controller.dll`；MediaBrowser.Server.Core → `lib/netstandard2.0/MediaBrowser.Controller.dll`）。同一个编译单元无法同时引用它们（类型全名冲突：`MediaBrowser.Controller.Providers.IRemoteMetadataProvider` 等在两侧都存在）。

**⚠️ 这个坑比"编译报错"更危险**【实测】：在同一个项目里**同时**引用 `Jellyfin.Controller 10.11.10` 与 `MediaBrowser.Server.Core 4.9.1.90`，`dotnet build` **会成功且 0 警告**，但用 MSBuild `ReferencePath` 打印可见 **Emby 那一整套程序集被静默丢弃**，实际只编译了 Jellyfin 版。
→ 含义：**"能编译"不能证明"引用生效"**；而且如果只是在单工程里加一行 PackageReference 就以为做完了 Emby 支持，会得到一个"看起来成功、实际完全是 Jellyfin 版"的产物。必须做构建维度切分。

因此只有两种可行形态：

- 形态 A（生态主流）：单工程 + 两个 MSBuild Configuration，每次只引用一侧 SDK，业务代码用 `#if __EMBY__` 分叉；
- 形态 B（推荐）：3 个程序集，Core 不引用任何宿主 SDK，每个适配器各引用一侧。

**为什么推荐 B（本项目具体情况）**：
1. 本项目的"共享部分"是**完整可分离的**（`AmaneClient` + DTO + ID 解析），不像生态项目那样共享逻辑嵌在 Provider 类里 → 拆工程不会产生"跨边界的碎 `#if`"。**PoC 实测改造量：Core 侧 24 条字面替换规则（`AmaneClient.cs` 23 + `AmaneModels.cs` 1，实际替换点 25 个）、Jellyfin 适配器侧 8 处（5/9 文件），`Plugin.cs`/`PluginConfiguration.cs`/两个 `ExternalId` 四个文件一字未改。**
2. 条件编译方案会让 **Emby 侧的改动直接出现在 Jellyfin 的编译单元里**——对本项目"不得破坏现有 Jellyfin 用户"的硬要求来说，工程边界是一道可验证的护栏（Jellyfin 工程的 `ProjectReference` 只有 Core，永远看不到 Emby SDK）。
3. 现有测试用 `InternalsVisibleTo` 依赖主工程内部类型；拆工程后 Core 测试与适配器测试边界清晰（PoC 已验证：Core 加 `InternalsVisibleTo("Amane.Core.Tests")` 后 21/21 运行时检查通过）。
4. `origin/feat/jellyfin-12` 表明未来还要并行 Jellyfin 10.11/12.x 两条线，工程化拆分比在单工程里堆 Configuration 更可控。
5. **类型隔离是"运行期"而非"编译期"问题**：实测两套 `MediaBrowser.*` 同栈不会报错，只会静默错绑定（Jellyfin 胜出，运行输出 `MediaBrowser.Model, Version=10.11.10.0`）。因此"Core 不引用任何 `MediaBrowser` 类型 + Emby 工程禁止 `Jellyfin.*`"不是风格偏好，而是这个架构**唯一的隔离机制**。

**代价（如实说明）**：多 2 个 csproj、多 1 个 sln、CI 多 1 个构建目标、`bin/` 路径变化（必须同步改 CI 与本地脚本）。生态里没有 3 工程先例 → 这一项属于**本项目自己的决策**，其可行性由本次 PoC 编译验证支撑（第 6.4 节）。

### 6.2 推荐结构

```
Jellyfin.Plugin.Amane/                      # 仓库根（GitHub 仓库名保持不变）
├── manifest.json                           # ★ 位置/内容格式不变（用户订阅 URL 依赖此路径）
├── meta.json  thumb.png  LICENSE  README.md  AGENTS.md
├── Amane.sln                               # 新增
├── src/
│   ├── Amane.Core/                         # net8.0，零宿主 SDK 引用
│   │   ├── Amane.Core.csproj
│   │   ├── AmaneClient.cs                  # 由现文件迁移 + 2 个 seam
│   │   ├── AmaneModels.cs                  # 原样迁移
│   │   ├── AmaneSettings.cs                # 平台无关配置 POCO + IReadOnlySettings 接口
│   │   ├── IHttpClientProvider.cs          # 取代 IHttpClientFactory
│   │   ├── AmaneMetadataView.cs            # 映射规则归一化产物（可选但推荐）
│   │   ├── AmaneIds.cs                     # "Amane"/"AmaneId" 常量 + 解析
│   │   └── ImageUrlPolicy.cs               # ToProxyImageUrl/ToDirectImageUrl
│   ├── Amane.Jellyfin/                     # net9.0（10.11.x 线），产出 Jellyfin.Plugin.Amane.dll
│   │   ├── Amane.Jellyfin.csproj           # AssemblyName 固定为 Jellyfin.Plugin.Amane
│   │   ├── Plugin.cs  ServiceRegistrator.cs
│   │   ├── Configuration/{PluginConfiguration.cs, configPage.html}
│   │   ├── Providers/{AmaneMovieProvider.cs, AmanePersonProvider.cs, AmaneImageProvider.cs,
│   │   │                AmaneMovieExternalId.cs, AmanePersonExternalId.cs}
│   │   ├── Api/AmaneDiagnosticsController.cs
│   │   └── JellyfinHttpClientProvider.cs   # IHttpClientFactory → Core seam
│   └── Amane.Emby/                         # net8.0（Emby 4.9/4.10 netcore），产出 Amane.Emby.dll
│       ├── Amane.Emby.csproj
│       ├── AmaneEmbyPlugin.cs              # BasePlugin<EmbyPluginConfiguration>
│       ├── Configuration/EmbyPluginConfiguration.cs(.html 或声明式 UI 选项类)
│       ├── Providers/{EmbyMovieProvider.cs, EmbyPersonProvider.cs, EmbyImageProvider.cs,
│       │                EmbyMovieExternalId.cs, EmbyPersonExternalId.cs}
│       ├── EmbyHttpClientProvider.cs       # 进程内 HttpClient / IHttpClient 包装
│       └── EmbyLoggerBridge.cs             # ILogManager → 最小日志接口（或 ILogger<T>）
├── tests/
│   ├── Amane.Core.Tests/                   # 目标 net10.0（Core 是 net8.0，任意新 TFM 均可测），搬现有 5 类测试
│   └── Jellyfin.Plugin.Amane.Tests/        # 保留：实体映射 + IExternalId 契约 + Provider 交互
├── docs/
│   ├── EMBY_COMPATIBILITY_RESEARCH.md      # 本文档
│   └── research/                           # 证据笔记（SDK/API/生态/PoC 原始取证）
└── .github/workflows/{release.yml, release-emby.yml, ci.yml}
```

**Jellyfin 兼容性红线（必须逐条满足）**：
- 插件 `AssemblyName` 保持 `Jellyfin.Plugin.Amane`（zip 里的 dll 名不变 → 既有安装原地升级）；
- 插件 GUID 保持 `9f2e4a6b-7c1d-4e3f-8a5b-0d9c2e1f4a7b`（Jellyfin 用 GUID 识别已安装插件，变了会被当成新插件）；
- `manifest.json` 路径与结构不变，且**只保留 Jellyfin 条目**；`versions[].sourceUrl` 仍指向 `Jellyfin.Plugin.Amane.zip`；
- Jellyfin 适配器 TFM 保持 `net9.0`（10.11.x 线），`Jellyfin.Controller/Model` 版本仍 10.11.10；
- 插件 `Name` 仍为 `"Amane"`（配置文件目录名/显示名不变，避免配置丢失）。

**Emby 侧建议**：新 GUID（避免两个宿主日志/支持材料混淆），但 `ProviderIdName="Amane"` / `InternalIdProviderIdName="AmaneId"` 与 Jellyfin 版**保持一致**，这样用户在两套系统间迁移时元数据绑定可识别（生态先例：bangumi 双实现刻意共用键名）。

### 6.3 TFM 决策

| Core TFM | Emby 适配器 TFM | 编译 | Jellyfin bin | Emby 插件目录（copy-local 打包代理） | 风险 |
|---|---|---|---|---|---|
| **net8.0** | **net8.0** | ✅ 0/0 | 31 个（30 + `Amane.Core.dll`），干净 | **14 个文件 / 10 个程序集，无多余垫片** | **低（推荐）** |
| net8.0 | netstandard2.0 | ✅ 0/0（需 3 处源码改动） | — | 14 个文件 / 10 个程序集，无多余垫片 | 低 |
| netstandard2.0 | net8.0 | ✅ 0/0（3 处 + 垫片） | 31 个，干净 | 14 个文件 / 10 个程序集，无多余垫片 | 低 |
| netstandard2.0 | netstandard2.0 | ✅ 0/0（共 7 处改动） | 31 个，干净 | **22 个文件 / 18 个程序集：多带 `Microsoft.Bcl.AsyncInterfaces`、`System.Text.Json`、`System.Memory`、`System.Buffers`、`System.Numerics.Vectors`、`System.Runtime.CompilerServices.Unsafe`、`System.Text.Encodings.Web`、`System.Threading.Tasks.Extensions`** | **高（正是历史事故形态）** |

**结论：`Core net8.0` + `Emby net8.0` + `Jellyfin net9.0`。** 理由：
1. 4 组组合都能编译，差别只在**分发洁净度**；**只有"Core 与 Emby 适配器同为 netstandard2.0"这一个组合**会把 8 个宿主应提供的程序集带进插件目录（其余三个组合都只有 14 个文件 / 10 个程序集）——这与生态里"插件自带 8.0 依赖打到 .NET 6 宿主"的真实事故是同一形态（§8.2），也是最难排查的一类故障。
2. netstandard2.0 还需要**改动 Core 源码**：`trimmed["Amane:".Length..]` 依赖的 `System.Range/Index` 在 netstandard2.0 下不可用，**且 `System.Memory` 包并不提供 `Range/Index`**（实测字节扫描确认），最终只能用 `Substring` 改写；此外还有 `StartsWith(char)`、`ReadAsStreamAsync(ct)`、`IsExternalInit` 垫片与 12 个可空性警告。**为了"代码复用"而劣化被复用的代码，方向是反的。**
3. 代价是放弃 Emby 4.8(.NET 6) 与 Mono —— 这是**产品决策**：要覆盖 4.8 就必须接受第 1 条的分发风险，且 Core 也无法降级（netstandard2.0 会让两侧都变脏）。
4. 附注：`System.Text.Json 8.0.0` 存在 NU1903 高危漏洞通告（2 条）——本方案在 net8.0 下不需要引用该包（用框架内置版本），这一点也顺带规避了。

### 6.4 共享层是否应完全禁止引用 Jellyfin/Emby SDK

**应完全禁止**，且要机械化保障：
1. Core 的 csproj **不出现** `Jellyfin.*` / `MediaBrowser.*` 任何 `PackageReference`；依赖只有 `Microsoft.Extensions.Logging.Abstractions`（若保留 `ILogger<T>`）与 BCL。
2. CI 加一条断言（例如 `dotnet list src/Amane.Core/Amane.Core.csproj package` 输出中不得含 `Jellyfin|MediaBrowser`），防止未来无意引入。
3. Core 的 source 不得出现 `using MediaBrowser.*` / `using Jellyfin.*`（可用 grep 断言）。
4. Core 不做任何"宿主对象"构造：只返回自己的 DTO/视图。

这一条同时带来一个额外好处：**Core 的测试完全不依赖任何一个宿主 SDK**，可以在任何 TFM 上跑，且不会被 Emby/Jellyfin 的 SDK 版本升级打断。

---

## 7. 构建与发布方案

### 7.1 是否继续保留现有 manifest.json —— **保留，一行不改地继续用**

- 现网用户订阅的是 `https://raw.githubusercontent.com/.../main/manifest.json`；**路径、GUID 条目、versions 结构都必须保持不变**。
- CI 的 python 回写逻辑硬编码 `manifest[0]`：**绝对不要把 Emby 当作第二个条目加进 manifest.json**（会直接打坏 Jellyfin 自动更新）。
- Emby 没有 manifest 概念，因此 Emby 版本不写进任何 Jellyfin 清单。

### 7.2 目录/工程重构对 Jellyfin Repository 更新的影响

**零影响，只要满足 7.1 的红线**。Jellyfin 端的订阅链路只依赖三样东西：
1. manifest 的 URL 与 `sourceUrl` 指向的 Release 资产名（`Jellyfin.Plugin.Amane.zip`）；
2. zip 内的 dll 名（`Jellyfin.Plugin.Amane.dll` + 拆分后必须一起加的 `Amane.Core.dll`）与插件 GUID；
3. `targetAbi`（当前 `10.11.10.0`）。
仓库内部从根目录单工程搬成 `src/` 多工程，对这三者都不可见——**必须同步改的是两处**：① CI 里 `bin/Release/net9.0` 路径（改成 `src/Amane.Jellyfin/bin/Release/net9.0`）；② zip 打包内容（从 1 个 dll 变成 2 个 dll）。**第 ② 条是唯一会影响现有用户的改动，必须与拆分同 PR 验证。**

### 7.3 Release 同时产出两个 zip —— **可以，但用两条独立流水线**

- `Jellyfin.Plugin.Amane.zip`：**必须含 `Jellyfin.Plugin.Amane.dll` + `Amane.Core.dll`**（拆分后 Core 是独立程序集；只用单 dll 打包会 FileNotFoundException）。除此之外与现在一致：不含任何 `MediaBrowser.*`。
- `Amane.Emby.zip`（命名建议，便于用户区分）：**必须含 `Amane.Emby.dll` + `Amane.Core.dll`**（Core 是独立程序集，两个宿主都一样）。**不要打包任何 `MediaBrowser.*.dll`**（生态一致做法【生态取证】；同时规避 SDK 授权不明的问题）。PoC 实测：Emby 适配器**不要开** `CopyLocalLockFileAssemblies`，否则 `MediaBrowser.*.dll` 会落进输出目录（net8.0 方案下 copy-local 输出为 14 个程序集，需按白名单校验）。

### 7.4 版本号：独立

- Jellyfin 线继续 `1.0.x`（4 段 `1.0.7.0`），与 manifest `versions[].version` 对齐；
- Emby 线从 `0.1.0.0` 起独立演进；
- 两者共用一个 `Amane.Core` → **Core 的变更必须同时触发两侧的兼容性回归**（见 7.7）。

### 7.5 Git Tag：分开管理

- `v1.0.8` → Jellyfin 发版（沿用现有工作流，语义不变）；
- `emby-v0.1.0` → Emby 发版（新工作流）；
- 不建议用同一 tag 双发：两边版本节奏不同，且同一 tag 无法表达"只修 Emby"的情形。

### 7.6 GitHub Actions 方案（三个 workflow）

| Workflow | 触发 | 作用 |
|---|---|---|
| `release.yml`（现有，保持语义） | `push: tags: v*` | 构建 Jellyfin 适配器 → zip → Release → 回写 manifest.json。**内部不加入任何 Emby 构建步骤**。 |
| `release-emby.yml`（新增） | `push: tags: emby-v*` | 单独构建 `Amane.Emby` → zip → 附到同一个 GitHub Release 或在 Emby tag 上建 Release；**不碰 manifest.json**。 |
| `ci.yml`（新增，可选） | `pull_request` / `push: main` | 构建 3 个工程 + 跑全部测试 + 校验 Core 无宿主 SDK 引用。失败只挡合并，不影响发版。 |

关键点：**两个 release workflow 完全解耦**，因此"Emby 构建失败"不可能阻塞 Jellyfin 正式 Release（这正是用户担心的场景）。反过来，Jellyfin 的发版也不会因为 Emby 的 TFM/引用调整而失败。

### 7.7 需要新增的验证（防止 Core 改动悄悄打坏某一侧）

- Core 有行为改动时，**两侧适配器测试都必须跑**（CI 的 `ci.yml` 里加矩阵：Jellyfin 适配器测试（net9.0 宿主引用）+ Core 测试（net10.0 宿主无关））。
- 基于 PoC 的三条具体护栏（可直接实现）：
  1. **Emby 适配器 csproj 里禁止出现任何 `Jellyfin.*` 包引用**（CI grep）；
  2. **构建后扫两个适配器 DLL 的引用表**：Emby 适配器不得引用 `Jellyfin.*`，Jellyfin 适配器不得引用 `Emby.Media.Model`/`Emby.Web.GenericEdit`（PoC 用 ilspycmd / 字节堆扫描实现过）；
  3. **Emby 适配器不开 `CopyLocalLockFileAssemblies`**，确保 `MediaBrowser.*.dll` 不进插件目录。
- 建议给 Jellyfin 侧加一个"打包冒烟"：构建后断言 `src/Amane.Jellyfin/bin/Release/net9.0/Jellyfin.Plugin.Amane.dll` 与 `Amane.Core.dll` 都存在，且 **zip 内恰好 2 个 dll**（防止重构后 zip 结构变化导致用户升级失败）。

### 7.8 README 如何区分两个平台

建议在 README 顶部放一张支持矩阵，并分节写安装/限制：

```markdown
| 平台 | 状态 | 宿主要求 | 安装方式 | 备注 |
|---|---|---|---|---|
| Jellyfin | ✅ 稳定（v1.x，有真实用户） | 10.11.10 ~ 10.11.x（net9.0） | 插件仓库订阅 manifest.json / 手动 zip | 现有用户升级路径不变 |
| Emby | 🧪 实验（PoC→v0.x） | 4.9.x / 4.10.x（.NET 8 netcore 安装） | 手动：下载 zip → 解压 dll → 放入 `plugins/` → 重启 | 无自动更新；图片选择器预览/手动下载可能失败；不支持 Emby 4.8 与 Mono 资产；功能对齐清单见下 |
```

并明确写出 Emby 侧的**已知限制**（这一节必须诚实，否则支持成本会转到 issue 区）：
- 无插件目录订阅/自动更新（Emby 官方机制所限）；
- 配置页机制待验证（可能改用声明式 UI，界面与 Jellyfin 版不完全一样）；
- 识别对话框/人物头像在 Emby 侧的行为待实机确认；
- 已实测验证过的 Emby 版本范围（4.9.x / 4.10.x；4.8 与 Mono 资产不支持——原因见 §6.3，若将来要做必须先解决 8 个程序集垫片的分发风险）。

---

## 8. 兼容性与技术风险

### 8.1 Jellyfin 侧（现有用户）

| 风险 | 判断 | 缓解 |
|---|---|---|
| 重构导致 dll 名/GUID/zip 结构变化 → 升级被当成新插件或安装失败 | **可控但必须严格** | 第 6.2/7.2 红线 + CI 断言。**特别注意：Core 独立成程序集后，zip 必须同时含 `Amane.Core.dll`**，否则现有用户升级后会 `FileNotFoundException`（这是本轮 PoC 暴露出的唯一"会影响现有用户"的改动点） |
| `manifest.json` 被 Emby 内容污染 → 自动更新解析失败 | 可控 | 明确禁止写入 Emby 条目 |
| Core seam 改造引入行为回归（如 Bearer 泄漏、超时/熔断语义变化） | 中等 | 现有 56 个测试全部保留；`AmaneClientImageTests` 的"Bearer 不外泄"断言必须继续通过 |
| Jellyfin 12 线（`feat/jellyfin-12`）与新结构冲突 | 中等 | 拆分时同步 rebase 该分支（只改 csproj/TFM/CI 路径） |
| 本机无 dotnet 导致本地验证不可复现 | 低（环境问题） | 记录工具链安装步骤（本轮已装 SDK 10.0.401 到 `~/.dotnet`） |

### 8.2 Emby 侧技术风险

| 风险 | 等级 | 依据 |
|---|---|---|
| 宿主服务差异（无 `IHttpClientFactory`、`ILogManager`、SimpleInjector、无 `IPluginServiceRegistrator`） | **高** | 【实测】4.10 system 目录清点 + 接口签名；全部有适配路径，但必须实机验证 |
| 图片链路（`GetImageResponse` 返回 `HttpResponseInfo`、`GetImages` 多 `LibraryOptions`、双轨制 URL 语义） | **高** | 【实测】签名差异；【生态取证】4.8→4.9 已因此破坏过一次；【生态取证】人物页空白 issue 未关 |
| 配置页机制（`IHasWebPages` 是否仍渲染 vs 官方推荐 `BasePluginSimpleUI`） | 中高 | 【实测】接口存在于 4.10；【官方文档】官方明确吐槽旧 HTML 页面"often broken" |
| 诊断端点无法移植（ASP.NET Core MVC → Emby 自研 `IService`） | 中高 | 【实测】Emby 无 ASP.NET Core MVC 管道；【生态取证】Emby 插件端点是 `IService`+`[Route]` |
| 版本碎片化 + 无 ABI 门控（4.8/4.9/4.10、Mono/netcore、升级即静默失败） | 中高 | 【实测】Release 资产双运行时；【生态取证】升级打坏插件的真实 issue |
| **依赖漂移**：插件自带比宿主更新的依赖程序集 → 旧宿主 `Could not load file or assembly` | **高（已有真实事故）；本方案已通过 TFM 选型规避** | Emby LDAP 插件 1.0.44 在 Emby 4.8.10(.NET 6.0.31) 报 `Could not load file or assembly 'Microsoft.Bcl.AsyncInterfaces, Version=8.0.0.0'`——根因就是插件自带了 8.0 时代的依赖。【生态取证】；**本报告选 net8.0（而非 netstandard2.0）正是为了不产生这类垫片分发**（实测：Core+Emby 同为 netstandard2.0 时输出 22 文件 / 18 程序集，含 8 个垫片；net8.0 组合为 14 文件 / 10 程序集，0 垫片） |
| **接口成员新增 → `TypeLoadException`**：Emby 4.10.0.17 给 `ILiveStream` 加成员后，4.9 编译的 DLL 直接类型加载失败 | 中高 | emby-xtream 的 ADR-017 记录了该事故与解法【生态取证】 |
| 插件加载后异常无隔离（Emby 侧插件异常可能影响启动/刮削流程） | 中 | 【需实机验证】 |
| 反编译依赖（宿主闭源，只能靠 dump 推断内部行为） | 中 | 【实测】本轮全部签名来自二进制反射 |
| 元数据映射规则漂移（同一 Core 供两侧，日期/评分/流派归一化只应有一处） | 低 | 设计上把规则放 Core 的 View |

### 8.3 功能缺失清单（要在文档/README 里如实声明）

1. Emby 侧**没有自动更新与仓库订阅**（机制不存在）。
2. Emby 侧**没有 targetAbi 兼容门控**（装错版本只会运行时失败；只有进官方 catalog 才有 `Required Version`）。
3. Emby 侧配置页形态可能与 Jellyfin 不同（取决于采用 `IHasWebPages` 还是声明式 UI）。
4. Emby 4.8（.NET 6）与 Mono/NAS 资产不支持（net8.0 方案的固有边界，见 §6.3；不是"暂未验证"而是"已实测否决 netstandard 路线"）。
5. Jellyfin 侧的"识别框填 `Amane:番号`"这类 UI 文案行为在 Emby 侧可能不同（`IExternalId.Name` 语义差异，且 Emby 不会自动拼 "Id"）。
6. **图片功能在 Emby 侧会降级**：图片选择器的**预览**与**手动下载**由 Emby 自己拉 URL、插件无法注入 `Authorization`，因此需要 Token 的 Amane 本地资源（`/api/resources/proxy`、`/api/resources/{hash}`）在这两条路径上会 401。可用的缓解手段：
   - 短期：Emby 侧 `RemoteImageInfo.Url` 直接给**外源绝对 URL**（回到"直出"策略），代价是失去代理带来的防盗链/本地缓存收益，且 Amane 本地裁切海报无法使用；
   - 中期（推荐）：请 Amane 侧支持**自鉴权图片 URL**（例如带短期签名 token 的 query 参数），使两条"宿主自取"路径也能取图——这是唯一能保住代理收益的方案；
   - 保底：文档中明确"Emby 侧图片选择器预览/手动下载可能失败"，把自动刷新与识别缩略图（路径 A/B）作为主路径。

### 8.4 无 Emby 服务器时也能做的跨版本防护（建议纳入 CI）

生态里给出的两个可复制做法【生态取证】：

1. **所有基于 SDK 的新接口成员都编译进全部构建**（旧服务器上多实现一个成员无害），避免"为 A 版本编译、在 B 版本 TypeLoadException"（xtream ADR-017 的结论）。
2. **`sdk-load-check` 门禁**：不需要真实 Emby Server，把插件 DLL 对着 SDK 目录加载（枚举全部类型 + JIT 全部方法），xtream 实测覆盖 209 types / 1241 methods / 0 failures。本项目可以把它变成 `scripts/sdk-load-check`（或 CI 里一个 `dotnet` 小程序），在每次发版前跑：
   - 断言 Emby 适配器程序集能在"目标 SDK 版本集合"（建议 4.8.11 / 4.9.1.90 / 4.10.x）下完成类型加载与方法 JIT；
   - 断言 **Emby 适配器的输出目录里不含 `MediaBrowser.*`，也不含 `System.Text.Json`/`System.Memory`/`Microsoft.Bcl.AsyncInterfaces` 等宿主应提供的程序集**（防依赖漂移）；
   - 断言 **Jellyfin 适配器的输出目录为 31 个 dll（基线 30 + `Amane.Core.dll`）**（防部署语义变化）。

---

## 9. License / 分发风险

| 事项 | 事实 | 建议 |
|---|---|---|
| Emby 插件 API 程序集授权 | `MediaBrowser.Server.Core` / `MediaBrowser.Common` 的 nuspec **无 license 字段**，仅 `copyright © Emby 2023`、`requireLicenseAcceptance=false`【实测】 | **不要把 `MediaBrowser.*.dll` 打进插件 zip**；`PackageReference` 一律 `PrivateAssets="all"` 且不拷贝到输出（`ExcludeAssets="runtime"` 亦可）。这与所有存活插件的实际做法一致【生态取证】 |
| Emby Server 本体授权 | `MediaBrowser/Emby` GitHub 仓库仍公开、GPL-2.0、最后推送 2024-03；当前 4.x 实现闭源分发 | 不要以 GPL 推断插件 API 授权；调试只能靠日志 + 反编译 |
| Emby 服务条款（ToS） | 现行 ToS（2026-08-16 版）许可范围为 **personal / non-commercial**，且 §3(d)(4) **禁止衍生作品**【生态取证·需法务确认】 | 我们的插件是 MIT 独立作品、不分发 Emby 任何二进制、只与该软件的公开插件接口交互；**但仍建议在 README 明确"非官方插件"**。工程侧无法定论的部分已列入 §12 |
| 开发者协议 / NDA | **未发现**任何书面 NDA 或开发者协议文本（仅 catalog 申请流程需要 developer id） | 不阻塞独立开发与侧载分发 |
| 第三方独立开发/发布 | 官方文档提供手动安装、模板、VS 调试全套流程【官方文档】 | 允许；不强制进官方 Catalog |
| 进官方 Catalog 的额外约束 | 官方 Development Policy：不得违反美国法律、不得未经许可 web scraping、不得违反被分发库/Emby 的许可，违者无通知下架【官方文档】。申请需向 Emby 申请 developer id，上传物是单 DLL，catalog 版本号必须与 DLL AssemblyInfo 严格一致，ABI 门控靠版本级 `Required Version`【生态取证】 | 本插件只访问用户自建的本地 Amane 服务，不直接抓取第三方站点；但**番号/成人元数据类插件大概率过不了官方 catalog 审核**（政策明确禁 scraping）。**短期建议只做 GitHub Release 分发，不申请 Catalog**；手动侧载不受该 policy 约束 |
| 官方 SDK 示例代码 | "There are no licenses attached to the code and developers are free to decide how to make use of the provided code examples."【官方文档】 | 可参考，但本项目代码自有（MIT），不复制模板代码即可 |
| 两个宿主同名程序集 | `MediaBrowser.Controller.dll` / `MediaBrowser.Model.dll` 两侧同名【实测】 | 仓库只允许每个适配器引用一侧；打包时严格按 zip 白名单（只有自己的 dll） |
| 本项目 LICENSE（MIT） | 现有 `LICENSE` 为 MIT | 新增的 Core/Emby 代码沿用 MIT；若 Core 要被 Emby（闭源宿主）加载，MIT 无冲突 |

---

## 10. 分阶段实施路线

### Phase 0：技术验证（PoC，必须先做）

**目标：证明"能跑"，而不是"能编译"**。编译验证本轮已完成（Emby 最小插件在 net8.0 与 netstandard2.0 下均编译通过；3 工程 Core+双适配器结构、4 种 TFM 共存、Core 依赖表洁净 —— 见 `docs/research/`），因此 Phase 0 的重点是**真机运行时验证**。

PoC 必须实现的最小能力（按优先级）：
1. **插件能被 Emby 4.10 加载**：Dashboard → Plugins 出现 "Amane"，名称/版本正确（验证 `BasePlugin<T>` 与插件目录）。
2. **影片元数据能写入**：配置写死 ServerUrl/Token → 媒体库放 1 个影片 → 扫描 → 断言 `Name=番号 标题`、`Overview`、`PremiereDate`、`ProductionYear`、`Studios`、`Genres`、`RunTimeTicks`、`CommunityRating`、`ProviderIds["Amane"]/["AmaneId"]` 全部落库（验证 `IRemoteMetadataProvider<Movie, MovieInfo>` + DI + 日志 + HTTP 通路）。
3. **识别弹窗能搜到**：Emby Web 里对该影片执行 Identify，输入番号 → 返回候选且缩略图不破图（验证 `IRemoteSearchProvider` + `ImageUrl` 直出语义）。
4. **配置能保存并读回**：ServerUrl/Token 落盘 → 重启 → 读回（验证配置基类与页面机制；若 `IHasWebPages` 不可用，直接切 `BasePluginSimpleUI` 验证声明式 UI）。
5. **图片链路实测**：分别验证路径 A/B（插件 `GetImageResponse`，可带 Bearer）与路径 C/D（Emby 自取，401 预期）的真实表现，据此确定 Emby 侧 `RemoteImageInfo.Url` 策略。
6. **打包洁净度验证（离线即可做，成本极低）**：
   - Emby 适配器输出目录**只应含自己的 dll + `Amane.Core.dll`**，不得含 `MediaBrowser.*`（用 `Reference`+`Private=false` 或 `PackageReference`+`ExcludeAssets=runtime` 控制），也不得出现额外 BCL 垫片（net8.0 方案实测为 14 个程序集，含垫片即说明 TFM/引用策略出错）；
   - Jellyfin 侧 `bin/Release/net9.0/` 应为 **31 个 dll**（基线 30 + `Amane.Core.dll`），zip 必须同时包含两个插件 dll。
7. **编译期验证已完成，不必重做**【实测】：Emby 最小插件在 `net8.0` 与 `netstandard2.0` 两个 TFM 下均一次编译通过（0 warning / 0 error），源码与 csproj 见 `docs/research/emby-provider-api.md`。

**不通过判据**：若 2 或 4 无法在真机跑通，则说明 Emby 侧的宿主差异超出"适配器可消化"的范围，需要回到"是否值得做"的产品判断（而不是继续投入）。若 6 发现 Emby 插件目录混入了 `MediaBrowser.*` 或额外垫片程序集，则先在 csproj 上用 `Private=false`/`ExcludeAssets=runtime` 收紧；仍不干净则说明引用方式选错（对照 §6.3 的实测组合）。

### Phase 1：最小可用 Emby 插件（v0.1）

- `Amane.Core` 拆分（含 2 个 seam + Core 测试迁移）。
- **同步完成 Jellyfin 侧回归**：zip 打包含 `Amane.Core.dll`；在一次真实升级路径上验证（旧版本已装 → 换成新 zip → 插件正常加载 + 能刮削）。**这一步不能推迟到 Phase 3**，否则中间任何一次 Jellyfin 发版都可能把用户弄坏。
- `Amane.Emby`：插件入口 + 配置 + 影片 Provider（`GetMetadata`/`GetSearchResults`）+ External ID（影片）。
- 发布通道打通：`release-emby.yml` + README Emby 安装说明 + 手动 zip 装机验证。

### Phase 2：功能对齐

- 图片 Provider（`GetImages` 带 `LibraryOptions`、`GetImageResponse` → `HttpResponseInfo`）。
- 人物 Provider（`PersonType` 映射、头像 `PersonInfo.ImageUrl`、Person External ID）。
- 手动识别/External ID 的 UI 行为确认与修补。
- 双轨制图片 URL 在 Emby 的实测与修正（直出 vs 代理）。
- 演员缓存清空/连接测试的 Emby 实现（声明式 UI 钩子或 `IService` 端点）。

### Phase 3：发布与维护

- README 平台矩阵/限制、`docs/` 补充 Emby 排障。
- 版本策略与双 tag 流程落地；`ci.yml` 守卫 Core 无宿主引用 + Jellyfin 打包冒烟。
- 明确 Emby 支持版本策略（建议：4.9+ netcore；4.8/Mono 需先解决垫片分发风险），并写进 README。
- （可选）评估是否申请 Emby 官方 Catalog；若将来要支持 Emby 4.8/Mono，必须先在真机上验证"多带 8 个垫片程序集"的分发方案是否可行（§6.3 已给出风险）+ 更新 README 支持范围。

### 与用户原始阶段划分的差异说明

用户建议的 Phase 1 含"测试连接"，我把它并入 Phase 0 的验证项 4/5：因为**配置页机制是 Emby 侧最大的未知项**，放在 PoC 里一起验证成本最低；如果等到 Phase 1 才发现 `IHasWebPages` 不渲染，Phase 1 的范围要重排。另外新增了"图片链路"到 Phase 2 的重点，因为它是唯一**已被真实 issue 证明会坏**的接口族。

---

## 11. 预计需要修改 / 新增的主要文件

### 新增

| 文件 | 作用 | 预估规模 |
|---|---|---|
| `Amane.sln` | 3 工程 + 2 测试工程 | 小 |
| `src/Amane.Core/Amane.Core.csproj` | `net8.0`，零宿主引用（只依赖 `Microsoft.Extensions.Logging.Abstractions`；实测程序集引用表里无任何 `MediaBrowser.*`） | 小 |
| `src/Amane.Core/AmaneSettings.cs` | 配置 POCO + 读取接口（seam 1） | ~40 行 |
| `src/Amane.Core/IHttpClientProvider.cs` | HttpClient 获取抽象（seam 2） | ~15 行 |
| `src/Amane.Core/AmaneIds.cs` | `"Amane"`/`"AmaneId"` 常量 + ID 解析（从适配器命名空间上移） | ~40 行 |
| `src/Amane.Core/AmaneMetadataView.cs` | 归一化映射规则（展示名/日期/评分/流派/时长） | ~80 行 |
| `src/Amane.Emby/Amane.Emby.csproj` | `net8.0` + `MediaBrowser.Server.Core`（`PrivateAssets=all`，不开 `CopyLocalLockFileAssemblies`） | 小 |
| `src/Amane.Emby/AmaneEmbyPlugin.cs` | 插件入口 | ~60 行 |
| `src/Amane.Emby/Configuration/*` | Emby 配置类（+ 配置页或声明式选项类） | ~60–150 行 |
| `src/Amane.Emby/Providers/EmbyMovieProvider.cs` | 影片 Provider + 映射 | ~220 行（对照 Jellyfin 版） |
| `src/Amane.Emby/Providers/EmbyPersonProvider.cs` | 人物 Provider | ~140 行 |
| `src/Amane.Emby/Providers/EmbyImageProvider.cs` | 图片 Provider + `HttpResponseInfo` 包装 | ~120 行 |
| `src/Amane.Emby/Providers/EmbyMovieExternalId.cs` / `EmbyPersonExternalId.cs` | External ID（Emby 成员形状） | 各 ~20 行 |
| `src/Amane.Emby/EmbyHttpClientProvider.cs` / `EmbyLoggerBridge.cs` | 宿主服务适配 | 各 ~30–60 行 |
| `src/Amane.Emby/EmbyDiagnostics.cs`（或选项保存钩子） | 测试连接/清缓存 | ~60 行 |
| `tests/Amane.Core.Tests/*` | 迁 5 类测试（契约/缓存/弹性/URL/ID 解析） | 复用现有代码 |
| `.github/workflows/release-emby.yml`、`ci.yml` | 发布与 CI | 中 |
| `scripts/sdk-load-check/` | 跨版本绑定门禁：把 Emby 适配器 DLL 对目标 SDK 目录做全类型加载 + 全方法 JIT；同时断言输出目录不含宿主程序集（§8.4） | 中 |
| `docs/research/*.md` | 本轮证据笔记 | 已产出 |

### 修改

| 文件 | 改动 | 风险 |
|---|---|---|
| 根 `Jellyfin.Plugin.Amane.csproj` → `src/Amane.Jellyfin/Amane.Jellyfin.csproj` | 位置迁移 + `AssemblyName`/`RootNamespace` 固定 + `ProjectReference Amane.Core` + 删除 `Compile Remove="tests/**"`（不再需要） | **中**（必须验证 dll 名与 zip 结构） |
| `Plugin.cs` / `ServiceRegistrator.cs` / `Providers/*` / `Api/*` / `Configuration/*` | 移入 `src/Amane.Jellyfin/`，接上 Core seam，映射改用 Core 视图 | 中 |
| `.github/workflows/release.yml` | 改构建/打包路径（`src/Amane.Jellyfin/bin/Release/net9.0`）+ **zip 内容从 1 个 dll 改为 2 个 dll（含 `Amane.Core.dll`）**，其余语义与产物名不变 | **高**（发版链路，唯一影响现有用户的改动点） |
| `scripts/build-release.sh` | 同步路径 | 低 |
| `tests/Jellyfin.Plugin.Amane.Tests/*` | 拆分：契约/缓存/弹性/URL/ID 测试移 Core；实体映射与 ExternalId 契约留下 | 低 |
| `manifest.json` | **不改** | — |
| `README.md` | 增加平台矩阵/Emby 安装与限制 | 低 |
| `AGENTS.md` | 更新目录结构、构建命令、新约定（Core 禁宿主引用、DTO 改动需同步两侧映射测试、双 tag 发布） | 低 |

---

## 12. 尚未确认、需要实际 Emby Server 验证的问题

| # | 问题 | 为什么现在无法确认 | 验证方式 |
|---|---|---|---|
| V1 | Emby 是否真的没有任何"第三方插件目录 URL"机制 | 官方文档只写 manual install；5 个候选 catalog URL 全部 404；Emby 管理员公开答复也是"手动放 dll"；未反编译服务器 | 真机 Dashboard→Plugins→Catalog 界面核查；必要时反编译 `Emby.Server.Implementations` |
| V2 | `IHasWebPages` 在 Emby 4.10 是否仍被渲染（本项目配置页依赖它） | 接口在 4.10 程序集里存在且签名相同，但官方已推 `BasePluginSimpleUI`，渲染路径未验证 | 真机打开插件配置页；失败则改用 `BasePluginSimpleUI<TOptions>` |
| V3 | Emby 容器是否注册 `Microsoft.Extensions.Logging.ILogger<T>` | 程序集存在但注册情况不可见 | 真机构造注入 `ILogger<T>`；失败则用 `ILogManager` 桥接 |
| V4 | Emby 插件自定义服务的正确写法（`IService` + `[Route]` + `IReturn<T>` 在 4.10 的具体形态） | 需要真机 + 反编译 `MediaBrowser.Model.Services` | 写一个最小端点实测；或改用声明式 UI 钩子绕过 |
| V5 | 识别（Identify）对话框在 Emby Web 中的实际行为：候选列表渲染、`ProviderIds` 回填链路 | UI 行为无法静态确认（已确认的是：Emby 不拼 "Id" 后缀、`SearchProviderName` 被 `provider.Name` 覆盖） | 真机执行 Identify + 手动填 External ID |
| V6 | 图片路径 **C/D** 的真实失败表现（预览 `/Images/Remote`、手动下载 `POST /Items/{Id}/RemoteImages/Download`） | 反编译已确认这两条由 Emby 自己发 HTTP、插件无法注入 `Authorization`，但 401 后的 UI 表现（报错/静默失败/回退）未验证 | 真机点开图片选择器预览 + 手动下载，看服务器日志与 UI |
| V6b | 图片路径 **A/B** 是否稳定命中插件 `GetImageResponse`（含 Bearer 是否真的生效） | 反编译结论需要在运行时确认 | 真机自动刷新图片 + Identify 缩略图，抓日志确认走了插件 |
| V6c | `PersonInfo.ImageUrl` 的抓取时机与鉴权（Emby 何时把它落成 Person 的 Primary 图） | 反编译只能看到落库路径（`SqliteItemRepository.GetPersonId` → `CreateItemByNameId<Person>(…, imageUrl, …)`） | 真机看演员头像是否落盘、是否带 token 需求 |
| V7 | Emby 4.9+/4.10 人物头像自动下载是否可靠（ThePornDB #123 人物页空白仍 open） | issue 未关，成因不明 | 真机人物页验证；必要时改为 Skeleton/独立 Person 图片 Provider |
| V8 | Mono/netframework 资产的实际占比与插件加载行为（是否值得为它做 netstandard 通道） | 只有资产清单，没有用户分布数据 | 收集用户反馈/统计下载量；真机 Mono 安装验证 |
| V9 | Emby 4.9.x 的运行时是否也是 net8.0（决定 `net8.0` 目标能否覆盖 4.9） | 只实测了 4.10.0.40；4.9 的 runtimeconfig 未取 | 下载 `embyserver-netcore_4.9.5.0.zip` 看 `EmbyServer.runtimeconfig.json` |
| V10 | Emby 对插件的异常处理与启动影响（插件抛异常是否影响服务器启动/刮削队列） | 宿主实现闭源 | 真机植入故障插件观察 |
| V11 | Emby 侧 `ProviderIds` 双键（`Amane` + `AmaneId`）是否都能在 UI/API 中读写与展示 | `ProviderIdDictionary` 是自由字典，但 UI 只列 `ExternalIdInfo` | 真机编辑元数据 + 查 REST API 返回的 `ProviderIds` |
| V12 | Emby 版本升级时本插件的具体断裂点（对照 4.8→4.9 的 `LibraryOptions`） | 只能靠历史先例推断（已有两起真实事故：`LibraryOptions` 形参、`ILiveStream` 成员） | 建立"每次 Emby 新版发布后跑一次冒烟"的流程 + §8.4 的 `sdk-load-check` |
| V13 | ~~netstandard2.0 路线下 `System.Text.Json` 是否会被拷进输出目录~~ | **已实测（结论已用于 TFM 决策）**：Core 与 Emby 同为 netstandard2.0 时输出 22 文件 / 18 程序集（含 STJ、Bcl.AsyncInterfaces、System.Memory 等 8 个垫片），net8.0 组合为 14 文件 / 10 程序集且为 0 → 因此选择 net8.0 | 已完成，见 §6.3 矩阵 |
| V14 | **Jellyfin 侧 bin 输出与 zip 内容在拆分后是否符合预期**（基线：bin 30 个 dll；zip 1 个 dll → 拆分后 bin 应为 31 个，zip 应变成 2 个 dll） | 需要真实构建 + 真实装包验证 | `dotnet build -c Release` 后 diff 文件清单；把一个 zip 装进真实 Jellyfin 插件目录，确认插件能加载并刮削（这是**唯一会影响现有用户的改动点**） |
| V15 | Emby 4.8（.NET 6）与 Mono 资产是否值得支持 | 本机无 Emby 4.8/Mono 安装；且已实测：要支持就得接受 8 个垫片程序集的分发风险（§6.3） | 先按"不支持"声明；若用户明确需要，再在真机 4.8.11 + Mono 上验证垫片方案 |
| V16 | Emby catalog 的线上 JSON 端点与字段名、`IPluginConfigurationPage` 的实际注册方式、Emby 前端可用的 JS 全局对象 | 后台需 developer 账号；SDK 里该接口没有落地示例；官方明说前端 API 从未文档化且频繁变更 | 申请 developer id 后核查；配置页优先走声明式 UI 以规避 |
| V17 | Emby 插件目录是否**必须**同时放 `Amane.Emby.dll` 与 `Amane.Core.dll`（两个独立程序集） | 需要真机确认加载上下文能否解析同目录的依赖程序集 | 真机把两个 dll 一起放进 `plugins/`，确认插件加载成功；若 Emby 只加载单文件，则需 ILRepack 合并（生态里有先例） |
| V18 | Emby 4.8 的运行时是否确为 .NET 6（影响"是否要支持 4.8"这一产品决策） | 本机未取 4.8 的 `runtimeconfig.json`；当前仅"4.8.11.0 于 2025-03-10 发布"是硬证据，".NET 6"来自生态取证 | 下载 `embyserver-netcore_4.8.11.0.zip` 看 `EmbyServer.runtimeconfig.json` |

---

## 13. 调查来源

### 本仓库（代码）
- `Jellyfin.Plugin.Amane.csproj`、`Plugin.cs`、`ServiceRegistrator.cs`、`AmaneClient.cs`、`AmaneModels.cs`
- `Providers/*.cs`、`Configuration/PluginConfiguration.cs`、`Configuration/configPage.html`、`Api/AmaneDiagnosticsController.cs`
- `manifest.json`、`meta.json`、`.github/workflows/release.yml`、`scripts/build-release.sh`、`tests/**`、`AGENTS.md`、`README.md`
- 远端分支 `origin/feat/jellyfin-12`（Jellyfin 12.1.0 / net10.0 适配）

### Emby 官方文档 / 官方仓库
- 插件总览与手动安装：<https://emby.media/support/articles/Plugins.html>
- 插件开发（运行时、模板、调试、`IServerEntryPoint`）：<https://dev.emby.media/doc/plugins/dev/index.html>
- 开发政策（Catalog 合规要求）：<https://dev.emby.media/doc/plugins/dev/Development-Policy.html>
- 插件 UI（声明式 UI 与旧 HTML 方式的取舍）：<https://dev.emby.media/doc/plugins/ui/index.html>
- Emby SDK（模板与示例，含"No licenses attached"原文）：<https://dev.emby.media/home/sdk/index.html>、<https://dev.emby.media/home/sdk/plugins/index.html>
- 服务器源码仓库状态（GPL-2.0 / 2024-03 最后推送）：<https://github.com/MediaBrowser/Emby>
- 版本与资产清单（含 mono/netframework 资产）：<https://github.com/MediaBrowser/Emby.Releases/releases>

### NuGet 官方包（本次实际下载/解包）
- `MediaBrowser.Server.Core` 4.9.1.90 / 4.10.0.24-beta2：<https://www.nuget.org/packages/MediaBrowser.Server.Core>
- `MediaBrowser.Common` 4.9.1.90 / 4.10.0.24-beta2：<https://www.nuget.org/packages/MediaBrowser.Common>
- flatcontainer 版本索引：<https://api.nuget.org/v3-flatcontainer/mediabrowser.server.core/index.json>、<https://api.nuget.org/v3-flatcontainer/mediabrowser.common/index.json>
- 实测：`MediaBrowser.Model` 作为独立包不存在（404）

### 真实二进制（本次实测产物）
- 官方 `embyserver-netcore_4.10.0.40.zip`（219 MB）解包：`system/EmbyServer.runtimeconfig.json`（`tfm: net8.0`、`Microsoft.NETCore.App` + `Microsoft.AspNetCore.App` 8.0）、`system/` 78 个 dll 清点
- 反射 dump（本机 Emby 4.10.0.40 全部公开签名，920 KB）——证据笔记见 `docs/research/emby-provider-api.md`
- Jellyfin SDK：`~/.nuget/packages/jellyfin.controller/10.11.10/lib/net9.0/MediaBrowser.Controller.dll`、`jellyfin.model/10.11.10/lib/net9.0/MediaBrowser.Model.dll`

### 存活第三方插件（生态取证，详见 `docs/research/emby-ecosystem.md`）
- `jellyfin-adult/Jellyfin.Plugin.Pronium`（同仓双平台，`__EMBY__` + 双 TFM + 双 Configuration）
- `ThePornDatabase/Jellyfin.Plugin.ThePornDB`（同款架构，583 stars；open issue #123「人物页空白」）
- `Whereis-Alice/emby-plugin-bangumi`（Emby-only，`net8.0`，三级 DLL 回退；Emby 4.9.x–4.10.x 声明）
- `honue/MediaInfoKeeper`（Emby-only，活跃；`net8.0;net6.0`；版本支持声明）
- `JavScraper/Emby.Plugins.JavScraper`（历史事实标准，已停更；兼容层被时间证伪的案例）
- Emby 官方 Org：`MediaBrowser/NfoMetadata`、`MediaBrowser/Emby.Plugins.Anime`（`netstandard2.0`）
- Emby 官方上下文的官方插件：Trakt、AutoOrganize（均为 `netstandard2.0`，`BasePlugin<T> + IHasWebPages`）

### 其它关键来源
- Emby 服务条款（personal/non-commercial、禁衍生作品）：<https://emby.media/terms.html>
- Emby 官方插件后台（需 developer 账号）：<https://plugins.emby.tv/admin>
- 依赖漂移事故：Emby LDAP 插件在 4.8.10(.NET 6.0.31) 报 `Could not load file or assembly 'Microsoft.Bcl.AsyncInterfaces, Version=8.0.0.0'`【生态取证，详见 `docs/research/emby-framework.md`】
- 接口成员新增事故与 `sdk-load-check` 手法：emby-xtream ADR-017（`ILiveStream` 成员致 `TypeLoadException`）【生态取证，详见 `docs/research/emby-framework.md`】

### 本报告的证据笔记（原始取证，未加工）
- `docs/research/emby-framework.md` —— Emby 插件框架/SDK/打包/授权层取证
- `docs/research/emby-provider-api.md` —— Emby Provider API 反射实测 + 最小编译 PoC
- `docs/research/emby-ecosystem.md` —— 存活第三方插件逐一取证（含安装方式原文、issue 证据、zip 解包结果）
- `docs/research/core-split-poc.md` —— Core/Adapter 三工程拆分编译验证（TFM 共存、seam 清单、类型冲突结论）
- `docs/research/verification-report.md` —— 交付文档的**独立核对报告**（核对对象是本文件的一个中间修订版：13 章完整性、22 条【代码】断言行号、多项【实测】断言复跑、一致性、仓库洁净度；**报告中的 10 条修正项已全部落入本最终版本**）。同目录 `verification-build.log` / `verification-test.log` 为核对时的构建与测试原始输出。

---

## 附：本次调查对仓库的改动范围声明

- 新增：`docs/EMBY_COMPATIBILITY_RESEARCH.md`（本文件）、`docs/research/`（4 份证据笔记 + 1 份独立核对报告 + 2 份核对构建/测试日志）
- 修改：**无**。现有插件源码、`manifest.json`、`.github/workflows/release.yml` 均未改动。
- 仓库外实验（隔离，未进入版本库）：`%TEMP%\amane-emby-poc`（Emby 最小插件 PoC）、`%TEMP%\amane-emby-poc-tools`（.NET SDK 与真实 Emby Server 解包）、`%TEMP%\amane-core-poc`（三工程 Core 拆分 PoC）、`%TEMP%\amane-emby-nuget`（NuGet 包解包）。
- 基线验证：`dotnet build -c Release` = 0 warning / 0 error；`dotnet test` = 56 passed / 0 failed。
