# Core/Adapter 拆分编译验证 PoC（仓库外实验）

> 任务：task-4。实验代码全部在 `%TEMP%\amane-core-poc`，**仓库未被修改**（`git status --porcelain` 仅有其他队友的 `?? .research-scratch/`、`?? docs/`）。
> 工具链：`dotnet 10.0.401`（`DOTNET_ROOT=C:\Users\rappa\.dotnet`）。
> 所有结论均由真实 `dotnet build` / `dotnet run` 输出支撑，原文见第 8、9 节。

---

## 0. 结论速览

| 问题 | 结论 | 证据 |
|---|---|---|
| net8.0 Core 能否被 net9.0（Jellyfin）与 net8.0（Emby）适配器同时引用并编译？ | **能，成立**。0 warning 0 error，且三者在同一个解决方案图里一次构建通过 | §8.2 / §8.4 / §8.6 |
| Core 是否真的与平台解耦？ | **是**。`Amane.Core.dll` 的程序集引用里只有 `Microsoft.Extensions.Logging.Abstractions`（+ 传递的 `DependencyInjection.Abstractions`），**没有任何 `MediaBrowser.*`** | §8.1 / §10.2 |
| 原仓库代码改造量 | Core 侧 **24 处**字面替换（`AmaneClient.cs` 23 + `AmaneModels.cs` 1）；Jellyfin 适配器侧 **8 处**替换，涉及 5/9 个文件；**4 个文件一字未改** | §5 / §6 |
| 编译期会不会撞 `MediaBrowser.*` 同名类型？ | **不会报错，但会静默错绑定**——这是比硬报错更危险的结论 | §10 |
| `IHttpClientFactory` 能不能留在 Core？ | **不能**。Emby 宿主目录里没有 `Microsoft.Extensions.Http.dll`；且共享 `HttpClient` 后设 `Timeout` 会抛 `InvalidOperationException`。必须换成 Core 自有的 `IAmaneHttpClientProvider` | §4.2 / §10.3 |
| `InternalsVisibleTo` 测试通道 | 可行，但目标程序集必须改名：Core 里 `InternalsVisibleTo("Amane.Core.Tests")`，21 项冒烟检查全过 | §5 / §9 |
| **追加：Core 该选 net8.0 还是 netstandard2.0** | **选 `net8.0`**。netstandard2.0 要付 3 处源码劣化 + 12 个可空性警告 + `IsExternalInit` 垫片，且在 Emby copy-local 打包下多带出 8 个 netstandard2.0 垫片（含 `Microsoft.Bcl.AsyncInterfaces`）；Jellyfin 侧 bin 不受 Core TFM 影响 | §13 |
| **追加：拆分会不会污染 Jellyfin bin** | **不会**。仓库现状 bin 本来就是 **30 个 dll**（不是 1 个）；拆分后 31 个，只多 `Amane.Core.dll`，无 `System.Text.Json.dll` | §13.0 / §13.3 |

---

## 1. 实验产物

```
%TEMP%\amane-core-poc\
├─ Amane.CorePoc.sln
├─ Amane.Core\                      net8.0  ← 仓库 AmaneModels.cs + AmaneClient.cs 的副本（改 seam）+ 新增 AmaneSettings.cs
├─ Amane.JellyfinAdapter\           net9.0  ← 仓库 Providers/ Plugin.cs ServiceRegistrator.cs Configuration/ Api/ 的副本（8 处改动）
├─ Amane.EmbyAdapter\               net8.0  ← 全新 Emby 适配器（445 行）
├─ Amane.Core.Tests\                net10.0 ← Core 自己的测试程序集（21 项运行时冒烟检查）
├─ collision-probe\                 net9.0  ← 负向实验：两套 MediaBrowser.* 同时引用
├─ setup-poc.ps1 / apply-spec.ps1 / seam-replacements.txt / jellyfin-adapter-replacements.txt
└─ log-*.txt                        ← 全部真实构建/运行日志
```

`setup-poc.ps1` 是幂等的：从仓库 `Copy-Item` 还原原始文件 → 应用 seam 规格 → 断言每处替换的出现次数 → 断言 Core 里不存在 `Plugin.Instance` / `IHttpClientFactory` / `Jellyfin.` / `MediaBrowser.`。**任何一次替换的锚点不唯一就抛异常停止**，所以本文的行号与改动量是可复算的。

---

## 2. Amane.Core（net8.0）csproj 全文

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>Amane.Core</AssemblyName>
    <RootNamespace>Amane.Core</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <!-- 唯一允许的共享依赖：Emby 宿主自带 8.0.1024.46610，Jellyfin 宿主自带 9.x（会被 lift） -->
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.0" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Amane.Core.Tests" />
  </ItemGroup>
</Project>
```

**TFM 选择依据**：net8.0 是 Emby 4.10 的宿主 TFM，也是 net9.0 能引用的最低版本；netstandard2.0 也可以被两边引用，但会丢掉 `SocketsHttpHandler` / `init` 访问器等 BCL 便利（Emby 自己发布的 SDK 是 netstandard2.0，见 §10.1）。

## 3. Amane.JellyfinAdapter（net9.0）csproj 全文

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Amane.JellyfinAdapter</AssemblyName>
    <RootNamespace>Jellyfin.Plugin.Amane</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <!-- 与仓库原 csproj 完全一致：IHttpClientFactory 来自 ASP.NET Core 共享框架 -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Jellyfin.Controller" Version="10.11.10">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Jellyfin.Model" Version="10.11.10">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Amane.Core\Amane.Core.csproj" />
  </ItemGroup>
</Project>
```

与仓库原 csproj 的唯一差别就是多了一行 `ProjectReference`。顺带确认：仓库能用到 `IHttpClientFactory` 是因为 `FrameworkReference Microsoft.AspNetCore.App` 的共享框架里带 `Microsoft.Extensions.Http.dll`（实测 `C:\Users\rappa\.dotnet\shared\Microsoft.AspNetCore.App\10.0.12\Microsoft.Extensions.Http.dll`），不是 NuGet 包。

## 4. Amane.EmbyAdapter（net8.0）csproj 全文

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>Amane.EmbyAdapter</AssemblyName>
    <RootNamespace>Amane.EmbyAdapter</RootNamespace>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <LangVersion>latest</LangVersion>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS0169;CS0649;CS8632</NoWarn>
    <UseRealEmbyAssemblies Condition="'$(UseRealEmbyAssemblies)' == ''">false</UseRealEmbyAssemblies>
    <EmbyRefDir Condition="'$(EmbyRefDir)' == ''">$(LocalAppData)\Temp\amane-emby-poc-tools\dl\emby-server-4.10.0.40\system</EmbyRefDir>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Amane.Core\Amane.Core.csproj" />
  </ItemGroup>

  <ItemGroup Condition="'$(UseRealEmbyAssemblies)' != 'true'">
    <PackageReference Include="MediaBrowser.Server.Core" Version="4.10.0.24-beta2" />
  </ItemGroup>

  <ItemGroup Condition="'$(UseRealEmbyAssemblies)' == 'true'">
    <Reference Include="MediaBrowser.Controller">
      <HintPath>$(EmbyRefDir)\MediaBrowser.Controller.dll</HintPath><Private>false</Private>
    </Reference>
    <Reference Include="MediaBrowser.Model">
      <HintPath>$(EmbyRefDir)\MediaBrowser.Model.dll</HintPath><Private>false</Private>
    </Reference>
    <Reference Include="MediaBrowser.Common">
      <HintPath>$(EmbyRefDir)\MediaBrowser.Common.dll</HintPath><Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

两种引用模式都实测通过（§8.3 / §8.5）。**同一个 Emby 适配器源码在「官方 NuGet netstandard2.0 SDK」与「真实 Emby Server 4.10.0.40 的 net8.0 程序集」下都能编译**。

注意：**没有设 `CopyLocalLockFileAssemblies`**。Emby 插件目录里绝不能出现 `MediaBrowser.*.dll`（会和宿主自己的程序集打架）。实测 Emby 适配器 `bin\Release\net8.0` 只有 `Amane.Core.dll` + `Amane.EmbyAdapter.dll` 两个文件——这是正确形态。仓库根 csproj 的 `CopyLocalLockFileAssemblies=true` **不能照搬到 Emby 侧**。

