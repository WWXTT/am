"""中文梗语料库：灌入向量库 + 查询。

语料（qq-agent\\data\\meme-corpus\\*.json）：目前是 CHIME（1458 条中文网络梗，带含义/出处/例句/类型，
EMNLP 2025，https://github.com/yuboxie/chime）。以后加别的语料只要同样格式丢进目录即可。

用法（仓库根）：
  yuno\\.venv\\Scripts\\python.exe qq-agent\\tools\\meme-corpus.py build          # 建/重建索引（会调 bge 模型，约 1-3 分钟）
  ... search "你这背景太假了" [-k 5]                                            # 向量检索
  ... exact "yyds"                                                              # 词条精确/前缀匹配
  ... stats                                                                     # 看看库里有什么

索引：qq-agent\\data\\meme-index.db（sqlite；vec 存 float32 字节，粗暴全量余弦，1458 条毫秒级）。
embedding 模型与 Yuno 共用：yuno\\data\\bge-large-zh-v1.5（1024 维，CPU）。
"""

import argparse
import json
import pathlib
import sqlite3
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
CORPUS_DIR = ROOT / "qq-agent" / "data" / "meme-corpus"
DB = ROOT / "qq-agent" / "data" / "meme-index.db"
MODEL = ROOT / "yuno" / "data" / "bge-large-zh-v1.5"

SCHEMA = """
create table if not exists memes (
  id integer primary key,
  term text not null,
  meaning text,
  origin text,
  examples text,
  type_cn text,
  profanity integer default 0,
  offense integer default 0,
  source text,
  vec blob
);
create index if not exists idx_memes_term on memes(term);
create table if not exists meta (k text primary key, v text);
"""

_model = None


def model():
    global _model
    if _model is None:
        from sentence_transformers import SentenceTransformer
        _model = SentenceTransformer(str(MODEL), device="cpu")
    return _model


def iter_corpus():
    """把目录里所有 json 归一成记录。支持 CHIME 的 list[dict] 和通用 jsonl。"""
    for p in sorted(CORPUS_DIR.glob("*.json")):
        try:
            data = json.loads(p.read_text(encoding="utf-8-sig"))
        except Exception as e:
            print(f"[skip] {p.name}: {e}", file=sys.stderr)
            continue
        rows = data if isinstance(data, list) else data.get("data") or []
        for r in rows:
            if not isinstance(r, dict):
                continue
            term = (r.get("meme") or r.get("term") or r.get("title") or "").strip()
            if not term:
                continue
            yield {
                "term": term,
                "meaning": (r.get("meaning") or r.get("explanation") or "").strip(),
                "origin": (r.get("origin") or "").strip(),
                "examples": r.get("examples") or [],
                "type_cn": (r.get("type_cn") or r.get("type") or "").strip(),
                "profanity": int(bool(r.get("profanity"))),
                "offense": int(bool(r.get("offense"))),
                "source": r.get("source") or p.stem,
            }


def cmd_build(args):
    rows = list(iter_corpus())
    if not rows:
        print(f"[build] {CORPUS_DIR} 里没有可用语料", file=sys.stderr)
        return 2
    # 同词条去重（先到先得）
    seen, uniq = set(), []
    for r in rows:
        key = r["term"].lower()
        if key in seen:
            continue
        seen.add(key)
        uniq.append(r)

    con = sqlite3.connect(DB)
    cur = con.cursor()
    cur.executescript(SCHEMA)

    old = {}
    if not args.full:
        for tid, term, vec in cur.execute("select id,term,vec from memes"):
            old[term.lower()] = (tid, vec)
        if old:
            uniq = [r for r in uniq if r["term"].lower() not in old]
            print(f"[build] 增量：已有 {len(old)} 条，只编码新增 {len(uniq)} 条（--full 可整库重建）", flush=True)

    if uniq:
        texts = [f"{r['term']}。{r['meaning'][:200]}" for r in uniq]
        print(f"[build] 开始编码 {len(uniq)} 条…", flush=True)
        vecs = model().encode(texts, batch_size=32, normalize_embeddings=True,
                              show_progress_bar=True, convert_to_numpy=True)
        for r, v in zip(uniq, vecs):
            cur.execute(
                "insert into memes(term,meaning,origin,examples,type_cn,profanity,offense,source,vec)"
                " values(?,?,?,?,?,?,?,?,?)",
                (r["term"], r["meaning"], r["origin"], json.dumps(r["examples"], ensure_ascii=False),
                 r["type_cn"], r["profanity"], r["offense"], r["source"], v.astype("float32").tobytes()),
            )
        dim = vecs.shape[1]
    else:
        dim = int((cur.execute("select v from meta where k='dim'").fetchone() or ["0"])[0])

    total = cur.execute("select count(*) from memes").fetchone()[0]
    cur.execute("insert or replace into meta(k,v) values('dim',?)", (str(dim),))
    cur.execute("insert or replace into meta(k,v) values('built_at',datetime('now','localtime'))")
    cur.execute("insert or replace into meta(k,v) values('count',?)", (str(total),))
    con.commit()
    con.close()
    print(f"[build] 写入 {DB}：共 {total} 条（本次新增 {len(uniq)}），dim={dim}")
    return 0


