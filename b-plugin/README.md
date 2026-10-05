# Jellyfin.Plugin.LocalMeta（B 阶段）

Jellyfin 人物资料的**本地兜底 provider**。跟 MetaTube 这类远程刮削器互补：
`Order = 100` 排在它们后面，只在头像/简介为空时才补，永不覆盖远程刮到的内容。

与 A 阶段的 `localmeta/localmeta.py` 是**两条互为备份的同一套逻辑**，
数据源、罩杯映射、姓名归一化规则两边保持一致，不会分叉。

**状态：已在 Jellyfin 10.11.6 上编译通过 + 加载验证通过（2026-10-05）。**
验证方式不是"看着像对"，而是用 `dotnet-setup/loadtest` 反射加载产物，
逐条确认 Jellyfin 能识别出三个 provider 实现。输出见文末「验证记录」。

## 目录

```
jellyfin-plugin-localmeta/
├─ build.ps1                                 编译 + 部署 / 卸载（含产物安全校验）
└─ Jellyfin.Plugin.LocalMeta/
   ├─ Jellyfin.Plugin.LocalMeta.csproj       net9.0 + Jellyfin.Controller 10.11.6
   ├─ Plugin.cs                              插件入口（BasePlugin<T> + IHasWebPages）
   ├─ PluginConfiguration.cs                 全部配置项，只增不删
   ├─ Configuration/
   │  └─ configPage.html                     配置页，内嵌成资源
   ├─ Sources/
   │  └─ LocalMetaSources.cs                 源抽象 + JavBoss sqlite + Gfriends 头像 + 工厂
   ├─ Providers/
   │  ├─ LocalMetaPersonMetadataProvider.cs  ILocalMetadataProvider<Person>
   │  └─ LocalMetaPersonImageProvider.cs     IRemoteImageProvider
   └─ Tasks/
      └─ LocalMetaBackfillTask.cs            IScheduledTask，每天跑一次
```

## 编译

需要 .NET 9 SDK。本机实测装在 `<USERPROFILE>\.dotnet\dotnet.exe`（9.0.318）：

```bat
powershell -ExecutionPolicy Bypass -File build.ps1
```

编译 + 部署到 `C:\Jellyfin\Data\plugins\Jellyfin.Plugin.LocalMeta`。
`build.ps1` 会自动挑**真正带 SDK** 的那个 dotnet（见下方「PATH 陷阱」）。

> `-PackOnly` 只编译不部署；`-Uninstall` 卸载（会先把旧目录改名备份）。

## PATH 陷阱（本机实测）

`C:\Program Files\dotnet` 存在但**只有 runtime、没有 sdk 目录**，而它在 PATH 里排前面。
直接敲 `dotnet` 会命中这个空壳，`--list-sdks` 返回空、`--version` 无输出，
看起来像"没装 SDK"。真相是 SDK 装在 `%USERPROFILE%\.dotnet`。

`build.ps1` 逐个候选跑 `--list-sdks` 探测，谁有 SDK 用谁，不用你手动改 PATH。

## 部署后

1. **重启 Jellyfin**（dll 不是热加载的，不重启不生效）
2. 后台 → 插件 → 已安装：看 LocalMeta 是否出现
3. 配置页填两个路径（见下表）
4. 后台 → 计划任务 → 跑一次「LocalMeta 人物资料补齐」
5. 控制台日志搜 `[LocalMeta]`

## 配置页（后台 → 插件 → LocalMeta）

| 分组 | 项 | 作用 |
|---|---|---|
| 总开关 | 启用插件 | 关掉后 provider 和任务都不动数据 |
| 总开关 | 目标媒体库 | 逗号分隔，留空不限库 |
| 总开关 | 只补空不覆盖 | 默认关（=只补空，安全） |
| 总开关 | 单次最多处理人数 | 默认 100，防大库跑太久 |
| 资料源 | JavBoss 路径 / 头像源目录 | 留空自动探测 |
| 资料源 | 姓名替换规则 | `原名\|替换名` 每行一条 |
| 输出格式 | 简介模板 | `{bust} {waist} {hips} {cup} {height} {debut}` |
| 输出格式 | 罩杯字母表 | 默认 `ABCDEFGHIJKLMNOPQ` |
| 输出格式 | 身高/围度区间 | 越界脏数据直接跳过 |

