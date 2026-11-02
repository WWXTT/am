"""按群清除历史记录（Yuno 记忆库 + 原文层 + 印象卡 + OCR 账本）。

用法（仓库根）：
  yuno\\.venv\\Scripts\\python.exe qq-agent\\tools\\purge-group-history.py --chat 潮汐            # 只统计（dry-run）
  ... --chat 潮汐 --apply                                                                        # 执行（先自动备份）

清除范围（scope = group:<gid> 与 group_all:<gid>）：
  bot.db      memories / memory_attrs / memory_meta / events / memory_history / memory_trace /
              relationships / entities / topics / vec_index / bm25_docs（+ FTS 重建）
  insight.db  analysis_runs / person_attributes / relation_evidence / relation_edges / interaction_stats（按 group_id）
  files       logs\\messages.jsonl（该群的行移到备份，其余行重排）
              logs\\ocr-ledger.jsonl（从剩余原文的 [图片|…] 重建，行号连续）
              profiles\\group_<gid>\\*.md（移进备份目录）
              logs\\amy-pending.json（若属于该群则删除，等 daemon 重新投递）

**先备份后删**；备份落在 yuno\\data\\persona-amy\\backups\\purge-<chat>-<ts>\\。
原文只存不删是红线——只在用户明确要求清库时跑本脚本（本文件即该授权的执行体）。
"""

import argparse
import datetime
import json
import pathlib
import shutil
import sqlite3
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
QA = ROOT / "qq-agent"
LOGS = QA / "logs"
YUNO_DATA = ROOT / "yuno" / "data" / "persona-amy"
BOT = YUNO_DATA / "bot.db"
INSIGHT = YUNO_DATA / "insight.db"

SCOPE_TABLES = [
    "memories", "memory_attrs", "memory_meta", "events", "memory_history",
    "memory_trace", "relationships", "entities", "topics", "vec_index", "bm25_docs",
]
INSIGHT_TABLES = ["analysis_runs", "person_attributes", "relation_evidence",
                  "relation_edges", "interaction_stats"]


def load_json(p, default):
    try:
        return json.loads(pathlib.Path(p).read_text(encoding="utf-8-sig"))
    except Exception:
        return default


def has_col(cur, table, col):
    try:
        return col in [c[1] for c in cur.execute(f"pragma table_info({table})")]
    except Exception:
        return False


def counts_bot(cur, scopes):
    out = {}
    mark = ",".join("?" * len(scopes))
    for t in SCOPE_TABLES:
        try:
            out[t] = cur.execute(f"select count(*) from {t} where scope in ({mark})", scopes).fetchone()[0]
        except Exception as e:
            out[t] = f"ERR {e}"
    return out


def counts_insight(cur, gid):
    out = {}
    for t in INSIGHT_TABLES:
        try:
            out[t] = cur.execute(f"select count(*) from {t} where group_id=?", (gid,)).fetchone()[0]
        except Exception as e:
            out[t] = f"ERR {e}"
    return out


