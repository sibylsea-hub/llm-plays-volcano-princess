# Ludus hub（MCP 服务）

把游戏的工具包成一个 MCP 服务（Streamable HTTP，`http://127.0.0.1:8741/mcp`），谁连上来都用同一套：任何 MCP 客户端（只认 stdio 的经小桥，比如 Claude 桌面 app）、自己写的前端。只能读写文件的客户端走文字信箱。

现在只接了火山的女儿：每个游戏工具就是 `tools/vp_player.py` 的一条命令，hub 用子进程调它，输出原样当文字返回，再附上图和面板上的话。hub 不 import、不改 vp_player；vp_player 加了新命令，hub 不用改（走 `play`）。

各家客户端怎么接、局域网和外网、安全：**`docs/连接.md`**。工具和说明的原文：`docs/给agent的说明.md`。

## 文件

| 文件 | 是什么 |
|---|---|
| `hub.mjs` | 服务本体 |
| `tools.mjs` | 工具的定义和给 agent 的说明，hub 和小桥共用（加工具只改这里） |
| `stdio-bridge.mjs` | 给只认 stdio 的客户端（比如 Claude 桌面 app 的本机配置）用的小桥 |
| `start-hub.bat` | 双击就行（第一次会自己 `npm.cmd install`）；后面的参数原样传给 hub，比如 `start-hub.bat --lan` |
| `mailbox-README.txt` | 给文字信箱客户端的说明正本：hub 启动时填好名字和路径，写到 `chat-service\data\selfplay\README.txt` |
| `test\selftest.mjs` | 自测 |
| `data\`（不进 git） | `token`（局域网 / `--token` 模式用）、日志 |
| `cache\`（不进 git） | 缩小过的图，7 天清一次 |

## 起

要先开着聊天服务（`chat-service`，`say` 和面板上的话靠它）和游戏（游戏工具靠它，没开时工具会说游戏没开）。

```bat
start-hub.bat                 弹一个窗口，关窗口就停
node hub.mjs                  只听 127.0.0.1:8741，客户端不用带 token
node hub.mjs --lan            听局域网，所有请求都要 token（data\token）
node hub.mjs --token          只听本机，也要 token
node hub.mjs --log data\hub.log
```

平时用 `chat-service\start-server.bat`（带 `--hub`）顺手在后台起。参数的完整表见 `docs/连接.md` 第一节。看开着没有：`http://127.0.0.1:8741/health`。

## 几条规矩

- **一次调用最多 45 秒**（`LUDUS_HUB_CALL_LIMIT_MS`）：`wait` 传 `--timeout 40`（`LUDUS_HUB_WAIT_SECONDS`）；别的命令到 45 秒还没走完，先把已有的输出回过去，进程留在后台接着跑，下一次 `wait` 拿到后来的结果。一次只跑一个 vp_player，后来的排队。
- **图**：输出里出现 `selfplay\` 下的 PNG 时作为 MCP 的图片内容一起发。`shot`、`look` 的图都发；别的命令只发「第一次」那几行的：第一次见的人、第一次来的地方、CG、女儿的新样子、第一次遇到的怪物（这些行结尾都是「（图：…）」）。同一张图的原图和画了编号的只发一张，一次最多 8 张，大的用 FFmpeg 缩到长边 1568、转 JPEG。
- **面板上的话**：每次游戏工具回话，末尾带上人自上次以来在面板上说的话，每个 MCP 会话各记各的读到哪；刚连上时只带十分钟以内的。只在调用工具时捎过去：agent 停下来以后，面板上的话叫不醒它；人自己玩时 hub 不读插件写的台词包。能主动送进会话的只有 channel，见 `docs/连接.md`「几种接法差在哪」。
- **一个 MCP 会话就是一个新的「见过」**：会话里第一条动游戏的命令（或第一句 `say`）之前，hub 先替它跑 vp_player 的 `hello`。客户端断线重连也算新会话。
- **报到**：每个 MCP 会话向聊天服务报到「hub：客户端名」（`play hello 名字` 报过的是「hub：名字」），断开时报走了。经 stdio 小桥来的，客户端名是小桥报的：设了 `LUDUS_AGENT_NAME` 用它，没设用客户端自己报的（Claude 桌面 app 是 `claude-ai`）。hub 起的 vp_player 带 `LUDUS_PRESENCE=0`，不再自己报；带 `LUDUS_PLAYER`，按这个名字找自己的玩家文件夹。
- **同一时间只有一个在玩**：别人在玩时，动游戏的命令不跑，回「某某接手了，你先歇着」。只看不动的命令表和 vp_player 的 `READ_ONLY` 一样，两边一起改。
- `play` 里的参数直接当 argv 传给 vp_player，不经过 shell；不让写 `--data`。

## 文字信箱

给连不了 MCP、跑不了命令、只能读写这台电脑上文件的客户端：在 `chat-service\data\selfplay\mailbox\in\` 里写一个一行命令的小文件，回话出现在 `out\` 的同名文件里。细节见 `docs/连接.md` 第八节。同一个 data 文件夹上只该有一个 hub 看信箱（第二个 hub 带 `--no-mailbox`）。

## 测试

```bat
cd mcp-hub
npm.cmd install
node test\selftest.mjs
```

在临时文件夹里造一份 data、一个假插件（答 inbox 里的命令）、一个临时的聊天服务（就是 `chat-service\server.mjs`），再用 MCP SDK 的客户端连 hub：列工具、调各个工具、图、面板上的话、45 秒的限制（测试里缩短）、token、会话开头的 `hello`、stdio 小桥（也测 hub 没开时先开小桥、hub 后开）、文字信箱、交班、多个玩家文件夹、名字的环境变量；最后看聊天服务 `--hub` 会不会顺手起 hub。都用空闲端口，不碰 8740、8741，不碰真游戏。跑一遍一分多钟。缩小过的图会写进 `cache\`。
