"""从中文维基 / 萌娘百科补中文梗语料。

用法（仓库根）：
  yuno\\.venv\\Scripts\\python.exe qq-agent\\tools\\fetch-meme-wiki.py wikipedia-list
  ... moegirl --terms "扶她,夺笋,栓Q"        # 按词条名抓萌娘百科单页（HTML，无批量 API）
  ... moegirl --limit 200                   # 用现有语料里的词去萌娘百科试抓（404 自动跳过）

输出：qq-agent\\data\\meme-corpus\\wikipedia-memes.json / moegirl-<md5前8>.json
格式与 CHIME 对齐（term/meaning/origin/examples/type_cn/source），meme-corpus.py build 会自动吃进去。

说明：
- 中文维基 API 可用（CC BY-SA），「中国大陆网络用语列表」是一页结构化清单，逐行 `词：解释`。
- 萌娘百科 **匿名 API 被禁**（action-notallowed），但单页 HTML 可读（CC BY-NC-SA 3.0 CN）；
  没有批量枚举，只能按词条名逐个试抓，404 即不存在。
- 只做只读 GET，带 UA、限速；抓下来的内容保留 source 字段以便署名。
"""

import argparse
import hashlib
import json
import pathlib
import re
import sys
import time
import urllib.parse
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT_DIR = ROOT / "qq-agent" / "data" / "meme-corpus"
UA = "AmyMemeBot/0.1 (personal research; +https://zh.moegirl.org.cn / https://zh.wikipedia.org)"

WIKI_API = "https://zh.wikipedia.org/w/api.php"

SECTION_TYPE = {
    "英文原字或簡寫": "缩写", "英文原字或简写": "缩写",
    "漢語拼音縮寫": "缩写", "汉语拼音缩写": "缩写",
    "諧音": "谐音", "谐音": "谐音",
    "數字": "数字", "数字": "数字",
    "縮略": "缩写", "缩略": "缩写",
    "成句": "成句", "俗語": "俗语", "俗语": "俗语",
    "顏文字": "符号", "颜文字": "符号",
}


def http_get(url, timeout=40):
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept-Language": "zh-CN,zh;q=0.9"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status, r.read().decode("utf-8", "replace")


def wiki_api(params):
    params = {**params, "format": "json", "formatversion": "2"}
    st, body = http_get(WIKI_API + "?" + urllib.parse.urlencode(params))
    return json.loads(body)


def fetch_wikipedia_list():
    d = wiki_api({"action": "query", "prop": "extracts", "explaintext": "1",
                  "titles": "中国大陆网络用语列表"})
    page = (d.get("query", {}).get("pages") or [{}])[0]
    text = page.get("extract") or ""
    if not text:
        print("取不到维基列表页", file=sys.stderr)
        return []

    entries = []
    seen = set()
    section = ""
    for raw in text.splitlines():
        line = raw.strip()
        if not line:
            continue
        m = re.match(r"^=+\s*(.+?)\s*=+$", line)
        if m:
            section = m.group(1)
            continue
        if len(line) > 400:
            continue
        mm = re.match(r"^([^\s：:，,。]{1,12})\s*[：:]\s*(.{6,})$", line)
        if not mm:
            continue
        term, meaning = mm.group(1).strip(), mm.group(2).strip()
        if term in seen or any(c in term for c in "，。；、"):
            continue
        seen.add(term)
        entries.append({
            "term": term,
            "meaning": meaning,
            "origin": "",
            "examples": [],
            "type_cn": SECTION_TYPE.get(section, "网络用语"),
            "profanity": 0,
            "offense": 0,
            "source": "wikipedia-网络用语列表",
        })
    return entries


LABELS = {"用语名称", "其他表述", "用语出处", "相关条目", "基本信息", "用语", "别名", "出处",
          "编辑本条目", "萌娘百科", "目录", "注释", "外部链接", "参见", "参考资料", "导航"}

BOILER = ("萌娘百科", "Moegirl", "编辑本条目", "条目编辑规范", "维基入门", "祝您", "This is the greeting",
          "欢迎您", "感谢您", "协助", "编辑前请阅读", "查找相关资料", "本站", "贡献", "本条目")


