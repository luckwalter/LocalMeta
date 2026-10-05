# Jellyfin.Plugin.LocalMeta（B 阶段）

Jellyfin 人物资料的**本地兜底 provider**。跟 MetaTube 这类远程刮削器互补：
`Order = 100` 排在它们后面，只在头像/简介为空时才补，永不覆盖远程刮到的内容。

与 A 阶段的 `localmeta/localmeta.py` 是**两条互为备份的同一套逻辑**，
数据源、罩杯映射、姓名归一化规则两边保持一致，不会分叉。

**状态：**
- Jellyfin **10.11.6**：编译通过 + 加载验证通过（2026-10-05）
- Jellyfin **12.1.0**：**已随宿主升级**——TFM 重定 `net10.0`、引用升到
  `Jellyfin.Controller 12.1.0`，重新编译并部署，实测
  `Loaded plugin: "LocalMeta" "0.3.0.0"` + `[LocalMeta] 载入资料源 2 个`。

（此前"net9.0 插件在 net10.0 宿主下也能跑"的结论仍成立，但既然宿主已是 net10.0，
  就同步过去：接口签名经 apiprobe 取证确认与 10.11 完全一致，升级零改动。）

10.11.6 的验证方式不是"看着像对"，而是用 `dotnet-setup/loadtest` 反射加载产物，
逐条确认 Jellyfin 能识别出三个 provider 实现。输出见文末「验证记录」。

12.1.0 的适配结论与数据库 schema 变更见
[`../docs/10-升级适配-Jellyfin-12.1.0.md`](../docs/10-升级适配-Jellyfin-12.1.0.md)。

## 目录

