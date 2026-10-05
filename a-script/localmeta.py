#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
LocalMeta 守护脚本 —— Jellyfin 人物资料本地兜底补齐。

设计原则：
  1. 幂等：反复跑，结果不变。已有文件/已有字段不重复写。
  2. 只补不覆盖：默认 overwriteExisting=false，绝不冲掉 metatube 刮来的内容。
  3. 配置驱动：数据源、库名、归一化规则全在 config.json，加源不用改代码。
  4. 可审计：每次跑生成日志 + JSON/CSV 报告，缺口清单留档。

用法：
  python localmeta.py                       # 按计划任务默认配置跑
  python localmeta.py --dry-run             # 只扫描不写
  python localmeta.py --only avatar         # 只补头像
  python localmeta.py --only bio            # 只补简介
  python localmeta.py --config other.json   # 换配置
"""
import argparse
import csv
import json
import os
import re
import shutil
import sqlite3
import struct
import sys
import time
import uuid
from datetime import datetime

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CFG = os.path.join(HERE, "config.json")
IMG_TYPE_PRIMARY = 0


def log(msg):
    print("[%s] %s" % (datetime.now().strftime("%H:%M:%S"), msg))


# ---------------------------------------------------------------- 配置
def load_config(path):
    with open(path, encoding="utf-8") as f:
        cfg = json.load(f)
    return cfg


def make_normalizer(rules):
    """按配置构造名字归一化规则"""
    pats = [re.compile(p) for p in rules.get("stripPatterns", []) if p]

    def norm(name):
        s = str(name or "").strip()
        if rules.get("stripParenthesized", True):
            s = re.split(r"[（(]", s)[0].strip()
        for p in pats:
            s = p.split(s)[0].strip() if p.pattern.find("|") >= 0 else p.sub("", s)
        if rules.get("foldWhitespace", True):
            s = re.sub(r"\s+", " ", s)
        if rules.get("trim", True):
            s = s.strip()
        return s

    return norm


# ---------------------------------------------------------------- 数据源
class AvatarFolderSource:
    """本地文件夹头像源：plan(json) 做 名字->编号 索引，avatars 目录放实体文件"""

    def __init__(self, spec):
        self.name = spec["name"]
        self.root = spec["path"]
        self.avatars = os.path.join(self.root, spec.get("dirs", {}).get("avatars", "avatars"))
        self.plan_file = os.path.join(self.root, spec.get("dirs", {}).get("plan", "gfriends_plan.json"))
        self.exts = spec.get("extensions", [".jpg", ".webp"])
        self._by_name = {}
        self._load_plan()

    def _load_plan(self):
        if not os.path.exists(self.plan_file):
            return
        with open(self.plan_file, encoding="utf-8") as f:
            plan = json.load(f)
        for item in plan:
            gid, nm = str(item[0]), str(item[1])
            self._by_name.setdefault(nm, gid)
            self._by_name.setdefault(re.split(r"[（(]", nm)[0].strip(), gid)

    def find(self, raw_name):
        """返回本地头像文件路径，找不到返回 None"""
        gid = self._by_name.get(raw_name)
        if gid:
            for e in self.exts:
                p = os.path.join(self.avatars, gid + e)
                if os.path.exists(p):
                    return p
        # 兜底：目录里直接以人名命名
        for e in self.exts:
            p = os.path.join(self.avatars, raw_name + e)
            if os.path.exists(p):
                return p
        return None


class AvatarPlanSource:
    """纯映射表源：plan 的 name 直接映射到文件名"""

    def __init__(self, spec):
        self.name = spec["name"]
        self.plan_file = spec["path"]
        self.exts = spec.get("extensions", [".jpg", ".webp"])
        self.dir = spec.get("dir")
        self._by_name = {}
        if os.path.exists(self.plan_file):
            with open(self.plan_file, encoding="utf-8") as f:
                plan = json.load(f)
            for item in plan:
                key, val = str(item[1]), str(item[0])
                self._by_name.setdefault(key, val)
                self._by_name.setdefault(re.split(r"[（(]", key)[0].strip(), val)

    def find(self, raw_name):
        val = self._by_name.get(raw_name)
        if not val:
            return None
        for e in self.exts:
            if self.dir:
                p = os.path.join(self.dir, val + e)
            else:
                p = os.path.join(os.path.dirname(self.plan_file), val + e)
            if os.path.exists(p):
                return p
        return None


class ProfileSqliteSource:
    """本地 sqlite 人物资料源（如 JavBoss 的 jav_idol）"""

    CUP_LETTERS = "ABCDEFGHIJKLMNOPQ"   # cup 数字是索引：0->A，实测 cup=6->G/7->H/8->I 自洽

    def __init__(self, spec, limits=None):
        self.name = spec["name"]
        self.db = spec["path"]
        self.limits = limits or {}
        self.table = spec.get("table", "jav_idol")
        self.cols = spec.get("columns") or {
            "name": "name", "roman": "roman_name", "height": "height_cm",
            "bust": "bust", "waist": "waist", "hips": "hips",
            "cup": "cup", "birth": "birth_date",
        }
        self._rows = {}
        self._load()

    def _load(self):
        if not os.path.exists(self.db):
            return
        con = sqlite3.connect("file:%s?mode=ro" % self.db, uri=True)
        c, t = self.cols, self.table
        try:
            rows = con.execute(
                "SELECT %s, %s, %s, %s, %s, %s, %s, %s FROM %s"
                % (c["name"], c.get("roman", c["name"]), c["height"], c["bust"],
                   c["waist"], c["hips"], c["cup"], c["birth"], t)).fetchall()
        except sqlite3.Error as e:
            log("  资料源 %s 读取失败: %s" % (self.name, e))
            con.close()
            return
        con.close()
        for r in rows:
            for key in (r[0], r[1]):
                if key:
                    self._rows.setdefault(str(key), r)
        log("  资料源 %s 载入 %d 条" % (self.name, len(rows)))

    def _cup_letter(self, v):
        try:
            i = int(v)
        except (TypeError, ValueError):
            return str(v) if v and str(v).strip() else ""
        if i < 0 or i >= len(self.CUP_LETTERS):
            return ""
        return self.CUP_LETTERS[i]

    def _int(self, v):
        try:
            return int(str(v).strip())
        except (TypeError, ValueError):
            return 0

    def _date(self, v):
        m = re.search(r"(\d{4})-(\d{1,2})-(\d{1,2})", str(v or ""))
        return "%s年%d月%d日" % (m.group(1), int(m.group(2)), int(m.group(3))) if m else ""

    def build(self, name):
        """拼一条 Overview 文本，资料不足返回空串"""
        r = self._rows.get(name)
        if not r:
            return ""
        _, _, height, bust, waist, hips, cup, birth = r
        lim = self.limits
        hr = lim.get("heightRange", [130, 200])
        gr = lim.get("girthRange", [50, 130])
        parts = []
        b, w, h = self._int(bust), self._int(waist), self._int(hips)
        if b and w and h and gr[0] <= b <= gr[1] and gr[0] <= w <= gr[1] and gr[0] <= h <= gr[1]:
            parts.append("3サイズ: B:%d / W:%d / H:%d" % (b, w, h))
        cl = self._cup_letter(cup)
        if cl and b:
            parts.append("カップサイズ: %s" % cl)
        hh = self._int(height)
        if hr[0] <= hh <= hr[1]:
            parts.append("身長: %dcm" % hh)
        bd = self._date(birth)
        if bd:
            parts.append("デビュー: %s" % bd)
        return " <br> ".join(parts)


FACTORY = {
    "avatar_folder": AvatarFolderSource,
    "avatar_plan": AvatarPlanSource,
    "profile_sqlite": ProfileSqliteSource,
}


def build_sources(cfg):
    out = {"avatar": [], "profile": []}
    for spec in cfg.get("sources", []):
        if not spec.get("enabled", True):
            log("  数据源 %s 已禁用" % spec["name"])
            continue
        kind = spec.get("kind")
        if kind not in FACTORY:
            log("  未知数据源类型 %r，跳过" % kind)
            continue
        obj = FACTORY[kind](spec, cfg.get("limits", {})) if kind == "profile_sqlite" \
            else FACTORY[kind](spec)
        bucket = "profile" if kind == "profile_sqlite" else "avatar"
        out[bucket].append(obj)
    return out


# ---------------------------------------------------------------- 图片
def image_size(path):
    """极简读 jpg/webp 宽高，失败返回 (0,0)"""
    try:
        with open(path, "rb") as f:
            data = f.read(65536)
    except OSError:
        return 0, 0
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        if data[12:16] == b"VP8 ":
            w = struct.unpack("<HH", data[26:30])
            return w[0] & 0x3FFF, w[1] & 0x3FFF
        if data[12:16] == b"VP8L":
            b = data[21:25]
            bits = int.from_bytes(b, "little")
            return (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1
    i = 2
    while i < len(data) - 9:
        if data[i] != 0xFF:
            i += 1
            continue
        m = data[i + 1]
        if m in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB):
            h, w = struct.unpack(">HH", data[i + 5:i + 9])
            return int(w), int(h)
        if m in (0xD8, 0xD9) or 0xD0 <= m <= 0xD7:
            i += 2
            continue
        i += 2 + struct.unpack(">H", data[i + 2:i + 4])[0]
    return 0, 0


def detect_person_type(con, cfg):
    """探测 BaseItems.Type 里 Person 实体的实际写法。

    Jellyfin 12.1.0 起 Type 从短名 Person 改成完整类名
    MediaBrowser.Controller.Entities.Person；10.11 及更早是短名。
    以配置为首选，查不到就依次退回，两个版本都能跑。
    """
    cands = [cfg.get("personType") or "Person", "Person",
             "MediaBrowser.Controller.Entities.Person"]
    tried = []
    for t in cands:
        if not t or t in tried:
            continue
        tried.append(t)
        if con.execute("SELECT 1 FROM BaseItems WHERE Type=? LIMIT 1", (t,)).fetchone():
            if t != cands[0]:
                log("  Type 写法回退：%s -> %s（数据库与配置不一致，已自动适配）"
                    % (cands[0], t))
            return t
    return cands[0]


# ---------------------------------------------------------------- 扫描
def scan(cfg):
    """只读扫描：返回本库演员清单（peoples_id / name / person_id / 现有图 / 现有简介）"""
    db = cfg["jellyfinDb"]
    if not os.path.exists(db):
        raise SystemExit("找不到 Jellyfin 数据库: %s" % db)
    con = sqlite3.connect("file:%s?mode=ro" % db, uri=True)
    libs = [x.strip() for x in cfg["library"].split(",") if x.strip()]
    q = lambda s, *a: con.execute(s, a).fetchall()

    person_type = detect_person_type(con, cfg)

    film_ids = []
    for lib in libs:
        got = q("SELECT Id FROM BaseItems WHERE Name=?", lib)
        if got:
            film_ids += [r[0] for r in q("SELECT ItemId FROM AncestorIds WHERE ParentItemId=?", got[0][0])]
        else:
            log("  警告：找不到媒体库 %r" % lib)
    if not film_ids:
        con.close()
        return []

    act = q("SELECT m.PeopleId, p.Name FROM PeopleBaseItemMap m JOIN Peoples p ON p.Id=m.PeopleId "
            "WHERE p.PersonType='Actor' AND m.ItemId IN (%s)"
            % ",".join("?" * len(film_ids)), *film_ids)

    per = {}
    for pid, nm in act:
        per.setdefault(pid, nm)

    person = {}
    for pid, nm in per.items():
        got = q("SELECT Id FROM BaseItems WHERE Type=? AND Name=?", person_type, nm)
        person[pid] = got[0][0] if got else ""

    # 12.1.0 起 Person 条目的 BaseItems.Id 与 Peoples.Id 脱钩成两个 GUID，
    # 头像记录挂在 BaseItems.Id（Person 条目）上；10.11 两者是同一个值。
    # 因此先查 person_id，查不到再退回 peoples_id，两个版本都不会误判。
    have_img = set()
    for pid, iid in person.items():
        for cand in (iid, pid):
            if not cand:
                continue
            if q("SELECT COUNT(*) FROM BaseItemImageInfos WHERE ItemId=? AND ImageType=?",
                 cand, IMG_TYPE_PRIMARY)[0][0]:
                have_img.add(pid)
                break

    have_bio = set()
    for pid, iid in person.items():
        if iid:
            for (ov,) in q("SELECT Overview FROM BaseItems WHERE Id=?", iid):
                if ov and ov.strip():
                    have_bio.add(pid)

    con.close()
    return [{"peoples_id": pid, "name": nm, "person_id": person.get(pid, ""),
             "has_img": pid in have_img, "has_bio": pid in have_bio}
            for pid, nm in per.items()]


def person_dir(metadata_dir, name):
    return os.path.join(metadata_dir, name[0], name)


# ---------------------------------------------------------------- 执行
def run(cfg, only="all", dry=False):
    started = datetime.now()
    log("LocalMeta 启动 | 库=%s | 模式=%s%s" % (
        cfg["library"], only, " | DRY-RUN" if dry else ""))

    sources = build_sources(cfg)
    norm = make_normalizer(cfg.get("nameRules", {}))
    rows = scan(cfg)
    log("本库唯一演员: %d" % len(rows))

    md = cfg["metadataDir"]
    avatars = sources["avatar"]
    profiles = sources["profile"]
    overwrite = cfg.get("overwriteExisting", False)
    limits = cfg.get("limits", {})

    todo_img, todo_bio = [], []
    for r in rows:
        if only in ("all", "avatar") and not r["has_img"]:
            todo_img.append(r)
        if only in ("all", "bio") and not r["has_bio"]:
            todo_bio.append(r)

    log("待补头像 %d，待补简介 %d" % (len(todo_img), len(todo_bio)))

    # ---- 头像 ----
    files_ok, files_skip_db, files_no_src = 0, 0, 0
    rows_img = []
    avatar_log = []
    for r in todo_img:
        name = r["name"]
        if avatars:
            src = None
            for s in avatars:
                src = s.find(name) or s.find(norm(name))
                if src:
                    break
        else:
            src = None
        if not src:
            files_no_src += 1
            continue
        dst_dir = person_dir(md, name)
        ext = os.path.splitext(src)[1]
        dst = os.path.join(dst_dir, "folder" + ext)
        if not dry:
            os.makedirs(dst_dir, exist_ok=True)
            shutil.copy2(src, dst)
        w, h = image_size(dst if not dry and os.path.exists(dst) else src)
        if limits.get("minHeight") and h and h < limits["minHeight"]:
            log("  跳过过小图: %s (%dx%d)" % (name, w, h))
            continue
        files_ok += 1
        avatar_log.append({"name": name, "src": src, "dst": dst, "w": w, "h": h})
        if overwrite or not r["has_img"]:
            # 头像要挂在 Person 条目（BaseItems.Id）上；只有在该演员根本没有
            # Person 条目时才退回 Peoples.Id。10.11 下两者同值，写一条即可，
            # 不会像旧版那样产生两条重复记录。
            for iid in ([r["person_id"]] if r["person_id"] else [r["peoples_id"]]):
                if iid:
                    rows_img.append((str(uuid.uuid4()).upper(), iid, IMG_TYPE_PRIMARY,
                                     datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S.0000000Z"),
                                     h, w, dst.replace("/", "\\"), None))
    log("头像 落地文件 %d，无源 %d" % (files_ok, files_no_src))

    # ---- 简介 ----
    bio_ok, bio_nosrc = 0, 0
    bio_log = []
    for r in todo_bio:
        text = ""
        for s in profiles:
            text = s.build(r["name"]) or s.build(norm(r["name"]))
            if text:
                break
        if not text:
            bio_nosrc += 1
            continue
        bio_ok += 1
        bio_log.append({"name": r["name"], "person_id": r["person_id"],
                        "text": text, "len": len(text)})

    log("简介 可补 %d，无源 %d" % (bio_ok, bio_nosrc))

    # ---- 备份 + 写库 ----
    backups = []
    if cfg.get("backup", True) and (files_ok or bio_ok) and not dry:
        db = cfg["jellyfinDb"]
        bak = db + ".bak_localmeta_" + started.strftime("%Y%m%d_%H%M%S")
        with open(bak, "wb") as f:
            f.write(open(db, "rb").read())
        backups.append(bak)
        log("已备份 -> %s (%.1f MB)" % (os.path.basename(bak), os.path.getsize(bak) / 1048576))
        keep = cfg.get("backupMax", 10)
        olds = sorted([p for p in os.listdir(os.path.dirname(db))
                       if p.startswith(os.path.basename(db) + ".bak_localmeta_")])
        for p in olds[:-keep]:
            try:
                os.remove(os.path.join(os.path.dirname(db), p))
            except OSError:
                pass

    written = {"images": 0, "bios": 0}
    if not dry and (rows_img or bio_ok):
        con = sqlite3.connect(cfg["jellyfinDb"], timeout=(cfg.get("busyTimeoutMs", 30000) / 1000.0))
        con.execute("PRAGMA busy_timeout=%d" % cfg.get("busyTimeoutMs", 30000))
        if rows_img:
            con.executemany(
                "INSERT OR REPLACE INTO BaseItemImageInfos "
                "(Id,ItemId,ImageType,DateModified,Height,Width,Path,Blurhash) "
                "VALUES (?,?,?,?,?,?,?,?)", rows_img)
        if bio_ok:
            for t in bio_log:
                if t["person_id"]:
                    con.execute(
                        "UPDATE BaseItems SET Overview=? WHERE Id=? AND (Overview IS NULL OR Overview='')",
                        (t["text"], t["person_id"]))
        con.commit()
        written["images"] = len(rows_img)
        con.close()
    else:
        log("DRY-RUN / 无写入：跳过写库")

    # ---- 报告 ----
    stamp = started.strftime("%Y%m%d_%H%M")
    rep = {"run_at": started.isoformat(timespec="seconds"), "mode": only,
           "dry_run": dry, "actors": len(rows),
           "avatar_found": files_ok, "avatar_no_source": files_no_src,
           "bio_found": bio_ok, "bio_no_source": bio_nosrc,
           "db_writes": written, "backups": [os.path.basename(b) for b in backups],
           "duration_s": round((datetime.now() - started).total_seconds(), 1)}
    rd = os.path.join(HERE, cfg.get("reportDir", "lists"))
    os.makedirs(rd, exist_ok=True)
    with open(os.path.join(rd, "report_%s.json" % stamp), "w", encoding="utf-8") as f:
        json.dump(rep, f, ensure_ascii=False, indent=2)

    log("完成：头像文件 %d / 图记录 %d / 简介 %d / 耗时 %.1fs"
        % (files_ok, written["images"], bio_ok, rep["duration_s"]))

    # 计划任务默认不保留 stdout，脚本自己把摘要落盘，方便以后查"它到底跑没跑"
    try:
        with open(os.path.join(HERE, "logs", "scheduled.log"), "a", encoding="utf-8") as f:
            f.write("[%s] 头像 %d / 图记录 %d / 简介 %d / 耗时 %.1fs%s\n"
                    % (started.strftime("%Y-%m-%d %H:%M:%S"), files_ok, written["images"],
                       bio_ok, rep["duration_s"],
                       " | DRY-RUN" if dry else ""))
    except OSError:
        pass
    return rep


def main():
    ap = argparse.ArgumentParser(description="LocalMeta: Jellyfin 人物资料本地兜底补齐")
    ap.add_argument("--config", default=DEFAULT_CFG)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--only", choices=["all", "avatar", "bio"], default="all")
    a = ap.parse_args()
    cfg = load_config(a.config)
    run(cfg, only=a.only, dry=a.dry_run)


if __name__ == "__main__":
    main()