配置文件落在 `C:\Jellyfin\Data\plugins\configurations\Jellyfin.Plugin.LocalMeta.xml`
（**XML**，不是 JSON —— Jellyfin 插件配置走 `IXmlSerializer`）。

## 升级与自定义（重点）

### 加一个新数据源

1. 在 `Sources/LocalMetaSources.cs` 里实现 `ILocalMetaProfileSource`
   （`TryGetProfile` / `TryGetAvatarPath` 各一个）
2. 在 `LocalMetaSourceFactory.Build()` 里挂一行
3. A 阶段 `config.json` 加一条 `sources` 项

provider 和计划任务都不用改 —— 它们只面向接口编程。

### 改输出格式

不要改代码，去配置页填「简介模板」。想加个血型就扩 `PersonProfile`，
并在 `RenderOverview` 里补占位符替换。

### 跟着 Jellyfin 升级

| 步骤 | 做法 |
|---|---|
| 1 | 先取证，别先改代码（见下） |
| 2 | `csproj` 里把 `Jellyfin.Controller` 版本号改成目标大版本 |
| 3 | `Microsoft.Data.Sqlite` 版本对齐目标版 Jellyfin 自带版本 |
| 4 | `build.ps1 -PackOnly`，看编译有没有红 |
| 5 | 重新部署 + 重启，看日志 |

**第 1 步的做法（重要）**：10.11 相比 10.9 有实质变动，照老教程写必编译不过。
用取证工具直接读目标版本 DLL 的元数据：

```bat
cd ..\dotnet-setup\apiprobe
"<USERPROFILE>\.dotnet\dotnet.exe" build -c Release
:: 查某个接口的完整签名
"<USERPROFILE>\.dotnet\dotnet.exe" bin\Release\net9.0\apiprobe.dll C:\Jellyfin IRemoteImageProvider
:: 列出整个命名空间有哪些接口
"<USERPROFILE>\.dotnet\dotnet.exe" bin\Release\net9.0\apiprobe.dll C:\Jellyfin NS:MediaBrowser.Controller.Providers
```

这比查文档可靠 —— 文档说的是 10.7/10.9，你装的是 10.11.6。

## 已实测的扩展点清单（10.11.6）

全部从本地 `C:\Jellyfin` 的 DLL 元数据取证，不是照抄教程：

| 接口 / 类型 | 真实位置 | 备注 |
|---|---|---|
| `ILocalMetadataProvider<Person>` | `MediaBrowser.Controller.Providers` | **不是** `IMetadataProvider<T>` |
| `GetMetadata(ItemInfo, IDirectoryService, CancellationToken)` | 同上 | 10.x 改成从 `ItemInfo` 出发返回新 result，不是 `(result, ct)` |
| `IRemoteImageProvider` | 同上 | 需实现 `Supports(BaseItem)` + `GetImageResponse` |
| `GetSupportedImages(BaseItem)` | 同上 | 返回 `IEnumerable<ImageType>`，**不是** `BaseItemKind` |
| `RemoteImageInfo` | **`MediaBrowser.Model.Providers`** | 不是 `MediaBrowser.Model.Images` |
| `BaseItemKind` | **`Jellyfin.Data.Enums`** | 10.11 从 Model 搬到了 Jellyfin.Data |
| `BasePlugin<T>` / `IPlugin` | `MediaBrowser.Common.Plugins` | |
| 配置基类 | `MediaBrowser.Model.Plugins.BasePluginConfiguration` | **10.11 移除了 `IPluginConfiguration`** |
| 配置序列化 | `MediaBrowser.Model.Serialization.IXmlSerializer` | **走 XML，不是 JSON** |
| `IScheduledTask` | `MediaBrowser.Model.Tasks` | 10.11 **新增 `Key`**，不实现编译不过 |
| `ExecuteAsync(IProgress<double>, CancellationToken)` | 同上 | **progress 在前**，老文档多是反的 |
| 计划任务枚举 | `TaskTriggerInfoType.IntervalTrigger` | 不是 `TaskTriggerInfo.TriggerInterval` |
| `IHasWebPages` / `PluginPageInfo` | `MediaBrowser.Model.Plugins` | 10.9+ 走内嵌资源，老的 `GetConfigurationPageHtml()` 已废弃 |
| `IHasOrder.Order` | `MediaBrowser.Controller.Providers` | 升序调用，值大 = 更晚兜底 |