### 4.1 Emby NuGet SDK 的实际内容

| 包 | 内容 |
|---|---|
| `MediaBrowser.Server.Core` 4.10.0.24-beta2 | `lib/netstandard2.0/MediaBrowser.Controller.dll`、`lib/netstandard2.0/Emby.Naming.dll` |
| └ 依赖 `MediaBrowser.Common` 4.10.0.24-beta2 | `lib/netstandard2.0/MediaBrowser.Common.dll`、`MediaBrowser.Model.dll`、`Emby.Media.Model.dll`、`Emby.Web.GenericEdit.dll` |

仓库已有的 `Jellyfin.Controller` 10.11.10 提供的是**同名**的 `lib/net9.0/MediaBrowser.Controller.dll` / `MediaBrowser.Model.dll`。

### 4.2 Emby 宿主环境约束（实测）

`%TEMP%\amane-emby-poc-tools\dl\emby-server-4.10.0.40\system` 里：
- **有** `MediaBrowser.{Common,Controller,Model}.dll`、`Emby.Naming.dll`、`Microsoft.Extensions.Logging.Abstractions.dll`（8.0.1024.46610）、`Microsoft.Extensions.DependencyInjection*.dll`
- **没有** `Microsoft.Extensions.Http.dll` → **Emby 宿主给不了 `IHttpClientFactory`**

所以 Core 里保留 `IHttpClientFactory` 需要么把 `Microsoft.Extensions.Http.dll` 塞进 Emby 插件目录（引入与宿主冲突的风险），么就是做不到。**必须换成 Core 自有的 `IAmaneHttpClientProvider`**。

---

## 5. Seam 清单：Core 与宿主耦合点（精确到 文件:行）

以下行号均为**仓库当前 `AmaneClient.cs`（668 行）的行号**。

### 5.1 配置读取（task 描述的「8 处 `Plugin.Instance?.Configuration`」全部命中）

| # | 位置 | 现状 | 建议抽象 | 读取时机 |
|---|---|---|---|---|
| S3 | `AmaneClient.cs:94` | `?? Plugin.Instance?.Configuration?.MaxConcurrentRequests ?? DefaultMaxConcurrentRequests` | `IAmaneSettings.MaxConcurrentRequests` | 构造时读一次（原语义） |
| S4 | `AmaneClient.cs:109` | `Plugin.Instance?.Configuration?.TimeoutSeconds ?? DefaultTimeoutSeconds` | `IAmaneSettings.TimeoutSeconds` | 每请求（改配置即时生效） |
| S5 | `AmaneClient.cs:124` | `Plugin.Instance?.Configuration?.ActorCacheMinutes ?? (int)DefaultActorCacheTtl.TotalMinutes` | `IAmaneSettings.ActorCacheMinutes` | 每请求 |
| S9 | `AmaneClient.cs:385` | `ToDirectImageUrl` 里读 `ServerUrl` | `private string ServerUrl => _settings.ServerUrl?.TrimEnd('/') ?? DefaultServerUrl;` | 每次调用 |
| S10 | `AmaneClient.cs:405` | `ToProxyImageUrl` 里读 `ServerUrl` | 同上 | 每次调用 |
| S11 | `AmaneClient.cs:439`（+`:441`） | `GetImageAsync` 读 `ServerUrl` **和** `ApiToken` | `ServerUrl` + `_settings.ApiToken` | 每次调用 |
| S12 | `AmaneClient.cs:474-475` | `CheckHealthAsync` 读 `ServerUrl` | `ServerUrl` | 每次调用 |
| S13 | `AmaneClient.cs:517` | `CheckHealthAsync` 读 `ApiToken` | `_settings.ApiToken` | 每次调用 |
| S14 | `AmaneClient.cs:558-559` | `GetAsync<T>` 读 `ServerUrl` | `ServerUrl` | 每次请求 |
| S15 | `AmaneClient.cs:580-582` | `GetAsync<T>` 读 `ApiToken` | `_settings.ApiToken` | 每次请求 |

> 注：`Plugin.Instance?.Configuration` 字面点共 8 处（94/109/124/385/405/439/474/558，与 task 清单一致）；其中 `:439` 与 `:517` 各额外读了一次 `ApiToken`，故实际配置读取点 10 个。

### 5.2 其它平台耦合（task 清单未列，但同样必须处理）

| # | 位置 | 现状 | 建议抽象 |
|---|---|---|---|
| S1 | `AmaneClient.cs:36`（字段）、`:65/:67/:76`（构造函数） | 依赖 `IHttpClientFactory` | `IAmaneHttpClientProvider`（Core 自有，见 §4.2） |
| S16 | `AmaneClient.cs:663` | `_httpClientFactory.CreateClient()` | `_httpClientProvider.CreateClient()` |
| S17 | `AmaneClient.cs:585` | 注释提到 `IHttpClientFactory` | 文案调整 |
| S6 | `AmaneClient.cs:187` | `Providers.AmaneMovieProvider.InternalIdProviderIdName` —— **Core 反向依赖 Jellyfin 的 Provider 类取 const** | `AmaneProviderIds.InternalIdProviderIdName`（搬到 Core） |
| S7 | `AmaneClient.cs:198`、`:313` | `Providers.AmaneMovieProvider.ProviderIdName` | `AmaneProviderIds.ProviderIdName` |
| S8 | `AmaneClient.cs:222`、`:238` | `internal static NormalizeIdValue` / `TryParseInternalId` —— 拆分后 Provider 在另一个程序集，访问不到 | 改 `public static`；**测试用的 internal 构造函数保持不变**，靠 Core 自己的 `InternalsVisibleTo("Amane.Core.Tests")` |
| — | `AmaneClient.cs:15` | `namespace Jellyfin.Plugin.Amane` | `namespace Amane.Core`（Core 不该以宿主命名） |
| — | `AmaneModels.cs:5` | 同上 | 同上 |

### 5.3 新增到 Core 的三个类型（`Amane.Core/AmaneSettings.cs`，约 70 行）

```csharp
public interface IAmaneSettings
{
    string? ServerUrl { get; }
    string? ApiToken { get; }
    int TimeoutSeconds { get; }        // <= 0 → 内置默认 5s
    int MaxConcurrentRequests { get; } // 构造时读一次
    int ActorCacheMinutes { get; }     // <= 0 → 禁用缓存
}

public sealed class AmaneSettings : IAmaneSettings   // 不可变实现，测试/无配置系统的宿主直接 new
{
    public const int DefaultMaxConcurrentRequests = 4;
    public string? ServerUrl { get; init; } = "http://127.0.0.1:18000";
    public string? ApiToken { get; init; }
    public int TimeoutSeconds { get; init; } = 5;
    public int MaxConcurrentRequests { get; init; } = DefaultMaxConcurrentRequests;
    public int ActorCacheMinutes { get; init; } = 360;
}

public interface IAmaneHttpClientProvider
{
    // 约定：每次返回"尚未发起过请求"的 HttpClient —— Core 会在其上设 Timeout，
    // 复用已发过请求的实例再设 Timeout 会抛 InvalidOperationException。
    HttpClient CreateClient();
}

public static class AmaneProviderIds
{
    public const string ProviderIdName = "Amane";              // 原 Providers.AmaneMovieProvider.ProviderIdName
    public const string InternalIdProviderIdName = "AmaneId";  // 原 ...InternalIdProviderIdName
}
```

### 5.4 Core 改动量汇总

`setup-poc.ps1` 实测输出（断言每处替换出现次数）：

