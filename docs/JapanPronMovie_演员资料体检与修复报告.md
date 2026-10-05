# Japan Pron Movie 库 演员资料体检与修复报告

- 体检时间：2026-10-05
- 环境：Jellyfin **10.11.6 跑在本机 Windows**（`C:\Jellyfin`，端口 8096，**不是 NAS**）；数据 `C:\Jellyfin\Data\data\jellyfin.db` + `Data\metadata\People`
- 数据源：NAS(<NAS_IP>) 上的 Gfriends 头像仓库 / JavBoss `javboss.db`；MetaTube 后端 `http://<METATUBE_IP>:8080`
- 脚本：全部在工作目录 `<WORKDIR>`，输出见 `out_*.txt`

---

## 一、先厘清：数据到底从哪来、Jellyfin 有两套 ID

```
Gfriends（GitHub 纯头像仓库）
   └─ gfriends_plan.json（1276 条）→ _gfriends_apply.py
        └─ javboss.db 的 jav_idol 表（avatar_code='GFAV-<id>'，1254 条）
Jellyfin（本机 8096）
   ├─ 影片元数据：MetaTube 插件 → <METATUBE_IP>:8080
   ├─ 头像：Gfriends（女优）
   └─ 简介：MetaTube 人物刮削器写的，跟 Gfriends 无关
```

**关键认知：Gfriends 从头到尾只提供图片，不提供任何简介/资料字段。** 所以"缺图"和"缺简介"是两件事，得分开治。

**Jellyfin 10.11 有两套 ID**（本次排查最大的坑）：

| 维度 | 表/字段 | 用途 | 备注 |
|---|---|---|---|
| 影片页演员位 | `Peoples.Id` + `PeopleBaseItemMap` | 影片详情页的演员头像 | 名字可能带括号，如「小日向みゆう（清原みゆう）」 |
| 人物页条目 | `BaseItems.Id`（`Type=Person`） | 点进演员个人页的头像/简介 | metatube 给的主名，如「小日向みゆう」 |

> 第一轮排查时把图全挂在人物条目 ID 上，导致**影片页演员头像实质 0%**——不是"6 个人缺图"，是全部缺。修正后两套 ID 都写了，才真正生效。

## 二、体检 vs 修复后（Japan Pron Movie 库）

| 指标 | 修复前 | 修复后 | 说明 |
|---|---|---|---|
| 库内影片 | 2522 | 2522 | — |
| 唯一演员（Peoples 维度） | 754 | 754 | — |
| 有头像（影片页口径） | **0（0%）** | **678（89.9%）** | 影片页演员位原本一张都没有 |
| 有头像（人物页口径） | 723（96%） | 723（96%） | 人物页原本就有 |
| 有简介 | 355（47%） | **549（72.8%）** | 本次写入 194 条 |
| 头像 + 简介 双全 | 355 | **527** | 从 47% → 70% |

**为什么影片页从 0% 起步：** 头像存在 `BaseItemImageInfos` 里，之前只挂在人物条目 ID 上；影片页读的是 `PeopleId`（`Peoples.Id`）维度，两边对不上，所以页面上一个头像都渲染不出来。

## 三、能修的都修了，剩下的是"数据源没收录"

### 头像侧：678/754（89.9%）

剩 **76 人没图**，原因只有两种：

| 子类 | 人数 | 性质 |
|---|---|---|
| Gfriends 确实没有 | ~76 | 男优 / 素人团体（豆沢豆太郎、TAKE-D、ドラゴン西川、肉尊、ZAMPA…）、新人女优 |

> Gfriends 仓库按片商分目录只收女优头像。男优不显示头像**是仓库定位决定的，不是故障**，不建议硬修。

### 简介侧：549/754（72.8%）

| 子类 | 人数 | 性质 | 能否修 |
|---|---|---|---|
| 已有（MetaTube 刮的） | 355 | — | — |
| 本次补入（JavBoss 身体资料） | **194** | 三围/罩杯/身高/出道日 | ✅ 已写库 |
| 名字对得上但资料全空 | 190 | javboss 有这个人记录，字段全空 | ❌ 源无数据 |
| javboss 完全没这人 | 15（模糊核查后 29） | 新人 / 男优 | ❌ 源无数据 |

写入样例（10 条抽验全部一致）：

```
金谷うの     3サイズ: B:96 / W:63 / H:96 <br> 身長: 156cm <br> デビュー: 1988年11月30日
香水じゅん    3サイズ: B:82 / W:55 / H:85 <br> カップサイズ: D <br> 身長: 157cm <br> デビュー: 2001年3月30日
九野ひなの    3サイズ: B:90 / W:60 / H:88 <br> カップサイズ: H <br> 身長: 157cm <br> デビュー: 2001年5月25日
```

