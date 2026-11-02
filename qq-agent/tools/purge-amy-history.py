#!/usr/bin/env python
"""清理 Yuno 记忆库里与 Amy 相关的内容 + 旧聊天语料（2026-10-26 用户定案）。

规则（用户口径："数据库中存 Amy 相关的内容都删掉，只保留其余有价值的历史事实"）：
  1. 原始聊天历史全清：memory_trace / query_log / feedback_log。
  2. 与 Amy 相关的行全删：scope='ai'，或文本命中 Amy/艾米/1000000001。
     覆盖 memories / memory_attrs / memory_meta / events / topics / topic_params / memory_history。
  3. 由被删事实派生的索引（fts / bm25 / vec）同步清掉；fts 用剩余 memories 重建。
  4. 人物印象卡（profiles/*.md）：删掉「与 Amy 的关系」段与任何提到 Amy 的行；
     删掉 .changelog.json（历史版本）与测试群/测试号卡；其余人物档案保留。
  5. insight.db：删掉 Amy 本人（1000000001）参与的人物属性/关系证据/关系边/互动统计，以及文本提到 Amy 的行。
  6. kv 的 memory 命名空间（living_items/lastmsg 等旧角色扮演/聊天状态）清掉；roster 保留。

用法：
  python qq-agent/tools/purge-amy-history.py            # 只统计，不写
  python qq-agent/tools/purge-amy-history.py --apply    # 执行（会 VACUUM）
"""
import argparse
import json
import os
import re
import sqlite3
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # 仓库根
DATA = os.path.join(ROOT, "yuno", "data", "persona-amy")
BOT_DB = os.path.join(DATA, "bot.db")
INSIGHT_DB = os.path.join(DATA, "insight.db")
PROFILES = os.path.join(DATA, "profiles")

AMY_RX = re.compile(r"Amy|艾米|1000000001", re.IGNORECASE)
AMY_QQ = 1000000001
TEST_QQS = {"999000", "12345678", "999999"}
TEST_GROUPS = {"999999"}

BOT_MEM_TABLES = ["memories", "memory_attrs", "memory_meta", "events", "topics", "memory_history"]


def amy_where_for(table, cols):
    parts = []
    for c in cols:
        parts.append(f"COALESCE({c},'') LIKE '%Amy%' OR COALESCE({c},'') LIKE '%艾米%' OR COALESCE({c},'') LIKE '%1000000001%'")
    return " OR ".join(parts)


def table_cols(con, table):
    return [r[1] for r in con.execute(f"pragma table_info({table})")]


