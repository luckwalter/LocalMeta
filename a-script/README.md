# LocalMeta 守护脚本（A 阶段）

Jellyfin 人物资料的**本地兜底补齐**。每天自动扫一次缺口，把漏掉的头像和简介补上；
上游（metatube / Gfriends / JavBoss）已经有的数据**一律不碰**。

## 三条硬规矩

1. **幂等** —— 反复跑结果不变。今天补完明天再跑，只会看到"待补 0"。
2. **只补不覆盖** —— 默认 `overwriteExisting: false`，文件已存在 / 库里已有 Primary 图 /
   已有 Overview 的，全部跳过，绝不冲掉 metatube 刮的内容。
3. **配置驱动** —— 加数据源、换库名、调归一化规则，改 `config.json` 就行，不用动 `localmeta.py`。

## 文件

| 文件 | 作用 |
|---|---|
| `config.json` | 全部配置（库名、路径、数据源、归一化规则、脏数据阈值） |
| `localmeta.py` | 主程序，扫缺口 → 补头像/简介 → 备份 → 写库 → 出报告 |
| `install_task.ps1` | 注册/卸载 Windows 计划任务 |
| `logs/` | 每次运行的日志 + `task_install.txt`（注册记录） |
| `lists/report_*.json` | 每次运行的统计（补齐数、缺口数、耗时、备份文件） |

## 常用命令

```bat
python localmeta.py --dry-run              :: 只看不写，先看缺口
python localmeta.py --only avatar          :: 只补头像
python localmeta.py --only bio             :: 只补简介
python localmeta.py --config other.json    :: 换一份配置
```

计划任务：

```bat
powershell -ExecutionPolicy Bypass -File install_task.ps1              :: 注册（重复执行=重建）
powershell -ExecutionPolicy Bypass -File install_task.ps1 -Uninstall   :: 卸载
powershell -ExecutionPolicy Bypass -File install_task.ps1 -Hour 4 -Minute 30   :: 换个时间
```

> 系统执行策略若是 `Restricted`，直接跑 `.ps1` 会被拒；
> 一定带 `-ExecutionPolicy Bypass`，或者先在任务计划程序 GUI 里建一次拿到管理员上下文。
> 脚本文件必须是 **UTF-8 with BOM**，否则 PowerShell 5.1 按 GBK 解码会把中文注释
> 解析坏，表现为 `New-ScheduledTaskSettingsSet` 返回 `$null`、注册报"参数为 Null"。

## 配置说明（config.json 关键字段）

| 字段 | 说明 |
|---|---|
| `library` | 媒体库名，多个逗号分隔 |
| `jellyfinDb` / `metadataDir` | 数据库与人物头像根目录 |
| `overwriteExisting` | `false`=只补空（默认）；`true`=源里有的都覆盖一次 |
| `backup` / `backupMax` | 写库前备份，按时间轮转保留最近 N 份 |
| `nameRules.stripPatterns` | 名字归一化正则，比如去掉括号别名 |
| `sources` | 数据源数组，**加源只改这里** |
| `limits` | 脏数据拦截：身高区间、围度区间、最小图高 |

### 加一个新数据源

在 `sources` 里加一项，然后往 `FACTORY` 里注册一个新 kind（见 `localmeta.py` 末尾）：

```json
{ "name": "mynewsrc", "kind": "profile_sqlite", "enabled": true,
  "path": "D:\\data\\mydb.db", "table": "actors" }
```

`kind` 支持：`avatar_folder`（plan 索引 + 文件目录）、`avatar_plan`（纯映射表）、
`profile_sqlite`（本地 sqlite 人物资料）。

## 罩杯字母映射（踩过的坑）

`javboss.jav_idol.cup` 存的是**数字索引**不是字母。实测校验（用 `bust - waist` 差值反推）：

| cup 数字 | 字母 | 依据 |
|---|---|---|
| 0 → A | … | |
| 6 | **G** | 差值中位 30，落在 G 带（27.5–30） |
| 7 | **H** | 差值中位 32，落 H 带（30–32.5） |
| 8 | **I** | 差值中位 35，落 I 带（32.5–35） |
| 9 | **J** | 差值中位 37，落 J 带（35–37） |

即 `字母 = chr(65 + cup)`。上一轮脚本用 `CUP_LETTER = "ABCDEFGHI"` 会把 cup≥9 的静默丢弃，
本版改成 `"ABCDEFGHIJKLMNOPQ"` 覆盖到 cup=16。

## 回滚

```bat
:: 用备份覆盖回去（挑一个 .bak_localmeta_ 时间戳最新的）
copy /y "C:\Jellyfin\Data\data\jellyfin.db.bak_localmeta_20261005_133649" "C:\Jellyfin\Data\data\jellyfin.db"
:: 头像文件在 C:\Jellyfin\Data\metadata\People 下，手工删除对应目录
```