```
models-namespace                     AmaneModels.cs        x1
client-namespace                     AmaneClient.cs        x1
S1-fields / S2-public-ctor / S3-test-ctor / S4-ctor-body   x1 each
S5-max-concurrency / S6-drop-default-concurrency-const     x1 each
S7-timeout / S8-actor-cache-ttl / S9-serverurl-prop        x1 each
S10-default-serverurl-const                                x1
S11-internal-id-const x1 / S11-provider-id-const x2
S12-normalize-public x1 / S12-parseid-public x1
S13-direct-url / S14-proxy-url / S15-image-auth            x1 each
S16-health-head / S17-health-token / S18-getasync-head     x1 each
S19-getasync-auth / S20-createclient                       x1 each
TOTAL replacements applied: 24 across 2 file(s)
INVARIANT OK: no Plugin.Instance / IHttpClientFactory / Providers.AmaneMovieProvider / Jellyfin. / MediaBrowser.
```

**`AmaneClient` 的 668 行业务逻辑一行未删**：HTTP 弹性（信号量背压 / 每请求 linked CTS 超时 / 连续失败熔断）、演员进程内缓存与双键、`ResolveMetadataAsync` / `ResolveActorAsync` 逐级解析、图片 URL 双轨制、`CheckHealthAsync` 全部原样保留。

---

## 6. Jellyfin 适配器的改动明细（最少改动证明）

`setup-poc.ps1` 实测输出：

```
JF-using-core                Providers\AmaneMovieProvider.cs      x1
JF-const-alias-provider      Providers\AmaneMovieProvider.cs      x1
JF-const-alias-internal-id   Providers\AmaneMovieProvider.cs      x1
JF-using-core                Providers\AmaneImageProvider.cs      x1
JF-using-core                Providers\AmanePersonProvider.cs     x1
JF-using-core                Api\AmaneDiagnosticsController.cs    x1
JF-di-registration           ServiceRegistrator.cs                x1
JF-di-extra-registrations    ServiceRegistrator.cs                x1
TOTAL replacements applied: 8 across 5 file(s)
```

| 文件 | 改动 |
|---|---|
| `Providers/AmaneMovieProvider.cs` | +`using Amane.Core;`；`:26/:31` 的 const 改为 `AmaneProviderIds.*` 别名（**保持源码兼容**，原有调用点 `ProviderIdName` 不用改） |
| `Providers/AmaneImageProvider.cs` | +`using Amane.Core;`（仅此一行） |
| `Providers/AmanePersonProvider.cs` | +`using Amane.Core;`（仅此一行） |
| `Api/AmaneDiagnosticsController.cs` | +`using Amane.Core;`（仅此一行） |
| `ServiceRegistrator.cs` | +`using Amane.Core;`；`:15` 从 1 行注册变 3 行（加 `IAmaneSettings` / `IAmaneHttpClientProvider`） |
| **`Providers/AmaneMovieExternalId.cs`** | **零改动** |
| **`Providers/AmanePersonExternalId.cs`** | **零改动** |
| **`Plugin.cs`** | **零改动**（`Plugin.Instance` 仍在适配层，只有 `JellyfinAmaneSettings` 读它） |
| **`Configuration/PluginConfiguration.cs`** | **零改动** |

**9 个文件里 4 个字面未改，5 个文件里 4 个只多加了一行 `using`。** 这是「现有 Provider 代码在最小改动下仍能编译」的直接证据。

新增 2 个适配器文件（共 48 行）：

```csharp
// JellyfinAmaneSettings.cs —— 每次读取取当前值，保持"改配置即时生效"语义
public sealed class JellyfinAmaneSettings : IAmaneSettings
{
    private static Configuration.PluginConfiguration? Config => Plugin.Instance?.Configuration;
    public string? ServerUrl => Config?.ServerUrl;
    public string? ApiToken => Config?.ApiToken;
    public int TimeoutSeconds => Config?.TimeoutSeconds ?? 5;
    public int MaxConcurrentRequests => Config?.MaxConcurrentRequests ?? AmaneSettings.DefaultMaxConcurrentRequests;
    public int ActorCacheMinutes => Config?.ActorCacheMinutes ?? 360;
}

// JellyfinHttpClientProvider.cs
public sealed class JellyfinHttpClientProvider : IAmaneHttpClientProvider
{
    private readonly IHttpClientFactory _factory;
    public JellyfinHttpClientProvider(IHttpClientFactory factory) => _factory = factory;
    public HttpClient CreateClient() => _factory.CreateClient();
}
```

`ServiceRegistrator.RegisterServices` 最终形态：

```csharp
// 适配层负责把宿主能力接到 Core 的两个 seam 上
serviceCollection.AddSingleton<IAmaneSettings, JellyfinAmaneSettings>();
serviceCollection.AddSingleton<IAmaneHttpClientProvider, JellyfinHttpClientProvider>();
serviceCollection.AddSingleton<AmaneClient>();
```

---

## 7. Emby 适配器「必须自己实现」的清单

PoC 落地的 5 个文件共 **445 行**（不含 Emby 配置页 html，PoC 未做）：

| 文件 | 行数 | 内容与关键差异 |
|---|---|---|
| `EmbyAmaneMovieProvider.cs` | 226 | `IRemoteMetadataProvider<Movie, MovieInfo>` + `IHasOrder`；Core DTO → Emby `MetadataResult<Movie>` 映射 |
| `EmbyAmaneImageProvider.cs` | 99 | `IRemoteImageProvider`（**`GetImages` 多一个 `LibraryOptions` 参数**） |
| `EmbyAmanePlugin.cs` | 52 | `BasePlugin` + `IHasWebPages`，固定 GUID |
| `EmbySeams.cs` | 40 | `EmbyAmaneSettings` + `EmbyHttpClientProvider`（共享 `SocketsHttpHandler`，每次新包一层 `HttpClient`） |
| `EmbyAmaneExternalId.cs` | 28 | `IExternalId`（**成员集与 Jellyfin 不同**） |

对应的 Jellyfin 侧「必须自己实现」清单（≈717 行 + `configPage.html`）：`Plugin.cs` 55 行、`Configuration/PluginConfiguration.cs` 34 行、`ServiceRegistrator.cs` 21 行、`Api/AmaneDiagnosticsController.cs` 50 行、`Providers/AmaneMovieProvider.cs` 229 行、`AmanePersonProvider.cs` 135 行、`AmaneImageProvider.cs` 95 行、`AmaneMovieExternalId.cs` 25 行、`AmanePersonExternalId.cs` 25 行、`JellyfinAmaneSettings.cs` 25 行、`JellyfinHttpClientProvider.cs` 23 行。

### 7.1 两个宿主 API 的真实签名差异（实测 dump + PoC 编译验证）

| 契约点 | Jellyfin 10.11.10 | Emby 4.10（NuGet netstandard2.0 与真实 net8.0 一致） | 是否可共享 |
|---|---|---|---|
| 图片下载返回 | `Task<HttpResponseMessage>`（`System.Net.Http`） | `Task<HttpResponseInfo>`（`MediaBrowser.Common.Net`，成员 `Content`/`ContentType`/`ContentLength`/`StatusCode`/`ResponseUrl`，有 `HttpResponseInfo(IDisposable[])` 重载） | ❌ 必须适配 |
| 人物类型枚举 | `Jellyfin.Data.Enums.PersonKind`（`PersonKind.Actor`） | `MediaBrowser.Model.Entities.PersonType`（`PersonType.Actor`） | ❌ |
| 人物信息载体 | `MediaBrowser.Controller.Entities.PersonInfo`（同名同类，但 `Type` 类型不同） | 同名 | ⚠️ 同名不同枚举 |
| 远程图片提供器 | `IRemoteImageProvider.GetImages(BaseItem, CancellationToken)` | `IRemoteImageProvider.GetImages(BaseItem, LibraryOptions, CancellationToken)` | ❌ |
| 外部 ID 声明 | `IExternalId { ProviderName; Key; Type(ExternalIdMediaType?); Supports }` | `IExternalId { Name; Key; UrlFormatString; Supports }`（**无 `ExternalIdMediaType`**） | ❌ |
| 搜索排序 | `IRemoteMetadataProvider<,> : ... IHasOrder`（可选） | `IHasOrder` 是独立接口（同样可选） | ✅ 概念相同 |
| 元数据结果 | `MetadataResult<T> { HasMetadata; Item; ResultLanguage; People; AddPerson }` | 同名同成员 | ✅ 概念相同 |
| ProviderIds 入参 | `IReadOnlyDictionary<string,string>`（`ProviderIdDictionary : Dictionary<string,string>`） | 同名同基类 | ✅ **Core 的签名选对了就不需要适配代码** |
| 插件基类 | `BasePlugin<TConfiguration>` + `IPluginServiceRegistrator`（DI 注册） | `BasePlugin` / `BasePlugin<TConfiguration>`；**没有 `IPluginServiceRegistrator`** | ❌ DI 接线各自实现 |
| 配置页接口 | `IHasWebPages`（`MediaBrowser.Model.Plugins`） | `IHasWebPages`（同名同命名空间） | ✅ 接口同，html 内容不同 |
| 插件入口 GUID | `Plugin`（`IApplicationPaths, IXmlSerializer`） | `EmbyAmanePlugin`（无参构造 + `SetAttributes`） | ❌ |