def _moegirl_text(html):
    """取正文段落：切出 mw-content-text，抓 <p>，丢掉站点样板与纯外文段，取前两段中文。"""
    i = html.find('id="mw-content-text"')
    if i > 0:
        html = html[i:]
    j = html.find('id="catlinks"')
    if j > 0:
        html = html[:j]
    picked = []
    for m in re.finditer(r"<p\b[^>]*>([\s\S]*?)</p>", html):
        seg = re.sub(r"<[^>]+>", "", m.group(1))
        for a, b in (("&nbsp;", " "), ("&amp;", "&"), ("&quot;", '"'), ("&#39;", "'"),
                     ("&lt;", "<"), ("&gt;", ">"), ("&ldquo;", "“"), ("&rdquo;", "”")):
            seg = seg.replace(a, b)
        seg = re.sub(r"\s+", " ", seg).strip()
        if len(seg) < 20:
            continue
        if any(b in seg for b in BOILER):
            continue
        if len(re.findall(r"[\u4e00-\u9fff]", seg)) < 5:   # 纯外文段（镜像翻译）跳过
            continue
        picked.append(seg)
        if len("".join(picked)) >= 320:
            break
    out = "".join(p if p.endswith(("。", "！", "？")) else p + "。" for p in picked[:2])
    return out[:420].strip()


def fetch_moegirl(terms, sleep=0.6):
    out = []
    for i, term in enumerate(terms, 1):
        url = "https://zh.moegirl.org.cn/" + urllib.parse.quote(term)
        try:
            st, html = http_get(url)
        except Exception as e:
            code = getattr(e, "code", None)
            if code == 404:
                print(f"[{i}/{len(terms)}] {term}: 无此条目")
            else:
                print(f"[{i}/{len(terms)}] {term}: ERR {e}")
            time.sleep(sleep)
            continue
        text = _moegirl_text(html)
        head = text[:600]
        if len(text) < 80:
            print(f"[{i}/{len(terms)}] {term}: 内容太短，跳过")
            time.sleep(sleep)
            continue
        out.append({
            "term": term,
            "meaning": head,
            "origin": "",
            "examples": [],
            "type_cn": "网络用语",
            "profanity": 0,
            "offense": 0,
            "source": "moegirl",
            "source_url": url,
        })
        print(f"[{i}/{len(terms)}] {term}: {len(head)} 字")
        time.sleep(sleep)
    return out


def existing_terms():
    terms = []
    p = OUT_DIR / "chime_full.json"
    if p.exists():
        try:
            for r in json.loads(p.read_text(encoding="utf-8-sig")):
                t = (r.get("meme") or "").strip()
                if t:
                    terms.append(t)
        except Exception:
            pass
    return terms


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("source", choices=["wikipedia-list", "moegirl"])
    ap.add_argument("--terms", default="", help="逗号分隔的词条名（moegirl 用）")
    ap.add_argument("--limit", type=int, default=0, help="moegirl：用现有语料词条试抓，限制条数")
    ap.add_argument("--sleep", type=float, default=0.6)
    args = ap.parse_args()
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    if args.source == "wikipedia-list":
        entries = fetch_wikipedia_list()
        out = OUT_DIR / "wikipedia-memes.json"
    else:
        if args.terms:
            terms = [t.strip() for t in args.terms.split(",") if t.strip()]
        else:
            terms = existing_terms()
            if args.limit:
                terms = terms[: args.limit]
        if not terms:
            print("没有词条可抓（用 --terms 或先放一份语料）", file=sys.stderr)
            return 2
        entries = fetch_moegirl(terms, sleep=args.sleep)
        h = hashlib.md5(",".join(sorted(terms)).encode()).hexdigest()[:8]
        out = OUT_DIR / f"moegirl-{h}.json"

    if not entries:
        print("没抓到内容", file=sys.stderr)
        return 1
    out.write_text(json.dumps(entries, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n写入 {out}：{len(entries)} 条")
    return 0


if __name__ == "__main__":
    sys.exit(main())
