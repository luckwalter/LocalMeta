# LocalMeta 项目总览

> 维护入口。产品说明看 [`README.md`](README.md)，
> 部署/编译细则看 [`b-plugin/README.md`](b-plugin/README.md)。

Jellyfin 人物资料（头像 / 简介）的**本地兜底**方案。跟 MetaTube 这类远程刮削器互补：
排在它们之后，只在字段为空时才补，**永不覆盖**远程刮到的内容。

> **2026-10-06 起单轨**：早期曾有一套 Python 守护脚本与插件并行，现已整仓移除，
> 只保留插件。远程 git 历史里还能翻到（`git log --diff-filter=D -- 'a-script/*'`）。

---

## 当前状态

| 项 | 状态 |
|---|---|
| Jellyfin 10.11.6 | 编译通过 + 加载验证通过 |
| Jellyfin 12.1.0 | **已适配**：插件已重定 `net10.0` + 引用升到 `Jellyfin.Controller 12.1.0`，重新编译并部署，日志确认加载成功 |
| 代码托管 | `git@github.com:luckwalter/LocalMeta.git`（main） |
| 数据源 | **已配置**：`C:\Jellyfin\Data\LocalMeta-Sources`，日志确认「载入资料源 2 个」 |
| 补数据效果 | **未验证**：需在后台刷新人物元数据，或跑一次「LocalMeta 人物资料补齐」计划任务 |

---

## 工作方式

插件挂两条触发路径，共用同一套判定逻辑：

| 路径 | 触发时机 | 作用 |
|---|---|---|
| `ILocalMetadataProvider<Person>` + `IRemoteImageProvider` | 人物页刷新、元数据重刮、新演员入库 | **当场兜底**，不用等定时任务 |
| `IScheduledTask`（默认每 24 小时） | 定时全量扫描 | 补漏 |

provider 路径是插件独有的价值——外部脚本无论怎么定时都做不到"入库即兜底"。

---

## 目录

```
LocalMeta/
├─ PROJECT.md              本文件：项目维护入口
├─ README.md               产品说明（定位、设计、数据源、环境要求）
├─ b-plugin/               插件（C#，net10.0）
│  ├─ Jellyfin.Plugin.LocalMeta/
│  ├─ build.ps1            编译 + 部署 / 卸载
│  ├─ deploy.ps1           部署（含产物安全校验）
│  └─ README.md            **部署规则、接口取证清单、踩坑记录都在这里**
├─ tools/
│  ├─ apiprobe/            元数据取证：读 DLL 打印真实接口签名
│  └─ loadtest/            加载验证：反射确认宿主能识别 provider
├─ docs/
│  ├─ 10-升级适配-Jellyfin-12.1.0.md      12.1.0 适配说明（schema 变更、数据目录坑）
│  ├─ 11-兼容性检查报告-12.1.0.md          升级后的检查过程与证据
│  ├─ 20-插件方案-实施版.md                 实施方案与取舍记录
│  ├─ 30-演员资料体检与修复报告.md          数据来源与缺口分析
│  └─ 40-协作-GitHub推送与上传.md          SSH 推送配置与常见故障
└─ .workbuddy/memory/      项目记忆（长期约定 + 开发日志）
```

---

## 维护路线

### 待办（按优先级）

1. **验证实际写入** —— 后台 → 计划任务 → 跑一次「LocalMeta 人物资料补齐」，
   或对某个演员刷新元数据，看日志 `[LocalMeta] 补简介/补头像`。
   数据源已就绪，实测 Pron 库缺口里头像能再补 4 人、简介能再补 2 人
   （增量小是正常的：此前已用同一份源补过一轮，剩下的是源里本来就没有的人）
2. 可选：消除 `NU1903` 警告 —— `Microsoft.Data.Sqlite 9.0.0` 传递依赖
   `SQLitePCLRaw.lib.e_sqlite3 2.1.10`（有已知漏洞告警）。
   升级需重新验证 SQLite 原生库加载，风险不小，暂无收益

### 数据源位置

```
C:\Jellyfin\Data\LocalMeta-Sources\
├─ javboss.db            JavBoss 资料库（jav_idol，1456 条）
├─ gfriends_plan.json    名字 -> 头像编号 索引（1276 条）
└─ avatars/              头像实体文件（1346 个，jpg + webp）
```

