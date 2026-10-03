# Emby 插件框架 / 工程 / 分发 / 授权 调查报告

> 调查员：teammate `emby-framework`｜共享任务 `task-1`
> 范围：**框架层**（入口类、生命周期、配置页机制、SDK/包、TargetFramework、打包分发、版本兼容、License）。
> Provider API 签名细节由 `emby-provider-api` 负责，本笔记不重复深挖。
> 所有结论均附 [来源](URL) 或本机实测输出代码块。无法确认的显式标注「**需要实机验证**」。

---

## 0. 取证环境（先读，影响所有结论的强度）

### 0.1 本机 .NET SDK：不在全局安装，PATH 上没有

```
=== dotnet --info ===
dotnet : 无法将"dotnet"项识别为 cmdlet、函数、脚本文件或可运行程序的名称。
=== dotnet --list-sdks ===
dotnet : 无法将"dotnet"项识别为 cmdlet、函数、脚本文件或可运行程序的名称。
=== registry dotnet ===            (HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sdk 为空)
=== Test-Path "C:\Program Files\dotnet" ===
False
=== cmd /c "where dotnet" ===
INFO: Could not find files for the given pattern(s).
```

- 系统级 .NET SDK **不存在**（`C:\Program Files\dotnet` 不存在、`where dotnet` 无结果）。
- 但 `%USERPROFILE%\.dotnet` 下有 `10.0.401.dotnetFirstUseSentinel` / `10.0.401.aspNetCertificateSentinel` 等哨兵文件，说明本机历史上跑过 .NET **10.0.401**。
- 实际可用的 SDK 是队友（`emby-provider-api`）在临时目录落地的：

```
$dn = "C:\Users\rappa\AppData\Local\Temp\amane-emby-poc-tools\dotnet\dotnet.exe"
& $dn --version          -> 10.0.401
& $dn --list-sdks        -> 10.0.401 [C:\Users\rappa\AppData\Local\Temp\amane-emby-poc-tools\dotnet\sdk]
& $dn --list-runtimes    -> Microsoft.AspNetCore.App 10.0.12
                            Microsoft.NETCore.App 10.0.12
                            Microsoft.WindowsDesktop.App 10.0.12
```

**推论**：本机只有 .NET 10 SDK/运行时。所有实验均在该 SDK 上完成。**net8.0 / net9.0 的 targeting pack 是通过 NuGet 还原的**（下文实测成功），因此「能否 build」不代表本机能「运行」net8/net9 程序。

### 0.2 本机没有安装 Emby Server

```
=== APPDATA Emby-Server ===            (空)
=== LOCALAPPDATA Emby-Server ===       (空)
=== Program Files Emby-Server ===      (空)
=== ProgramData Emby-Server ===        (空)
=== search *mby* dirs under APPDATA/LOCALAPPDATA/Program Files/ProgramData ===   (空)
=== running emby processes ===         (空)
=== Get-ChildItem C:\ -Recurse -Filter "MediaBrowser.Controller.dll" ===         (空)
```

> ⚠️ 因此**无法**通过本机 Emby Server 的 `programdata/system` 枚举 `MediaBrowser.Controller.dll` / `MediaBrowser.Model.dll` / `MediaBrowser.Common.dll` 的文件版本。
> **替代取证**：改为从 nuget.org 下载官方 SDK 包并直接检查其中的同 DLL（见 §1.2、§2），这比读服务器目录更权威（服务器里的就是这些包的同源产物）。

### 0.3 使用的工具链（自建，可复现）

| 工具 | 路径 | 用途 |
|---|---|---|
| Emby/Jellyfin 反射转储器 | `%TEMP%\emby-fw-probe\refl`（`MetadataLoadContext`） | 逐版本列出导出类型与成员签名 |
| Emby SDK 编译探针 | `%TEMP%\emby-fw-probe\sdktest` | netstandard2.0 + `MediaBrowser.Server.Core` 真实 restore/build |
| 双 SDK 共存探针 | `%TEMP%\emby-fw-probe\dual` | Jellyfin + Emby SDK 同时引用 |
| 单源同编探针 | `%TEMP%\emby-fw-probe\unified`（+ `unified-emby` / `unified-jf`） | 同一份 .cs 分别对两个 SDK 编译 |
| 真实插件快照 | `%TEMP%\emby-fw-probe\repos\ext` | 6 个真实 Emby 插件源码 |

---

## 1. Emby 插件是否通过官方 NuGet SDK 开发？

### 结论

**是——存在官方 NuGet SDK，且是主流做法；但 HintPath 直接引用服务器 DLL 也是真实存在的替代做法。**
两者都不是「必须」，实际项目两种都用。

### 1.1 nuget.org 搜索 API 实测

`https://azuresearch-usnc.nuget.org/query?q=mediabrowser&take=50`（totalHits=13）关键命中：

```
MediaBrowser.Server.Core     | latest=4.9.1.90  | dl=3233744 | verified=False | authors=Emby Team
   Contains core components required to build plugins for Emby Server.
MediaBrowser.Common          | latest=4.9.1.90  | dl=2832602 | verified=False | authors=Emby Team
   Contains common model objects and interfaces used by all Emby solutions.
MediaBrowser.Common.Internal | latest=3.0.680   | ... | "Not intended for plugin developer consumption."
MediaBrowser.ApiClient       | latest=3.1.0     | ... | "Api libraries for connecting to Emby Server."
```

`q=emby&take=50`（totalHits=20）另有官方 `Emby.ApiClient` 4.10.0.40（**verified=True**，用于外部应用访问 REST API，**不是**插件 SDK）。

