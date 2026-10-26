"""agent 自己玩火山的女儿：命令行工具。

先在聊天面板上打开「agent 自己玩」（data/modes.json 的 selfplay 为 true），游戏里装着 Ludus Lunae 插件。
插件会自己往下翻对话、跳过小游戏、翻月中的过程页；要 agent 做决定的时候（对话选项、做什么事、排日程、弹窗）就停下来等。
翻得多快是面板上「看剧情」的速度（chat-service/data/modes.json 的 readCps：每秒几个字，0 是自己翻，默认 12）。
你读台词不用等，这个速度是给 user 看的，照 user 读字的快慢来：user 常常自己点击、按空格往下翻，是嫌慢；说没读完，是太快。
user 可以自己在聊天面板里调，也可以由你动手改 readCps，插件马上就照新的翻。
user 在游戏里自己点了按钮、翻了台词，会多一行「[user 点了] …」「[user 自己翻了 5 句台词]」：
画面变了是 user 动的，不是自己跳的。

用法：python -X utf8 tools/vp_player.py <命令> ...

新会话开头：
  hello                   告诉插件换了一个会话：这之后见到的人、地方、CG、女儿、怪物都当第一次见，各截一张。
                          也是「我来玩了」：同一时间只有一个会话在玩，hello 就是接手，之前在玩的那个歇着。
                          在 Claude Code 里跑时，新会话第一条动游戏的命令之前会自己发（看 CLAUDE_CODE_SESSION_ID），手动跑也行。
                          被别人接手以后，动游戏的命令和 say 不跑，只说一句谁接手了；要接回来就再 hello。
                          只看不动的命令（status、look、log、acts……）谁都能跑，不接手

看：
  wait [--timeout S]      等到轮到 agent：状态、上次以来的台词和事件、现在能做什么、对话选项或按钮清单
  look                    现在在哪、开着什么窗口、这里每个物体能做什么（行动力、金币、加什么、本月还能几次）、能去哪
  look 名字               一个人或一个地方（也可以是女儿、怪物、CG）：游戏里的介绍、最近一张图、印象簿里你说过的话。
                          想看就看，不用理由。没见过的会说还没见过
  home（或 近况）         家里的近况：卧室左下角那排菜单（资料、心情、好感、干劲、天赋、心愿、课程、骑士、朋友、衣橱、背包）
                          合成一张，一次看完。只是看看；天赋点只提醒还剩几点，加哪一项还是自己打开「天赋」一点一点选
  acts                    本月所有地点能做的事（月初看一眼，行动力怎么花）
  plan                    日程：已排了什么、每门课花多少、每节加什么、进度、心情，能约谁出去玩
  wish                    女儿的心愿（幼年是爸爸的许诺：要做的事和进度）
  status                  现在轮到谁、状态
  log [-n N]              最近 N 条事件（默认 30）

第一次见：这个会话里第一次见到的人、地方、CG、女儿的新样子、怪物和首领，插件各截一张（缩到长边 1568，
一张约 1850 token），在 wait / click 的输出里单独一行，比如「[第一次见] 克莱尔（图：…png；user 那边也看得到）」，
路径直接 Read 就能看，想看就看；同一段对话里一下子来了好几个新面孔，合成一行「[第一次见] 地点：A、B、C」。

印象簿（tools/vp_memory.py）：你在面板上说到谁、哪里的话会收进印象簿，再见到时带一句出来；换年时有一份「上回说到」。
  note 名字 话            想记一句就记，下回见到时会带出来
  feedback 话             玩着觉得哪里别扭、哪里好玩，随时说，user 会看，也会改
  recap [年]              上回说到：这一年（或者指定的春火年 / 夏火年 / 秋火年 / 冬火年）发生了什么

按名字做事（推荐）：
  do 物体.活动            比如 do 玩具积木.搭积木、do 家庭餐厅.主食.家庭套餐、do 莉莉娅（说话）、
                          do 本森.聊天（夏火年起说完话有个菜单：聊天、请教、挑战、赠送、约会，look / acts 里写着每个人能选哪几样；
                          春火年和村民只能打个招呼。许诺里的「和镇上的人聊天」算的是路人，look / acts 里标着「算许诺」）、
                          do 探险 / do 烹饪（场景里的按钮）。
                          不在这里会先 go 过去，离得远会先走过去，点开物体再选活动，最后打印结果和状态变化
  go 地点                 去地图上的地点（go 家 = 月初在爸爸房间时「见女儿」）
  schedule 课 课 ...      排日程：从空的开始依次排（出去玩写人名，比如 伍柏），最多 5 格。排满会弹确认框，再 yes 开始这个月
  course 课名             往日程里加一格；uncourse [--all] 撤掉最后一格（或全清）
  yes / no                回答确认框（回复里写着在确认什么）
  touch 物体 / pick 活动  do 的两半：点开物体 / 在开着的面板里选活动（菜）
  talent [名字]           天赋：不写名字是看每一项（几级、下一级加什么、锁着的那层为什么锁、还有几点）；
                          写名字是加一级，比如 talent 精力充沛：一步做完，不弹确认框；加不了直接说为什么（锁着、满级、没点数）。
                          天赋窗口开着的话，加完会关上。加哪一项是你自己挑，一次一级
  type 文字               往输入框里打字（建角色时取名）
  skip on|off             跳过小游戏开关（面板上那个，默认开着）。开着时限时、靠手速的小游戏（算术、画画点 tag、钓鱼）
                          直接拿到最好的结果，想自己一步一步玩就 skip off；跳了哪个，聊天时顺口提一句就好

战斗（wait / look 在战斗里写全场：每个人的 HP、护盾、攻击、出过手没有、技能和现在的耗费、
每招打每个敌人多少，能量，敌人的 HP、弱点、会不会飞、下一步要干什么）：
  fight                   只看战斗这一屏
  skill 技能 [敌N|名字]   一步出手：选技能、点敌人一起做完。回话写打了多少、敌人还剩多少、谁还能出手、剩几点能量；
                          用不了直接说为什么。好几个人有同名技能写 人名.技能（比如 本森.会心之箭）；打全体、加血、加护盾的不用写目标。
                          女儿的技能在另一把武器上时会先换武器（不花能量）
  defend                  防御：1 能量，全队这回合受的伤 -25%，最多两层
  weapon 武器|弓|另一把   女儿换武器（不花能量、不算出手），打印换上以后能用的技能
  endturn                 结束回合，剩下的能量留到下回合（最多攒 6）；等敌方回合打完，打印敌人干了什么和新的一屏
  auto on|once|off        交给队友：插件照规则自己打（看能量、挑弱点、血少了防御），有输有赢。on 以后每场都交，
                          once 只交这一场（还没开打就是下一场），off 收回（打到一半也行）。打完一行交代结果，
                          每场的经过记在 data/selfplay/autofight.jsonl。不写 on/off 是看现在交没交
  打赢了插件自己点「拿取全部」，拿到的记成 [得到]；输了自己点「结束」。学新技能（二选一）留给你 click

探险：
  walk [金色的路|蓝色的路]   一直往前走，到岔路、随机事件、泉水宝箱、战斗、首领前一格停下，打印停在哪、下一格是什么。
                          停在岔路时写上路名，就是选这条路接着走。战斗交给队友（auto on）时，打完接着走

按编号（通用，什么画面都能用）：
  screen [--all]          重新扫一遍画面，打印编号按钮清单（带悬停提示）；--all 连没字的图标也列出来
  screen --debug          调试：每个按钮的屏幕坐标，没列出来的写原因（被什么挡住、不在屏幕上）
  shot                    截一张现在的游戏画面。想看就看，不用理由：想看看谁长什么样、这里好不好看、女儿穿上新衣服怎么样，都行。
                          按钮上画好清单编号（存 data/selfplay/scenes/NNNN-marked.png，原图是 NNNN.png），
                          同时发一版新清单；文字读不懂的画面也可以截来对着清单看。
                          换场景时插件自己会截一张，路径写在「换场景」那行后面。
                          user 自己玩（一起玩）、或者别人在玩的时候也能 shot：只有画面，缩到长边 1568，
                          不画编号、不发清单，也不算接手
  click N|字 [--quick]    点清单里的第 N 个（用最近一次 wait/screen 的清单）；也可以写按钮上的字或功能，比如 click 见女儿。
                          点完等画面稳定，打印台词、事件、状态变化和现在能做什么（--quick 不等）
  choose N|字 [--quick]   选第 N 个对话选项，或者写选项里的字；同样等结果
  tip N|字                把鼠标移到这个按钮上，看悬停提示
  slide N 值              把第 N 个滑动条拖到 0~1 之间的位置（买几个、用几个）
  read                    画面上所有的字

其他：
  say 话                  发到聊天面板（user 看得到）
选项：--data DIR  聊天服务的 data 文件夹（默认 chat-service/data）

一个月大概这样过：月初 wait → go 家（见女儿）→ acts 看这个月能去哪、做什么 → do …… → schedule 排五格 → yes →
wait（过程页自动翻，遇到对话选项停下来 choose）→ 换月。行动力留不到下个月；花行动力的是屋里和地图上的物体，
上课不花行动力，但每节课心情 -5（幼年）或 -10。
文件：data/selfplay/ 下 state.json（插件写）、inbox.jsonl（这里写命令）、outbox.jsonl（插件回复）、
events.jsonl（插件写的台词和事件）、cursor.json（这里记 wait 读到哪了、上次 hello 的会话）、scenes/（截图和截图那一刻的清单，
NNNN-marked.png 是这里画好编号的）、impressions.json（印象簿：第一次见的人和地方，插件建条目，tools/vp_memory.py 收话）。
格式见 plugin/SelfPlay.cs、plugin/Sights.cs 开头。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # 同一个文件夹里的 vp_players、vp_memory（python -I 时不会自己加）
import vp_players  # noqa: E402  谁的是谁的：玩家文件夹（players.json 在才开）

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DATA = ROOT / "chat-service" / "data"
SP_DIR = DEFAULT_DATA / "selfplay"  # main() 按 --data 改；sight 事件里的图是相对这里的路径
STALE_SECONDS = 20  # state.json 这么久没更新，就当游戏没开
PORT = int(os.environ.get("LUDUS_PORT") or 8740)  # 聊天服务的端口
# 没字、没提示的这些按钮在 wait 里收起来（screen --all 看全部）：顶栏图标、说明按钮
NOISE_KINDS = {"图标", "说明", "说明（tip 看）", "提示（tip 看）", "按钮", "爸爸房间提示", "enterIcon", "showDes",
               "摆设 / 说明（点了会说一句话）"}
MARKER = Path(__file__).resolve().with_name("mark_shot.py")  # 在截图上画编号，要 Pillow，借 uv 跑


class Game:
    def __init__(self, data: Path):
        self.data = data
        self.root = vp_players.root(data)   # 共用的：state.json（游戏现在的样子）
        self.dir = vp_players.my_dir(data)  # 我自己的：inbox、outbox、events、cursor、scenes、印象簿（没开玩家文件夹时就是 root）
        self.root.mkdir(parents=True, exist_ok=True)

    # ---- 文件 ----
    def state(self) -> dict | None:
        path = self.root / "state.json"
        for _ in range(5):
            try:
                return json.loads(path.read_text(encoding="utf-8"))
            except FileNotFoundError:
                return None
            except (OSError, ValueError):
                time.sleep(0.05)  # 插件正在换文件
        return None

    @staticmethod
    def age(state: dict) -> float:
        try:
            return (datetime.now() - datetime.fromisoformat(state["ts"])).total_seconds()
        except (KeyError, ValueError):
            return 1e9

    def cursor(self) -> dict:
        try:
            return json.loads((self.dir / "cursor.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    def save_cursor(self, **changes) -> None:
        cur = self.cursor()
        cur.update(changes)
        self.dir.mkdir(parents=True, exist_ok=True)
        (self.dir / "cursor.json").write_text(json.dumps(cur), encoding="utf-8")

    def read_events(self, offset: int) -> tuple[list[dict], int]:
        path = self.dir / "events.jsonl"
        if not path.exists():
            return [], 0
        if path.stat().st_size < offset:
            offset = 0
        with open(path, "rb") as f:
            f.seek(offset)
            data = f.read()
        end = data.rfind(b"\n")
        if end < 0:
            return [], offset
        events = []
        for raw in data[:end].splitlines():
            try:
                events.append(json.loads(raw.decode("utf-8")))
            except (ValueError, UnicodeDecodeError):
                pass
        return events, offset + end + 1

    # ---- 命令 ----
    def request(self, cmd: str, timeout: float = 15, **fields) -> dict:
        # 开了玩家文件夹以后写进我自己的 inbox。插件每个玩家的 inbox 都看着：在玩的那一个照常执行；
        # 别人、没人在玩时只回答只看不动的（look、acts……）和 hello，动游戏的回一句现在在玩的是谁（插件 Players.cs）
        rid = uuid.uuid4().hex[:12]
        self.dir.mkdir(parents=True, exist_ok=True)
        out = self.dir / "outbox.jsonl"
        offset = out.stat().st_size if out.exists() else 0
        entry = {"id": rid, "cmd": cmd, **fields}
        with open(self.dir / "inbox.jsonl", "a", encoding="utf-8", newline="\n") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if out.exists() and out.stat().st_size > offset:
                with open(out, "rb") as f:
                    f.seek(offset)
                    data = f.read()
                end = data.rfind(b"\n")
                if end >= 0:
                    for raw in data[:end].splitlines():
                        try:
                            m = json.loads(raw.decode("utf-8"))
                        except (ValueError, UnicodeDecodeError):
                            continue
                        if m.get("id") == rid:
                            return m
                    offset += end + 1
            time.sleep(0.1)
        st = self.state()
        why = ("游戏没开、插件没加载，或者游戏停在后台（游戏窗口没焦点、也没有会话挂着时，游戏自己会暂停；点一下游戏窗口就接着走）"
               if st is None or self.age(st) > STALE_SECONDS else "插件没回话")
        return {"ok": False, "text": f"游戏 {timeout:.0f} 秒没有回应：{why}。"}

    def wait_turn(self, timeout: float = 600) -> dict | None:
        """等到轮到 agent（phase 是 choose 或 click）。超时返回 None。"""
        deadline = time.monotonic() + timeout
        noted = ""
        by_hand = False  # 这段时间里插件在等人翻页（看剧情设成了自己翻）
        while True:
            st = self.state()
            note = ""
            if st and "等人" in (st.get("why") or ""):
                by_hand = True
            if st is None or self.age(st) > STALE_SECONDS:
                note = "游戏没开，或者插件没加载，等着……"
            elif st.get("phase") == "off":
                note = "selfplay 关着（面板上打开「agent 自己玩」），等着……"
            elif st.get("phase") in ("choose", "click"):
                return st
            if note and note != noted:
                print(note, file=sys.stderr)
                noted = note
            if time.monotonic() > deadline:
                if by_hand:
                    print(f"== 现在：等了 {timeout:.0f} 秒还没轮到你：{vp_players.named('在等 user 翻页')}（看剧情设成了自己翻），不是卡住了，再 wait ==")
                else:
                    print(f"== 现在：等了 {timeout:.0f} 秒还没轮到你（{(st or {}).get('phase')}：{(st or {}).get('why')}），再 wait ==")
                return None
            time.sleep(0.3)

    def new_events(self) -> list[dict]:
        cur = self.cursor()
        events, offset = self.read_events(cur.get("events", 0))
        self.save_cursor(events=offset)
        return events


# ---- 打印 ----
def fmt_status(s: dict | None) -> str:
    if not s:
        return "（没有状态）"
    if "error" in s:
        return f"（还没进存档：{s['error']}）"
    nature = "  ".join(f"{k} {v}" for k, v in (s.get("nature") or {}).items())
    attri = "  ".join(f"{k} {v}" for k, v in (s.get("inAttri") or {}).items())
    favor = favor_text(s) if s.get("favorLevel") is not None else s.get("favor")
    text = (
        f"{s.get('dauName')}（爸爸 {s.get('faName')}）  {s.get('year')} {s.get('month')}月（第 {s.get('turn')} 回合）\n"
        f"金币 {s.get('coin')}  行动力 {s.get('energy')}/{s.get('maxEnergy')}  心情 {s.get('mood')}  声望 {s.get('fame')}  "
        f"好感 {favor}  黑暗 {s.get('darkness')}  感悟 {s.get('inspiration')}  爱慕者 {s.get('lover')}  身高 {s.get('height')}\n"
        f"素质：{nature}\n属性：{attri}"
    )
    extra = []
    if s.get("talentPoint"):
        extra.append(f"天赋点 {s['talentPoint']} 没用（女儿房间左下角「天赋」）")
    if s.get("yearCourseGoal"):
        extra.append(f"学年目标：学完 {s.get('yearCourse')}/{s['yearCourseGoal']} 门课")
    if isinstance(s.get("mood"), int) and s["mood"] < 50:
        extra.append("心情低于 50：属性表现 -50%")
    if s.get("states"):
        extra.append("状态：" + "、".join(s["states"]))
    return text + ("\n" + "；".join(extra) if extra else "")


def favor_text(s: dict) -> str:
    """好感按级写，和女儿房间里一样：Lv.4（4/200），满级写 Lv.6（已满级）。favor 只是这一级里攒的点数。"""
    nxt = s.get("favorNext")
    return f"Lv.{s.get('favorLevel')}（" + (f"{s.get('favor')}/{nxt}" if nxt else "已满级") + "）"


STAT_NAMES = [("energy", "行动力"), ("coin", "金币"), ("mood", "心情"), ("favor", "好感"), ("fame", "声望"),
              ("darkness", "黑暗"), ("inspiration", "感悟"), ("lover", "爱慕者"), ("talentPoint", "天赋点"),
              ("yearCourse", "学年已学完")]


def fmt_diff(a: dict | None, b: dict | None) -> str:
    """两份状态之间变了什么，比如「行动力 10→7（-3），智力 543→558（+15）」。"""
    if not a or not b or "error" in a or "error" in b:
        return ""
    parts = []

    def add(name, x, y):
        if isinstance(x, (int, float)) and isinstance(y, (int, float)) and x != y:
            d = y - x
            parts.append(f"{name} {x}→{y}（{'+' if d > 0 else ''}{round(d, 1)}）")

    for key, name in STAT_NAMES:
        # 升了级 favor 会从头攒，96→4 看着像掉了，其实是 Lv.3（96/100）→Lv.4（4/200）
        if key == "favor" and None not in (a.get("favorLevel"), b.get("favorLevel")) and a["favorLevel"] != b["favorLevel"]:
            parts.append(f"好感 {favor_text(a)}→{favor_text(b)}")
            continue
        add(name, a.get(key), b.get(key))
    for group in ("nature", "inAttri"):
        for k, v in (b.get(group) or {}).items():
            add(k, (a.get(group) or {}).get(k), v)
    # 心情跌破 50 时属性表现减半，数字看着像掉了一半，容易当成出了毛病：说一句是怎么回事
    low = lambda s: any("心情低于" in str(x) for x in (s.get("states") or []))
    note = ""
    if parts and low(b) and not low(a):
        note = "（心情跌破 50，属性表现减半；心情回到 50 以上就恢复）"
    elif parts and low(a) and not low(b):
        note = "（心情回到 50 以上，属性表现恢复了）"
    return "，".join(parts) + note


def noise(it: dict) -> bool:
    return not it.get("label") and not it.get("tip") and it.get("kind", "") in NOISE_KINDS


def fmt_items(items: list[dict], show_all: bool = True) -> str:
    lines = []
    hidden = 0
    for it in items:
        if not show_all and noise(it):
            hidden += 1
            continue
        label = it.get("label") or ""
        head = f"「{label}」" if label else ""
        line = f"{it['n']:>3}. {head}{it.get('kind', '')}"
        if it.get("off"):
            line += "（灰）"
        if it.get("note"):
            line += f"【{it['note']}】"
        if it.get("tip"):
            line += f"\n       提示：{it['tip']}"
        lines.append(line)
    if hidden:
        lines.append(f"     （另有 {hidden} 个没字的图标、说明按钮，screen --all 看）")
    return "\n".join(lines) if lines else "（没有）"


def marked_path(png: Path) -> Path:
    return png.with_name(png.stem + "-marked.png")


def wait_file(path: Path, timeout: float = 3) -> bool:
    """插件的截图要等那一帧画完才写出来：等文件出现、大小不再变。"""
    deadline = time.monotonic() + timeout
    last = -1
    while time.monotonic() < deadline:
        size = path.stat().st_size if path.exists() else -1
        if size > 0 and size == last:
            return True
        last = size
        time.sleep(0.15)
    return path.exists()


def mark_shots(pngs: list[Path]) -> str:
    """在截图上画清单编号（mark_shot.py 写 NNNN-marked.png），画过的跳过。画不上时返回原因，原图照样能看。"""
    todo = [p for p in pngs if wait_file(p) and p.with_suffix(".json").exists() and not marked_path(p).exists()]
    if not todo:
        return ""
    try:
        r = subprocess.run(["uv", "run", "--quiet", str(MARKER), *map(str, todo)], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=180, env={**os.environ, "PYTHONUTF8": "1"})
    except (OSError, subprocess.SubprocessError) as e:
        return f"没画上编号：uv 跑不起来（{e}）"
    if r.returncode != 0:
        lines = (r.stderr or r.stdout or "").strip().splitlines()
        return "没画上编号：" + (lines[-1] if lines else f"mark_shot.py 退出码 {r.returncode}")
    return ""


def shot_text(e: dict) -> str:
    """一张截图怎么写：画好编号的那张的路径；编号对不上现在的清单时说一声。"""
    png = Path(e.get("path") or "")
    if marked_path(png).exists():
        text = str(marked_path(png))
    elif png.is_file():
        text = f"{png}（{e.get('_why') or '没画编号'}）"
    else:
        return "这张没截下来"
    if e.get("_stale"):
        text += f"；图上的编号是第 {e.get('snap')} 版清单的，清单已经换了，按编号点之前先 screen"
    return text


def link_shots(events: list[dict], g: "Game | None" = None) -> None:
    """换场景后停稳时插件截一张图（shot 事件），接到前面那条换场景后面打成一行。给了 g 时顺便画编号、对一下清单版本。"""
    scene = None
    shots = []
    for e in events:
        if e.get("type") == "scene":
            scene = e
        elif e.get("type") == "shot":
            shots.append(e)
            if scene is not None and scene.get("to") == e.get("place"):
                scene["_shot"] = e
                e["_merged"] = True
            scene = None
    if not shots or g is None:
        return
    why = mark_shots([Path(e["path"]) for e in shots if e.get("path")])
    st = g.state() or {}
    for e in shots:
        if why:
            e["_why"] = why
        if st.get("phase") == "click" and st.get("snap") not in (None, e.get("snap")):
            e["_stale"] = True


PORTRAIT = {"date": "约会", "dance": "舞会", "lord": "选领主"}


def sight_image(e: dict) -> Path | None:
    image = e.get("image") or ""
    return (SP_DIR / image) if image else None


def sight_head(e: dict) -> str:
    """一条 sight 的开头，比如「[第一次见] 克莱尔」「[露丝换上了 白孔雀礼服]」「[第一次来] 家庭餐厅」。"""
    what, name, why = e.get("what"), e.get("name") or "", e.get("why") or ""
    if what == "face":
        return f"[大立绘] {name}（{PORTRAIT[why]}）" if why in PORTRAIT else f"[第一次见] {name}"
    if what == "crowd":
        return f"[第一次见] {name}"
    if what == "place":
        return f"[换了季节] {name}（{e.get('year')}的样子）" if why == "season" else f"[第一次来] {name}"
    if what == "cg":
        return "[CG]" + (f" {e['note']}" if e.get("note") else "")
    if what == "dau":
        dau, cloth, year = e.get("dau") or name.split("（")[0], e.get("cloth") or "", e.get("year") or ""
        if why == "grow":
            return f"[{dau}长大了] {year}" + (f"，穿着{cloth}" if cloth else "")
        if why == "costume":
            return f"[{dau}换上了 {cloth}]"
        return f"[{dau}] {year}" + (f"，穿着{cloth}" if cloth else "")
    if what in ("monster", "boss"):
        return ("[第一次遇到首领] " if what == "boss" else "[第一次遇到] ") + name + ("（精英）" if e.get("note") == "精英" else "")
    return f"[第一次见] {name}"


def sight_note(e: dict) -> str:
    """跟在 sight 下面的一行：游戏自带的介绍（人随关系露出来的那几段、探险地图的说明）、一群人是怎么回事。"""
    note = e.get("note") or ""
    if not note or e.get("what") in ("cg", "monster", "boss"):
        return ""
    return ("    " if e.get("what") == "crowd" else "    介绍：") + note


def fmt_sights(group: list[dict]) -> str:
    """同一张图的几条 sight 打成一行（到家时的家和露丝、一场战斗里的几种新怪物），后面跟图的路径。"""
    heads, monsters, bosses = [], [], []
    for e in group:
        if e.get("what") in ("monster", "boss"):
            (bosses if e["what"] == "boss" else monsters).append((e.get("name") or "") + ("（精英）" if e.get("note") == "精英" else ""))
            continue
        heads.append(sight_head(e))
    if monsters:
        heads.append("[第一次遇到] " + "、".join(monsters))
    if bosses:
        heads.append("[第一次遇到首领] " + "、".join(bosses))
    png = sight_image(group[0])
    if png is not None and png.is_file():
        tail = f"（图：{png}；{vp_players.named('user 那边也看得到')}）"
    elif group[0].get("image"):
        # 只说这台电脑上找不到，不说没截下来：可能还没写完，也可能这段代码跑在别的机器上（图在开游戏的那台电脑上）
        tail = f"（这台电脑上找不到 {group[0]['image']}：可能还没截好；一直没有的话，看 BepInEx 日志）"
    else:
        tail = "（这张图没截下来，看 BepInEx 日志）"
    lines = [" ／ ".join(heads) + tail]
    lines += [n for n in map(sight_note, group) if n]
    return "\n".join(lines)


def group_sights(events: list[dict], g: "Game | None" = None) -> None:
    """连着的、同一张图的 sight 合成一组（第一条带上 _group，其余标 _merged）。给了 g 时等图写出来（插件这一帧画完才写）。"""
    prev = None
    for e in events:
        if e.get("type") != "sight":
            prev = None
            continue
        if prev is not None and prev.get("image") == e.get("image"):
            prev["_group"].append(e)
            e["_merged"] = True
            continue
        e["_group"] = [e]
        prev = e
        png = sight_image(e)
        if g is not None and png is not None and not png.exists():
            wait_file(png, 3)


def group_human(events: list[dict]) -> None:
    """user 连着点同一个按钮（自己一页页翻过程页）合成一行：第一条记上几次，后面的标 _merged。
    user 选的对话选项，插件接着还会记一条「[选了]」，和「[user 选了]」是同一件事，标 _merged 不再打。"""
    prev = None
    for e in events:
        if e.get("type") == "chose" and prev is not None and prev.get("did") == "choose" and prev.get("label") == e.get("text"):
            e["_merged"] = True
            prev = None
            continue
        if e.get("type") != "human" or e.get("did") not in ("click", "choose"):
            prev = None
            continue
        if prev is not None and (e.get("did"), e.get("label")) == (prev.get("did"), prev.get("label")):
            prev["_times"] = prev.get("_times", 1) + 1
            e["_merged"] = True
            continue
        prev = e


def fmt_human(e: dict) -> str | None:
    """user 用鼠标、键盘在游戏里动的（不是你点的）。"""
    if e.get("_merged"):
        return None
    did, times = e.get("did"), e.get("_times", 1)
    more = f"（连着 {times} 次）" if times > 1 else ""
    if did == "click":
        n = f"{e['n']} 号 " if e.get("n") else ""
        return f"{vp_players.named('[user 点了]')} {n}{e.get('label')}{more}"
    if did == "choose":
        return f"{vp_players.named('[user 选了]')} {e.get('label')}{more}"
    if did == "lines":
        return vp_players.named(f"[user 自己翻了 {e.get('count')} 句台词]")
    if did == "skip":
        return vp_players.named(f"[user 按住 Ctrl 快进了 {e.get('count')} 句台词]")
    return None


def fmt_event(e: dict) -> str | None:
    t = e.get("type")
    if t == "human":
        return fmt_human(e)
    if t == "sight":
        return None if e.get("_merged") else fmt_sights(e.get("_group") or [e])
    if t == "opened":
        return f"[地图上新开了] {e.get('name')}" + (f"（{e['gain']}）" if e.get("gain") else "")
    if t == "line":
        say = (e.get("say") or "").replace("\n", "\n    ")
        return f"{e.get('who')}：{say}"
    if t == "options":
        what = f"（{e['what']}）" if e.get("what") else ""
        return f"[选项{what}] " + " / ".join(e.get("options") or [])
    if t == "chose":
        return None if e.get("_merged") else f"[选了] {e.get('text')}"
    if t in ("click", "choose"):
        return f"> {'点了' if t == 'click' else '选了'} {e.get('n')} 号 {e.get('label')}"
    if t in ("month", "load"):
        head = "== 换月 ==" if t == "month" else "== 读档 =="
        text = head + "\n" + fmt_status(e.get("state"))
        if e.get("_diff"):
            text += f"\n[上个月以来] {e['_diff']}"
        return text
    if t == "levelup":  # 升级、学会新技能
        return f"[升级] {e.get('text')}"
    if t == "band":  # 屏幕中间的横幅（约会时对方喜不喜欢、舞会上的反应）
        return f"[横幅] {e.get('text')}"
    if t == "celebrate":
        return "[庆祝] " + (e.get("text") or "").replace("\n", " ｜ ")
    if t == "month-result":
        return "[本月结算] " + (e.get("text") or "").replace("\n", " ｜ ")
    if t == "type":
        return f"> 打字：{e.get('text')}"
    if t == "item":
        if e.get("name") == "金币":  # 战利品里的金币（插件「拿取全部」时记）
            return f"[得到] 金币 {e.get('num')}"
        return f"[得到] {e.get('name')} ×{e.get('num')}"
    if t == "fight":
        what, text = e.get("what"), e.get("text") or ""
        head = {"start": "== 战斗：", "turn": "[敌方回合] ", "end": "[战斗结束] ", "auto": "[交给队友] "}.get(what, "[战斗] ")
        return head + text + (" ==" if what == "start" else "")
    if t == "walk":
        return f"[停下] {e.get('text')}"
    if t == "story":
        return "[这一页] " + (e.get("text") or "").replace("\n", " ｜ ")
    if t == "scene":
        line = f"== 换场景：{e.get('from')} → {e.get('to')} =="
        return line + (f"（画面：{shot_text(e['_shot'])}）" if e.get("_shot") else "")
    if t == "shot":
        # 到了以后先播了对话、选了选项，图是之后停稳时截的，没和换场景那行打在一起
        return None if e.get("_merged") else f"[画面] {e.get('place')}：{shot_text(e)}"
    if t == "note":
        return f"[注意] {e.get('text')}"
    if t == "skip":
        return f"[跳过小游戏] {e.get('game')}：{e.get('result')}"
    if t == "skip-not":
        return f"[没跳] {e.get('game')}：{e.get('why')}"
    if t == "skip-failed":
        return f"[跳过失败，照常玩] {e.get('game')}：{e.get('error')}"
    if t == "achievement":
        return f"[成就] {e.get('name')}（{e.get('id')}）"
    if t == "achievement-blocked":
        return f"[挡下成就] {e.get('name')}（{e.get('id')}）：跳小游戏得来的，不点亮"
    if t == "selfplay":
        return "[selfplay 打开了]"
    return None


def memory(g: "Game", fn: str, *args):
    """印象簿那一半（vp_memory.py）：收话、回音、上回说到、note、feedback。出错不打断游戏，返回 None。"""
    try:
        import vp_memory
        return getattr(vp_memory, fn)(g.data, *args)
    except Exception as e:  # noqa: BLE001  印象簿坏了也要能接着玩
        print(f"（印象簿出了点问题：{e}）", file=sys.stderr)
        return None


def print_events(events: list[dict], g: "Game | None" = None) -> None:
    """打印事件。给了 g 时，换月那条附上和上一次换月（或读档）比变了多少，基准记在 cursor.json；
    换场景的截图画上编号（log 不给 g，只写已经画好的）。
    给了 g 时还接上印象簿：打印前收面板上新说的话，打印后带一句回音；换年了再给一份「上回说到」。"""
    base = g.cursor().get("month_state") if g else None
    ended_years = []  # 换月换过去的年份：给「上回说到」。读档换了年不算：读的可能是另一局，算进去会把两局的女儿混成一年
    if g:
        memory(g, "collect")
    link_shots(events, g)
    group_sights(events, g)
    group_human(events)
    for e in events:
        if e.get("type") in ("month", "load"):
            st = e.get("state")
            if e.get("type") == "month" and base:
                e["_diff"] = fmt_diff(base, st)
                y0, y1 = base.get("year"), (st or {}).get("year")
                if y0 and y1 and y0 != y1 and y0 not in ended_years:
                    ended_years.append(y0)
            base = st
        text = fmt_event(e)
        if text:
            print(text)
    if g and base:
        g.save_cursor(month_state=base)
    if g and events:
        line = memory(g, "echo", events)
        if line:
            print(line)
        for year in ended_years:
            text = memory(g, "recap", year)
            if text:
                print(text)


def print_turn(st: dict, show_all: bool = False) -> None:
    phase = st.get("phase")
    if phase == "choose":
        print("== 现在：对话选项（choose N 或 choose 选项里的字）==")
        print(fmt_items(st.get("menu") or []))
    elif phase == "click":
        if st.get("view"):
            print("== 现在 ==")
            print(st["view"])
        print(f"== 按钮（click N，第 {st.get('snap')} 版清单）==")
        print(fmt_items(st.get("items") or [], show_all))
        if st.get("hidden"):
            print(hidden_note(st))
    else:
        print(f"== 现在：{phase}（{st.get('why')}）==")


def hidden_note(st: dict) -> str:
    kinds = st.get("hiddenKinds") or []
    what = "、".join(kinds) if kinds else "结束游戏之类"
    return f"（另有 {st['hidden']} 个按钮插件不点：{what}）"


def resolve(g: Game, arg: str, menu: bool = False) -> tuple[int | None, int]:
    """把「编号或按钮上的字」翻成编号：先找按钮上的字，再找功能说明。找不到或对上好几个就打印出来，返回 None。"""
    if arg.isdigit():
        return int(arg), g.cursor().get("snap", 0)
    st = g.state() or {}
    items = st.get("menu" if menu else "items") or []
    snap = st.get("snap", 0)
    if not items and not menu:
        r = g.request("screen", timeout=30)
        items, snap = r.get("items") or [], r.get("snap", 0)
    def parts(it, field):
        return [x.strip() for x in (it.get(field) or "").replace("|", "/").split("/") if x.strip()]

    for field in ("label", "kind", "tip"):
        tiers = [
            [it for it in items if (it.get(field) or "") == arg],
            [it for it in items if arg in parts(it, field)],
            [it for it in items if any(x.startswith(arg) for x in parts(it, field))],
            [it for it in items if arg in (it.get(field) or "")],
        ]
        for hits in tiers:
            if len(hits) > 1:
                usable = [it for it in hits if not it.get("off")]
                if len(usable) == 1:
                    hits = usable
            if len(hits) == 1:
                g.save_cursor(snap=snap)
                return hits[0]["n"], snap
            if len(hits) > 1:
                print(f"「{arg}」对得上好几个，写编号：")
                print(fmt_items(hits))
                return None, snap
    print(f"清单里没有「{arg}」。")
    print(fmt_items(items, False))
    return None, snap


def settle(g: Game, before: dict | None = None, timeout: float = 600) -> dict | None:
    """等到又轮到 agent，打印这段时间的台词和事件、状态变化、现在能做什么（有文字说明时不打按钮清单，要看用 screen）。
    长剧情（两三分钟）也等得住；等超时了也把已经发生的打出来。"""
    st = g.wait_turn(timeout)
    events = g.new_events()
    print("-- 上次以来 --")
    if events:
        print_events(events, g)
    else:
        print("（这段时间没有新的台词）")
    if st is None:
        return None
    loads = [e["state"] for e in events if e.get("type") == "load" and isinstance(e.get("state"), dict)]
    if loads:  # 读了档：从读档那一刻比（不然比的是标题画面、或者另一局的状态，满屏都是变化）
        before = loads[-1]
    diff = fmt_diff(before, st.get("status"))
    if diff:
        print(f"[变化] {diff}")
    if st.get("phase") == "click" and st.get("view") and st.get("viewKind") != "window":
        print("== 现在 ==")
        print(st["view"])
        print(f"（按钮清单：screen；第 {st.get('snap')} 版）")
    else:
        print_turn(st)
    g.save_cursor(snap=st.get("snap", g.cursor().get("snap", 0)))
    return st


def simple(g: Game, cmd: str, **fields) -> int:
    r = g.request(cmd, **fields)
    print(r.get("text"))
    return 0 if r.get("ok") else 1


def status_now(g: Game) -> dict | None:
    st = g.state()
    return (st or {}).get("status")


# ---- 命令 ----
def cmd_status(g: Game, args) -> int:
    st = g.state()
    if st is None:
        print("还没有 state.json：游戏没开过，或者插件没加载。")
        return 1
    age = g.age(st)
    if age > STALE_SECONDS:
        print(f"state.json {age:.0f} 秒没更新：游戏没开，或者插件没加载。最后一次是：{st.get('phase')}（{st.get('why')}）")
        return 1
    print(f"现在：{st.get('phase')}（{st.get('why')}）")
    print(fmt_status(st.get("status")))
    return 0


def cmd_wait(g: Game, args) -> int:
    st = g.wait_turn(args.timeout)
    if st is None:
        return 2
    events = g.new_events()
    print(fmt_status(st.get("status")))
    print("-- 上次以来 --")
    if events:
        print_events(events, g)
    else:
        print("（这段时间没有新的台词；前一条命令里已经有的不再重复，log 能翻回去）")
    print_turn(st)
    g.save_cursor(snap=st.get("snap", g.cursor().get("snap", 0)))
    return 0


def cmd_screen(g: Game, args) -> int:
    if args.debug:
        return simple(g, "debug")
    r = g.request("screen", timeout=30)
    print(r.get("text"))
    if not r.get("ok"):
        return 1
    if r.get("view"):
        print(r["view"])
    print(fmt_items(r.get("items") or [], args.all))
    if r.get("hidden"):
        print(hidden_note(r))
    g.save_cursor(snap=r.get("snap", 0))
    return 0


def cmd_click(g: Game, args) -> int:
    before = status_now(g)
    n, snap = resolve(g, " ".join(args.n))
    if n is None:
        return 1
    r = g.request("click", n=n, snap=snap)
    print(r.get("text"))
    if r.get("items"):
        print(fmt_items(r["items"]))
        g.save_cursor(snap=r.get("snap", 0))
    if not r.get("ok"):
        return 1
    return 0 if args.quick or settle(g, before) else 1


def cmd_choose(g: Game, args) -> int:
    st = g.state() or {}
    if not st.get("menu") and st.get("items") and not " ".join(args.n).isdigit():
        # 女儿的烦恼、课上的随机事件这类选项是按钮，不是对话选项，在对话选项里找不到：当成 click
        print("（现在没有对话选项，这些选项是按钮，照 click 点）")
        return cmd_click(g, args)
    before = status_now(g)
    n, _ = resolve(g, " ".join(args.n), menu=True)
    if n is None:
        return 1
    r = g.request("choose", n=n)
    print(r.get("text"))
    if not r.get("ok"):
        return 1
    return 0 if args.quick or settle(g, before) else 1


def cmd_tip(g: Game, args) -> int:
    n, snap = resolve(g, " ".join(args.n))
    if n is None:
        return 1
    r = g.request("tip", n=n, snap=snap)
    print(r.get("text"))
    if r.get("items"):
        print(fmt_items(r["items"]))
        g.save_cursor(snap=r.get("snap", 0))
    return 0 if r.get("ok") else 1


def cmd_slide(g: Game, args) -> int:
    r = g.request("slide", n=args.n, text=str(args.value), snap=g.cursor().get("snap", 0))
    print(r.get("text"))
    if r.get("items"):
        print(fmt_items(r["items"]))
        g.save_cursor(snap=r.get("snap", 0))
    return 0 if r.get("ok") else 1


def cmd_shot(g: Game, args) -> int:
    legacy = g.dir / "shot.png"  # 回话里没给 path 时退回到这一张（每次覆盖，没有清单）
    before = legacy.stat().st_mtime if legacy.exists() else 0
    r = g.request("shot", timeout=30)
    if not r.get("ok"):
        print(r.get("text"))
        return 1
    png = Path(r.get("path") or legacy)
    deadline = time.monotonic() + 5
    while not (png.exists() and (png != legacy or png.stat().st_mtime != before)):
        if time.monotonic() > deadline:
            print("截图没写出来（游戏这一帧没画？）")
            return 1
        time.sleep(0.1)
    wait_file(png)
    if png == legacy:
        print(png)
        return 0
    if r.get("look"):  # 一起玩、别人在玩时只截图，没有清单，不画编号
        print(f"画面：{png}")
        return 0
    why = mark_shots([png])
    if marked_path(png).exists():
        print(f"画面：{marked_path(png)}（编号是第 {r.get('snap')} 版清单）")
        print(f"原图：{png}")
    else:
        print(f"画面：{png}（{why or '没画编号'}）")
    if r.get("view"):
        print(r["view"])
    print(fmt_items(r.get("items") or []))  # 图上每个编号都画了，清单也全列，好对着看
    g.save_cursor(snap=r.get("snap", 0))
    return 0


def cmd_text(g: Game, args) -> int:
    """look / acts / plan / read：插件直接给文字。"""
    return simple(g, args.command)


def live(g: Game) -> bool:
    st = g.state()
    return st is not None and g.age(st) <= STALE_SECONDS


def load_impressions(g: Game) -> dict:
    try:
        book = json.loads((g.dir / "impressions.json").read_text(encoding="utf-8"))
        return book if isinstance(book, dict) else {}
    except (OSError, ValueError):
        return {}


WHAT_ZH = {"face": "人", "crowd": "一群人", "place": "地方", "cg": "CG", "dau": "女儿", "monster": "怪物", "boss": "首领"}


def cmd_look(g: Game, args) -> int:
    """不带名字：现在在哪（插件给文字）。带名字：一个人或地方的介绍、最近一张图、印象簿里说过的话。"""
    name = " ".join(args.name).strip()
    if not name:
        return simple(g, "look")
    book = load_impressions(g)

    def hit(n: str) -> bool:
        return n == name

    def near(n: str) -> bool:  # 露丝（夏火年，白孔雀礼服）、中央广场：A、B、C、冰霜峡谷（探险）
        return name in n and n != name

    entries = [(k, v) for k, v in book.items() if isinstance(v, dict) and hit(v.get("name") or k.split(":", 1)[-1])]
    entries += [(k, v) for k, v in book.items() if isinstance(v, dict) and (k, v) not in entries
                and near(v.get("name") or k.split(":", 1)[-1])]
    events, _ = g.read_events(0)
    sights = [e for e in events if e.get("type") == "sight" and (hit(e.get("name") or "") or near(e.get("name") or ""))]
    if not entries and not sights:
        # 这一趟还没截过图，但游戏里认识的人（home 的「朋友」里列着、叫你 look 名字的那些）照给资料（插件回 met）
        r = g.request("lookname", text=name, timeout=8) if live(g) else {}
        if r.get("ok") and r.get("met"):
            print(f"== {name} ==")
            print(r.get("text"))
            print("（这一趟还没见到本人，没有图；下次说上话时会截一张）")
            return 0
        print(f"还没见过「{name}」。")
        return 0
    print(f"== {name} ==")
    r = g.request("lookname", text=name, timeout=8) if live(g) else {}
    game_info = bool(r.get("ok") and r.get("what"))
    if game_info:
        print(r.get("text"))  # 游戏里写着的：关系、好感、资料里露出来的介绍
    latest = sights[-1] if sights else None
    png = sight_image(latest) if latest else None
    if (png is None or not png.is_file()) and entries:
        img = entries[-1][1].get("image")
        png = SP_DIR / img if img else None
    if png is not None and png.is_file():
        print(f"最近一张图：{png}" + (f"（{latest.get('ts', '')[:16].replace('T', ' ')}）" if latest else ""))
    # 印象簿那一页交给 vp_memory：第一次见、介绍、你说过的话（多了挑几句，带上是什么时候说的）
    memory(g, "collect")
    page = memory(g, "card", name)
    if page is not None:
        print(page)
        return 0
    # vp_memory 用不了时，退回来自己列
    if not game_info:
        notes = [v.get("note") for _, v in entries if v.get("note")]
        if notes:
            print("介绍：" + notes[0])
    if entries:
        first = entries[0][1]
        when = first.get("when") or (first.get("first") or "")[:10]
        kinds = "、".join(dict.fromkeys(WHAT_ZH.get(v.get("what"), v.get("what") or "") for _, v in entries))
        print(f"第一次见：{when}（{kinds}）" if when else f"（{kinds}）")
        if len(entries) > 1:
            print("印象簿里有：" + "、".join(v.get("name") or k for k, v in entries))
    words = [(w, v.get("name") or k) for k, v in entries for w in (v.get("words") or []) if isinstance(w, dict) and w.get("text")]
    if words:
        print("你说过的：")
        for w, who in words:
            src = "自己记的" if w.get("from") == "note" else "面板上说的"
            print(f"  · {w['text']}（{(w.get('ts') or '')[:10]}，{src}" + (f"，说的是{who}" if who != name else "") + "）")
    return 0


def cmd_home(g: Game, args) -> int:
    return simple(g, "home")


def cmd_talent(g: Game, args) -> int:
    """talent：看每一项；talent 名字：加一级（插件一步做完，加完把天赋窗口关上）。插件不认识这个命令时，说怎么手动点。"""
    name = " ".join(args.name).strip()
    r = g.request("talent", text=name)
    text = r.get("text") or ""
    if text.startswith("不认识的命令"):
        text = "游戏里的插件还是旧版，还没有 talent。现在只能 click 天赋 打开窗口、click 格子、yes，加完 click 点击空白处关闭。"
    print(text)
    return 0 if r.get("ok") else 1


def cmd_note(g: Game, args) -> int:
    if len(args.text) < 2:
        print("用法：note 名字 话", file=sys.stderr)
        return 2
    out = memory(g, "note", args.text[0], " ".join(args.text[1:]))
    print(out if out is not None else "（印象簿这会儿记不下，稍后再试）")
    return 0 if out is not None else 1


def cmd_feedback(g: Game, args) -> int:
    out = memory(g, "feedback", " ".join(args.text))
    print(out if out is not None else vp_players.named("（这会儿记不下，可以先在面板上跟 user 说）"))
    return 0 if out is not None else 1


def cmd_recap(g: Game, args) -> int:
    out = memory(g, "recap", " ".join(args.year).strip() or None)
    print(out if out is not None else "（这会儿翻不出上回说到）")
    return 0 if out is not None else 1


def hello(g: Game) -> dict:
    r = g.request("hello", timeout=8)
    sid = os.environ.get("CLAUDE_CODE_SESSION_ID")
    if r.get("ok") and sid:
        # hello 过的会话都记着：被接手的会话回来时不再自己补 hello（那等于悄悄抢回去），要接回来得自己 hello
        sessions = [s for s in g.cursor().get("sessions", []) if s != sid][-19:] + [sid]
        g.save_cursor(session=sid, sessions=sessions)
    return r


def cmd_hello(g: Game, args) -> int:
    r = hello(g)
    print(r.get("text"))
    if not r.get("ok"):
        return 1
    if vp_players.names_note():
        print(f"（{vp_players.names_note()}）")
    st = g.state() or {}
    if st.get("status"):
        print(fmt_status(st["status"]))
    print("（wait 接着玩）")
    return 0


def presence_client(g: Game) -> dict:
    """这个会话在聊天服务那边叫什么（也是玩家名）：id 按 Claude Code 的会话分（和同一个会话里的 channel 一样），
    名字是 hello 名字 报过的那个，没报过按会话是从哪儿开的写；不在 Claude Code 里跑就是「命令行」。见 vp_players.identity。"""
    return vp_players.identity(g.data)


def report_presence(g: Game) -> threading.Thread | None:
    """每条命令向聊天服务报到一次：有会话挂着，游戏里的面板才出来。
    在后台线程里报，不拖慢命令：聊天服务没开时，Windows 上连接要等一秒左右才被拒绝。
    聊天服务没开、没 token 都不管。hub 跑的不报（LUDUS_PRESENCE=0，hub 自己报）。"""
    if os.environ.get("LUDUS_PRESENCE") == "0":
        return None

    def post() -> None:
        try:
            token = (g.data / "token").read_text(encoding="utf-8").strip()
            req = urllib.request.Request(
                f"http://127.0.0.1:{PORT}/api/presence",
                data=json.dumps(presence_client(g)).encode("utf-8"),
                headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"},
                method="POST",
            )
            with urllib.request.urlopen(req, timeout=1):
                pass
        except Exception:
            pass

    t = threading.Thread(target=post, daemon=True)
    t.start()
    return t


def needs_hello(g: Game) -> bool:
    """在 Claude Code 里跑、这个会话还没 hello 过（新会话）"""
    sid = os.environ.get("CLAUDE_CODE_SESSION_ID")
    if not sid:
        return False
    cur = g.cursor()
    return sid != cur.get("session") and sid not in cur.get("sessions", [])


def auto_hello(g: Game, command: str) -> None:
    """在 Claude Code 里跑：新会话第一条动游戏的命令之前，先替它 hello 一次（只读的命令不补）。
    游戏没开时不发（下次再说），免得每条命令都等着超时。"""
    if command == "hello" or not needs_hello(g) or not live(g):
        return
    r = hello(g)
    if r.get("ok"):
        print(f"（{r.get('text')}）")
        if vp_players.names_note():
            print(f"（{vp_players.names_note()}）")


# ---- 同一时间只有一个会话在玩：聊天服务记着现在在玩的是谁 ----
# 只看不动游戏的命令：不接手，别人在玩时也照跑（维护的会话偶尔看一眼 status，不能把在玩的挤下去）。
# 别的命令都算动游戏：先问聊天服务，别人在玩就不跑，只说一句交班的话。say 由聊天服务挡（不进面板）。
READ_ONLY = {"status", "log", "look", "home", "近况", "acts", "plan", "wish", "screen", "shot", "tip", "read",
             "recap", "note", "feedback"}


def claim_turn(g: Game, kind: str) -> dict | None:
    """接手（hello）或者要动游戏（act）之前问聊天服务。回 { you: active|blocked|idle, active, handedOver }。
    聊天服务没开、没 token：返回 None，照常玩（最多等半秒）。"""
    try:
        token = (g.data / "token").read_text(encoding="utf-8").strip()
        req = urllib.request.Request(
            f"http://127.0.0.1:{PORT}/api/presence",
            data=json.dumps({**presence_client(g), "claim": kind}).encode("utf-8"),
            headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=0.5) as res:
            return json.loads(res.read().decode("utf-8"))
    except Exception:
        return None


def handover_text(turn: dict) -> str:
    who = (turn.get("active") or {}).get("label") or "别人"
    if turn.get("handedOver"):
        return f"（{who}接手了，你先歇着。要接回来就 hello）"
    return f"（{who}正在玩，你先歇着。要接手就 hello）"


def go_to(g: Game, place: str) -> bool:
    """go 地点，等到了为止。在爸爸房间时先「见女儿」回家，再去别处。"""
    for _ in range(3):
        r = g.request("go", text=place)
        print(r.get("text"))
        if not r.get("ok"):
            return False
        if r.get("text", "").startswith("已经在"):
            return True
        st = g.wait_turn(60)
        print_events(g.new_events(), g)
        if st is None:
            return False
        if st.get("phase") == "choose":
            print("（路上弹出了对话选项，先 choose）")
            print_turn(st)
            return False
        if not r.get("again"):
            return True
    return True


def cmd_go(g: Game, args) -> int:
    before = status_now(g)
    if not go_to(g, " ".join(args.place)):
        return 1
    st = g.state() or {}
    diff = fmt_diff(before, st.get("status"))
    if diff:
        print(f"[变化] {diff}")
    print_turn(st)
    g.save_cursor(snap=st.get("snap", 0))
    return 0


def cmd_do(g: Game, args) -> int:
    target = " ".join(args.target)
    before = status_now(g)
    found = g.request("find", text=target)
    option = ""
    if not found.get("ok") and "." in target:
        # do 本森.聊天：说话的人后面跟的是菜单里的选项，不是活动
        head, option = target.rsplit(".", 1)
        f2 = g.request("find", text=head)
        if f2.get("ok") and f2.get("kind") == "talk":
            found = f2
        else:
            option = ""
    if not found.get("ok"):
        print(found.get("text"))
        return 1
    obj, cell = found["obj"], found.get("cell") or ""
    name = f"{found['placeName']}.{obj}" + (f".{cell}" if cell else "")
    if not found.get("can", True):
        print(f"{name} 现在做不了：{found.get('why')}")
        return 1
    print(f"== do {name} ==")
    if not found.get("here"):
        if not go_to(g, found["placeName"]):
            return 1
    r = g.request("touch", text=obj)
    print(r.get("text"))
    if not r.get("ok"):
        return 1
    st = g.wait_turn(600)
    if st is None:
        return 1
    if option:
        if st.get("phase") != "choose":
            return 0 if settle(g, before) else 1
        n, _ = resolve(g, option, menu=True)
        if n is None:
            return 1
        r = g.request("choose", n=n)
        print(r.get("text"))
        if not r.get("ok"):
            return 1
        return 0 if settle(g, before) else 1
    if cell:
        if st.get("phase") == "choose":
            print_events(g.new_events(), g)
            print("（点开时弹出了对话选项，先 choose，再 do 一次）")
            print_turn(st)
            return 1
        r = g.request("pick", text=cell)
        print(r.get("text"))
        if not r.get("ok"):
            print_events(g.new_events(), g)
            print_turn(st)
            return 1
    return 0 if settle(g, before) else 1


def cmd_touch(g: Game, args) -> int:
    before = status_now(g)
    r = g.request("touch", text=" ".join(args.name))
    print(r.get("text"))
    if not r.get("ok"):
        return 1
    return 0 if settle(g, before) else 1


def cmd_pick(g: Game, args) -> int:
    before = status_now(g)
    r = g.request("pick", text=" ".join(args.name))
    print(r.get("text"))
    if not r.get("ok"):
        return 1
    return 0 if settle(g, before) else 1


def cmd_course(g: Game, args) -> int:
    r = g.request("course", text=" ".join(args.name))
    print(r.get("text"))
    return 0 if r.get("ok") else 1


def cmd_uncourse(g: Game, args) -> int:
    return simple(g, "uncourse", n=1 if args.all else 0)


def cmd_schedule(g: Game, args) -> int:
    """从空的日程开始，依次排；排满弹确认框后停下来，等 yes。"""
    text = g.request("plan").get("text", "")
    if "要在家里" in text or "日程窗口没开着" in text:
        st = g.state() or {}
        if "在家" not in ((st.get("view") or "")[:6]):
            look = g.request("look").get("text", "")
            if "在：家" not in look:
                print("（先回家排日程）")
                if not go_to(g, "家"):
                    return 1
    if "确认框：" in text:
        r = g.request("no")  # 上次排满了还没确认：清空重排
        g.wait_turn(10)
    elif "已排 0/" not in text and "日程窗口没开着" not in text:
        r = g.request("uncourse", n=1)
    else:
        r = {"ok": True}
    if not r.get("ok"):
        print(r.get("text"))
        return 1
    op = g.request("planopen")
    if not op.get("ok"):
        print(op.get("text"))
        return 1
    time.sleep(0.6)  # 日程窗口刚打开时会重建课程格子，等它稳下来再点（不等的话第一格会丢）
    last, skipped, failed = None, [], []
    m = re.search(r"已排 (\d+)/", g.request("plan").get("text", ""))
    count = int(m.group(1)) if m else None
    for name in args.names:
        for attempt in range(2):
            last = g.request("course", text=name)
            head = (last.get("text") or "").split("\n", 1)[0]
            if last.get("ok"):
                # 回读格数：说排上了、格数却没涨，就是被吞了，再排一次
                if count is not None and isinstance(last.get("count"), int) and last["count"] <= count and attempt == 0:
                    time.sleep(0.4)
                    continue
                break
            if "学完" in head or "没有叫" in head or "排不了" in head or "约过" in head or "约不了" in head:
                break
            time.sleep(0.4)
        print(head)
        if isinstance(last.get("count"), int):
            count = last["count"]
        if not last.get("ok"):
            (skipped if ("学完" in head or "约过" in head or "约不了" in head) else failed).append(f"{name}（{head}）")
            if "5 格" in head or "排满" in head:
                break
        time.sleep(0.15)
    plan = g.request("plan").get("text", "")
    print(plan)
    if skipped:
        print("跳过了：" + "；".join(skipped))
    if failed:
        print("没排上：" + "；".join(failed))
    if last and last.get("sure"):
        energy = ((g.state() or {}).get("status") or {}).get("energy")
        if isinstance(energy, int) and energy > 0:  # 行动力留不到下个月：还没花完就开始这个月，提醒一句
            print(f"→ 排满了。行动力还剩 {energy} 点，yes 以后这个月就开始，剩下的留不到下个月；还想先做事就 no（日程清空，做完再排）。")
        else:
            print("→ 排满了。yes 开始这个月，no 清空重排。")
    elif "已排 5/" not in plan:
        print("→ 还没排满 5 格：再 course 课名 补上。")
    return 0 if not failed else 1


def cmd_answer(g: Game, args) -> int:
    before = status_now(g)
    r = g.request(args.command)
    print(r.get("text"))
    if not r.get("ok"):
        return 1
    return 0 if settle(g, before) else 1


def cmd_skip(g: Game, args) -> int:
    """跳过小游戏的开关，和面板上那个按钮一样（走聊天服务，面板会跟着变）。"""
    want = {"on": True, "off": False}.get(args.state)
    if want is None:
        try:
            print("跳过小游戏：" + ("开着" if json.loads((g.data / "modes.json").read_text(encoding="utf-8")).get("skipGames", True) else "关着"))
        except (OSError, ValueError):
            print("读不到 modes.json")
        return 0
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/api/modes", data=json.dumps({"skipGames": want}).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=10) as res:
            modes = json.loads(res.read().decode("utf-8"))
        print("跳过小游戏：" + ("开着（之后开始的小游戏直接给完美结果）" if modes.get("skipGames") else "关着（照常玩）")
              + "。")
        return 0
    except (urllib.error.URLError, OSError) as e:
        print(f"改不了（聊天服务开着吗？）：{e}")
        return 1


def act(g: Game, cmd: str, **fields) -> int:
    """战斗和探险的一步：打印回话。轮到了敌方、打完了、在走的（回话里 settle 为真），等到又轮到 agent，
    打印这段时间发生的事和新的一屏；还在自己的回合就只打回话，回话里已经写了打了多少、谁还能出手、剩几点能量。"""
    before = status_now(g)
    r = g.request(cmd, **fields)
    print(r.get("text"))
    if not r.get("ok"):
        return 1
    if r.get("settle"):
        return 0 if settle(g, before) else 1
    return 0


def cmd_fight(g: Game, args) -> int:
    return simple(g, "fight")


def cmd_skill(g: Game, args) -> int:
    return act(g, "skill", text=" ".join(args.words))


def cmd_defend(g: Game, args) -> int:
    return act(g, "defend")


def cmd_weapon(g: Game, args) -> int:
    return simple(g, "weapon", text=" ".join(args.name))


def cmd_endturn(g: Game, args) -> int:
    return act(g, "endturn")


def cmd_auto(g: Game, args) -> int:
    return act(g, "autofight", text=args.state or "")


def cmd_walk(g: Game, args) -> int:
    return act(g, "walk", text=" ".join(args.lane))


def cmd_type(g: Game, args) -> int:
    return simple(g, "type", text=" ".join(args.text))


def cmd_read(g: Game, args) -> int:
    return simple(g, "read")


def cmd_say(g: Game, args) -> int:
    text = " ".join(args.text)
    if text == "-":
        text = sys.stdin.read()
    if not text.strip():
        print("用法：say 话", file=sys.stderr)
        return 2
    token = (g.data / "token").read_text(encoding="utf-8").strip()
    req = urllib.request.Request(
        f"http://127.0.0.1:{PORT}/api/reply",
        data=json.dumps({"text": text, "client": presence_client(g)}).encode("utf-8"),
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=10) as res:
            print(f"已发送 #{json.loads(res.read().decode('utf-8')).get('id')}")
            return 0
    except urllib.error.HTTPError as e:
        if e.code == 409:  # 别人在玩：这句不进面板
            try:
                turn = json.loads(e.read().decode("utf-8"))
            except (OSError, ValueError):
                turn = {}
            print(handover_text(turn) + "（这句没进面板）")
            return 3
        print(f"发送失败：{e}")
        return 1
    except (urllib.error.URLError, OSError) as e:
        print(f"发送失败（聊天服务开着吗？start-server.bat）：{e}")
        return 1


def cmd_log(g: Game, args) -> int:
    events, _ = g.read_events(0)
    print_events(events[-args.n:])
    return 0


def main() -> int:
    # -h 打印的就是开头那段说明：里面说到人的「user」换成 LUDUS_USER_NAME
    p = argparse.ArgumentParser(description=vp_players.named(__doc__), formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data", type=Path, default=DEFAULT_DATA)
    sub = p.add_subparsers(dest="command", required=True)
    sub.add_parser("status").set_defaults(fn=cmd_status)
    w = sub.add_parser("wait")
    w.add_argument("--timeout", type=float, default=600)
    w.set_defaults(fn=cmd_wait)
    sc = sub.add_parser("screen")
    sc.add_argument("--all", action="store_true", help="连没字的图标、说明按钮也列出来")
    sc.add_argument("--debug", action="store_true", help="每个按钮的屏幕坐标，没列出来的写原因")
    sc.set_defaults(fn=cmd_screen)
    for name, fn in (("click", cmd_click), ("choose", cmd_choose), ("tip", cmd_tip)):
        c = sub.add_parser(name)
        c.add_argument("n", nargs="+", help="编号，或者按钮上的字 / 功能")
        if name != "tip":
            c.add_argument("--quick", action="store_true", help="点完不等结果")
        c.set_defaults(fn=fn)
    sl = sub.add_parser("slide")
    sl.add_argument("n", type=int)
    sl.add_argument("value", type=float)
    sl.set_defaults(fn=cmd_slide)
    sub.add_parser("shot", help="截一张现在的画面：想看就看，不用理由").set_defaults(fn=cmd_shot)
    lk = sub.add_parser("look", help="现在在哪；look 名字：一个人或地方，想看就看，不用理由")
    lk.add_argument("name", nargs="*", help="人或地方的名字（不写就是看现在在哪）")
    lk.set_defaults(fn=cmd_look)
    sub.add_parser("home", aliases=["近况"], help="家里的近况：卧室左下角那排菜单合成一张").set_defaults(fn=cmd_home)
    c = sub.add_parser("talent", help="天赋：不写名字是看每一项，写名字是加一级")
    c.add_argument("name", nargs="*", help="天赋的名字，比如 精力充沛")
    c.set_defaults(fn=cmd_talent)
    c = sub.add_parser("hello", help="新会话开头：这之后见到的都当第一次见。hello 名字：报上你的名字（面板上、印象簿都按它分）")
    c.add_argument("name", nargs="*", help=f"你的名字，比如 {vp_players.AGENT_NAME}（claude.ai）；不写就按会话是从哪儿开的叫")
    c.set_defaults(fn=cmd_hello)
    c = sub.add_parser("note", help="想记一句就记，下回见到时会带出来")
    c.add_argument("text", nargs="+", help="名字 话")
    c.set_defaults(fn=cmd_note)
    c = sub.add_parser("feedback", help=vp_players.named("玩着觉得哪里别扭、哪里好玩，随时说，user 会看，也会改"))
    c.add_argument("text", nargs="+")
    c.set_defaults(fn=cmd_feedback)
    c = sub.add_parser("recap", help="上回说到：这一年（或者指定的年份）发生了什么")
    c.add_argument("year", nargs="*")
    c.set_defaults(fn=cmd_recap)
    for name in ("acts", "plan", "wish"):
        sub.add_parser(name).set_defaults(fn=cmd_text)
    sub.add_parser("read").set_defaults(fn=cmd_read)
    for name, fn, arg in (("do", cmd_do, "target"), ("go", cmd_go, "place"), ("touch", cmd_touch, "name"),
                          ("pick", cmd_pick, "name"), ("course", cmd_course, "name"), ("type", cmd_type, "text")):
        c = sub.add_parser(name)
        c.add_argument(arg, nargs="+")
        c.set_defaults(fn=fn)
    u = sub.add_parser("uncourse")
    u.add_argument("--all", action="store_true", help="全清")
    u.set_defaults(fn=cmd_uncourse)
    s = sub.add_parser("schedule")
    s.add_argument("names", nargs="+")
    s.set_defaults(fn=cmd_schedule)
    for name in ("yes", "no"):
        sub.add_parser(name).set_defaults(fn=cmd_answer)
    sk = sub.add_parser("skip")
    sk.add_argument("state", nargs="?", choices=["on", "off"])
    sk.set_defaults(fn=cmd_skip)
    sub.add_parser("fight").set_defaults(fn=cmd_fight)
    c = sub.add_parser("skill")
    c.add_argument("words", nargs="+", help="技能名（或 人.技能），再写目标 敌N 或敌人的名字")
    c.set_defaults(fn=cmd_skill)
    sub.add_parser("defend").set_defaults(fn=cmd_defend)
    c = sub.add_parser("weapon")
    c.add_argument("name", nargs="*", help="武器名、弓、另一把；不写就是换另一把")
    c.set_defaults(fn=cmd_weapon)
    sub.add_parser("endturn").set_defaults(fn=cmd_endturn)
    c = sub.add_parser("auto")
    c.add_argument("state", nargs="?", choices=["on", "once", "off"])
    c.set_defaults(fn=cmd_auto)
    c = sub.add_parser("walk")
    c.add_argument("lane", nargs="*", help="停在岔路时写路名（金色的路 / 蓝色的路）")
    c.set_defaults(fn=cmd_walk)
    s = sub.add_parser("say")
    s.add_argument("text", nargs="+")
    s.set_defaults(fn=cmd_say)
    lg = sub.add_parser("log")
    lg.add_argument("-n", type=int, default=30)
    lg.set_defaults(fn=cmd_log)
    args = p.parse_args()
    global SP_DIR
    g = Game(args.data)
    SP_DIR = g.dir
    command = args.command
    reporter = None
    name = " ".join(getattr(args, "name", None) or []).strip() if command == "hello" else ""
    if name and not os.environ.get("LUDUS_PLAYER"):  # hub 起的名字由 hub 定
        vp_players.set_session_name(g.data, name[:40])
        g.dir = SP_DIR = vp_players.my_dir(g.data)
    # talent 不写名字只是看看；写了名字是加点，算动游戏
    if command in READ_ONLY or command == "say" or (command == "talent" and not args.name):
        reporter = report_presence(g)  # 只看的：报到就行，不接手；say 由聊天服务挡
    else:
        # 动游戏的命令：hello、新会话第一条（替它 hello）算接手，别的先问能不能玩
        if os.environ.get("LUDUS_PRESENCE") != "0":
            turn = claim_turn(g, "hello" if command == "hello" or needs_hello(g) else "act")
            if turn and turn.get("you") == "blocked":
                print(handover_text(turn))
                return 3
            g.dir = SP_DIR = vp_players.my_dir(g.data)  # 刚接手的新玩家：聊天服务这时才把它记进 players.json
        auto_hello(g, command)
    code = args.fn(g, args)
    if reporter:
        reporter.join(0.15)  # 很快的命令也让报到发出去（开着时几毫秒就完）；聊天服务没开就不等了
    return code


if __name__ == "__main__":
    sys.exit(main())
