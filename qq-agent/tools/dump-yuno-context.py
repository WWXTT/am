# -*- coding: utf-8 -*-
"""抓 3 份「寄！」群的 Yuno 记忆注入样例（/amy/context 与 /amy/card），写到桌面。"""
import json
import pathlib
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
ENV = ROOT / "yuno" / ".env"
OUT = ROOT / "logs" / "yuno-context-samples.md"

token = ""
for line in ENV.read_text(encoding="utf-8-sig").splitlines():
    if line.strip().startswith("YUNO_API_TOKEN"):
        token = line.split("=", 1)[1].strip().strip('"')
        break

BASE = "http://127.0.0.1:8457"


def post(path, payload):
    req = urllib.request.Request(
        BASE + path,
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=40) as r:
        return json.loads(r.read().decode("utf-8"))


def get(path):
    req = urllib.request.Request(BASE + path, headers={"Authorization": "Bearer " + token})
    with urllib.request.urlopen(req, timeout=40) as r:
        return json.loads(r.read().decode("utf-8"))


GROUP = "group:123456789"
GALL = "group_all:123456789"
scopes = [GROUP, GALL]

samples = [
    ("A. 群主随口一句（无回忆线索）", "你们又在聊什么", 1000000000, 3, 700),
    ("B. 点名 + 回忆线索（触发向量检索）", "你还记得我之前说过骂人要怎么骂吗", 1000000000, 5, 900),
    ("C. 普通群友问事（另一个人）", "上次牛牛说的那个显卡梗是啥来着", 535273458, 5, 900),
]

blocks = []
for title, query, uid, topk, budget in samples:
    try:
        r = post("/amy/context", {
            "query": query, "scopes": scopes, "user_id": uid,
            "top_k": topk, "budget": budget, "min_score": 0.0,
        })
        ctx = r.get("context") or ""
    except Exception as e:
        ctx = f"[请求失败] {e}"
    blocks.append((title, query, uid, topk, budget, ctx))

# 两张卡做对照
cards = []
for uid in (1000000000, 535273458):
    try:
        c = get(f"/amy/card?scope={GROUP}&uid={uid}")
        cards.append((uid, c.get("card") or c.get("context") or json.dumps(c, ensure_ascii=False)))
    except Exception as e:
        cards.append((uid, f"[失败] {e}"))

lines = []
lines.append("# 10 · 「寄！」群（group 123456789）的 Yuno 记忆注入样例")
lines.append("")
lines.append("生成：2026-10-27 00:19　接口：`POST /amy/context`（Bearer）　数据：`yuno\\data\\persona-amy\\bot.db`")
lines.append("说明：`context` 字段就是一整段拼好的纯文本——真值接塞进判断层上下文，Writer 看不到。")
lines.append("")
for title, query, uid, topk, budget, ctx in blocks:
    lines.append(f"## {title}")
    lines.append("")
    lines.append(f"- 请求：`query=\"{query}\"`　`user_id={uid}`　`top_k={topk}`　`budget={budget}`　`scopes=[{GROUP}, {GALL}]`")
    lines.append(f"- 返回长度：{len(ctx)} 字符")
    lines.append("")
    lines.append("```text")
    lines.append(ctx if ctx else "(空)")
    lines.append("```")
    lines.append("")

lines.append("## D. 单人印象卡（/amy/card，现场包 memory.card 的来源）")
lines.append("")
for uid, card in cards:
    lines.append(f"### uid {uid}")
    lines.append("")
    lines.append("```markdown")
    lines.append(card.strip() if card else "(空)")
    lines.append("```")
    lines.append("")

lines.append("## 注")
lines.append("")
lines.append("- `·[vector]` / `·[lexical/vector]` / `·[lexical]` 标的是这条记忆被哪条检索腿命中（向量 / 关键词 / 两者）。")
lines.append("- `·归属QQ…` 是防张冠李戴的归属标注；`⚠` 表示这条事实属于别人，不能安到当前对话者头上。")
lines.append("- 现场包里的 `memory.card` 就是 D 段这张卡；`memory.cards` 是 A/B/C 里 memory 部分（被 `MemoryGate=cue` 门控）。")

OUT.write_text("\n".join(lines), encoding="utf-8")
print("written:", OUT)
for title, query, uid, *_rest, ctx in [(b[0], b[1], b[2], b[3], b[4], b[5]) for b in blocks]:
    print(f"  {title}: {len(ctx)} chars")
