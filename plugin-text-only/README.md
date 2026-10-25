# 纯文字版插件（0.3.7）

只用文字的老版本，给用的模型看不了图、或者只想用文字玩的人：不会自动给图，`shot` 照样能截整屏。和 `plugin/`（截图版）二选一装：两版的 BepInEx GUID、dll 名字一样，装哪个就用哪个。

- `plugin/`：插件源码（`Plugin.cs`、`SelfPlay.cs`、`TextPlay.cs`、`MiniGames.cs`）和 `build.bat`。
- `tools/vp_player.py`：配这一版的命令行工具。别拿主线的 `tools/vp_player.py` 来配它。

## 能做什么

插件替 agent 往下翻对话、跳过小游戏（面板上「跳过小游戏」开着时，默认开着）、翻月中的过程页，停在要做决定的地方。agent 用文字命令玩：

- 看：`wait`、`look`、`acts`、`plan`、`wish`、`status`、`log`
- 按名字做事：`do`、`go`、`schedule`、`course`、`uncourse`、`yes`、`no`、`touch`、`pick`、`type`、`skip`
- 按编号（文字读不懂的画面）：`screen`、`click`、`choose`、`tip`、`slide`、`read`；`shot` 截一张整屏的图存成 `selfplay/shot.png`

全部命令：`python -X utf8 plugin-text-only\tools\vp_player.py -h`。

和截图版比，这一版**没有**：第一次见的截图和印象簿、回音、「上回说到」（`tools/vp_memory.py` 那一套）；战斗写成文字和交给队友（战斗、探险要按编号点）；家里的近况（`home`）、天赋（`talent`）；一个玩家一个文件夹、`hello` 交班。陪玩同步（人自己玩时把台词一段段发给 agent）两版都有。

## 怎么装、怎么玩

1. 游戏里装好 BepInEx 5，启动一次游戏到标题画面（`docs/安装.md` 第二节），再编。
2. 编、装进游戏：`plugin\build.bat`（cmd 里在 `plugin-text-only\` 下跑；PowerShell 里写 `.\plugin\build.bat`）。**编完默认直接装进游戏**，会换掉游戏里装着的截图版；只想编译设 `NOINSTALL=1`。设置和截图版一样（`GAME`、`CSC`、`DATA_DIR`、`OUT`、`NOINSTALL`、`BEPINEX_CORE`，也可以写进 `plugin\build.local.bat`），见 `plugin/README.md` 和 `docs/安装.md` 第三节。
3. 起聊天服务（`chat-service\start-server.bat`），面板上打开「agent 自己玩」。
4. agent 跑 `python -X utf8 plugin-text-only\tools\vp_player.py wait`。它默认用 `<仓库>\chat-service\data`，别的位置加 `--data`；聊天服务换了端口就设 `LUDUS_PORT`。

注意：

- **别打开「一个玩家一个文件夹」**（别建 `selfplay/players.json`，`docs/安装.md` 第六节的 `migrate_players.py --go` 别跑）。这一版的插件和工具只认 `selfplay/` 下那一份。
- **通过 hub 玩**：hub 默认调主线的 `tools/vp_player.py`，要让它调这一版：`node mcp-hub\hub.mjs --player plugin-text-only\tools\vp_player.py`（路径相对你起 hub 的文件夹，写绝对路径最稳）。这一版的工具没有 `hello`，hub 会跳过会话开头的清空。
- 人的名字：工具读 `LUDUS_USER_NAME`（默认 user），和别的组件一样。

## 和截图版差在哪

截图版在这一版之后加了：按钮清单去掉重复的说明、好感按等级写；换场景时截图、按钮编号画在图上；战斗当策略游戏玩、交给队友；第一次见的图和印象簿；家里的近况、天赋一步加；一个玩家一个文件夹；约会、横幅、升级写成文字。
