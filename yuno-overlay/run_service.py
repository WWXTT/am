"""Amy 记忆服务启动包装（Yuno SDK 服务的本项目定制入口）。

为什么需要本文件而不是直接 `python -m yuno_memory`：
1. plugins/_db.py 在模块导入时读取 YUNO_DB_BACKEND（默认 postgresql），而官方
   加载 .env 的代码（plugins/_shared.py:25）发生在导入 _db 之后——.env 里的
   YUNO_DB_BACKEND 永远不生效。必须在导入任何 yuno 模块之前把 .env 灌入环境。
2. 官方 SDK 的 /memory/ingest 是简化入口（直通 memory.ingest），缺了 Yuno 自己
   bot 回合里的前置管线：表达画像更新、关系引擎、信息增益缓冲合并、群整体记忆。
   本包装额外暴露 /amy/after_chat，直接调用 plugins.memory.after_chat（同一函数，
   完整管线保真 100%），以及 /amy/context（七路检索+预算化注入的成品上下文）。
"""

import os
import time
from datetime import datetime
from pathlib import Path

from dotenv import load_dotenv

ROOT = Path(__file__).resolve().parent

# 关键顺序：先灌环境变量，再导入 yuno 代码（否则 DB 后端选错、密钥丢失）
# override=True：本服务以 yuno/.env 为唯一权威——避免机器级残留环境变量
# （如失效的旧 DEEPSEEK_API_KEY）把 .env 里的正确配置顶掉
load_dotenv(ROOT / ".env", override=True)
os.environ.setdefault("YUNO_DB_BACKEND", "sqlite")
os.environ.setdefault("HF_HOME", str(ROOT / "data" / "hf_cache"))
os.environ.setdefault("HF_ENDPOINT", "https://hf-mirror.com")
os.environ.setdefault("TIMEZONE", "Asia/Shanghai")

from fastapi import FastAPI  # noqa: E402
from pydantic import BaseModel  # noqa: E402

from yuno_memory.server import app, init_memory  # noqa: E402

# persona pack 切到 amy（personas/amy/persona.md → 启动同步为 scope='ai' 的自我认知基线；
# 数据目录随之变为 data/persona-amy/，与 Yuno 自带角色包隔离）
# embedder 用本地模型目录（ModelScope 下载的完整 sentence-transformers 目录），
# 避免 huggingface hub 在本机的下载卡死问题；CPU 推理（本机无 CUDA）。
init_memory(
    data_dir=str(ROOT / "data"),
    config={"memory": {"core": {"persona_pack": {"pack": "amy"}}}},
    embedder={
        "provider": "local",
        "model": str(ROOT / "data" / "bge-large-zh-v1.5"),
        "device": "cpu",
    },
)

import memory  # noqa: E402
from plugins import _db  # noqa: E402
import profile_cards  # noqa: E402

# 管线分步计时（性能诊断）：见 perf_trace.py；不用时注释掉这两行即可
import perf_trace  # noqa: E402

perf_trace.install()

# insight 管线（人物画像/关系挖掘，窗口分析）：必须在 _shared.CONFIG 初始化后导入
from insight import ingest_window as _insight_ingest_window  # noqa: E402
from insight import status as _insight_status  # noqa: E402
from insight import inject as insight_inject  # noqa: E402

# 上游 bug 修复：memory 包的 stats() 函数会被同名子模块 memory/stats.py 遮蔽
# （任何一次错误计数后 Memory.stats() 就变成调模块 → /health 500）。
# 只补 SDK 类方法，不动包属性（错误审计 _stats_err 依赖子模块）。
from yuno_memory.core import Memory as _MemoryCls  # noqa: E402


def _safe_stats(_self):
    return {
        "memories": len(_db.memory_rows()),
        "events": len(_db.event_rows()),
        "ai_memory": len(_db.memory_rows("ai")),
        "attrs": len(_db.attr_rows()),
    }


_MemoryCls.stats = _safe_stats


