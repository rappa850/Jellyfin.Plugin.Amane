# AGENTS.md

## 项目定位

`Jellyfin.Plugin.Amane` 提供 Jellyfin 10.11.x（最低 10.11.10）、Jellyfin 12.1 和 Emby .NET 8 三个分别编译的适配器。插件定位为本地 Amane 元数据服务（默认 `http://127.0.0.1:18000`）的**透明 HTTP 代理客户端（Thin Client）**：

- 接收 Jellyfin / Emby 传入的文件名/番号 → 请求 Amane JSON API → 字段原样映射回各自宿主契约对象。
- **不做**番号正则解析、多源降级、图片中转。文件名清洗/番号提取/LLM 润色都是 Amane 后端的职责。
- 图片下载回调将外源 URL 改写为 Amane 的 `/api/resources/proxy`，附加域内 Bearer；直出路径使用 `ToDirectImageUrl`，遵循宿主的鉴权能力限制。插件不刮削、裁切、缓存图片，也不提供自己的图片代理端点。

## 构建与测试

```bash
dotnet build Amane.slnx -c Release                            # Core + 三适配器
dotnet test Amane.slnx -c Release                             # 全部测试（net10.0）
AMANE_TOKEN=xxx ./scripts/probe-amane.sh [番号]                # T3 实时契约冒烟 + 采样
# Windows 也可设置 AMANE_URL / AMANE_TOKEN 后运行：
python scripts/probe-amane.py [番号] --actor "演员名"           # 当前公开 API 样本写入 Amane/current
```

- 本机有 SDK 9/10 与 .NET 8/9/10 runtime；Core/Emby net8，Jellyfin 10.11 net9，Jellyfin 12.1 net10；测试统一 net10。
- 白名单打包用 `./scripts/build-release.ps1 all`（Windows）或 `bash scripts/build-release.sh all`。三个适配器均通过源码 Link 将 Core 编译进单个插件 DLL，不分发 Amane.Core.dll；Core 项目保留用于独立测试。禁止复制宿主 SDK DLL。
- Jellyfin 10.11（SDK 10.11.10 / net9）与 12.1（SDK 12.1.0 / net10）分别生成同名 DLL，不可互换或同时部署；Emby 使用独立 DLL（SDK 4.9.1.90 / net8，实测服务器 4.10.0.40）。编译通过不能替代实际宿主验证，Emby Mono / .NET Framework 不在支持范围内。
- Emby 从字节加载且先反射类型，Core 通过源码 Link 编译进 Emby 单 DLL，不能仅拷 Core DLL 或依赖 ModuleInitializer。构造插件时用 Lazy 延迟配置读取。诊断 IService 异步方法必须返回 Task<object>。
- 实测与图片边界见 `docs/compatibility-validation.md`、`docs/image-url-evaluation.md`；Emby 自动下载鉴权图片要求媒体库开启 DownloadImagesInAdvance。

## 结构

