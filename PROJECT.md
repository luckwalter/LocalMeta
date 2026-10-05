# LocalMeta 项目总览

> 维护入口。产品说明看 [`README.md`](README.md)，
> 部署/编译细则看 [`b-plugin/README.md`](b-plugin/README.md)，
> 脚本用法看 [`a-script/README.md`](a-script/README.md)。

Jellyfin 人物资料（头像 / 简介）的**本地兜底**方案。跟 MetaTube 这类远程刮削器互补：
排在它们之后，只在字段为空时才补，**永不覆盖**远程刮到的内容。

---

## 当前状态

| 项 | 状态 |
|---|---|
| Jellyfin 10.11.6 | A 阶段已上线运行；B 阶段编译通过 + 加载验证通过 |
| Jellyfin 12.1.0 | **已适配**：A 脚本 schema 兼容已修复并 dry-run 验证；B 插件实测加载成功（未改代码） |
| 代码托管 | `git@github.com:luckwalter/LocalMeta.git`（main） |
| B 阶段数据源 | **已配置**：`C:\Jellyfin\Data\LocalMeta-Sources`，日志确认「载入资料源 2 个」 |
| B 阶段补数据效果 | **未验证**：需在后台刷新人物元数据，或跑一次「LocalMeta 人物资料补齐」计划任务 |

---

## 两条路，互为备份

```
a-script/   纯 Python 守护脚本 + Windows 计划任务
            零编译，改配置即生效。已上线。
b-plugin/   Jellyfin 插件（C#，net9.0）
            跟 Jellyfin 生命周期绑定，人物页刷新/重刮时自动生效。已部署待验证。
```

两者是**同一套逻辑的两份实现**：数据源抽象、姓名归一化规则、罩杯映射、脏数据阈值
全部对齐。改一处要同步另一处 —— 这是刻意的：一条挂了另一条还在。

---

## 目录

```
LocalMeta/
├─ PROJECT.md              本文件：项目维护入口
├─ README.md               产品说明（定位、设计、数据源、环境要求）
├─ a-script/               A 阶段：守护脚本（Python）
│  ├─ localmeta.py         主程序，配置驱动、幂等、只补空
│  ├─ config.json          全部配置项
│  ├─ install_task.ps1     注册 / 卸载 Windows 计划任务
│  └─ README.md            详细说明
├─ b-plugin/               B 阶段：Jellyfin 插件（C#）
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
│  ├─ 20-插件方案-实施版.md                 A+B 实施方案
│  ├─ 30-演员资料体检与修复报告.md          数据来源与缺口分析
│  └─ 40-协作-GitHub推送与上传.md          SSH 推送配置与常见故障
└─ .workbuddy/memory/      项目记忆（长期约定 + 开发日志）
```

---

## 维护路线

### 待办（按优先级）

1. **验证 B 阶段实际写入** —— 后台 → 计划任务 → 跑一次「LocalMeta 人物资料补齐」，
   或对某个演员刷新元数据，看日志 `[LocalMeta] 补简介/补头像`。
   数据源已就绪，实测 Pron 库缺口里头像能再补 4 人、简介能再补 2 人
   （增量小是正常的：A 阶段已经用同一份源补过一轮，剩下的是源里本来就没有的人）
2. 可选：B 插件重定 `net10.0`（不重定也能跑，见 10 号文档）

### 数据源位置

A 阶段与 B 阶段**共用同一份数据源**，别各指一份，否则两边行为会分叉：

```
C:\Jellyfin\Data\LocalMeta-Sources\
├─ javboss.db            JavBoss 资料库（jav_idol，1456 条）
├─ gfriends_plan.json    名字 -> 头像编号 索引（1276 条）
└─ avatars/              头像实体文件（1346 个，jpg + webp）
```

配置位置：
- B 阶段：`C:\Jellyfin\Data\plugins\configurations\Jellyfin.Plugin.LocalMeta.xml`
  的 `ProfileDbPath` / `AvatarSourceDir`（也可在后台配置页改）
- A 阶段：`a-script/config.json` 的 `sources`

这份数据是从 NAS 的 JavBoss 容器拉下来的
（`/share/CACHEDEV1_DATA/Container/javboss/data`），源更新时重新拉一次覆盖即可。

### 改代码时的规矩

- **A 和 B 的逻辑必须同步**：姓名归一化、罩杯映射、脏数据阈值，改一处要改两处
- **改 Jellyfin 版本前先取证，别先改代码**：
  ```bat
  cd tools\apiprobe && dotnet build -c Release
  dotnet bin\Release\net9.0\apiprobe.dll "C:\Program Files\Jellyfin\Server" IRemoteImageProvider
  ```
- **改数据库操作前先看 schema**：12.1.0 已经坑过一次（Id 脱钩），
  别照抄旧 SQL，用 sqlite 只读查一遍当前真实结构
- **改完必须 dry-run 对比基线**：`python localmeta.py --dry-run`，
  "待补"数字要和升级前吻合，不吻合就是判定逻辑错了

### 升级 Jellyfin 时的自检清单

1. 备份 `jellyfin.db`
2. 看日志 `Storage path` —— **确认数据目录没换**（12.1.0 默认会换到
   `%LOCALAPPDATA%\jellyfin`，换了就是空库）
3. 看日志 `Loaded plugin` 里有没有 LocalMeta
   （注意核对同一条日志的 `Jellyfin version`，别拿旧版本日志当证据）
4. apiprobe 取证扩展点签名
5. sqlite 只读比对 schema
6. `python localmeta.py --dry-run` 核对数字

详见 [`docs/10-升级适配-Jellyfin-12.1.0.md`](docs/10-升级适配-Jellyfin-12.1.0.md)。

---

## 常用命令

```bat
:: A 阶段
cd a-script
python localmeta.py --dry-run              :: 空跑，看会补什么
python localmeta.py                        :: 实际执行
powershell -ExecutionPolicy Bypass -File install_task.ps1   :: 注册每天 03:10 自动跑

:: B 阶段
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
| Jellyfin 安装 | `C:\Program Files\Jellyfin\Server\` |
| 数据目录 | `C:\Jellyfin\Data`（12.1.0 会自动接管，别让它跑到 AppData） |
| 数据库 | `C:\Jellyfin\Data\data\jellyfin.db` |
| 人物头像 | `C:\Jellyfin\Data\metadata\People\` |
| 托盘启动器 | `C:\Jellyfin\jellyfin-windows-tray\Jellyfin.Windows.Tray.exe`（在**子目录**里） |
| .NET SDK | `%USERPROFILE%\.dotnet`（PATH 里的 `C:\Program Files\dotnet` 是空壳，只有 runtime） |
| 媒体源 | SMB：`\\HOMENAS\Porn Movie\番号`、`\\HOMENAS\Movie` |
| 媒体库 | 电影 / Japan Pron Movie / 电视剧 |

**PATH 陷阱**：直接敲 `dotnet` 会命中 `C:\Program Files\dotnet` 这个没有 SDK 的空壳，
`--list-sdks` 返回空，看起来像没装。`build.ps1` 已做探测，不用手动改 PATH。
