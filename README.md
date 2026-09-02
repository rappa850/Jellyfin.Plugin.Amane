# Jellyfin.Plugin.Amane

Jellyfin 10.11 的元数据插件，作为本地 [Amane](https://github.com/sqzw-x/amane) 元数据服务的**透明 HTTP 代理客户端（Thin Client）**。

接入 Amane 之后，Jellyfin 扫库即可自动获得完整的影片信息：

- **影片元数据**：番号+标题、日文原标题、LLM 润色的中文简介、发行日期、厂商、流派、评分
- **图片**：封面（Primary）、背景图/剧照（Backdrop），由 Jellyfin 自动下载缓存
- **演员信息**：头像、简介、生日，演员随影片自动绑定 Amane ID，同名演员不混淆

整个过程无需手工干预：Amane 后端完成文件名清洗、番号提取、PostgreSQL 离线库毫秒级检索与 LLM 中文润色，本插件只负责把结果透明地映射回 Jellyfin。

对接的 Amane 版本：**v0.6.2**（测试开发基准版本）。

## 实现思路

Amane 后端已完成所有重活，因此插件保持极简，不做任何番号正则解析、多源降级或图片中转：

1. 接收 Jellyfin 传入的文件名/番号；
2. 请求 Amane JSON API（`GET /api/metadata?search=…`、`GET /api/actors?search=…`，`Authorization: Bearer` 鉴权）；
3. 将返回字段原样映射回 Jellyfin 契约对象（标题、原标题、简介、发行日期、厂商、流派、评分、演员/导演、番号 ProviderId）。

封面、背景图与演员头像一律以 URL 形式交给 Jellyfin 自行下载缓存。演员头像等人物信息依赖 Amane 侧先完成演员刮削。

插件结构、API 契约要点与开发约定见 [AGENTS.md](AGENTS.md)。

## 功能

- 影片元数据提供器（Movie）：搜索结果与详情映射，标题格式为 `番号 标题`，内联补充演员头像
- 人物元数据提供器（Person）：演员头像、简介、生日
- 图片提供器：`poster_url` → 封面，`thumb_url` + `extrafanart` → 背景图
- 识别框 "Amane" 外部 ID：支持番号或 Amane 内部数字 id 精确绑定
- 配置页：Amane 服务地址、API Token、请求超时

## 构建与部署

```bash
dotnet build -c Release
```

将 `bin/Release/net9.0/` 下所有 dll 复制到 Jellyfin 数据目录 `plugins/Amane/`，重启 Jellyfin 后在插件配置页填入 Amane 地址与 API Token，并在媒体库的元数据/图片下载器中启用 "Amane"。

也可以通过 Jellyfin 插件仓库订阅安装/更新（推荐）：

1. 仪表盘 → 插件 → 存储库 → 添加：`https://raw.githubusercontent.com/rappa850/Jellyfin.Plugin.Amane/main/manifest.json`
2. 在目录中找到 "Amane" 安装；之后打 `v*` tag 会触发 GitHub Actions 自动构建、发布 Release 并更新 manifest，Jellyfin 侧即可看到更新提示。

## 识别与 ID 绑定

影片"识别"对话框含 **Amane** 外部 ID 输入框，支持三种填法：`Amane:IPZZ-822`、`IPZZ-822`（番号搜索）、`22`（Amane 内部数字 id，精确直取）。

识别成功后自动写入两个键：`Amane`（番号，稳定可读）与 `AmaneId`（内部数字 id，后续刷新的快速路径；Amane 库重建导致 id 失效时自动回退番号搜索）。

演员（Person）同样支持绑定：影片入库时演员自动携带 Amane 演员 id（同名演员不再混淆）；也可在人物"编辑元数据"对话框的 External IDs 区手动填写，支持数字 id（精确直取）或演员名（走搜索）。

## 测试

```bash
dotnet test tests/Jellyfin.Plugin.Amane.Tests      # 单元测试（契约反序列化 + 字段映射）
AMANE_TOKEN=xxx ./scripts/probe-amane.sh [番号]    # 对真实 Amane 的契约冒烟 + 响应采样
```

## License

见 [LICENSE](LICENSE)。
