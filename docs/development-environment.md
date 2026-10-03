# Win11 .NET 开发环境

更新日期：2026-10-03

## 当前安装

- `dotnet` 命令来自 `C:\Program Files\dotnet\dotnet.exe`，位于当前 PATH。
- .NET SDK：`9.0.318`、`10.0.401`。
- .NET runtime：`Microsoft.NETCore.App 8.0.21`、`9.0.20`、`10.0.12`；ASP.NET Core runtime 为 `9.0.20`、`10.0.12`。
- 未发现用户级 `C:\Users\84422\.dotnet\dotnet.exe`，不需要额外安装。SDK 8 未安装；SDK 10 可构建 `net8.0`，实际 `net8.0` 临时类库构建通过。`net8.0` 控制台程序也已在本机成功运行。
- 依赖还原与以上验证均未设置网络代理；如 NuGet 网络访问失败，可在 PowerShell 当前会话临时使用：

  ```powershell
  $env:HTTP_PROXY = 'http://192.168.1.13:7897'
  $env:HTTPS_PROXY = 'http://192.168.1.13:7897'
  dotnet restore
  ```

  完成后可用 `Remove-Item Env:HTTP_PROXY, Env:HTTPS_PROXY` 清除当前会话设置。没有写入持久代理配置。

## 验证记录

以下为拆分前三个适配器之前的基线记录。最终三适配器构建/80 项测试及真实宿主结果见 [compatibility-validation.md](compatibility-validation.md)；当前构建命令为 `dotnet build Amane.slnx -c Release`，旧根项目路径已迁移到 `src/`。

- `dotnet build -c Release`：成功，0 warning / 0 error。验证时工作树中的主项目目标为 `net10.0`（Jellyfin 12.x 包）；产物为 `bin/Release/net10.0/Jellyfin.Plugin.Amane.dll`。
- `dotnet test tests/Jellyfin.Plugin.Amane.Tests -c Release`：56 passed / 0 failed。测试项目存在一条既有 `CS8604` 可空性警告（`ActorBindingTests.cs:29`）。
- 临时 `net8.0` 类库：使用已安装 .NET SDK 10.0.401 成功还原并构建，0 warning / 0 error。
- 临时 `net8.0` 控制台程序：成功运行并输出 `Hello, World!`，确认 .NET 8 runtime 可用。

## 后续使用

在仓库根目录运行 `dotnet --info` 可查看实际 SDK 选择；直接调用 `dotnet` 即解析到系统安装路径。当前环境可构建并运行 Emby/Core 的 `net8.0` 项目、现有 Jellyfin `net9.0` 或 `net10.0` 项目，以及 `net10.0` 测试项目。使用 `net8.0` 不要求安装 SDK 8；如要避免 SDK 10 默认模板不提供 `net8.0` 模板的影响，可直接将项目的 `<TargetFramework>` 设为 `net8.0`。
