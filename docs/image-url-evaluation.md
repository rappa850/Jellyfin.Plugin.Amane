# 图片 URL 多轨简化评估

2026-10-03。评估目标：所有图片仅使用 Amane 的资源能力，插件不自行发起图片请求。测试使用隔离 Amane 0.17.0（上游提交 `b4b5154f28f4f980f4b31dcdf33be63c12ddd52f`）、合成蓝色 PNG、公开 crop-poster 接口产生的本地 JPEG，未刮削第三方内容。

## 当前结论

**启用 Amane 鉴权时，现有公开 API 无法完成这个简化。** Amane 负责资源下载、缓存与裁切已经可用，但宿主有多条图片消费路径，其中若干只接受可匿名下载的 URL。插件的 `GetImageResponse` 只是宿主下载时附加 Bearer 的必要适配，不执行刮削、裁切或第三方图片缓存。现阶段保留多轨，不能把全部字段简单替换成 `/api/resources/*`。

## Amane 能力实测

证据：[请求观察](../Amane/current/image-auth-observations.json)、[当前 OpenAPI](../Amane/current/openapi.sample.json)。仅测试公开端点。

| 访问方式 | 图片代理 `/api/resources/proxy` | 本地资源 `/api/resources/{hash}` |
|---|---|---|
| Bearer | 200，image/png | 200，image/jpeg |
| 无凭据 | 401 | 401 |
| `?token=...` | 未作为方案使用 | 401，不能作为鉴权 |
| `amane_token` cookie | 200 | 未测试 |

Cookie 可以满足已有 Amane 登录会话的浏览器请求，但不会自动传播至 Jellyfin / Emby 的服务端 HTTP 客户端；还受到浏览器与 Amane 地址/会话关系影响，无法覆盖整个图片链路。当前公开 OpenAPI 没有带期限、限定资源权限的签名图片 URL。不能把完整 API Token 写进图片 URL 来模拟签名。

## 三个宿主的实测

证据：[宿主响应摘要](verification/host-observations.json)。HTTP 500 是宿主处理上游 401 后返回的状态，不是 Amane 返回 500。

| 路径 | Jellyfin 10.11.10 | Jellyfin 12.1.0 | Emby 4.10.0.40 |
|---|---|---|---|
| 自动影片图 | 经插件鉴权回调，缓存图 GET 200 | 同左 | 开启提前下载后缓存 GET 200；关闭时延迟裸下载 401 |
| 手动选本地裁切图 | 裸 HTTP 下载，上游 401、宿主 500 | 同左 | 同左 |
| 识别预览 | 浏览器直接使用 ImageUrl，需要匿名 URL | 同左 | 当时记为 RemoteSearch/Image 回调插件（issue #11 更正：Web 识别框走 `/Images/Remote` 裸请求） |
| 人物/内联演员图 | 外源匿名 URL 直出；鉴权本地图无法供裸下载 | 同左 | 同类裸下载约束 |

按当时的判断，Jellyfin 识别结果采用外源候选，Emby 识别结果保留需 Bearer 的本地图。issue #11 表明 Emby Web 识别预览同样是裸请求，该差异不再成立，见下方更正。

本轮真实自动下载确认电影裁切海报和背景图能够缓存。评分、双 ID、演员数字绑定、简介和生日也通过验证。影片时长可能由媒体探针覆盖（合成视频 1 秒），这符合宿主对实际媒体属性的处理。

## 2026-11 更正：Emby Web 识别预览（issue #11）

上表「识别预览」一行把 Emby 4.10 写成「宿主 RemoteSearch/Image 调用插件，鉴权本地图预览 200」。用户在 Amane 0.24.1 上的识别对话框实际把 `RemoteSearchResult.ImageUrl` 交给 `/emby/Images/Remote?imageUrl=`，由服务器裸 HTTP GET 拉取，不调用 `Provider.GetImageResponse`，因此不附带 Bearer。相对路径裁切海报经 `ToProxyImageUrl` 只补全为 `http://{ServerUrl}/api/resources/{hash}`，响应正文是 Amane 的 `Unauthorized`。

把服务地址从 loopback 改成局域网地址，或开启「预先下载图像」，都不能给这条预览请求加上鉴权。提前下载只影响自动影片图的 `GetImageResponse` 回调。

Emby 影片与演员搜索结果的 `ImageUrl` 已与 Jellyfin 对齐，使用 `ToDirectImageUrl`：

- 外源绝对 URL 原样返回，可供裸请求访问。
- Amane 本地资源在已配置图片 Token 时补全为绝对地址并附加 `?token=`。这里只用图片 Token，不使用 API Token。
- 未配置图片 Token 时跳过该本地候选。影片搜索继续尝试后续海报与横幅；全部不可直出时 `ImageUrl` 为空，避免再交给宿主一个必然 401 的地址。

自动下载仍把 `RemoteImageInfo.Url` 写成 `ToProxyImageUrl`，由 `GetImageResponse` 附加 Bearer。Emby 自动影片图仍须开启提前下载。手动选图的裸下载限制不变。本仓库不新增图片代理端点。

## 可行的后续简化方向

1. Amane 上游提供专用于图片、限制资源与有效期的签名 URL，或者独立且明确控制的公开图片资源服务。这必须是 Amane 的公开能力，插件不自行制造签名。
2. 插件获得该 URL 后，统一填入搜索预览、图片候选、演员头像；分别验证浏览器和宿主裸 HTTP 下载。需要覆盖已裁切的相对资源、过期续签、反向代理地址以及跨宿主访问。
3. 宿主仍要求实现 `GetImageResponse` 契约。即使 URL 可匿名访问，也只能将该回调委托给宿主原生 HTTP 服务，或调整宿主本身；不能直接删除接口方法而保持 Provider 可加载。

关闭整个 Amane 的 API 鉴权会改变服务访问边界，不作为本插件默认方案。本轮没有对用户原有 Amane 实例关闭鉴权，也没有添加插件图片代理端点。