| 路径 | 职责 |
|------|------|
| `Amane.slnx` | Core、三个适配器与四个测试项目的解决方案 |
| `src/Amane.Core/AmaneClient.cs` | 薄 HTTP 客户端、统一 ID 解析、演员缓存（默认 360 分钟，0 禁用）、探活；查询失败记日志返回空，外部取消向上抛。并发背压默认 4，每请求 linked CTS 超时默认 5s，连续失败 5 次熔断 30s；图片与探活不占并发额度、不计熔断。图片超时下限 30s，非 2xx/非 image 抛异常；Bearer 只发 Amane 域内 URL |
| `src/Amane.Core/AmaneModels.cs` | snake_case DTO，兼容新旧多图字段，区分演员卡片与完整详情 |
| `src/Amane.Core/AmaneSettings.cs` / `AmaneMetadataView.cs` | 无宿主依赖的配置与 HTTP 提供接口、绑定键和公共字段转换 |
| `src/Amane.Jellyfin/` | Jellyfin 10.11 项目与适配源码；`Plugin.cs` GUID 为 `9f2e4a6b-7c1d-4e3f-8a5b-0d9c2e1f4a7b` |
| `src/Amane.Jellyfin12/` | 独立 12.1 项目，通过 Link 共用 Jellyfin 适配源码与 Core 源码 |
| `src/Amane.Jellyfin/Configuration/` | 五项配置与内嵌配置页；12.1 同步共用 |
| `src/Amane.Jellyfin/Api/AmaneDiagnosticsController.cs` | 授权的 `GET /Amane/Health` 与 `POST /Amane/ClearCache`；服务端请求 Amane，避免浏览器直连的 CORS/Token 暴露 |
| `src/Amane.Jellyfin/Providers/` | Movie/Person 元数据与外部 ID、Movie 图片；影片双键绑定，演员数字 ID 随 PersonInfo 写入；标题为 `番号 标题` |
| `src/Amane.Jellyfin/ServiceRegistrator.cs` / `JellyfinHostServices.cs` | 注册共享客户端单例，桥接 HTTP 工厂与热配置 |
| `src/Amane.Emby/` | 独立适配、原生日志/HTTP 桥接、响应流所有权包装；GUID 为 `e4c1a8a9-023d-4a7a-978b-eec21d66c059` |
| `src/Amane.Emby/Api/` / `Configuration/` | 管理员 IService 诊断接口、内嵌 HTML + AMD JS 配置页 |
| `Amane/*.sample.json` | 真实 API 响应样本（探针保存），单测的数据源 |
| `Amane/current/` | 当前上游公开 API 的合成实测样本与 OpenAPI；旧文档及旧样本保留回归 |
| `Amane/api.md` | 历史 API 层文档；当前开发以最新上游公开 API 与实测为准 |
| `scripts/probe-amane.sh` | 探针：采样 + 契约断言 + 图片代理（/api/resources/proxy）可达性检查，漂移时非零退出 |
| `scripts/probe-amane.py` | Windows / WSL 可用的当前公开 API 采样与图片契约探针 |
| `scripts/build-release.ps1` / `build-release.sh` | 三平台 Release 编译 + 单 DLL 白名单 zip + md5 |
| `scripts/integration/` | 隔离 Docker 三宿主、合成图片源、测试转发与实际宿主验证；凭据不入库 |
| `manifest.json` | Jellyfin 可订阅的仓库清单；versions 由 CI 在打 tag 时自动追加 |
| `.github/workflows/release.yml` | 打 `v*` tag 触发：构建 → zip → GitHub Release → 更新 manifest.json 回 main |
| `.github/workflows/release-emby.yml` / `ci.yml` | Emby 独立发布；三平台构建、测试、隔离与打包检查 |
| `tests/` | Core 及三个适配器的 xunit 测试；两版 Jellyfin 共用适配测试源码 |

## 安装与连接文档约定

- 三适配器使用相同发布版本号；`v*` 标签发布三个安装包。Emby 包和程序集分别为 `Emby.Plugin.Amane.zip` / `Emby.Plugin.Amane.dll`，通过 `IHasThumbImage` 内嵌共用根目录 `thumb.png`；显示名称仍为 Amane。升级旧 Emby 开发包须移除 `Amane.Emby.dll`，避免重复加载。
- Emby 配置文件名跟随程序集名：升级须停服备份并迁移 `plugins/configurations/Amane.Emby.xml` 到 `Emby.Plugin.Amane.xml`，已有新配置不要覆盖。插件 GUID 不变，但不能据此认定配置文件自动迁移。
- README 保留现有章节结构，维护三平台包名和单 DLL 部署方式。Jellyfin 仓库清单通过 `targetAbi` 区分 10.11 / 12.1；Emby 不加入 Jellyfin 清单。未发布的本地包不能描述为已可通过订阅安装。
- Jellyfin 部署到实际数据目录的 `plugins/Amane/`；Emby 部署到 `plugins/`。Docker 官方镜像分别是 `/config/plugins/Amane/` 与 `/config/plugins/`，其他镜像以实际数据目录为准；须保留持久化挂载，升级旧开发包清理独立 Core DLL。
- ServerUrl 是宿主服务器访问 Amane 的根地址，不附加 `/api`：同一本机用 loopback，同 Docker 网络用服务名/别名与内部端口，容器访问宿主用 `host.docker.internal`，本机访问容器用发布后的宿主端口。
- Linux Docker 的宿主别名通常须配置 `extra_hosts: ["host.docker.internal:host-gateway"]`；这只提供解析，宿主监听接口仍须可由网桥访问。不要把本机 WSL 测试网桥 IP / 18102 转发端口作为通用安装默认值。
- 插件 API 连通、宿主图片下载、浏览器预览是不同访问路径，分别验证；同 Docker 网络的服务别名不保证浏览器可解析。图片简化仍以 `docs/image-url-evaluation.md` 的实测约束为准，不通过暴露完整 Token 到 URL 绕过鉴权。