def count(con, sql, args=()):
    try:
        return con.execute(sql, args).fetchone()[0]
    except Exception as e:
        return f"ERR {e}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="真正写入；默认只统计")
    args = ap.parse_args()
    apply = args.apply
    if not os.path.exists(BOT_DB):
        sys.exit(f"找不到 {BOT_DB}")

    con = sqlite3.connect(BOT_DB)
    con.row_factory = sqlite3.Row
    report = {}

    # ---------- 1) 原始聊天历史全清 ----------
    for t in ["memory_trace", "query_log", "feedback_log"]:
        n = count(con, f"select count(*) from {t}")
        report[t] = n
        if apply and isinstance(n, int) and n:
            con.execute(f"delete from {t}")

    # ---------- 2) Amy 相关行 ----------
    amy_rows = {}
    for t in BOT_MEM_TABLES:
        cols = table_cols(con, t)
        text_cols = [c for c in cols if c in ("fact", "value", "title", "content", "topic", "attr")]
        where = ["scope = 'ai'"]
        if text_cols:
            where.append(amy_where_for(t, text_cols))
        w = " OR ".join(where)
        n = count(con, f"select count(*) from {t} where {w}")
        amy_rows[t] = n
        if apply and isinstance(n, int) and n:
            con.execute(f"delete from {t} where {w}")
    # topic_params 跟着被删的 topics 走
    tp = count(con, "select count(*) from topic_params where topic_id not in (select id from topics)")
    amy_rows["topic_params(orphan)"] = tp
    if apply and isinstance(tp, int) and tp:
        con.execute("delete from topic_params where topic_id not in (select id from topics)")

    # 记住被删 facts，供索引/派生表同步
    deleted_facts = set()
    if apply:
        for r in con.execute("select scope,key,fact from vec_index"):
            pass  # 占位：索引在下面按 Amy 规则删
    else:
        for r in con.execute("select scope,key,fact from memories where scope='ai' or fact like '%Amy%' or fact like '%艾米%' or fact like '%1000000001%'"):
            deleted_facts.add((r[0], r[1], r[2]))

    # ---------- 3) 派生素引 ----------
    for t in ["bm25_docs", "bm25_terms", "vec_index"]:
        cols = table_cols(con, t)
        if "fact" not in cols:
            continue
        w = "fact LIKE '%Amy%' OR fact LIKE '%艾米%' OR fact LIKE '%1000000001%'"
        if "scope" in cols:
            w = "scope='ai' OR " + w
        n = count(con, f"select count(*) from {t} where {w}")
        amy_rows[f"{t}(amy)"] = n
        if apply and isinstance(n, int) and n:
            con.execute(f"delete from {t} where {w}")
    # 删掉已不存在的 memories 对应索引行（保一致性）
    for t in ["bm25_docs", "bm25_terms", "vec_index"]:
        if apply:
            con.execute(f"delete from {t} where fact not in (select fact from memories)")
    # fts 直接按剩余 memories 重建
    if apply:
        try:
            con.execute("delete from memories_fts")
            con.execute("insert into memories_fts(scope,key,fact) select scope,key,fact from memories")
        except Exception as e:
            print(f"[warn] fts rebuild: {e}")

    # ---------- 4) kv：只清 memory 命名空间 ----------
    kv = count(con, "select count(*) from kv where namespace='memory'")
    report["kv(memory)"] = kv
    if apply and isinstance(kv, int) and kv:
        con.execute("delete from kv where namespace='memory'")

    # ---------- 5) event_relations 孤儿 ----------
    er = count(con, "select count(*) from event_relations where src not in (select id from events) or dst not in (select id from events)")
    report["event_relations(orphan)"] = er
    if apply and isinstance(er, int) and er:
        con.execute("delete from event_relations where src not in (select id from events) or dst not in (select id from events)")

    if apply:
        con.commit()
        con.execute("vacuum")
    con.close()

    # ---------- 6) 人物印象卡 ----------
    card_stats = {"deleted_test": 0, "stripped": 0, "kept": 0, "changelogs_deleted": 0}
    if os.path.isdir(PROFILES):
        for entry in os.listdir(PROFILES):
            gdir = os.path.join(PROFILES, entry)
            if not os.path.isdir(gdir):
                continue
            gid = entry.split("_", 1)[-1]
            if gid in TEST_GROUPS:
                card_stats["deleted_test"] += len(os.listdir(gdir))
                if apply:
                    for f in os.listdir(gdir):
                        os.remove(os.path.join(gdir, f))
                    os.rmdir(gdir)
                continue
            for name in os.listdir(gdir):
                path = os.path.join(gdir, name)
                if name.endswith(".changelog.json"):
                    card_stats["changelogs_deleted"] += 1
                    if apply:
                        os.remove(path)
                    continue
                if not name.endswith(".md"):
                    continue
                qq = name[:-3]
                if qq in TEST_QQS:
                    card_stats["deleted_test"] += 1
                    if apply:
                        os.remove(path)
                    continue
                text = open(path, encoding="utf-8").read()
                if not AMY_RX.search(text):
                    card_stats["kept"] += 1
                    continue
                # 去掉「## 与 Amy 的关系」整段
                text2 = re.sub(r"\n##\s*与\s*Amy\s*的关系\s*\n.*?(?=\n##\s|\Z)", "\n", text, flags=re.S)
                # 其余段落里提到 Amy 的行删掉
                lines = [ln for ln in text2.splitlines() if not AMY_RX.search(ln)]
                text2 = "\n".join(lines).rstrip() + "\n"
                card_stats["stripped"] += 1
                if apply:
                    open(path, "w", encoding="utf-8").write(text2)

    # ---------- 7) insight.db ----------
    insight = {}
    if os.path.exists(INSIGHT_DB):
        ic = sqlite3.connect(INSIGHT_DB)
        for t, where in [
            ("person_attributes", f"qq={AMY_QQ} OR COALESCE(value,'') LIKE '%Amy%' OR COALESCE(evidence,'') LIKE '%Amy%'"),
            ("relation_evidence", f"qq_a={AMY_QQ} OR qq_b={AMY_QQ} OR COALESCE(content,'') LIKE '%Amy%' OR COALESCE(evidence,'') LIKE '%Amy%'"),
            ("relation_edges", f"qq_a={AMY_QQ} OR qq_b={AMY_QQ}"),
            ("interaction_stats", f"qq_a={AMY_QQ} OR qq_b={AMY_QQ}"),
        ]:
            try:
                n = ic.execute(f"select count(*) from {t} where {where}").fetchone()[0]
            except Exception as e:
                n = f"ERR {e}"
            insight[t] = n
            if apply and isinstance(n, int) and n:
                ic.execute(f"delete from {t} where {where}")
        if apply:
            ic.commit()
            ic.execute("vacuum")
        ic.close()

    print("== 删除统计 ==")
    print(json.dumps({"raw_history": report, "amy_rows": amy_rows, "cards": card_stats, "insight": insight},
                     ensure_ascii=False, indent=2))
    print("\n== 剩余 ==")
    con = sqlite3.connect(f"file:{BOT_DB}?mode=ro", uri=True)
    for t in BOT_MEM_TABLES + ["memory_trace", "memory_meta", "bm25_docs", "vec_index", "kv"]:
        try:
            print(f"{t}: {con.execute(f'select count(*) from {t}').fetchone()[0]}")
        except Exception as e:
            print(t, "ERR", e)
    con.close()
    if not apply:
        print("\n(dry-run：未写入；加 --apply 执行)")


if __name__ == "__main__":
    main()
