# 交付文档独立核对报告（doc-verifier / task-5）

- 被核对对象：`docs/EMBY_COMPATIBILITY_RESEARCH.md`
- **核对版本基线**：108,915 bytes / 982 行 / mtime `2026-09-29 16:11:30` / SHA256 `A0DEBB9F0C10B0DB3ACDBE5DA39A3C9890F320CEE10C1626CD80B8AD50078EE2`
- ⚠️ **核对期间文档被 Lead 改过一次**（100,198 B / 962 行 → 108,915 B / 982 行：TFM 结论由「netstandard2.0 优先」翻转为「net8.0」，dll 基线由 35 改为 30，新增 PoC 改造量与 TFM 矩阵）。本报告全部结论针对上述最终 revision；每次引用的行号均为该 revision 的行号。核对结束前再次确认该文件未被再次改动（哈希一致）。
- 核对方式：只读文档 + 实跑构建/测试 + 本地二进制/包反射 + 网络复核外部事实。**未修改被核对文档**，未修改任何插件代码 / `manifest.json` / CI 配置。
- 工具链：`dotnet 10.0.401`（`DOTNET_ROOT=C:\Users\rappa\.dotnet`）。

---

## 0. 三态汇总

| # | 维度 | 结论 | 一句话 |
|---|---|---|---|
| 1 | 结构完整性（13 章） | **通过** | 13 章齐全，另有证据等级标记、§5.6、附录 |
| 2 | 【代码】断言抽样核对（22 条） | **通过** | 22 条全部与真实文件/行号一致，含 8 处 `Plugin.Instance` 行号逐一命中 |
| 3 | 【实测】断言抽样复核 | **通过（有 1 项单位错误）** | 构建/测试/dll 数/nuspec/404/runtimeconfig/编译 PoC/版本 diff/反编译行号 全部复现；4 项无法核对 |
| 4 | 内部一致性（残留矛盾） | **不通过** | TFM 翻转后残留 5 处矛盾 + 编号/字段/口径小错 7 处 |
| 5 | 证据可追溯性 | **不通过** | `docs/research/*.md` 路径全部悬空（笔记实际在 `.research-scratch/`）；笔记内 1 个 ADR URL 已 404 |
| 6 | 仓库洁净度 | **通过** | `git diff` 空、工作区仅 `docs/` 与 `.research-scratch/` 两个未跟踪目录 |

---

## 1. 结构完整性 —— **通过**

用户要求的 13 个章节全部存在且非空：

| 要求 | 文档标题 | 行号 |
|---|---|---|
| Executive Summary | `## 1. Executive Summary` | 22 |
| 当前 Jellyfin 插件架构 | `## 2. 当前 Jellyfin 插件架构（以实际代码为准）` | 54 |
| Emby 插件体系调查 | `## 3. Emby 插件体系调查` | 173 |
| API 对照表 | `## 4. Jellyfin ↔ Emby API 对照` | 466 |
| 可共享代码分析 | `## 5. 可共享代码分析（按模块，不用百分比糊弄）` | 500 |
| 推荐仓库架构 | `## 6. 推荐仓库 / Solution 架构` | 573 |
| 构建与发布 | `## 7. 构建与发布方案` | 674 |
| 兼容性风险 | `## 8. 兼容性与技术风险` | 745 |
| License 与分发风险 | `## 9. License / 分发风险` | 796 |
| 分阶段路线 | `## 10. 分阶段实施路线` | 812 |
| 需修改新增文件 | `## 11. 预计需要修改 / 新增的主要文件` | 859 |
| 需实机验证 | `## 12. 尚未确认、需要实际 Emby Server 验证的问题` | 900 |
| 调查来源 | `## 13. 调查来源` | 926 |

证据：`Select-String -Pattern '^## '` 输出（另有 L8 证据等级标记、L977 附录）。

---

## 2. 【代码】类断言抽样核对（22 条） —— **通过**

### 2.1 关键行号（task-5 点名项）