## ID 绑定设计

- 影片双键存储：`Amane`（番号，稳定可读，识别框显示值）+ `AmaneId`（内部数字 id，精确直取快速路径）。
- 演员单键存储：`Amane`（数字 id；演员无番号类可读标识，名字会撞名）；影片入库时随 `PersonInfo.ProviderIds` 自动写入。
- 识别框/编辑框输入容忍 `Amane:` 前缀（大小写不敏感，自动剥离）；数字走 `GET /api/metadata/{id}` 或 `GET /api/actors/{id}` 直取，否则按番号/演员名搜索。
- 解析统一收口在 `AmaneClient.ResolveMetadataAsync`（影片：AmaneId 直取 → 识别框值 → 名称兜底）与 `ResolveActorAsync`（演员：Amane 值数字直取/名字搜索 → 名称兜底）；数字 id 失效自动回退。
- 演员缓存同时按名字与 `id:N` 双键写入（`CacheActor`），任一入口命中都回填另一键；`ActorCacheMinutes` 配置为 0 时完全不读写缓存，`POST /Amane/ClearCache` 可立即清空。

## Amane API 契约要点（旧基准 v0.16.1；当前公开 API 已实测 v0.17.0）

- 最新合成实测样本位于 `Amane/current/`，以公开上游 `b4b5154` 与 `/openapi.json` 为依据。新增字段先 `scripts/probe-amane.py` 采样，再同步 DTO/映射/测试。
- 当前多图候选：`poster_urls`、`thumb_urls`、按来源分组的 `extrafanart_urls`；兼容旧单值字段，保持后端顺序并去重。
- 演员列表是卡片，简介/生日必须直取详情；卡片缓存不能冒充完整详情。图片无匿名或 token 查询参数鉴权能力，Cookie 不能覆盖宿主裸 HTTP 路径。

- 鉴权：`Authorization: Bearer <token>`，token 在插件配置页填。其余 header 形态（X-API-Token 等）均 401。
- 元数据查询：`GET /api/metadata?search={q}&limit=n` → `{items: [MetadataResponse], total}`，**列表项即完整详情**，无需二次请求。
- 元数据直取：`GET /api/metadata/{id}` → `{metadata, files, …}`（识别框填数字 id 时使用）。
- 演员查询：`GET /api/actors?search={name}` → `{items, total}`，列表为卡片；简介/生日以详情响应为准。演员头像依赖 Amane 侧已有图片（刮削入口 `POST /api/actors/{id}/scrape`），插件不发起演员刮削。
- 演员直取：`GET /api/actors/{id}` → **无包装**直接返回演员对象（列表项不填简介/别名，详情全量含 `aliases`/`provider_ids`/`source_urls`）。
- 图片代理：`GET /api/resources/proxy?url={外源图片URL}`（**需 token**，实测无 token 401）→ 命中本地 ResourceStore 直接返回，未命中下载后入 store；上游失败 502 且进程内负缓存 15 分钟。注意 `poster_url` 可能是**相对路径** `/api/resources/{hash}`（裁切海报，实测 SONE-614）——代理端点只接受绝对外源 URL（相对路径 400），插件 `ToProxyImageUrl` 对相对路径直接补全 ServerUrl 直取，外源 URL 才走代理。`/api/resources/{hash}` 同样需 token（无 token 401）。
- **图片 URL 双轨制**（v1.0.6 修复 v1.0.5 回归）：Jellyfin 的图片消费分两类——下载路径 `RemoteImageInfo.Url` 经 `ItemImageProvider` → `GetImageResponse`（插件可附 Bearer，用 `ToProxyImageUrl`）；直出路径无法带 token，必须用 `ToDirectImageUrl`（外源原样、Amane 本地资源返回 null）：识别/搜索弹窗缩略图 `RemoteSearchResult.ImageUrl`（jellyfin-web 把 ImageUrl 原样塞进 `<img>`，浏览器直连）、`RemoteImageInfo.ThumbnailUrl`、演员头像 `PersonInfo.ImageUrl` 与 Person `ItemImageInfo.Path`（Jellyfin 用裸 HttpClient 经 `ConvertImageToLocal`/`ProviderManager.SaveImage` 下载）。直出路径拿不到 Amane 图是机制使然，不是 bug。
- 三宿主手动远程选图均绕过插件鉴权回调，上游 401 可能表现为宿主 500。Emby 识别预览调用 Provider 图片回调，可显示鉴权本地图；自动影片图必须开启 `DownloadImagesInAdvance`，延迟裸下载不带 Token。检查实际图片响应，不能只根据 ImageTags 判定下载成功。
- OpenAPI：`GET /openapi.json`（无需 token）；Amane 的鉴权中间件只保护 `/api/*`，除 `/api/health` 外均需 Bearer。配置页"测试连接"通过 `GET /api/metadata?limit=1` 验证 Token，不能用公开的 OpenAPI 文档验证。
- 健康检查：`GET /api/health` → `{status, version}`，**无需 token**（错误 token 也返回 200），只证明服务可达，不能据此判断鉴权。
- 关键字段名：`plot`（非 overview）、`release`、`tags`、`poster_url/thumb_url/extrafanart`、`actors` 为纯字符串数组；日文原标题从 `raw.<来源>.title` 提取。
- 评分 `score` 为来源站 5 分制，插件 ×2 换算到 Jellyfin 10 分制。