**判定：插件 SDK = `MediaBrowser.Server.Core`**（包标题 `Emby.Server.Core`，描述明确写 "required to build plugins for Emby Server"）。注意包 ID 是 `MediaBrowser.Server.Core`，不是 `Emby.*`。
[来源](https://azuresearch-usnc.nuget.org/query?q=mediabrowser&take=50) ｜ [nuget 包页](https://www.nuget.org/packages/MediaBrowser.Server.Core)

### 1.2 包 ID / 版本 / 依赖（nuspec 原文 + flatcontainer）

`https://api.nuget.org/v3-flatcontainer/mediabrowser.server.core/index.json` → **965 个版本**，4.x 稳定版：

```
4.2.0.40, 4.3.0.12, 4.3.0.30, 4.4.2, 4.5.0.28, 4.6.0.50,
4.7.0.9, 4.7.0.10, 4.7.0.27, 4.7.0.29, 4.7.0.30, 4.7.1, 4.7.3, 4.7.6, 4.7.7, 4.7.9,
4.8.0.80, 4.8.2, 4.8.5, 4.8.10, 4.8.11,
4.9.1.80, 4.9.1.90,
4.10.0.24-beta, 4.10.0.24-beta2   ← 最新仍是 beta
```

`mediaBrowser.common` 版本号与 `server.core` **完全同步**（同 976 个版本）。

`MediaBrowser.Server.Core` 4.9.1.90 的 nuspec 原文：

```xml
<id>MediaBrowser.Server.Core</id>
<version>4.9.1.90</version>
<title>Emby.Server.Core</title>
<authors>Emby Team</authors>
<owners>ebr,Luke</owners>
<requireLicenseAcceptance>false</requireLicenseAcceptance>
<projectUrl>https://github.com/MediaBrowser/Emby</projectUrl>
<description>Contains core components required to build plugins for Emby Server.</description>
<copyright>Copyright © Emby 2023</copyright>
<dependencies>
  <dependency id="MediaBrowser.Common" version="4.9.1.90" />
</dependencies>
```

> 注意：**nuspec 没有 `<license>` / `<licenseUrl>` 元素**，只有 `requireLicenseAcceptance=false`。这一点在 §7 授权分析里很关键。

`MediaBrowser.Common` 的 nuspec **没有任何 dependency**（叶子包）。

[来源](https://api.nuget.org/v3-flatcontainer/mediabrowser.server.core/index.json)

### 1.3 依赖树（真实 `dotnet restore` + `dotnet list --include-transitive`）

```
项目"sdktest"具有以下包引用
   [netstandard2.0]:
   顶级包                        已请求        已解决
   > MediaBrowser.Server.Core    4.9.1.90     4.9.1.90
   > NETStandard.Library   (A)   [2.0.3, )    2.0.3

   可传递的包                     已解决
   > MediaBrowser.Common         4.9.1.90
   > Microsoft.NETCore.Platforms 1.1.0
```

**依赖极干净**：只有 1 个直接依赖 + 1 个传递依赖。
**关键**：官方 SDK **不会**引入 `Microsoft.Bcl.AsyncInterfaces` 之类的现代 BCL 前置包——这一点在 §6.3 解释 LDAP 插件真实事故时是根因证据。

### 1.4 替代做法：HintPath 直接引用服务器 DLL（真实存在）

`firestaerter3/emby-xtream`（2026 仍活跃）**完全不用 NuGet**：

```xml
<!-- Emby 4.9.x SDK (default Release config) -->
<ItemGroup Condition="'$(Configuration)' != 'Release_4_10'">
  <Reference Include="MediaBrowser.Common"     HintPath="$(MSBuildThisFileDirectory)../lib/emby4_9/MediaBrowser.Common.dll" Private="true" />
  <Reference Include="MediaBrowser.Controller" HintPath="$(MSBuildThisFileDirectory)../lib/emby4_9/MediaBrowser.Controller.dll" Private="true" />
  <Reference Include="MediaBrowser.Model"      HintPath="$(MSBuildThisFileDirectory)../lib/emby4_9/MediaBrowser.Model.dll" Private="true" />
</ItemGroup>
```

[来源](https://github.com/firestaerter3/emby-xtream/blob/main/Emby.Xtream.Plugin/Emby.Xtream.Plugin.csproj)（本机快照：`%TEMP%\emby-fw-probe\repos\ext\xtream\emby-xtream-main\Emby.Xtream.Plugin\Emby.Xtream.Plugin.csproj`）

**因此**：`dotnet add package MediaBrowser.Server.Core` 可用且推荐，但当需要针对**尚未上 NuGet 的服务器版本**或需要绝对控制程序集时，社区会用 HintPath 引用从服务器目录拷出的 DLL。

### 1.5 本机真实还原实验（`dotnet add package` + `dotnet build`）

项目 `%TEMP%\emby-fw-probe\sdktest\sdktest.csproj`（netstandard2.0 + `MediaBrowser.Server.Core` 4.9.1.90），源码实现 `BasePlugin<PluginConfiguration> + IHasWebPages + GetPages()`：

```
############ dotnet restore ############
  正在确定要还原的项目…
  已还原 ...\sdktest.csproj (用时 1.43 秒)。
############ EXIT=0 ############
############ dotnet build -c Release ############
  sdktest -> ...\bin\Release\netstandard2.0\EmbyFwProbe.dll
已成功生成。
    0 个警告
    0 个错误
############ EXIT=0 ############
```

产物只有 `EmbyFwProbe.dll` + `deps.json` + `pdb`——**SDK 程序集不会被拷出**（编译期引用、运行时由服务器提供），这与 Emby「单 DLL 安装」模型一致。

---

## 2. 实际使用的 TargetFramework

### 2.1 SDK 包本身的 TFM：**全部是 `netstandard2.0`**

直接解压 nupkg 看 `lib/` 目录（本机实测）：

```
================= mediabrowser.server.core 4.7.9 =================
lib/netstandard2.0/MediaBrowser.Controller.dll     464896
================= mediabrowser.server.core 4.8.11 =================
lib/netstandard2.0/Emby.Naming.dll                  54784
lib/netstandard2.0/MediaBrowser.Controller.dll     541696
================= mediabrowser.server.core 4.9.1.90 =================
lib/netstandard2.0/Emby.Naming.dll                  56832
lib/netstandard2.0/MediaBrowser.Controller.dll     575488
================= mediabrowser.server.core 4.10.0.24-beta2 =================
lib/netstandard2.0/Emby.Naming.dll                  56832
lib/netstandard2.0/MediaBrowser.Controller.dll     587264
================= mediabrowser.server.core 4.6.0.50 =================
lib/netstandard2.0/MediaBrowser.Controller.dll     435200

================= mediabrowser.common 4.9.1.90 =================
lib/netstandard2.0/Emby.Media.Model.dll             218112
lib/netstandard2.0/Emby.Web.GenericEdit.dll         190976   ← 4.8+ 新增
lib/netstandard2.0/MediaBrowser.Common.dll           50688
lib/netstandard2.0/MediaBrowser.Model.dll           481280
```

**结论**：从 4.6 到 4.10 beta，**Emby 插件 ABI 一直是 `netstandard2.0`**。4.8 起 `MediaBrowser.Common` 额外带上 `Emby.Web.GenericEdit.dll`（新式声明式 UI 的基座）。

### 2.2 真实插件 csproj 的 TargetFramework（7 个真实证据）

| # | 项目 | TargetFramework | SDK 引用方式 | 备注 |
|---|---|---|---|---|
| 1 | `MediaBrowser/trakt`（**Emby 官方**插件） | `netstandard2.0` | `mediabrowser.server.core` **4.2.0.6-beta4** | 官方插件用的 SDK 版本极老也不影响 |
| 2 | `MediaBrowser/Emby.AutoOrganize`（**Emby 官方**） | `netstandard2.0` | `mediabrowser.server.core` **4.8.0.13-beta** | 多 HTML+JS 配置页 |
| 3 | `JavScraper/Emby.Plugins.JavScraper`（JAV 刮削，最贴近 Amane） | `netstandard2.1` | `MediaBrowser.Server.Core` **4.6.0.50-\***（Emby 配置）/ `Jellyfin.Controller` 10.4-\*（Jellyfin 配置，`__JELLYFIN__` 宏） | **单仓库双目标**的早期范式 |
| 4 | `metatube-community/jellyfin-plugin-metatube`（2025 活跃，Jellyfin+Emby 双支持） | Jellyfin 配置 `net9.0` / **Emby 配置 `net8.0`** | `Jellyfin.Controller` 10.11.0 / **`MediaBrowser.Server.Core` 4.9.1.80** | 用 `Configuration` 区分：`Debug;Release;Debug.Emby;Release.Emby` |
| 5 | `firestaerter3/emby-xtream`（2026 活跃） | `netstandard2.0` | **HintPath 引用服务器 DLL**（`lib/emby4_9`、`lib/emby4_10`） | `LangVersion 7.3` |
| 6 | `fengymi/emby-plugin-danmu` | `netstandard2.0` | `MediaBrowser.Server.Core` **4.8.5** | 弹幕插件 |
| 7 | `cloud-fs/clouddrive-mediaserver-plugin` | （未取到 csproj，见下） | — | 仅用于生态佐证 |

**结论**：
- **`netstandard2.0` 是最广泛、最安全的选择**（官方两个插件 + 2 个社区插件都用它），因为 Emby 4.7/4.8/4.9/4.10 的 SDK 都是 netstandard2.0。
- `netstandard2.1`（JavScraper）也真实存在。
- **针对特定服务器版本**可以用 `net8.0`（metatube 的 Emby 配置，对应 Emby 4.9 = .NET 8，见 §6.1）——但这会**丧失对 4.8（.NET 6）的兼容**。
- 目前**没有任何真实插件用 `net9.0`/`net10.0` 面向 Emby**；`net9.0` 在双支持项目里是留给 Jellyfin 的。

### 2.3 本机实测：netstandard2.0 包可被哪些 TFM 消费

对同一个 `sdktest` 项目切换 TFM 逐个 restore+build：

```
############ TFM=net8.0 ############   已还原 …  已成功生成。  0 个警告  0 个错误   EXIT=0
############ TFM=net9.0 ############   已还原 …  已成功生成。  0 个警告  0 个错误   EXIT=0
############ TFM=net10.0 ############  已还原 …  已成功生成。  0 个警告  0 个错误   EXIT=0
```

**结论**：`MediaBrowser.Server.Core` 4.9.1.90 可被 netstandard2.0 / net8.0 / net9.0 / net10.0 全部消费。**编译期无 TFM 障碍，风险在运行期（见 §6）。**

---

## 3. 插件入口与生命周期

### 3.1 SDK 真实 API 面（`MediaBrowser.Common.dll` 4.9.1.90 反射转储，本机）

```
### ASSEMBLY MediaBrowser.Common v4.9.1.90
### TFM attr: .NETStandard,Version=v2.0

abstract class MediaBrowser.Common.Plugins.BasePlugin : System.Object
        + MediaBrowser.Common.Plugins.IPlugin, MediaBrowser.Common.Plugins.IPluginAssembly
      meth PluginInfo GetPluginInfo()
      meth String GetPluginPageUrl(String name)
      meth Void OnUninstalling()
      meth Void SetAttributes(String assemblyFilePath, String dataFolderPath, Version assemblyVersion)
      meth Void SetId(Guid assemblyId)
      prop String AssemblyFilePath { get; set; }
      prop String DataFolderPath { get; set; }
      prop String Description { get; }
      prop Guid Id { get; set; }
      prop String Name { get; }
      prop Version Version { get; set; }

abstract class MediaBrowser.Common.Plugins.BasePlugin`1 : MediaBrowser.Common.Plugins.BasePlugin
        + IPlugin, IPluginAssembly, IHasPluginConfiguration
      meth PluginInfo GetPluginInfo()
      meth Void SaveConfiguration()
      meth Void SetStartupInfo(Action`1 directoryCreateFn)
      meth Void UpdateConfiguration(BasePluginConfiguration configuration)
      prop TConfigurationType Configuration { get; set; }
      prop String ConfigurationFileName { get; }
      prop String ConfigurationFilePath { get; }
      prop Type ConfigurationType { get; }
      prop Boolean IsFirstRun { get; set; }

interface MediaBrowser.Common.Plugins.IPlugin
      meth PluginInfo GetPluginInfo()
      meth Void OnUninstalling()
      prop String AssemblyFilePath { get; }
      prop String DataFolderPath { get; }
      prop String Description { get; }
      prop Guid Id { get; }
      prop String Name { get; }
      prop Version Version { get; }
```

### 3.2 关键结论：**`IPlugin` / `BasePlugin<T>` 里没有 `Load` / `Initialize` / `GetPages`**

- **入口类**：`public class Plugin : BasePlugin<PluginConfiguration>`，构造函数注入 `(IApplicationPaths, IXmlSerializer)`。
- **没有 `Load()`、没有 `Initialize()`**。需要「启动时干活」时用**另一个类**实现 `IServerEntryPoint`（`Run()`），由容器自动发现装配（Automatic Type Discovery）。
- **`GetPages()` 不在入口类接口里**——它在单独的 `IHasWebPages`（见 §4）。
- 官方文档印证：「you will need an entrypoint that can initialize and accept the various dependencies … This is done by creating a class that implements the `IServerEntryPoint` interface.」
  [来源](https://dev.emby.media/doc/plugins/dev/index.html)

真实例证（官方 Trakt 插件）——**一个 DLL 里两个类**：

```csharp
// Trakt/Plugin.cs
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
{
    public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer) : base(appPaths, xmlSerializer) { Instance = this; }
    ...
}
// Trakt/ServerMediator.cs
public class ServerMediator : IServerEntryPoint   // ← 真正干活的入口
```

[来源](https://github.com/MediaBrowser/trakt/blob/master/Trakt/Plugin.cs) ｜ [ServerMediator.cs](https://github.com/MediaBrowser/trakt/blob/master/Trakt/ServerMediator.cs)

### 3.3 生命周期相关接口（4.9.1.90 `MediaBrowser.Controller` 反射）

```
interface MediaBrowser.Controller.Plugins.IServerEntryPoint + System.IDisposable
      meth Void Run()
interface MediaBrowser.Controller.Plugins.IRunBeforeStartup
interface MediaBrowser.Controller.Plugins.IHasSetupUrl
      prop String SetupUrl { get; }
interface MediaBrowser.Controller.Plugins.IPluginUIPagesRegistrar
      meth IList`1 GetPluginUIPageRegistrations()
      meth Boolean RegisterPageController(IPlugin plugin, IPluginUIPageController pluginUIPageController)
```

### 3.4 配置模型与保存流程

- 配置类继承 `MediaBrowser.Model.Plugins.BasePluginConfiguration`。
- 保存：`BasePlugin<T>.SaveConfiguration()`；外部改配置：`UpdateConfiguration(BasePluginConfiguration)`。
- 配置落盘位置：`BasePlugin<T>.ConfigurationFilePath` / `ConfigurationFileName`（构造函数通过 `IApplicationPaths` 注入路径）。
  本机编译通过 `base(applicationPaths, xmlSerializer)` 并访问 `ConfigurationFilePath` / `ConfigurationFileName` / `SaveConfiguration()`，0 错误（见 §8.2 的单源同编实验）。
- 配置序列化器为 `IXmlSerializer`（**XML**，不是 JSON）——注意与 Jellyfin 的 `IXmlSerializer` 同名同义。

### 3.5 Selector 差异速查表（Emby 4.9 框架层）

| 能力 | Emby | Jellyfin 10.11 | 兼容性 |
|---|---|---|---|
| 入口基类 | `MediaBrowser.Common.Plugins.BasePlugin<T>` | 同名同命名空间 | ✅ 完全一致 |
| 插件接口 | `MediaBrowser.Common.Plugins.IPlugin` | 同名同命名空间 | ✅ 完全一致 |
| 配置基类 | `MediaBrowser.Model.Plugins.BasePluginConfiguration` | 同名同命名空间 | ✅ 完全一致 |
| 配置页 | `MediaBrowser.Model.Plugins.IHasWebPages.GetPages()` | **同名同命名空间同签名** | ✅ 见 §8.2 |
| `Load` / `Initialize` | **不存在** | **不存在**（Jellyfin 也没有） | ✅ 一致 |
| DI 注册 | `IServerEntryPoint` + 自动类型发现 | `IPluginServiceRegistrator`（本仓库 `ServiceRegistrator.cs`） | ❌ **不同** |
| `System.ServiceModel` 式注册 | 无 | 无 | — |

> ⚠️ **`IPluginServiceRegistrator` 在 Emby 中不存在**（对 `MediaBrowser.Server.Core` + `MediaBrowser.Common` 全量导出类型做过检索，零命中）。本仓库 `ServiceRegistrator.cs` 需要为 Emby 换成 `IServerEntryPoint`（构造函数注入）或直接由 `Plugin` 构造函数持有。

---

## 4. 配置页机制

### 结论

| 机制 | 形态 | 官方文档 | 真实插件使用 |
|---|---|---|---|
| **A. 嵌入式 HTML + JS**（经典，等价 Jellyfin `IHasWebPages`） | `.html`/`.js` 作为 `EmbeddedResource`，通过 `IHasWebPages.GetPages()` 返回 `PluginPageInfo{Name, EmbeddedResourcePath}` | 有（"Typical Approach" 历史段） | Trakt / AutoOrganize / danmu / JavScraper / Xtream |
| **B. `IPluginConfigurationPage`** | 实现 `GetHtmlStream()` 返回 HTML 流 | SDK 里有接口 | 未见于本次采样的真实插件 |
| **C. 声明式 Generic UI**（无需 HTML/JS） | `BasePluginSimpleUI<TOptions>` + `EditableOptionsBase` 属性 + `[DisplayName]`/`[Description]`/`[Required]` | 有，官方主推 | metatube（`Plugin : BasePluginSimpleUI<PluginConfiguration>`） |

### 4.1 IHasWebPages 确实存在于 Emby（反射实测，跨 3 个版本都存在）

```
interface MediaBrowser.Model.Plugins.IHasWebPages
      meth IEnumerable`1 GetPages()

class MediaBrowser.Model.Plugins.PluginPageInfo : System.Object
      ctor ()
      prop String DisplayName { get; set; }
      prop String EmbeddedResourcePath { get; set; }
      prop Boolean EnableInMainMenu { get; set; }      ← 4.8+ 新增
      prop Boolean EnableInUserMenu { get; set; }      ← 4.8+ 新增
      prop String FeatureId { get; set; }              ← 4.8+ 新增
      prop Boolean IsMainConfigPage { get; set; }
      prop String MenuIcon { get; set; }
      prop String MenuSection { get; set; }
      prop String Name { get; set; }
```

> `PluginInfo`（`MediaBrowser.Model.Plugins.PluginInfo`）在 4.7/4.8/4.9 三版**完全一致**：
> `ConfigurationFileName / Description / Id / ImageTag / Name / Version`（都是 `string`，含 `{ get; set; }`）。

### 4.2 官方 Trakt 插件的配置页注册（逐字原文）

```csharp
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
{
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "Trakt",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "traktjs",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
            }
        };
    }
}
```

csproj 侧对应：

```xml
<ItemGroup>
  <None Remove="Configuration\configPage.html" />
  <None Remove="thumb.png" />
</ItemGroup>
<ItemGroup>
  <EmbeddedResource Include="Configuration\configPage.html" />
  <EmbeddedResource Include="thumb.png" />
</ItemGroup>
```

**这与本仓库 Jellyfin 侧写法（`Plugin.cs` 的 `IHasWebPages` + 内嵌 `Configuration\configPage.html`）在结构上完全同构**，只是命名约定不同：Emby 习惯把 `.js` 也注册成一个 page（`Name = "traktjs"`）。

[来源](https://github.com/MediaBrowser/trakt/blob/master/Trakt/Plugin.cs) ｜ [Trakt.csproj](https://github.com/MediaBrowser/trakt/blob/master/Trakt/Trakt.csproj)

**更多真实证据**（本机快照 `%TEMP%\emby-fw-probe\repos\ext`）：

| 插件 | 配置页文件 | csproj 打包方式 |
|---|---|---|
| Trakt（官方） | `Configuration/configPage.html` + `configPage.js` | `EmbeddedResource` |
| Emby.AutoOrganize（官方） | `autoorganizelog/smart/movie/tv.html` + 对应 `.js` + `fileorganizer.template.html` + `thumb.jpg` | 全部 `EmbeddedResource` |
| emby-plugin-danmu | `Configuration/configPage.html` + `config.js` | `EmbeddedResource` |
| JavScraper | `Configuration/ConfigPage.html` + `JavOrganizationConfigPage.html`（+ Jellyfin 变体） | `EmbeddedResource` |
| emby-xtream | `Configuration/Web/**` | `<EmbeddedResource Include="Configuration\Web\**" />` |

### 4.3 官方对「旧做法」的自我描述（重要，说明 HTML 页会随服务器 UI 变更而碎）

> 「Creating user interfaces for Emby Server plugins has been quite a complex and tedious task. It was required to create API endpoints … create custom HTML files … use the same HTML elements and CSS classes, **none of which were ever documented and which are subject to frequent changes**. … Plugins got broken quite too often, either visually or sometimes even functionally due to breaking changes in Emby Server.」

→ 官方因此推出 Generic UI / Simple UI：**"it is impossible for a developer to affect the UI presentation"**，UI 由服务器按 ViewModel 结构自动生成。
[来源](https://dev.emby.media/doc/plugins/ui/index.html)

### 4.4 声明式 UI（Simple UI）要点

- 让插件类继承 `MediaBrowser.Controller.Plugins.BasePluginSimpleUI<TOptionType>`（SDK 实测存在：`GetOptions` / `SaveOptions` / `OnBeforeShowUI` / `OnOptionsSaving`）。
- 另建一个继承 `Emby.Web.GenericEdit.EditableOptionsBase` 的选项类，属性 + `[DisplayName]` / `[Description]` / `[Required]` / `[EditFolderPicker]` 特性。
- **不需要 HTML / JS**；用户点插件右键菜单 "Settings" 即自动生成页面。
- 回调：`OnBeforeShowUI`（展示前调整）、验证特性（首选）、`OnOptionsSaving`（可取消保存）、`OnOptionsSaved`（保存后通知）；读写用 `GetOptions()` / `SaveOptions()`。
- 想出现在导航栏：覆写 `OnCreatePageInfo()` 调整 `PluginPageInfo`。
[来源](https://dev.emby.media/doc/plugins/ui/simpleui.html)

> ⚠️ **`Emby.Web.GenericEdit.dll` 只从 Emby 4.8 起随 `MediaBrowser.Common` 提供**（4.7.9 包里没有），所以**方案 C 无法支持 Emby 4.7**。方案 A（HTML+JS）在 4.7/4.8/4.9 全可用。

### 4.5 需要实机验证

- `IPluginConfigurationPage` 的**实际注册方式**（它是接口，但谁去实现一个 provider 把它暴露给服务器？本次未在任何真实插件或官方示例中找到落地代码）。**需要实机验证**。
- 配置页 JS 里可用的 Emby 前端全局对象（`Dashboard`、`ApiClient` 等）与调用约定：官方明说 "none of which were ever documented"。**需要实机验证**（本仓库 Jellyfin 侧用 `Api/AmaneDiagnosticsController` 规避浏览器直连，这个思路在 Emby 上同样适用且更稳）。

---

## 5. 安装 / 打包 / 分发

### 5.1 插件目录与安装形态

- **单 DLL 安装**是官方支持且被官方人员明确认可的方式。
- Windows 路径：`%AppData%\Emby-Server\programdata\plugins\`
- Linux 裸机：`/var/lib/emby/plugins/`
- Docker：`/config/plugins/`

官方开发文档的 Post-Build 事件示例（逐字）：

```xml
<Target Name="PostBuild" AfterTargets="PostBuildEvent">
  <Exec Command="copy $(TargetPath) %AppData%\Emby-Server\programdata\plugins\" />
</Target>
```
[来源](https://dev.emby.media/doc/plugins/dev/index.html)

**Emby 官方管理员 Luke（2025-09-06）在本仓库关心的问题上直接答复**：

> 用户问：如何在 Emby 里添加自定义插件仓库（plugins not listed）？
> Neminem：「TBH I have never seen a way to import a external source into Emby. That Being said, you can upload plugins to Emby plugin server folder. But side loaded plugins are sometimes not reliable.」
> **Luke（Emby 管理员）：「You can copy the DLL into your plugins folder manually.」**
[来源](https://emby.media/community/topic/142301-emby-running-in-docker-how-to-add-catalogrepositories/#findComment-1467730)

**→ 手动安装是官方支持的一等公民路径**（但 Emby 官方不为其背书，见下）。

### 5.2 Emby **没有** Jellyfin 那样的「用户可订阅第三方插件仓库」

- Jellyfin：`manifest.json` 可被用户作为 repository URL 添加，用户端自行刷新。
- **Emby：没有这种机制**。上面论坛帖子里 Emby 管理员确认的只有「手动拷 DLL」。本次实测也**未能找到**任何公开的 catalog JSON 端点：

```
https://www.mb3admin.com/data/plugin/plugins.json   -> ERR (404)
https://www.mb3admin.com/data/plugins.json          -> ERR (404)
https://plugins.emby.tv/packages.json               -> ERR (404)
https://plugins.emby.tv/manifest.json               -> ERR (404)
https://www.mb3admin.com/data/plugin/Plugins.json   -> ERR (404)
```

> **需要实机验证**：Emby Server 实际请求的 catalog JSON URL 与线上字段名。它不是公开文档化的，且 catalog 后台 `https://plugins.emby.tv/admin/packages.html` 需要开发者账号登录。

### 5.3 进官方 catalog 的流程（官方文档逐字要点）

> 「First, create an introduction/beta testing thread in the plugins forum … Then, **request a developer id from the Emby team by sending a PM to ebr or posting in the dev forum** … Once you have your credentials you'll need to go to `https://plugins.emby.tv/admin/packages.html` and login … go to the "Packages" area.」
[来源](https://dev.emby.media/doc/plugins/dev/Getting-your-plug-in-in-the-catalog.html)

**Package 级字段（官方文档逐字）**：

| 字段 | 含义 |
|---|---|
| Name | catalog 显示名 |
| GUID | **程序集唯一 id，从 assemblyinfo.cs 粘贴**，用于识别更新 |
| Short Description / Overview / Website | 描述与主页 |
| Thumb Image | 目录页缩略图，**16:9 瓦片** |
| Preview Image | 详情页顶部大图 |
| Target System | 安装目标系统（Server / MB Classic / MB Theater） |
| Package Type | 插件选 `Userinstalled` |
| Category / Tile Color | 分类 / 瓦片底色 |
| **Target File Name** | **安装时落到 plugins 目录的文件名，应为 DLL 名（含 `.dll`）** |
| Premium Plug-in / Feature ID / Price / Registration Information | 付费插件相关（付费插件自动 14 天试用） |

**Version 级字段（官方文档逐字）**：

| 字段 | 含义 |
|---|---|
| **Version** | **必须与 DLL 通过 AssemblyInfo 上报的版本号完全一致** |
| **Version Class** | 更新通道：`Dev` / `Beta` / `Release`（用户只看到自己设定级别的版本） |
| Description | 该版本的更新日志（显示在 catalog 变更记录区） |
| **Required Version** | **= Jellyfin `targetAbi` 的等价物**：目标系统最低版本。可写部分版本（如 `3.0.4890`）以匹配「该前缀以上全部」 |

> ⚠️ **上传物是 DLL**：文档原文「Then select the dll file from your local computer and save. This will upload the dll and make it available in the plug-in catalog.」——**官方 catalog 走单 DLL**，不是 zip。

### 5.4 版本号格式要求

- Emby 插件版本是 **4 段式 `System.Version` 风格**（受 .NET `AssemblyVersion` 约束），且 **catalog 的 Version 必须与 DLL 自报版本严格相等**。
- 生态里的真实做法：
  - JavScraper：`<Version>1.$([System.DateTime]::Now.ToString(yyyy.MMdd.HHmm))</Version>`（时间戳版本）
  - metatube：`<Version>$([System.DateTime]::UtcNow.ToString(yyyy.Mdd.Hmm.0))</Version>`
  - Xtream：`git describe` 派生，tag `v1.2.0` → `1.2.0`；tag 之后 N 个提交 → `1.2.0.N`（ADR 提到实际发布过 `Version 1.4.96.0`）
- 对比本仓库 Jellyfin 侧：`<Version>1.0.7</Version>` + `manifest.json` 三键版本——**Jellyfin 用 `manifest.json` 声明版本，Emby 用 DLL 自报版本**，两者的单一事实源不同，双支持时要注意别让两者漂移。

### 5.5 Emby 官方对侧载的官方立场（值得写进 README 风险提示）

> Neminem：「But side loaded plugins are sometimes not reliable. And please only use plugins, that state … That they work with Emby. When you do this, it's out of Emby's control and dev's might not able to help you.」
[来源](https://emby.media/community/topic/142301-emby-running-in-docker-how-to-add-catalogrepositories/#findComment-1467705)

**→ 分发建议**：把「手动放 DLL 到 plugins 目录」作为主路径写清楚（因为这是官方唯一支持的自助路径），并把「DLL 与服务器版本对应关系」写进 release notes。

---

## 6. 版本兼容性风险

### 6.1 运行时版本演进（官方一手证据）

| Emby 版本 | 运行时 | 证据 |
|---|---|---|
| 4.8.x | **.NET 6** | Emby 4.8.10.0 服务器日志逐字：`Framework: .NET 6.0.31` [来源](https://emby.media/community/topic/133644-ldap-1044-update-causes-could-not-load-file-or-assembly/#findComment-1399597) |
| 4.9.x | **.NET 8** | Emby 管理员 Luke，2025-09-10：「HI, the upcoming 4.9 server release has updated to dotnet 8.」[来源](https://emby.media/community/topic/142389-net-core-version-upgrade/#findComment-1468633) |
| 4.10.x | .NET 8（推断） | 官方 SDK 仍 netstandard2.0；社区 ADR 称 4.10 需要 v8 的 `System.Text.Json`/`System.IO.Pipelines` [来源](https://github.com/firestaerter3/emby-xtream/blob/main/docs/decisions/017-single-dll-for-emby-49-and-410.md) —— **4.10 具体运行时需要实机验证**（4.10 尚在 beta，最新 NuGet 为 `4.10.0.24-beta2`） |

**→ 关键影响**：4.7/4.8（.NET 6）与 4.9+（.NET 8）之间的差异**不在插件 ABI（都是 netstandard2.0），而在插件带进来的第三方依赖**。插件若引用 8.0 时代的 BCL 前置包，在 .NET 6 服务器上会加载失败。

### 6.2 SDK 导出类型的真实删减（本机反射逐版本 diff）

对 `MediaBrowser.Controller` / `MediaBrowser.Common` / `MediaBrowser.Model` 三个程序集，用 `MetadataLoadContext` 导出全部 public 类型后逐版本求差：

**导出类型数量**

| 程序集 | 4.7.9 | 4.8.11 | 4.9.1.90 |
|---|---|---|---|
| MediaBrowser.Controller | 330 | 389 | 398 |
| MediaBrowser.Common | 41 | 43 | 44 |
| MediaBrowser.Model | 397 | 418 | 431 |

**4.7.9 → 4.8.11 被删除的类型**

```
MediaBrowser.Controller.Entities.IMetadataContainer
MediaBrowser.Controller.Library.IDirectStreamProvider
MediaBrowser.Controller.Notifications.INotificationService
MediaBrowser.Controller.Notifications.INotificationTypeFactory
MediaBrowser.Model.Diagnostics.IProcessFactory
MediaBrowser.Model.Logging.IConsoleLogger
```
（新增 38 个 Controller 类型 / 20 个 Model 类型）

**4.8.11 → 4.9.1.90 被删除的类型**

```
MediaBrowser.Controller.MediaEncoding.IMediaImageConverter
MediaBrowser.Controller.Providers.IMetadataService
MediaBrowser.Controller.Resolvers.IMultiItemResolver
```

> 📌 **官方文档已经过期**：开发文档仍在推荐实现 `MediaBrowser.Model.Diagnostics.IProcessFactory`，但该接口**在 4.8 已被删除**。
> [来源](https://dev.emby.media/doc/plugins/dev/index.html)（文档）vs 本机反射 diff（事实）。**不要照抄官方文档的接口清单。**
> 📌 `INotificationService` / `INotificationTypeFactory` 在 4.8 被删除，与论坛帖「4.8 Server - New Notification API's for Plugins」一致。
> [来源](https://emby.media/community/topic/114611-48-server-new-notification-apis-for-plugins/)

### 6.3 真实破坏性事故：LDAP 插件 4.8 上 `Could not load file or assembly`（2024-11）

服务器日志逐字：

```
Version: 4.8.10.0
Framework: .NET 6.0.31
System.IO.FileNotFoundException: Could not load file or assembly
  'Microsoft.Bcl.AsyncInterfaces, Version=8.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51'.
    at LDAP.AuthenticationProvider.Authenticate(String username, String password)
```

结局：Luke 让作者发 1.0.45，用户确认修复。
[来源](https://emby.media/community/topic/133644-ldap-1044-update-causes-could-not-load-file-or-assembly/)

**根因（本机实测交叉验证）**：官方 SDK 的依赖树**极干净**（只有 `MediaBrowser.Common` + `Microsoft.NETCore.Platforms`，见 §1.3），所以 `Microsoft.Bcl.AsyncInterfaces 8.0.0.0` **不是 SDK 带来的**，而是插件自己引入的 8.0 时代依赖。在 .NET 6 宿主里没有 8.0 版程序集 → 加载失败。

**这是双支持最大的现实风险**：Amane 现在完全没有第三方依赖（AGENTS.md「不新增第三方依赖」），这个约束恰好是 Emby 兼容性的最大护城河，**必须保持**。

### 6.4 真实破坏性事故：Emby 4.10 给 `ILiveStream` 加接口成员（2026-09）

Xtream 插件 ADR-017（2026-09-27）逐字记录：

```
Emby 4.10.0.17 added AddConsumer(string) and RemoveConsumer(string) to ILiveStream.
...
Emby 4.10.0.40 then became the public stable release. Users whose server updated itself
were left with the 4.9 DLL, which fails to load:

Method 'AddConsumer' in type 'Emby.Xtream.Plugin.Service.XtreamLiveStream' from assembly
'Emby.Xtream.Plugin, Version=1.4.96.0' does not have an implementation.
```

**该类破坏的机制**：netstandard2.0 插件针对 4.9 SDK 编译；4.10 给接口**加了成员**；运行时 CLR 做接口映射时发现实现类缺少该成员 → `TypeLoadException`。这是**接口增员就是二进制破坏性变更**的经典案例，且 4.9→4.10 是**自更新跨线**，用户无从选择。

**该 ADR 给出的解法（可直接借鉴）**：
1. 把新接口成员**编译进所有构建**（在 4.9 上它们只是普通未用方法，运行时不会去匹配 → 无害；在 4.10 上被正确匹配）。
2. 只发布一份 DLL（4.9 SDK 编译），用资产名冗余兜住旧更新路径。
3. 引入 `scripts/sdk-load-check`：**把插件 DLL 对着某个 SDK 目录加载**（只从该目录解析 `MediaBrowser.*`），加载每个类型并对每个方法做 JIT 编译；缺接口成员会在类型加载阶段失败，SDK 成员被删/改签名会在 JIT 阶段失败。ADR 列出实测表格：209 types / 1241 methods / 0 failures（4.9 SDK 与 4.10 SDK 各一次）。
[来源](https://github.com/firestaerter3/emby-xtream/blob/main/docs/decisions/017-single-dll-for-emby-49-and-410.md)

**→ 对本仓库的直接启发**：如果决定做 Emby 支持，**强烈建议把等价的 `sdk-load-check` 加进 CI**：下载 `MediaBrowser.Server.Core` 4.7.9 / 4.8.11 / 4.9.1.90 三个 SDK 目录，把构建产物对着每个目录做「加载全部类型 + JIT 全部方法」。这是唯一能在没有 Emby 服务器的情况下抓住跨版本绑定失败的自动化手段。

### 6.5 框架层逐版本成员稳定性（本机反射比对）

| 类型 | 4.7.9 | 4.8.11 | 4.9.1.90 | 结论 |
|---|---|---|---|---|
| `MediaBrowser.Model.Plugins.IHasWebPages` | `GetPages()` | 同 | 同 | ✅ **三版零变更** |
| `MediaBrowser.Model.Plugins.PluginInfo` | 6 个 string 属性 | 同 | 同 | ✅ **三版零变更** |
| `MediaBrowser.Controller.Plugins.IPluginConfigurationPage` | 4 成员 | 同 | 同 | ✅ **三版零变更** |
| `MediaBrowser.Common.Plugins.IPlugin` | 8 成员 | 同 | 同 | ✅ **三版零变更** |
| `MediaBrowser.Common.Plugins.BasePlugin<T>` | 成员同 | 同 | 同 | ✅ 三版一致 |
| `MediaBrowser.Model.Plugins.PluginPageInfo` | 4 属性 | +`EnableInMainMenu` / `EnableInUserMenu` / `FeatureId` | 同 4.8 | ⚠️ 4.8 加 3 属性（**纯增加，不破坏**） |
| `IPluginUIPagesRegistrar` | **不存在** | 存在 | 存在 | ⚠️ 4.8 才有（新式 UI 需要 4.8+） |
| `Emby.Web.GenericEdit.dll` | **不存在** | 存在 | 存在 | ⚠️ 4.8 才有（声明式 UI 需要 4.8+） |

**→ 如果只走 `BasePlugin<T> + IHasWebPages + PluginPageInfo{Name, EmbeddedResourcePath}` 这条最保守的路，框架层在 4.7.9 / 4.8.11 / 4.9.1.90 之间是零破坏的。**

### 6.6 Jellyfin 12.x 的对照风险

AGENTS.md 指出本仓库需为 Jellyfin 12 单独适配。Emby 侧没有等价的「大版本重写」信号：SDK 的 ABI 从 4.6 到 4.10 beta **一直是 netstandard2.0**，框架层接口 §6.5 显示 4.7→4.9 零破坏。**Emby 侧的兼容性风险主要来自（a）运行时 .NET 6→8、（b）接口增员、（c）UI 前端约定漂移，而不是 ABI 断裂。**

---

## 7. License / 授权

### 7.1 结论速览

| 问题 | 结论 |
|---|---|
| Emby 插件 SDK 有开源许可吗？ | **没有任何许可声明**。NuGet nuspec 无 `<license>` / `<licenseUrl>`；`requireLicenseAcceptance=false`；`projectUrl` 指向 `https://github.com/MediaBrowser/Emby` |
| Emby Server 是开源的吗？ | **否**。ToS 明确禁止反向工程/修改/衍生作品（除法定例外与 LGPL/GPL2 调试例外） |
| 第三方独立开发插件被允许吗？ | **事实上允许**（官方有完整插件开发文档 + 模板 + catalog 提交流程），但**没有任何书面许可条款明确授予该权利**——属于「官方鼓励但不落纸面」 |
| 需要签 NDA / 开发者协议吗？ | **本次调查未发现任何 NDA 或书面开发者协议**。只有「PM ebr 申请 developer id」这一轻量流程。**需要实机验证**（账号开通时是否附带条款） |
| 官方示例代码的许可 | 明确声明「**There are no licenses attached to the code and developers are free to decide how to make use of the provided code examples.**」 |

### 7.2 nuget 包无许可（本机实测 nuspec 原文）

```xml
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>MediaBrowser.Server.Core</id>
    <version>4.9.1.90</version>
    <requireLicenseAcceptance>false</requireLicenseAcceptance>
    <projectUrl>https://github.com/MediaBrowser/Emby</projectUrl>
    <copyright>Copyright © Emby 2023</copyright>
```

- **无 `<license type="expression">` / `<licenseUrl>`**。按 NuGet 约定，未声明许可 = 用户**没有被授予**明确的使用/再分发权利（默认版权保留）。
- `MediaBrowser.Common` 4.7.9/4.8.11/4.9.1.90 同样无许可声明。
- `projectUrl` 指向的 `https://github.com/MediaBrowser/Emby` 是 **releases-only 仓库**，**不含服务器源码**（服务器自 2018 年起闭源）。
  - 实测：`Invoke-WebRequest https://github.com/MediaBrowser/Emby` → `HTTP 200`，页面标题为 `GitHub - MediaBrowser/Emby: Emby Server is a personal media server with apps on just about every device.`，即仓库存在但只放 release 产物。
  - 对照：`https://github.com/MediaBrowser/Emby.Plugins` 亦存在（2018 年后基本停滞，`pushed_at=2018-06-20`）。

### 7.3 Emby ToS 原文（关键条款逐字，2026-08-16 版）

> **3. Intellectual Property**
> **(b)** For any Software made available to you to download through the website … Emby grants you a **personal, non-commercial, worldwide, royalty-free, revocable, non-transferable, non-sublicensable, and non-exclusive license to use the software** provided to you by Emby ("Software"). This license is for the sole purpose of enabling you to use and enjoy the benefit of the Software in the manner permitted by these Terms.
>
> **(d)** Except as provided in this section, you may not, or allow anyone else to, directly or indirectly to: (1) copy, modify, distribute, sell, or lease any part of the Software; (2) **reverse engineer, disassemble, decompile, or otherwise attempt to discover the source code or structure, sequence, and/or organization of all or any part of the Software**, unless laws prohibit those restrictions or **you have our written permission**; (3) rent, lease, or use the Software for timesharing or service bureau purposes; (4) **develop any improvement, modification, or derivative works of the Software, or include any portion thereof in any other product, software, work, equipment, or item** …
>
> **(g)** You may reverse engineer the Software solely (1) as permitted by applicable law, or (2) **for the purpose of debugging modifications made by you to certain third party files in source code format that are licensed under the GNU Lesser General Public License (LGPL) or under the GNU General Public License version 2 (GPL2)** and only provided that you have made, prior to any such reverse engineering permitted under this sentence, unsuccessful prior reasonable good faith efforts to debug such modifications using techniques other than the reverse engineering of the Software.
>
> **(h)** As between the parties … **Emby shall own all title, ownership rights, and intellectual property rights in and to the Software**, and any copies or derivative works thereof …
>
> 页脚：**© 2026 Emby LLC** … Downloading any Emby software constitutes acceptance of these terms.

[来源](https://emby.media/terms.html)

**分析（区分事实与判断）**：

- **事实**：许可范围是「personal, non-commercial」，且是「use the Software」，**没有**授予「开发/分发插件」的权利；§3(d)(4) 禁止衍生作品。
- **事实**：引用 `MediaBrowser.Controller.dll` 等**编译期程序集**做插件开发，是 Emby 官方文档**主动指导**的行为（官方提供模板、文档、catalog 提交入口），因此在「官方意图」层面显然被允许。
- **判断（非事实）**：单独发布的插件程序集（只引用公共 API、不包含 Emby 代码）通常不被视为 Software 的衍生作品；但**ToS 没有明文豁免**，且 §3(h) 把衍生作品权利归 Emby。这属于**需要法务意见的灰区**，不应由工程判断下结论。
- **与本仓库相关的具体风险**：本仓库 `LICENSE` 是 Jellyfin 生态常用许可；若同一仓库同时产出 Emby 插件，**不要**把 Emby 的 SDK DLL 或反编译产物放进仓库；Emby 侧分发物只能是自研程序集。

### 7.4 官方 Development Policy（对**进 catalog** 的约束，不是许可）

> 「**No plug-in shall directly violate or otherwise circumvent or cause the Emby product as a whole to violate or circumvent any laws as governed by the United States of America.**」
> 明确列举包括：
> - 「Directly violating the Terms of Use or Terms of Service of any source of data」
> - 「Using 'web scraping' techniques to obtain data from a web site unless that site grants consent … or the site contains public-domain information only.」
> - 「Including or otherwise distributing code or libraries in a manner that violates the license terms of those particular libraries or **the license terms of Emby**.」
> 违规后果：「Any plug-in that violates this rule will be **removed from the plug-in catalog without notice**.」
> 明确点名禁止：从 IMDb 抓取数据、直接从 YouTube 下载视频。
[来源](https://dev.emby.media/doc/plugins/dev/Development-Policy.html)

> ⚠️ **对 Amane 的直接相关性**：Amane 是「本地元数据服务 + 番号刮削」形态。如果将来要把 Emby 版放进**官方 catalog**，Development Policy 的「不得违反数据源 ToS / 不得 web scraping」条款会成为实质审核门槛（番号刮削源多半不授予 consent）。**手动手动侧载不进 catalog 则不受该 policy 约束**——这是重要的路径选择依据。

### 7.5 官方示例代码许可（明确免责）

> 「### Licensing
> There are no licenses attached to the code and developers are free to decide how to make use of the provided code examples.」
[来源](https://dev.emby.media/home/sdk/plugins/index.html)

**注意范围**：这句只覆盖 **SDK 里的 SampleCode**，**不覆盖** SDK 的二进制程序集（`MediaBrowser.*.dll`）。

### 7.6 闭源对开发调试的实际影响

- Emby Server 闭源 → **不能读服务器源码**来确认行为；只能靠反射 SDK 程序集 + 日志 + 实机试。
  本笔记 §3 / §4 / §6.5 全部靠**反射 SDK 程序集**得出，这在此环境下是**唯一**的事实来源，也说明这条路可行。
- 官方提供 Visual Studio「Executable」调试配置（指向 EmbyServer 可执行文件、工作目录、`-nointerface` 参数关托盘图标）→ **可以断点调试插件**，不需要服务器源码。
  [来源](https://dev.emby.media/doc/plugins/dev/index.html)
- **需要实机验证**：账号开通 developer id 时是否附带额外条款/NDA。

---

## 8. Jellyfin 与 Emby 当前（2025/2026）API 是否仍高度兼容

### 8.1 强证据一：**同一份源码文件，分别对两个 SDK 编译，双双 0 错误 0 警告**

实验设计（本机真实执行）：单一源文件 `%TEMP%\emby-fw-probe\unified\Plugin.cs`，通过 `<Compile Include="..\unified\Plugin.cs" />` 被两个项目共享，**一个字都不改**。

源文件用到（全部只用两边都有的成员）：

```csharp
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer) { ... }

    public override string Name => "Amane";
    public override Guid Id => new Guid("9f2e4a6b-7c1d-4e3f-8a5b-0d9c2e1f4a7b");
    public override string Description => "Amane metadata proxy";

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo { Name = "Amane",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" },
        new PluginPageInfo { Name = "amanejs",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js" }
    };

    public void PersistProbe()
    {
        Configuration.ServerUrl = "http://127.0.0.1:18000";
        SaveConfiguration();
        Console.WriteLine(ConfigurationFilePath);
        Console.WriteLine(ConfigurationFileName);
    }
}

public class PluginConfiguration : BasePluginConfiguration { ... }
```

**(A) 对 Emby SDK 编译**（`MediaBrowser.Server.Core` 4.9.1.90，netstandard2.0）：

```
  emby -> ...\unified-emby\bin\Release\netstandard2.0\UnifiedEmby.dll
已成功生成。
    0 个警告
    0 个错误
-- EXIT=0 --
```

**(B) 对 Jellyfin SDK 编译**（`Jellyfin.Controller` + `Jellyfin.Model` 10.11.10，net9.0）：

```
  jf -> ...\unified-jf\bin\Release\net9.0\UnifiedJellyfin.dll
已成功生成。
    0 个警告
    0 个错误
-- EXIT=0 --
```

**→ 框架/入口/配置页这一层的源码级 API，在 Emby 4.9.1.90 与 Jellyfin 10.11.10 之间是兼容的。** 这是本次调查中关于「当前是否仍高度兼容」最强的证据（不是历史推测，是当前版本实测）。

### 8.2 强证据二：两边共用同一套命名空间与类型名

本仓库 Jellyfin 侧 `Plugin.cs` 用的类型：

| 类型（全限定名） | Emby 4.9.1.90 | Jellyfin 10.11.10 |
|---|---|---|
| `MediaBrowser.Common.Plugins.BasePlugin<T>` | ✅ 存在 | ✅ 存在 |
| `MediaBrowser.Common.Plugins.IPlugin` | ✅ 8 成员一致 | ✅ |
| `MediaBrowser.Model.Plugins.IHasWebPages` | ✅ `GetPages()` | ✅ `GetPages()` |
| `MediaBrowser.Model.Plugins.PluginPageInfo` | ✅ `Name` / `EmbeddedResourcePath` / … | ✅ 同名 |
| `MediaBrowser.Model.Plugins.BasePluginConfiguration` | ✅ | ✅ |
| `MediaBrowser.Model.Serialization.IXmlSerializer` | ✅ | ✅ |
| `MediaBrowser.Common.Configuration.IApplicationPaths` | ✅ | ✅ |

**两者的程序集名也相同**（见 §8.3），这是一把双刃剑。

### 8.3 强证据三（也是最大的坑）：两个 SDK 的**程序集同名**

本机解包对照：

```
########## jellyfin.controller 10.11.10 ##########
  lib/net9.0/MediaBrowser.Controller.dll      607232
########## jellyfin.model 10.11.10 ##########
  lib/net9.0/MediaBrowser.Model.dll           483328
########## jellyfin.common 10.11.10 ##########
  lib/net9.0/MediaBrowser.Common.dll           48640

########## mediabrowser.server.core 4.9.1.90 ##########
  lib/netstandard2.0/MediaBrowser.Controller.dll  575488
########## mediabrowser.common 4.9.1.90 ##########
  lib/netstandard2.0/MediaBrowser.Model.dll       481280
  lib/netstandard2.0/MediaBrowser.Common.dll       50688
```

**两边产出的是同名程序集**：`MediaBrowser.Controller.dll`、`MediaBrowser.Model.dll`、`MediaBrowser.Common.dll`（内容/大小/TFM 都不同）。

**恶果实测**：同一个项目同时引用 `Jellyfin.Controller`/`Jellyfin.Model` 10.11.10 **和** `MediaBrowser.Server.Core` 4.9.1.90：

```
############ restore ############
  已还原 ...\dual.csproj (用时 329 毫秒)。     -- EXIT=0 --
############ build ############
  dual -> ...\bin\Release\net9.0\DualSdkProbe.dll
已成功生成。
    0 个警告
    0 个错误                                    -- EXIT=0 --
```

**看起来成功了，但这是假象。** 用 MSBuild `ResolveReferences` 打印编译器实际拿到的 `ReferencePath`：

```
REF>> C:\Users\rappa\.nuget\packages\jellyfin.controller\10.11.10\lib\net9.0\MediaBrowser.Controller.dll
REF>> C:\Users\rappa\.nuget\packages\jellyfin.model\10.11.10\lib\net9.0\MediaBrowser.Model.dll
REF>> C:\Users\rappa\.nuget\packages\jellyfin.common\10.11.10\lib\net9.0\MediaBrowser.Common.dll
REF>> (MediaBrowser.Server.Core 的对应项：**空**)
```

**→ NuGet/MSBuild 静默丢弃了整套 Emby 程序集**（0 警告 0 错误，只保留 Jellyfin 的）。也就是说：
- 你以为在做「双目标」的构建，实际上**编译的是 Jellyfin 版**；
- 产物对 Emby 服务器是「引用 Jellyfin 10.11 版 MediaBrowser.Controller」的程序集，运行时只靠程序集**简单名**绑定（这些程序集**未强命名**，`PublicKeyToken=null`，见 §6.3 报错原文），版本号 10.11.10.0 vs 服务器 4.9.1.90 不匹配 → 行为未定义，随时 `TypeLoadException` / `MissingMethodException`。

**→ 硬性结论：不可能在一个 `ItemGroup`（一个 TFM/一个 Configuration）里同时引用两个 SDK。必须做构建维度切分。**

生态里的两种可行切分（都真实存在）：

**方案 ①（metatube 式）——按 `Configuration` 切分**：

```xml
<PropertyGroup>
  <Configurations>Debug;Release;Debug.Emby;Release.Emby</Configurations>
</PropertyGroup>
<PropertyGroup>
  <TargetFramework Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'">net9.0</TargetFramework>
  <TargetFramework Condition="'$(Configuration)'=='Debug.Emby' or '$(Configuration)'=='Release.Emby'">net8.0</TargetFramework>
</PropertyGroup>
<ItemGroup Condition="'$(Configuration)'=='Debug' or '$(Configuration)'=='Release'">
  <PackageReference Include="Jellyfin.Controller" Version="10.11.0"/>
  <PackageReference Include="Jellyfin.Model" Version="10.11.0"/>
</ItemGroup>
<ItemGroup Condition="'$(Configuration)'=='Debug.Emby' or '$(Configuration)'=='Release.Emby'">
  <PackageReference Include="MediaBrowser.Server.Core" Version="4.9.1.80"/>
</ItemGroup>
```
[来源](https://github.com/metatube-community/jellyfin-plugin-metatube/blob/main/Jellyfin.Plugin.MetaTube/Jellyfin.Plugin.MetaTube.csproj)

**方案 ②（JavScraper 式）——按 `Configuration` + 预处理宏切分**：`Release` / `Release.Jellyfin`，配 `__JELLYFIN__` 宏，Jellyfin 分支引用 `Jellyfin.Controller`、Emby 分支引用 `MediaBrowser.Server.Core`。
[来源](https://github.com/JavScraper/Emby.Plugins.JavScraper/blob/master/Emby.Plugins.JavScraper/Emby.Plugins.JavScraper.csproj)

**方案 ③（更彻底）——独立项目 / 独立 TFM**：共享一份纯逻辑代码（`Compile Include` 链接），入口与 SDK 引用各自独立。本仓库若要保持 Jellyfin 产线零风险，这是最干净的。

### 8.4 兼容性边界：哪些**不**兼容

| 维度 | Emby 4.9 | Jellyfin 10.11 | 影响 |
|---|---|---|---|
| 程序集名 | `MediaBrowser.*` | `MediaBrowser.*` | ❌ **同名冲突，必须构建切分** |
| 目标框架 | `netstandard2.0` | `net9.0` | ⚠️ Jellyfin 侧需 net9.0（本仓库已如此），Emby 侧 netstandard2.0 最安全 |
| DI 注册 | `IServerEntryPoint` + 自动类型发现 | `IPluginServiceRegistrator` | ❌ 本仓库 `ServiceRegistrator.cs` 必须另写 Emby 版 |
| 配置页注册 | `IHasWebPages.GetPages()` 或 `BasePluginSimpleUI<T>` | `IHasWebPages.GetPages()` | ✅ 前者同名同签名 |
| 版本号单一事实源 | DLL `AssemblyInfo` 自报 + catalog 后台 | `manifest.json` + release zip | ❌ 分发流水线完全不同 |
| 分发渠道 | 手动 DLL / 官方 catalog（无用户可订阅仓库） | `manifest.json` 可订阅仓库 | ❌ 无对应物 |
| Provider API | 由 `emby-provider-api` 结论为准 | — | 本笔记不覆盖 |
| 运行时 | 4.7/4.8 = .NET 6；4.9+ = .NET 8 | 10.11 = .NET 9 | ⚠️ 依赖包版本需分别验证 |

**总结判断**：**「框架/入口/配置页」这一层，Jellyfin 10.11 与 Emby 4.9 至今（2025/2026）仍然高度兼容，甚至源码级同构（§8.1 实测）**。不兼容的地方集中在**程序集同名冲突**、**DI 注册方式**、**分发/版本单一事实源**这三处——都是**工程问题，不是 API 问题**。

---

## 9. 面向本仓库的可操作建议（工程层）

1. **最大风险不是 API，而是程序集同名**：`MediaBrowser.Controller/Model/Common.dll` 在 Jellyfin 与 Emby 两侧同名。**绝不要**在同一 TFM/Configuration 下同时引用两个 SDK（本机实测会静默丢掉 Emby 侧，§8.3）。采用 `Configuration` 切分（metatube 式）或独立项目。
2. **Emby 侧 TFM 选 `netstandard2.0`**：SDK 从 4.6 到 4.10 beta 一直是 netstandard2.0（§2.1），`netstandard2.0` 可覆盖 4.7/4.8/4.9/4.10，且可被 net8/net9/net10 消费（§2.3）。选 `net8.0` 会丢掉 4.8（.NET 6）。
3. **坚持「零第三方依赖」**：官方 SDK 依赖树只有 2 个包（§1.3）；LDAP 事故（§6.3）正是插件自带 8.0 BCL 包导致的。Amane 现有「不新增第三方依赖」约定必须保持，这是 Emby 兼容性的护城河。
4. **入口类可以几乎复用**：`BasePlugin<T> + IHasWebPages + GetPages()` 的写法两个 SDK 都能编译（§8.1 实测）。配置页继续用内嵌 HTML（Emby 侧把 `.js` 也注册成一个 page，见 Trakt）。
5. **`ServiceRegistrator.cs` 需要 Emby 版**：Emby 没有 `IPluginServiceRegistrator`，用 `IServerEntryPoint`（构造函数注入）替代。
6. **加一个 `sdk-load-check` 到 CI**：对 `MediaBrowser.Server.Core` 4.7.9 / 4.8.11 / 4.9.1.90 三个 SDK 目录做「加载全部类型 + JIT 全部方法」，这是无 Emby 服务器时唯一能抓住跨版本绑定失败的手段（§6.4 的 Xtream ADR 已验证该手法有效）。
7. **分发路径**：Emby 官方 catalog 需要向 ebr 申请 developer id + 走 Development Policy 审核（番号刮削大概率过不了「不得 scraping」条款，§7.4）。**手动单 DLL 安装是官方支持路径**（Luke 原话），把它当主路径写在 README。
8. **许可**：Emby SDK NuGet **无任何许可声明**（§7.2），Emby ToS 许可范围是 personal/non-commercial 且禁衍生作品（§7.3）。**建议在真正上线前取一次法务意见**，并确保仓库内不含 Emby 的 SDK DLL 或反编译产物。

---

## 10. 「需要实机验证」清单（本笔记未能闭环）

| # | 待验证项 | 为什么本机做不到 | 建议验证方式 |
|---|---|---|---|
| 1 | 本机 Emby Server 已安装目录下 `MediaBrowser.Controller.dll` 等的 `VersionInfo` | 本机**未安装 Emby Server**（§0.2 全部路径为空，全盘搜 `MediaBrowser.Controller.dll` 零命中） | 装一台 Emby 4.8.11 / 4.9.5.0，枚举 `programdata/system` 做版本比对 |
| 2 | Emby 插件 catalog 的**线上 JSON URL 与字段名** | 未能找到公开端点（5 个候选 URL 全 404），后台需开发者账号登录 | 登录 `https://plugins.emby.tv/admin/packages.html`，或抓 Emby Server 出网请求 |
| 3 | Emby 4.10 的**实际运行时版本**（.NET 8？） | 4.10 仍 beta（NuGet 最新 `4.10.0.24-beta2`），官方仅确认 4.9 = .NET 8 | 装 Emby 4.10 beta，看启动日志 `Framework:` 行 |
| 4 | `IPluginConfigurationPage` 的**实际注册与路由方式** | SDK 有接口，但未找到任何真实插件/官方示例的落地代码 | 反编译一个用了该接口的插件，或官方论坛提问 |
| 5 | Emby 是否支持**从 zip 安装**插件 | 官方文档与论坛只提单 DLL；未找到 zip 安装的官方说明 | 在实机 plugin 目录放 zip 观察行为 |
| 6 | 配置页 JS 中可用的 **Emby 前端全局对象/API** | 官方明说从未文档化且频繁变更 | 实机 + 参考真实插件（Trakt `configPage.js`） |
| 7 | 申请 developer id 时是否附带 **NDA/开发者协议** | 需要真实账号流程 | 走一次申请流程 |
| 8 | Emby ToS 对**独立发布的插件程序集**是否构成阻碍 | 需法务判断，工程无法定论 | 法务意见 |
| 9 | Emby 4.7（.NET ?）实际运行时 | 只拿到 4.8 = .NET 6 的日志证据；4.7 无一手证据 | 查 4.7 时代服务器日志的 `Framework:` 行 |
| 10 | Emby Provider API（`IRemoteMetadataProvider` 等的当代表现） | **超出本笔记范围**，由 `emby-provider-api` 负责 | 见 `emby-provider-api.md` |

---

## 附录 A：本笔记用到的本机路径（可复现）

```
%TEMP%\emby-fw-probe\
├── refl\                       反射转储器（net10.0 + System.Reflection.MetadataLoadContext 10.0.12）
├── sdktest\                    Emby SDK 编译探针（netstandard2.0 + MediaBrowser.Server.Core 4.9.1.90）
├── dual\                       Jellyfin + Emby 双 SDK 共存探针（证明静默丢弃）
├── unified\Plugin.cs           单源同编的共享源文件
├── unified-emby\               对 Emby SDK 编译
├── unified-jf\                 对 Jellyfin SDK 编译
├── nupkg\, extract\, combined479\, combined4811\, combined491\
│                              Emby SDK 三版本 nupkg 与解包
├── jfpkg\, jflib\              Jellyfin SDK nupkg 与解包
└── repos\ext\                  6 个真实 Emby 插件源码快照
```

## 附录 B：本笔记引用的全部 URL

**官方文档**
- [Server Plugins 总览](https://dev.emby.media/doc/plugins/index.html)
- [Plugin Development（入口/IServerEntryPoint/调试/PostBuild）](https://dev.emby.media/doc/plugins/dev/index.html)
- [Development Policy（catalog 合规条款）](https://dev.emby.media/doc/plugins/dev/Development-Policy.html)
- [Getting your Plugin in the catalog（catalog 字段全表）](https://dev.emby.media/doc/plugins/dev/Getting-your-plug-in-in-the-catalog.html)
- [Creating UI for Plugins（HTML/JS 旧法 + 声明式 UI）](https://dev.emby.media/doc/plugins/ui/index.html)
- [Simple Plugin UI（BasePluginSimpleUI / EditableOptionsBase）](https://dev.emby.media/doc/plugins/ui/simpleui.html)
- [Emby SDK（SDK 包内容）](https://dev.emby.media/home/sdk/index.html)
- [Plugin Sample Code（示例代码许可声明）](https://dev.emby.media/home/sdk/plugins/index.html)
- [Template: Simple Plugin](https://dev.emby.media/home/sdk/plugins/template_simple.html)
- [Emby Terms of Service（2026-08-16 版）](https://emby.media/terms.html)

**NuGet / 包**
- [MediaBrowser.Server.Core 搜索命中](https://azuresearch-usnc.nuget.org/query?q=mediabrowser&take=50)
- [MediaBrowser.Server.Core 版本索引](https://api.nuget.org/v3-flatcontainer/mediabrowser.server.core/index.json)
- [nuget.org 包页：MediaBrowser.Server.Core](https://www.nuget.org/packages/MediaBrowser.Server.Core)

**官方 / 真实插件源码**
- [MediaBrowser/trakt（Emby 官方）Plugin.cs](https://github.com/MediaBrowser/trakt/blob/master/Trakt/Plugin.cs) ｜ [Trakt.csproj](https://github.com/MediaBrowser/trakt/blob/master/Trakt/Trakt.csproj)
- [MediaBrowser/Emby.AutoOrganize（Emby 官方）](https://github.com/MediaBrowser/Emby.AutoOrganize)
- [JavScraper/Emby.Plugins.JavScraper](https://github.com/JavScraper/Emby.Plugins.JavScraper/blob/master/Emby.Plugins.JavScraper/Emby.Plugins.JavScraper.csproj)
- [metatube-community/jellyfin-plugin-metatube](https://github.com/metatube-community/jellyfin-plugin-metatube/blob/main/Jellyfin.Plugin.MetaTube/Jellyfin.Plugin.MetaTube.csproj)
- [firestaerter3/emby-xtream](https://github.com/firestaerter3/emby-xtream) ｜ [ADR-017 单 DLL 策略](https://github.com/firestaerter3/emby-xtream/blob/main/docs/decisions/017-single-dll-for-emby-49-and-410.md)
- [fengymi/emby-plugin-danmu](https://github.com/fengymi/emby-plugin-danmu)
- [MediaBrowser/Emby（releases-only，服务器源码不公开）](https://github.com/MediaBrowser/Emby)

**Emby 官方论坛**
- [Luke：You can copy the DLL into your plugins folder manually.（无第三方仓库）](https://emby.media/community/topic/142301-emby-running-in-docker-how-to-add-catalogrepositories/#findComment-1467730)
- [Luke：upcoming 4.9 server release has updated to dotnet 8.](https://emby.media/community/topic/142389-net-core-version-upgrade/#findComment-1468633)
- [LDAP 1.0.44：Emby 4.8.10 / .NET 6.0.31 `Could not load file or assembly` 事故](https://emby.media/community/topic/133644-ldap-1044-update-causes-could-not-load-file-or-assembly/)
- [4.8 Server - New Notification API's for Plugins（对应 `INotificationService` 被删）](https://emby.media/community/topic/114611-48-server-new-notification-apis-for-plugins/)