class AfterChatRequest(BaseModel):
    """完整管线写入：对应 Yuno 自己 bot 回合的 after_chat 调用。

    kind=group → scope=group:<group_id>，key=<user_id>（成员归属）
    kind=private → scope=c2c:<user_id>
    text=用户原话（建议带发送者前缀），reply=Amy 的回复（被动听取群聊时为空）
    sender=发送者当前昵称（群名片）：进花名册 + 重写 text 前缀为 QQ 号锚定格式
    """

    kind: str = "group"
    group_id: int = 0
    user_id: int = 0
    text: str
    reply: str = ""
    sender: str = ""
    msg_time: int = 0  # 消息原始时间戳：回放/挂起 flush 的旧消息不许回拨花名册现名


# ── 群花名册（实体归一）：QQ号 → {现名, 曾用名, last_seen} ──────────────
# 背景：昵称会变，记忆文本若以昵称锚定人物，改名后即"失联"（一人分裂成两人）。
# 花名册是权威映射：ingest 时更新，context 注入时前置，Amy 认人一律以 QQ 号为准。
# 存 kv(namespace='roster', key='group:<gid>')，不建新表、不动 SDK。

_ROSTER_NAMESPACE = "roster"
_ROSTER_MAX_MEMBERS = 40  # 注入上限（按 last_seen 近者优先）
_ROSTER_MAX_ALIASES = 8
_ROSTER_REWRITE_EVERY_S = 3600  # 昵称没变时最多 1h 重写一次 kv（省 SQLite 写）


def _record_roster(scope: str, user_id, nickname: str, msg_time: int = 0) -> None:
    if not scope.startswith("group:") or not user_id or str(user_id) in ("0", ""):
        return
    nickname = (nickname or "").strip()
    if not nickname:
        return
    uid = str(user_id)
    ts = msg_time or time.time()
    roster = _db.kv_get(_ROSTER_NAMESPACE, scope, {}) or {}
    entry = roster.get(uid) or {}
    prev_ts = _parse_iso_ts(entry.get("last_seen", ""))
    if prev_ts and ts < prev_ts:
        return  # 陈旧消息（挂起 flush / 乱序回放）：不许把现名回拨成旧昵称
    cur = (entry.get("nickname") or "").strip()
    if cur == nickname:
        # 昵称没变：节流重写（只刷新 last_seen）
        if time.time() - prev_ts < _ROSTER_REWRITE_EVERY_S:
            return
    aliases = [a for a in entry.get("aliases", []) if a and a != nickname]
    if cur and cur != nickname and cur not in aliases:
        aliases.insert(0, cur)  # 改名：旧名自动归档为曾用名
    roster[uid] = {
        "nickname": nickname,
        "aliases": aliases[:_ROSTER_MAX_ALIASES],
        "last_seen": datetime.fromtimestamp(ts).isoformat(timespec="seconds"),
    }
    _db.kv_set(_ROSTER_NAMESPACE, scope, roster)


def _parse_iso_ts(s: str) -> float:
    try:
        return datetime.fromisoformat(s).timestamp() if s else 0.0
    except ValueError:
        return 0.0


def _anchor_group_text(sender: str, user_id, text: str) -> str:
    """群消息身份锚定：'昵称：内容' → '用户（昵称 X，QQ:uid）：内容'。

    提取 LLM 只看得见 text，身份若只有昵称，事实就以昵称锚定（曾出现昵称
    "想当兵马俑，因为每天守嬴政的很爽"被提取成"他想当兵马俑"的事故）。
    用"用户（…）："而非"昵称（QQ:…）："：提取 prompt 约定正文格式为
    '用户：…机器人：…'，主语必须是"用户"，否则 LLM 会把前缀里的名字
    当成消息内容提取成"用户提到某名字"。
    挂起合并的多行文本逐行剥同一前缀；不匹配时前置锚定，绝不丢内容。
    """
    if not sender:
        return text
    prefix = f"{sender}："
    tag = f"（昵称 {sender}，QQ:{user_id}）："
    out = []
    for ln in text.split("\n"):
        out.append(f"用户{tag}" + ln[len(prefix):] if ln.startswith(prefix) else ln)
    anchored = "\n".join(out)
    if f"QQ:{user_id}" not in anchored:
        anchored = f"用户{tag}{anchored}"
    return anchored