| 文档断言 | 实测 | 结论 |
|---|---|---|
| `AmaneClient.cs` 8 处 `Plugin.Instance` 行号 = 94/109/124/385/405/439/474/558 | `grep` 命中：94、109、124、385、405、439、474、558（**8 处，逐一命中，零偏差**） | 通过 |
| `AmaneClient.cs:187,198,313` = `Providers.AmaneMovieProvider.ProviderIdName` / `InternalIdProviderIdName` | L187 `InternalIdProviderIdName`、L198 `ProviderIdName`、L313 `ProviderIdName`（3 个引用点 / 2 个常量） | 通过 |
| `AmaneClient.cs:181-217` = `ResolveMetadataAsync` 三级解析 | L181-217 正是 `ResolveMetadataAsync`（AmaneId 直取 L187-195 → 识别框值 L198-211 → 名称兜底 L213-216） | 通过 |
| `AmaneClient.cs:425-464` = `GetImageAsync` | L425 `public async Task<HttpResponseMessage> GetImageAsync`，L464 方法结束；UA L436、Bearer 域内判定 L442、非 2xx 抛异常 L448-453 | 通过 |
| `AmaneMovieProvider.cs:108-165` = 搜索+结果组装 | L108 `GetSearchResults`，L139-165 `ToSearchResult`（双键 L156/L161） | 通过 |
| `AmaneMovieProvider.cs:60-105` = `GetMetadata` | L60-105 完全吻合（`MapToMovie` L75、`PersonKind.Actor` L85、演员 id 写入 L93） | 通过 |
| `AmaneMovieProvider.cs:178-226` = `MapToMovie` | L178-226 完全吻合（Genres=Tags L200、score×2 L211、双键 L217/L222） | 通过 |
| `AmaneImageProvider.cs:50-92` = `GetImages` URL 双轨 | L50-92 完全吻合（`Url=ToProxyImageUrl` L67/L80、`ThumbnailUrl=ToDirectImageUrl` L68/L81、poster→Primary L69、thumb+extrafanart→Backdrop L73-83） | 通过 |
| `Plugin.cs` GUID `9f2e4a6b-…` + `BasePlugin<PluginConfiguration>`+`IHasWebPages` | `Plugin.cs:14/19/26/38/44` 全部吻合，文件 55 行 | 通过 |
| csproj `net9.0` / `Jellyfin.* 10.11.10` / `PrivateAssets` | `Jellyfin.Plugin.Amane.csproj:4` = `net9.0`；`:22-27` = 两个 `PackageReference` + `PrivateAssets=all`；`:21` = `FrameworkReference`；`:6-17` = `CopyLocalLockFileAssemblies`/`ImplicitUsings`/`Nullable`/`EmbeddedResource`/`InternalsVisibleTo`/`Compile Remove` | 通过 |
| `release.yml` 的 `manifest[0]` 假设 | `.github/workflows/release.yml:51-59` = `manifest[0]['versions']` 过滤+插入；`:29-30` = `cd bin/Release/net9.0` + `zip -j "$DLL_NAME"` | 通过 |
| `scripts/build-release.sh:17` 单文件打包 | L17 = `(cd bin/Release/net9.0 && zip -j -X … Jellyfin.Plugin.Amane.dll)` | 通过 |
| README:24 订阅 URL | README 第 24 行 = `https://raw.githubusercontent.com/rappa850/Jellyfin.Plugin.Amane/main/manifest.json` | 通过 |
| tests csproj:5-14 = `net10.0` + xunit 2.9.3 | `:5`=`<TargetFramework>net10.0`，`:13`=`xunit 2.9.3` | 通过 |

### 2.2 文件行数与清单

文档行数表（§5.1）与我实测**逐项一致**：`AmaneClient.cs` 668 ✓、`AmaneModels.cs` 206 ✓、`AmaneMovieProvider.cs` 227 ✓、`AmanePersonProvider.cs` 133 ✓、`AmaneImageProvider.cs` 93 ✓、两个 ExternalId 25+25=50 ✓、`Plugin.cs` 55 ✓、`ServiceRegistrator.cs` 17 ✓、`PluginConfiguration.cs` 34 ✓、`configPage.html` 139 ✓、`AmaneDiagnosticsController.cs` 48 ✓。

其他：`configPage.html` 含 `ApiClient.getPluginConfiguration`/`updatePluginConfiguration`/`getJSON(ApiClient.getUrl('Amane/Health'))`/`Amane/ClearCache` ✓；控制器 `[ApiController] [Authorize] [Route("Amane")]` + `GET Health`/`POST ClearCache` ✓；`manifest.json` 单条目、GUID 与 `Plugin.cs` 一致、`targetAbi=10.11.10.0` ✓;`meta.json`/`thumb.png`/`LICENSE`(MIT) 存在 ✓;`origin/feat/jellyfin-12` 远端分支存在 ✓；测试目录 8 个测试文件、41 个 `[Fact]/[Theory]`（运行结果 56 例）✓。