### 7.2 绝对不能共享、必须各写一份的东西

1. **元数据映射** `AmaneMetadata → Movie`（目标类型不同；`PersonKind` vs `PersonType`；字段可空性不同）
2. **Person/演员映射**（`PersonInfo.Type` 枚举类型不同）
3. **图片返回类型转换**（`HttpResponseMessage` ↔ `HttpResponseInfo`）
4. **图片 URL 策略的消费端语义**（`RemoteImageInfo.ThumbnailUrl`、`PersonInfo.ImageUrl`、演员 `ItemImageInfo.Path` 在两侧的下载路径不同）
5. **配置页 html + 配置持久化**（`BasePluginConfiguration` + `IXmlSerializer` vs Emby 的插件目录 xml）
6. **DI 注册**（`IPluginServiceRegistrator` 是 Jellyfin 专有）
7. **External ID 声明**（`IExternalId` 成员集不同）
8. **REST/控制器层**（Jellyfin 是 ASP.NET Core `[ApiController]`；Emby 走 ServiceStack）

### 7.3 值得下沉到 Core 的共享候选（PoC 中已在两侧重复）

- `FormatDisplayName(AmaneMetadata)` —— 纯字符串函数，两侧逐字相同（`番号 + 空格 + 标题`）→ 建议下沉
- `metadata.GetOriginalTitle()` —— 已经在 Core（`AmaneModels.GetOriginalTitle`）✅
- `5 分制 × 2 → 10 分制` 的评分换算与 `Runtime 分钟 → ticks` —— 纯计算，两侧重复 → 建议下沉为 `AmaneMapping` 静态类

---

## 8. 真实 `dotnet build` 输出（原文）

### 8.1 Amane.Core（net8.0）

```
  正在确定要还原的项目…
  已还原 C:\Users\rappa\AppData\Local\Temp\amane-core-poc\Amane.Core\Amane.Core.csproj (用时 1.54 秒)。
  Amane.Core -> C:\Users\rappa\AppData\Local\Temp\amane-core-poc\Amane.Core\bin\Release\net8.0\Amane.Core.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:03.53
```

`dotnet msbuild -getItem:ReferencePath` 解析出的编译引用（已剔除 `Microsoft.NETCore.App.Ref`）：

```
microsoft.extensions.dependencyinjection.abstractions\8.0.0\lib\net8.0\Microsoft.Extensions.DependencyInjection.Abstractions.dll
microsoft.extensions.logging.abstractions\8.0.0\lib\net8.0\Microsoft.Extensions.Logging.Abstractions.dll
```

`Amane.Core.dll` 堆字符串扫描（`MediaBrowser.*` 全无）：

```
Amane.Core -> Microsoft.Extensions.Logging.Abstractions, Amane.Core
```

### 8.2 Amane.JellyfinAdapter（net9.0）引用 net8.0 Core

```
  正在确定要还原的项目…
  已还原 C:\Users\rappa\AppData\Local\Temp\amane-core-poc\Amane.Core\Amane.Core.csproj (用时 292 毫秒)。
  已还原 C:\Users\rappa\AppData\Local\Temp\amane-core-poc\Amane.JellyfinAdapter\Amane.JellyfinAdapter.csproj (用时 310 毫秒)。
  Amane.Core -> ...\Amane.Core\bin\Release\net8.0\Amane.Core.dll
  Amane.JellyfinAdapter -> ...\Amane.JellyfinAdapter\bin\Release\net9.0\Amane.JellyfinAdapter.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:02.12
```

解析出的编译引用：

```
Amane.Core\bin\Release\net8.0\Amane.Core.dll
jellyfin.naming\10.11.10\lib\net9.0\Emby.Naming.dll
jellyfin.common\10.11.10\lib\net9.0\MediaBrowser.Common.dll
jellyfin.controller\10.11.10\lib\net9.0\MediaBrowser.Controller.dll
jellyfin.model\10.11.10\lib\net9.0\MediaBrowser.Model.dll
```

### 8.3 Amane.EmbyAdapter（net8.0）· 官方 NuGet SDK 模式

```
  已还原 C:\Users\rappa\AppData\Local\Temp\amane-core-poc\Amane.EmbyAdapter\Amane.EmbyAdapter.csproj (用时 301 毫秒)。
  Amane.Core -> ...\Amane.Core\bin\Release\net8.0\Amane.Core.dll
  Amane.EmbyAdapter -> ...\Amane.EmbyAdapter\bin\Release\net8.0\Amane.EmbyAdapter.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:02.01
```

解析出的编译引用：

```
Amane.Core\bin\Release\net8.0\Amane.Core.dll
mediabrowser.common\4.10.0.24-beta2\lib\netstandard2.0\Emby.Media.Model.dll
mediabrowser.server.core\4.10.0.24-beta2\lib\netstandard2.0\Emby.Naming.dll
mediabrowser.common\4.10.0.24-beta2\lib\netstandard2.0\Emby.Web.GenericEdit.dll
mediabrowser.common\4.10.0.24-beta2\lib\netstandard2.0\MediaBrowser.Common.dll
mediabrowser.server.core\4.10.0.24-beta2\lib\netstandard2.0\MediaBrowser.Controller.dll
mediabrowser.common\4.10.0.24-beta2\lib\netstandard2.0\MediaBrowser.Model.dll
microsoft.extensions.logging.abstractions\8.0.0\lib\net8.0\Microsoft.Extensions.Logging.Abstractions.dll
```

`Microsoft.Extensions.Logging.Abstractions` 是**从 Core 传递过来**的（Emby 适配器自己没有包引用）——这正是「Core 只依赖宿主都有的最小公共依赖」的验证点。

`bin\Release\net8.0` 内容（**没有 MediaBrowser.\*，符合 Emby 插件部署要求**）：

```
Amane.Core.dll
Amane.EmbyAdapter.dll
```

### 8.4 同上 · 真实 Emby Server 4.10.0.40 程序集模式

（`dotnet build -p:UseRealEmbyAssemblies=true`）

```
  已还原 C:\Users\rappa\AppData\Local\Temp\amane-core-poc\Amane.EmbyAdapter\Amane.EmbyAdapter.csproj (用时 301 毫秒)。
  Amane.Core -> ...\Amane.Core\bin\Release\net8.0\Amane.Core.dll
  Amane.EmbyAdapter -> ...\Amane.EmbyAdapter\bin\Release\net8.0\Amane.EmbyAdapter.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:02.19
```

### 8.5 同一个解决方案图里的 4 个 TFM 一起构建

`Amane.CorePoc.sln` = net8.0 Core + net9.0 Jellyfin 适配器 + net8.0 Emby 适配器 + net10.0 Core 测试：

```
  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
  Amane.Core -> ...\Amane.Core\bin\Release\net8.0\Amane.Core.dll
  Amane.EmbyAdapter -> ...\Amane.EmbyAdapter\bin\Release\net8.0\Amane.EmbyAdapter.dll
  Amane.Core.Tests -> ...\Amane.Core.Tests\bin\Release\net10.0\Amane.Core.Tests.dll
  Amane.JellyfinAdapter -> ...\Amane.JellyfinAdapter\bin\Release\net9.0\Amane.JellyfinAdapter.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:03.07
```

