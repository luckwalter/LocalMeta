# Jellyfin 12.1.0 升级适配说明

本仓库插件在 Jellyfin **10.11.6 → 12.1.0** 升级后的实测结论与改动。

结论先行：**插件的业务代码无需改动即可在 12.1.0 上加载运行。**
真正会出事的是两件跟代码无关的事：数据目录可能换、数据库 schema 变了。
前者会让 Jellyfin 变成空库，后者会让"按旧写法直查数据库"的代码误判全员缺图。

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

## 四、插件侧：实测兼容，无需改动

| 关注点 | 结论 |
|---|---|
| 扩展点签名 | `ILocalMetadataProvider<T>` / `IRemoteImageProvider` / `IScheduledTask` / `BasePlugin<T>` / `BasePluginConfiguration` / `RemoteImageInfo` / `ImageType` / `BaseItemKind` —— 12.1.0 与 10.11 **完全一致** |
| TFM | 插件原为 `net9.0`，宿主 `net10.0`，**实测能正常加载**；现已同步重定为 `net10.0` 并引用 `Jellyfin.Controller 12.1.0`，详见 [12-环境升级-.NET10-SDK.md](12-环境升级-.NET10-SDK.md) |
| 加载结果 | `Loaded plugin: "LocalMeta" "0.3.0.0"` |
| 第三方插件 | MetaTube 2025.1102.2200.0 / ThePornDB 1.6.0.11 / TheTVDB 20.0.0.0（均为 10.11 编译）也全部加载成功 |

### 已重定到 net10.0

插件 TFM 现为 `net10.0`，引用 `Jellyfin.Controller 12.1.0`，与宿主完全对齐。
（此前 net9.0 也能在 net10.0 宿主下加载，但既然宿主已是 net10.0 就同步过去了。）
过程与踩坑见 [12-环境升级-.NET10-SDK.md](12-环境升级-.NET10-SDK.md)。

---

## 六、升级前的自检清单

1. **备份** `jellyfin.db`
2. 记下当前数据目录：日志里搜 `Storage path`
3. 升级后**先确认数据目录没变**；变了就加 `--datadir` 指回旧目录再启动
4. 看插件加载列表里有没有 `LocalMeta`
5. 用 sqlite 只读核对缺口数字是否和升级前一致（基线：待补头像 27 / 待补简介 208）

---

## 七、取证方法（换版本都能复用）

别照抄教程，直接从目标版本的 DLL 里读真实签名：

```bat
cd tools\apiprobe
dotnet build -c Release
dotnet bin\Release\net10.0\apiprobe.dll "C:\Jellyfin" IRemoteImageProvider
dotnet bin\Release\net10.0\apiprobe.dll "C:\Jellyfin" NS:MediaBrowser.Controller.Providers
```

> 目录必须是真在跑的那份 Jellyfin：`C:\Jellyfin`（`tfm: net10.0`）。
> `C:\Program Files\Jellyfin\Server` 是旧 10.11 残留（`tfm: net8.0`），别对着它取证。

数据库 schema 用 sqlite 只读查（别用 GUI 猜）：

```python
import sqlite3
con = sqlite3.connect("file:C:\\Jellyfin\\Data\\data\\jellyfin.db?mode=ro", uri=True)
print(con.execute("SELECT Type, COUNT(*) FROM BaseItems GROUP BY Type").fetchall())
```