**唯一措辞瑕疵**：§1 L33「2 处 Jellyfin 命名空间常量」实际是 3 个引用点（§5.1 L507 自己写「11 处」= 8+3，反而对）。不影响结论。

---

## 3. 【实测】类断言抽样复核 —— **通过（1 项单位错误）**

### 3.1 仓库侧（全部复现）

| 文档断言 | 我的独立复跑 | 结论 |
|---|---|---|
| `dotnet build -c Release` = 0 warning / 0 error | `已成功生成。0 个警告 0 个错误`（EXITCODE=0） | 通过 |
| `dotnet test tests/Jellyfin.Plugin.Amane.Tests` = 56 passed | `已通过! - 失败: 0，通过: 56，已跳过: 0，总计: 56` | 通过 |
| `bin/Release/net9.0/` = **30 个 dll（32 个文件）** | 实测 30 个 dll / 32 个文件（递归同样 30）；用 `BaseOutputPath` 全新建到临时目录后仍是 30 个且集合完全相同 | 通过（此项在上一 revision 写的是 35，已修正） |
| Core 依赖表无 `MediaBrowser.*` | `Amane.Core.deps.json` 只含 `Amane.Core/1.0.0`、`Microsoft.Extensions.DependencyInjection.Abstractions/8.0.0`、`Microsoft.Extensions.Logging.Abstractions/8.0.0` | 通过 |
| Jellyfin 适配器 bin = 31 个 dll 且无 `System.Text.Json.dll` | 实测 31 个 dll（含 `Amane.Core.dll`），`Select-String System.Text.Json` 零命中 | 通过 |
| Emby 适配器默认 bin = 2 个 dll | 实测 2 个 dll（两种 TFM 都是） | 通过 |
| netstandard2.0 Core 编译 = 12 warning / 0 error | 日志尾部 `12 个警告 / 0 个错误` | 通过 |
| `System.Text.Json 8.0.0` 有 2 条 NU1903 高危通告 | 日志中 2 个不同 advisory：`GHSA-8g4q-xg66-9fp4`、`GHSA-hh2w-p6rv-4g7w` | 通过 |
| Core 拆分改造量：24 处（`AmaneClient.cs` 23 + `AmaneModels.cs` 1）；Jellyfin 侧 8 处 / 5 个文件 | 证据文件 `seam-replacements.txt` 是 **24 条规则**，但替换**点数 25**（`AmaneClient.cs` 24 点 + `AmaneModels.cs` 1 点，因 `S11-provider-id-const` 记为 2）；Jordan 侧 8 点/8 规则/5 文件 ✓ | 数值可通过（口径=规则数），建议注明口径 |
| copy-local「14 个 → 22 个，多 8 个垫片」 | 独立复跑 `-p:CopyLocalLockFileAssemblies=true`：net8 = **10 dll / 14 文件**；ns2.0 = **18 dll / 22 文件**，多出的 8 个垫片与文档列举**完全一致** | 数值通过，**但文档称其为「14/22 个程序集」是单位错误**（见 §4.3） |

### 3.2 外部事实（实地复核）

