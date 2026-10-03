# Jellyfin.Plugin.Amane

[Amane](https://github.com/sqzw-x/amane) 元数据服务的 Jellyfin / Emby 薄客户端插件。

Amane 负责文件名清洗、番号识别、元数据检索与 LLM 中文润色；本插件把结果透明地映射回 Jellyfin / Emby。接入后扫描媒体库即可自动获得影片信息。

## 功能

- **影片元数据**：番号+标题、日文原标题、中文简介、发行日期、厂商、流派、评分（标题格式为 `番号 标题`）
- **图片**：封面（Primary）、背景图/剧照（Backdrop），由 Jellyfin / Emby 下载缓存
- **演员信息**：头像、简介、生日；演员随影片自动绑定 Amane ID，同名演员不混淆
- **识别绑定**："识别"对话框的 Amane 外部 ID 支持番号或内部数字 id 精确绑定，识别失败可手动指定
- **配置页**：服务地址、API Token、超时、并发上限，内置"测试连接"按钮，一键验证连通性、延迟与 Token 有效性

## 要求

- 支持下列三个独立适配器；运行环境由对应媒体服务器提供，无需另外运行插件进程。
- 已部署的 Amane 服务及 API Token；当前契约已以 **0.17.0 / b4b5154** 的公开 API 实测，并保留 0.16.1 样本回归。

| 媒体服务器 | 插件目标 | 实测版本 |
|---|---|---|
| Jellyfin 10.11.x（最低 10.11.10） | .NET 9 | 10.11.10 |
| Jellyfin 12.1 | .NET 10 | 12.1.0 |
| Emby .NET 8 宿主 | .NET 8，SDK 4.9.1.90 | 4.10.0.40 |

Jellyfin 两版共用源码但分别编译，必须选择匹配的安装包；Emby 使用自己的安装包。Emby Mono / .NET Framework 宿主未支持。

## 安装

### 插件仓库订阅（Jellyfin）

1. 仪表盘 → 插件 → 存储库 → 添加：`https://raw.githubusercontent.com/rappa850/Jellyfin.Plugin.Amane/main/manifest.json`
2. 在目录中找到 "Amane" 安装，重启 Jellyfin
3. 之后有新版本时 Jellyfin 会提示更新

发布清单通过 `targetAbi` 区分 Jellyfin 10.11 与 12.1，服务器选择匹配的版本。Emby 使用手动安装，不使用 Jellyfin 的仓库清单。

### 手动安装

从 [Releases](https://github.com/rappa850/Jellyfin.Plugin.Amane/releases) 选择与服务器匹配的安装包；从 **v1.0.8** 起，同一版本提供三个适配器。也可用 `./scripts/build-release.ps1 all` 在本地构建；Linux / WSL 可用 `bash scripts/build-release.sh all`。包名和部署位置如下（本地构建输出到 `dist/`）：

| 宿主 | 安装包 | 部署 |
|---|---|---|
| Jellyfin 10.11 | `Jellyfin.Plugin.Amane.zip` | 单个 `Jellyfin.Plugin.Amane.dll` 放入数据目录 `plugins/Amane/` |
| Jellyfin 12.1 | `Jellyfin.Plugin.Amane.12.zip` | 单个 `Jellyfin.Plugin.Amane.dll` 放入数据目录 `plugins/Amane/` |
| Emby .NET 8 | `Emby.Plugin.Amane.zip` | 单个 `Emby.Plugin.Amane.dll` 放入数据目录 `plugins/` |

三个适配器使用相同的发布版本号（本次为 **1.0.8**）与 Amane 图标；Emby 图标内嵌在 DLL 中，插件列表显示名称仍为 **Amane**。从旧 Emby 开发包升级时，停止服务器，删除 `plugins/Amane.Emby.dll`，避免同一插件重复加载，再安装 `Emby.Plugin.Amane.dll`。若要保留旧配置，备份并将 `plugins/configurations/Amane.Emby.xml` 复制为 `plugins/configurations/Emby.Plugin.Amane.xml`（已有新配置时不要覆盖）；否则重新填写服务地址与 Token。

重启对应宿主。不要把宿主 SDK DLL 拷入插件目录，也不要同时部署两版 Jellyfin 适配器。三个适配器均将同一份 Core 源码编译进各自插件 DLL，不再分发独立 `Amane.Core.dll`。从此前双 DLL 开发包升级时，移除插件目录中的旧 `Amane.Core.dll`。

### 本机安装（Windows / Linux）

1. 在服务器仪表盘或启动日志中确认实际数据目录，停止服务器。
2. 解压对应安装包，把 DLL 放入上表的目录。Jellyfin 创建 `plugins/Amane/`；Emby 直接放入 `plugins/`。
3. 启动服务器，在仪表盘 → 插件中确认 **Amane** 已加载，然后填写配置。

Jellyfin 常见目录为 Windows 托盘安装的 `%ProgramData%\Jellyfin\Server\plugins\Amane\`，以及 Linux 包安装的 `/var/lib/jellyfin/plugins/Amane/`；自定义安装或服务启动参数可能改变数据目录。Emby 使用其 **Server Data Folder** 下的 `plugins/`。路径说明见 [Jellyfin 插件文档](https://jellyfin.org/docs/general/server/plugins/index.html)与 [Emby 插件文档](https://emby.media/support/articles/Plugins.html)。

### Docker 安装（绑定目录 / 命名卷）

以下使用官方 `jellyfin/jellyfin` 与 `emby/embyserver` 镜像的 `/config` 数据目录，容器名称示例为 `jellyfin` / `emby`；替换成自己的名称。其他镜像先确认实际插件目录。

若 Compose 中挂载 `./jellyfin-config:/config`，将 Jellyfin DLL 放入宿主机的 `./jellyfin-config/plugins/Amane/`，再执行 `docker restart jellyfin`。Emby 挂载 `./emby-config:/config` 时，将 Emby DLL 放入 `./emby-config/plugins/`，再执行 `docker restart emby`。

命名卷或不方便操作宿主目录时，可把解压后的 DLL 复制进现有容器：

```sh
# Jellyfin：10.11 / 12.1 选择各自安装包，DLL 文件名相同
docker exec jellyfin mkdir -p /config/plugins/Amane
docker cp ./Jellyfin.Plugin.Amane.dll jellyfin:/config/plugins/Amane/Jellyfin.Plugin.Amane.dll
docker restart jellyfin

# Emby
docker exec emby mkdir -p /config/plugins
docker cp ./Emby.Plugin.Amane.dll emby:/config/plugins/Emby.Plugin.Amane.dll
docker restart emby
```

`/config` 应挂载为持久化目录或命名卷，否则重建容器会丢失插件和配置。若出现目录写入失败，检查挂载目录和容器运行用户的权限。Jellyfin 容器挂载说明见 [官方容器文档](https://jellyfin.org/docs/general/installation/container/)。

## 使用

1. 仪表盘 → 插件 → Amane：填入 Amane 服务地址（如 `http://127.0.0.1:18000`）与 API Token，保存后点**测试连接**确认连通性与鉴权状态
2. 媒体库设置中，在"元数据下载器"与"图片获取器"里启用 **Amane**
3. 刷新媒体库即可自动刮削

Emby 媒体库须启用 **提前下载图片**（`DownloadImagesInAdvance`）。实测关闭时，宿主把远程 URL 延迟保存后用裸 HTTP 下载，Amane 鉴权资源返回 401；开启后自动下载会经过插件回调，海报与背景图正常缓存。手动远程选图仍绕过插件鉴权回调，当前有已知限制，见 [图片 URL 评估](docs/image-url-evaluation.md)。

识别不准时，可在影片"识别"对话框的 Amane 输入框手动指定：

- `IPZZ-822` 或 `Amane:IPZZ-822`（按番号搜索）
- `22`（Amane 内部数字 id，精确直取）

演员绑定：影片入库时演员自动携带 Amane 演员 id；也可在人物"编辑元数据"的 External IDs 区手动填写数字 id 或演员名。

### Amane 服务地址与 Docker 内部连接

插件的服务地址是 **Jellyfin / Emby 服务器访问 Amane 的地址**，填写服务根地址，不附加 `/api`。容器里的 `127.0.0.1` 指该容器自身。下表以 Amane 监听端口 `18000` 为例；实际端口不同就相应替换。

| 部署方式 | 插件中填写的服务地址 | 条件 |
|---|---|---|
| 媒体服务器与 Amane 都运行在同一本机 | `http://127.0.0.1:18000` | Amane 在本机监听 18000 |
| 两者都是容器，位于同一 Docker 网络 | `http://amane:18000` | `amane` 是 Compose 服务名或网络别名；使用容器内部端口 |
| 媒体服务器在 Docker Desktop，Amane 在宿主机 | `http://host.docker.internal:18000` | 使用 Docker Desktop 提供的宿主机别名 |
| 媒体服务器在 Linux Docker，Amane 在宿主机 | `http://host.docker.internal:18000` | 给媒体服务器容器添加下方 `extra_hosts`，并保证宿主服务可被网桥访问 |
| 媒体服务器在本机，Amane 在 Docker | `http://127.0.0.1:18000` | Amane 已通过 `18000:18000` 发布端口；填写发布后的宿主端口 |
| Amane 在局域网另一台机器 | `http://192.168.1.20:18000` | 替换为实际地址；监听地址、防火墙允许服务器访问 |

同一 Compose 项目中的容器默认可用服务名互访。例如 Amane 服务名为 `amane`，端口映射是 `18080:18000`：同网络的 Jellyfin / Emby 填 `http://amane:18000`，宿主机访问则用 `http://127.0.0.1:18080`。不同 Compose 项目需显式加入同一个共享网络；单独写相同别名不会打通网络。[Docker Compose 网络说明](https://docs.docker.com/compose/how-tos/networking/)

Linux Docker 访问宿主机时，在 **Jellyfin / Emby 服务**下添加：

```yaml
services:
  jellyfin: # Emby 则放在 emby 服务下
    extra_hosts:
      - "host.docker.internal:host-gateway"
```

更新 Compose 配置后执行 `docker compose up -d jellyfin`（Emby 替换服务名）。`host-gateway` 提供名称解析；Linux 网桥访问不到宿主仅绑定 `127.0.0.1` 的服务，Amane 需监听可达的宿主接口，例如 `0.0.0.0`，并配置相应的网络访问范围。Docker Desktop 的宿主别名见 [官方说明](https://docs.docker.com/desktop/features/networking/networking-how-tos/)。

本项目的 WSL 实测环境另有转发端口 `18102`，只属于开发测试配置；普通部署使用自己 Amane 的实际端口，见 [集成测试说明](scripts/integration/README.md)。修改地址或 Token 后先**保存**，再点**测试连接**；连接正常但鉴权失败时检查 Token。

识别弹窗的外源缩略图还需要浏览器可访问，演员直出图片需要服务器可访问。Docker 内部服务名通常只在容器网络内解析，不能据此认定浏览器也能加载图片；图片路径的具体限制见 [图片 URL 评估](docs/image-url-evaluation.md)。

## 开发

插件结构、API 契约与构建测试命令见 [AGENTS.md](AGENTS.md)。

```powershell
dotnet build Amane.slnx -c Release
dotnet test Amane.slnx -c Release
./scripts/build-release.ps1 all
```

WSL 环境见 [集成测试说明](scripts/integration/README.md)，实际验证记录见 [兼容性验证](docs/compatibility-validation.md)。

## License

见 [LICENSE](LICENSE)。