**net8.0 Core 被 net9.0 与 net8.0 适配器同时引用并成功编译，成立。**

---

## 9. 运行时冒烟测试（Core 自己的测试程序集，21/21 PASS）

原仓库的 `tests/Jellyfin.Plugin.Amane.Tests` 通过 `InternalsVisibleTo` 用 `AmaneClient` 的 internal 构造函数。拆分后这条通道改为 Core 自己的测试程序集，**实测可用**：

```
PASS  InternalsVisibleTo: internal 测试构造函数在 Amane.Core.Tests 内可用
PASS  Core 持有 ProviderId 常量
PASS  NormalizeIdValue 剥离 Amane: 前缀
PASS  TryParseInternalId 接受正整数
PASS  TryParseInternalId 拒绝番号
PASS  seam ServerUrl: 相对路径补全宿主地址并裁掉尾部斜杠  -> http://amane.test:1234/api/resources/hash
PASS  seam ServerUrl: 已是 Amane 绝对 URL 原样返回
PASS  seam ServerUrl: ToDirectImageUrl 对本地资源返回 null
PASS  seam ServerUrl: ToDirectImageUrl 对外源原样返回
PASS  seam TimeoutSeconds=7 -> HttpClient.Timeout=12s（7+5s 兜底）  -> 00:00:12
PASS  seam ProviderIds: AmaneId 常量命中 /api/metadata/4242  -> /api/metadata/4242
PASS  seam ProviderIds: 反序列化出 AmaneMetadata
PASS  seam ApiToken: GetAsync 带上 Bearer  -> Bearer test-token
PASS  seam ActorCacheMinutes=60: 同名演员第二次查询命中进程内缓存  -> requests=1
PASS  演员解析结果正确
PASS  ClearActorCache 返回被清条目数（按名字 + id:N 双键，共 2 条）并失效缓存
PASS  清空后重新请求后端  -> requests=2
PASS  seam CheckHealthAsync: 可达 + 版本 + 鉴权 ok  -> reachable=True version=9.9.9 auth=ok
PASS  seam 图片: Bearer 只发给 Amane 域内 URL
PASS  图片成功路径不释放响应（流仍可读）
PASS  seam 图片: 不把 Bearer 泄露给第三方图床  -> null

ALL SMOKE CHECKS PASSED
```

意义：seam 不只是"能编译"，在**只有 BCL + Logging.Abstractions 的环境**下，ServerUrl / ApiToken / TimeoutSeconds / ActorCacheMinutes / ProviderId 常量五个 seam 的行为都与原实现一致；`ToDirectImageUrl` 的 null 语义、Bearer 只发 Amane 域内、成功响应不释放等原有约定全部保留。

（两处初版断言失败已修正并记录：① Timeout 断言位置早于首个 `CreateClient()`；② `ClearActorCache` 因按"名字 + `id:N`"双键写入，条目数是 2 不是 1。）

---

## 10. 类型冲突实验：`MediaBrowser.*` 会不会撞

### 10.1 负向探针

`collision-probe.csproj`（net9.0）**同时**引用：

```xml
<PackageReference Include="Jellyfin.Controller" Version="10.11.10" />
<PackageReference Include="Jellyfin.Model" Version="10.11.10" />
<PackageReference Include="MediaBrowser.Server.Core" Version="4.10.0.24-beta2" />
```

`Program.cs` 触碰两边都提供的同名类型：

```csharp
var a = typeof(MediaBrowser.Model.Entities.ImageType).Assembly.FullName;
var b = typeof(MediaBrowser.Controller.Entities.Movies.Movie).Assembly.FullName;
```

结果 —— **编译成功，0 warning 0 error**：

```
  已还原 ...\collision-probe.csproj (用时 2.45 秒)。
  collision-probe -> ...\collision-probe\bin\Release\net9.0\collision-probe.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:04.04
```

MSBuild 自己解析出的编译引用（`-getItem:ReferencePath -t:ResolveReferences`）显示 **Emby 的 `MediaBrowser.*` 被静默丢弃**，只剩 Emby 独有的两个程序集：

```
mediabrowser.common\4.10.0.24-beta2\lib\netstandard2.0\Emby.Media.Model.dll
jellyfin.naming\10.11.10\lib\net9.0\Emby.Naming.dll          ← 注意：连 Emby.Naming 也被 Jellyfin 的顶掉了
mediabrowser.common\4.10.0.24-beta2\lib\netstandard2.0\Emby.Web.GenericEdit.dll
jellyfin.common\10.11.10\lib\net9.0\MediaBrowser.Common.dll
jellyfin.controller\10.11.10\lib\net9.0\MediaBrowser.Controller.dll
jellyfin.model\10.11.10\lib\net9.0\MediaBrowser.Model.dll
```

运行探针（`<RollForward>Major</RollForward>` 让 net9.0 探针跑在 .NET 10 运行时上）确认实际绑定的是 **Jellyfin**：

```
MediaBrowser.Model, Version=10.11.10.0, Culture=neutral, PublicKeyToken=null
MediaBrowser.Controller, Version=10.11.10.0, Culture=neutral, PublicKeyToken=null
```

### 10.2 结论

> **编译期不会报「类型冲突」错误，而是按程序集简单名（simple name）静默去重，Jellyfin 的 net9.0 程序集胜出，全程 0 warning。**
> 真正会撞的是**运行期**：Emby 适配器若不小心（直接或传递地）拉进 Jellyfin 包，会拿 Jellyfin 的 `MediaBrowser.Controller.dll` 编译通过，然后在 Emby 宿主里 `TypeLoadException` / `MissingMethodException`。
> **因此"Core 不引用任何 MediaBrowser 类型"不是风格偏好，而是这个架构唯一的隔离机制。**

正向对照（各工程解析出的引用互不污染）：

| 工程 | `MediaBrowser.*` 来源 | 额外平台程序集 |
|---|---|---|
| `Amane.Core` | **无**（堆扫描也确认无） | 无 |
| `Amane.JellyfinAdapter` | `jellyfin.*\10.11.10\lib\net9.0\` | `Jellyfin.Data`（堆扫描可见）、ASP.NET Core 共享框架 |
| `Amane.EmbyAdapter` | `mediabrowser.*\4.10.0.24-beta2\lib\netstandard2.0\` | `Emby.Media.Model`、`Emby.Web.GenericEdit`、`Emby.Naming` |

### 10.3 建议的 CI 护栏

1. Emby 适配器工程里**禁止出现任何 `Jellyfin.*` 包引用**（可在 CI grep csproj）。
2. 构建后扫描两个适配器 DLL 的引用表：Emby 适配器不得含 `Jellyfin.Data` / `Jellyfin.*`；Jellyfin 适配器不得含 `Emby.Media.Model` / `Emby.Web.GenericEdit`。PoC 用的方法（`ilspycmd` 或字节堆扫描）可直接复用。
3. Emby 适配器不得开 `CopyLocalLockFileAssemblies`，确保 `MediaBrowser.*.dll` 不进插件目录。

---

## 11. 风险与必须实机验证的清单

PoC 只证明了**编译与 Core 运行时行为**，以下未验证：

1. **Emby 实机加载**：`BasePlugin` 发现流程、`GetPages()` 配置页是否真的渲染、Provider 是否被 Emby 扫描到（PoC 未在真实 Emby Server 进程内加载）。
2. **Emby 图片链路**：`HttpResponseInfo(IDisposable[])` 的生命周期与 Emby 消费 `Content` 流的时机；`TaskCanceledException` / 非 2xx 时 Emby 的行为与 Jellyfin 是否一致。PoC 只编译，未跑真实 HTTP。
3. **Emby 人物头像**：`PersonInfo.ImageUrl` 在 Emby 4.10 是否走 `ConvertImageToLocal`（不能带 token）——「直出路径必须用 `ToDirectImageUrl`」这条 Jellyfin 结论需要重新在 Emby 侧取证。
4. **Emby 配置持久化**：`BasePlugin<TConfiguration>` 的 XML 序列化、插件目录权限、`ServerUrl/ApiToken` 的读写时机；`MaxConcurrentRequests` 需重启才生效这条语义是否要保留。
5. **Emby provider id 键名**：`"Amane"` / `"AmaneId"` 是否需要在 Emby 的 `ExternalIdInfo` 里声明，是否会与内置 provider 冲突。Emby `IExternalId` 没有 `ExternalIdMediaType`，人物/影片的区分方式要重新设计。
6. **netstandard2.0 SDK vs 真实 net8.0 宿主程序集**：PoC 只证明同一份源码两边都能**编译**；`netstandard2.0` 编译产物的运行时行为是否与 net8.0 宿主完全一致未验证（发布插件时建议按真实程序集编译，或至少实机跑一遍）。
7. **Jellyfin 侧部署形态变化**（回归风险）：Core 变成独立程序集后，`bin/Release/net9.0` 会多出 `Amane.Core.dll`，必须一起拷进 `plugins/Amane/`；`CopyLocalLockFileAssemblies=true` 与新的 `ProjectReference` 的交互要跑一次真实部署（能加载、能刮削）。
8. **Jellyfin 12.x**：本 PoC 未涉及（仓库 AGENTS.md 已说明需要单独适配）。
9. **`MaxConcurrentRequests` 默认值语义**：原实现 `Plugin.Instance?.Configuration?.MaxConcurrentRequests ?? 4` 在「配置对象为 null」与「配置存在且为 0」两种情况下结果分别是 4 和 1（被 `Math.Max(1, ...)` 夹到 1）；现在由 `JellyfinAmaneSettings` 承担 `?? 4` 兜底，语义保持一致，但**这种默认值分散到适配层**是拆分的一个隐性代价，建议在接口 XML 文档里写死契约。
10. **`GetAsync` 不读 `_apiTokenOverride`** 这点原样保留了（只有图片与健康检查路径读覆盖值）。这在原实现里看起来像不一致（测试注入的 token 对元数据查询不生效），拆分时**没有顺手改**以避免行为漂移；是否要修是独立决策。

---

## 12. 复现步骤

```powershell
$env:DOTNET_ROOT='C:\Users\rappa\.dotnet'; $env:PATH="$env:DOTNET_ROOT;$env:PATH"

