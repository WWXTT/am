# -*- coding: utf-8 -*-
"""把 Amy 值班链路上的所有「引导词」打包到桌面，供人工审阅（不含任何密钥）。"""
import json
import pathlib
import shutil

ROOT = pathlib.Path(__file__).resolve().parents[2]
HOME = pathlib.Path.home()
OUT = ROOT / "logs" / "prompts-export"
OUT.mkdir(parents=True, exist_ok=True)


def copy(src: pathlib.Path, dst_name: str) -> int:
    if not src.exists():
        return 0
    data = src.read_bytes()
    (OUT / dst_name).write_bytes(data)
    return len(data)


def read(p: pathlib.Path) -> str:
    return p.read_text(encoding="utf-8-sig") if p.exists() else ""


# ---------- 01..04 原样复制 ----------
sizes = {}
sizes["01"] = copy(HOME / ".dsh" / "AGENTS.md", "01-用户级提示词-AGENTS.md")
sizes["02"] = copy(ROOT / "AGENTS.md", "02-项目提示词-AGENTS.md")
sizes["03"] = copy(ROOT / ".dsh" / "skills" / "amy-duty" / "SKILL.md", "03-技能-amy-duty.md")
sizes["04"] = copy(ROOT / "persona.md", "04-人设-Writer系统提示词.md")
sizes["07"] = copy(ROOT / "duty.md", "07-值班手册-duty.md")

