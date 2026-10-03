# 三适配器开发与实测

日期：2026-10-03。原调查文档保留为历史依据；本文记录最终实现与真实宿主结果。

## 实现

- `src/Amane.Core`：net8，无 Jellyfin / Emby 契约依赖；共享 HTTP、DTO、解析、演员缓存、弹性与公共字段转换。
- `src/Amane.Jellyfin`：net9 / Jellyfin SDK 10.11.10。
- `src/Amane.Jellyfin12`：net10 / Jellyfin SDK 12.1，共用 Jellyfin 适配源码、分别编译。
- `src/Amane.Emby`：net8 / MediaBrowser.Server.Core 4.9.1.90；独立 GUID、原生日志桥接、IService 诊断、Emby 配置页和 Provider 契约。

三个适配器均通过 MSBuild Link 编译同一份 Core 源码进各自插件 DLL，安装包各只有一个 DLL。Core 项目保留用于隔离测试，不是部署依赖。Jellyfin 从此前开发包升级须移除旧 Amane.Core.dll。

单 DLL 调整后重新通过 Release 构建（0 警告/错误）、80 项测试及 ZIP 内容检查。两台 Jellyfin 测试容器已移除独立 Core DLL，重新加载插件并通过元数据、演员详情、实际图片字节、识别预览及清除缓存验证；已知手动鉴权选图限制保持原评估结果。

Emby 实测发现宿主从字节加载程序集并立即反射类型，单独部署 Core DLL 无法解析，ModuleInitializer 也晚于类型枚举。因此通过 MSBuild Link 编译同一份 Core 源码进单个 Emby DLL，禁止复制/分叉业务代码。插件构造阶段还未设置程序集属性，客户端延迟创建，避免提前读取 Configuration。IService 的异步诊断使用 Task<object>，Task<T> 曾被宿主识别为 204。

当前 Amane 0.17 API 支持多候选海报/横幅/剧照映射；演员列表卡片与完整详情缓存分开标记，避免有头像但没有简介/生日。Bearer 域内判定使用 URI 的协议、主机、端口和路径边界，避免字符串前缀泄露令牌。

## 环境及验证

Windows SDK 10.0.401；WSL Ubuntu 26.04 / Docker 29.1.3。隔离容器：Emby 4.10.0.40、Jellyfin 10.11.10、Jellyfin 12.1.0。独立 Amane 0.17.0 使用公开上游提交 `b4b5154f28f4f980f4b31dcdf33be63c12ddd52f`，测试数据库只含合成影片、演员与图片。

三宿主均成功加载插件，连接与 Token 验证通过。完成真实影片入库、标题/简介/评分映射、影片 Amane + AmaneId 写入、演员 Amane 数字 ID 绑定、演员详情刷新（简介与生日）、自动影片图片缓存、清除缓存。Emby 配置页经浏览器验证，测试连接和清除缓存按钮可用。

v1.0.8 的 Emby 更名包在隔离宿主加载成功，插件列表版本为 `1.0.8.0`；图标接口 HTTP 200 / image/png，返回字节与根目录 `thumb.png` 完全一致。配置文件名随 DLL 改名，旧配置迁移方式见 README。

图片验证实际读取字节与 Content-Type，三台封面、演员头像和识别预览均 HTTP 200。测试期间替换了不适合容器访问的旧 loopback 演员 URL，并清理隔离库里旧的远程图片引用；仅有 ImageTags 不能证明图片可用。Amane 最新公开 API 样本与探针也已重新采样通过。

单元测试覆盖 Core 和三个适配器共 81 项（含 Emby 包名与内嵌图标回归）；Release 构建与白名单打包检查。运行命令：

```powershell
dotnet build Amane.slnx -c Release
dotnet test Amane.slnx -c Release
./scripts/build-release.ps1 all
python scripts/integration/verify_hosts.py
```

宿主摘要在 [host-observations.json](verification/host-observations.json)，Amane 公开 API 合成样本在 [Amane/current](../Amane/current)。测试凭据仅在本机临时运行目录与隔离容器配置内，未写入仓库。`verify_hosts.py` 需要临时目录里已经初始化的会话，不是无需准备的一键安装脚本。

图片的手动选图、浏览器直出和延迟下载存在真实鉴权约束，详见 [评估报告](image-url-evaluation.md)。Emby 必须开启媒体库提前下载图片。未验证 Emby 4.9 实际服务器、其他平台/CPU 或 Mono 宿主，不能由 SDK 编译成功推断这些平台可用。用户另已反馈在其 Emby 服务器配置正确 Token 后，实际影片成功识别并获取封面和简介。发布状态以 GitHub Releases 为准。