# 1. 还原仓库副本并应用 seam（幂等，会断言每处替换的出现次数；不写仓库）
& "$env:TEMP\amane-core-poc\setup-poc.ps1"

# 2. 三个工程 + Core 测试一起构建
dotnet build "$env:TEMP\amane-core-poc\Amane.CorePoc.sln" -c Release -v minimal

# 3. Emby 适配器改用真实 Emby 4.10.0.40 程序集再构建一次
dotnet build "$env:TEMP\amane-core-poc\Amane.EmbyAdapter\Amane.EmbyAdapter.csproj" `
             -c Release -p:UseRealEmbyAssemblies=true

# 4. Core seam 运行时冒烟（21 项）
dotnet run --project "$env:TEMP\amane-core-poc\Amane.Core.Tests\Amane.Core.Tests.csproj" -c Release

# 5. 负向实验：两套 MediaBrowser.* 同栈 → 静默去重
dotnet build "$env:TEMP\amane-core-poc\collision-probe\collision-probe.csproj" -c Release
```

日志：`%TEMP%\amane-core-poc\log-*.txt`。

---

## 13. 追加实验：TFM 矩阵（net8.0 vs netstandard2.0）与 bin 输出污染

> Lead 追加验证请求。新增工程：`Amane.Core.NetStandard`（netstandard2.0）、`Amane.EmbyAdapter.Ns20`（netstandard2.0）、`Amane.JellyfinAdapter.Ns20`（net9.0 + netstandard2.0 Core）、`baseline-repo-src`（仓库副本，用于取干净基线）。

## 13.0 基线更正（重要）

Lead 给的基线「`bin/Release/net9.0` 里只有一个 `Jellyfin.Plugin.Amane.dll`」**与实际不符**。用仓库的干净副本重新构建（repo 未被改动）：

```powershell
robocopy <repo> $env:TEMP\amane-core-poc\baseline-repo-src /E /XD .git bin obj
dotnet build "$env:TEMP\amane-core-poc\baseline-repo-src\Jellyfin.Plugin.Amane.csproj" -c Release
```

```
  已还原 ...\baseline-repo-src\Jellyfin.Plugin.Amane.csproj (用时 322 毫秒)。
  Jellyfin.Plugin.Amane -> ...\baseline-repo-src\bin\Release\net9.0\Jellyfin.Plugin.Amane.dll

已成功生成。
    0 个警告
    0 个错误
```

**基线 = 30 个 dll**（不是 1 个）：

```
BitFaster.Caching.dll            Jellyfin.Data.dll                 Microsoft.Extensions.Caching.Memory.dll
Diacritics.dll                   Jellyfin.Database.Implementations.dll   Microsoft.Extensions.Configuration.Abstractions.dll
Emby.Naming.dll                  Jellyfin.Extensions.dll           Microsoft.Extensions.Configuration.Binder.dll
ICU4N.dll                        Jellyfin.MediaEncoding.Keyframes.dll    Microsoft.Extensions.DependencyInjection.Abstractions.dll
ICU4N.Transliterator.dll         Jellyfin.Plugin.Amane.dll         Microsoft.Extensions.DependencyInjection.dll
J2N.dll                          MediaBrowser.Common.dll           Microsoft.Extensions.Logging.Abstractions.dll
NEbml.Core.dll                   MediaBrowser.Controller.dll       Microsoft.Extensions.Logging.dll
Polly.Core.dll                   MediaBrowser.Model.dll            Microsoft.Extensions.Options.dll
Polly.dll                        Microsoft.EntityFrameworkCore.dll Microsoft.Extensions.Primitives.dll
                                 Microsoft.EntityFrameworkCore.Abstractions.dll
                                 Microsoft.EntityFrameworkCore.Relational.dll
```

原因：仓库根 csproj 的 `CopyLocalLockFileAssemblies=true` 会把整张依赖图拷进 bin；`PrivateAssets=all` 只阻止资产**流向引用方**（测试项目），**不阻止**本项目自己的 copy-local。仓库现存的 `bin/Release/net9.0` 也是同样 30 个 dll，两边一致。

**这不改变结论，反而降低了「多出 dll」的敏感度**：AGENTS.md 的「把 bin 下所有 dll 拷到 plugins/Amane/」本来就在拷 30 个宿主程序集。真正的红线是**相对这 30 个不能多出任何第三方程序集**（尤其 `System.Text.Json.dll`）。

## 13.1 netstandard2.0 Core 缺什么（实测报错原文）

第一次尝试（只加 `System.Text.Json` 8.0.0，`LangVersion=latest`）：

```
AmaneSettings.cs(36,37): error CS0518: 预定义类型“System.Runtime.CompilerServices.IsExternalInit”未定义或导入   ×5
    4 个警告
    5 个错误
```

加 `IsExternalInit` 垫片后（只影响新增的 `AmaneSettings.cs`，仓库原有代码没用 `init`）：

```
AmaneClient.cs(238,23): error CS0518: 预定义类型“System.Range”未定义或导入
AmaneClient.cs(238,23): error CS0518: 预定义类型“System.Index”未定义或导入
AmaneClient.cs(393,43): error CS1503: 参数 1: 无法从“char”转换为“string”      ← url.StartsWith('/')
AmaneClient.cs(415,28): error CS1503: 参数 1: 无法从“char”转换为“string”      ← url.StartsWith('/')
AmaneClient.cs(501,50): error CS1501: “ReadAsStreamAsync”方法没有采用 1 个参数的重载
AmaneClient.cs(617,54): error CS1501: “ReadAsStreamAsync”方法没有采用 1 个参数的重载
    12 个警告
    6 个错误
```

逐条结论（**「Range/Index 可能需要 System.Memory」这个假设不成立**）：