def _load_all():
    import numpy as np
    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
    cur = con.cursor()
    rows = cur.execute("select id,term,meaning,origin,examples,type_cn,profanity,offense,source,vec from memes").fetchall()
    con.close()
    if not rows:
        return [], None
    dim = len(rows[0][9]) // 4
    mat = np.frombuffer(b"".join(r[9] for r in rows), dtype="float32").reshape(len(rows), dim)
    return rows, mat


def cmd_search(args):
    import numpy as np
    rows, mat = _load_all()
    if not rows:
        print("索引为空：先跑 build", file=sys.stderr)
        return 2
    q = model().encode([args.query], normalize_embeddings=True, convert_to_numpy=True)[0]
    sims = mat @ q
    order = np.argsort(-sims)[: args.k]
    print(f"# 梗语料检索：{args.query}（top {args.k} / 共 {len(rows)} 条）\n")
    for i in order:
        r = rows[i]
        print(f"## {r[1]}　（相似度 {sims[i]:.3f}｜{r[5] or '未分类'}｜{r[8]}）")
        print(f"含义：{r[2]}")
        if r[3]:
            print(f"出处：{r[3][:200]}")
        ex = json.loads(r[4] or "[]")
        if ex:
            print("例句：" + " ｜ ".join(ex[:2]))
        if r[6] or r[7]:
            print(f"⚠{'含脏话 ' if r[6] else ''}{'带冒犯性' if r[7] else ''}")
        print()
    return 0


def cmd_exact(args):
    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
    cur = con.cursor()
    rows = cur.execute(
        "select term,meaning,origin,examples,type_cn,source from memes where term=? or term like ?",
        (args.term, args.term + "%"),
    ).fetchall()
    con.close()
    if not rows:
        print(f"（库里没有「{args.term}」的精确/前缀匹配）")
        return 1
    for term, meaning, origin, examples, t, src in rows:
        print(f"## {term}（{t or '未分类'}｜{src}）")
        print(f"含义：{meaning}")
        if origin:
            print(f"出处：{origin[:300]}")
        print()
    return 0


def cmd_stats(args):
    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
    cur = con.cursor()
    meta = dict(cur.execute("select k,v from meta").fetchall())
    total = cur.execute("select count(*) from memes").fetchone()[0]
    types = cur.execute("select type_cn,count(*) from memes group by type_cn order by 2 desc").fetchall()
    srcs = cur.execute("select source,count(*) from memes group by source").fetchall()
    con.close()
    print(f"库：{DB}")
    print(f"条数：{total}　dim：{meta.get('dim')}　建库：{meta.get('built_at')}")
    print("来源：" + "，".join(f"{s} {n}" for s, n in srcs))
    print("类型：" + "，".join(f"{t or '未分类'} {n}" for t, n in types[:12]))
    return 0


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("build")
    p.add_argument("--full", action="store_true", help="整库重建（默认增量：只编码新词条）")
    p.set_defaults(func=cmd_build)
    p = sub.add_parser("search")
    p.add_argument("query")
    p.add_argument("-k", type=int, default=5)
    p.set_defaults(func=cmd_search)
    p = sub.add_parser("exact")
    p.add_argument("term")
    p.set_defaults(func=cmd_exact)
    sub.add_parser("stats").set_defaults(func=cmd_stats)
    args = ap.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
