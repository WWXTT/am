# yuno-overlay — 叠加在 Yuno 上游之上的本项目定制层

Yuno 本身是独立工程（[sty84/yuno-qq-bot](https://github.com/sty84/yuno-qq-bot)，MIT）。
这个目录只放**本项目新增/改动**的几个文件，拷进 `yuno/` 覆盖即可：

```powershell
git clone https://github.com/sty84/yuno-qq-bot.git yuno
Copy-Item yuno-overlay\* yuno\ -Force
```

## 文件

| 文件 | 作用 |
|---|---|
| `run_service.py` | 服务入口。必须用它而不是 `python -m yuno_memory`：① 先加载 `.env` 再导入 Yuno 模块（Yuno 自己的 `.env` 加载晚于 `_db` 导入，`YUNO_DB_BACKEND` 会失效）；② 额外暴露 `POST /amy/after_chat`（直通 `plugins.memory.after_chat`，完整管线）和 `POST /amy/context`（七路检索 + 预算化注入的成品上下文）；③ 把 persona pack 切到 `amy`（数据落在 `data/persona-amy/`）、embedder 指向本地 `data/bge-large-zh-v1.5`（CPU） |
| `start-yuno.bat` | 无窗启动（转发到上游的 `start_hidden.py`），已在跑则直接退出 |
| `nightly_grow.py` | 夜间成长：`backup → memory-grow → memory-sleep`，同样是「先 `.env` 后导入」的顺序包装 |
| `nightly-growth.bat` | 计划任务入口（调 `nightly_grow.py`） |

## 依赖

- `yuno/.env`（参照上游 `.env.example`）：至少 `YUNO_API_TOKEN`、`DEEPSEEK_API_KEY`、`YUNO_DB_BACKEND=sqlite`
- `yuno/data/bge-large-zh-v1.5/`：完整 sentence-transformers 目录（1024 维，CPU 推理）
- `yuno/personas/amy/persona.md`：由仓库根 `persona.md` 同步生成：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\tools\sync-persona.ps1
```

## 边界

- 记忆库 `yuno/data/persona-*/bot.db` **不入库**（含聊天原文，属隐私数据）。
- 上游升级时，`run_service.py` 里的接口（`/amy/after_chat`、`/amy/context`）要跟着 `plugins.memory` 的签名走；
  `duty doctor` 会检查 `/health`，接口不匹配会在首回合就报错。
