#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
avatarscan - Jellyfin 演员头像分辨率体检

用途:
  1. 扫描 Jellyfin 生效头像目录 metadata/People/**，统计分辨率分布
  2. 交叉 LocalMeta 数据源 (gfriends_plan.json + avatars/)，判定低分头像的来源
     - 若 People 头像字节数 == avatars/<id>.jpg 字节数  -> 由 LocalMeta 写入
     - 若 gfriends 源图本身就是 1XX                     -> 源头即低分
     - 若未命中 gfriends / 字节不等                      -> 来自 Metatube 等远程刮削
  3. 输出 CSV 明细 + 控制台汇总，供决定是否需要走 Metatube 重刮

只读脚本，不修改任何数据。
"""
import os
import sys
import csv
import json
import time
from collections import Counter

from PIL import Image

PEOPLE = r"C:\Jellyfin\Data\metadata\People"
SOURCES = r"C:\Jellyfin\Data\LocalMeta-Sources"
AVATARS = os.path.join(SOURCES, "avatars")
PLAN = os.path.join(SOURCES, "gfriends_plan.json")
HERE = os.path.dirname(os.path.abspath(__file__))
OUT_CSV = os.path.join(HERE, "avatarscan_report.csv")

IMG_EXT = {".jpg", ".jpeg", ".png", ".webp"}
LOW_MIN, LOW_MAX = 100, 199          # 1XX 像素区间


def size_of(path):
    try:
        with Image.open(path) as im:
            return im.size
    except Exception:
        return None


def bucket(w):
    if w <= 0:
        return "0-损坏"
    if w < 100:
        return "<100"
    if w <= 199:
        return "100-199(1XX)"
    if w <= 299:
        return "200-299"
    if w <= 449:
        return "300-449"
    if w <= 599:
        return "450-599"
    if w <= 999:
        return "600-999"
    return ">=1000"


BUCKET_ORDER = ["0-损坏", "<100", "100-199(1XX)", "200-299", "300-449",
                "450-599", "600-999", ">=1000"]


def main():
    # ---------- 1. gfriends 计划：id <-> name ----------
    name2id, id2name = {}, {}
    if os.path.exists(PLAN):
        with open(PLAN, encoding="utf-8") as f:
            for item in json.load(f):
                gid, name = item[0], item[1]
                id2name[gid] = name
                name2id.setdefault(name, gid)
    print(f"[源] gfriends_plan.json: {len(id2name)} 条")

    # ---------- 2. avatars 源图：id -> {ext: (w,h,bytes)} ----------
    src = {}
    src_zero = Counter()
    for fn in os.listdir(AVATARS):
        stem, ext = os.path.splitext(fn)
        ext = ext.lower()
        if ext not in IMG_EXT or not stem.isdigit():
            continue
        p = os.path.join(AVATARS, fn)
        b = os.path.getsize(p)
        gid = int(stem)
        if b == 0:
            src_zero[ext] += 1
            src.setdefault(gid, {})[ext] = (None, None, 0)
            continue
        s = size_of(p)
        src.setdefault(gid, {})[ext] = (s[0] if s else None,
                                        s[1] if s else None, b)

    print(f"[源] avatars: {len(src)} 个 id, 0 字节文件 "
          f"webp={src_zero.get('.webp',0)} jpg={src_zero.get('.jpg',0)}")

    src_buckets = Counter()
    for gid, m in src.items():
        jpg = m.get(".jpg")
        if jpg and jpg[0]:
            src_buckets[bucket(jpg[0])] += 1
    print("\n=== 源头 avatars/*.jpg 分辨率分布 ===")
    for b in BUCKET_ORDER:
        if src_buckets.get(b):
            print(f"  {b:>14}: {src_buckets[b]}")

    # ---------- 3. Jellyfin 生效头像 ----------
    rows = []
    for root, _dirs, files in os.walk(PEOPLE):
        person = os.path.basename(root)
        if root == PEOPLE:
            continue
        for fn in files:
            ext = os.path.splitext(fn)[1].lower()
            if ext not in IMG_EXT:
                continue
            p = os.path.join(root, fn)
            b = os.path.getsize(p)
            s = size_of(p)
            w = s[0] if s else 0
            h = s[1] if s else 0
            gid = name2id.get(person)
            s_jpg = src.get(gid, {}).get(".jpg") if gid is not None else None
            s_webp = src.get(gid, {}).get(".webp") if gid is not None else None
            # 字节完全一致 => 由 LocalMeta 从源复制。
            # 两边同为 0 也要算：源里存在 0 字节 jpg，复制进库就是一张坏图，
            # 比低分更糟，不能因为它"和源一样"就归到远程刮削头上。
            if b == 0:
                origin = "LocalMeta(源0字节坏图)" if (s_jpg and s_jpg[2] == 0) else "坏图(来源不明)"
            elif s_jpg and s_jpg[2] == b:
                origin = "LocalMeta(gfriends-jpg)"
            elif s_webp and s_webp[2] == b:
                origin = "LocalMeta(gfriends-webp)"
            elif gid is not None:
                origin = "远程刮削(源中有此人)"
            else:
                origin = "远程刮削(源无此人)"
            rows.append({
                "person": person,
                "file": os.path.relpath(p, PEOPLE),
                "w": w, "h": h, "bytes": b,
                "mtime": time.strftime("%Y-%m-%d %H:%M",
                                       time.localtime(os.path.getmtime(p))),
                "gfriends_id": gid if gid is not None else "",
                "src_w": (s_jpg[0] if s_jpg and s_jpg[0] else ""),
                "src_h": (s_jpg[1] if s_jpg and s_jpg[1] else ""),
                "src_bytes": (s_jpg[2] if s_jpg else ""),
                "origin": origin,
            })

    print(f"\n[库] People 头像文件: {len(rows)}")
    people_buckets = Counter(bucket(r["w"]) for r in rows)
    print("\n=== Jellyfin 生效头像分辨率分布 (按宽度) ===")
    for b in BUCKET_ORDER:
        if people_buckets.get(b):
            n = people_buckets[b]
            print(f"  {b:>14}: {n}")

    origin_cnt = Counter(r["origin"] for r in rows)
    print("\n=== 来源判定 ===")
    for k, v in origin_cnt.most_common():
        print(f"  {k:<28}: {v}")

    # ---------- 4. 问题头像明细 ----------
    # 短边判定最合理：166x236 这类竖图宽 166 该算低分，400x600 这种又不能误杀。
    def short_side(r):
        return min(r["w"], r["h"]) if r["w"] and r["h"] else 0

    low_w = [r for r in rows if LOW_MIN <= r["w"] <= LOW_MAX]
    low_s = [r for r in rows if LOW_MIN <= short_side(r) <= LOW_MAX]
    bad = [r for r in rows if r["w"] == 0]

    seen = set()
    low = []
    for r in sorted(bad + low_s, key=lambda r: (r["w"], r["h"])):
        if r["file"] not in seen:
            seen.add(r["file"])
            low.append(r)

    print(f"\n=== 问题头像 ===")
    print(f"  0 字节坏图      : {len(bad)}")
    print(f"  宽度 1XX        : {len(low_w)}")
    print(f"  短边 1XX        : {len(low_s)}")
    print(f"  合计(坏图+短边1XX): {len(low)}")
    low_origin = Counter(r["origin"] for r in low)
    print("\n  来源分布:")
    for k, v in low_origin.most_common():
        print(f"    {k:<28}: {v}")

    # 问题图里：源图本身也低分？
    src_also_low = [r for r in low if r["src_w"] and LOW_MIN <= r["src_w"] <= LOW_MAX]
    src_ok = [r for r in low if r["src_w"] and r["src_w"] >= 300]
    src_missing = [r for r in low if not r["src_w"]]
    print(f"\n  其中 源图本身也是 1XX     : {len(src_also_low)}  (换源无效, 需 Metatube)")
    print(f"  其中 源图 >=300px 却被压低: {len(src_ok)}  (异常, 需排查)")
    print(f"  其中 源无头像/源为 0 字节 : {len(src_missing)}  (Metatube 兜底)")

    # ---------- 5. 写 CSV ----------
    with open(OUT_CSV, "w", newline="", encoding="utf-8-sig") as f:
        wcsv = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        wcsv.writeheader()
        wcsv.writerows(sorted(rows, key=lambda r: r["w"]))
    print(f"\n[输出] 明细: {OUT_CSV}")

    print("\n=== 问题头像清单 (前 40) ===")
    for r in low[:40]:
        print(f"  {r['w']}x{r['h']:<5} {r['person'][:24]:<26} "
              f"源={r['src_w'] or '-'}x{r['src_h'] or '-':<5} {r['origin']}")

    # ---------- 6. 建议重刮名单 ----------
    need = [r for r in low if not (r["src_w"] and r["src_w"] >= 300)]
    need_names = sorted({r["person"] for r in need})
    out_list = os.path.join(HERE, "avatar_low1xx_names.txt")
    with open(out_list, "w", encoding="utf-8") as f:
        f.write("\n".join(need_names))
    print(f"\n[输出] 建议走 Metatube 重刮的演员名 ({len(need_names)} 个): {out_list}")

if __name__ == "__main__":
    main()
