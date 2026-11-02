# qq-agent — UIA 驱动官方 QQNT 的传感器 + 执行器

不注入、不读协议、不改 QQ 文件：所有读写都落在**真实界面元素**上（Windows UI Automation）。

## 构建

```powershell
dotnet build qq-agent\src\QqAgent\QqAgent.csproj -c Release   # 仓库根执行
# 产物：qq-agent\src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe
```

`src/QqDb` 是只读 QQ 本地加密库（SQLCipher）的小工具，值班链路不依赖它。

## 运行前必须准备

| 文件 | 从哪来 |
|---|---|
| `config.json` | 复制 `config.example.json`，填 `Model.ApiKey`、`Daemon.Chat`、`Action.OwnerUids` |
| `data/scope-map.json` | 复制 `scope-map.example.json`：会话标题 → 群/好友 |
| `data/roster-map.json` | 复制 `roster-map.example.json`：群名片 → QQ 号（`duty roster add` 或 `bot-workspace/normalize_roster.py` 生成） |
| `data/meme-index.db` | `python tools/meme-corpus.py build`（**不入库**，约 9 MB） |
| `brain/node_modules/` | `powershell -File brain/install.ps1`（**不入库**，约 600 MB） |

## 入口

```bat
..\一键启动.bat            :: 六步启动（检查/编译/脑依赖/Yuno/QQ/daemon）
amy.cmd duty doctor        :: 全链体检
amy.cmd duty chat --seq N --intent "…"
src\QqAgent\bin\Release\net10.0-windows\qq-agent.exe probe      :: 只读看当前会话与消息
```

命令全集见 [维护手册.md §4](../维护手册.md)，每回合怎么值班见 [duty.md](../duty.md)，
机制见 [哨兵.md](../哨兵.md)。

## 布局

| 文件 | 作用 |
|---|---|
| `QqUi.cs` / `Native.cs` | UIA 树定位、焦点/输入、点击、窗口几何 |
| `Daemon.cs` | 事件监听、原文层、现场包投递、心跳、直播 |
| `Scene.cs` | 现场包 v2 构建（预算、记忆门控、梗命中） |
| `Duty.cs` | `duty` 子命令（chat/skip/card/meme/state/stats/run/…） |
| `Writer.cs` | 声线模块：唯一出正文 |
| `DshBrain.cs` | spawn headless 值班脑、超时、lock、冷却 |
| `Fwd.cs` / `ImgCache.cs` / `EmojiSender.cs` | 转发全文、识图取图、表情面板 |
| `Vision.cs` / `Vd.cs` | 视觉兜底（0–1000 归一化 bbox）、虚拟桌面 |
| `Integrations.cs` / `Journal.cs` / `Memes.cs` | Yuno 调用、日志与账本、梗库 |