## 踩过的坑

- **宿主程序集不能进插件目录。** `dotnet publish` 会把 `Jellyfin.Controller`
  的传递依赖全带出来（38 个文件，含 604KB 的 `MediaBrowser.Controller.dll`）。
  覆盖进 `C:\Jellyfin` 会导致程序集版本冲突甚至 Jellyfin 起不来。
  csproj 里加了 `RemoveHostAssemblies` 目标剔除 `MediaBrowser.*` / `Jellyfin.*`，
  `build.ps1` 部署前再校验一次（发现残留直接 exit 5）。
  坑点：通配符 `Jellyfin.*.dll` 会把插件自己的 `Jellyfin.Plugin.LocalMeta.dll` 一起删掉，
  必须显式 `Exclude` 自身。
- **依赖版本要对齐宿主。** Jellyfin 10.11.6 自带 `Microsoft.Data.Sqlite` 9.0.x、
  `SQLitePCLRaw` 2.1.10、`Newtonsoft.Json` 13.0.4。csproj 里的版本要跟上，
  否则插件和宿主抢 SQLite 原生库。
- **`.ps1` 必须是 UTF-8 with BOM。** PowerShell 5.1 默认按 GBK 解码 `.ps1`，
  中文注释会把整行解析带崩（症状：注册对象报"参数为 Null"）。
  本机执行策略是 `Restricted`，跑脚本要 `-ExecutionPolicy Bypass`。
- **别用 SYSTEM 账户注册计划任务。** A 阶段注册时踩过，非管理员 shell 会失败。
- **Jellyfin 运行中改 db 不生效。** 内存有缓存，页面要重启才刷新。插件同理（换 dll 必须重启）。

## 验证记录（2026-10-05）

`dotnet-setup/loadtest` 反射加载产物后的实测输出：

```
OK  : 程序集加载成功 -> Jellyfin.Plugin.LocalMeta, Version=0.3.0.0
OK  : 类型全部解析成功，共 17 个
OK  : Plugin 基类 = MediaBrowser.Common.Plugins.BasePlugin<PluginConfiguration>
OK  : Plugin 实现 IHasWebPages
OK  : PluginConfiguration 基类 = MediaBrowser.Model.Plugins.BasePluginConfiguration
OK  : ILocalMetadataProvider<Person> 已实现
OK  : IRemoteImageProvider 已实现
OK  : IScheduledTask 已实现
OK  : IScheduledTask.Key = LocalMetaActorRefill
OK  : 嵌入资源: Jellyfin.Plugin.LocalMeta.Configuration.configPage.html
```

这验证了「编译过」之外更重要的一件事：Jellyfin 的 DI 容器能识别出这三个 provider，
配置页资源名也和 `GetPages()` 里的拼接一致。

**还没验证的**：装进真实 Jellyfin 后的运行期行为（数据源路径探测、
计划任务实际写库效果）。那需要重启 Jellyfin 并在后台点一次，属于交互步骤。

## 回滚

```bat
powershell -ExecutionPolicy Bypass -File build.ps1 -Uninstall
:: 或手工删 C:\Jellyfin\Data\plugins\Jellyfin.Plugin.LocalMeta
```

`build.ps1` 部署前会把旧目录改名成 `.bak_<时间戳>`，回滚就是改回原名。

数据回滚：把 `jellyfin.db.bak_localmeta_*` 覆盖回去，
头像文件在 `C:\Jellyfin\Data\metadata\People` 下手工删。