| 文档断言 | 复核结果 | 结论 |
|---|---|---|
| Emby 有官方 NuGet SDK，最新 `4.10.0.24-beta2`、稳定线 `4.9.1.90` | flatcontainer 实测：稳定线最高 4.9.1.90，最高预发布 4.10.0.24-beta2 | 通过 |
| 两包均 `lib/netstandard2.0`；`MediaBrowser.Model` 独立包 404 | 逐包解包：4.6.0.50、4.7.9、4.8.11、4.9.1.90、4.10.0.24-beta2 **全部只有 `lib/netstandard2.0/`**；`api.nuget.org/v3-flatcontainer/mediabrowser.model/index.json` → **HTTP 404** | 通过 |
| `Server.Core` → `MediaBrowser.Controller.dll`(587 KB)+`Emby.Naming.dll`；`Common` → Model/Common/Emby.Media.Model/Emby.Web.GenericEdit | 解包一致（587,264 B，按十进制 587 KB） | 通过 |
| `.nuspec` 无 `<license>`，仅 projectUrl/copyright/authors/requireLicenseAcceptance=false | 4 个 nuspec 原文核对，**`<license` 零命中**；四项字段逐字一致 | 通过 |
| 累计下载 3,233,744 | azuresearch API `totalDownloads=3233744` | 通过 |
| Emby 4.10.0.40 运行时 = net8.0 | `EmbyServer.runtimeconfig.json`：`tfm: net8.0`、`Microsoft.NETCore.App 8.0.0` + `Microsoft.AspNetCore.App 8.0.0` | 通过 |
| `system/` = 78 个 dll；无 `Microsoft.Extensions.Http`/`System.Text.Json`/`Newtonsoft.Json`；有 SimpleInjector/ServiceStack.Text/`Logging.Abstractions 8.0.1024.46610` | 逐一实测，**全部与文档一致** | 通过 |
| 4.10.0.40 的 96 个 Release 资产中 20 个 mono/netframework | GitHub API：96 assets / 20 mono ✔（另测：4.8.11.0=99/20、4.9.3.0=94/20、4.11.0.4=96/20） | 通过（全量表述可收窄，见 §7.3） |
| `MediaBrowser/Emby` 仓库公开、GPL-2.0、pushed_at 2024-03-27、4975 stars | API：`GPL-2.0`、`2024-03-27T18:20:17Z`、`4975`、`archived: false` | 通过 |
| 「一份一字不改的 .cs 分别对 Emby SDK 与 Jellyfin SDK 编译，双双 0 错 0 警」 | 找到真实产物 `%TEMP%\emby-fw-probe\unified\Plugin.cs`（含 `BasePlugin<T>`、`SaveConfiguration`、`ConfigurationFilePath`、`IHasWebPages`），我**重新编译** `unified-emby`(nstd2.0+Server.Core 4.9.1.90) 与 `unified-jf`(net9.0+Jellyfin 10.11.10)：**双双 0 warning / 0 error** | 通过 |
| 三个 SDK 版本（4.7.9/4.8.11/4.9.1.90）diff 显示框架层接口零变更 | 用本机 `ctrl./common./model.479/4811/491.txt` 逐块提取：`BasePlugin`、`BasePlugin\`1`、`IPlugin`、`IHasWebPages`、`PluginInfo`、`IPluginConfigurationPage` **6 个声明块三次逐字节相同** | 通过 |
| 同项目同时引用两侧 SDK → build 成功但 Emby 程序集被静默丢弃 | 复跑 `%TEMP%\emby-fw-probe\dual\dual.csproj`：`0 个警告 0 个错误`；`ReferencePath` 中 `MediaBrowser.*` 只剩 Jellyfin 三件，Emby 侧仅 `Emby.Media.Model.dll`/`Emby.Web.GenericEdit.dll` | 通过 |
| 图片路径 A：`ItemImageProvider.cs` 约 :470 | `Emby.Providers.Manager\ItemImageProvider.cs:470` = `provider.GetImageResponse(url, ct)` **精确命中** | 通过 |
| 图片路径 B：`ProviderManager.cs` 约 :1311 | `ProviderManager.cs:1311` = `public Task<HttpResponseInfo> GetSearchImage(...)`，L1327 = `remoteSearchProvider.GetImageResponse(...)` **精确命中** | 通过 |
| 图片路径 C：`RemoteImageService` 用 `_httpClient.GetResponse` | `Emby.Api.Images\RemoteImageService.cs:27/37` 注入 `IHttpClient`，**L145 = `_httpClient.GetResponse(...)`** | 通过 |
| 图片路径 D：`SaveImageFromRemoteUrl` → `_ioManager.GetResponse` | `ProviderManager.cs:168/171/173` = `SaveImageFromRemoteUrl` → `_ioManager.GetResponse(new HttpRequestOptions{...})` | 通过 |
| Emby 不给 `Key` 拼 `Id`（三重证据） | (1) `GetExternalIdInfos` 原文 `Key = i.Key` ✔；(2) `itemidentifier.js` 确有 `data-providerkey` ✔；(3) 895 个反编译 `.cs` 中 `+ "Id"` **仅 1 处命中且是无关的 `attrib + "id"`（小写）** ✔ | 通过 |
| `ProviderManager` 无条件覆盖 `SearchProviderName = provider.Name` | `ProviderManager.cs:1306` = `array2[i].SearchProviderName = provider.Name;` | 通过 |
| `SqliteItemRepository.GetPersonId` → `CreateItemByNameId<Person>(…, imageUrl, …)` | L1607 `GetPersonId(... string imageUrl, ...)` → L1609 `CreateItemByNameId<Person>(db, itemInfo, …, providerIds, null, imageUrl, …)`；L12006 用 `personInfo.ImageUrl` 调用 | 通过 |
| 反射 dump 含全部关键类型、且**不含** `IPluginServiceRegistrator` | 898.7 KiB（=920 KB 十进制）dump 中：`IRemoteMetadataProvider`/`IRemoteSearchProvider`/`IHasOrder`/`RemoteImageInfo`/`PersonInfo`/`ProviderIdDictionary`/`IExternalId`/`BasePlugin<T>`/`IHasWebPages`/`BasePluginSimpleUI`/`HttpResponseInfo`/`IHttpClient`/`IApplicationHost` 全部命中；`IPluginServiceRegistrator` **0 命中** | 通过 |
| §3.4/3.6/3.7/3.8/3.9 引用的签名 | 与 dump 原文逐字核对：`IRemoteMetadataProvider<T,T2>`（含 `IRemoteSearchProvider` 继承链与 4 个必写成员）、`IRemoteImageProvider`（多 `LibraryOptions`、返回 `HttpResponseInfo`）、`RemoteImageInfo` 12 字段、`ImageType` 枚举、`MetadataResult<T>`/`BaseMetadataResult`、`PersonInfo`、`PersonType{Actor=0…Lyricist=7}`、`IExternalId`、`ProviderIdsExtensions`、`HttpResponseInfo`、`IHttpClient`、`IApplicationHost`（含 `manageLiftime` 原样拼写）、`IHasWebPages`、`PluginPageInfo`、`BasePluginSimpleUI<T>` 钩子集 —— **全部一致** | 通过 |
| 官方文档引文（4 条） | 实地抓取：`dev.emby.media/home/sdk/plugins/` "There are no licenses attached…" ✔；`doc/plugins/dev/index.html` "Emby Server runs on two different runtimes: .NET Core 2.0+, and Mono." + "Its constructor can accept any number of injected dependencies" ✔；`doc/plugins/ui/index.html` "Plugins got broken quite too often…" ✔；`Development-Policy.html` 两段政策原文 ✔ | 通过 |
| dev 文档停在 2022、旧路径 404 | 页脚 `Copyright 2022 © EMBY LLC` ✔；`/doc/plugins/Development-Policy.html` 与 `/doc/plugins/Dependency-Injection.html` **HTTP 404**，`/doc/plugins/dev/*` **HTTP 200** ✔ | 通过 |
| ThePornDB #123「Blank actor page after updating to Emby 4.9.3.0」仍 open | GitHub API：title 逐字一致、`state: open`、created 2026-05-18、0 评论 | 通过 |
| Trakt / AutoOrganize 均 netstandard2.0 | `MediaBrowser/trakt/Trakt.csproj` 与 `Emby.AutoOrganize.csproj` 均 `<TargetFrameworks>netstandard2.0;</TargetFrameworks>` | 通过 |
| Emby ToS 2026-08-16、personal/non-commercial、§3(d)(4) 禁衍生作品 | `emby.media/terms.html`："Updated August 16, 2026"、"personal, non-commercial … license"、"(4) develop any improvement, modification, or derivative works of the Software" | 通过 |
| xtream ADR-017 的 `ILiveStream` 事故与 `209 types / 1241 methods / 0 failures` | 本地快照 `017-single-dll-for-emby-4-9-and-4-10.md` 原文命中：`ILiveStream` `AddConsumer/RemoveConsumer`、`| Release … | 209 types, 1241 methods, 0 failures |` | 通过（笔记 URL 已失效，见 §5.3） |

