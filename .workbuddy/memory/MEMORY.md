# LocalMeta 项目记忆

Jellyfin 人物资料（头像/简介）本地兜底。A 阶段 Python 守护脚本 + B 阶段 C# 插件，
两条路互为备份，逻辑必须保持同步。

## 项目约定

- 维护入口 `PROJECT.md`；产品说明 `README.md`；部署细则 `b-plugin/README.md`
- 代码托管 `git@github.com:luckwalter/LocalMeta.git`（main 分支）
- 改 A 阶段的逻辑（姓名归一化 / 罩杯映射 / 脏数据阈值）必须同步改 B 阶段，反之亦然
- 改完先 `--dry-run`，"待补"数字要和基线吻合才允许实际执行

## 关键知识（踩过坑，别再踩）

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
直接敲 `dotnet` 命中的是空壳。真 SDK 在 `%USERPROFILE%\.dotnet`。

**Python 占位符**：`Get-Command python` 会命中 `WindowsApps` 的应用商店占位符，
非交互会话里是空壳（不报错、不做事、退出码 0）。

## 本机环境

Jellyfin 安装 `C:\Program Files\Jellyfin\Server\`；数据目录 `C:\Jellyfin\Data`；
人物头像 `C:\Jellyfin\Data\metadata\People\`；托盘启动器在子目录
`C:\Jellyfin\jellyfin-windows-tray\Jellyfin.Windows.Tray.exe`；
媒体走 SMB `\\HOMENAS\Porn Movie\番号`、`\\HOMENAS\Movie`；
媒体库：电影 / Japan Pron Movie / 电视剧。
