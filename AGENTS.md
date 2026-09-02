# AGENTS.md

## 项目定位

`Jellyfin.Plugin.Amane` 是 Jellyfin 10.11 的元数据插件，定位为本地 Amane 元数据服务（默认 `http://127.0.0.1:18000`）的**透明 HTTP 代理客户端（Thin Client）**：

- 接收 Jellyfin 传入的文件名/番号 → 请求 Amane JSON API → 字段原样映射回 Jellyfin 契约对象。
- **不做**番号正则解析、多源降级、图片中转。文件名清洗/番号提取/LLM 润色都是 Amane 后端的职责。
- 图片（封面/背景/演员头像）一律以 URL 形式交给 Jellyfin 自行下载缓存。

## 构建与测试

```bash
dotnet build -c Release                                        # 编译插件（目标 net9.0）
dotnet test tests/Jellyfin.Plugin.Amane.Tests                  # 单元测试（net10.0，见下文说明）
AMANE_TOKEN=xxx ./scripts/probe-amane.sh [番号]                # T3 实时契约冒烟 + 采样
```

- 本机只有 .NET 10 运行时/SDK，插件目标 net9.0（Jellyfin 10.11 要求），测试项目目标 net10.0（net10 可加载 net9 程序集）。
- 部署：`bin/Release/net9.0/` 下所有 dll 复制到 Jellyfin 数据目录 `plugins/Amane/` 后重启。

## 结构

| 路径 | 职责 |
|------|------|
| `Plugin.cs` | `BasePlugin<PluginConfiguration>` + `IHasWebPages`，固定 GUID `9f2e4a6b-7c1d-4e3f-8a5b-0d9c2e1f4a7b` |
| `Configuration/` | `PluginConfiguration`（ServerUrl/ApiToken/TimeoutSeconds）+ 内嵌 `configPage.html` |
| `AmaneClient.cs` | 薄 HTTP 客户端：通用 `GetAsync<T>`、元数据/演员查询、演员 6 小时进程内缓存、`ResolveMetadataAsync`/`ResolveActorAsync` 统一 ID 解析；失败记日志返回空，**不抛异常** |
| `AmaneModels.cs` | DTO（`AmaneMetadata`/`AmaneActor`/`AmaneMetadataDetailResponse`），`[JsonPropertyName]` 对齐 snake_case |
| `Providers/AmaneMovieProvider.cs` | `IRemoteMetadataProvider<Movie, MovieInfo>`；标题格式 `番号 标题`；内联补演员头像并随 `PersonInfo.ProviderIds` 自动绑定演员 id；识别成功双键写入 `Amane`+`AmaneId` |
| `Providers/AmaneMovieExternalId.cs` | `IExternalId`：影片识别框外部 ID（ProviderName 只写 "Amane"，Jellyfin 会自动拼 "Id" 后缀） |
| `Providers/AmanePersonProvider.cs` | `IRemoteMetadataProvider<Person, PersonLookupInfo>`（演员头像/简介/生日）；`ResolveActorAsync` 解析，搜索返回前 5 候选 |
| `Providers/AmanePersonExternalId.cs` | `IExternalId`（Type=Person）：人物编辑框外部 ID；Person 无识别对话框，手动绑定入口在"编辑元数据"External IDs 区 |
| `Providers/AmaneImageProvider.cs` | `IRemoteImageProvider`：`poster_url`→Primary，`thumb_url`+`extrafanart`→Backdrop |
| `ServiceRegistrator.cs` | `IPluginServiceRegistrator` 注册 `AmaneClient` 单例 |
| `Amane/*.sample.json` | 真实 API 响应样本（探针保存），单测的数据源 |
| `Amane/api.md` | Amane 作者提供的 API 层文档 |
| `scripts/probe-amane.sh` | 探针：采样 + 契约断言，schema 漂移时非零退出 |
| `scripts/build-release.sh` | 本地打包：Release 编译 + zip + md5 |
| `manifest.json` | Jellyfin 可订阅的仓库清单；versions 由 CI 在打 tag 时自动追加 |
| `.github/workflows/release.yml` | 打 `v*` tag 触发：构建 → zip → GitHub Release → 更新 manifest.json 回 main |
| `tests/` | xunit 单测（契约反序列化 + 字段映射） |

