# 插件（截图版，0.4.11）

《火山的女儿》的 BepInEx 5 插件，让 agent 能用文字玩，第一次见到的人、地方、CG、怪物会截图给它看。游戏是 Unity 6、Mono 后端。

插件不连网，只读写聊天服务的 data 文件夹（默认 `chat-service/data/`）里的文件：

- **agent 自己玩**（面板上「agent 自己玩」开着）：插件替 agent 往下翻对话、跳过小游戏（「跳过小游戏」开着时，默认开着）、翻月中的过程页、点只有一个选择的按钮，停在要做决定的地方；agent 用 `tools/vp_player.py` 按名字下命令（`do 玩具积木.搭积木`、`go 家庭餐厅`、`skill 坚实一击 敌1`……）。
- **一起玩**（开关关着，人自己玩）：插件记下每句台词、每次选项和选择，一段段写进 `game-events.jsonl`，agent 那边（channel）就看得到。

装法见 `docs/安装.md` 第三节。

## 编

先在游戏里装好 BepInEx 5、启动一次游戏到标题画面，再编（`docs/安装.md` 第二、三节）。**`build.bat` 编完默认直接装进游戏**（复制到 `BepInEx\plugins\LudusLunae\`），只想编译设 `NOINSTALL=1`。

cmd：

```bat
set GAME=游戏文件夹
build.bat
```

PowerShell：

```powershell
$env:GAME = '游戏文件夹'
.\build.bat
```

| 设置 | 默认 | 是什么 |
|---|---|---|
| `GAME` | `C:\Program Files (x86)\Steam\steamapps\common\VolcanoPrincess` | 游戏文件夹，要先装好 BepInEx 5 |
| `CSC` | 用 vswhere 找 VS 2022 / Build Tools 里的 Roslyn `csc.exe` | C# 编译器 |
| `DATA_DIR` | `..\chat-service\data` | 编进插件的 `DataDir` 默认值 |
| `OUT` | 这个文件夹 | dll 放哪 |
| `NOINSTALL` | 不设（编完复制进游戏） | 设成 `1` 只编、不复制进游戏 |
| `BEPINEX_CORE` | `%GAME%\BepInEx\core` | BepInEx 的 `core` 文件夹（`BepInEx.dll`、`0Harmony.dll`）。游戏里还没装 BepInEx 时，指到单独解压的 BepInEx 5 的 `BepInEx\core`，先编起来；这时编完不往游戏里复制 |

可以写进旁边的 `build.local.bat`（不进 git），build.bat 每次先读它。编的时候用的是游戏自己的程序集（`VolcanoPrincess_Data\Managed\`）和 BepInEx 的 `core\`，仓库里不带这些。build.bat 会生成 `BuildInfo.g.cs`（只有 `DataDir` 的默认值），也不进 git。

编好的 `LudusLunae.VolcanoPrincess.dll` 默认直接复制到游戏的 `BepInEx\plugins\LudusLunae\`（游戏里还没装 BepInEx 时不复制，只说一声）。改了源码要重编、重开游戏。

## 配置

游戏第一次带着插件启动后，生成 `BepInEx\config\ludus-lunae.volcano-princess.cfg`：

- `DataDir`：聊天服务的 data 文件夹。
- 自己玩的两个节奏：每句台词停多久、画面上能点的东西多久不变才算轮到 agent。

日志在游戏的 `BepInEx\LogOutput.log`，开头一行「Ludus Lunae 已加载，数据文件夹：…」。

## 开关（`data/modes.json`，面板上切）

| 键 | 是什么 | 没写时 |
|---|---|---|
| `selfplay` | agent 自己玩 | 关（人在玩，开陪玩同步） |
| `skipGames` | 跳过小游戏，谁玩都能开关。默认开着：限时小游戏 agent 一步一步点来不及；面板上的开关、`skip off` 可以关 | 开（聊天服务新建 `modes.json` 时也写开着） |
| `packByScene` | 一起玩时一段一段地发（「边玩边聊」）；关了只在换月时发一包 | 开 |
| `autoFight` | 战斗交给队友。现在由 `vp_player auto on|once|off` 管，面板上要加开关就写这个键 | 关 |

插件每次用到时看文件改没改，改了才重读。

## 源码

| 文件 | 管什么 |
|---|---|
| `Plugin.cs` | 入口、配置、开关；陪玩同步（台词、选项、选择、换月、换场景的包） |
| `SelfPlay.cs` | 自己玩：读屏（找出鼠标真点得到的按钮，翻成中文说明）、点按钮、自动翻页和自动点、截图、读命令写回话 |
| `TextPlay.cs` | 文字化的「现在在哪、能做什么」：`look`、`acts`、`plan`、`wish`、`do`、`go`、`schedule`、约会…… |
| `Fight.cs` | 战斗写成文字、一步出手、交给队友打；探险里 `walk` 一直走 |
| `Sights.cs` | 第一次见：截图、`sight` 事件、印象簿条目、`look 名字` |
| `Home.cs` | 家里的近况（`home`） |
| `Talents.cs` | 天赋（`talent`） |
| `MiniGames.cs` | 跳过小游戏，跳过时挡成就 |
| `Players.cs` | 一个玩家一个文件夹：谁在玩、东西写进谁的文件夹 |

和工具之间的文件格式写在 `SelfPlay.cs`、`Sights.cs`、`Players.cs` 开头。每个挂钩都包在 try/catch 里：插件出错只写日志，不影响游戏。

## 插件不点的、会挡的

- 不点「结束游戏」和「打开网页」：清单里照常列出来、写清楚是什么，真点下去插件拒绝。别的（存档、读档、设置、成就同步）照常能点。
- 跳过小游戏的那一段时间里挡成就（本地也不记），事件里写「[挡下成就]」；正常玩出来的成就写「[成就]」。

## 跳过小游戏时给什么结果

在小游戏开始的那一刻截住，直接给完美玩家能拿到的结果，再接回游戏自己的结算：

- 骰子：直接赢（押默认的一半）。鸟节三局两胜也直接赢。
- 赛马：按自己的马第一个到终点结算。
- 舞会：选了舞伴直接把对方聊满（第一次见那位有剧情的舞伴照常玩）。
- 演戏：按三句台词全对结算。
- 鸟节画画：第一名。鸟节算术：全对。
- 钓鱼：冒出来的鱼全点到、垃圾不碰；鸟节钓鱼比赛同样算分。
- 不跳的：骏马选美（只有一次对话选项）、鸟节转盘（纯运气）、探险里的战斗（`skill` 自己打，或者 `auto on` 交给队友真打）、抓马（回合制，按钮都在清单里）。
- 跳过出错就退回游戏原来的玩法，事件里写「跳过失败，照常玩」。

## 战斗怎么玩（游戏的规则）

战斗是回合制的卡牌式玩法：能量、技能、敌人头顶亮出来的下一步。插件把这些写成文字，`skill 技能 敌N` 一步出手。

- **能量**：第一回合 2 点，以后每回合 +2，最多攒 6，用不完留到下回合。
- **出手**：每个队友一回合一次。同一招刚用过的几回合里贵一些。能量用完而且大家都出过手，游戏自己进敌方回合；不然 `endturn`。
- **防御**：1 能量一层，全队这回合受的伤 -25%，最多两层。每个人另有护盾，每回合补满，敌人先打护盾再掉血。
- **伤害**：武器属性（物理 / 魔法）对上敌人的弱点 ×1.2；飞的敌人，不是弓打的减半；暴击 ×1.5。
- **武器**：只有女儿能在战斗里换（不花能量、不算出手），技能跟着武器走。
- **输赢**：敌人全倒就赢，插件自己点「拿取全部」；女儿倒下或全队倒下就输，这趟探险结束。升级时学新技能，二选一的那几次停下来让 agent 挑。
- **交给队友**（`auto on|once|off`）：插件照规则真打，有输有赢。每回合在能量之内把出手和防御的组合算一遍，挑这回合得分加留下的能量最多的那组；打完一行交代结果，每场经过记进玩家文件夹的 `autofight.jsonl`。

## 纯文字版

只想用文字玩、或者模型看不了图：`plugin-text-only/`（0.3.7）。两版的 BepInEx GUID 一样，装一个就行。