def backup(chat, gid, messages_path, ledger_path, card_dir):
    ts = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    dst = YUNO_DATA / "backups" / f"purge-{chat}-{ts}"
    dst.mkdir(parents=True, exist_ok=True)
    # sqlite 在线备份（WAL 安全）
    for src, name in ((BOT, "bot.db"), (INSIGHT, "insight.db")):
        if src.exists():
            s = sqlite3.connect(str(src))
            d = sqlite3.connect(str(dst / name))
            with d:
                s.backup(d)
            d.close()
            s.close()
    for src, name in ((messages_path, "messages.jsonl"), (ledger_path, "ocr-ledger.jsonl")):
        if src.exists():
            shutil.copy2(src, dst / name)
    if card_dir.exists():
        shutil.copytree(card_dir, dst / card_dir.name)
    return dst


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--chat", default="")
    ap.add_argument("--group-id", type=int, default=0)
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    scope_map = load_json(QA / "data" / "scope-map.json", {})
    chat = args.chat or next(iter(scope_map), "")
    gid = args.group_id or (scope_map.get(chat) or {}).get("id", 0)
    if not gid:
        print(f"找不到会话「{chat}」的群号（data/scope-map.json）", file=sys.stderr)
        return 2
    scopes = [f"group:{gid}", f"group_all:{gid}"]

    messages_path = LOGS / "messages.jsonl"
    ledger_path = LOGS / "ocr-ledger.jsonl"
    card_dir = YUNO_DATA / "profiles" / f"group_{gid}"

    con = sqlite3.connect(str(BOT))
    cur = con.cursor()
    bot_counts = counts_bot(cur, scopes)

    ins = sqlite3.connect(str(INSIGHT)) if INSIGHT.exists() else None
    ins_counts = counts_insight(ins.cursor(), gid) if ins else {}

    lines = messages_path.read_text(encoding="utf-8").splitlines() if messages_path.exists() else []
    keep, drop = [], []
    for ln in lines:
        try:
            obj = json.loads(ln)
            (drop if obj.get("chat") == chat else keep).append(ln)
        except Exception:
            keep.append(ln)
    ledger = []
    if ledger_path.exists():
        for x in ledger_path.read_text(encoding="utf-8-sig").splitlines():
            if x.strip():
                try:
                    ledger.append(json.loads(x))
                except Exception:
                    pass
    ledger_hit = [e for e in ledger if e.get("chat") == chat]
    cards = list(card_dir.glob("*.md")) if card_dir.exists() else []
    pending_path = LOGS / "amy-pending.json"
    pending_chat = (load_json(pending_path, {}) or {}).get("chat") if pending_path.exists() else None

    print(f"== 目标：会话「{chat}」 group {gid}　scopes={scopes}")
    print(f"-- bot.db")
    for t, n in bot_counts.items():
        print(f"   {t:16s} {n}")
    if ins_counts:
        print(f"-- insight.db")
        for t, n in ins_counts.items():
            print(f"   {t:16s} {n}")
    print(f"-- 文件")
    print(f"   messages.jsonl   保留 {len(keep)} 行 / 删除 {len(drop)} 行")
    print(f"   ocr-ledger       共 {len(ledger)} 条 / 属本群 {len(ledger_hit)} 条（将按剩余原文重建）")
    print(f"   印象卡             {len(cards)} 张（{card_dir}）")
    print(f"   pending           {pending_chat if pending_chat else '（无）'}")

    if not args.apply:
        print("\n[dry-run] 没有改动任何东西。加 --apply 执行（会自动先备份）。")
        con.close()
        if ins:
            ins.close()
        return 0

    print("\n== 备份")
    dst = backup(chat, gid, messages_path, ledger_path, card_dir)
    print("   ", dst)

    print("== 删 bot.db 行")
    mark = ",".join("?" * len(scopes))
    total = 0
    with con:
        for t in SCOPE_TABLES:
            if not has_col(cur, t, "scope"):
                print(f"   {t:16s} 跳过（无 scope 列）")
                continue
            n = cur.execute(f"delete from {t} where scope in ({mark})", scopes).rowcount
            total += max(0, n)
            print(f"   {t:16s} -{n}")
        try:
            cur.execute("insert into memories_fts(memories_fts) values('rebuild')")
            print("   FTS 已重建")
        except Exception as e:
            print("   FTS 重建跳过：", e)
        try:
            cur.execute("delete from vec_centroids")
            print("   vec_centroids 已清空（稍后重建索引）")
        except Exception as e:
            print("   vec_centroids 清理失败：", e)
    con.close()
    print(f"   共删 {total} 行")

    if ins:
        print("== 删 insight.db 行")
        ic = ins.cursor()
        with ins:
            for t in INSIGHT_TABLES:
                try:
                    n = ic.execute(f"delete from {t} where group_id=?", (gid,)).rowcount
                    print(f"   {t:20s} -{n}")
                except Exception as e:
                    print(f"   {t:20s} 跳过：{e}")
        ins.close()

    print("== 重写原文层")
    messages_path.write_text("\n".join(keep) + ("\n" if keep else ""), encoding="utf-8")
    print(f"   messages.jsonl → {len(keep)} 行（原 {len(lines)}）")

    # 从剩余原文重建 OCR 账本（行号连续，img 按 [图片|a／b] 拆分）
    rebuilt = []
    for i, ln in enumerate(keep, start=1):
        try:
            obj = json.loads(ln)
        except Exception:
            continue
        text = obj.get("Text") or ""
        if not text.startswith("[图片|") or not text.endswith("]"):
            continue
        parts = text[len("[图片|"):-1].split("／")
        for img, part in enumerate(parts, start=1):
            rebuilt.append({
                "line": i, "img": img, "chat": obj.get("chat", ""), "ts": obj.get("ts", 0),
                "sender": obj.get("Sender", ""), "uid": obj.get("uid", 0),
                "via": "direct", "read_by": "purge-rebuild",
                "read_at": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                "text": part or "无文字",
            })
    ledger_path.write_text("".join(json.dumps(e, ensure_ascii=False) + "\n" for e in rebuilt), encoding="utf-8")
    print(f"   ocr-ledger → {len(rebuilt)} 条（原 {len(ledger)}）")

    if cards:
        card_dir.mkdir(parents=True, exist_ok=True)
        for p in card_dir.glob("*.md"):
            p.unlink()
        print(f"   印象卡已清空：{card_dir}")

    if pending_chat == chat and pending_path.exists():
        pending_path.unlink()
        print("   pending 已删除（属于本群）")

    print("\n== 完成。下一步：重建向量索引 + 重启 daemon（行号基线变了）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
