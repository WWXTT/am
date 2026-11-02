# Amy 运维工作区

四份唯一文档（从这里进去，不要另开文档）：

| 文档 | 文件 | 内容 |
|---|---|---|
| **人设** | [persona.md] | 群消息回复时的人设 |
| **操作手册** | [duty.md]| 值班代理每回合流程 + 人工操作（启停/命令/QQ/OCR/记忆/梗） |
| **哨兵** | [哨兵.md] | daemon + DSH 值班脑 + 现场包 + 唤醒/ack 的机制、状态与失败处理 |
| **维护手册** | [维护手册.md] | 依赖安装、配置、命令、测试、排障、记忆库/清库、备份清单、基线 |

启动：双击仓库根 **`一键启动.bat`**（= `qq-agent/start-all.ps1`：依赖准备 → QQNT → Yuno → 值班脑依赖 → daemon；**生图 SD 不启动**）。

人设：唯一真源 `persona.md`；改完跑 `qq-agent\tools\sync-persona.ps1` 同步 `yuno/personas/amy/persona.md`；`duty doctor` 会报漂移。

模式（同一时刻只允许一个大脑）：
- `Daemon.NotifyOnly=true`**：daemon 只做哨兵——检测/落库/投递 `logs\amy-pending.json`，**不调模型、不发送、不 spawn**；
  由 DSH GUI 会话（`.dsh\skills\amy-duty` + `duty-watch.ps1` 后台任务）消费 pending，做整套判断/取证/写意图，再 `duty chat` 出话。
  用户定案：「哨兵调起，DSH 会话完成推理和回复」——headless 值班脑判断太浅，已弃用。消费端活着才有回复；会话关了要重新说「开始值班」。

核心红线：
- **认人**只认 `uid` + `roster`；OCR/图片结论不能反推发送者。
- **发送前核会话**（`duty chat` 已内建），不一致中止。
- **原文只存不删**；用户明确要求清库才走 `qq-agent\tools\purge-amy-history.py`（默认 dry-run）。
- 只操作 QQ 窗口内部；不改别人文件、不碰设置/账号/支付。
- 单大脑；机器高压只做纯文字短回，复杂任务拒绝并直播 `HANDOFF`。
- **行动力**：L0–L3 分级见 [duty.md §15](duty.md#15-行动力2026-10-26-新增能动手就别只动嘴)；`duty run` 本机命令只认 owner（owner 可关机/重启/结束进程/装软件；只硬拦磁盘擦除类），群友只能跑白名单脚本/发白名单目录产物；**不做运行时自拓展**（新工具=改代码）。



