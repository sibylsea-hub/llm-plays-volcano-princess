"""火山的女儿：agent 自己玩时的「记忆和鼓励」：印象簿收话、再见时的回音、feedback、换年时的「上回说到」。

做法照 docs/设计理念.md 第二节第 5 条（不索要，但鼓励）、第四节（印象簿和事件的格式）、
第五节（只收，不要；引用旧话，不催新话）。这里不放完成度，不催 agent 写感想：说了的话收下来，
再见到那个人、那个地方时把当时那句带出来。被引用是甜的，被催稿是苦的。只用标准库（Python 3.12）。

函数（第一个参数都是 data 文件夹，就是 vp_player 的 g.data，默认 chat-service/data）：
  collect(data, again=False) -> int
      收话：从 chat.jsonl 里 agent 说的话（from 为 agent）中，挑出提到印象簿里名字的句子（人名、地名、怪物名；
      露丝也算，没有她的条目就建一个 dau:露丝），截出那一句（不超过 120 字，太长就截名字附近的几个小句），
      写进那个条目的 words，from 记 panel。读到哪记在 memory.json，反复调用只看新行；同一条目里一样的话不收第二次。
      不收的：聊工具的句子（插件、清单、截图、wait……）。单字的名字（利、泽）和泛称（学者、士兵）只在像人名的
      地方认：前一个字是标点或「对、和、跟……」，后一个字是标点或「说、问、来、学长……」，所以「顺利」「战利品」
      「利落地」「开朗」都不算。名字后面带括号的（「冰霜峡谷（探险）」）去掉括号也认。
      again=True 从头再扫一遍（印象簿里新加了名字以后用；重复的照样不收）。返回收了几句。
      挑哪句带出来、look 时挑哪几句：agent 自己的感受（喜欢、难过、表情、叹号）在前，转述的台词、数字、
      杂活（行动力、金币、排课）在后，只在括号里顺带一提的不挑，自己记的（note）最先。
  note(data, 名字, 话) -> str
      agent 自己记一句，from 记 note。名字可以写「face:克莱尔」这样的键。没有这个条目就新建 note:名字。
      返回一句游戏里的回话，比如「（记在心里了。）」。
  echo(data, events) -> str
      回音：events 是 wait 刚读到的那一批。里面出现了印象簿里有话的人（line 的 who）、地方（scene 的 to）、
      第一次见（sight，dau 就是露丝的新样子，crowd 按名单里的每个人），就挑一句从前说过的话带出来，比如
      「（又见到妮娜。去年 10 月，你说过：「……」）」。只带比这一批早的回合说的话；时间说法从那句话的回合
      （没记就按 ts 对 events 里的换月）推。一次最多一句；同一个名字一个游戏年里最多带一次，带过的记在 memory.json。
      露丝说话、爸爸说话不算（每批都有），露丝只在 sight dau 时带。没有可带的就返回空字符串。
      sight 的 why（插件 Sights.cs 写的）换前半句：露丝 grow →「露丝长大了。」、costume →「露丝换上了白孔雀的礼服。」，
      地方 season →「又来到遗忘森林，已经是冬火年的样子了。」
  feedback(data, 话) -> str
      追加到 selfplay/feedback.jsonl（{ts, text, turn}），回「记下了，user 会看」这类话（user 是 LUDUS_USER_NAME 设的名字）。
  recap(data, year=None) -> str
      上回说到：这一年（或者指定的春火年 / 夏火年 / 秋火年 / 冬火年，也可以只写「夏」）的长大、学完的课、成就、
      做过的选择、第一次见的人和地方、这一年说过的话、feedback，末尾问一句这一年玩得怎么样。一屏左右。
      不写年份时是最近的一年；最近的一年才刚开头（只换过一次月）时，说的是上一年，所以换年那一刻直接调就对。
      年份按 month / load 事件里 state 的 year 认。
  words_for(data, 名字) -> list[dict]
      这个名字收到的话，按时间排（每条是 words 里那一条，多一个 key 字段写它在哪个条目里）。
      同名的条目（face:妮娜、note:妮娜）合在一起；露丝 = 所有 dau 条目；群像（crowd）里提到这个名字的话也算。
  card(data, 名字) -> str
      look 名字 用的一页：条目、介绍、那张图的完整路径（Read 就能看）、说过的话（多了挑几句）。

文件（都在 data 文件夹下）：
  （selfplay/players.json 在的话，下面 selfplay/ 下的 events、impressions、feedback、memory 都在
   「我」的玩家文件夹 selfplay/players/<文件夹>/ 里，state.json 还在 selfplay/；收话只收 who 是我的那几句。见 vp_players.py）
  chat.jsonl                聊天服务写：{"id","ts","from":"agent|user","text","who"}，ts 是 UTC（带 Z）。这里只读。
  selfplay/events.jsonl     插件写：line（who, say）、options（options, what）、chose（text）、month / load（state）、
                            scene（from, to）、sight（what, name, image, note）……这里只读。
  selfplay/impressions.json 印象簿。插件第一次见时建条目（Sights.cs 的 Impressions.Add），键是「what:名字」，值是
                            {"name","what","image","note","first","when","words":[{"ts","text","from":"panel|note"}]}。
                            露丝的名字像「露丝（夏火年，学院制服）」，群像像「中央广场：A、B、C」。
                            这里只往 words 里加（和 note / 露丝时新建条目），不动别的字段。words 里每条另外带
                            turn / year / month（说这句话时是哪个回合，知道的话），回音靠它说「去年 10 月」。
                            写的时候先读最新的、改完换掉整个文件（os.replace）；插件那边是把新条目拼在最后一个 }
                            前面，已有的不碰，所以两边写的都留得住。读不懂就不写。
  selfplay/feedback.jsonl   feedback 追加：{"ts","text","turn"}。
  selfplay/memory.json      这里自己的记录：{"chat": chat.jsonl 读到的字节位置, "echoed": {名字: [{ts,year,turn,text}]}}。
  ts 一律存成本地时间 YYYY-MM-DDTHH:MM:SS（和插件写的一样）；chat.jsonl 的 UTC 读进来时换成本地时间。

在 vp_player.py 里这样接（同一个文件夹，import vp_memory 就行）：
  - wait 和 settle()（click / choose / do / go / yes / no / walk / skill……打印「上次以来」的地方）：
      new_events() 之后、print_events 之前：vp_memory.collect(g.data)
      print_events 之后：line = vp_memory.echo(g.data, events)，有就 print(line)（放在「现在」那段之前）
      这一批里有 month 事件、年份和 cursor 里 month_state 的 year 不一样（换年了）：
      print(vp_memory.recap(g.data, 旧的年份))，放在回音后面。go_to() 里打印的事件同样可以 echo。
  - look 名字：先 collect(g.data)，再 print(vp_memory.card(g.data, 名字))，接在插件 LookName 给的游戏资料后面。
    不带名字的 look 照旧。
  - note 名字 话    → print(vp_memory.note(g.data, 名字, 话))
  - feedback 话     → print(vp_memory.feedback(g.data, 话))
  - recap [年]      → print(vp_memory.recap(g.data, 年 or None))
  说明里建议这样写（想看就看、想说就说，不写成「备用」）：
    look 名字     一个人、一个地方在印象簿里的那一页：第一次见的图、介绍、你说过的话。想看就看
    note 名字 话  想记一句就记，下回见到时会带出来
    feedback 话   玩着觉得哪里别扭、哪里好玩，随时说，user 会看，也会改
    recap [年]    上回说到：这一年（或者指定的年份）发生了什么

单独跑（测试、手动用）：python -X utf8 vp_memory.py [--data DIR] collect|note|echo|feedback|recap|words|card ...
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import unicodedata
from bisect import bisect_right
from datetime import datetime, timedelta
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # 同一个文件夹里的 vp_players（python -I 时不会自己加）
import vp_players  # noqa: E402  谁的是谁的：players.json 在的话，印象簿、记录、feedback 都在自己的玩家文件夹里

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DATA = ROOT / "chat-service" / "data"

YEARS = ["春火年", "夏火年", "秋火年", "冬火年"]
MAX_WORD = 120
WHAT_ORDER = ["face", "place", "boss", "monster", "dau", "cg", "art", "note", "crowd"]
WHAT_CH = {"face": "人", "crowd": "人", "place": "地方", "monster": "怪物", "boss": "首领", "cg": "画面", "art": "画",
           "dau": "女儿", "note": "自己记的"}

# 不当名字认的：旁白、问号、泛指
SKIP_NAMES = {"旁白", "？？？", "???", "所有人", "孩子", "小孩", "爸爸", "妈妈"}
# 当名字认、但要看前后文的泛称（和单字的名字一样）
ROLE_WORDS = {"学者", "士兵", "国王", "侍者", "猎人", "主教", "修女", "神官", "大臣", "侍卫", "骑士", "公主", "王子",
              "管家", "老板", "店主", "商人", "家"}
NAME_BEFORE = set("对和跟给找陪约叫问请向被让替与同把帮")
NAME_AFTER = set("说问讲道笑来去是也还又就都的提和跟对给帮聊那这在会要把被让")
TITLES = ("学长", "学姐", "先生", "小姐", "老师", "教授", "主教", "修女", "公主", "王子", "叔叔", "阿姨", "大叔",
          "姐姐", "哥哥", "爷爷", "奶奶")
# 聊工具的句子不收（「wait 卡在神秘商人那里」不是对神秘商人的印象）
TOOL_WORDS = ("插件", "清单", "命令", "截图", "交接书", "脚本", "调用", "反编译", "编号", "按钮", "规划本", "面板",
              "唤醒", "直播")
TOOL_RE = re.compile(r"(?<![A-Za-z])(wait|screen|click|choose|schedule|look|log|busy|bug|skip|acts|plan|shot|"
                     r"settle|json|jsonl|BepInEx)(?![A-Za-z])", re.I)

# events 里一条 sight 都没有时，「第一次见」按台词里第一次露脸算；这些泛称不算人
GENERIC_RE = re.compile(r"(小孩|女孩|男孩|孩子|学生|宾客|管家|管事|后辈|男子|女子|女性|男性|士兵|侍者|侍卫|神官|大臣|天使|"
                        r"学者|主教|修女|猎人|国王|老板|商人|村民|贵族|精灵|小天使)$")

OPEN, CLOSE = "「『“（(《【", "」』”）)》】"
QOPEN, QCLOSE = "「『“", "」』”"
ENDS = "。！？!?"
CLAUSE_END = "，；：、,;:"


# ---- 小工具 ----
def _sp(data) -> Path:
    """我的东西在哪：印象簿、memory.json、feedback、events。开了玩家文件夹（players.json）就是自己的那个，
    不然是 data/selfplay/。谁是「我」看 vp_players.identity（hub 起的带 LUDUS_PLAYER，Claude Code 里按会话）"""
    return vp_players.my_dir(data)


def _now() -> str:
    return datetime.now().strftime("%Y-%m-%dT%H:%M:%S")


def norm_ts(ts) -> str:
    """统一成本地时间 YYYY-MM-DDTHH:MM:SS：聊天服务写的是 UTC 带 Z，插件写的是本地时间不带时区。"""
    if not ts:
        return ""
    try:
        d = datetime.fromisoformat(str(ts).replace("Z", "+00:00"))
    except ValueError:
        return str(ts)[:19]
    if d.tzinfo is not None:
        d = d.astimezone().replace(tzinfo=None)
    return d.strftime("%Y-%m-%dT%H:%M:%S")


def _hours(a: str, b: str) -> float:
    """b 比 a 晚几个小时。"""
    try:
        return (datetime.fromisoformat(b) - datetime.fromisoformat(a)) / timedelta(hours=1)
    except ValueError:
        return 0.0


def _read_json(path: Path, default, strict: bool = False):
    """插件可能正在换文件：读不懂就等一下再读。strict 时一直读不懂就报错（免得拿空的盖掉插件写的）。"""
    for _ in range(20):
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return default
        except (OSError, ValueError):
            time.sleep(0.05)
    if strict:
        raise RuntimeError(f"{path.name} 读不懂，先不写")
    return default


def _write_json(path: Path, obj) -> None:
    tmp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    tmp.write_text(json.dumps(obj, ensure_ascii=False, indent=1), encoding="utf-8")
    for _ in range(40):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:  # 插件正开着这个文件
            time.sleep(0.05)
    os.replace(tmp, path)


def _book(data) -> dict:
    book = _read_json(_sp(data) / "impressions.json", {})
    return book if isinstance(book, dict) else {}


def _update_book(data, fn):
    """读最新的印象簿，fn(book) 返回 (结果, 改了没有)，改了才写回。"""
    path = _sp(data) / "impressions.json"
    book = _read_json(path, {}, strict=True)
    if not isinstance(book, dict):
        raise RuntimeError("impressions.json 不是一个对象，先不写")
    result, changed = fn(book)
    if changed:
        path.parent.mkdir(parents=True, exist_ok=True)
        _write_json(path, book)
    return result


def _memory(data) -> dict:
    mem = _read_json(_sp(data) / "memory.json", {})
    return mem if isinstance(mem, dict) else {}


def _save_memory(data, mem: dict) -> None:
    _sp(data).mkdir(parents=True, exist_ok=True)
    _write_json(_sp(data) / "memory.json", mem)


def _events(data) -> list[dict]:
    try:
        raw = (_sp(data) / "events.jsonl").read_bytes()
    except OSError:
        return []
    out = []
    for line in raw.splitlines():
        try:
            e = json.loads(line.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            continue  # 插件写到一半的最后一行
        if isinstance(e, dict):
            out.append(e)
    return out


def _is_cjk(ch: str) -> bool:
    return bool(ch) and ("一" <= ch <= "鿿" or "㐀" <= ch <= "䶿")


def _is_emoji(ch: str) -> bool:
    return bool(ch) and (unicodedata.category(ch) == "So" or ch in "️‍" or 0x1F3FB <= ord(ch) <= 0x1F3FF)


def _strip_markup(s: str) -> str:
    return re.sub(r"<[^>]*>", "", s or "")


def _short(s: str, n: int) -> str:
    s = re.sub(r"\s+", "", _strip_markup(s))
    return s if len(s) <= n else s[: n - 1] + "……"


def _tidy(s: str) -> str:
    """「挺好的。」。→「挺好的。」"""
    return re.sub(r"([。！？!?…])([」』])。", r"\1\2", s)


def _requote(s: str) -> str:
    """放进「」里的话，里面的「」换成『』。"""
    return s.replace("「", "『").replace("」", "』")


def _what(key: str, e: dict) -> str:
    return e.get("what") or key.split(":", 1)[0]


def _name(key: str, e: dict) -> str:
    return (e.get("name") or key.split(":", 1)[-1]).strip()


def _base(name: str) -> str:
    """去掉名字后面的括号：「冰霜峡谷（探险）」→「冰霜峡谷」。"""
    return re.sub(r"\s*[（(][^（）()]*[）)]$", "", name or "").strip()


def _dau_of(key: str, e: dict) -> str:
    """一个 dau 条目是哪个女儿的：插件建的名字像「露丝（夏火年，学院制服）」，去掉括号就是她的名字；
    收话时建的 dau:露丝 就是名字本身。一个玩家玩过好几局、养过好几个女儿时，靠它分开"""
    return _base(_name(key, e))


def crowd_members(name: str) -> list[str]:
    """「开学：A、B、C」→ [A, B, C]。"""
    body = re.split(r"[：:]", name, 1)[-1]
    return [m.strip() for m in re.split(r"[、，,/]", body) if m.strip()]


def _good_name(name: str) -> bool:
    if not name or name in SKIP_NAMES:
        return False
    if not re.search(r"[一-鿿A-Za-z]", name):
        return False
    return not re.fullmatch(r".*[一-鿿][A-Z]", name)  # 小孩A、贵族B 这种是泛指


# ---- 时间：哪个回合、哪一年 ----
def _timeline(events: list[dict]) -> tuple[list[str], list[dict]]:
    """events 里每次换月（和读档）的时间和状态。开局那个月还没换过月：第一次换月是第 1 回合时，
    从记录开头到它算第 0 回合（春火年 1 月）。"""
    tss, states = [], []
    for e in events:
        st = e.get("state")
        if e.get("type") in ("month", "load") and isinstance(st, dict) and "error" not in st and e.get("ts"):
            tss.append(norm_ts(e["ts"]))
            states.append(st)
    if states and states[0].get("turn") == 1 and events and events[0].get("ts") and norm_ts(events[0]["ts"]) < tss[0]:
        tss.insert(0, norm_ts(events[0]["ts"]))
        states.insert(0, dict(states[0], turn=0, month=max(1, (states[0].get("month") or 2) - 1)))
    return tss, states


def _state_at(tl, ts: str) -> dict | None:
    tss, states = tl
    i = bisect_right(tss, ts) - 1
    return states[i] if i >= 0 else None


def _year_index(st: dict | None) -> int | None:
    if not st:
        return None
    if st.get("year") in YEARS:
        return YEARS.index(st["year"])
    turn = st.get("turn")
    if isinstance(turn, int):  # DataSys.Stage()：前 6 回合春火年，之后每 12 回合换一年
        return 0 if turn < 6 else min(3, 1 + (turn - 6) // 12)
    return None


def _state_json(data) -> tuple[str, dict | None]:
    st = _read_json(vp_players.root(data) / "state.json", {})  # 游戏现在的样子是大家共用的
    if not isinstance(st, dict):
        return "", None
    status = st.get("status")
    return norm_ts(st.get("ts")), (status if isinstance(status, dict) and "error" not in status else None)


def _current(data, batch: list[dict] | None = None, tl=None) -> dict | None:
    """现在是哪个回合：这一批里最后的换月；state.json 和这一批一样新就用它；不然按这一批的时间对 events 里的换月。"""
    batch = batch or []
    for e in reversed(batch):
        st = e.get("state")
        if e.get("type") in ("month", "load") and isinstance(st, dict) and "error" not in st:
            return st
    stamps = [norm_ts(e.get("ts")) for e in batch if e.get("ts")]
    last = max(stamps) if stamps else ""
    sts, status = _state_json(data)
    if status and (not last or sts >= last):
        return status
    tl = tl or _timeline(_events(data))
    if last:
        return _state_at(tl, last) or status
    return tl[1][-1] if tl[1] else status


def _word_state(w: dict, tl) -> dict | None:
    if isinstance(w.get("turn"), int):
        return {"turn": w["turn"], "year": w.get("year"), "month": w.get("month")}
    return _state_at(tl, norm_ts(w.get("ts"))) if w.get("ts") else None


def _when(ws: dict | None, now: dict | None, dau: str, dau_view: bool) -> str:
    """说那句话的时候，用游戏里的时间说：上个月、这一年 3 月、去年 10 月、她还小的时候。"""
    if not ws or not now or not isinstance(ws.get("turn"), int) or not isinstance(now.get("turn"), int):
        return "上次玩的时候"
    y0, y1 = _year_index(ws), _year_index(now)
    month = ws.get("month")
    dt = now["turn"] - ws["turn"]
    if y0 == y1 and abs(dt) < 12:
        if dt <= 0:
            return "这个月"
        return "上个月" if dt == 1 else f"这一年 {month} 月"
    if y0 == 0:
        return "她还小的时候" if dau_view else f"{dau}还小的时候"
    if y0 is not None and y1 is not None and y1 - y0 == 1:
        return f"去年 {month} 月"
    return f"{ws.get('year') or ''} {month} 月".strip()


# ---- 句子 ----
def sentences(text: str) -> list[str]:
    """按句号、叹号、问号、换行切句；引号里的不切；句末的引号、表情、波浪号跟着前一句。"""
    out, buf, depth, i = [], [], 0, 0
    while i < len(text):
        ch = text[i]
        if ch == "\n":
            out.append("".join(buf))
            buf, depth = [], 0
            i += 1
            continue
        buf.append(ch)
        i += 1
        if ch in OPEN:
            depth += 1
        elif ch in CLOSE:
            depth = max(0, depth - 1)
        elif ch in ENDS and depth == 0:
            while i < len(text) and text[i] != "\n" and (text[i] in ENDS or text[i] in CLOSE or text[i] in "~～ "
                                                         or _is_emoji(text[i])):
                buf.append(text[i])
                i += 1
            out.append("".join(buf))
            buf = []
    out.append("".join(buf))
    return [s for s in (_clean(x) for x in out) if len(s) >= 4]


def _clean(s: str) -> str:
    s = s.strip()
    prev = None
    while s != prev:  # 句首的「· 」「① 」「1. 」「🎉 」
        prev = s
        s = re.sub(r"^(?:[·•\-\*—>]+|[①-⑳❶-❿]|\d+[\.、）)](?!\d))\s*", "", s)
        while s and (_is_emoji(s[0]) or s[0] == " "):
            s = s[1:]
    return s.strip()


def _clip(s: str, i: int) -> str:
    """一句太长：从名字所在的小句往两边加，加到 120 字为止，切掉的那头写「……」。"""
    if len(s) <= MAX_WORD:
        return s
    spans, depth, start = [], 0, 0
    for k, ch in enumerate(s):
        if ch in OPEN:
            depth += 1
        elif ch in CLOSE:
            depth = max(0, depth - 1)
        elif ch in CLAUSE_END and depth == 0:
            spans.append((start, k + 1))
            start = k + 1
    if start < len(s):
        spans.append((start, len(s)))
    c = next((n for n, (a, b) in enumerate(spans) if a <= i < b), 0)
    lo = hi = c
    budget = MAX_WORD - 4
    grew = True
    while grew:
        grew = False
        if hi + 1 < len(spans) and spans[hi + 1][1] - spans[lo][0] <= budget:
            hi += 1
            grew = True
        if lo > 0 and spans[hi][1] - spans[lo - 1][0] <= budget:
            lo -= 1
            grew = True
    a, b = spans[lo][0], spans[hi][1]
    if b - a > budget:  # 一个小句就超了：以名字为中心硬截
        a = max(0, min(i - budget // 3, len(s) - budget))
        b = min(len(s), a + budget)
    text = s[a:b].strip()
    if b < len(s):
        text = text.rstrip(CLAUSE_END) + "……"
    if a > 0:
        text = "……" + text
    return text


def _tool_talk(s: str) -> bool:
    return any(w in s for w in TOOL_WORDS) or bool(TOOL_RE.search(s))


def _person_like(text: str, i: int, n: int) -> bool:
    """单字的名字、泛称：前一个字是标点或「对、和……」，后一个字是标点或「说、问……」才算。"""
    before = text[i - 1] if i > 0 else ""
    rest = text[i + n:]
    after = rest[:1]
    ok_before = not _is_cjk(before) or before in NAME_BEFORE
    ok_after = not _is_cjk(after) or after in NAME_AFTER or rest.startswith(TITLES)
    if rest.startswith(("说明", "说服", "的话", "是否")):
        ok_after = False
    return ok_before and ok_after


def _find_names(text: str, names: dict) -> list[tuple[int, int, str]]:
    hits = []
    for name, info in names.items():
        start = 0
        while (i := text.find(name, start)) >= 0:
            start = i + 1
            if info["risky"] and not _person_like(text, i, len(name)):
                continue
            hits.append((i, i + len(name), name))
    hits.sort(key=lambda h: (h[0] - h[1], h[0]))  # 长的先占位：「露丝小天使」里不再认「露丝」
    taken = []
    for h in hits:
        if not any(h[0] < t[1] and t[0] < h[1] for t in taken):
            taken.append(h)
    return sorted(taken)


def _quoted_ratio(text: str) -> float:
    depth = n = 0
    for ch in text:
        if ch in QOPEN:
            depth += 1
        elif ch in QCLOSE:
            depth = max(0, depth - 1)
        elif depth:
            n += 1
    return n / max(1, len(text))


def _outside(text: str) -> str:
    depth, out = 0, []
    for ch in text:
        if ch in QOPEN:
            depth += 1
        elif ch in QCLOSE:
            depth = max(0, depth - 1)
        elif not depth:
            out.append(ch)
    return "".join(out)


FEEL_RE = re.compile(r"好[玩棒虐看帅甜可]|真[好是的]|太[好可帅]|觉得|喜欢|难过|想哭|哭了|可爱|帅|厉害|心疼|开心|好奇|担心|"
                     r"温柔|感动|有意思|漂亮|害怕|舍不得|鸡皮疙瘩|幸福|骄傲|放心|佩服|期待")
CHORE_RE = re.compile(r"行动力|金币|\d+ ?G\b|Lv\.|HP|\d+ ?血|回合|排了|花了|属性|[+-] ?\d")


def _in_parens(text: str, i: int) -> bool:
    depth = 0
    for ch in text[:i]:
        if ch in "（(":
            depth += 1
        elif ch in "）)":
            depth = max(0, depth - 1)
    return depth > 0


def _voice(w: dict, name: str | None = None) -> float:
    """有多少是 agent 自己的话、值不值得再带出来：转述的台词、数字和杂活扣分；感受、表情、叹号加分；
    名字在句首附近加分，只在括号里顺带一提的扣分；自己记的（note）加分。"""
    text = w.get("text") or ""
    out = _outside(text)
    score = -1.5 * _quoted_ratio(text)
    if "我" in out:
        score += 1
    score += min(2, len(set(FEEL_RE.findall(out))))
    if "！" in out or "!" in out or any(_is_emoji(c) for c in out):
        score += 1
    score -= min(2.0, 0.5 * len(CHORE_RE.findall(out)))
    if sum(c.isdigit() for c in out) >= 4:
        score -= 1.5
    if len(text) > 100:
        score -= 0.5
    if name:
        i = text.find(name)
        if 0 <= i < 20:
            score += 1
        if i >= 0 and _in_parens(text, i):
            score -= 2
    if w.get("from") == "note":
        score += 3
    return score


# ---- 印象簿：名字和条目 ----
def _dau_name(data, events: list[dict] | None = None) -> str:
    _, status = _state_json(data)
    if status and status.get("dauName"):
        return status["dauName"]
    for e in reversed(events or []):
        st = e.get("state")
        if isinstance(st, dict) and st.get("dauName"):
            return st["dauName"]
    return "露丝"


def _name_index(book: dict, dau: str) -> dict:
    """名字 → {risky, own: [键], crowd: [键]}；露丝另算。"""
    idx: dict = {}
    for key, e in book.items():
        if not isinstance(e, dict):
            continue
        what, name = _what(key, e), _name(key, e)
        if what == "dau":
            continue
        if what == "crowd":
            for m in crowd_members(name):
                if _good_name(m):
                    idx.setdefault(m, {"own": [], "crowd": []})["crowd"].append(key)
            continue
        for alias in dict.fromkeys((name, _base(name))):  # 「冰霜峡谷（探险）」聊天里写的是「冰霜峡谷」
            if _good_name(alias):
                idx.setdefault(alias, {"own": [], "crowd": []})["own"].append(key)
    idx[dau] = {"own": [], "crowd": [], "dau": True}
    for name, info in idx.items():
        info["risky"] = len(name) == 1 or name in ROLE_WORDS
    return idx


def _rank(book: dict, key: str) -> tuple:
    what = _what(key, book[key])
    return (WHAT_ORDER.index(what) if what in WHAT_ORDER else 99, book[key].get("first") or "")


def _dau_key(book: dict, dau: str, ts: str) -> str:
    """露丝的话放进那时候她那个样子的条目（first 不晚于这句话的最后一条 dau）；还没有就建 dau:露丝。
    只在这个女儿自己的条目里挑：别的女儿（上一局的）那几条不算。"""
    keys = sorted((k for k, e in book.items() if isinstance(e, dict) and _what(k, e) == "dau" and _dau_of(k, e) == dau),
                  key=lambda k: book[k].get("first") or "")
    if not keys:
        key = f"dau:{dau}"
        book[key] = {"name": dau, "what": "dau", "image": None, "note": None, "first": ts, "words": []}
        return key
    earlier = [k for k in keys if (book[k].get("first") or "") <= ts]
    return earlier[-1] if earlier else keys[0]


def _target(book: dict, info: dict, dau: str, ts: str) -> str:
    if info.get("dau"):
        return _dau_key(book, dau, ts)
    if info["own"]:
        return min(info["own"], key=lambda k: _rank(book, k))
    return min(info["crowd"], key=lambda k: book[k].get("first") or "")


def _word(ts: str, text: str, frm: str, st: dict | None) -> dict:
    w = {"ts": ts, "text": text, "from": frm}
    if st and isinstance(st.get("turn"), int):
        w.update(turn=st["turn"], year=st.get("year"), month=st.get("month"))
    return w


def _add_word(entry: dict, w: dict) -> bool:
    words = entry.setdefault("words", [])
    if not isinstance(words, list):
        entry["words"] = words = []
    if any(isinstance(x, dict) and x.get("text") == w["text"] for x in words):
        return False
    words.append(w)
    return True


def _pool(book: dict, name: str, dau: str, dau_view: bool = False) -> list[tuple[str, dict]]:
    """一个名字的全部话：同名的条目合在一起；露丝 = 她自己的那些 dau 条目（别的女儿的不算）；群像里只算提到这个名字的话。"""
    out = []
    for key, e in book.items():
        if not isinstance(e, dict):
            continue
        what, nm = _what(key, e), _name(key, e)
        words = [w for w in (e.get("words") or []) if isinstance(w, dict) and w.get("text")]
        same = nm == name or _base(nm) == _base(name)
        mine = what == "dau" and _dau_of(key, e) == dau and (dau_view or name == dau)
        if mine or (not dau_view and what not in ("dau", "crowd") and same):
            out += [(key, w) for w in words]
        elif not dau_view and what == "crowd" and name in crowd_members(nm):
            out += [(key, w) for w in words if name in w["text"]]
    return out


def _entries_named(book: dict, name: str, dau: str) -> list[str]:
    if ":" in name and name in book:
        return [name]
    keys = [k for k, e in book.items() if isinstance(e, dict) and _what(k, e) not in ("dau", "crowd")
            and (_name(k, e) == name or _base(_name(k, e)) == _base(name))]
    if name == dau:
        keys += [k for k, e in book.items() if isinstance(e, dict) and _what(k, e) == "dau" and _dau_of(k, e) == dau
                 and k not in keys]
    return sorted(keys, key=lambda k: _rank(book, k))


# ---- 1. 收话 ----
def collect(data=DEFAULT_DATA, again: bool = False) -> int:
    """chat.jsonl 里 agent 新说的话，提到印象簿里名字的句子收进 words（from: panel）。返回收了几句。"""
    data = Path(data)
    chat = data / "chat.jsonl"
    try:
        size = chat.stat().st_size
    except OSError:
        return 0
    mem = _memory(data)
    offset = 0 if again else int(mem.get("chat") or 0)
    if offset > size:  # chat.jsonl 换过了
        offset = 0
    with open(chat, "rb") as f:
        f.seek(offset)
        raw = f.read()
    end = raw.rfind(b"\n")
    if end < 0:
        return 0
    # 开了玩家文件夹：只收自己说的（面板上每句话记着是谁，who）。别人的话不进我的印象簿
    mine = vp_players.identity(data)["label"] if vp_players.layout_on(data) else None
    # 也只收这个玩家来了以后（players.json 里它的 first）说的。玩家文件夹被挪走、同一个名字再来时，新文件夹里没有
    # memory.json，要从 chat.jsonl 开头读；聊天服务这时把 first 记成重新来的时候，旧文件夹那时说的话就不会又收进新印象簿
    since = ""
    if mine is not None:
        entry = vp_players.players(data).get(mine)
        since = norm_ts(entry.get("first")) if isinstance(entry, dict) else ""
    msgs = []
    for line in raw[:end].splitlines():
        try:
            m = json.loads(line.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            continue
        if isinstance(m, dict) and m.get("from") == "agent" and isinstance(m.get("text"), str):
            if mine is None or (m.get("who") == mine and (not since or norm_ts(m.get("ts")) >= since)):
                msgs.append(m)
    added = 0
    if msgs:
        events = _events(data)
        tl = _timeline(events)
        dau = _dau_name(data, events)

        def apply(book):
            n = 0
            names = _name_index(book, dau)
            for m in msgs:
                ts = norm_ts(m.get("ts")) or _now()
                st = _state_at(tl, ts)
                for s in sentences(m["text"]):
                    if _tool_talk(s):
                        continue
                    done = set()
                    for i, _, name in _find_names(s, names):
                        key = _target(book, names[name], dau, ts)
                        if key in done:
                            continue
                        done.add(key)
                        if _add_word(book[key], _word(ts, _clip(s, i), "panel", st)):
                            n += 1
            return n, n > 0

        try:
            added = _update_book(data, apply)
        except RuntimeError:
            return 0  # 印象簿这会儿读不懂（插件正在写？）：游标不动，下次再收
    mem["chat"] = offset + end + 1
    _save_memory(data, mem)
    return added


# ---- 2. 记一句 ----
NOTE_REPLIES = ["（记在心里了。）", "（{name}的这一句，收进印象簿了。）", "（记下了。下回见到{name}，这句还在。）"]


def note(data, name: str, text: str) -> str:
    """agent 自己记一句（from: note）。没有这个条目就新建 note:名字。"""
    data = Path(data)
    name, text = (name or "").strip(), (text or "").strip()
    if not name or not text:
        return "（写成 note 名字 话。）"
    events = _events(data)
    dau = _dau_name(data, events)
    st = _current(data, tl=_timeline(events))
    ts = _now()

    def apply(book):
        if ":" in name and name in book:
            key = name
        elif name == dau:
            key = _dau_key(book, dau, ts)
        else:
            keys = [k for k in _entries_named(book, name, dau) if _what(k, book[k]) != "crowd"]
            key = keys[0] if keys else f"note:{name}"
        fresh = key not in book
        if fresh:
            book[key] = {"name": name, "what": "note", "image": None, "note": None, "first": ts, "words": []}
        if not _add_word(book[key], _word(ts, text, "note", st)):
            return "（这句已经记着了。）", False
        shown = _name(key, book[key])
        if fresh:
            return f"（印象簿里新起了一页「{shown}」，记下了。）", True
        n = sum(1 for e in book.values() if isinstance(e, dict)
                for w in (e.get("words") or []) if isinstance(w, dict) and w.get("from") == "note")
        return NOTE_REPLIES[n % len(NOTE_REPLIES)].format(name=shown), True

    try:
        return _update_book(data, apply)
    except RuntimeError:
        return "（印象簿这会儿翻不开，过一会儿再记一次。）"


# ---- 3. 回音 ----
def _triggers(events: list[dict], dau: str, fa: str | None) -> list[tuple[str, str, dict]]:
    """这一批里出现的人和地方，(种类, 名字, 那条事件)，按先后；第一次见的排前面，露丝的新样子最前。"""
    found: dict[str, tuple[int, int, str, dict]] = {}

    def add(prio, i, name, kind, e):
        name = (name or "").strip()
        if name and (name not in found or found[name][0] > prio):
            found[name] = (prio, i, kind, e)

    for i, e in enumerate(events):
        t = e.get("type")
        if t == "sight":
            what, name = e.get("what"), e.get("name") or ""
            if what == "dau":
                add(0, i, "\0dau", "dau", e)
            elif what == "crowd":
                for m in crowd_members(name):
                    add(1, i, m, "face", e)
            elif _good_name(name.strip()):
                add(1, i, name, what or "face", e)
        elif t == "scene":
            add(2, i, e.get("to"), "place", e)
        elif t == "fight" and e.get("what") == "start":
            # 开战（「敌1 青鹿、敌2 毒蛾（飞）（绿洲森林）」）：note 记过的怪物再遇到也带一句。
            # 怪物的 sight 只在第一次见时有，不从开战这一行找，再遇到时就什么都带不出来
            for part in re.sub(r"，交给队友打$", "", e.get("text") or "").split("、"):
                name = re.sub(r"（[^）]*）", "", re.sub(r"^敌\d+\s*", "", part)).strip()
                if _good_name(name):
                    add(2, i, name, "monster", e)
        elif t == "line":
            who = (e.get("who") or "").strip()
            if _good_name(who) and who not in (dau, fa):
                add(3, i, who, "face", e)
    return [(kind, name, e) for name, (_, _, kind, e) in sorted(found.items(), key=lambda kv: kv[1][:2])]


def _echoed_this_year(records: list[dict], now: dict | None, now_ts: str) -> bool:
    for r in records:
        if now and r.get("year") and now.get("year"):
            if r["year"] == now["year"] and (not isinstance(r.get("turn"), int) or not isinstance(now.get("turn"), int)
                                             or abs(now["turn"] - r["turn"]) < 12):
                return True
        elif abs(_hours(r.get("ts") or now_ts, now_ts)) < 6:
            return True
    return False


def _echo_head(kind: str, name: str, dau: str, e: dict) -> str:
    """回音的前半句。插件的 sight 带着 why：露丝是 first / grow / costume，地方是 first / season。"""
    why = e.get("why") if e.get("type") == "sight" else ""
    name = _base(name) or name
    if kind == "dau":
        if why == "grow":
            return f"{dau}长大了。"
        if why == "costume" and e.get("cloth"):
            return f"{dau}换上了{e['cloth']}。"
        return f"这是现在的{dau}。"
    if kind == "place":
        if why == "season" and e.get("year"):
            return f"又来到{name}，已经是{e['year']}的样子了。"
        return "回到家。" if name == "家" else f"又来到{name}。"
    if kind in ("monster", "boss"):
        return f"又遇到{name}。"
    if kind in ("cg", "art"):
        return f"{name}。"
    return f"又见到{name}。"


def echo(data, events) -> str:
    """这一批事件里再见到的人、地方、露丝的新样子：带一句从前说过的话。没有就返回空字符串。"""
    data = Path(data)
    events = [e for e in (events or []) if isinstance(e, dict)]
    if not events:
        return ""
    book = _book(data)
    if not any(isinstance(e, dict) and e.get("words") for e in book.values()):
        return ""
    tl = _timeline(_events(data))
    now = _current(data, events, tl)
    stamps = [norm_ts(e.get("ts")) for e in events if e.get("ts")]
    now_ts = max(stamps) if stamps else _now()
    dau = (now or {}).get("dauName") or _dau_name(data)
    fa = (now or {}).get("faName")
    mem = _memory(data)
    echoed = mem.setdefault("echoed", {})
    for kind, name, ev in _triggers(events, dau, fa):
        dau_view = kind == "dau"
        tag = f"dau:{dau}" if dau_view else name
        if _echoed_this_year(echoed.get(tag) or [], now, now_ts):
            continue
        said_before = {r.get("text") for r in echoed.get(tag) or []}
        best = None
        for _, w in _pool(book, dau if dau_view else name, dau, dau_view):
            ts = norm_ts(w.get("ts"))
            if ts > now_ts:
                continue  # 这一批之后才说的
            ws = _word_state(w, tl)
            if ws and now and isinstance(ws.get("turn"), int) and isinstance(now.get("turn"), int):
                if ws["turn"] >= now["turn"]:
                    continue  # 这个月说的，还在上下文里，不用带
            elif _hours(ts, now_ts) < 6:
                continue
            older_year = 2 if ws and now and _year_index(ws) != _year_index(now) else 0
            score = _voice(w, None if dau_view else name) + older_year - (5 if w["text"] in said_before else 0)
            cand = (score, [-ord(c) for c in ts], w, ws)  # 分数一样时挑早的
            if best is None or cand[:2] > best[:2]:
                best = cand
        if best is None:
            continue
        w, ws = best[2], best[3]
        said = "你记下过" if w.get("from") == "note" else "你说过"
        line = f"（{_echo_head(kind, name, dau, ev)}{_when(ws, now, dau, dau_view)}，{said}：「{_requote(w['text'])}」）"
        echoed.setdefault(tag, []).append({"ts": now_ts, "year": (now or {}).get("year"),
                                           "turn": (now or {}).get("turn"), "text": w["text"]})
        _save_memory(data, mem)
        return line
    return ""


# ---- 4. feedback ----
FEEDBACK_REPLIES = [vp_players.named(t) for t in (
    "（记下了，user 会看。）", "（收到。user 会看，以后改插件的人也会看到这句。）",
    "（记下了。哪里别扭、哪里好玩，想到就说。）")]


def feedback(data, text: str) -> str:
    """追加到 selfplay/feedback.jsonl：{ts, text, turn}。"""
    data = Path(data)
    text = (text or "").strip()
    if not text:
        return "（想说什么，写在 feedback 后面就好。）"
    st = _current(data)
    path = _sp(data) / "feedback.jsonl"
    path.parent.mkdir(parents=True, exist_ok=True)
    rec = {"ts": _now(), "text": text, "turn": (st or {}).get("turn")}
    with open(path, "a", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    n = sum(1 for _ in open(path, encoding="utf-8"))
    return FEEDBACK_REPLIES[(n - 1) % len(FEEDBACK_REPLIES)]


def _feedback_rows(data) -> list[dict]:
    rows = []
    try:
        lines = (_sp(data) / "feedback.jsonl").read_text(encoding="utf-8").splitlines()
    except OSError:
        return rows
    for line in lines:
        try:
            r = json.loads(line)
        except ValueError:
            continue
        if isinstance(r, dict) and r.get("text"):
            rows.append(r)
    return rows


# ---- 5. 上回说到 ----
def _year_spans(events: list[dict]) -> list[dict]:
    """按年切段：连着的同一年算一段；第一次换月之前的事件（开场）归第一段。"""
    spans: list[dict] = []
    for i, e in enumerate(events):
        st = e.get("state")
        if e.get("type") not in ("month", "load") or not isinstance(st, dict) or not st.get("year"):
            continue
        if not spans:
            spans.append({"year": st["year"], "start": 0})
        elif st["year"] != spans[-1]["year"]:
            spans[-1]["end"] = i
            spans.append({"year": st["year"], "start": i})
    if spans:
        spans[-1]["end"] = len(events)
    return spans


def _grow_text(a: dict | None, b: dict | None, dau: str) -> list[str]:
    if not a or not b or a is b:
        return []
    out = []
    h0, h1 = a.get("height"), b.get("height")
    if isinstance(h0, (int, float)) and isinstance(h1, (int, float)) and h1 > h0:
        out.append(f"{dau}长高了 {round(h1 - h0, 1)} 厘米，到了 {h1}。")
    parts = []
    for group in ("nature", "inAttri"):
        for k, v in (b.get(group) or {}).items():
            v0 = (a.get(group) or {}).get(k)
            if isinstance(v, (int, float)) and isinstance(v0, (int, float)) and v != v0:
                parts.append(f"{k} {'+' if v > v0 else ''}{round(v - v0, 1)}（{v}）")
    if parts:
        out.append("、".join(parts) + "。")
    parts = []
    if None not in (a.get("favorLevel"), b.get("favorLevel")) and a["favorLevel"] != b["favorLevel"]:
        parts.append(f"好感 Lv.{a['favorLevel']} → Lv.{b['favorLevel']}")
    for key, name in (("coin", "金币"), ("fame", "声望"), ("lover", "爱慕者"), ("mood", "心情"),
                      ("darkness", "黑暗"), ("inspiration", "感悟")):
        x, y = a.get(key), b.get(key)
        if isinstance(x, (int, float)) and isinstance(y, (int, float)) and x != y:
            parts.append(f"{name} {x} → {y}")
    if parts:
        out.append("，".join(parts) + "。")
    return out


def _prompt_line(events: list[dict], i: int, lo: int) -> dict | None:
    """选项前面最近的一句台词（往回最多 12 条，碰到换月、换场景、别的选择就停）。"""
    for j in range(i - 1, max(lo, i - 12) - 1, -1):
        t = events[j].get("type")
        if t == "line":
            return events[j]
        if t in ("month", "load", "scene", "chose"):
            return None
    return None


def _prompt_text(line: dict, dau: str, fa: str | None) -> str:
    who = (line.get("who") or "").strip()
    say = _short((line.get("say") or "").strip().strip('"“”'), 30)
    if who in (fa, "旁白") or not who:
        return f"（{say.strip('（）()')}）"
    verb = "问" if say.rstrip("…").endswith(("？", "?", "呢", "吗")) else "说"
    return f"{'有人' if not _good_name(who) else who}{verb}「{say}」，"


def _choices(events: list[dict], lo: int, hi: int, all_events: list[dict], dau: str, fa: str | None) -> list[str]:
    counts: dict[str, int] = {}
    for e in all_events:
        if e.get("type") == "chose":
            t = _strip_markup(e.get("text") or "").strip()
            counts[t] = counts.get(t, 0) + 1
    whats: dict[str, list[str]] = {}
    partners: list[str] = []
    singles: list[tuple[str, str]] = []
    for i in range(lo, hi):
        e = events[i]
        if e.get("type") != "chose":
            continue
        text = _strip_markup(e.get("text") or "").strip()
        if not text:
            continue
        oi = next((j for j in range(i - 1, max(lo, i - 10) - 1, -1) if events[j].get("type") == "options"), None)
        opt = events[oi] if oi is not None else None
        if opt and opt.get("what"):
            seq = whats.setdefault(opt["what"], [])
            if not seq or seq[-1] != text:
                seq.append(text)
            continue
        line = _prompt_line(events, oi if oi is not None else i, lo)
        if "邀为探险伙伴" in text:
            if line and line.get("who") and line["who"].strip() not in partners:
                partners.append(line["who"].strip())
            continue
        if re.match(r"^\d+\.\s", text) or counts.get(text, 0) >= 3 or (not line and re.search(r"\d|%", text)):
            continue  # 和人说话时的菜单（聊天、请教）、每次都一样的（去叫露丝）、探险里的泉水（生命回复 50%）
        singles.append((_prompt_text(line, dau, fa) if line else "", text))
    out = [f"{what}：{' → '.join(seq)}。" for what, seq in whats.items()]
    if partners:
        out.append(f"邀了{'、'.join(partners)}一起去探险。")
    grouped: dict[str, list[str]] = {}
    for p, t in singles:
        grouped.setdefault(p, []).append(t)
    rest = []
    for p, ts in grouped.items():
        if len(ts) > 1:
            out.append(_tidy(f"{p}你先后选了{''.join('「' + _requote(t) + '」' for t in ts)}。"))
        else:
            rest.append(_tidy(f"{p}你选了「{_requote(ts[0])}」。"))
    if len(rest) > 8:  # 太多就隔几个挑一个，一年里前后都有
        rest = [rest[round(k * (len(rest) - 1) / 7)] for k in range(8)]
    return out + rest


def _first_seen(events: list[dict], lo: int, hi: int, dau: str, fa: str | None) -> list[str]:
    sights = [e for e in events if e.get("type") == "sight"]
    groups: dict[str, list[str]] = {}
    if sights:  # 插件写了「第一次见」：照它的
        for e in events[lo:hi]:
            if e.get("type") != "sight" or not e.get("name"):
                continue
            what = e.get("what") or "face"
            names = crowd_members(e["name"]) if what == "crowd" else [e["name"].strip()]
            label = "女儿的新样子" if what == "dau" else WHAT_CH.get(what, what)
            seq = groups.setdefault(label, [])
            seq += [n for n in names if n not in seq]
    else:  # 一条 sight 都没有：按台词里第一次露脸、第一次去的地方算
        first: dict[str, int] = {}
        place_first: dict[str, int] = {}
        talk: dict[str, int] = {}
        for i, e in enumerate(events):
            if e.get("type") == "line":
                who = (e.get("who") or "").strip()
                if _good_name(who) and who not in (dau, fa):
                    first.setdefault(who, i)
                    talk[who] = talk.get(who, 0) + 1
            elif e.get("type") == "scene" and e.get("to"):
                place_first.setdefault(e["to"].strip(), i)
        people = [n for n, i in first.items() if lo <= i < hi and "的" not in n and not GENERIC_RE.search(n)]
        if people:
            groups["人"] = people
        places = [n for n, i in place_first.items() if lo <= i < hi]
        if places:
            groups["地方"] = places
        top = sorted(groups.get("人", []), key=lambda n: -talk[n])[:12]
        if "人" in groups:
            more = len(groups["人"]) - len(top)
            groups["人"] = [n for n in groups["人"] if n in top] + ([f"……（还有 {more} 位）"] if more > 0 else [])
    out = []
    for label, names in groups.items():
        names = [n for n in names if n]
        if not names:
            continue
        if label == WHAT_CH["cg"]:  # CG 的名字是编号（「10、38、60」读不出是什么），只说几张
            out.append(f"{len(names)} 张 CG")
            continue
        out.append(f"{'、'.join(names[:14])}（{label}）")
    return out


def recap(data=DEFAULT_DATA, year: str | None = None) -> str:
    """上回说到：一年里的长大、大事、选择、第一次见、说过的话、feedback。"""
    data = Path(data)
    events = _events(data)
    spans = _year_spans(events)
    if not spans:
        return "（还没有能说的：还没换过月。）"
    if year:
        want = next((y for y in YEARS if y == year or y.startswith(year[:1])), year)
        hits = [s for s in spans if s["year"] == want]
        if not hits:
            have = "、".join(dict.fromkeys(s["year"] for s in spans))
            return f"（{want}还没有记录。有的是：{have}。）"
        span = hits[-1]
    else:
        span = spans[-1]
        months = sum(1 for e in events[span["start"]:span["end"]] if e.get("type") == "month")
        if months <= 1 and len(spans) > 1:  # 刚换年：说的是上一年
            span = spans[-2]
    lo, hi = span["start"], span["end"]
    k = spans.index(span)
    nxt = spans[k + 1] if k + 1 < len(spans) else None
    states = [e["state"] for e in events[lo:hi] if e.get("type") in ("month", "load") and isinstance(e.get("state"), dict)]
    start = states[0] if states else None
    end = events[nxt["start"]]["state"] if nxt else (states[-1] if states else None)
    if not nxt:
        sts, status = _state_json(data)
        if status and status.get("year") == span["year"] and status.get("turn", -1) >= (end or {}).get("turn", -1):
            end = status
    dau = (end or start or {}).get("dauName") or "露丝"
    fa = (end or start or {}).get("faName")
    t0 = norm_ts(events[lo].get("ts")) if lo < len(events) else ""
    t1 = norm_ts(events[nxt["start"]].get("ts")) if nxt else "9999"

    out = [f"【上回说到 · {span['year']}】"]
    ident = None
    for i, e in enumerate(events):
        if e.get("type") == "options" and e.get("what") == "爸爸的身份":
            ident = next((x.get("text") for x in events[i + 1:i + 10] if x.get("type") == "chose"), ident)
    m0 = (start or {}).get("month")
    if isinstance(m0, int) and lo < len(events) and events[lo].get("type") not in ("month", "load"):
        m0 = max(1, m0 - 1)  # 开场那个月还没换过月
    m1 = (states[-1] if states else {}).get("month")
    if ident:
        out.append(f"你想做的是「{_strip_markup(ident)}」。")
    head = f"{span['year']}，从 {m0} 月到 {m1} 月。" if m0 and m1 else f"{span['year']}。"
    out.append(head + "".join(_grow_text(start, end, dau)))

    courses, others, achievements, skipped = [], [], [], []
    for e in events[lo:hi]:
        t = e.get("type")
        if t == "celebrate":
            text = e.get("text") or ""
            m = re.search(r"完成课程：(.+?)[！!]", text)
            if m:
                courses.append(m.group(1))
            else:
                lines = [x.strip() for x in text.splitlines() if x.strip() and "———" not in x and x.strip() != "确认"]
                if lines:
                    others.append(f"「{lines[0]}」" + (f"（{lines[1].rstrip('。')}）" if len(lines) > 1 else ""))
        elif t == "achievement" and e.get("name"):
            achievements.append(f"「{e['name']}」")
        elif t == "skip" and e.get("game"):
            skipped.append(e["game"] + (f"（{e['result']}）" if e.get("result") else ""))
    big = []
    if courses:
        big.append(f"学完了{'、'.join(courses)}")
    if others:
        big.append("得到" + "、".join(others))
    if achievements:
        big.append(f"拿到成就{'、'.join(achievements)}")
    if skipped:
        big.append(f"跳过的小游戏：{'、'.join(skipped)}")
    if big:
        out.append("这一年" + "；".join(big) + "。")

    picks = _choices(events, lo, hi, events, dau, fa)
    if picks:
        out.append("\n你做过的选择：")
        out += [f"· {p}" for p in picks]

    seen = _first_seen(events, lo, hi, dau, fa)
    if seen:
        out.append("\n第一次见到：" + "；".join(seen) + "。")

    book = _book(data)
    words: dict[str, tuple[str, dict]] = {}
    for key, e in book.items():
        if not isinstance(e, dict):
            continue
        for w in e.get("words") or []:
            if isinstance(w, dict) and w.get("text") and t0 <= norm_ts(w.get("ts")) < t1:
                prev = words.get(w["text"])
                if prev is None or (_what(prev[0], book[prev[0]]) == "dau" and _what(key, e) != "dau"):
                    words[w["text"]] = (key, w)
    if words:
        top = sorted(words.values(), key=lambda kw: -_voice(kw[1], _name(kw[0], book[kw[0]])))[:4]
        out.append("\n这一年你说过：")
        for key, w in sorted(top, key=lambda kw: norm_ts(kw[1].get("ts"))):
            what = _what(key, book[key])
            who = _dau_of(key, book[key]) if what == "dau" else _name(key, book[key])
            if what == "crowd":
                who = next((m for m in crowd_members(who) if m in w["text"]), who)
            how = "记下" if w.get("from") == "note" else "说起"
            out.append(f"· {how}{who}：「{_requote(_short(w['text'], 70))}」")

    fb = [r for r in _feedback_rows(data) if t0 <= norm_ts(r.get("ts")) < t1]
    if fb:
        out.append(vp_players.named("\n你留给 user 的话："))
        out += [f"· {_short(r['text'], 80)}" for r in fb[-5:]]

    out.append(f"\n{span['year']}玩得怎么样？想说就说（feedback 话），不说也行。")
    return "\n".join(out)


# ---- 6. look 用 ----
def words_for(data, name: str) -> list[dict]:
    """这个名字收到的话，按时间排；每条多一个 key，写它在哪个条目里。"""
    data = Path(data)
    name = (name or "").strip()
    book = _book(data)
    dau = _dau_name(data)
    if ":" in name and name in book:
        pairs = [(name, w) for w in (book[name].get("words") or []) if isinstance(w, dict)]
    else:
        pairs = _pool(book, name, dau, dau_view=(name == dau))
    return [dict(w, key=k) for k, w in sorted(pairs, key=lambda kw: norm_ts(kw[1].get("ts")))]


def card(data, name: str, limit: int = 6) -> str:
    """look 名字：印象簿里这一页。图给完整路径，Read 就能看。"""
    data = Path(data)
    name = (name or "").strip()
    book = _book(data)
    events = _events(data)
    dau = _dau_name(data, events)
    keys = _entries_named(book, name, dau)
    words = words_for(data, name)
    if not keys and not words:
        return f"印象簿里还没有「{name}」。想记一句就 note {name} 话。"
    tl = _timeline(events)
    out = []
    for key in keys:
        e = book[key]
        what = _what(key, e)
        head = f"[印象簿] {_name(key, e)}（{WHAT_CH.get(what, what)}）"
        first = norm_ts(e.get("first"))
        label = "第一次记" if what == "note" else "第一次见"
        if e.get("when"):  # 插件建条目时写的游戏时间
            head += f"  {label}：{e['when']}"
        elif first:
            st = _state_at(tl, first)
            head += f"  {label}：{st['year']} {st['month']} 月" if st else f"  {label}：{first[:10]}"
        out.append(head)
        if e.get("image"):
            out.append(f"  图：{_sp(data) / e['image']}")
        if e.get("note"):
            out.append(f"  介绍：{e['note']}")
    if words:
        now = _current(data, tl=tl)
        shown = words if len(words) <= limit else sorted(
            sorted(words, key=lambda w: -_voice(w, name))[:limit], key=lambda w: norm_ts(w.get("ts")))
        more = f"（共 {len(words)} 句，挑了 {len(shown)} 句）" if len(shown) < len(words) else ""
        out.append(f"你说过{more}：")
        for w in shown:
            when = _when(_word_state(w, tl), now, dau, name == dau)
            out.append(f"  · {when}：「{_requote(w['text'])}」" + ("（自己记的）" if w.get("from") == "note" else ""))
    return "\n".join(out)


# ---- 单独跑 ----
def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data", type=Path, default=DEFAULT_DATA)
    sub = p.add_subparsers(dest="command", required=True)
    c = sub.add_parser("collect")
    c.add_argument("--again", action="store_true", help="从头再扫一遍")
    c = sub.add_parser("note")
    c.add_argument("name")
    c.add_argument("text", nargs="+")
    c = sub.add_parser("echo", help="拿 events.jsonl 里最后 N 条当这一批（试回音用）")
    c.add_argument("-n", type=int, default=50)
    c = sub.add_parser("feedback")
    c.add_argument("text", nargs="+")
    c = sub.add_parser("recap")
    c.add_argument("year", nargs="?")
    for name in ("words", "card"):
        sub.add_parser(name).add_argument("name")
    a = p.parse_args()
    if a.command == "collect":
        print(f"收了 {collect(a.data, a.again)} 句")
    elif a.command == "note":
        print(note(a.data, a.name, " ".join(a.text)))
    elif a.command == "echo":
        print(echo(a.data, _events(a.data)[-a.n:]) or "（这一批没有可带的话）")
    elif a.command == "feedback":
        print(feedback(a.data, " ".join(a.text)))
    elif a.command == "recap":
        print(recap(a.data, a.year))
    elif a.command == "words":
        for w in words_for(a.data, a.name):
            print(f"{w.get('ts')}  [{w.get('key')}] {w.get('text')}")
    elif a.command == "card":
        print(card(a.data, a.name))
    return 0


if __name__ == "__main__":
    sys.exit(main())