```
jellyfin-plugin-localmeta/
├─ build.ps1                                 编译 + 部署 / 卸载（含产物安全校验）
└─ Jellyfin.Plugin.LocalMeta/
   ├─ Jellyfin.Plugin.LocalMeta.csproj       net10.0 + Jellyfin.Controller 12.1.0
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

需要 **.NET 10 SDK**。本机实测装在 `<USERPROFILE>\.dotnet\dotnet.exe`（10.0.401）：

```bat
powershell -ExecutionPolicy Bypass -File build.ps1 -PackOnly
```

> 注意 `C:\Program Files\dotnet` 在 PATH 里排前面但**没有 SDK**，直接敲 `dotnet`
> 会报"No .NET SDKs were found"。`build.ps1` 会自动探测真正带 SDK 的那个。

装 SDK（非管理员，装到用户目录）：

```powershell
# 官方脚本默认装在 %USERPROFILE%\.dotnet，无需管理员
& .\dotnet-install.ps1 -Version 10.0.401 -Architecture x64 -InstallDir "$env:USERPROFILE\.dotnet"
```

> 实测坑：官方脚本解压 300MB 的 zip 时**可能中途静默中断**，
> 表现为 `dotnet --list-sdks` 看得到版本号，一编译就报
> `无法解析 SDK "Microsoft.NET.Sdk"` / `MSB4236`。
> 判据是 `sdk\<版本>\Sdks` 目录不存在。修法：自己下载 zip 解压覆盖（不必删，覆盖即可）。

> `-PackOnly` 只编译不部署。部署请用下面的 `deploy.ps1`（**不要**用 build.ps1 直接装）。

## 部署

```bat
powershell -ExecutionPolicy Bypass -File deploy.ps1
:: 编译产物不在默认位置时
powershell -ExecutionPolicy Bypass -File deploy.ps1 -PublishDir <publish目录>
```

脚本会：停 Jellyfin → 备份旧目录 → 复制白名单文件 → 校验 → 重启（走 Tray）。

`-Uninstall` 卸载（目录移到 `Data\LocalMeta_backup\`，可回滚）。

### ⚠️ 部署规则（这几条踩过，别改）

**规则 1：Jellyfin 会递归扫描插件目录下每一个 `.dll`，尝试当程序集加载。**
任何非托管文件都会抛 `BadImageFormatException`，进而在 `meta.json` 里把整个插件
标成 `status: Malfunctioned` 并跳过加载。

实测因此崩溃过两次：
- 插件根目录放 `e_sqlite3.dll`（我以为这样更保险）→ 直接崩
- `runtimes/win-arm/native/e_sqlite3.dll` 等一堆 RID 原生库 → 直接崩

**所以 `runtimes/` 整个目录都不能带**，原生库由 SQLitePCLRaw 自行解析。
部署脚本已按白名单只复制托管程序集，并在部署后断言「没有多余 dll、没有子目录」。

**规则 2：宿主程序集（`MediaBrowser.*` / `Jellyfin.*`）绝不能进插件目录。**
覆盖会导致程序集版本冲突甚至 Jellyfin 起不来。
注意主程序集自己就叫 `Jellyfin.Plugin.LocalMeta.dll`，做排除时要显式放行它。

**规则 3：`Microsoft.Extensions.*` / `EntityFrameworkCore` / `Polly` / `Newtonsoft`
这些宿主基础设施，插件也不该带副本。** 实测 8 个版本与宿主不一致
（如 `Microsoft.Data.Sqlite` 宿主 9.0.1125 vs 插件 9.0.24），留着可能与宿主抢程序集。

> **12.1.0 起规则 2、3 的剔除方式改了。** `Jellyfin.Controller 12.1.0` 的依赖图比
> 10.11.6 大得多，除 `MediaBrowser.*` / `Jellyfin.*` 外还带进 EF Core、
> ICU4N（13MB）、Polly、Emby.Naming、J2N、NEbml 等一堆运行时 dll。
> 按前缀删的旧办法（`RemoveHostAssemblies`）删不干净——第三方依赖没有统一前缀。
> 现在改成**白名单**（csproj 的 `PrunePublishDir` 目标）：只留
> `Jellyfin.Plugin.LocalMeta.dll` + `Microsoft.Data.Sqlite.dll` +
> 3 个 `SQLitePCLRaw.*.dll`，其余全删，`runtimes/` 整个目录一起删。
> 实测一次删掉 19 个多余程序集。

**规则 4：加载失败后 Jellyfin 会记住。** `meta.json` 里写 `status: Malfunctioned`，
即使文件修好也不加载，必须删掉 `meta.json` 让它重新扫描。
注意 `meta.json` 的 `name` 是按**目录名**记的，改过目录名要一并删。

**规则 5：改 dll 必须重启 Jellyfin**（不是热加载）。
停的时候要连 `Jellyfin.Windows.Tray.exe` 一起停，否则 Tray 会把 `jellyfin.exe` 再拉起来。
启动用 Tray，它的实际路径在**子目录**里：`C:\Jellyfin\jellyfin-windows-tray\Jellyfin.Windows.Tray.exe`。

**规则 6：备份目录必须放在 `plugins\` 树之外。**
放里面等于留了个含 dll 的目录让 Jellyfin 去扫（规则 1）。脚本落在 `Data\LocalMeta_backup\`。

### 部署踩过的其它坑

| 坑 | 症状 | 根因 |
|---|---|---|
| PowerShell `Copy-Item` | **报成功但文件没落地**，后续 `Test-Path` 才发现主 dll 不存在 | 受限环境下不可靠。改用 `copy_files.py`（Python 逐文件复制 + 逐个校验大小） |
| `Get-Command python` | 命中 `AppData\Local\Microsoft\WindowsApps\python.exe` | 那是**应用商店占位符**，非交互会话里是空壳（不报错、不做事、退出码 0）。`Find-Python` 已显式跳过并实际跑一次验证 |
| `Where-Object { return $true }` | 主 dll 被漏掉 | `return` 只是跳过本次判断，不代表结果为 true，必须用 `if/else` 明确赋值 |
| `os.makedirs(exist_ok=True)` 抛 `FileExistsError` | 明明传了 `exist_ok` | 目标路径**存在但不是目录**（残留文件）。先判断 `isfile` 再删 |
| 插件目录加载失败后一直不生效 | 反复重启都没用 | `meta.json` 里的 Malfunctioned 标记（规则 4） |


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

## 部署后怎么验证有效果

**第 1 步：确认插件已激活**

后台 → 插件，LocalMeta 应显示 **Active**。命令行查：

```bash
grep -a '"name"\|"status"' "C:/Jellyfin/Data/plugins/Jellyfin.Plugin.LocalMeta/meta.json"
# 期望：name=LocalMeta  status=Active
```

**第 2 步：确认数据源已载入**

启动日志里会有一行：

```
[LocalMeta] 载入资料源 N 个
```

**N = 0 说明配置页还没填数据源路径**，插件处于空转状态。填完两个路径后需重启 Jellyfin
（配置在构造函数里读，改配置不会热加载）。

命令行查：

```bash
grep -a "LocalMeta" "C:/Jellyfin/Data/log/"*.log | tail -5
```

**第 3 步：确认 provider 真的被调用**

打开某个人物页 → 触发元数据刷新（对该人物右键「刷新元数据」），
日志里应出现：

```
[LocalMeta] 提供头像: <名字> <- <源名>
[LocalMeta] 补简介: <名字> <- <源名>
```

**看不到这两行就是没生效**，按这个顺序排查：

| 现象 | 原因 |
|---|---|
| 插件都没加载 | 看 `meta.json` 的 status（规则 4：可能是 Malfunctioned 残留） |
| 载入资料源 0 个 | 配置页没填路径，或填错 |
| 只有头像没有简介 | 数据源里该人物没有文字资料（JavBoss `jav_idol` 字段为空） |
| 两条日志都没有 | provider 没被调用：确认人物页确实触发了元数据刷新 |

> 注意：Jellyfin 的人物元数据有缓存，光打开页面可能不会真的调 provider，
> 必须显式刷新。插件设计上「只补空」，如果该人物已有简介/头像，它本来就不会动。

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
"<USERPROFILE>\.dotnet\dotnet.exe" bin\Release\net10.0\apiprobe.dll C:\Jellyfin IRemoteImageProvider
:: 列出整个命名空间有哪些接口
"<USERPROFILE>\.dotnet\dotnet.exe" bin\Release\net10.0\apiprobe.dll C:\Jellyfin NS:MediaBrowser.Controller.Providers
```

这比查文档可靠 —— 文档说的往往落后好几个版本。

> **取证目录必须是真正跑着的那个 Jellyfin。** 本机 `C:\Program Files\Jellyfin\Server`
> 是旧 10.11 的残留（`jellyfin.runtimeconfig.json` 里 `tfm: net8.0`），
> 12.1.0 实际装在 `C:\Jellyfin`（`tfm: net10.0`）。对着旧目录取证等于白取证。

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
  的传递依赖全带出来（12.1.0 下是 24 个文件，10.11.6 下 38 个）。
  覆盖进 `C:\Jellyfin` 会导致程序集版本冲突甚至 Jellyfin 起不来。
  csproj 用 `PrunePublishDir` 目标按**白名单**清理（只留插件自身 + SQLite 4 件套），
  `build.ps1` 部署前再校验一次（发现残留直接 exit 5）。
  历史上用过按前缀删的 `RemoveHostAssemblies`，两个坑：通配符 `Jellyfin.*.dll`
  会把插件自己的 `Jellyfin.Plugin.LocalMeta.dll` 一起删掉；且 12.1.0 带来的
  ICU4N / EF Core / Polly 这类第三方依赖没有统一前缀，删不干净。
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
