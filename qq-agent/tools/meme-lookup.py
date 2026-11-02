"""梗查询：本地人工梗库（群内真实用法）+ 中文梗语料（CHIME / 维基 / 萌娘）向量检索。

用法（仓库根）：
  qq-agent\\meme.cmd "邪恶奶龙"
  yuno\\.venv\\Scripts\\python.exe qq-agent\\tools\\meme-lookup.py "夺笋" -k 3 --min-score 0.55

规则（2026-10-27 定案）：
- 人工库优先：只收**群内实际用法**，不收模型总结式的"词条介绍"。
- 语料库只当线索：相似度低于 --min-score（默认 0.55）一律不展示，宁可说"没查到"，不硬凑。
- 不接 knowthismeme-mcp（中文群梗查不到，已移除）。
"""

import argparse
import json
import pathlib
import sqlite3
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
QA = ROOT / "qq-agent"
CURATED = QA / "data" / "memes.json"
INDEX = QA / "data" / "meme-index.db"


def curated_lookup(term):
    try:
        data = json.loads(CURATED.read_text(encoding="utf-8-sig"))
    except Exception:
        return []
    out = []
    low = term.lower()
    for m in data.get("entries", []):
        names = [m.get("term", "")] + list(m.get("aliases") or [])
        if any(low == str(n).lower() or (n and str(n).lower() in low) for n in names):
            out.append(m)
    return out


def corpus_exact(term):
    if not INDEX.exists():
        return []
    con = sqlite3.connect(f"file:{INDEX}?mode=ro", uri=True)
    rows = con.execute(
        "select term,meaning,origin,examples,type_cn,source from memes where term=?", (term,)
    ).fetchall()
    con.close()
    return rows


def corpus_search(term, k=3, min_score=0.55):
    if not INDEX.exists():
        return []
    try:
        import importlib.util
        spec = importlib.util.spec_from_file_location("meme_corpus", QA / "tools" / "meme-corpus.py")
        mc = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mc)
        import numpy as np
        rows, mat = mc._load_all()
        if not rows:
            return []
        q = mc.model().encode([term], normalize_embeddings=True, convert_to_numpy=True)[0]
        sims = mat @ q
        order = np.argsort(-sims)[: max(k * 3, k)]
        out = []
        for i in order:
            if float(sims[i]) < min_score:
                continue
            out.append((rows[i][1], rows[i][2], rows[i][3], rows[i][4], rows[i][5], rows[i][8], float(sims[i])))
            if len(out) >= k:
                break
        return out
    except Exception as e:
        print(f"[corpus] 检索失败：{e}", file=sys.stderr)
        return []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("term")
    ap.add_argument("-k", type=int, default=3)
    ap.add_argument("--min-score", type=float, default=0.55, help="相近项的最低相似度，低于它不展示")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    curated = [{k: m.get(k) for k in ("term", "meaning", "examples", "scope", "source")}
               for m in curated_lookup(args.term)]
    exact = [{"term": t, "meaning": mi, "origin": o, "examples": json.loads(ex or "[]"),
              "type": ty, "source": s}
             for t, mi, o, ex, ty, s in corpus_exact(args.term)]
    near = [] if exact else [
        {"term": t, "meaning": mi, "origin": (o or "")[:160], "examples": json.loads(ex or "[]")[:2],
         "type": ty, "source": s, "score": round(sc, 3)}
        for t, mi, o, ex, ty, s, sc in corpus_search(args.term, args.k, args.min_score)
    ]

    if args.json:
        print(json.dumps({"term": args.term, "curated": curated, "corpus_exact": exact,
                          "corpus_near": near}, ensure_ascii=False, indent=2))
        return 0

    print(f"# 梗查询：{args.term}\n")
    if curated:
        print("## 群内用法（人工核对，优先级最高）")
        for m in curated:
            print(f"- **{m['term']}**：{m['meaning']}")
            if m.get("examples"):
                print(f"  例：{'；'.join(m['examples'])}")
            if m.get("source"):
                print(f"  来源：{m['source']}")
        print()
    if exact:
        print("## 中文梗语料（精确命中）")
        for r in exact:
            print(f"- **{r['term']}**（{r['type']}｜{r['source']}）：{r['meaning'][:300]}")
        print()
    elif near:
        print(f"## 语料相近（≥{args.min_score}，仅供参考）")
        for r in near:
            print(f"- **{r['term']}**（{r['type']}｜相似度 {r['score']}）：{r['meaning'][:160]}")
        print()
    if not (curated or exact or near):
        print("（没查到。别拿相近项硬凑：按字面接，或者先问清楚这个词在群里怎么用。）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