| 现象 | 位置 | netstandard2.0 下缺什么 | 解决 |
|---|---|---|---|
| `init` 访问器 | `AmaneSettings.cs`（PoC 新增文件，非仓库代码） | `System.Runtime.CompilerServices.IsExternalInit`（netstandard2.0 无此类型） | 加 3 行垫片，或把 `init` 改成 `set` |
| `trimmed["Amane:".Length..]` 范围运算符 | `AmaneClient.cs:238`（仓库代码） | `System.Range` / `System.Index`。**`System.Memory` 4.5.5 并不提供这两个类型**——实测字节扫描该 DLL：`System.Range=False`、`System.Index=False`；显式引用 `System.Memory` 后仍然报同样的 CS0518 | 改写成 `trimmed.Substring("Amane:".Length)`（行为等价），或用社区垫片（`IndexRange` / `PolySharp`）。**不需要 `System.Memory` 显式引用** |
| `url.StartsWith('/')` | `AmaneClient.cs:393`、`:415`（仓库代码，2 处） | netstandard2.0 的 `String` 没有 `StartsWith(char)` 重载 | 改成 `url.StartsWith("/", StringComparison.Ordinal)`（在 net8.0 上也合法） |
| `ReadAsStreamAsync(ct)` | `AmaneClient.cs:501`、`:617`（仓库代码，2 处） | 带 `CancellationToken` 的重载是 .NET 5+ API，netstandard2.0 没有（`System.Net.Http` 包也补不了） | 改成 `ReadAsStreamAsync()`，**代价：流读取不再观察取消令牌**（因为 `SendAsync` 默认 `ResponseContentRead`，内容已缓冲，影响有限） |
| 12 个可空性警告 | `AmaneClient.cs` 多处 | netstandard2.0 引用程序集没有可空注解，`string.IsNullOrWhiteSpace` 等不再是流敏感的 | 只能告警抑制或用 `[NotNullWhen]` 垫片；**同一份源码在 net8.0 下是 0 警告** |

其它依赖全部可用（无需额外包）：`SemaphoreSlim`、`ConcurrentDictionary`、`HttpClient` / `HttpRequestMessage` / `AuthenticationHeaderValue`、`Stopwatch`、`ILogger<T>`、`System.Text.Json`（8.0.0 支持 netstandard2.0）、`Array.Empty<T>`、`IReadOnlyDictionary`。

**最终 netstandard2.0 Core 构建结果：**

```
    12 个警告
    0 个错误
已用时间 00:00:01.47
```

（12 个警告全部是上面那类可空性告警；另有 `NU1903: 包 "System.Text.Json" 8.0.0 具有已知的 高 严重性漏洞`（GHSA-8g4q-xg66-9fp4 / GHSA-hh2w-p6rv-4g7w），**无论选哪个 TFM 都建议把 System.Text.Json 提到 8.0.5+ / 9.x 已修补版本**。）

## 13.2 netstandard2.0 Emby 适配器需要什么

源码是 `Amane.EmbyAdapter` 的副本 + **3 处**修正（`ns20-emby-replacements.txt`）：

| 位置 | net8.0 写法 | netstandard2.0 必须改成 |
|---|---|---|
| `EmbySeams.cs` | `new SocketsHttpHandler { PooledConnectionLifetime = ... }` | `new HttpClientHandler()`（netstandard2.0 无 `SocketsHttpHandler`，也无 `PooledConnectionLifetime`） |
| `EmbyAmaneMovieProvider.cs` | `ReadAsStreamAsync(cancellationToken)` | `ReadAsStreamAsync()` |
| `EmbyAmaneImageProvider.cs` | `ReadAsStreamAsync(cancellationToken)` | `ReadAsStreamAsync()` |

构建结果：**0 个错误**（12 个警告全部来自 netstandard2.0 Core）。

## 13.3 bin 输出实测（完整清单）

### Jellyfin 适配器 bin（net9.0，`CopyLocalLockFileAssemblies=true`，与仓库一致）

| 组合 | dll 数 | 相对基线的差异 | System.Text.Json.dll / System.Memory.dll / Microsoft.Bcl.AsyncInterfaces.dll |
|---|---|---|---|
| 基线（未拆分，仓库现状） | 30 | — | 无 |
| 适配器 + **net8.0** Core | **31** | `−Jellyfin.Plugin.Amane.dll`、`+Amane.Core.dll`、`+Amane.JellyfinAdapter.dll` | **无 / 无 / 无** |
| 适配器 + **netstandard2.0** Core | **31** | `−Jellyfin.Plugin.Amane.dll`、`+Amane.Core.NetStandard.dll`、`+Amane.JellyfinAdapter.Ns20.dll` | **无 / 无 / 无** |

显式存在性检查（两种组合都是 False）：

```
System.Text.Json.dll = False
System.Memory.dll = False
Microsoft.Bcl.AsyncInterfaces.dll = False
System.Runtime.CompilerServices.Unsafe.dll = False
```

`Amane.JellyfinAdapter.Ns20.deps.json` 的 `libraries` 里只有 `Amane.JellyfinAdapter.Ns20/1.0.0` 和 `Amane.Core.NetStandard/1.0.0`，**没有 System.Text.Json**。

**为什么 netstandard2.0 Core 也没污染 Jellyfin bin**：.NET SDK 的 `Microsoft.NETCore.App.Ref/data/PackageOverrides.txt` 把一批包标记为"框架已提供"，net9.0 目标下 NuGet 直接剪枝：

```
System.Buffers|5.0.0
System.Memory|5.0.0
System.Numerics.Vectors|5.0.0
System.Runtime.CompilerServices.Unsafe|7.0.0
System.Text.Encodings.Web|10.0.12
System.Text.Json|10.0.12
System.Threading.Tasks.Extensions|5.0.0
```

> **结论：Jellyfin 侧 bin 不受 Core TFM 影响，拆分只多出 `Amane.Core.dll` 一个程序集。**

### Emby 适配器 bin

**默认（不设 `CopyLocalLockFileAssemblies`）= 2 个 dll，两种 TFM 都干净：**

```
net8.0 Core + net8.0 适配器:      Amane.Core.dll, Amane.EmbyAdapter.dll
netstandard2.0 Core + ns2.0 适配器: Amane.Core.NetStandard.dll, Amane.EmbyAdapter.Ns20.dll
```

**`-p:CopyLocalLockFileAssemblies=true`（打包代理，这才是真实风险面）：**

| 组合 | 文件数 | 清单 |
|---|---|---|
| net8.0 Core + net8.0 适配器 | **14** | `Amane.Core.*`、`Amane.EmbyAdapter.*`、`Emby.Media.Model.dll`、`Emby.Naming.dll`、`Emby.Web.GenericEdit.dll`、`MediaBrowser.Common.dll`、`MediaBrowser.Controller.dll`、`MediaBrowser.Model.dll`、`Microsoft.Extensions.DependencyInjection.Abstractions.dll`、`Microsoft.Extensions.Logging.Abstractions.dll` |
| netstandard2.0 Core + ns2.0 适配器 | **22** | 上面 14 个之外**多出 8 个**：`Microsoft.Bcl.AsyncInterfaces.dll`、`System.Text.Json.dll`、`System.Memory.dll`、`System.Buffers.dll`、`System.Numerics.Vectors.dll`、`System.Runtime.CompilerServices.Unsafe.dll`、`System.Text.Encodings.Web.dll`、`System.Threading.Tasks.Extensions.dll` |

两种组合都会拷出 `MediaBrowser.*` / `Emby.*` —— 这些**绝对不能进 Emby 插件目录**，所以 Emby 侧的打包必须显式排除（`<Private>false</Private>` / 打包时过滤），不能简单沿用 `CopyLocalLockFileAssemblies=true`。

**netstandard2.0 那 8 个多出来的程序集正是 Lead 提到的历史事故形态**：`Microsoft.Bcl.AsyncInterfaces.dll`（Emby 4.8/.NET 6 上 `Could not load file or assembly` 的元凶）连同 `System.Text.Json.dll` / `System.Memory.dll` 一起随插件分发，会与宿主已有程序集同名共存。

## 13.4 结论表：Core TFM × 适配器 TFM

