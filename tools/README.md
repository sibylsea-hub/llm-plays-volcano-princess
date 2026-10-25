# 玩的时候用的 Python 工具

只用标准库（`mark_shot.py` 除外），Python 3.10 或更新。在仓库根目录跑，加 `-X utf8`（Windows 的控制台默认不是 UTF-8）。

| 文件 | 是什么 |
|---|---|
| `vp_player.py` | agent 玩游戏的命令行工具：`wait`、`look`、`do`、`go`、`schedule`、`skill`、`shot`……每条命令写进插件的 inbox，等插件回话，整理成一屏好读的字打印出来。hub、channel 也是调它 |
| `vp_memory.py` | 印象簿、回音、「上回说到」、feedback。vp_player 在打印事件时调它，hub 在 vp_player 之外也直接调它的 `note`、`feedback`、`recap` |
| `vp_players.py` | 「我是谁、我的文件夹在哪」：一个玩家一个文件夹的那一套，几个工具共用；人和 agent 的名字（`LUDUS_USER_NAME`、`LUDUS_AGENT_NAME`）也在这里读 |
| `mark_shot.py` | 在截图上画按钮编号，存成 `NNNN-marked.png`。要 Pillow：脚本头写着依赖，vp_player 借 `uv run` 跑它，系统的 Python 不用装 Pillow |
| `migrate_players.py` | 打开「一个玩家一个文件夹」，把单文件夹布局下的东西搬进玩家文件夹 |

## vp_player.py

```
python -X utf8 tools\vp_player.py -h          全部命令和说明
python -X utf8 tools\vp_player.py wait        等到轮到 agent
python -X utf8 tools\vp_player.py --data 文件夹 status
```

- 默认用 `<仓库>\chat-service\data`，别的位置用 `--data`。
- 游戏要开着、装着插件；插件写的 `selfplay\state.json` 20 秒没更新就当游戏没开。动游戏的命令要面板上「agent 自己玩」开着。
- 每条命令顺手向聊天服务报到一次（`POST /api/presence`）：有 agent 挂着，游戏里的面板才出来，标题栏写着是谁。聊天服务没开也照常跑。`LUDUS_PORT` 换聊天服务的端口；`LUDUS_PRESENCE=0` 不报（hub 起的就是这样，hub 自己报）。
- 在 Claude Code 里跑时按 `CLAUDE_CODE_SESSION_ID` 认会话：新会话第一次动游戏之前自己补一个 `hello`（清空「见过的」、接手）。默认名字按 `CLAUDE_CODE_ENTRYPOINT` 写成「agent（终端）」「agent（桌面 app）」……；`hello 名字` 报过的用那个名字。不在 Claude Code 里是「命令行」。
- 同一时间只有一个在玩：别人在玩时，动游戏的命令不跑，打印谁接手了，退出码 3；只看不动的命令（`status`、`log`、`look`、`home`、`acts`、`plan`、`wish`、`screen`、`shot`、`tip`、`read`、`recap`、`note`、`feedback`）谁都能跑。这张表和 hub 的一样，两边一起改。
- 截图上画编号要 `uv` 在 PATH 上；没有就给原图，说一声没画上。

一个月的节奏：月初 `wait` → `go 家`（见女儿）→ `acts` 看行动力怎么花 → `do ……` → `schedule` 排五格 → `yes` → `wait`（过程页自动翻，对话选项停下来 `choose`）→ 换月。

## vp_memory.py

平时不用单独跑，vp_player 和 hub 会调它。想手动试：

```
python -X utf8 tools\vp_memory.py collect          把面板上 agent 说的话收进印象簿
python -X utf8 tools\vp_memory.py note 克莱尔 话
python -X utf8 tools\vp_memory.py recap [年]
python -X utf8 tools\vp_memory.py card 名字          look 名字 时印象簿那一页
```

存什么、怎么用，见 `docs/记忆.md`。

## migrate_players.py

```
python -X utf8 tools\migrate_players.py           只说要怎么搬，什么都不动
python -X utf8 tools\migrate_players.py --go      真搬
```

游戏关着时跑。只给图像版（`plugin/`）用：纯文字版（`plugin-text-only/`）不认玩家文件夹，别跑。新装的（data 文件夹是空的）直接 `--go`，只建 `selfplay\players.json` 和 `selfplay\active.json`。以前用单文件夹布局玩过的：`--current 名字` 写那个玩家以后报到时用的名字（`-h` 里有几种情况的写法）。只挪、只分，不删；搬过一次就不再搬。

## 名字

| 环境变量 | 默认 | 用在 |
|---|---|---|
| `LUDUS_USER_NAME` | `user` | `-h` 和打印的字里人的名字 |
| `LUDUS_AGENT_NAME` | `agent` | 没报名字时的默认玩家名 |
| `LUDUS_PLAYER`、`LUDUS_PLAYER_ID` | — | hub 起 vp_player 时带上，告诉它这一步是哪个玩家的 |
