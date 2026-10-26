"""火山的女儿：谁的是谁的（docs/设计理念.md 第十一节）。

一个玩家（来玩的 LLM，带着名字）一个文件夹：data/selfplay/players/<文件夹>/ 里放它自己的 inbox、outbox、events、
印象簿、scenes/、memory.json、feedback.jsonl、cursor.json。共用的只有 data/selfplay/state.json、modes.json 和面板的 chat.jsonl。

  data/selfplay/players.json   {"players": {"<名字>": {"dir": "players/<文件夹>", "first": "第一次来的时间"}}}，聊天服务建、维护
  data/selfplay/active.json    {"player": "<名字>", "dir": "players/<文件夹>", "since": 时间, "inbox": 交班那一刻 inbox 的字节数}，
                               聊天服务交班时写；没人在玩时 player 是空的。插件照它认谁在玩，交班前的命令不执行
  data/selfplay/session-names.json  {"<Claude Code 会话号>": "名字"}：命令行 hello 名字 报上来的名字（vp_player 写）

开关：players.json 存在才走玩家文件夹；不存在就都在 data/selfplay/ 下。搬家脚本 migrate_players.py 建它（游戏关着时跑）。
插件、聊天服务、hub、vp_player 都只看这一个文件在不在，所以它一出现大家一起换，不会一边按玩家文件夹读写、
一边还写在 data/selfplay/ 下。

名字（环境变量，没设或是空的就用默认）：
  LUDUS_USER_NAME   人的名字，默认 user。vp_player、vp_memory 给 agent 看的字里说到人的地方都换成它（named()）
  LUDUS_AGENT_NAME  agent 的名字，默认 agent。没报过名字的会话按它起默认名：「agent（终端）」「agent（桌面 app）」
                    「agent（claude.ai）」（identity()）。channel 报到时用同一个写法，两边的名字才对得上

vp_player、vp_memory、migrate_players 共用这里，只用标准库。
"""
from __future__ import annotations

import json
import os
import re
from pathlib import Path

INVALID = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
RESERVED = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}

USER_NAME = (os.environ.get("LUDUS_USER_NAME") or "").strip() or "user"
AGENT_NAME = (os.environ.get("LUDUS_AGENT_NAME") or "").strip() or "agent"


def names_note() -> str:
    """名字还是默认的（没设 LUDUS_USER_NAME / LUDUS_AGENT_NAME）时，提醒 agent 问问人、把名字设上的一句话；都设了就是空的。
    和 hub（mcp-hub/tools.mjs 的 namesNote）、channel 说明里的是同一句话。"""
    said, env = [], []
    if not (os.environ.get("LUDUS_USER_NAME") or "").strip():
        said.append("「user」是陪你玩的人")
        env.append("LUDUS_USER_NAME（TA 的名字）")
    if not (os.environ.get("LUDUS_AGENT_NAME") or "").strip():
        said.append("「agent」是你")
        env.append("LUDUS_AGENT_NAME（你的名字）")
    if not said:
        return ""
    return f"名字还是占位的：说明里{'、'.join(said)}。可以把 {'、'.join(env)}设上（docs/安装.md 第五节「名字」：setx 一次，重开聊天服务、hub 和你的客户端），之后满眼都是你们俩的名字啦。"

# 写死的字里的「user」：前后不是英文字母、数字（「user_name」「users」不算）；两边挨着中文的空格一起带上，好按名字去留
_USER_WORD = re.compile(r"((?<=[^\x00-\x7f]) )?(?<![A-Za-z0-9_])user(?![A-Za-z0-9_])( (?=[^\x00-\x7f]))?")


def named(text: str) -> str:
    """给 agent 看的字里的「user」换成人的名字（LUDUS_USER_NAME）。名字头尾是中文时，和旁边中文之间的空格去掉：
    「跟 user 说」→「跟小雨说」；英文名留着空格：「跟 Mia 说」。
    只拿来换写死的字：别套在带路径、带聊天内容的字上（路径里可能就有 user 这个词）。"""
    if USER_NAME == "user":
        return text

    def sub(m: re.Match) -> str:
        left = (m.group(1) or "") if USER_NAME[0].isascii() else ""
        right = (m.group(2) or "") if USER_NAME[-1].isascii() else ""
        return left + USER_NAME + right

    return _USER_WORD.sub(sub, text)