配置位置：`C:\Jellyfin\Data\plugins\configurations\Jellyfin.Plugin.LocalMeta.xml`
的 `ProfileDbPath` / `AvatarSourceDir`（也可在后台配置页改）。

这份数据是从 NAS 的 JavBoss 容器拉下来的
（`/share/CACHEDEV1_DATA/Container/javboss/data`），源更新时重新拉一次覆盖即可。

### 改代码时的规矩

- **改 Jellyfin 版本前先取证，别先改代码**（目录必须是真在跑的那份，见下）：
  ```bat
  cd tools\apiprobe && dotnet build -c Release
  dotnet bin\Release\net10.0\apiprobe.dll "C:\Jellyfin" IRemoteImageProvider
  ```
- **改数据库操作前先看 schema**：12.1.0 已经坑过一次（Id 脱钩），
  别照抄旧 SQL，用 sqlite 只读查一遍当前真实结构
- **改判定逻辑后要核对数字**：插件没有空跑/预演模式，
  所以用 sqlite 只读统计缺口数，跟基线对比（基线：待补头像 27 / 待补简介 208）：
  ```sql
  -- 缺头像的 Actor 数（按 Peoples.Id 查 Primary 图）
  SELECT COUNT(DISTINCT p.Id) FROM Peoples p
    JOIN PeopleBaseItemMap m ON m.PeopleId=p.Id
    LEFT JOIN BaseItemImageInfos i ON i.ItemId=p.Id AND i.ImageType=0
    WHERE p.PersonType='Actor' AND i.Id IS NULL;
  ```
  数字对不上就是判定逻辑错了，别急着放它去写库。

### 升级 Jellyfin 时的自检清单

1. 备份 `jellyfin.db`
2. 看日志 `Storage path` —— **确认数据目录没换**（12.1.0 默认会换到
   `%LOCALAPPDATA%\jellyfin`，换了就是空库）
3. 看日志 `Loaded plugin` 里有没有 LocalMeta
   （注意核对同一条日志的 `Jellyfin version`，别拿旧版本日志当证据）
4. apiprobe 取证扩展点签名
5. sqlite 只读比对 schema
6. 用上面的 SQL 核对缺口数字与基线是否吻合

详见 [`docs/10-升级适配-Jellyfin-12.1.0.md`](docs/10-升级适配-Jellyfin-12.1.0.md)。

---

## 常用命令

```bat
:: 插件
cd b-plugin
powershell -ExecutionPolicy Bypass -File build.ps1 -PackOnly   :: 只编译
powershell -ExecutionPolicy Bypass -File deploy.ps1            :: 部署 + 重启

:: 推送 GitHub（HTTPS 在本机会超时，必须走 SSH）
GIT_SSH_COMMAND="ssh -i %USERPROFILE%/.ssh/id_ed25519_github -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null" git push origin main
```

---

## 本机环境（维护用）

| 项 | 值 |
|---|---|
| Jellyfin 12.1.0 安装 | **`C:\Jellyfin\`**（`runtimeconfig` 里 `tfm: net10.0`，自包含发布） |
| ⚠️ 旧版残留 | `C:\Program Files\Jellyfin\Server\` 是 **10.11 的残留**（`tfm: net8.0`），别对着它取证/配置 |
| 数据目录 | `C:\Jellyfin\Data`（12.1.0 会自动接管，别让它跑到 AppData） |
| 数据库 | `C:\Jellyfin\Data\data\jellyfin.db` |
| 人物头像 | `C:\Jellyfin\Data\metadata\People\` |
| 托盘启动器 | `C:\Jellyfin\jellyfin-windows-tray\Jellyfin.Windows.Tray.exe`（在**子目录**里） |
| .NET SDK | **10.0.401**，装在 `%USERPROFILE%\.dotnet`；runtime 10.0.12（含 ASP.NET Core） |
| ⚠️ PATH 陷阱 | `C:\Program Files\dotnet` 在 PATH 里排前面但**没有 SDK**，直接敲 `dotnet` 会报"找不到 SDK" |
| 媒体源 | SMB：`\\HOMENAS\Porn Movie\番号`、`\\HOMENAS\Movie` |
| 媒体库 | 电影 / Japan Pron Movie / 电视剧 |

**PATH 陷阱**：直接敲 `dotnet` 会命中 `C:\Program Files\dotnet` 这个没有 SDK 的空壳，
`--list-sdks` 返回空，看起来像没装。`build.ps1` 已做探测，不用手动改 PATH。
