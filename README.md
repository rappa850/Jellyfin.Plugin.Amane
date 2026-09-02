# Jellyfin.Plugin.Amane

Jellyfin 10.11 的元数据插件，作为本地 [Amane](https://github.com/sqzw-x/amane) 元数据服务的**透明 HTTP 代理客户端（Thin Client）**。

对接的 Amane 版本：**v0.6.2**（测试开发基准版本）。

## 实现思路

Amane 后端已完成文件名清洗、番号提取、PostgreSQL 离线库检索与 LLM 中文润色，因此插件保持极简，不做任何番号正则解析、多源降级或图片中转：

1. 接收 Jellyfin 传入的文件名/番号；
2. 请求 Amane JSON API（`GET /api/metadata?search=…`、`GET /api/actors?search=…`，`Authorization: Bearer` 鉴权）；
3. 将返回字段原样映射回 Jellyfin 契约对象（标题、原标题、简介、发行日期、厂商、流派、评分、演员/导演、番号 ProviderId）。

封面、背景图与演员头像一律以 URL 形式交给 Jellyfin 自行下载缓存。演员头像等人物信息依赖 Amane 侧先完成演员刮削。

插件结构、API 契约要点与开发约定见 [AGENTS.md](AGENTS.md)。

## 功能

- 影片元数据提供器（Movie）：搜索结果与详情映射，内联补充演员头像
- 人物元数据提供器（Person）：演员头像、简介、生日
- 图片提供器：`poster_url` → 封面，`thumb_url` + `extrafanart` → 背景图
- 配置页：Amane 服务地址、API Token、请求超时

## 构建与部署

```bash
dotnet build -c Release
```

将 `bin/Release/net9.0/` 下所有 dll 复制到 Jellyfin 数据目录 `plugins/Amane/`，重启 Jellyfin 后在插件配置页填入 Amane 地址与 API Token，并在媒体库的元数据/图片下载器中启用 "Amane"。

## 测试

```bash
dotnet test tests/Jellyfin.Plugin.Amane.Tests      # 单元测试（契约反序列化 + 字段映射）
AMANE_TOKEN=xxx ./scripts/probe-amane.sh [番号]    # 对真实 Amane 的契约冒烟 + 响应采样
```

## License

见 [LICENSE](LICENSE)。
