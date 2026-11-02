"""生成「群像包」：把人设卡、群级记忆、梗库、现场状态在开局一次性拼成一份文件，
值班回合直接读它，不再每回合调 duty card / duty context。

用法（仓库根）：
  yuno\\.venv\\Scripts\\python.exe qq-agent\\tools\\build-group-pack.py          # 默认 scope-map 第一个会话
  ... --chat 潮汐 [--out qq-agent\\logs\\group-pack.md] [--per-person 700] [--memories 40]

数据源（全部只读，不写任何库）：
  qq-agent\\data\\scope-map.json / roster-map.json / memes.json
  qq-agent\\logs\\amy-scene-state.json
  yuno\\data\\persona-amy\\profiles\\<scope>\\<uid>.md     印象卡（Yuno 档案层）
  yuno\\data\\persona-amy\\bot.db                         群级 memories（只读连接）
"""

import argparse
import datetime
import json
import pathlib
import re
import sqlite3
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]          # 仓库根
QA = ROOT / "qq-agent"
DATA = QA / "data"
YUNO_DATA = ROOT / "yuno" / "data" / "persona-amy"
DB = YUNO_DATA / "bot.db"


def load_json(path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception:
        return default


def safe_scope(scope):
    return re.sub(r"[^0-9A-Za-z_.-]", "_", scope or "x")


def read_card(scope, uid):
    p = YUNO_DATA / "profiles" / safe_scope(scope) / f"{uid}.md"
    if not p.exists():
        return ""
    text = p.read_text(encoding="utf-8")
    m = re.match(r"^---\r?\n(.*?)\r?\n---\r?\n?(.*)$", text, re.S)
    body = m.group(2) if m else text
    meta = {}
    if m:
        for ln in m.group(1).splitlines():
            if ":" in ln:
                k, _, v = ln.partition(":")
                meta[k.strip().strip('"')] = v.strip().strip('"')
    return meta, body.strip()


def section(body, name):
    """取 ## name 到下一个 ## 之间的内容。"""
    m = re.search(rf"^##\s*{re.escape(name)}\s*$(.*?)(?=^##\s|\Z)", body, re.S | re.M)
    return m.group(1).strip() if m else ""


def group_memories(scopes, limit):
    if not DB.exists():
        return []
    try:
        con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
        cur = con.cursor()
        mark = ",".join("?" * len(scopes))
        rows = cur.execute(
            f"select scope,key,fact,confidence from memories where scope in ({mark}) "
            f"order by rowid desc limit ?", (*scopes, limit),
        ).fetchall()
        con.close()
        return rows
    except Exception as e:
        print(f"[warn] 读记忆失败：{e}", file=sys.stderr)
        return []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--chat", default="")
    ap.add_argument("--out", default=str(QA / "logs" / "group-pack.md"))
    ap.add_argument("--per-person", type=int, default=700, help="每人卡正文最多字符")
    ap.add_argument("--memories", type=int, default=40, help="群级记忆条数")
    args = ap.parse_args()

    scope_map = load_json(DATA / "scope-map.json", {})
    roster = load_json(DATA / "roster-map.json", {})
    memes = load_json(DATA / "memes.json", {}).get("entries", [])
    scene_state = load_json(QA / "logs" / "amy-scene-state.json", {})

    chat = args.chat or (next(iter(scope_map), "") if scope_map else "")
    info = scope_map.get(chat) or {}
    gid = info.get("id", 0)
    scope = f"group:{gid}" if gid else ""
    scopes = [scope, f"group_all:{gid}"] if gid else []

    # uid -> 昵称们（同一人可能有多个曾用名）
    names_by_uid = {}
    for name, uid in (roster.get(scope) or {}).items():
        names_by_uid.setdefault(uid, []).append(name.strip())

    # 卡片目录里实际有哪些人（以文件为准，比 roster 全）
    card_dir = YUNO_DATA / "profiles" / safe_scope(scope)
    card_uids = sorted(int(p.stem) for p in card_dir.glob("*.md") if p.stem.isdigit()) if card_dir.exists() else []
    uids = sorted(set(card_uids) | set(names_by_uid))

    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    out = []
    out.append(f"# 群像包 · {chat}（group {gid}）")
    out.append(f"生成：{now}　成员：{len(uids)} 人（有卡 {len(card_uids)} 张）　范围：{scope} / group_all:{gid}")
    out.append("")
    out.append("> 开局一次性加载用：判断前先查这份；需要最新事实再 `duty context --seq N`。"
               "卡里没有的事不要编，别把别人的卡安到当前对象头上。")
    out.append("")

    # 索引表
    out.append("## 成员索引")
    out.append("")
    out.append("| 昵称（含曾用名） | uid | 卡 | 一句话 |")
    out.append("|---|---|---|---|")
    cards = {}
    for uid in uids:
        got = read_card(scope, uid) if card_dir.exists() else ("", "")
        meta, body = got if isinstance(got, tuple) else ({}, "")
        cards[uid] = (meta, body)
        tag = ""
        if body:
            first = section(body, "基本印象") or section(body, "性格与说话风格")
            first = re.sub(r"\s+", " ", first.splitlines()[0] if first else "")[:60]
            tag = first
        alias = " / ".join(names_by_uid.get(uid, [])) or (meta.get("nickname") or "")
        out.append(f"| {alias or '（未知）'} | {uid} | {'有' if body else '—'} | {tag} |")
    out.append("")

    # 每人卡
    out.append("## 人（按 uid，卡的正文）")
    out.append("")
    for uid in uids:
        meta, body = cards.get(uid, ({}, ""))
        alias = " / ".join(names_by_uid.get(uid, [])) or meta.get("nickname") or "?"
        out.append(f"### {uid}　{alias}")
        if not body:
            out.append("（无印象卡）")
            out.append("")
            continue
        if args.per_person > 0 and len(body) > args.per_person:
            body = body[: args.per_person].rstrip() + "…"
        out.append(body)
        out.append("")

    # 群级记忆
    mem = group_memories(scopes, args.memories) if scopes else []
    out.append(f"## 群级记忆（最近 {len(mem)} 条，scope 级）")
    out.append("")
    if not mem:
        out.append("（无）")
    for sc, key, fact, conf in mem:
        who = f" [key={key}]" if key else ""
        out.append(f"- {fact}{who}　·{sc}")
    out.append("")

    # 梗库
    out.append(f"## 梗库（{len(memes)} 条）")
    out.append("")
    for m in memes:
        out.append(f"- **{m.get('term')}**：{m.get('meaning')}"
                   + (f"　例：{'；'.join(m.get('examples') or [])}" if m.get("examples") else ""))
    out.append("")

    # 现场状态
    st = (scene_state or {}).get(chat) or {}
    out.append("## 现场状态（上一回合留下的）")
    out.append("")
    if st:
        out.append(f"- 话题：{st.get('topic', '')}")
        out.append(f"- 在玩的梗：{st.get('jokes', '')}")
        out.append(f"- 更新时间：{st.get('updated', '')}")
    else:
        out.append("（无）")
    out.append("")

    text = "\n".join(out)
    pathlib.Path(args.out).write_text(text, encoding="utf-8")
    print(f"written {args.out}  {len(text)} chars  ({len(uids)} 人 / {len(mem)} 条群记忆 / {len(memes)} 条梗)")


if __name__ == "__main__":
    main()
