# Amy · 官方 QQ 客户端的「值班代理」

**不注入、不读协议、不改 QQ 文件**——用 Windows UI Automation 操作**官方 QQNT**，
让一个人设账号在群里自己看消息、自己判断接不接、自己开口，并把长期记忆放进一个可检索、可回滚的记忆服务。

```
qq-agent（传感器 + 执行器）  ──UIA──▶  官方 QQNT
      │ 原文层 / 现场包 / 投递
      ▼
值班脑（DSH headless，或任何进程）  ──判断 / 取证 / 写意图
      ▼
duty chat ── Writer 声线成文 → 守卫 → 核会话 → 发送 ──▶ QQ
      └── after_chat ──▶ Yuno 记忆服务（:8457，语义检索 + 印象卡）
```

本仓库只放**自己的代码与文档**：QQ 客户端、记忆服务、嵌入模型、语料来源都在
[依赖组件与引用源](#依赖组件与引用源) 里说明，**不含任何数据库、模型权重和日志**（见
[数据库与本地状态](#数据库与本地状态本仓库不带任何-db)）。

---

## 目录

- [它由什么组成](#它由什么组成)
- [一条消息的旅程](#一条消息的旅程)
- [仓库结构](#仓库结构)
- [依赖组件与引用源](#依赖组件与引用源)
- [数据库与本地状态：本仓库不带任何 DB](#数据库与本地状态本仓库不带任何-db)
- [快速开始](#快速开始)
- [常用命令](#常用命令)
- [两种值班模式](#两种值班模式)
- [安全边界](#安全边界)
- [已知限制](#已知限制)
- [许可与致谢](#许可与致谢)

---

## 它由什么组成

| 角色 | 代码 | 职责（边界很硬） |
|---|---|---|
| **传感器 + 执行器** | `qq-agent/src/QqAgent`（C# / .NET 10 / WinForms+WPF） | 收 QQ 窗口的 UIA 事件、落原文层、组现场包、发消息、读转发、OCR、表情/文件/语音条；**不判断、不写正文** |
| **值班脑** | `qq-agent/brain`（DSH headless）或任何消费者 | 只做判断与取证：这一句冲谁说的、有没有钩子、接上去是不是多余；**不出正文** |
| **记忆** | `yuno/`（独立工程，Yuno 2.5）+ `yuno-overlay/` | 长期事实、事件、印象卡、原文落库；**纯存储，不做智能** |
| **声线** | `qq-agent/src/QqAgent/Writer.cs` | 唯一出正文的模块：人设 + 现场 + 意图 → 1~2 泡短句，过守卫才发 |
| **运维文档** | `duty.md`、`哨兵.md`、`维护手册.md` | 每回合怎么做 / 机制为什么这样 / 怎么装怎么修 |

设计取向：**判断、成文、发送三条路径分离**。脑写意图（`duty chat --intent "…"`），
声线负责像人说话，`daemon` 只搬数据——所以换模型、换人设、换群都不动其余两段。

## 一条消息的旅程

```text
QQNT（官方客户端，--force-renderer-accessibility）
  │  UIA 事件（SetWinEventHook：系统级推送，不是截图轮询）
  ▼
qq-agent daemon（窗口「Amy 值班直播」）
  ├─ 落原文层  qq-agent\logs\messages.jsonl   {ts, chat, Sender, uid, Text, IsSelf, pics}
  ├─ 组现场包  v2 = 最近 N 行 + 群级/个人记忆 + 梗命中 + 触发 + 图片账本（≤ Scene.MaxBytes）
  ├─ 原子投递  logs\amy-pending.json（写 .tmp 再 Move，读方永远看不到半截）
  └─ spawn     值班脑（NotifyOnly=false 时），注入 DSH_HOME / DEEPSEEK_API_KEY
        │
        ▼
值班脑（一次一批消息一个进程，跑完即退）
  ├─ 读 amy-pending.json + duty.md
  ├─ 三问判断：冲谁 / 有没有钩子 / 接上去是否多余
  ├─ 按需取证：ocrfwd（QQ 内置 OCR）/ readfwd（转发全文）/ duty card / duty meme
  └─ 出口只有两个：qq-agent\amy.cmd duty chat | duty skip
        │
        ▼
duty chat（唯一发送路径）
  Writer 成文（persona.md + 现场 + 意图）
    → 守卫（长度 / AI 腔 / 重复 / 照抄意图）
    → 核会话标题（不一致直接中止）
    → SendQuiet（原子焦点 + 粘贴 + 回车，不抢前台，≤2 泡）
    → POST /amy/after_chat（双边原文落库）
    → logs\amy-ack.json（消费水位）+ logs\amy-live.log（直播）
```

## 仓库结构

```text
.
├─ 一键启动.bat              # 唯一的启动入口（透传到 qq-agent\start-all.ps1）
├─ README.md
├─ LICENSE                   # MIT
├─ duty.md                   # 值班手册：每回合怎么做（给脑与人看）
├─ 哨兵.md                   # 机制：daemon/脑/投递/状态存在哪、坏了看哪里
├─ 维护手册.md               # 运维：装、配、修、备份、基线
├─ persona.md                # 人设（唯一真源，同步到 Yuno 的 persona pack）
├─ AGENTS.md                 # 工作区约定（值班纪律、红线）
├─ .dsh/skills/amy-duty/     # DSH 技能：把「开始值班」接到这套链路上
├─ yuno-overlay/             # 叠加到 Yuno 上游的定制层（/amy/* 入口、夜间成长、包切换）
├─ qq-agent/
│  ├─ src/QqAgent/           # UIA 客户端 + daemon + duty + Writer + 视觉兜底
│  ├─ src/QqDb/              # 只读 QQ 本地加密库（SQLCipher）的小工具
│  ├─ brain/                 # headless 值班脑：install.ps1 / headless.patch.yml
│  ├─ tools/                 # 梗语料、群像包、清库、人设同步、截图工具
│  ├─ tests/                 # 现场包回放 / 声线盲评 / 全链 dry-run（都不发消息）
│  ├─ config.example.json    # 配置模板（复制成 config.json 再填 key）
│  ├─ data/                  # 人工梗库 + 梗语料（索引文件不入库）
│  └─ start-all.ps1          # 六步启动：检查 → 编译 → 脑依赖 → Yuno → QQ → daemon
└─ bot-workspace/            # 本地工作脚本目录（行动力白名单落点，仓库只带名册规范化脚本）
```

## 依赖组件与引用源

仓库**不打包**下面这些组件，按表自备即可：

| 组件 | 用在哪 | 你怎么提供 | 引用源 | 许可 |
|---|---|---|---|---|
| 官方 QQNT（Windows，9.9.36 实测） | UIA 驱动的对象 | 安装并登录；启动时须带 `--force-renderer-accessibility` | <https://im.qq.com/> | 专有软件 |
| .NET 10 SDK | 编译 `qq-agent` / `qq-db` | 安装 | <https://dotnet.microsoft.com/download> | MIT |
| Node.js 20+ | 跑 headless 值班脑 | 安装 | <https://nodejs.org/> | MIT |
| **Yuno 2.5** 记忆系统 | 记忆服务 `127.0.0.1:8457` | `git clone` 到 `yuno/`，叠加 `yuno-overlay/` | <https://github.com/sty84/yuno-qq-bot> | MIT |
| `@deepseek-ai/dsh` | headless 值班脑 CLI（`brain/install.ps1` 安装，pin `0.2.1-rc.2`） | `brain/install.ps1` | <https://www.npmjs.com/package/@deepseek-ai/dsh> | 见上游 |
| DeepSeek API | 值班脑与声线的文本模型 | 自备 key（`Model.ApiKey`） | <https://platform.deepseek.com/> | 商业 API |
| 智谱 GLM-4.6V（可选） | 视觉兜底 `see`/`snap` | 自备 key（`Vision.ApiKey`） | <https://open.bigmodel.cn/> | 商业 API |
| **BAAI/bge-large-zh-v1.5** | 记忆与梗语料的向量化（1024 维，CPU） | 下载完整 sentence-transformers 目录到 `yuno/data/bge-large-zh-v1.5` | <https://huggingface.co/BAAI/bge-large-zh-v1.5> | MIT |
| NAudio 2.2.1 | 语音条回环播放（WASAPI） | NuGet 自动还原 | <https://github.com/naudio/NAudio> | MIT |
| Microsoft.Data.Sqlite + SQLitePCLRaw `bundle_e_sqlcipher` | 只读 QQ 本地加密库 | NuGet 自动还原 | <https://github.com/ericsink/SQLitePCL.raw> · <https://www.zetetic.net/sqlcipher/> | MIT / Apache-2.0 / BSD |
| Windows UI Automation | 读界面、发消息 | 系统自带 | <https://learn.microsoft.com/windows/win32/winauto/entry-uiauto-win32> | 系统 API |
| Grabacr07/VirtualDesktop | 虚拟桌面切换的 COM GUID（`Vd.cs` 只声明用到的最小 vtable） | 代码内嵌，未打包 | <https://github.com/Grabacr07/VirtualDesktop> | MIT |
| **CHIME** 中文梗语料（1458 条） | `qq-agent/data/meme-corpus/chime_full.json` | 随仓库分发 | <https://github.com/yuboxie/chime>（EMNLP 2025） | 见上游 |
| 中文维基「中国大陆网络用语列表」 | `wikipedia-memes.json` | 随仓库分发 | <https://zh.wikipedia.org/wiki/中国大陆网络用语列表> | CC BY-SA 4.0 |
| 萌娘百科 | `moegirl-*.json` | 随仓库分发 | <https://zh.moegirl.org.cn/> | CC BY-NC-SA 3.0 CN |

> 语料文件的版权归原出处，随仓库分发只为让「查梗」开箱可用；若权利人不同意，删掉
> `qq-agent/data/meme-corpus/` 下对应文件即可（`meme-corpus.py build` 会按现存文件重建索引）。

## 数据库与本地状态：本仓库不带任何 DB

仓库里**没有** `*.db`、模型权重、日志、记忆原文。下面这些都在本地生成或由别的组件维护：

| 路径 | 是什么 | 怎么得到 |
|---|---|---|
| `qq-agent/data/meme-index.db`（约 9 MB） | 梗语料的向量索引（sqlite，float32 原始字节） | `meme-corpus.py build` 生成；**不入库** |
| `yuno/data/persona-<包名>/bot.db` | Yuno 记忆库（SQLite；生产可切 PostgreSQL） | Yuno 首次启动自建；**含聊天原文与人物画像，隐私数据，永不入库** |
| `yuno/data/bge-large-zh-v1.5/` | 嵌入模型（约 1.2 GB） | 自行下载（见上表） |
| `yuno/data/hf_cache/` | 模型缓存 | 首次加载自动重建 |
| QQ 本地消息库（`Documents\Tencent Files\…`，SQLCipher 加密） | QQ 自己的聊天记录 | 由 QQ 维护；`src/QqDb` 只读，仓库不含 |
| `qq-agent/logs/` | 原文层、现场包、OCR 账本、截图、直播、ack | 运行时生成 |
| `qq-agent/brain/dsh-home/` | 值班脑的会话与 profile | `brain/install.ps1` + 首次运行生成 |
| `qq-agent/brain/node_modules/`（约 600 MB） | 值班脑 CLI | `brain/install.ps1` 重装 |
| `Sound/`、`bot-workspace/chattts-*` | 语音/翻唱成品与推理环境（数 GB） | 与值班链路无关，按需自备 |

## 快速开始

> 仅在 **Windows 10/11 x64** 上验证；需要已登录、已解锁的交互式桌面。

### 1. 前置

- 安装 **QQNT** 并登录（记住安装路径，默认 `C:\Program Files\Tencent\QQNT\QQ.exe`）
- 安装 **.NET 10 SDK**、**Node.js 20+**、**Python 3.11+**

### 2. 取代码

```powershell
git clone https://github.com/WWXTT/amy.git
cd amy
```

### 3. 起记忆服务（Yuno 上游 + 本项目 overlay）

```powershell
git clone https://github.com/sty84/yuno-qq-bot.git yuno
Copy-Item yuno-overlay\* yuno\ -Force          # /amy/* 入口、夜间成长、persona pack 切换

# 模型：把 BAAI/bge-large-zh-v1.5 的完整目录放到 yuno\data\bge-large-zh-v1.5
# 配置：复制 yuno\.env.example → yuno\.env，至少填 YUNO_API_TOKEN、DEEPSEEK_API_KEY

python -m venv yuno\.venv
yuno\.venv\Scripts\python.exe -m pip install -r yuno\requirements.txt

# 人设同步（persona.md → Yuno 的 persona pack）
powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\tools\sync-persona.ps1
```

### 4. 配置与数据

```powershell
Copy-Item qq-agent\config.example.json qq-agent\config.json
Copy-Item qq-agent\data\scope-map.example.json qq-agent\data\scope-map.json
Copy-Item qq-agent\data\roster-map.example.json qq-agent\data\roster-map.json
```

然后编辑 `qq-agent\config.json`：

- `Model.ApiKey`：DeepSeek key（同时作为值班脑的 `DEEPSEEK_API_KEY`）
- `Daemon.Chat`：值班会话标题，必须和 `data\scope-map.json` 里的键一致
- `Action.OwnerUids`：owner 的 QQ 号（本机命令只认 owner，留空则没人能用）
- 可选：`Vision.Enabled=true` + `Vision.ApiKey` 打开视觉兜底

`scope-map.json`（会话标题 → 群/好友）、`roster-map.json`（群名片 → QQ 号）都是本地数据，
仓库只给示例；`bot-workspace\normalize_roster.py` 可以从群里一次性生成名册。

### 5. 编译与安装值班脑

```powershell
dotnet build qq-agent\src\QqAgent\QqAgent.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\brain\install.ps1

# 梗索引（可选，但「查梗」需要；会调 bge 模型，1~3 分钟）
yuno\.venv\Scripts\python.exe qq-agent\tools\meme-corpus.py build
```

产物：`qq-agent\src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe`

### 6. 启动

```powershell
.\一键启动.bat
```

六步：配置/数据检查 → 编译 → 值班脑依赖 → Yuno（轮询 `/health` 最多 120 s）→
拉 QQ（带无障碍参数）→ daemon（新窗口「Amy 值班直播」）。跑完自动 `duty doctor`。

只想体检、什么都不启动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File qq-agent\start-all.ps1 -CheckOnly
```

### 7. 体检

```powershell
qq-agent\amy.cmd duty doctor
```

逐项检查：模式闸 / DSH CLI / 补丁 dump-config / 密钥 / Yuno `/health` / QQ 窗口 / daemon 心跳 /
pending-ack 水位 / 人设漂移 / 词库 / 会话堆积 / 双大脑。全绿再去群里发一条消息验证。

## 常用命令

包装脚本 `qq-agent\amy.cmd` 会自动追加 `--config qq-agent\config.json`，在**仓库根**执行：

| 命令 | 作用 |
|---|---|
| `duty doctor` | 全链体检 |
| `duty pack [--seq N] [--rebuild]` | 打印/重建现场包 |
| `duty say --intent "…"` | 只成文不发送（调试声线） |
| `duty chat --seq N --intent "…"` | 成文 → 守卫 → 核会话 → 发送 → 落库 → ack |
| `duty skip --seq N --why "…"` | 本轮沉默（写 ack） |
| `duty card --uid N` | 读某人印象卡 |
| `duty meme list\|add\|prune` | 人工梗库维护 |
| `duty state set --chat X --topic "…"` | 写现场状态（下一回合可见） |
| `duty stats [--last 20]` | 最近回合时延、守卫命中、记忆命中率 |
| `duty run/sendfile/sendimg` | 行动力：本机命令 / 发文件 / 发图片（分级见下） |

`qq-agent.exe` 常用子命令：`probe`（只读看会话与消息）、`sendq`（静默发文本）、
`readfwd`（读转发全文）、`ocrfwd`（QQ 内置 OCR 识别图片并按行回写账本）、`readimg`
（从 QQNT 图片缓存取原图）、`snap`/`see`（截图 / 视觉定位，调试用）、`status --watch`
（值班实时面板）、`daemon`（守护进程本体）。完整表见 [维护手册.md §4](维护手册.md)。

## 两种值班模式

同一时刻**只允许一个大脑**，由 `Daemon.NotifyOnly` 切换：

| 模式 | `NotifyOnly` | 行为 |
|---|---|---|
| 哨兵 | `true` | daemon 只检测、落库、投递 `logs\amy-pending.json`；不调模型、不发送、不 spawn。由外部会话（DSH 值班会话 / 人工）消费 pending 并 `duty chat` |
| 全自动 | `false` | daemon 在投递后自己 spawn headless 值班脑，脑跑完即退；正文仍由 `duty chat` 的 Writer 产出 |

`logs\amy.lock` 存脑的 node PID，跨 daemon 代际互斥；脑异常秒退会进 5 分钟冷却并告警。

## 安全边界

- **只操作 QQ 窗口内部**：不注入 DLL、不读协议、不改 QQ 文件；发送前核对会话标题，不一致直接中止。
- **认人只认 uid + `roster-map`**：OCR、图片识别出的内容不能反推发送者。
- **行动力分级**：本机命令只认 owner；群友只能用 `Action.GroupAllowedCommands` 白名单脚本、
  只能发 `Action.SendDirs` 白名单目录里的产物；危险片段（磁盘擦除、关机类等）硬拒。
  **不做运行时自拓展**：要加工具＝改代码 + 重新编译。
- **记忆原文只存不删**：清库走 `qq-agent\tools\purge-*.py`（默认 dry-run），需要显式确认。
- **密钥不进仓库**：`config.json`、`yuno\.env` 均在 `.gitignore` 里；仓库只提供 `*.example.*`。

## 已知限制

- 只在 Windows + 官方 QQNT 上工作；QQ 版本升级可能改动 UIA 结构（按钮名、面板层级）。
- 需要**已解锁的交互式桌面**：远程桌面断开、锁屏、虚拟桌面切换都会让 UIA 输入失效。
- 一次一个大脑：daemon 与值班会话同时回话会双回。
- 生图（SD WebUI）不在链路内，且已下线。
- 与别的机器人交互只能用 `sendat`（QQNT 不响应注入点击机器人卡片）。

## 许可与致谢

本项目以 **MIT** 发布，见 [LICENSE](LICENSE)。

- [**Yuno**](https://github.com/sty84/yuno-qq-bot) —— 长期记忆内核（七路检索、事件图、印象卡），MIT
- [**CHIME**](https://github.com/yuboxie/chime) —— 中文网络梗解释语料（EMNLP 2025）
- [中文维基「中国大陆网络用语列表」](https://zh.wikipedia.org/wiki/中国大陆网络用语列表)（CC BY-SA 4.0）、
  [萌娘百科](https://zh.moegirl.org.cn/)（CC BY-NC-SA 3.0 CN）—— 梗语料补充
- [NAudio](https://github.com/naudio/NAudio)（MIT）、[SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw)（Apache-2.0）、
  [SQLCipher](https://www.zetetic.net/sqlcipher/)（BSD）—— 音频与本地库读取
- [Grabacr07/VirtualDesktop](https://github.com/Grabacr07/VirtualDesktop)（MIT）—— 虚拟桌面 COM 适配
- [BAAI/bge-large-zh-v1.5](https://huggingface.co/BAAI/bge-large-zh-v1.5)（MIT）—— 中文向量化模型

> 名称与内容仅为技术演示：群里的人设、语料、行为阈值都由 `persona.md` 与 `duty.md` 决定，
> 使用前请自行确认所在群规与当地法规。