### 3.3 无法核对（4 项）

| 文档断言 | 为什么无法核对 |
|---|---|
| 「官方 SDK 4 个 TFM（netstandard2.0/net8.0/net9.0/net10.0）`dotnet add package` + build 全部 0 错 0 警」 | 未找到该 4-TFM 探针的日志/产物（`%TEMP%\amane-core-poc\nuget-probe` 只有 net8.0）。属**无法核对**，非否定 |
| 「Emby 4.8 运行时是 .NET 6」 | 本地无 4.8 的 `EmbyServer.runtimeconfig.json`，未下载 200+ MB 包；我只能确认 **4.8.11.0 于 2025-03-10 正式发布**。且**文档未给它标注证据等级、也未列入 §12**（见 §7.1） |
| 「Emby 4.9.x 运行时也是 net8.0」 | §12 V9 自己承认未取 4.9 的 runtimeconfig → 与 §3.2 表 / §7.8 README 矩阵的断言冲突（见 §7.2） |
| §3.2「每个正式版…96 个资产」 | 只有 4.10.0.40 恰为 96；4.8.11.0=99、4.9.3.0=94、4.7.x 多在 79–98 之间 |

---

## 4. 内部一致性（残留矛盾） —— **不通过**

### 4.1 【高】§7.7 的打包冒烟断言与 TFM/打包结论直接冲突

