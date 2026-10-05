# LocalMeta

> 维护者看 [`PROJECT.md`](PROJECT.md)：状态、待办、维护规矩、本机环境。

Jellyfin 人物资料（头像 / 简介）的**本地兜底**方案。跟 MetaTube 这类远程刮削器互补：
排在它们之后，只在字段为空时才补，**永不覆盖**远程刮到的内容。

针对 Jellyfin **10.11.6** 开发，已在 **12.1.0** 上完成升级验证与适配（数据目录、数据库
schema、插件加载三项均实测通过，见 [docs/10-升级适配-Jellyfin-12.1.0.md](docs/10-升级适配-Jellyfin-12.1.0.md)）。

## 为什么要这个

刮削链路上，演员资料缺失分三层，**只有第三层是本地能救的**：

| 层 | 症状 | 本方案能救吗 |
|---|---|---|
| 网络层 | 上游超时、连不上 | ❌ 改不了网络 |
| 源覆盖层 | 上游库里根本没这条数据 | ❌ 不能凭空造数据 |
| 自动化层 | 一次性脚本补过，但会回退、新片子进来又缺 | ✅ **这才是这里解决的** |

所以定位是**兜底 + 防回退**，不是"再去刨新数据源"。

## 工作方式

插件挂在 Jellyfin 的 provider 链上，靠 `Order = 100` 排在 MetaTube 等远程刮削器**之后**，
所以它天然"上游没刮到才兜"，不需要写任何互斥判断。两条触发路径：

| 路径 | 触发时机 | 作用 |
|---|---|---|
| `ILocalMetadataProvider<Person>` + `IRemoteImageProvider` | 人物页刷新、元数据重刮、新演员入库 | **当场兜底**，不用等定时任务 |
| `IScheduledTask`（默认每 24 小时） | 定时全量扫描 | 补漏：历史条目、provider 没覆盖到的情况 |

两条路径共用同一套判定逻辑，行为一致。

## 目录

```
PROJECT.md          项目维护入口：状态、待办、维护规矩、本机环境
README.md           本文件：产品说明

b-plugin/           插件（C#，net10.0）
├─ Jellyfin.Plugin.LocalMeta/
│  ├─ Plugin.cs                          插件入口
│  ├─ PluginConfiguration.cs             配置模型
│  ├─ Sources/LocalMetaSources.cs        源抽象 + JavBoss sqlite + Gfriends 头像
│  ├─ Providers/                         Person 文字 / 图片 provider
│  ├─ Tasks/                             IScheduledTask
│  └─ Configuration/configPage.html      配置页（内嵌资源）
├─ build.ps1         编译 + 部署 / 卸载
└─ README.md         详细说明 + 实测接口签名清单

tools/
├─ apiprobe/         元数据取证工具：读 DLL 打印真实接口签名
└─ loadtest/         加载验证工具：反射确认宿主能识别 provider

docs/
├─ 10-升级适配-Jellyfin-12.1.0.md      12.1.0 适配说明（schema 变更、数据目录坑）
├─ 11-兼容性检查报告-12.1.0.md          升级后的检查过程与证据
├─ 20-插件方案-实施版.md                 实施方案与取舍记录
├─ 30-演员资料体检与修复报告.md          数据来源与缺口分析
└─ 40-协作-GitHub推送与上传.md          SSH 推送配置与常见故障
```

## 快速开始

### 编译与部署

```bat
:: 需要 .NET 10 SDK（12.1.0 宿主是 net10.0，插件 TFM 已同步）
cd b-plugin
powershell -ExecutionPolicy Bypass -File build.ps1 -PackOnly   :: 只编译
powershell -ExecutionPolicy Bypass -File deploy.ps1           :: 部署 + 重启
```

脚本会自动停 Jellyfin、备份旧目录、按白名单复制、校验、重启。
**部署规则坑较多**（Jellyfin 会递归扫描插件目录下每个 dll，原生库会导致整个插件
被标 Malfunctioned），务必看 `b-plugin/README.md` 的「部署规则」章节，别自己手工拷 dll。

## 核心设计：三条硬规矩

1. **只补空，永不覆盖。** 目标字段已有内容就跳过。
   靠 `Order = 100` 排在远程 provider 之后，天然"它没刮到才兜"，
   不需要写任何互斥判断。
2. **幂等。** 重复执行结果不变，跑一百次和跑一次一样。
3. **配置驱动。** 数据源、阈值、输出格式全在配置文件 / 配置页，
   加新源不用改代码。

## 数据源

| 源 | 提供什么 | 说明 |
|---|---|---|
| **Gfriends** | 头像（图片文件） | 纯图仓库，**不提供**简介/资料 |
| **JavBoss** `jav_idol` | 三围/罩杯/身高/出道日 | 用于拼简介文本 |
| **MetaTube**（已装插件） | 影片元数据 | 远程，通常够用 |

新增数据源：实现 `ILocalMetaProfileSource`（两个方法）→ 在工厂挂一行 → 配置加一项。
provider 和任务代码不用动。