## 易踩的坑（改动时注意）

- 10.11 中配置页接口是 `MediaBrowser.Model.Plugins.IHasWebPages`（旧文档里的 `IHasWebConfiguration` 已不存在）；`PersonKind` 在 `Jellyfin.Data.Enums`；`PersonInfo.ImageUrl` 存在但官方 XML 文档未列出。
- 适配项目位于 `src/`；Core 源码通过 Link 引入，Jellyfin 12.1 还链接 10.11 适配源码，必须排除源项目的 `bin/obj`。业务代码只维护一份，独立 Core 测试项目不能与包含同名 Core 类型的适配器同时引用而造成类型冲突。
- 主项目 Jellyfin 包引用带 `PrivateAssets=all`，不传递给测试项目；测试项目需自行引用 `Jellyfin.Controller/Model`。
- `IHttpClientFactory` 创建的 HttpClient 不要 `using` 释放；`GetImageAsync` 返回的响应流由 Jellyfin 读取，客户端内不得释放（失败分支会先 `response.Dispose()` 再抛）。
- `GetImageAsync` 的 Bearer token 只发 Amane 域内 URL：按 URI 协议、主机、端口与路径边界判断，不能退回字符串 StartsWith（会误判同前缀第三方地址）。
- csproj 的 `InternalsVisibleTo` 向测试程序集开放 `internal` 成员（如 `MapToMovie`、`AmaneClient` 测试构造函数）。
- 弹性测试用 internal 构造函数注入并发/超时/熔断/缓存 TTL 参数；`MaxConcurrentRequests` 在 `AmaneClient` 构造时读取，改配置需重启宿主生效（`TimeoutSeconds`、`ActorCacheMinutes` 为每请求读取，即时生效）。宿主客户端 Timeout 设置为无限，由每请求 CTS 控制；复用客户端不能在发送后反复修改 Timeout。
- 插件 API 控制器（`Api/`）依赖 csproj 的 `FrameworkReference Microsoft.AspNetCore.App`（不拷出程序集）；Jellyfin 自动注册插件程序集中的控制器，接口响应按 Jellyfin 默认 **PascalCase** 序列化，前端 JS 取字段注意大小写。

## 约定

- 代码注释、配置页文案用中文；遵循官方 Jellyfin 插件模板结构（`.NET` 风格、文件头 XML doc）。
- 新增 Amane 字段映射时：先参考 `https://github.com/sqzw-x/amane` 当前公开 API，并运行探针更新样本，再改 DTO + 映射 + 单测断言，三者同步；不以历史 api.md 为唯一依据。
- 不新增第三方依赖；端点路径收敛在 `AmaneClient` 一处。
