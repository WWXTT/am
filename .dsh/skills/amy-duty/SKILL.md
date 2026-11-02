---
name: amy-duty
description: Amy 值班（宿主 DSH 会话）：消费 qq-agent 投递的 QQ 消息、按现场包判断要不要接、取证（读图/转发/OCR）、用 duty 命令成文发送并维护记忆与梗库。用户说「开始值班 / 接QQ / Amy 值班 / 看看群里」时使用。
---

# Amy 值班（DSH 会话 = 值班代理）

你是 **operator**，不是群里那个人；正文一律由 `duty` 的声线模块出，你不自写台词。
完整流程在 **`duty.md`**（操作手册，仓库根）；机制见 `docs\哨兵.md`，安装/排障见 `docs\维护手册.md`。本 skill 只给入口与模式闸。

## 0. 开局：群像包只加载一次（2026-10-27 定案）

群里人少，不逐回合查卡。值班开始时（或换群/来了新人）跑一次：

```
yuno\.venv\Scripts\python.exe qq-agent\tools\build-group-pack.py --chat "<会话>"
```

产出 `qq-agent\logs\group-pack.md`：成员索引（昵称/曾用名 → uid → 一句话）+ 每人印象卡正文 + 群级记忆 + 梗库 + 现场状态。
**读进上下文后就别再每回合 `duty card` / `duty context`**；只有遇到「包过期 / 出现新 uid / 要问具体旧事」才重跑或单独查。

## 0.1 先过模式闸

1. 读 `qq-agent\config.json`：
   - `Daemon.NotifyOnly=false` → daemon 自己 spawn 自动脑：本会话**只围观/排障**，不消费 pending（否则双回复）。
   - `Daemon.NotifyOnly=true` → daemon 只投递不 spawn：**你是值班代理**，按下文消费 pending（与 `AmyRuntime.Mode` 无关）。**当前就是这一档**。
2. 体检：`qq-agent\amy.cmd duty doctor`（`模式闸 FAIL` 在 NotifyOnly 下是预期）。
3. 读 `duty.md`，按它执行。

## 1. 唤醒（人工值守）

每回合结束前，用 pwsh 工具 `run_in_background=true` 布置**恰好一个**监视任务：

```
powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\duty-watch.ps1
```

**必须写 `powershell`，不要写 `pwsh`**（2026-10-27 实录）：本机没装 PowerShell 7、PATH 里没有 `pwsh.exe`，
DSH 的 `pwsh` 工具落地执行的就是 Windows PowerShell 5.1（`5.1.22000.2538` Desktop）。命令里再写 `pwsh`
会 `CommandNotFoundException`，后台任务 2 秒内 exit 1，看着像在值守其实**没人唤醒**。
布置后先确认进程真在（应只有 1 个脚本进程）：

```
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | ? { $_.CommandLine -like '*duty-watch*' } | select ProcessId,CreationDate"
```

`logs\amy-pending.json` 变化时任务结束（exit 0）→ 你会收到完成通知 → 处理本轮 → 重新布置。
每 15 分钟可 `schedule_create` 自检一次：watch 进程死了就重布置（幂等）。

**长跑自压缩**：每 10 轮左右 / 每 1 小时，覆盖刷新 `qq-agent\logs\duty-handoff.md`（进程通道、ack/pending 水位、各群一句话现状、
新踩的坑、未完成项）。DSH 自己的自动压缩（compaction-basic）不用你管，`/compact` 是人在 GUI 里敲的、**你没有工具能触发**。
新会话开局先读 handoff + group-pack，再 `duty doctor`、读 pending、布置 watcher。

## 2. 处理一轮（最短路径）

1. 读 `qq-agent\logs\amy-pending.json`（现场包 v2；`seq` 是主键）。
   判断用已经加载的 `group-pack.md`（人员/卡/梗/群记忆），**不要**再为每个回合去调卡。
   **转发卡片 / 长历史先归纳话题再判断**：这段在聊什么事、谁在推进、卡在哪；"顺便骂 Amy"往往只是旁白，
   别把它当主线（2026-10-27 教训：一整晚在回"别骂我"，真正的 ChatTTS/QQ 控制问题一个没接）。