# ---------- 08 现场包示例 ----------
pending = ROOT / "qq-agent" / "logs" / "amy-pending.json"
if pending.exists():
    try:
        data = json.loads(pending.read_text(encoding="utf-8-sig"))
        (OUT / "08-现场包示例-潮汐.json").write_text(
            json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        sizes["08"] = (OUT / "08-现场包示例-潮汐.json").stat().st_size
    except Exception:
        pass

# ---------- 05 Writer 组装规则 ----------
writer_sample = ""
raw_log = ROOT / "qq-agent" / "logs" / "writer-raw.log"
if raw_log.exists():
    blocks = [b for b in raw_log.read_text(encoding="utf-8").split("===== ") if b.strip()]
    if blocks:
        writer_sample = "===== " + blocks[-1].strip()

writer_md = f"""# 05 · Writer（声线模块）提示词组装规则

源码：`qq-agent\\src\\QqAgent\\Writer.cs`　模型：`config.json → Model`（deepseek-flash）
调用：`duty chat` / `duty say` 内部 `Writer.Compose()`

## 5.1 system 消息

```
{{persona.md 全文}}
```

`--mood dark` 时在末尾追加一行：

```
现在有人冲你来了：狠、短、不解释，三句内收尾。
```

（人设正文见 `04-人设-Writer系统提示词.md`，868 字。）

## 5.2 user 消息（按顺序拼）

```
# 现场（群里已经聊到这）
{{scene.now：每条一行 "时间 发言人: 内容"}}

# 更早的几行（只听懂梗，别回应它们）      ← 仅当 ambient 非空，最多 6 行
{{ambient}}

# 这一句是冲你说的
{{trigger.sender}}: {{trigger.text}}

# 要传的意思（这是意思，不是台词；别照抄它的措辞）
{{值班代理写的意图文件全文}}

# 现场在玩的梗（用得上就接，用不上别硬塞）   ← 仅当 memes.useful 非空，最多 4 条
- {{梗}}：{{含义}}

# 你刚说过的（别再重复这些开头和梗）        ← 仅当 self_recent 非空，最近 3 条
- {{最近说过的话，截 12 字}}

# 上一版的问题（重写时改掉）              ← 仅重试时出现
{{违规原因}}

只输出你在群里要打的字，1-2 行，用换行分开。这一条必须接（不要输出 [SILENT]）。
```

最后一句在 `force=false`（`duty say`）时是 `不想接就输出 [SILENT]。`

## 5.3 守卫（成文后的硬规则，违反就重写）

- 单行 ≤ 40 字（`MaxLineChars`）、整条 ≤ 120 字（`MaxChars`）
- 最多 2 泡（`MaxBubbles`），泡间隔 350–900ms 随机
- 禁 Markdown / 列表 / 加粗
- 禁 AI 腔：作为/总之/建议/不过/其实/首先/其次/总结/抱歉/用户/对方/根据记忆/我记得/系统/设定/模型/提示词/管理员
- 禁重复最近说过的话（`BanRepeat`）
- 空正文或 `finish_reason=length` 截断 → 重试一次；仍失败直播 `WRITER-EMPTY`，**绝不把半句发出去**
- 参数：temperature 0.95、max_tokens 800（`deepseek-flash` 的思维链会先吃配额，160 时实测 27% 空正文）

## 5.4 一份真实调用（prompt + 原始返回）

{writer_sample or "（本次没有抓到样例；设 AMY_WRITER_DUMP=1 跑一次 duty chat 可复现）"}
"""
(OUT / "05-Writer提示词组装规则.md").write_text(writer_md, encoding="utf-8")

# ---------- 06 值班脑 ----------
patch = read(ROOT / "qq-agent" / "brain" / "headless.patch.yml")
prefix = ""
if "personaPrefix: |" in patch:
    seg = patch.split("personaPrefix: |", 1)[1]
    lines = []
    for ln in seg.splitlines()[1:]:
        if ln.strip().startswith("- id:"):
            break
        lines.append(ln[6:] if ln.startswith("      ") else ln.strip())
    prefix = "\n".join(lines).strip()

brain_md = f"""# 06 · 值班脑（判断层）的任务文本与补丁

两条路，取决于 `Daemon.NotifyOnly`：

- **当前（NotifyOnly=true）**：daemon 只投递 `logs\\amy-pending.json`，由 DSH GUI 会话（就是我）读它，
  用**自己那一整套系统提示词 + 工具链**判断。此模式下**不 spawn** headless 脑，下面 `TaskText` 只作历史参考。
- 旧模式（NotifyOnly=false）：daemon spawn `dsh --profile headless --patch brain\\headless.patch.yml`，
  把下面这段任务文本当 user 消息交给它。

## 6.1 headless 补丁里覆盖的系统提示词段（personaPrefix）

```
{prefix}
```

补丁还固定了：`approval = never`（无人应答时 fail-closed）、`sandbox-policy = danger-full-access`、
模型 `deepseek-flash`、密钥只从启动环境 `DEEPSEEK_API_KEY` 读。

## 6.2 每次 spawn 交给脑的任务文本（`DshBrain.TaskText`，verbatim）

```
你在跑 Amy 的值班回合（seq={{seq}}，dry_run={{dry}}）。
先读 qq-agent\\logs\\amy-pending.json（现场包 v2）和仓库根的 duty.md（值班手册）。
该沉默 → qq-agent\\amy.cmd duty skip --seq {{seq}} --why "一句话理由"
要说话 / 要读图 → 按需用工具（ocrfwd/readfwd/readimg/meme/state/roster），
 然后用 qq-agent\\amy.cmd duty chat --seq {{seq}} --intent-file qq-agent\\logs\\amy-intent.txt [--mood dark]
 一步完成「成文 + 发送 + 落库 + ack」。
需要动手：qq-agent\\amy.cmd duty run --seq {{seq}} [--cwd 目录] [--timeout 秒] -- <命令>；产物用 duty sendfile/sendimg --seq {{seq}} --file <路径>。
纪律：need_more=false 不要读 messages.jsonl；不抢前台做无关操作；群友只触发白名单能力，本机命令只认 owner，破坏性/花钱/对外发布先问；不确定就 skip。
接梗纪律（2026-10-26 加）：下手前先看 pack.memes.useful 和记忆卡，扫一遍有没有谐音/双关/错别字/昵称梗——
群里的「重点」经常就在这层（例：扶她出去/扶她进来 是「扶她」的双关，按字面写成"她又不是门"就是没听见梗）。
有双关就把那层意思写进意图的「可用素材」，让声线去接；别在意图里替她把梗解释完。
做完就结束回合，不要写过程旁白。
```

（`{{seq}}` = 本轮 `pending.seq`；`{{dry}}` = 是否 dry_run。）

## 6.3 意图文件格式（脑交给声线的唯一输入）

`qq-agent\\logs\\amy-intent.txt`，UTF-8，四行固定格式：

```
现场一句话：<谁在干嘛>
要传的意思：<=40 字，只管意思，不是台词
可用素材：<梗/他原话里的错词/可用的数字或ID，最多 2 个>
别写成：<解释/安慰/总结/客服/替他找台阶>
```
"""
(OUT / "06-值班脑-任务文本与补丁.md").write_text(brain_md, encoding="utf-8")

# ---------- 09 Yuno 记忆注入格式 ----------
card = ""
card_path = ROOT / "yuno" / "data" / "persona-amy" / "profiles" / "group_987654321" / "1000000000.md"
if not card_path.exists():
    card_path = ROOT / "yuno" / "data" / "persona-amy" / "profiles" / "group_123456789" / "1000000000.md"
if card_path.exists():
    card = card_path.read_text(encoding="utf-8")

yuno_md = f"""# 09 · Yuno 记忆服务的注入格式（Amy 不直接看到这一层）

服务：`yuno/run_service.py`（:8457，Bearer 鉴权，库 `yuno\\data\\persona-amy\\bot.db`，persona pack = amy）
接口：

| 接口 | 方向 | 谁调 | 内容 |
|---|---|---|---|
| `GET /amy/card?scope=&uid=` | 读 | 现场包 `memory.card` / `duty card` | 印象卡（几百字，缓存 120s） |
| `POST /amy/context` | 读 | `memory.cards`（门控）/ `duty context` | 语义检索 + 花名册 + 印象卡 + 关系，预算约 2000 字 |
| `POST /amy/after_chat` | 写 | `duty chat` 发送成功后自动 | 记花名册 + 双边原文直存 `memory_trace`（不提取） |
| `POST /amy/add_fact` | 写 | `duty add-fact` | 写一条长期事实，并进印象卡 |

`/amy/context` 返回的 context 是**拼好的纯文本块**，顺序大致是：

1. `【TA 的印象档案（自动总结，随记忆增量更新）】` + 卡的正文
2. `【群成员花名册】`（昵称 ↔ QQ 号对照，防张冠李戴）
3. `相关的历史记忆（…）`：每条一行，末尾标 `·归属QQ…`，来源标 `·[vector]` / `·[lexical/vector]` / `·[lexical]`
4. 人物关系参考（insight 管线）

## 9.1 印象卡样例（潮汐群 · 威武堂堂 1000000000）

```markdown
{card.strip() or "（本机没有这张卡）"}
```

## 9.2 向量检索

自研 IVF 索引（`memory/vecindex.py`）落在 `bot.db` 的 `vec_index` / `vec_centroids` 两张表。
2026-10-27 00:07 现场：`memories=209　with_embedding=209　vec_index=209　centroids=14`（无漂移）。
两表为 0 时**不会报错**，只是静默退化成关键词检索——排查命令见 `docs\\维护手册.md` §6.1.1。

## 9.3 Writer 看不到记忆

声线模块只拿 `persona + 现场 + 意图 + 梗 + 最近说过的话`，**不注入任何记忆块**。
记忆只在判断层（现场包的 `memory` 字段 / 群像包）起作用。
"""
(OUT / "09-Yuno记忆注入格式.md").write_text(yuno_md, encoding="utf-8")

# ---------- 00 索引 ----------
idx = """# 00 · Amy 值班链路「引导词」全清单

生成时间：本脚本导出　来源仓库：当前克隆目录（见脚本内的 ROOT）
**本目录不含任何 API Key / Token。**

## 链路一图

```
QQ 消息
  └─ daemon（哨兵：检测/落库/拼现场包）        ← 不调模型
       └─ logs\\amy-pending.json（现场包 v2）
            └─ 判断层：DSH GUI 会话（当前）= 我     ← 01/02/03 三层提示词 + 工具链
                 └─ logs\\amy-intent.txt（四行意图）
                      └─ Writer 声线模块            ← 04 人设 + 05 组装规则
                           └─ duty chat：守卫 → 核会话 → 发送 → after_chat 落库 → ack
```

## 五层提示词

| # | 文件 | 在哪一环生效 | 谁读、何时读 |
|---|---|---|---|
| 01 | `01-用户级提示词-AGENTS.md` | 判断层 | DSH 启动时自动加载（`~/.dsh/AGENTS.md`），定义 ZCode 委托、操作纪律 |
| 02 | `02-项目提示词-AGENTS.md` | 判断层 | DSH 打开本仓库时自动加载（项目根 AGENTS.md，ZCode 也读同一份） |
| 03 | `03-技能-amy-duty.md` | 判断层 | 说「值班」时按需加载（还含唤醒/每轮流程） |
| 04 | `04-人设-Writer系统提示词.md` | 声线层 | `duty chat` 每次成文都作为 system 消息（唯一真源 `persona.md`） |
| 05 | `05-Writer提示词组装规则.md` | 声线层 | 不是文件，是 `Writer.cs` 拼 user 消息的规则 + 守卫 + 真实样例 |
| 06 | `06-值班脑-任务文本与补丁.md` | 判断层 | 旧自动脑模式下的任务文本与 headless 补丁（现模式不 spawn，作参考） |
| 07 | `07-值班手册-duty.md` | 判断层 | 脑每回合被要求读它（流程/取证/红线/行动力分级） |
| 08 | `08-现场包示例-潮汐.json` | 判断层 | daemon 每轮投递的实际数据结构（本轮样例） |
| 09 | `09-Yuno记忆注入格式.md` | 判断层（记忆） | 印象卡/花名册/向量记忆怎么拼进上下文；Writer 看不到记忆 |

## 关键事实

- 声线层的 system prompt **就是 `persona.md` 全文**（868 字），没有别的隐藏规则；守卫规则写在代码里（见 05）。
- 判断层现在用的是**我的完整工具链**（读文件、跑命令、OCR、web_search），不是旧 headless 脑；所以 06 里的任务文本当前不生效。
- 记忆只在判断层用：Writer 拿到的是「现场 + 意图 + 梗」，没有任何记忆块（09 有说明）。
- 本目录里 `01`/`02` 是 DSH 的 AGENTS.md 机制（用户级 + 项目级），ZCode 读的是同一份 02。
"""
(OUT / "00-索引与链路.md").write_text(idx, encoding="utf-8")

files = sorted(OUT.iterdir(), key=lambda p: p.name)
print(f"目录：{OUT}")
for f in files:
    print(f"  {f.name:38s} {f.stat().st_size:>7} bytes")