## ID 绑定设计

- 影片双键存储：`Amane`（番号，稳定可读，识别框显示值）+ `AmaneId`（内部数字 id，精确直取快速路径）。
- 演员单键存储：`Amane`（数字 id；演员无番号类可读标识，名字会撞名）；影片入库时随 `PersonInfo.ProviderIds` 自动写入。
- 识别框/编辑框输入容忍 `Amane:` 前缀（大小写不敏感，自动剥离）；数字走 `GET /api/metadata/{id}` 或 `GET /api/actors/{id}` 直取，否则按番号/演员名搜索。
- 解析统一收口在 `AmaneClient.ResolveMetadataAsync`（影片：AmaneId 直取 → 识别框值 → 名称兜底）与 `ResolveActorAsync`（演员：Amane 值数字直取/名字搜索 → 名称兜底）；数字 id 失效自动回退。
- 演员缓存同时按名字与 `id:N` 双键写入（`CacheActor`），任一入口命中都回填另一键。

## Amane API 契约要点（实测 v0.6.2）

- 鉴权：`Authorization: Bearer <token>`，token 在插件配置页填。其余 header 形态（X-API-Token 等）均 401。
- 元数据查询：`GET /api/metadata?search={q}&limit=n` → `{items: [MetadataResponse], total}`，**列表项即完整详情**，无需二次请求。
- 元数据直取：`GET /api/metadata/{id}` → `{metadata, files, …}`（识别框填数字 id 时使用）。
- 演员查询：`GET /api/actors?search={name}` → `ActorResponse`（`image_urls`/`birthday`/`overview` 等）；演员头像依赖 Amane 侧先刮削（`POST /api/actors/{id}/scrape`），未刮削的演员 `image_urls` 为空。
- 演员直取：`GET /api/actors/{id}` → **无包装**直接返回演员对象（列表项不填简介/别名，详情全量含 `aliases`/`provider_ids`/`source_urls`）。
- OpenAPI：`GET /openapi.json`（无需 token；`/api/openapi.json` 需 token）。
- 关键字段名：`plot`（非 overview）、`release`、`tags`、`poster_url/thumb_url/extrafanart`、`actors` 为纯字符串数组；日文原标题从 `raw.<来源>.title` 提取。
- 评分 `score` 为来源站 5 分制，插件 ×2 换算到 Jellyfin 10 分制。

## 易踩的坑（改动时注意）

- 10.11 中配置页接口是 `MediaBrowser.Model.Plugins.IHasWebPages`（旧文档里的 `IHasWebConfiguration` 已不存在）；`PersonKind` 在 `Jellyfin.Data.Enums`；`PersonInfo.ImageUrl` 存在但官方 XML 文档未列出。
- 主项目在仓库根，`Microsoft.NET.Sdk` 默认通配会扫进 `tests/**/*.cs`——csproj 里已有 `Compile Remove`，新增测试目录时保持该排除。
- 主项目 Jellyfin 包引用带 `PrivateAssets=all`，不传递给测试项目；测试项目需自行引用 `Jellyfin.Controller/Model`。
- `IHttpClientFactory` 创建的 HttpClient 不要 `using` 释放；`GetImageAsync` 返回的响应流由 Jellyfin 读取，客户端内不得释放。
- csproj 的 `InternalsVisibleTo` 向测试程序集开放 `internal` 成员（如 `MapToMovie`）。

## 约定

- 代码注释、配置页文案用中文；遵循官方 Jellyfin 插件模板结构（`.NET` 风格、文件头 XML doc）。
- 新增 Amane 字段映射时：先跑 `scripts/probe-amane.sh` 更新样本，再改 DTO + 映射 + 单测断言，三者同步。
- 不新增第三方依赖；端点路径收敛在 `AmaneClient` 一处。