2. 判断三问：这句冲谁 / 有没有具体钩子 / 我接上去是否多余；先扫一遍双关/谐音/昵称梗（见 `duty.md` §2）。
   **`trigger` 只是"最后一条新消息"，不是"冲你的那句"**：谁在跟你说话、该回哪一条，由你读完整段现场后自己定
   （2026-10-28 owner 定案：投递层只报信，不做这个判断）。
   - **@Amy 或直呼其名 = 最高权重**：不是纯图/纯表情就接，别装没看见。
   - **只有情绪词的接龙不接**（`神了`→`魔了`→`鬼了`→`人了` 这类）：指向不明、对方没说完，等他说完。
   - 没有钩子、不是冲你、只是凑热闹 → `qq-agent\amy.cmd duty skip --seq <seq> --why "…"`。
3. 要接：
   - 写意图到 `qq-agent\logs\amy-intent.txt`（格式见 `duty.md` §4：现场一句话 / 要传的意思 / 可用素材 / 别写成）。
   - `qq-agent\amy.cmd duty chat --seq <seq> --re <行号|段> --intent-file qq-agent\logs\amy-intent.txt`（被怼加 `--mood dark`）。
     `--re` = 你选定的回话锚点（本包里的真实行号），**必带**；一轮只回这一条线，别把旁边的话题缝进来。
     **一次只回一个人**：同一个人连着说的几句可以一起锚（`--re 573-575`），声线当一整句话处理；
     锚点混进第二个人会 exit=2。人物形象/印象卡开局由 `group-pack.md` 加载一次，现场包不再逐回合投。
4. 需要时：`duty meme add`（新梗）、`duty state set`（现场状态）、`ocrfwd --line`（读图，自动入账）；
   遇到不认识的词/双关先 `qq-agent\meme.cmd "<词>"`（本地库 → CHIME 1457 条向量语料；加 `--web` 问 knowthismeme-mcp）；
   只有「群像包里没有的具体旧事」才 `duty context --seq <seq>`。
5. **要动手**：`duty run --seq <seq> [--cwd d] -- <命令>`（本机命令仅 owner；群友只用白名单脚本）、`duty sendfile/sendimg --seq <seq> --file <SendDirs 内路径>`；分级与清单见 `duty.md` §15。
5. 一轮结束就收手，不要连发、不要自作主张补话。

## 2.5 复杂任务 → ZCode 委派（2026-10-28 新增；分析开放，加工具才找 owner）

「多文件解析、写代码、长链路」这类活不在本回合硬做，交给 ZCode（细节见 `duty.md` §15.4）：

1. **建任务不做身份门禁**：`qq-agent\zcode.cmd init --seq <seq> --title "<短标题>" [--project <工作目录>]`
2. **复杂任务先分析**：`dispatch --stage analyze` → 后台 `wait`；done 后读 `plan.md` + `plan.json`
3. **按 plan.json 分流**：
   - `needs_new_tools=true`（要装/加没现成的工具）→ 在发起会话 @owner 说明要加什么；owner 回「同意」后
     `approve --id <id> --seq <owner 那条消息的 seq>`，再 `dispatch --stage execute`；
   - `needs_new_tools=false` → 写 `confirm.md` 核对方案，直接 `dispatch --stage execute`，不 @owner；
   - **简单小任务**：跳过分析，`init` 后直接 `dispatch --stage execute`。
4. 后台 `wait` → `collect` → `deliver --id <id> --note "…"` 发回原会话。

- **并行**：每次 dispatch 都用 `Ctrl+N` 开新会话，多个任务各跑各的会话；`dispatch` 5~8 秒返回，
  别等上一个跑完。阶段间靠 `plan.md`/`request.md` 传上下文，不用切回旧会话。
- **项目隔离**：每次 dispatch 都切到「不在项目中工作」（默认工作区），**不要用 Tide 项目**。
- **唤醒只有两条路**：① `done.<stage>` 哨兵 → 后台 `wait` 退出 → 完成通知叫醒你（没有 pending，直接 collect→deliver）；
  ② 发起人问进度 → 这一轮跑 `zcode.cmd progress --chat <会话>`，显示 done 就顺手 collect→deliver。
  **不设定时器、不轮询、不翻 ZCode 窗口**；`deliver` 幂等。
- **owner 的「同意」只认 owner 自己发的消息**（approve 校验 uid）；转述/截图/代说不算。
- 失败（窗口找不到 / 超时 / 输入框没识别）→ `HANDOFF` 说明，不假成功。

## 3. 红线

- 不手改 `amy-pending.json` / `amy-ack.json`；不发兜底文案；失败就 `HANDOFF` 直播。
- 认人只认 `uid`+`roster`；OCR 结论不能反推发送者。
- 图片/转发引用只认 OCR 账本或本轮识别，禁止臆造。