**数据清洗做了 3 道：**
1. `cup` 列存的是数字索引（出现过 `cup=9` 这种脏值）→ 映射 0→A … 8→I，非法值丢弃；
2. 身高校验 130–200cm、胸围/腰围/臀围 50–130cm，越界视为脏数据跳过；
3. 日期格式兜底成 `YYYY年M月D日`（第一版错写成 `1996年05年10日`，已修）。

### 为什么剩下 205 人补不了 —— 双源都验过了

| 数据源 | 对这 205 人的情况 | 证据 |
|---|---|---|
| JavBoss `jav_idol` | 176 人**有名字记录但字段全空**，29 人查无此人 | `out_biofuzzy.txt` |
| MetaTube Gfriends provider | `summary`/`blood_type`/`cup_size`/`measurements` **全是空字符串**，只有 images 有内容 | `out_mtprobe.txt`实测返回 |

```json
{"id":"知良みか","summary":"","blood_type":"","cup_size":"","measurements":"","height":0,"aliases":[],"images":["https://raw.githubusercontent.com/gfriends/gfriends/..."]}
```

**结论：这 205 人是上游数据源本身没收录，不是本地没跑通。** 再折腾本地没用，得扩数据源。

## 四、本次执行记录（都改了什么）

| 步骤 | 动作 | 结果 |
|---|---|---|
| 0 | 备份 `jellyfin.db` | 47 MB → `jellyfin.db.bak_bio_20261005_131801` |
| 1 | 678 张头像落地 `metadata\People\{首字}\{全名}\folder.jpg` | 678 张 |
| 2 | 写 `BaseItemImageInfos`（`Peoples.Id` + `person_id` 双写） | 1352 行 |
| 3 | 拼 JavBoss 身体资料 → `BaseItems.Overview` | 194 行，全库 Person 有简介 430 → **624** |

**已备份文件（可回滚）：**
- `C:\Jellyfin\Data\data\jellyfin.db.bak_bio_20261005_131801`（本次简介写入前）
- `jellyfin.db.bak_*` 系列（早期头像写入前）

## 五、清单文件

| 文件 | 内容 | 条数 |
|---|---|---|
| `lists/avatar_tasks.csv` | 头像任务表（name / peoples_id / person_id / gf 编号） | 678 |
| `lists/bio_todo.json` | 本次写入的 194 条简介及原文 | 194 |
| `lists/缺简介_仍缺.csv` | 还剩 205 人无简介 | 205 |
| `lists/缺图_Gfriends有图可修.csv` | 早期 A 类（已随批量落地解决） | — |
| `lists/缺图_Gfriends无此人.csv` | 男优/未收录，接受现状 | — |
| `gf/avatars/` | 从 NAS 拉回的 678 张本地头像 | 678 |
| `gf/javboss.db` | JavBoss 库（1456 名偶像，554 人有完整三围） | — |

## 六、注意事项

1. **Jellyfin 现在正在运行**（`jellyfin.exe` 14552），改的是 SQLite 文件，进程内存里还是旧的。要让页面刷出来，需要 **重启 Jellyfin 服务**（控制台 → 库 → 刷新，或直接重启）。
2. 下次 Jellyfin 做元数据刮削/扫描时，有可能用 metatube 的返回**覆盖**掉本次写的 Overview 和头像。如果发现有回退，说明要治 MetaTube 后端（原报告 C 类：`/v1/actresses?keyword=` 返回 404，正确路径是 `/v1/actors/search?q=`）。
3. 回滚：把 `.bak_bio_20261005_131801` 覆盖回 `jellyfin.db` 即可，头像文件在 `metadata\People` 下需手工删。

## 七、下一步可选

| 方向 | 收益 | 成本 |
|---|---|---|
| **A. 修 MetaTube 后端路径**（`/v1/actresses` → `/v1/actors/search`）| 可能一次性把 205 人的简介刮回来 | 低，改插件配置 + 刷新 |
| **B. 让 MetaTube 人物元数据下载器跑起来** | 同上，顺带补齐头像 | 低 |
| **C. 换 JavBoss 新版本 / 换数据源**（JavLibrary 系） | 补那 176 人空记录 | 中，得挂新源 |
| **D. 男优头像** | 视觉整齐 | 低收益，不建议 |
| **E. 把 Gfriends 灌到其他库** | 「非番号」「私拍」两库风格统一 | 中，可复用现有脚本 |

**本次建议做 A + B**：低风险、可能一次性把缺口从 205 压到个位数，而且跟已有的 355 条同风格。
