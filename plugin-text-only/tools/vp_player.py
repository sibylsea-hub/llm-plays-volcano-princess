"""agent 自己玩火山的女儿：命令行工具。

先在聊天面板上打开「agent 自己玩」（data/modes.json 的 selfplay 为 true），游戏里装着纯文字版的 Ludus Lunae 插件（0.3.7）。
插件会自己往下翻对话、跳过小游戏、翻月中的过程页；要 agent 做决定的时候（对话选项、做什么事、排日程、弹窗）就停下来等。
翻得多快是面板上「看剧情」的速度（chat-service/data/modes.json 的 readCps：每秒几个字，0 是自己翻，默认 12）。
你读台词不用等，这个速度是给 user 看的，照 user 读字的快慢来：user 常常自己点击、按空格往下翻，是嫌慢；说没读完，是太快。
user 可以自己在聊天面板里调，也可以由你动手改 readCps，插件马上就照新的翻。
user 在游戏里自己点了按钮、翻了台词，会多一行「[user 点了] …」「[user 自己翻了 5 句台词]」：
画面变了是 user 动的，不是自己跳的。

用法：python -X utf8 plugin-text-only/tools/vp_player.py <命令> ...

看：
  wait [--timeout S]      等到轮到 agent：状态、上次以来的台词和事件、现在能做什么、对话选项或按钮清单
  look                    现在在哪、开着什么窗口、这里每个物体能做什么（行动力、金币、加什么、本月还能几次）、能去哪
  acts                    本月所有地点能做的事（月初看一眼，行动力怎么花）
  plan                    日程：已排了什么、每门课花多少、每节加什么、进度、心情，能约谁出去玩
  wish                    女儿的心愿（幼年是爸爸的许诺：要做的事和进度）
  status                  现在轮到谁、状态
  log [-n N]              最近 N 条事件（默认 30）

按名字做事（推荐）：
  do 物体.活动            比如 do 玩具积木.搭积木、do 家庭餐厅.主食.家庭套餐、do 莉莉娅（说话）、do 本森.聊天（说话后在菜单里选「聊天」）、
                          do 探险 / do 烹饪（场景里的按钮）。
                          不在这里会先 go 过去，离得远会先走过去，点开物体再选活动，最后打印结果和状态变化
  go 地点                 去地图上的地点（go 家 = 月初在爸爸房间时「见女儿」）
  schedule 课 课 ...      排日程：从空的开始依次排（出去玩写人名，比如 伍柏），最多 5 格。排满会弹确认框，再 yes 开始这个月
  course 课名             往日程里加一格；uncourse [--all] 撤掉最后一格（或全清）
  yes / no                回答确认框（回复里写着在确认什么）
  touch 物体 / pick 活动  do 的两半：点开物体 / 在开着的面板里选活动（菜）
  type 文字               往输入框里打字（建角色时取名）
  skip on|off             跳过小游戏开关（面板上那个，默认开着）。开着时限时、靠手速的小游戏（算术、画画点 tag、钓鱼）
                          直接拿到最好的结果，想自己一步一步玩就 skip off；跳了哪个，聊天时顺口提一句就好

按编号（通用，什么画面都能用）：
  screen [--all]          重新扫一遍画面，打印编号按钮清单（带悬停提示）；--all 连没字的图标也列出来
  screen --debug          调试：每个按钮的屏幕坐标，没列出来的写原因（被什么挡住、不在屏幕上）
  shot                    截一张游戏画面（data/selfplay/shot.png）。备用：先用文字（wait / look / screen / 窗口里的字），
                          文字读不懂的画面、或者想看人物长什么样、画面好不好看时再截
  click N|字 [--quick]    点清单里的第 N 个（用最近一次 wait/screen 的清单）；也可以写按钮上的字或功能，比如 click 见女儿。
                          点完等画面稳定，打印台词、事件、状态变化和现在能做什么（--quick 不等）
  choose N|字 [--quick]   选第 N 个对话选项，或者写选项里的字；同样等结果
  tip N|字                把鼠标移到这个按钮上，看悬停提示
  slide N 值              把第 N 个滑动条拖到 0~1 之间的位置（买几个、用几个）
  read                    画面上所有的字

其他：
  say 话                  发到聊天面板（{user} 看得到）
选项：--data DIR  聊天服务的 data 文件夹（默认 chat-service/data）

一个月大概这样过：月初 wait → go 家（见女儿）→ acts 看这个月能去哪、做什么 → do …… → schedule 排五格 → yes →
wait（过程页自动翻，遇到对话选项停下来 choose）→ 换月。行动力留不到下个月；花行动力的是屋里和地图上的物体，
上课不花行动力，但每节课心情 -5（幼年）或 -10。
文件：data/selfplay/ 下 state.json（插件写）、inbox.jsonl（这里写命令）、outbox.jsonl（插件回复）、
events.jsonl（插件写的台词和事件）、cursor.json（这里记 wait 读到哪了）。格式见 plugin-text-only/plugin/SelfPlay.cs 开头。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_DATA = ROOT / "chat-service" / "data"
STALE_SECONDS = 20  # state.json 这么久没更新，就当游戏没开
PORT = int(os.environ.get("LUDUS_PORT") or 8740)  # 聊天服务的端口
USER_NAME = os.environ.get("LUDUS_USER_NAME") or "user"  # 和 agent 一起玩的人叫什么：-h 里的 {user} 换成它
# 没字、没提示的这些按钮在 wait 里收起来（screen --all 看全部）：顶栏图标、说明按钮
NOISE_KINDS = {"图标", "说明", "说明（tip 看）", "提示（tip 看）", "按钮", "爸爸房间提示", "enterIcon", "showDes",
               "摆设 / 说明（点了会说一句话）"}


class Game:
    def __init__(self, data: Path):
        self.data = data
        self.dir = data / "selfplay"
        self.dir.mkdir(parents=True, exist_ok=True)

    # ---- 文件 ----
    def state(self) -> dict | None:
        path = self.dir / "state.json"
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
        rid = uuid.uuid4().hex[:12]
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
        why = "游戏没开，或者插件没加载" if st is None or self.age(st) > STALE_SECONDS else "插件没回话"
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
                    print(f"== 现在：等了 {timeout:.0f} 秒还没轮到你：在等 user 翻页（看剧情设成了自己翻），不是卡住了，再 wait ==")
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
    text = (
        f"{s.get('dauName')}（爸爸 {s.get('faName')}）  {s.get('year')} {s.get('month')}月（第 {s.get('turn')} 回合）\n"
        f"金币 {s.get('coin')}  行动力 {s.get('energy')}/{s.get('maxEnergy')}  心情 {s.get('mood')}  声望 {s.get('fame')}  "
        f"好感 {s.get('favor')}  黑暗 {s.get('darkness')}  感悟 {s.get('inspiration')}  爱慕者 {s.get('lover')}  身高 {s.get('height')}\n"
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
        add(name, a.get(key), b.get(key))
    for group in ("nature", "inAttri"):
        for k, v in (b.get(group) or {}).items():
            add(k, (a.get(group) or {}).get(k), v)
    return "，".join(parts)


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
        return f"[user 点了] {n}{e.get('label')}{more}"
    if did == "choose":
        return f"[user 选了] {e.get('label')}{more}"
    if did == "lines":
        return f"[user 自己翻了 {e.get('count')} 句台词]"
    if did == "skip":
        return f"[user 按住 Ctrl 快进了 {e.get('count')} 句台词]"
    return None


def fmt_event(e: dict) -> str | None:
    t = e.get("type")
    if t == "human":
        return fmt_human(e)
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
    if t == "celebrate":
        return "[庆祝] " + (e.get("text") or "").replace("\n", " ｜ ")
    if t == "month-result":
        return "[本月结算] " + (e.get("text") or "").replace("\n", " ｜ ")
    if t == "type":
        return f"> 打字：{e.get('text')}"
    if t == "item":
        return f"[得到] {e.get('name')} ×{e.get('num')}"
    if t == "story":
        return "[这一页] " + (e.get("text") or "").replace("\n", " ｜ ")
    if t == "scene":
        return f"== 换场景：{e.get('from')} → {e.get('to')} =="
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


def print_events(events: list[dict], g: "Game | None" = None) -> None:
    """打印事件。给了 g 时，换月那条附上和上一次换月（或读档）比变了多少，基准记在 cursor.json。"""
    base = g.cursor().get("month_state") if g else None
    group_human(events)
    for e in events:
        if e.get("type") in ("month", "load"):
            if e.get("type") == "month" and base:
                e["_diff"] = fmt_diff(base, e.get("state"))
            base = e.get("state")
        text = fmt_event(e)
        if text:
            print(text)
    if g and base:
        g.save_cursor(month_state=base)


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
        print("还没有 state.json：游戏没开过，或者插件没装上。")
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
    path = g.dir / "shot.png"
    before = path.stat().st_mtime if path.exists() else 0
    r = g.request("shot")
    if not r.get("ok"):
        print(r.get("text"))
        return 1
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if path.exists() and path.stat().st_mtime != before:
            time.sleep(0.3)  # 等游戏写完
            print(path)
            return 0
        time.sleep(0.1)
    print("截图没写出来（游戏这一帧没画？）")
    return 1


def cmd_text(g: Game, args) -> int:
    """look / acts / plan / read：插件直接给文字。"""
    return simple(g, args.command)


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
    time.sleep(0.6)  # 日程窗口刚打开时会重建课程格子，等它稳下来再点（点早了第一格会丢）
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
            if "学完" in head or "没有叫" in head or "排不了" in head or "约过" in head:
                break
            time.sleep(0.4)
        print(head)
        if isinstance(last.get("count"), int):
            count = last["count"]
        if not last.get("ok"):
            (skipped if ("学完" in head or "约过" in head) else failed).append(f"{name}（{head}）")
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
        data=json.dumps({"text": text}).encode("utf-8"),
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {token}"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=10) as res:
            print(f"已发送 #{json.loads(res.read().decode('utf-8')).get('id')}")
            return 0
    except (urllib.error.URLError, OSError) as e:
        print(f"发送失败（聊天服务开着吗？start-server.bat）：{e}")
        return 1


def cmd_log(g: Game, args) -> int:
    events, _ = g.read_events(0)
    print_events(events[-args.n:])
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=(__doc__ or "").replace("{user}", USER_NAME), formatter_class=argparse.RawDescriptionHelpFormatter)
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
    sub.add_parser("shot").set_defaults(fn=cmd_shot)
    for name in ("look", "acts", "plan", "wish"):
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
    s = sub.add_parser("say")
    s.add_argument("text", nargs="+")
    s.set_defaults(fn=cmd_say)
    lg = sub.add_parser("log")
    lg.add_argument("-n", type=int, default=30)
    lg.set_defaults(fn=cmd_log)
    args = p.parse_args()
    return args.fn(Game(args.data), args)


if __name__ == "__main__":
    sys.exit(main())
