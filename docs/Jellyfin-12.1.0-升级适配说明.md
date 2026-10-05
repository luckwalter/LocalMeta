# Jellyfin 12.1.0 升级适配说明

本仓库（A 阶段脚本 + B 阶段插件）在 Jellyfin **10.11.6 → 12.1.0** 升级后的实测结论与改动。

结论先行：**插件代码无需改动即可在 12.1.0 上加载运行；A 阶段脚本需改一处查询，本仓库已改完并验证。**

---

## 一、实测环境与证据

| 项 | 值 |
|---|---|
| 升级前 | Jellyfin 10.11.6，数据目录 `C:\Jellyfin\Data` |
| 升级后 | Jellyfin 12.1.0，`C:\Program Files\Jellyfin\Server\` |
| 迁移方式 | 手动启动 12.1.0，自动执行 `Running migration` 后接管旧数据目录 |
| 结果 | 约 2 分钟启动完成，迁移后零 `[ERR]` |

关键日志：

```
[17:32:25] Main: Jellyfin version: "12.1.0"
[17:32:25] Startup: Storage path "C:\Jellyfin\Data\data" (Fixed)   ← 仍是旧目录
[17:32:51] Loaded plugin: "LocalMeta" "0.3.0.0"                    ← 插件加载成功
[17:34:20] Core startup complete / Startup complete 0:01:55
```

---

## 二、数据继承情况：全部保留

| 项 | 升级前 | 升级后 | 判定 |
|---|---|---|---|
| 媒体库路径 | `\\HOMENAS\Porn Movie\番号` 等 | 完全一致 | 继承 |
| 影片数（Pron 库） | 2214 | 2214 | 一致 |
| 演员数（Pron 库） | 1049 | 1050 | 一致 |
| 演员头像覆盖 | 723（68.9%） | **726（69.1%）** | 一致 |
| 演员简介覆盖 | 353（33.7%） | **547（52.1%）** | 不减反增 |
| 用户 / 观看记录 | 1 用户 | 1 用户 / 1823 条 UserData | 继承 |
| 插件 | LocalMeta 等 | 全部加载 | 继承 |

### 一个坑：12.1.0 可能换数据目录

12.1.0 **默认**数据目录改成了：

```
C:\Users\<用户名>\AppData\Local\jellyfin\
```

（10.11 是 `C:\Jellyfin\Data`）。如果启动时用了新目录，你会看到**一个全新的空库**
（`jellyfin.db` 只有几百 KB，日志出现 `System initialization detected. Seed data`）——
媒体库、演员资料、插件全都不在里面。

**怎么确认自己落在哪个目录**：看日志里的 `Storage path`，不要猜。

**想固定用旧目录**（推荐，能保住全部数据）：启动时指定

```bat
jellyfin.exe --datadir C:\Jellyfin\Data
```

或设环境变量 `JELLYFIN_DATA_DIR=C:\Jellyfin\Data`。

迁移会改旧库，**执行前先备份 `jellyfin.db`**。

---

## 三、数据库 schema 变了三处

### 1. `BaseItems.Type` 从短名改成完整类名

| | 10.11 | 12.1.0 |
|---|---|---|
| 人物 | `Person` | `MediaBrowser.Controller.Entities.Person` |
| 影片 | `Movie` | `MediaBrowser.Controller.Entities.Movies.Movie` |

凡是写死 `WHERE Type='Person'` 的脚本都会静默失效（查不到，不报错）。

### 2. Person 条目 Id 与 `Peoples.Id` 脱钩

这是最隐蔽的一条 —— **同一个人在两张表里的 Id 不再是同一个 GUID**：

```
明日花キララ
  Peoples.Id    = 363552A7-053D-4300-855E-9B74F0C97090
  BaseItems.Id  = 004141DA-D847-6FCB-60DA-67EE3D933E02