| # | Core TFM | Emby 适配器 TFM | 编译 | Jellyfin bin 干净？ | Emby bin 干净？ | 已知风险 |
|---|---|---|---|---|---|---|
| **A（推荐）** | **net8.0** | **net8.0** | ✅ 0 error 0 warning；且 NuGet SDK 与真实 Emby 4.10.0.40 程序集两种模式都过 | ✅ 31 个，只比基线多 `Amane.Core.dll` | ✅ 默认 2 个；copy-local 14 个（无任何 netstandard2.0 垫片） | 无新增依赖分发风险；只需打包时排除 `MediaBrowser.*` |
| B | net8.0 | netstandard2.0 | ✅ 0 error（3 处源码修正） | — | ✅ copy-local 14 个，**无** Bcl.AsyncInterfaces | 与 A 等价，但 netstandard2.0 无收益 |
| C | netstandard2.0 | net8.0 | ✅ 0 error（Core 3 处源码修正 + IsExternalInit 垫片） | ✅ 31 个，框架剪枝后**无** System.Text.Json | ✅ copy-local 14 个，无垫片 | Jellyfin 侧安全；Emby 侧无收益 |
| **D** | **netstandard2.0** | **netstandard2.0** | ✅ 0 error（Core 3 处 + Emby 3 处 + 垫片） | ✅ 31 个（框架剪枝） | ⚠️ **copy-local 22 个，多出 `Microsoft.Bcl.AsyncInterfaces` / `System.Text.Json` / `System.Memory` / `System.Buffers` / `System.Numerics.Vectors` / `System.Runtime.CompilerServices.Unsafe` / `System.Text.Encodings.Web` / `System.Threading.Tasks.Extensions`** | **高**：正是 Emby 4.8/.NET 6 `Could not load file or assembly` 的历史事故形态；且 Emby 4.8 运行时是 .NET 6，net8.0 Core 本身就装不上 4.8 —— 4.8 支持与 net8.0 目标互斥，是独立的产品决策 |

**推荐 A（net8.0 Core + net8.0 Emby 适配器 + net9.0 Jellyfin 适配器）**，理由：

1. 编译证据最干净：Core 侧 **0 warning 0 error**（netstandard2.0 是 12 个可空性警告 + 3 处源码劣化）。
2. 不需要 `IsExternalInit` 垫片、不需要改写范围运算符、不需要牺牲 `ReadAsStreamAsync(ct)` 的取消语义、不需要把 `StartsWith(char)` 改写成字符串重载。
3. Emby 侧不会带出 `Microsoft.Bcl.AsyncInterfaces` 等 netstandard2.0 垫片 —— 零依赖分发风险。
4. Emby 4.9/4.10 的宿主就是 .NET 8，`net8.0` 与宿主 TFM 完全对齐（Emby 官方 SDK 用 netstandard2.0 只是为了**同时兼容 4.8**；如果不打算支持 4.8，就是白付风险）。
5. netstandard2.0 的**唯一**价值是理论上兼容 Emby 4.8（.NET 6）——但 net8.0 Core 在 .NET 6 上根本跑不起来，所以要走 netstandard2.0 才能谈 4.8 支持；这是「要不要支持 Emby 4.8」的产品决策，不是 TFM 技术选型。

**若最终仍选 netstandard2.0**，必须同时满足：
- 打包脚本**不得**把 `System.Text.Json.dll` / `System.Memory.dll` / `System.Buffers.dll` / `System.Numerics.Vectors.dll` / `System.Runtime.CompilerServices.Unsafe.dll` / `System.Text.Encodings.Web.dll` / `System.Threading.Tasks.Extensions.dll` / `Microsoft.Bcl.AsyncInterfaces.dll` / `MediaBrowser.*` / `Emby.*` 放进插件目录（只放 `Amane.Core.NetStandard.dll` + `Amane.EmbyAdapter.Ns20.dll`，其余由宿主提供）；
- 在目标 Emby 版本上实机验证 `System.Text.Json` 版本绑定（netstandard2.0 编译期引用 8.0.0，Emby 4.10 宿主运行期提供 9.x/10.x，靠 .NET 的向上版本滚动加载）。

## 13.5 复现命令

```powershell
$env:DOTNET_ROOT='C:\Users\rappa\.dotnet'; $env:PATH="$env:DOTNET_ROOT;$env:PATH"
$root = "$env:TEMP\amane-core-poc"

# 干净基线（仓库副本，repo 不被改动）
robocopy 'C:\Users\rappa\Documents\Projects\Jellyfin.Plugin.Amane' "$root\baseline-repo-src" /E /XD .git bin obj
dotnet build "$root\baseline-repo-src\Jellyfin.Plugin.Amane.csproj" -c Release

# netstandard2.0 Core（重放 3 处源码修正）
& "$root\setup-ns20.ps1"
dotnet build "$root\Amane.Core.NetStandard\Amane.Core.NetStandard.csproj" -c Release

# netstandard2.0 Emby 适配器（重放 3 处源码修正）
Copy-Item "$root\Amane.EmbyAdapter\*.cs" "$root\Amane.EmbyAdapter.Ns20\" -Force
& "$root\apply-spec.ps1" -Spec "$root\ns20-emby-replacements.txt" -Root "$root\Amane.EmbyAdapter.Ns20"
dotnet build "$root\Amane.EmbyAdapter.Ns20\Amane.EmbyAdapter.Ns20.csproj" -c Release

# Jellyfin 适配器 + netstandard2.0 Core
dotnet build "$root\Amane.JellyfinAdapter.Ns20\Amane.JellyfinAdapter.Ns20.csproj" -c Release

# 打包代理：看 copy-local 会把什么带出来
dotnet build "$root\Amane.EmbyAdapter.Ns20\Amane.EmbyAdapter.Ns20.csproj" -c Release `
             -p:CopyLocalLockFileAssemblies=true -o "$root\emby-ns20-copylocal"
dotnet build "$root\Amane.EmbyAdapter\Amane.EmbyAdapter.csproj" -c Release `
             -p:UseRealEmbyAssemblies=false -p:CopyLocalLockFileAssemblies=true -o "$root\emby-net8-copylocal"
```

日志：`log-core-ns20-build.txt`、`log-emby-ns20-build.txt`、`log-jellyfin-adapter-ns20-build.txt`、`log-baseline-repo-build.txt`。

## 13.6 最终全量构建（7 个工程 / 4 个 TFM，一次图）

`Amane.CorePoc.sln` 加入 netstandard2.0 变体后：

```
  Amane.Core.NetStandard -> ...\Amane.Core.NetStandard\bin\Release\netstandard2.0\Amane.Core.NetStandard.dll
  Amane.Core -> ...\Amane.Core\bin\Release\net8.0\Amane.Core.dll
  Amane.EmbyAdapter.Ns20 -> ...\Amane.EmbyAdapter.Ns20\bin\Release\netstandard2.0\Amane.EmbyAdapter.Ns20.dll
  Amane.Core.Tests -> ...\Amane.Core.Tests\bin\Release\net10.0\Amane.Core.Tests.dll
  Amane.JellyfinAdapter.Ns20 -> ...\Amane.JellyfinAdapter.Ns20\bin\Release\net9.0\Amane.JellyfinAdapter.Ns20.dll
  Amane.EmbyAdapter -> ...\Amane.EmbyAdapter\bin\Release\net8.0\Amane.EmbyAdapter.dll
  Amane.JellyfinAdapter -> ...\Amane.JellyfinAdapter\bin\Release\net9.0\Amane.JellyfinAdapter.dll

已成功生成。
    4 个警告
    0 个错误
```

（4 个警告全部是 `NU1903`：`System.Text.Json` 8.0.0 的已知高危漏洞 —— 见 §13.1 末尾的建议。）

复跑确认（源码重新生成后）：Core 冒烟 **21/21 PASS**；Emby 适配器在 NuGet SDK 模式与真实 Emby 4.10.0.40 程序集模式下均 **0 错误**；Jellyfin 适配器 bin 仍为 **31 个 dll 且无 `System.Text.Json.dll`**；Emby 适配器 bin 仍为 **2 个 dll**。