- `L724`：`…且 zip 内**只有一个 dll**（防止重构后 zip 结构变化导致用户升级失败）`
- 但 `L76`、`L688`、`L692`、`L826`、`L891` 一律写「**必须含 `Jellyfin.Plugin.Amane.dll` + `Amane.Core.dll`**，只用单 dll 打包会 FileNotFoundException」。
- 影响：这是要落成 CI 断言的句子，照抄会写出一个必然失败的校验。

### 4.2 【高】§6.3 的决策理由与自己的矩阵行自相矛盾

- `L657`：「**netstandard2.0 的两个组合**会把 8 个宿主应提供的程序集带进插件目录」
- 同表 `L652` 行 B（Core `net8.0` + Emby `netstandard2.0`）：`14 个，无多余垫片`。
- 证据侧（`.research-scratch/core-split-poc.md` L811 行 B）也写「copy-local 14 个，**无** Bcl.AsyncInterfaces」→ 只有「双 netstandard2.0」组合才带 8 个垫片。

### 4.3 【中】「程序集」/「文件」单位混用（4 处）

`L49`、`L219`、`L693`、`L825` 都写「14 个程序集 / 22 个程序集」。实测（含我独立复跑）：

```
Amane.EmbyAdapter      (net8.0)          dll=10  files=14
Amane.EmbyAdapter.Ns20 (netstandard2.0) dll=18  files=22
```

即 **14/22 是文件数**（含 2 个 `.deps.json` + 2 个 `.pdb`），**程序集数是 10/18**。8 个垫片的数量与名单无错。

### 4.4 【中】Phase 3 仍保留已被否决的 netstandard2.0 动作

- `L851`：「在真机 Mono 安装上实测 **netstandard2.0 产物**（决定 README 是否声明 Mono 支持）」
- 与 `L220`/`L659`/`L777`「已实测否决 netstandard2.0 路线」冲突。

### 4.5 【中】目录树注释未随 TFM 结论更新

- `L630`：`Amane.Core.Tests/  # 目标 net10.0（Core 是 netstandard2.0，任意新 TFM 均可测）` —— Core 已定为 `net8.0`。
- `L604`/`L621` 已正确写成 net8.0，仅此行残留。

### 4.6 【低】§2.9 残留历史条件句

- `L164`：「注意：**若 Core 选 `netstandard2.0`**，`System.Text.Json` 需要包引用…必须验证其分发行为（§6.3）」——该分支已在 §6.3 实测否决且 §6.3 结论是「net8.0 下不需要引用该包」；留着会让读者以为决策仍未定。

### 4.7 【低】编号 / 字段 / 顺序小错

| 位置 | 问题 |
|---|---|
| `L39` 与 `L43` | Executive Summary **两个「6.」**（第 6 条重复编号，后续 7/8 未顺延） |
| `L552` vs `L559` | `### 5.6` 排在 `### 5.5` 之前 |
| `L921` vs `L922` | §12 表 `V17` 排在 `V16` 之前 |
| `L424` | `PluginPageInfo` 字段写作 `EnableUserMenu`，dump 原文是 **`EnableInUserMenu`** |
| `L320` | `IImageProvider` 只列 `Supports(BaseItem)`，dump 原文还有 `Name { get; }`（同文档 §3.6 L337 又强调「必须写全成员」，易误导实现者漏掉 `Name`） |
| `L484` | `task<MediaBrowser.Common.Net.HttpResponseInfo>` 应为 `Task<…>`（首字母小写） |
| `L132` | 「24 处字面替换」= 规则数；替换**点数 25**（`AmaneClient.cs` 24 + `AmaneModels.cs` 1）。建议写成「24 条替换规则 / 25 个替换点」 |
| `L792` vs `L826` | §8.4 说「Jellyfin 适配器输出目录 dll 集合**与基线一致**」，§10 说「应为 **31 个**（基线 30 + Core）」——基线已变，措辞建议同步为「31 个」 |

---

## 5. 证据可追溯性 —— **不通过**

### 5.1 【高】`docs/research/*.md` 全部不存在（唯一溯源入口悬空）

文档 **11 处**引用 `docs/research/`：`L6`、`L816`、`L827`、`L883`、`L951`、`L954`、`L966`、`L967`、`L970-973`、`L979`。

实测仓库现状：

```
Get-ChildItem docs -Recurse   →  只有 docs\EMBY_COMPATIBILITY_RESEARCH.md
Get-ChildItem .research-scratch -Recurse
  →  core-split-poc.md / emby-ecosystem.md / emby-framework.md / emby-provider-api.md
```