def _roster_block(scopes: list) -> str:
    """花名册注入块：置于检索上下文最前，不占检索预算。"""
    lines = []
    for scope in scopes or []:
        if not scope or not scope.startswith("group:"):
            continue
        roster = _db.kv_get(_ROSTER_NAMESPACE, scope, {}) or {}
        if not roster:
            continue
        ranked = sorted(
            roster.items(),
            key=lambda kv: kv[1].get("last_seen") or "",
            reverse=True,
        )[:_ROSTER_MAX_MEMBERS]
        gid = scope.split(":", 1)[1]
        lines.append(f"〔群 {gid}〕")
        # 昵称撞主消歧：QQ 昵称可换主，多个 QQ 用过同一名字时反查有歧义
        name_owners: dict[str, int] = {}
        for _uid, _e in ranked:
            for _nm in [_e.get("nickname", ""), *(_e.get("aliases") or [])]:
                if _nm:
                    name_owners[_nm] = name_owners.get(_nm, 0) + 1
        for uid, e in ranked:
            aliases = e.get("aliases") or []
            dup = any((name_owners.get(nm, 0) or 0) > 1
                      for nm in [e.get("nickname", ""), *aliases] if nm)
            warn = "（⚠此昵称有多人用过，按昵称反查需谨慎）" if dup else ""
            alias_s = f"（曾用名：{'；'.join(a for a in aliases if a)}）" if aliases else ""
            lines.append(f"- QQ {uid} = 「{e.get('nickname', '')}」{alias_s}{warn}")
    if not lines:
        return ""
    return (
        "【群成员花名册】昵称随时会变，认人一律以 QQ 号为唯一身份；"
        "记忆里的旧昵称按曾用名对号入座，不要当成另一个人：\n" + "\n".join(lines)
    )


class ContextRequest(BaseModel):
    """检索注入：七路融合检索 + 预算化分级注入（议题→记忆→事件脉络）。"""

    query: str
    scopes: list[str]
    top_k: int = 5
    min_score: float = 0.25
    budget: int = 2000
    user_id: int = 0  # 当前对话者：非 0 时前置注入其印象卡


class AddFactRequest(BaseModel):
    """显式写入单条事实（用户对 Amy 说"记住 X"时用）。"""

    scope: str
    key: str = ""
    fact: str
    importance: float = 0.6
    confidence: float = 0.85


@app.post("/amy/after_chat")
async def amy_after_chat(req: AfterChatRequest):
    # 原文直存（2026-10-25 定案）：Yuno 只做存储，不做任何自动提取——
    # deepseek-flash 摄取管线曾用"QQ是X"级退化条目顶掉高置信记忆（详见
    # AGENTS.md 记忆规则）。记忆判断由 Amy 运行时自行完成：回复前自查
    # /amy/context，值得长存的用 /amy/add_fact 显式写入。
    # scene 内部按字符串 id 处理（Yuno 官方循环传的是字符串 openid，
    # int 会在 _safe_id 的 re.sub 里崩）。
    if req.kind == "group":
        scope = f"group:{req.group_id}"
        _record_roster(scope, req.user_id, req.sender, req.msg_time)
        req.text = _anchor_group_text(req.sender, req.user_id, req.text)
    else:
        scope = f"c2c:{req.user_id}"
    import asyncio as _a
    ts = datetime.now().isoformat(timespec="seconds")
    _db.kv_set("memory", f"lastmsg:{scope}", ts)
    _db.kv_set("memory", f"last_user_msg:{scope}",
               {"ts": ts, "text": str(req.text or "")[:200]})
    await _a.to_thread(
        memory.trace_record, scope,
        speaker="user", raw_content=req.text,
        action="raw_store", modules=["memory"],
        reasoning="原文直存（无自动提取）", hint="raw",
    )
    if req.reply:
        await _a.to_thread(
            memory.trace_record, scope,
            speaker="amy", raw_content=req.reply,
            action="raw_store", modules=["memory"],
            reasoning="原文直存（无自动提取）", hint="raw",
        )
    return {"status": "ok"}


