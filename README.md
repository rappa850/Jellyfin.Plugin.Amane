# Jellyfin.Plugin.Amane

[Amane](https://github.com/sqzw-x/amane) 元数据服务的 Jellyfin 插件——Amane 刮削能力在 Jellyfin 端的拓展。

Amane 负责文件名清洗、番号识别、PostgreSQL 离线库毫秒级检索与 LLM 中文润色；本插件把结果透明地映射回 Jellyfin。接入后 Jellyfin 扫库即可自动获得完整影片信息，无需手工干预。

## 功能

- **影片元数据**：番号+标题、日文原标题、中文简介、发行日期、厂商、流派、评分（标题格式为 `番号 标题`）
- **图片**：封面（Primary）、背景图/剧照（Backdrop），以 URL 交给 Jellyfin 自动下载缓存
- **演员信息**：头像、简介、生日；演员随影片自动绑定 Amane ID，同名演员不混淆
- **识别绑定**："识别"对话框的 Amane 外部 ID 支持番号或内部数字 id 精确绑定，识别失败可手动指定
- **配置页**：服务地址、API Token、超时、并发上限，内置"测试连接"按钮，一键验证连通性、延迟与 Token 有效性

## 要求

- Jellyfin **10.11+**
- 已部署的 Amane 服务（开发基准版本 **v0.6.2**）及其 API Token

## 安装

### 插件仓库订阅（推荐）

1. 仪表盘 → 插件 → 存储库 → 添加：`https://raw.githubusercontent.com/rappa850/Jellyfin.Plugin.Amane/main/manifest.json`
2. 在目录中找到 "Amane" 安装，重启 Jellyfin
3. 之后有新版本时 Jellyfin 会提示更新

### 手动安装

从 [Releases](https://github.com/rappa850/Jellyfin.Plugin.Amane/releases) 下载 zip，解压到 Jellyfin 数据目录 `plugins/Amane/`，重启 Jellyfin。

## 使用

1. 仪表盘 → 插件 → Amane：填入 Amane 服务地址（如 `http://127.0.0.1:18000`）与 API Token，保存后点**测试连接**确认连通性与鉴权状态
2. 媒体库设置中，在"元数据下载器"与"图片获取器"里启用 **Amane**
3. 刷新媒体库即可自动刮削

识别不准时，可在影片"识别"对话框的 Amane 输入框手动指定：

- `IPZZ-822` 或 `Amane:IPZZ-822`（按番号搜索）
- `22`（Amane 内部数字 id，精确直取）

演员绑定：影片入库时演员自动携带 Amane 演员 id；也可在人物"编辑元数据"的 External IDs 区手动填写数字 id 或演员名。

## 开发

插件结构、API 契约与构建测试命令见 [AGENTS.md](AGENTS.md)。

## License

见 [LICENSE](LICENSE)。
