#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
repair_list - 生成"低分头像待重刮"名单

输入: avatarscan_report.csv（先跑 avatarscan.py）
输出: avatar_repair.csv —— 演员名 / Person 实体 Id / 现状尺寸 / 源头像尺寸 / 建议动作

Jellyfin 12.1.0 注意：Person 实体的 BaseItems.Id 与 Peoples.Id 已脱钩，
刷新元数据要用 BaseItems.Id（人物页那个），用 Peoples.Id 调 API 会 400。
"""
import os
import csv
import json
import shutil
import sqlite3
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPORT = os.path.join(HERE, "avatarscan_report.csv")
OUT = os.path.join(HERE, "avatar_repair.csv")
DB_SRC = r"C:\Jellyfin\Data\data\jellyfin.db"
PERSON_TYPE = "MediaBrowser.Controller.Entities.Person"


def fresh_db():
    """复制一份再查，避免和正在运行的 Jellyfin 抢 sqlite 锁。"""
    tmp = os.path.join(tempfile.gettempdir(), "jf_repair.db")
    for suffix in ("", "-wal", "-shm"):
        src = DB_SRC + suffix
        if os.path.exists(src):
            try:
                shutil.copyfile(src, tmp + suffix)
            except Exception:
                pass
    return tmp


def main():
    rows = list(csv.DictReader(open(REPORT, encoding="utf-8-sig")))
    def is_bad(r):
        """坏图(0 字节) 或 短边落在 1XX —— 竖图按短边判，不误杀 400x600。"""
        w, h = int(r["w"] or 0), int(r["h"] or 0)
        if w == 0:
            return True
        return 100 <= min(w, h) <= 199

    low = [r for r in rows if is_bad(r)]
    # 同一演员可能有多张图（folder/poster），按人去重取最小的那张
    by_person = {}
    for r in low:
        cur = by_person.get(r["person"])
        if cur is None or int(r["w"]) < int(cur["w"]):
            by_person[r["person"]] = r

    db = fresh_db()
    conn = sqlite3.connect("file:" + db + "?mode=ro", uri=True)
    cur = conn.cursor()

    out = []
    for name, r in sorted(by_person.items()):
        pid = None
        for cand in (name, name.split("（")[0].strip()):
            cur.execute("SELECT Id FROM BaseItems WHERE Type=? AND Name=? LIMIT 1",
                        (PERSON_TYPE, cand))
            row = cur.fetchone()
            if row:
                pid = row[0]
                break

        src_w = r["src_w"]
        if src_w and int(src_w) >= 300:
            action = "源图够大却被压低-需排查"
        elif src_w:
            action = "源图即低分-走Metatube重刮"
        else:
            action = "源无头像-走Metatube重刮"

        out.append({
            "person": name,
            "person_item_id": pid or "",
            "cur_size": f'{r["w"]}x{r["h"]}',
            "cur_bytes": r["bytes"],
            "src_size": f'{src_w}x{r["src_h"]}' if src_w else "",
            "origin": r["origin"],
            "action": action,
        })

    with open(OUT, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=list(out[0].keys()))
        w.writeheader()
        w.writerows(out)

    ok = sum(1 for o in out if o["person_item_id"])
    print(f"1XX 头像 {len(out)} 人，已定位 Person 实体 Id 的 {ok} 人")
    print(f"输出: {OUT}")

    from collections import Counter
    print("\n建议动作分布:")
    for k, v in Counter(o["action"] for o in out).most_common():
        print(f"  {k}: {v}")

    print("\n前 3 条（用于探测 API）:")
    for o in out[:3]:
        print(f"  {o['person']} -> {o['person_item_id']}  {o['cur_size']}")

    with open(os.path.join(HERE, "repair_ids.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(o["person_item_id"] for o in out if o["person_item_id"]))
    print("\nId 清单: repair_ids.txt")


if __name__ == "__main__":
    main()