@app.post("/amy/context")
def amy_context(req: ContextRequest):
    text = memory.assemble_context(
        req.query, req.scopes, top_k=req.top_k,
        min_score=req.min_score, budget=req.budget,
        owner_uid=req.user_id or None,  # 记忆归属标注：他人名下事实打⚠（防张冠李戴）
    )
    roster = _roster_block(req.scopes)
    if roster:
        text = f"{roster}\n\n{text}" if text else roster
    # 当前对话者的印象卡：置于最前（人比名单更具体）
    if req.user_id:
        card = profile_cards.card_inject_text(req.scopes[0] if req.scopes else "", req.user_id)
        if card:
            text = f"{card}\n\n{text}" if text else card
    # 人物关系参考（insight 管线挖掘的社交结构，置于末尾）
    gid = 0
    for s in req.scopes or []:
        if s.startswith("group:"):
            try:
                gid = int(s.split(":", 1)[1])
            except ValueError:
                pass
            break
    if gid and req.user_id:
        try:
            rel = insight_inject.relations_block(gid, req.user_id, req.query)
            if rel:
                text = f"{text}\n\n{rel}" if text else rel
        except Exception as e:
            print(f"[insight] 关系注入失败: {e}")
    return {"status": "ok", "context": text}


class InsightMessage(BaseModel):
    """窗口消息：text 为识别增强后的平铺文本；reply_to_qq/at_users 为指向性（互动网络数据源）"""

    msg_id: str = ""
    qq: int
    sender: str = ""
    text: str
    ts: float = 0
    reply_to_qq: int = 0
    at_users: list[int] = []


class InsightWindowRequest(BaseModel):
    """窗口分析请求：napcat-mcp 采集侧按时间间隔切窗后整窗提交"""

    group_id: int
    messages: list[InsightMessage]


@app.post("/amy/ingest_window")
async def amy_ingest_window(req: InsightWindowRequest):
    import asyncio as _a
    msgs = [
        {"msg_id": m.msg_id, "qq": m.qq, "sender": m.sender, "text": m.text,
         "ts": m.ts, "reply_to_qq": m.reply_to_qq, "at_users": list(m.at_users)}
        for m in req.messages
    ]
    result = await _a.to_thread(_insight_ingest_window, req.group_id, msgs)
    return result


@app.post("/amy/insight_status")
def amy_insight_status():
    return {"status": "ok", "insight": _insight_status()}


@app.get("/amy/card")
def amy_card(scope: str, uid: int):
    """读某人的印象卡正文（记住类操作后回读给对方确认用——对话内人工终审）。"""
    text = profile_cards.card_inject_text(scope, uid)
    if not text:
        return {"status": "ok", "card": "", "note": "该人还没有印象卡"}
    return {"status": "ok", "card": text}


@app.post("/amy/refresh_cards")
def amy_refresh_cards(scope: str = "group:123456789", force: bool = True):
    """印象卡全量刷新（首次建档 / 夜间兜底 / 手动）。每人一次 LLM 调用，耗时较长。"""
    import asyncio as _a
    results = _a.run(_a.to_thread(profile_cards.refresh_all, scope, force))
    return {"status": "ok", "cards": results}


@app.post("/amy/add_fact")
def amy_add_fact(req: AddFactRequest):
    result = memory.add_fact(
        req.scope, req.key, req.fact,
        importance=req.importance, confidence=req.confidence,
        source="amy",
    )
    # 显式记住的事实（"记住X"/称谓等）立即固化进印象卡：记忆行会随整段重写被
    # 合并/淘汰，卡是永不丢失的档案层。群 scope 且带 key（具体某人）才刷。
    if result and req.scope.startswith("group:") and req.key:
        import asyncio as _a
        try:
            _a.run(_a.to_thread(profile_cards.generate_card, req.scope, req.key, False))
        except Exception:
            pass
    return {"status": "ok", "result": result}


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=int(os.getenv("YUNO_PORT", "8457")))