```

而 **头像记录（`BaseItemImageInfos.ItemId`）挂在 `BaseItems.Id` 上**，
`PeopleBaseItemMap.PeopleId` 却指向 `Peoples.Id`。

后果：用 `Peoples.Id` 去查头像 → **全部查不到** → 脚本误判"所有人都没有头像" → 重复回填。

### 3. 列名微调

| 表 | 列 |
|---|---|
| `PeopleBaseItemMap` | `ItemId` / **`PeopleId`**（不是 `PersonId`） |
| `AncestorIds` | `ItemId` / **`ParentItemId`**（不是 `AncestorId`） |
| `BaseItemImageInfos.ImageType` | 仍是数字（`0` = Primary），**未变** |

另外 12.1.0 的 `BaseItems.TopParentId` 是空的，按库统计条目要改用
`AncestorIds` 或按 `Path` 前缀。

---

## 四、A 阶段脚本改了什么

文件：`a-script/localmeta.py`

| 位置 | 改动 |
|---|---|
| 新增 `detect_person_type()` | 自动探测 `BaseItems.Type` 的实际写法（完整类名 ↔ 短名），配置值优先、查不到自动回退，10.11 / 12.1 都能跑 |
| `scan()` 头像判定 | 优先用 Person 条目 Id（`BaseItems.Id`）查，查不到再退回 `Peoples.Id` |
| 写入 `BaseItemImageInfos` | 只写 Person 条目 Id；仅当该演员没有 Person 条目时才退回 `Peoples.Id` |

改动前后对比（同一份 12.1.0 真实库，754 名 Actor）：

```
改前：待补头像 754   ← 全员误判为缺图
改后：待补头像  27   ← 与升级前基线吻合
```

写入侧顺带修掉一个旧缺陷：以前对两个 Id 各写一条，10.11 下两者同值会产生重复记录，
现在只写一条。

### 配置无需改动

`a-script/config.json` 里 `personType` 本来就是完整类名，正好匹配 12.1.0；
`jellyfinDb` / `metadataDir` 只要数据目录没变就仍然有效。

**升级后建议先空跑一遍确认**：

```bat
cd a-script
python localmeta.py --dry-run
```

---

## 五、B 阶段插件：实测兼容，无需改动

| 关注点 | 结论 |
|---|---|
| 扩展点签名 | `ILocalMetadataProvider<T>` / `IRemoteImageProvider` / `IScheduledTask` / `BasePlugin<T>` / `BasePluginConfiguration` / `RemoteImageInfo` / `ImageType` / `BaseItemKind` —— 12.1.0 与 10.11 **完全一致** |
| TFM | 插件 `net9.0`，12.1.0 宿主 `net10.0`，**实测能正常加载** |
| 加载结果 | `Loaded plugin: "LocalMeta" "0.3.0.0"` |
| 第三方插件 | MetaTube 2025.1102.2200.0 / ThePornDB 1.6.0.11 / TheTVDB 20.0.0.0（均为 10.11 编译）也全部加载成功 |

### 可选：重定目标到 net10.0

不重定也能跑，想彻底对齐宿主再做这步（需要 .NET 10 SDK）：

```xml
<!-- Jellyfin.Plugin.LocalMeta.csproj -->
<TargetFramework>net10.0</TargetFramework>
<PackageReference Include="Jellyfin.Controller" Version="12.1.0" />
```

改完必须重新编译验证，不要直接改版本号就部署。

---

## 六、升级前的自检清单

1. **备份** `jellyfin.db`（A 脚本每次执行也会自动备份，但升级前手动备一份更稳）
2. 记下当前数据目录：日志里搜 `Storage path`
3. 升级后**先确认数据目录没变**；变了就加 `--datadir` 指回旧目录再启动
4. 看插件加载列表里有没有 `LocalMeta`
5. `python localmeta.py --dry-run` 空跑，核对"待补"数字是否和升级前一致

---

## 七、取证方法（换版本都能复用）

别照抄教程，直接从目标版本的 DLL 里读真实签名：

```bat
cd tools\apiprobe
dotnet build -c Release
dotnet bin\Release\net9.0\apiprobe.dll "C:\Program Files\Jellyfin\Server" IRemoteImageProvider
dotnet bin\Release\net9.0\apiprobe.dll "C:\Program Files\Jellyfin\Server" NS:MediaBrowser.Controller.Providers
```

数据库 schema 用 sqlite 只读查（别用 GUI 猜）：

```python
import sqlite3
con = sqlite3.connect("file:C:\\Jellyfin\\Data\\data\\jellyfin.db?mode=ro", uri=True)
print(con.execute("SELECT Type, COUNT(*) FROM BaseItems GROUP BY Type").fetchall())
```