def safe_dir(name: str) -> str:
    """名字 → Windows 上安全的文件夹名（和聊天服务 server.mjs 的 safeDir 一样）。重名的加 -2、-3 由聊天服务管。"""
    s = INVALID.sub("_", str(name)).strip().rstrip(". ")
    s = re.sub(r"\s+", " ", s)[:60].rstrip(". ")
    if not s:
        s = "player"
    if s.split(".")[0].upper() in RESERVED:
        s = "_" + s
    return s


def root(data) -> Path:
    return Path(data) / "selfplay"


def _read(path: Path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return default


def layout_on(data) -> bool:
    """players.json 在不在：在就按玩家分文件夹"""
    return (root(data) / "players.json").exists()


def players(data) -> dict:
    p = _read(root(data) / "players.json", {})
    return p.get("players", {}) if isinstance(p, dict) and isinstance(p.get("players"), dict) else {}


def active(data) -> dict:
    """现在在玩的：{"player", "dir", "since", …}；没人在玩是空的 player"""
    a = _read(root(data) / "active.json", {})
    return a if isinstance(a, dict) else {}


def session_names(data) -> dict:
    n = _read(root(data) / "session-names.json", {})
    return n if isinstance(n, dict) else {}


def set_session_name(data, name: str) -> None:
    """命令行 hello 名字：这个会话以后用这个名字（存在 session-names.json）"""
    key = os.environ.get("CLAUDE_CODE_SESSION_ID") or "cli"
    names = session_names(data)
    names[key] = name
    path = root(data) / "session-names.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(names, ensure_ascii=False, indent=1), encoding="utf-8")
    os.replace(tmp, path)


def identity(data) -> dict:
    """我是谁：{"id": 报到用的 id, "label": 名字（也是玩家名）}。
    hub 起的带 LUDUS_PLAYER（信箱的名字、MCP 的客户端名）；Claude Code 里按会话分（hello 名字 报过的用那个名字，
    没报过按会话是从哪儿开的写，前面是 LUDUS_AGENT_NAME）；不在 Claude Code 里是「命令行」。"""
    env_name = (os.environ.get("LUDUS_PLAYER") or "").strip()
    if env_name:
        return {"id": os.environ.get("LUDUS_PLAYER_ID") or f"player:{env_name}", "label": env_name}
    sid = os.environ.get("CLAUDE_CODE_SESSION_ID")
    named = session_names(data).get(sid or "cli")
    if not sid:
        return {"id": "vp:cli", "label": named or "命令行"}
    if named:
        return {"id": f"claude:{sid}", "label": named}
    entry = os.environ.get("CLAUDE_CODE_ENTRYPOINT", "")
    label = {"cli": f"{AGENT_NAME}（终端）", "claude-desktop": f"{AGENT_NAME}（桌面 app）"}.get(entry)
    if not label:
        label = f"{AGENT_NAME}（claude.ai）" if re.search(r"remote|web", entry) else f"Claude Code（{entry or '?'}）"
    return {"id": f"claude:{sid}", "label": label}


def player_dir(data, name: str) -> Path:
    """这个玩家的文件夹：players.json 里记的；还没记（没玩过）就是按名字算出来的那个（多半还不存在）"""
    entry = players(data).get(name)
    rel = entry.get("dir") if isinstance(entry, dict) else None
    return root(data) / (rel or f"players/{safe_dir(name)}")


def my_dir(data) -> Path:
    """我的东西放在哪：没开玩家文件夹时就是 data/selfplay/。
    命令写进这里的 inbox：插件每个玩家的 inbox 都看着，在玩的照常执行，别人的只回答只看不动的和 hello（插件 Players.cs）"""
    if not layout_on(data):
        return root(data)
    return player_dir(data, identity(data)["label"])
