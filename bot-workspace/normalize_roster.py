"""注入完成后规范化花名册（roster kv）。

背景：重注入的消息按历史顺序流经 after_chat，Yuno 的花名册逻辑会把"最后见到
的名字"当成现名——3月的「难得糊涂」在10月的「牛牛」之后处理，方向就反了。
本脚本以 OneBot 群成员名册为权威，把每个 QQ 的现名拨正，其余名字全部归入
aliases，last_seen 取 messages.db 最新消息时间。

用法：python bot-workspace/normalize_roster.py [--dry-run]
前置：bot-workspace/roster_123456789.json（成员名册快照）、napcat-mcp/messages.db
"""
import argparse
import json
import sqlite3
import sys
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MDB = ROOT / "napcat-mcp" / "messages.db"
ROSTER_SNAP = Path(__file__).resolve().parent / "roster_123456789.json"
BOT_DB = ROOT / "yuno" / "data" / "persona-amy" / "bot.db"
GROUP = 123456789


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    members = {m["user_id"]: m for m in json.load(open(ROSTER_SNAP, encoding="utf-8"))["members"]}

    con = sqlite3.connect(MDB)
    rows = con.execute(
        "SELECT user_id, sender_name, MAX(timestamp) FROM messages "
        "WHERE group_id=? GROUP BY user_id, sender_name", (GROUP,)).fetchall()
    con.close()

    seen = {}  # qq -> {name: last_ts}
    for uid, name, ts in rows:
        if not uid or not name:
            continue
        seen.setdefault(uid, {})[name] = ts

    roster = {}
    for uid, names in seen.items():
        m = members.get(uid)
        if m is None:
            # 名册外的QQ（如已退群者）——保留原名，不加干预
            cur, aliases = max(names.items(), key=lambda kv: kv[1])[0], sorted(
                n for n in names if n != max(names.items(), key=lambda kv: kv[1])[0])
            roster[str(uid)] = {"nickname": cur, "aliases": aliases,
                                "last_seen": datetime.fromtimestamp(max(names.values())).isoformat()}
            continue
        cur = m["card"] or m["nickname"]  # 群内展示名：名片优先
        aliases = sorted(n for n in names if n != cur)
        roster[str(uid)] = {"nickname": cur, "aliases": aliases,
                            "last_seen": datetime.fromtimestamp(max(names.values())).isoformat()}

    print(f"花名册共 {len(roster)} 人")
    for uid in sorted(roster, key=lambda x: int(x)):
        r = roster[uid]
        print(f"  {uid}: {r['nickname']!r}  aliases={r['aliases']}")
    if args.dry_run:
        return

    yuno = sqlite3.connect(BOT_DB)
    key = f"group:{GROUP}"
    old = yuno.execute("SELECT value FROM kv WHERE namespace='roster' AND key=?", (key,)).fetchone()
    if old:
        yuno.execute("UPDATE kv SET value=? WHERE namespace='roster' AND key=?",
                     (json.dumps(roster, ensure_ascii=False), key))
    else:
        yuno.execute("INSERT INTO kv(namespace, key, value) VALUES('roster', ?, ?)",
                     (key, json.dumps(roster, ensure_ascii=False)))
    yuno.commit()
    yuno.close()
    print("已写回 yuno kv roster（服务运行中可能缓存，注入结束后重启 Yuno 生效）")


if __name__ == "__main__":
    sys.exit(main())
