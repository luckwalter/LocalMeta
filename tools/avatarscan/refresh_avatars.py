#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
refresh_avatars - 走 Jellyfin 远程通道（MetaTube）重刮低分头像

用法:
    python refresh_avatars.py --limit 10        # 先刷 10 个试效果
    python refresh_avatars.py                   # 全量
    python refresh_avatars.py --restore         # 还原刷之前的备份

注意（12.1.0 实测）:
  - 认证头必须是 Authorization: MediaBrowser Token="<key>"，
    X-Emby-Token 和 ?api_key= 都返回 401。
  - 刷新要用 Person 实体的 BaseItems.Id，用 Peoples.Id 会 400。
  - ReplaceAllImages=true 会先删旧图再下新图，刮不到就剩空，
    所以刷之前先把原文件备份到 LocalMeta_backup/avatars_before_refresh/。
"""
import os
import csv
import sys
import time
import shutil
import argparse
import urllib.request
import urllib.error

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPAIR = os.path.join(HERE, "avatar_repair.csv")
TOKEN_FILE = os.path.join(HERE, "_apitoken.txt")
PEOPLE = r"C:\Jellyfin\Data\metadata\People"
BACKUP = r"C:\Jellyfin\Data\LocalMeta_backup\avatars_before_refresh"
BASE = "http://localhost:8096"


def token():
    if not os.path.exists(TOKEN_FILE):
        print("缺少 " + TOKEN_FILE)
        sys.exit(1)
    return open(TOKEN_FILE, encoding="utf-8").read().strip()


def hdrs():
    # 12.1.0 只认这一种写法
    return {"Authorization": 'MediaBrowser Token="%s"' % token()}


def avatar_path(person):
    """定位 metadata/People/<首字母>/<姓名>/folder.*"""
    d = os.path.join(PEOPLE, person[0], person)
    if not os.path.isdir(d):
        return None
    for fn in os.listdir(d):
        if fn.lower().startswith("folder."):
            return os.path.join(d, fn)
    return None


def size_of(p):
    if not p or not os.path.exists(p):
        return (0, 0)
    try:
        if os.path.getsize(p) == 0:
            return (0, 0)
        with Image.open(p) as im:
            return im.size
    except Exception:
        return (0, 0)


def refresh(item_id):
    url = (BASE + "/Items/" + item_id + "/Refresh"
           "?MetadataRefreshMode=FullRefresh"
           "&ImageRefreshMode=FullRefresh"
           "&ReplaceAllImages=true")
    req = urllib.request.Request(url, method="POST", headers=hdrs())
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code
    except Exception as e:
        return "ERR:" + str(e)[:60]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="只刷前 N 个，0=全部")
    ap.add_argument("--skip", type=int, default=0, help="跳过前 N 个（续跑用）")
    ap.add_argument("--wait", type=int, default=90, help="刷完后等待秒数")
    ap.add_argument("--restore", action="store_true", help="还原备份，不做刷新")
    args = ap.parse_args()

    rows = [r for r in csv.DictReader(open(REPAIR, encoding="utf-8-sig"))
            if r["person_item_id"]]
    if args.skip:
        rows = rows[args.skip:]
    if args.limit:
        rows = rows[:args.limit]

    if args.restore:
        n = 0
        for r in rows:
            src = os.path.join(BACKUP, r["person"] + ".jpg")
            dst = avatar_path(r["person"])
            if os.path.exists(src) and dst:
                shutil.copyfile(src, dst)
                n += 1
        print("已还原 %d 个头像" % n)
        return

    os.makedirs(BACKUP, exist_ok=True)

    print("待刷 %d 人，先备份原图到 %s" % (len(rows), BACKUP))
    for r in rows:
        p = avatar_path(r["person"])
        if p and os.path.getsize(p) > 0:
            shutil.copyfile(p, os.path.join(BACKUP, r["person"] + ".jpg"))
        r["_before"] = size_of(p)
        r["_path"] = p

    print("\n开始刷新（间隔 2s，避免打爆 MetaTube）:")
    for i, r in enumerate(rows, 1):
        code = refresh(r["person_item_id"])
        print("  [%2d/%d] %-22s %s  before=%dx%d"
              % (i, len(rows), r["person"][:22], code,
                 r["_before"][0], r["_before"][1]))
        if i < len(rows):
            time.sleep(2)

    print("\n等待 %ds 让图片落盘..." % args.wait)
    time.sleep(args.wait)

    print("\n=== 对比 ===")
    better = same = worse = 0
    for r in rows:
        after = size_of(r["_path"])
        b, a = r["_before"], after
        bb, aa = min(b) if min(b) else 0, min(a) if min(a) else 0
        if aa > bb:
            flag, better = "↑ 变好", better + 1
        elif aa == bb:
            flag, same = "= 没变", same + 1
        else:
            flag, worse = "↓ 变差", worse + 1
        print("  %-22s %dx%d -> %dx%d  %s"
              % (r["person"][:22], b[0], b[1], a[0], a[1], flag))

    print("\n变好 %d / 没变 %d / 变差 %d" % (better, same, worse))
    if worse:
        print("变差的可用 python refresh_avatars.py --restore 还原")


if __name__ == "__main__":
    main()
