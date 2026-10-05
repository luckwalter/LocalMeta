# LocalMeta

Jellyfin 人物资料（头像 / 简介）的**本地兜底**方案。跟 MetaTube 这类远程刮削器互补：
排在它们之后，只在字段为空时才补，**永不覆盖**远程刮到的内容。

针对 Jellyfin **10.11.6** 开发与验证。

## 为什么要这个

刮削链路上，演员资料缺失分三层，**只有第三层是本地能救的**：

| 层 | 症状 | 本方案能救吗 |
|---|---|---|
| 网络层 | 上游超时、连不上 | ❌ 改不了网络 |
| 源覆盖层 | 上游库里根本没这条数据 | ❌ 不能凭空造数据 |
| 自动化层 | 一次性脚本补过，但会回退、新片子进来又缺 | ✅ **这才是这里解决的** |

所以定位是**兜底 + 防回退**，不是"再去刨新数据源"。

## 两条路，互为备份

```
a-script/   纯 Python 守护脚本 + Windows 计划任务
             → 零编译，改配置即生效。适合先跑起来。
b-plugin/   Jellyfin 插件（C#，.NET 9）
             → 跟 Jellyfin 生命周期绑定，人物页刷新/重刮时自动生效。
```

两者是**同一套逻辑的两份实现**：数据源抽象、姓名归一化规则、罩杯映射、脏数据阈值
全部对齐。改一处要同步另一处 —— 这也是刻意的：一条挂了另一条还在。

## 目录

```
a-script/           A 阶段：守护脚本（Python）
├─ localmeta.py     主程序，配置驱动、幂等、只补空
├─ config.json      全部配置项（数据源、阈值、姓名规则）
├─ install_task.ps1 注册 / 卸载 Windows 计划任务
└─ README.md        详细说明

b-plugin/           B 阶段：Jellyfin 插件（C#）
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
├─ Jellyfin_LocalMeta_插件方案_实施版.md
└─ JapanPronMovie_演员资料体检与修复报告.md
```

## 快速开始

### A 阶段（推荐先跑）

```bat
cd a-script
python -m venv .venv
.venv\Scripts\pip install requests

:: 改 config.json：填 Jellyfin 数据目录、数据源路径
python localmeta.py --dry-run     :: 先空跑，看会补什么
python localmeta.py               :: 实际执行

:: 注册每天自动跑（默认 03:10）
powershell -ExecutionPolicy Bypass -File install_task.ps1
```

### B 阶段

```bat
:: 需要 .NET 9 SDK
cd b-plugin
powershell -ExecutionPolicy Bypass -File build.ps1 -PackOnly   :: 只编译验证
powershell -ExecutionPolicy Bypass -File build.ps1             :: 编译并部署
:: 重启 Jellyfin，后台 → 插件 看到 LocalMeta
```

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
dotnet bin\Release\net9.0\apiprobe.dll "C:\Jellyfin" IRemoteImageProvider
dotnet bin\Release\net9.0\apiprobe.dll "C:\Jellyfin" NS:MediaBrowser.Controller.Providers
```

## 数据安全

- 两条路都**只补空字段**，不覆盖已有内容。
- 写 `jellyfin.db` 前自动备份，按时间轮转保留最近 N 份。
- 配置页可关掉备份（不建议）。
- 部署插件时**只部署插件自己的 dll**，宿主程序集由 csproj 目标自动剔除 ——
  覆盖 `MediaBrowser.*` / `Jellyfin.*` 会导致版本冲突甚至 Jellyfin 起不来。

## 环境要求

| | 要求 |
|---|---|
| Jellyfin | 10.11.x（接口在 10.9↔10.11 有破坏性变动，其他版本需重新取证） |
| A 阶段 | Python 3.9+ |
| B 阶段 | .NET 9 SDK |

## 状态

| | 状态 |
|---|---|
| A 阶段 | 已上线运行，计划任务已注册验证 |
| B 阶段 | 编译通过 + 加载验证通过（三个 provider 均被宿主识别） |
| B 阶段运行期 | **未验证** —— 需部署后重启 Jellyfin 在后台实测 |

## 许可

代码部分可自由使用。数据源（Gfriends / JavBoss）各自独立，请自行遵守其许可与当地法规。
