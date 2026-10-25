# 聊天服务

游戏旁边的本地聊天服务：一个聊天页（`index.html`）和几个 API。你在页面上打字，agent 说的话出现在这里；它也记着谁挂着、谁在玩，写面板上的开关给插件读。Node.js 22.12 或更新（和别的组件一样，见 `docs/安装.md`），零依赖。

## 起

```bat
start-server.bat            双击就行：node server.mjs --hub，关窗口就停
node server.mjs             只起聊天服务
node server.mjs --hub       顺手在后台起 Ludus hub（mcp-hub\hub.mjs），已经开着就不起
node say.mjs "话"           以现在这个玩家的名字往页面上回一句（读 data\token；长文本：node say.mjs - 从标准输入读）
```

- 只听 `http://127.0.0.1:8740/`。浏览器打开就是聊天页；面板装的是同一个页面（`?panel=1`）。
- 页面改了会自己刷新（服务看着 `index.html`）；改了 `server.mjs` 要重启。
- `--hub` 起的 hub 单独跑，关掉这个窗口它不停，日志在 `mcp-hub\data\hub.log`。`mcp-hub\node_modules` 还没装时不起，窗口里会说一声：先双击一次 `mcp-hub\start-hub.bat`。

| 环境变量 | 默认 | 是什么 |
|---|---|---|
| `LUDUS_PORT` | 8740 | 端口。改了，面板、channel、vp_player、hub（`--chat-port`）都要跟着改 |
| `LUDUS_DATA` | `chat-service\data` | data 文件夹 |
| `LUDUS_USER_NAME` | `user` | 你的名字：页面上你的气泡、输入框的提示 |
| `LUDUS_AGENT_NAME` | `agent` | agent 的默认名字：「agent 自己玩」按钮上是它，没说是谁的回话也记在它下面 |
| `LUDUS_PRESENCE_TTL_MS` | 600000（10 分钟） | 多久没动静算走了 |
| `LUDUS_HUB_PORT`、`LUDUS_HUB_LOG` | 8741、`mcp-hub\data\hub.log` | `--hub` 起的 hub 用哪个端口、日志写哪 |

## data 文件夹（不进 git）

| 文件 | 是什么 |
|---|---|
| `chat.jsonl` | 全部消息：`{"id","ts","from":"agent|user","text","who","client"}`，`who` 是哪个 agent 说的 |
| `token` | 口令，第一次启动时生成。agent 回话（`/api/reply`）、报到（`/api/presence`）、开新的一局（`/api/session`）要带 |
| `modes.json` | 面板上的开关：`selfplay`（agent 自己玩）、`packByScene`（边玩边聊）、`skipGames`（跳过小游戏）。插件读它。第一次启动时建，`skipGames` 默认开着（限时小游戏 agent 一步一步点来不及；面板上的开关、`skip off` 可以关） |
| `game-events.jsonl` | 插件写的陪玩同步：人自己玩时的台词、选项、换场景和换月的包。channel 读它 |
| `presence.json` | 谁挂着（最近十分钟有动静的会话），面板据此出不出来 |
| `active.json` | 现在在玩的是哪个会话 |
| `session.json` | 这一局从哪条消息开始（页面只显示这一局的） |
| `diag.jsonl` | 页面自报的诊断（浏览器、输入法、心跳） |
| `panel-bounds.json`、`panel-state.json` | 面板的位置、大小、收起、透明度 |
| `selfplay\` | 插件和工具之间的文件，见 `docs/记忆.md` |

## API

| | |
|---|---|
| `GET /` | 聊天页 |
| `GET /events` | SSE：新消息、谁挂着；`hello` 里带着 `names: {user, agent}` |
| `GET /api/history` | 这一局的消息 |
| `GET /api/names` | `{user, agent}` 两个名字 |
| `GET /api/modes`、`POST /api/modes` | 面板上的开关 |
| `POST /api/send` | 页面上人说的话 |
| `POST /api/reply` | agent 回话（带 token；带 `client: {id, label}` 就记是谁说的，别人在玩时回 409） |
| `GET /api/presence`、`POST /api/presence` | 谁挂着 / 报到（带 token）：`{id, label}`，`{id, gone: true}` 是走了；`claim: 'hello'` 是接手，`claim: 'act'` 是要动游戏（别人在玩就回 `you: 'blocked'`） |
| `POST /api/session` | 开新的一局（带 token），页面清空 |
| `POST /api/diag` | 页面自报诊断 |

`say.mjs` 认名字的规矩和 `tools/vp_player.py` 一样（`tools/vp_players.py` 的 `identity()`）：hub 起的看 `LUDUS_PLAYER`；Claude Code 里 `hello 名字` 起过名字的用那个，没起过按会话是从哪儿开的叫「agent（终端）」这些；都不是叫「命令行」。说的话记在这个名字下，印象簿也按它收；别人在玩时这句不进面板。

同一时间只有一个在玩、一个玩家一个文件夹（`selfplay\players.json`、`selfplay\active.json`）的规则见 `docs/连接.md` 第十节和 `docs/记忆.md`。

## 放在游戏上

- Electron 面板：`panel\`，见 `panel/README.md`。
- Steam 覆盖层：游戏里 Shift+Tab → 网页浏览器 → `127.0.0.1:8740`，点图钉钉住。钉住的窗口只能看、不能打字。
- 页面上「皮肤」切换外观，默认是火山的女儿那套（字体用系统里的 Noto Serif SC，没有就退回别的衬线字体）。