## 一个容易搞错的映射

`jav_idol.cup` 存的是**数字索引**不是字母，规则是 `字母 = chr(65 + cup)`（0→A）。

用 `bust - waist` 差值反推验证过：cup=6→G(差30)、7→H(32)、8→I(35)、9→J(37) 全部自洽；
反过来按"cup=1 是 A"推则全盘不自洽。

**字母表必须够长** —— JavBoss 实际有到 15 的值，字母表短了会**静默丢弃**高 cup 数据
（不报错，只是数据消失，很容易漏查）。

## 已验证的接口签名（10.11.6）

插件涉及的所有扩展点都从本地 DLL 元数据取证，不是照抄教程
（10.11 相比 10.9 有八处实质变动，照老教程写必编译不过）：

| 接口 / 类型 | 真实位置 |
|---|---|
| `ILocalMetadataProvider<T>` | `MediaBrowser.Controller.Providers`（不是 `IMetadataProvider<T>`） |
| `GetMetadata(ItemInfo, IDirectoryService, ct)` | 同上，10.x 不再是 `(result, ct)` |
| `GetSupportedImages` | 返回 `IEnumerable<ImageType>`（不是 `BaseItemKind`） |
| `RemoteImageInfo` | `MediaBrowser.Model.Providers` |
| `BaseItemKind` | `Jellyfin.Data.Enums` |
| 配置基类 | `BasePluginConfiguration`（`IPluginConfiguration` 已移除） |
| 配置序列化 | `IXmlSerializer`（**XML，不是 JSON**） |
| `IScheduledTask` | 10.11 新增 `Key`；`ExecuteAsync(progress, ct)` |

**升级 Jellyfin 前先取证，别先改代码：**

```bat
cd tools\apiprobe
dotnet build -c Release
dotnet bin\Release\net10.0\apiprobe.dll "C:\Jellyfin" IRemoteImageProvider
dotnet bin\Release\net10.0\apiprobe.dll "C:\Jellyfin" NS:MediaBrowser.Controller.Providers
```

> **取证目录必须是真正跑着的那个 Jellyfin。**
> 本机就踩过：`C:\Program Files\Jellyfin\Server` 是旧 10.11 的残留（runtimeconfig 写着
> `net8.0`），而 12.1.0 实际装在 `C:\Jellyfin`（`net10.0`）。对着旧目录取证等于没取证。
> 判断依据：看 `jellyfin.runtimeconfig.json` 里的 `tfm`，或日志里的 `Storage path`。

## 数据安全

- 只补空字段，不覆盖已有内容。
- 写 `jellyfin.db` 前自动备份，按时间轮转保留最近 N 份（配置页可调）。
- 配置页可关掉备份（不建议）。
- 部署插件时**只部署插件自己的 dll**，宿主程序集由 csproj 目标自动剔除 ——
  覆盖 `MediaBrowser.*` / `Jellyfin.*` 会导致版本冲突甚至 Jellyfin 起不来。

## 环境要求

| | 要求 |
|---|---|
| Jellyfin | **10.11.x 或 12.1.x**；其他版本需先跑 `tools/apiprobe` 取证，见下 |
| 构建 | **.NET 10 SDK**（插件 TFM `net10.0`，引用 `Jellyfin.Controller 12.1.0`） |
| 运行 | 无额外依赖，Jellyfin 宿主自带 runtime |

### Jellyfin 12.1.0 用户必读

12.1.0 改了三处数据库 schema，其中一条会让**按旧写法直查数据库的代码误判全员缺图**：

| # | 变更 | 10.11 | 12.1.0 |
|---|---|---|---|
| 1 | `BaseItems.Type` 值 | `Person` / `Movie` | `MediaBrowser.Controller.Entities.Person` |
| 2 | Person 条目 Id 与 `Peoples.Id` | 同一个 GUID | **两个不同 GUID** |
| 3 | `PeopleBaseItemMap.PeopleId` 指向 | 两者皆可 | `Peoples.Id` |

插件已适配：`PersonEntityType` 用完整类名，头像判定以 Person 条目 Id 为准、
查不到再退回 `Peoples.Id`，两个 Id 都要写图记录（否则影片页和人物页只有一个有图）。

升级 Jellyfin 后不用改配置，但要按
[docs/10-升级适配-Jellyfin-12.1.0.md](docs/10-升级适配-Jellyfin-12.1.0.md)
里的自检清单核一遍，重点是**数据目录有没有换**。

## 状态

| | 状态 |
|---|---|
| 插件 | **已部署到本地 Jellyfin 12.1.0，加载成功（`status: Active`）**，TFM `net10.0` + 引用 `Jellyfin.Controller 12.1.0` |
| 数据源 | **已配置**（头像源 + javboss.db，日志「载入资料源 2 个」） |
| 补数据效果 | 数据源命中已验证；实际写入待跑一次计划任务后确认 |

## 许可

代码部分可自由使用。数据源（Gfriends / JavBoss）各自独立，请自行遵守其许可与当地法规。