即四份证据笔记**确实存在**，但在 `.research-scratch/`，不在文档声明的 `docs/research/`。影响：

- 正文所有 `详见 docs/research/xxx.md` 的溯源链断开；
- §11「新增 `docs/research/*.md`（本轮证据笔记）已产出」与 §附录「新增：…、`docs/research/*.md`」为不实陈述；
- **建议**：把四份笔记落到 `docs/research/`（推荐，与文档/附录一致），或把全文路径改为实际位置。

### 5.2 【生态取证】断言的可追溯性（按内容看是够的）

正文带【生态取证】但无 inline URL 的断言主要有：`L381`（生态普遍把外源头像写进 `PersonInfo.ImageUrl`）、`L412`（bangumi 双实现共用 `"Bangumi"` 键）、`L436`（Pronium/ThePornDB/MediaInfoKeeper zip 只含自己的 dll）、`L458`（双平台项目 Emby 分支多一个形参）、`L459`（4.8.10 正常 / 4.9.1.90 坏的实证）、`L460`（ThePornDB 主仓转 Jellyfin 12、JavScraper 停更）、`L766`（LDAP 1.0.44 事故）、`L767`（xtream ADR-017）、`L786`/`L788`/`L789`（sdk-load-check）。

逐条抽查后：**这些断言在 `.research-scratch/*.md` 里都有具体 URL**（`emby-ecosystem.md` 含 ThePornDB #123、Trakt/AutoOrganize、Pronium release.yml、MediaInfoKeeper Version.json 等链接；`emby-framework.md` 含 `emby.media/community/topic/133644-ldap-1044-…`、xtream ADR、`emby.media/terms.html`）。**结论：内容可追溯，但因 §5.1 的路径错误而"名义上不可追溯"**。修好路径即可。

### 5.3 【低】证据笔记内 1 个 URL 已 404

`.research-scratch/emby-framework.md` 引用的
`https://github.com/firestaerter3/emby-xtream/blob/main/docs/decisions/017-single-dll-for-emby-49-and-410.md`
→ **HTTP 404**。实际文件名是 **`017-single-dll-for-emby-4-9-and-4-10.md`**（GitHub API `contents/docs/decisions` 已确认）。正文 §8.4 未带 URL，故只影响笔记。

---

## 6. 仓库洁净度 —— **通过**

```
$ git status --porcelain
?? .research-scratch/
?? docs/

$ git diff --stat
(空)
```

- 无任何 tracked 文件被修改 → 正式插件代码、`manifest.json`、`.github/workflows/*`、`scripts/*` 全部未被改动，与附录声明一致；
- 未跟踪项仅 `docs/`（交付文档）与 `.research-scratch/`（证据笔记 + 本报告 + 我产生的 2 个日志）；
- 我的构建/测试产物只落在 `bin/`、`obj/`、`tests/**/bin|obj`（正常，且已在 `.gitignore` 覆盖范围内）。
- 说明：我额外生成了 `.research-scratch/verification-build.log`、`.research-scratch/verification-test.log`（构建与测试原始输出留证）。

---

## 7. "说得比证据满"（过度断言）与措辞建议

### 7.1 【中】「Emby 4.8 运行时是 .NET 6」被当事实使用

出现于 `L50`、`L200`、`L220`、`L659`、`L777`、`L920`，但它**没有任何证据等级标记**，而按 §证据等级标记的规则「未标注等级的推断均已在第 12 节列为需要实机验证」——§12 里并没有这一条（V9 只问 4.9）。
**建议措辞**：「Emby 4.8 运行时为 .NET 6（社区/生态共识，本轮未取 4.8 的 `runtimeconfig`，见 V9 同类验证）」并补一条 V 项。本轮我同样无法核对（只确认 4.8.11.0 于 2025-03-10 发布）。

### 7.2 【中】「覆盖 Emby 4.9.x」与 §12 V9 冲突

`L200`（§3.2 表「覆盖范围」）、`L218`/`L220`、`L734`（README 矩阵「4.9.x / 4.10.x」）都把 4.9.x 当已确认覆盖，而 `L914` V9 明写「只实测了 4.10.0.40；4.9 的 runtimeconfig 未取」。
**建议**：三处都加「（4.9 运行时待确认，见 V9）」，README 矩阵写「4.10.x 已验证；4.9.x 待确认」。

### 7.3 【低】「每个正式版 96 个资产」

