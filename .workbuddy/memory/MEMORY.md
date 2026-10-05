# LocalMeta 项目记忆

Jellyfin 人物资料（头像/简介）本地兜底的 C# 插件。
两条触发路径（provider 当场兜底 + 计划任务每天补漏）共用同一套判定逻辑。

> 2026-10-06 起单轨：早期并行的 Python 守护脚本已整仓移除，git 历史里还能翻到。

## 项目约定

- 维护入口 `PROJECT.md`；产品说明 `README.md`；部署细则 `b-plugin/README.md`
- 代码托管 `git@github.com:luckwalter/LocalMeta.git`（main 分支）
- 数据源 `C:\Jellyfin\Data\LocalMeta-Sources\`
- 改判定逻辑后要核对缺口数字（基线：待补头像 27 / 待补简介 208），
  对不上就是判定错了，别放它去写库

## 关键知识（踩过坑，别再踩）

**插件三个致命坑（都只在真实数据上暴露）**
- Newtonsoft **不能** `DeserializeObject<IEnumerable>` → `JsonSerializationException`，
  两个 provider 构造失败，插件"加载成功"但完全不工作。用 `JArray.Parse` + try/catch
- `appPaths.DataPath` **本身就是 `.../Data/data`**，别再拼 `"data"`；
  而 `metadata/People` 在 DataPath 的**上一级**
- `cup` 为 NULL 不能当索引 0（会错写成 A 罩杯），javboss 里 74% 为空，
  空值必须返回空串
- `INSERT OR REPLACE` 配 `Guid.NewGuid()` 主键是**无效写法**（永远撞不上，
  REPLACE 不触发）→ 重复跑会积累多条图片记录。先 DELETE 同 ItemId+ImageType 再插

**判断插件是否真在工作**：光看 `Loaded plugin` 不够，要看
`[LocalMeta] 载入资料源 N 个`，N=0 就是源没配上；还要看有没有
`Error creating "...Provider"` —— 那条 ERR 意味着 provider 全废。

**Jellyfin 数据库 schema**
- 10.11：表名 `BaseItems` / `Peoples` / `PeopleBaseItemMap` / `BaseItemImageInfos` / `AncestorIds`
- 12.1.0 三处变更：① `BaseItems.Type` 从短名 `Person` 改成完整类名
  `MediaBrowser.Controller.Entities.Person`；② Person 的 `BaseItems.Id` 与 `Peoples.Id`
  **脱钩成两个 GUID**（头像挂 BaseItems.Id，`PeopleBaseItemMap.PeopleId` 指向 Peoples.Id）；
  ③ 列名是 `PeopleId` / `ParentItemId`，不是 `PersonId` / `AncestorId`
- 12.1.0 的 `BaseItems.TopParentId` 是空的，按库统计改用 `AncestorIds` 或 Path 前缀
- `BaseItemImageInfos.ImageType` 一直是数字（0=Primary）

**12.1.0 会换数据目录**：默认 `%LOCALAPPDATA%\jellyfin`（旧为 `C:\Jellyfin\Data`），
换了就是空库。确认方法：看日志 `Storage path`。要固定就加 `--datadir`。

**插件部署铁律**（详见 b-plugin/README.md）
- Jellyfin 递归扫描插件目录下每个 .dll，原生库（`e_sqlite3.dll`、`runtimes/`）
  会抛 `BadImageFormatException` → 整个插件被标 Malfunctioned
- 宿主程序集（`MediaBrowser.*` / `Jellyfin.*`）绝不能进插件目录
  （注意放行主程序集 `Jellyfin.Plugin.LocalMeta.dll`）
- 加载失败会被 `meta.json` 记住，修好文件也必须删 `meta.json` 才重新扫描
- 改 dll 必须重启，且要连 `Jellyfin.Windows.Tray.exe` 一起停
- 备份目录必须放 `plugins\` 树之外

**罩杯映射**：`jav_idol.cup` 是数字索引，`字母 = chr(65 + cup)`（0→A）。
字母表必须够长（JavBoss 有到 15 的值），短了会静默丢弃高 cup 数据。

**推送 GitHub**：HTTPS POST 在本机会超时，必须走 SSH：
```
GIT_SSH_COMMAND="ssh -i %USERPROFILE%/.ssh/id_ed25519_github -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null" git push origin main
```

**PATH 陷阱**：`C:\Program Files\dotnet` 只有 runtime 没有 SDK，排在 PATH 前面，
直接敲 `dotnet` 命中的是空壳。真 SDK 在 `%USERPROFILE%\.dotnet`（现为 10.0.401）。

**装 .NET SDK 的坑**：官方 `dotnet-install.ps1` 解压 300MB zip 时会**静默中断**，
表现是 `--list-sdks` 看得到版本号但一编译就 `MSB4236 无法解析 SDK Microsoft.NET.Sdk`。
判据：`sdk\<版本>\Sdks` 目录不存在、体积偏小（262M vs 完整 409M）。
修法：自己下 zip 覆盖解压即可，不用删（批量删 1000+ 文件会被安全策略拦）。
本机非管理员，SDK 只能装到 `%USERPROFILE%\.dotnet`。

**取证目录必须是真在跑的那份 Jellyfin**：`C:\Program Files\Jellyfin\Server` 是旧
10.11 残留（`runtimeconfig` 里 `tfm: net8.0`），12.1.0 实际在 `C:\Jellyfin`（`net10.0`）。
对着旧目录取证等于白取证（踩过一次，11 号文档已勘误）。
判断依据：`jellyfin.runtimeconfig.json` 的 `tfm` 或日志 `Storage path`。

**12.1.0 起产物清理按白名单**（csproj `PrunePublishDir`）：Controller 12.1.0 依赖图
比 10.11.6 大得多，带进 EF Core / ICU4N(13MB) / Polly / Emby.Naming 等第三方 dll，
按前缀删的旧办法删不干净。只留：插件自身 + `Microsoft.Data.Sqlite` + 3 个
`SQLitePCLRaw.*`，其余全删，`runtimes/` 整个删。

**Python 占位符**：`Get-Command python` 会命中 `WindowsApps` 的应用商店占位符，
非交互会话里是空壳（不报错、不做事、退出码 0）。

## 本机环境

Jellyfin **12.1.0 装在 `C:\Jellyfin\`**（自包含发布，`net10.0`）；
`C:\Program Files\Jellyfin\Server` 是 10.11 残留，别对着它操作；
数据目录 `C:\Jellyfin\Data`；人物头像 `C:\Jellyfin\Data\metadata\People\`；托盘启动器在子目录
`C:\Jellyfin\jellyfin-windows-tray\Jellyfin.Windows.Tray.exe`；
媒体走 SMB `\\HOMENAS\Porn Movie\番号`、`\\HOMENAS\Movie`；
媒体库：电影 / Japan Pron Movie / 电视剧。
