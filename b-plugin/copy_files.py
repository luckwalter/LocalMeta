"""把编译产物复制到 Jellyfin 插件目录。

PowerShell 的 Copy-Item 在受限环境下会「报成功但没落地」，
所以这里用 Python 逐个文件复制并校验大小。

用法：
    python copy_files.py --publish <publish目录> --dest <插件目录> [--log <日志文件>]
    python copy_files.py          # 用下面的默认值
"""
import argparse
import json
import os
import shutil
import sys

DEFAULT_PUBLISH = (
    r"C:\Users\luckw\WorkBuddy\2026-10-05-11-55-15\jellyfin-plugin-localmeta"
    r"\Jellyfin.Plugin.LocalMeta\bin\Release\net9.0\publish"
)
DEFAULT_DST = r"C:\Jellyfin\Data\plugins\Jellyfin.Plugin.LocalMeta"
DEFAULT_LOG = r"C:\Users\luckw\WorkBuddy\LocalMeta-GitHub\b-plugin\deploy_copy.txt"

_ap = argparse.ArgumentParser()
_ap.add_argument("--publish", default=DEFAULT_PUBLISH)
_ap.add_argument("--dest", default=DEFAULT_DST)
_ap.add_argument("--log", default=DEFAULT_LOG)
_args = _ap.parse_args()

PUBLISH = _args.publish
DST = _args.dest
LOG = _args.log
MAIN = "Jellyfin.Plugin.LocalMeta.dll"

# 插件目录里只允许这些托管程序集。
# Jellyfin 会递归扫描 plugins\ 下每个 .dll 当程序集加载，
# 原生库（e_sqlite3.*）一律 BadImageFormatException → 整个插件被标 Malfunctioned。
ALLOW = {
    MAIN,
    "Microsoft.Data.Sqlite.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.batteries_v2.dll",
    "SQLitePCLRaw.provider.e_sqlite3.dll",
    "Jellyfin.Plugin.LocalMeta.deps.json",
    "Jellyfin.Plugin.LocalMeta.pdb",
}

out = []

if not os.path.isdir(PUBLISH):
    out.append("FAIL: 找不到编译产物 " + PUBLISH)
    open(LOG, "w", encoding="utf-8").write("\n".join(out))
    sys.exit(2)

# 目标路径可能已经存在但【是文件不是目录】——踩过：
# Move-Item 移走目录后残留了同名文件，os.makedirs(exist_ok=True)
# 会抛 FileExistsError（明明传了 exist_ok）。先清掉。
if os.path.exists(DST) and not os.path.isdir(DST):
    out.append("目标路径已存在且不是目录（残留文件），先删除: " + DST)
    os.remove(DST)
    out.append("  已删除")
    out.append("")

# 目录已存在则先清空（保留目录本身，避免删权限问题）
os.makedirs(DST, exist_ok=True)
for name in os.listdir(DST):
    p = os.path.join(DST, name)
    try:
        shutil.rmtree(p) if os.path.isdir(p) else os.remove(p)
    except Exception as e:
        out.append("  清理失败 %s: %s" % (name, e))
out.append("目标目录已就绪: " + DST)
out.append("")

# 复制白名单文件
copied, skipped = [], []
for name in sorted(os.listdir(PUBLISH)):
    src = os.path.join(PUBLISH, name)
    if not os.path.isfile(src):
        continue
    if name not in ALLOW:
        skipped.append(name)
        continue
    dst = os.path.join(DST, name)
    shutil.copy2(src, dst)
    copied.append((name, os.path.getsize(dst)))

out.append("已复制 %d 个文件:" % len(copied))
for n, s in copied:
    out.append("  + %-46s %6d KB" % (n, s // 1024))

if skipped:
    out.append("")
    out.append("已跳过 %d 个（宿主自带或原生库，规则 1/2/3）:" % len(skipped))
    for n in skipped:
        out.append("  - " + n)

# 逐个校验大小一致
out.append("")
out.append("校验:")
bad = []
for n, s in copied:
    real = os.path.getsize(os.path.join(DST, n))
    ok = real == s and real > 0
    out.append("  %-46s %s" % (n, "OK" if ok else "不符（%d vs %d）" % (real, s)))
    if not ok:
        bad.append(n)

main_path = os.path.join(DST, MAIN)
if not os.path.exists(main_path) or os.path.getsize(main_path) == 0:
    out.append("")
    out.append("FAIL: 主程序集不存在或为空")
    open(LOG, "w", encoding="utf-8").write("\n".join(out))
    sys.exit(6)

# deps.json 重写：剔除已剔除的依赖，避免运行时找不到
deps_path = os.path.join(DST, "Jellyfin.Plugin.LocalMeta.deps.json")
deps = {
    "runtimeTarget": {"name": ".NETCoreApp,Version=v9.0", "signature": ""},
    "compilationOptions": {},
    "targets": {
        ".NETCoreApp,Version=v9.0": {
            "Jellyfin.Plugin.LocalMeta/1.0.0": {
                "dependencies": {"Microsoft.Data.Sqlite": "9.0.0", "Newtonsoft.Json": "13.0.3"},
                "runtime": {"Jellyfin.Plugin.LocalMeta.dll": {}},
            },
            "Microsoft.Data.Sqlite/9.0.0": {
                "runtime": {"Microsoft.Data.Sqlite.dll": {"assemblyVersion": "9.0.0.0", "fileVersion": "9.0.0.0"}}
            },
            "Newtonsoft.Json/13.0.3": {
                "runtime": {"Newtonsoft.Json.dll": {"assemblyVersion": "13.0.0.0", "fileVersion": "13.0.3.0"}}
            },
        }
    },
    "libraries": {
        "Jellyfin.Plugin.LocalMeta/1.0.0": {"type": "project", "serviceable": False, "sha512": ""},
        "Microsoft.Data.Sqlite/9.0.0": {"type": "package", "serviceable": True, "sha512": ""},
        "Newtonsoft.Json/13.0.3": {"type": "package", "serviceable": True, "sha512": ""},
    },
}
with open(deps_path, "w", encoding="utf-8") as f:
    json.dump(deps, f, indent=2)
out.append("  deps.json 已重写（剔除宿主已提供依赖）")

# 清掉 Malfunctioned 标记（规则 4）
meta = os.path.join(DST, "meta.json")
if os.path.exists(meta):
    os.remove(meta)
    out.append("  已删除 meta.json（清除 Malfunctioned）")

# 最终确认没有多余 dll
extra = [n for n in os.listdir(DST) if n.lower().endswith(".dll") and n not in ALLOW]
out.append("")
if extra:
    out.append("警告：目录里有多余 dll，Jellyfin 会当程序集加载导致崩溃: " + ", ".join(extra))
else:
    out.append("最终目录只有允许的托管程序集 ✓")

open(LOG, "w", encoding="utf-8").write("\n".join(out))
print("done")