`L216`。实测只有 4.10.0.40 恰好 96/20；4.8.11.0=99/20、4.9.3.0=94/20。
**建议措辞**：「4.10.0.40 为 96 个资产 / 20 个 mono-netframework；4.8–4.11 各正式版资产数在 94–99、mono 约 20 个」。

### 7.4 【低】§6.3 决策理由的第一条依赖 §4.3/§4.2 两个有瑕疵的表述

「4 组组合都能编译，差别只在分发洁净度」这一结论**成立**（我复跑了 copy-local 与三种组合的编译），但请与 §4.2/§4.3 一并修正单位与「两个组合」的表述，否则读者按行 B 自查会发现与理由不符。

---

## 8. 必须修正清单（按严重度，10 条）

1. **[高] `L724`（§7.7）**「zip 内**只有一个 dll**」与本 revision 的 2-dll 结论冲突 → 改为「zip 内**恰好 2 个 dll**：`Jellyfin.Plugin.Amane.dll` + `Amane.Core.dll`」。
2. **[高] 证据笔记路径悬空**：`L6/L816/L827/L883/L951/L954/L966/L967/L970-973/L979` 指向不存在的 `docs/research/*.md`（实际在 `.research-scratch/`）→ 落盘或改路径（【生态取证】的唯一溯源入口）。
3. **[高] 单位错误**：`L49/L219/L693/L825` 的「14/22 个**程序集**」→ 实测为「14/22 个**文件** = 10/18 个**程序集**」（垫片 8 个不变）。
4. **[高] `L657`（§6.3 理由 1）**「netstandard2.0 的**两个**组合都会带 8 个垫片」与同表 `L652` 行 B 矛盾 → 改为「只有 **Core 与 Emby 同为 netstandard2.0** 的组合会带 8 个垫片」。
5. **[中] `L851`（§10 Phase 3）** 仍要求「真机实测 **netstandard2.0** 产物决定 Mono 支持」→ 删除，或改为「若将来重启 netstandard2.0 方案（须先解决垫片分发风险）」。
6. **[中] `L200`/`L218`/`L220`/`L734`** 把「覆盖 Emby 4.9.x」当已确认，与 `L914` V9 冲突 → 加「4.9 运行时待 V9 确认」限定。
7. **[中] 「Emby 4.8 = .NET 6」**（`L50/L200/L220/L659/L777/L920`）无证据等级、未入 §12 → 标【生态取证】或补 V 项（我本轮同样无法核对）。
8. **[中] `L630`（§6.2 目录树）** 注释仍写「Core 是 netstandard2.0」→ 改为 net8.0。
9. **[低] `L164`（§2.9）** 残留「若 Core 选 netstandard2.0…必须验证其分发行为（§6.3）」的条件句 → 改为「该路线已在 §6.3 实测否决」。
10. **[低] 编号/字段/口径杂项**：Exec Summary 重复的第二个「6.」（`L39`/`L43`）；§5.6 与 §5.5 顺序（`L552`/`L559`）；§12 表 V17/V16 顺序（`L921`/`L922`）；`EnableUserMenu` → `EnableInUserMenu`（`L424`）；`IImageProvider` 补 `Name { get; }`（`L320`）；`task<…>` → `Task<…>`（`L484`）；「24 处」注明为规则数、点数为 25（`L132`）；§8.4 的「与基线一致」改为「31 个」（`L792`）。

---

## 9. 值得肯定的部分（核对通过，不必改）

- 8 处 `Plugin.Instance` 行号、§5.1 全部文件行数、csproj/release.yml/build-release.sh/README 行号引用 —— **零偏差**。
- 新增的 TFM 翻转结论（net8.0 优先）**证据链扎实**：4.10.0.40 runtimeconfig、system/ 78 dll 清单、两种 TFM 的 copy-local 垫片差异、NU1903、netstandard2.0 的 12 个可空性警告、四种组合的编译 —— 我逐项复跑/复算，均复现。
- 「同一份 .cs 双 SDK 编译 0/0」「3 个 SDK 版本零变更」「双引用静默丢弃」三条最关键的兼容性证据 —— 我用本机产物**独立重跑并复现**。
- 反编译行号（`ItemImageProvider.cs:470`、`ProviderManager.cs:1311`）精确到行，属高质量取证。
- 外部事实（nuspec 无 license、`MediaBrowser.Model` 404、96/20 资产、GPL-2.0/4975 stars/2024-03、ToS 2026-08-16、官方文档 4 条引文、ThePornDB #123 open）—— 全部实地命中。
- 仓库洁净度与「未改任何正式代码」的声明属实。
