# bot-workspace — 本地工作脚本目录

这个目录是**给你自己的脚本留的位置**，不在值班主链路上：

- `Action.GroupAllowedCommands` 里可以写 `bot-workspace|<python 脚本>`，让群友在限定范围内触发；
- `Action.SendDirs` 里可以放 `bot-workspace\archive` 之类，允许「发这个目录里的产物」。

仓库里只保留一个通用脚本：

| 脚本 | 作用 |
|---|---|
| `normalize_roster.py` | 把 Yuno 花名册（群名片 → QQ）按一份成员快照规范化，避免「按最后一次见到的名字当现名」的方向错误 |

## normalize_roster.py 的前置

1. 一份群成员快照 JSON（OneBot `get_group_member_list` 的返回，默认读 `roster_<群号>.json`）；
2. Yuno 记忆库 `yuno/data/persona-<包名>/bot.db`。

```powershell
python bot-workspace\normalize_roster.py --dry-run
```

> 语音/翻唱、生图（SD）等外挂能力不随本仓库分发：它们依赖数 GB 的本地模型与环境，
> 与值班链路解耦，需要时自行放置脚本并加进 `config.json` 的白名单。
