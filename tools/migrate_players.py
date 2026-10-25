"""搬家：打开「一个玩家一个文件夹」（docs/设计理念.md 第十一节），把 data/selfplay/ 下原有的一套搬进玩家文件夹。

    python -X utf8 tools/migrate_players.py          只看要怎么搬（什么都不动）
    python -X utf8 tools/migrate_players.py --go     真搬

没有 data/selfplay/players.json 时，来玩的 agent 共用 data/selfplay/ 下的一套 inbox、outbox、events、印象簿、
scenes/、memory.json、feedback、cursor……；players.json 一出现，插件、聊天服务、hub、vp_player、vp_memory
就都按玩家分文件夹（见 tools/vp_players.py）。这个脚本建 players.json 和 active.json，把原有的那一套挪进玩家文件夹。

什么时候跑：游戏关着的时候。游戏开着，插件会一直往 data/selfplay/ 下写，挪到一半两边就对不上了；
所以 state.json 还在更新（游戏看着还开着）就不搬。搬完再开游戏：下一个来的（wait 或 hello）接手时，
聊天服务把它记进 players.json，写进 active.json。

  - data 文件夹是空的（新装的，还没玩过）：直接 --go。建好 players.json（还没有玩家）和 active.json（没人在玩）就行。
  - 没分文件夹玩过：data/selfplay/ 下除了共用的（state.json、README.txt、mailbox/、archive/、players/、players.json、
    active.json、session-names.json），都挪进 --current 的文件夹。--current 写那个玩家以后报到时用的名字：
    hello 名字 报的、hub 的 LUDUS_PLAYER，或者没报名字时的默认名（「agent（终端）」「agent（桌面 app）」
    「agent（claude.ai）」，前面是 LUDUS_AGENT_NAME，见 vp_players.identity）。不写 --current 就是 LUDUS_AGENT_NAME
    （没设是 agent）。名字对不上的话，它来了拿到的是一个新的空文件夹。
  - --archive 文件夹=名字：data/selfplay/archive/<文件夹>/ 里放着另一个玩家的一套，挪进那个名字的文件夹。可以写好几个。
  - --split-before 时间 --split-to 名字：data/selfplay/ 下这一套里混着另一个玩家说的话（同一本印象簿先后有两个玩家用过）时，
    按时间分开：印象簿里这个时间以前收的话挪进 --split-to 那本（条目照抄名字、介绍，不带图），feedback 同样按时间分，
    --current 的 memory.json 里带过的、这些旧话的回音也去掉。时间写本地时间 YYYY-MM-DDTHH:MM:SS。不写就不分。
  - 写 players.json，写 active.json（没人在玩：下一个来的接手时聊天服务会写上它）。
什么都不删：只挪、只分。搬过一次（players.json 已经在）就不再搬。
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import vp_players  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DATA = ROOT / "chat-service" / "data"
KEEP = {"state.json", "README.txt", "mailbox", "archive", "players", "players.json", "active.json", "session-names.json"}
STALE_SECONDS = 20  # 和 vp_player 一样：state.json 这么久没更新就当游戏没开


def now() -> str:
    return datetime.now().strftime("%Y-%m-%dT%H:%M:%S")


def norm_ts(ts) -> str:
    """本地时间 YYYY-MM-DDTHH:MM:SS；带 Z 的（UTC）换成本地"""
    s = str(ts or "")
    if s.endswith("Z"):
        try:
            return datetime.fromisoformat(s.replace("Z", "+00:00")).astimezone().strftime("%Y-%m-%dT%H:%M:%S")
        except ValueError:
            return s
    return s[:19]


def read_json(path: Path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return default


def write_json(path: Path, obj) -> None:
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(obj, ensure_ascii=False, indent=1), encoding="utf-8")
    os.replace(tmp, path)


def first_ts(folder: Path) -> str:
    """这个玩家第一次来的时间：它的 events.jsonl 第一行的 ts，没有就是现在"""
    try:
        with open(folder / "events.jsonl", encoding="utf-8") as f:
            for line in f:
                try:
                    ts = json.loads(line).get("ts")
                except ValueError:
                    continue
                if ts:
                    return norm_ts(ts)
    except OSError:
        pass
    return now()


def split_book(book: dict, before: str) -> tuple[dict, dict, set[str]]:
    """印象簿按话的时间分开：(留下的, 挪走的, 挪走的话)。挪走的条目照抄名字、介绍，不带图"""
    old_book: dict = {}
    moved: set[str] = set()
    for key, entry in book.items():
        if not isinstance(entry, dict):
            continue
        words = entry.get("words") or []
        old = [w for w in words if isinstance(w, dict) and norm_ts(w.get("ts")) < before]
        if not old:
            continue
        entry["words"] = [w for w in words if w not in old]
        copy = {k: v for k, v in entry.items() if k not in ("words", "image", "when")}
        copy["first"] = min(norm_ts(w.get("ts")) for w in old)
        copy["words"] = old
        old_book[key] = copy
        moved.update(str(w.get("text")) for w in old)
    return book, old_book, moved


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--data", type=Path, default=DEFAULT_DATA)
    p.add_argument("--current", default=vp_players.AGENT_NAME,
                   help=f"data/selfplay/ 下现在这一套是谁的：写它以后报到时用的名字（默认 {vp_players.AGENT_NAME}）")
    p.add_argument("--archive", action="append", default=None, metavar="文件夹=名字",
                   help="archive/ 下哪个文件夹是谁的，可以写好几个（默认没有）")
    p.add_argument("--split-before", default=None, metavar="时间",
                   help="现在这本印象簿里，这个时间以前说的话不是 --current 的，挪给 --split-to（默认不分）")
    p.add_argument("--split-to", default=None, metavar="名字")
    p.add_argument("--go", action="store_true", help="真搬（不写就只说要怎么搬）")
    p.add_argument("--force", action="store_true", help="游戏看着还开着也搬（不建议）")
    a = p.parse_args()
    if bool(a.split_before) != bool(a.split_to):
        p.error("--split-before 和 --split-to 要一起写")
    archives = a.archive or []

    sp = vp_players.root(a.data)
    if (sp / "players.json").exists():
        print(f"已经搬过了：{sp / 'players.json'} 在。不再搬。")
        return 1
    st = read_json(sp / "state.json", None)
    if st and not a.force:
        try:
            age = (datetime.now() - datetime.fromisoformat(st["ts"])).total_seconds()
        except (KeyError, ValueError):
            age = 1e9
        if age < STALE_SECONDS:
            print(f"游戏看着还开着（state.json {age:.0f} 秒前还在更新）。先关游戏再搬（或者 --force）。")
            return 1

    # 谁搬到哪：名字 → 文件夹（重名的加 -2）
    plan: dict[str, Path] = {}
    used: set[str] = set()

    def dir_for(name: str) -> Path:
        if name not in plan:
            base = f"players/{vp_players.safe_dir(name)}"
            rel, n = base, 2
            while rel in used:
                rel, n = f"{base}-{n}", n + 1
            used.add(rel)
            plan[name] = sp / rel
        return plan[name]

    moves: list[tuple[Path, Path]] = []
    own = [item for item in sorted(sp.iterdir()) if item.name not in KEEP] if sp.is_dir() else []
    cur_dir = dir_for(a.current) if own else None  # 没有要挪的就不先建这个玩家：它来了聊天服务会建
    for item in own:
        moves.append((item, cur_dir / item.name))
    archive_srcs = []
    for spec in archives:
        folder, _, name = spec.partition("=")
        src = sp / "archive" / folder
        if not name or not src.is_dir():
            print(f"跳过 archive {spec}：{'没写名字' if not name else '找不到 ' + str(src)}")
            continue
        dst = dir_for(name)
        archive_srcs.append(src)
        for item in sorted(src.iterdir()):
            moves.append((item, dst / item.name))
    split_dir = dir_for(a.split_to) if a.split_before and cur_dir is not None else None

    print("要这样搬：")
    if not plan:
        print("  data/selfplay/ 下没有要挪的东西")
    for name, d in plan.items():
        print(f"  {name} → {d.relative_to(sp)}")
    for src, dst in moves:
        print(f"    {src.relative_to(sp)}  →  {dst.relative_to(sp)}")
    if split_dir is not None:
        print(f"  印象簿、feedback：{a.current} 那里 {a.split_before} 以前的话挪给 {a.split_to}")
    elif a.split_before:
        print(f"  不分印象簿：data/selfplay/ 下没有 {a.current} 的东西")
    print(f"  写 players.json（{len(plan)} 个玩家）、active.json（没人在玩）")
    if not a.go:
        print("\n只是看看，什么都没动。真搬加 --go。")
        return 0

    sp.mkdir(parents=True, exist_ok=True)
    for d in plan.values():
        d.mkdir(parents=True, exist_ok=True)
    for src, dst in moves:
        if dst.exists():
            print(f"  {dst.relative_to(sp)} 已经有了，{src.relative_to(sp)} 不挪")
            continue
        shutil.move(str(src), str(dst))
    for src in archive_srcs:
        try:
            src.rmdir()  # 空了才删得掉
        except OSError:
            pass

    # 印象簿：旧话挪给 --split-to（不写 --split-before 就不分）
    moved_words: set[str] = set()
    if split_dir is not None:
        book_path = cur_dir / "impressions.json"
        book = read_json(book_path, None)
        if isinstance(book, dict):
            book, old_book, moved_words = split_book(book, a.split_before)
            if old_book:
                target = split_dir / "impressions.json"
                merged = read_json(target, {})
                if not isinstance(merged, dict):
                    merged = {}
                for key, entry in old_book.items():
                    if key in merged and isinstance(merged[key], dict):
                        merged[key].setdefault("words", []).extend(entry["words"])
                    else:
                        merged[key] = entry
                write_json(target, merged)
                write_json(book_path, book)
                print(f"  印象簿：{len(moved_words)} 句旧话挪进 {a.split_to} 那本（{len(old_book)} 个条目）")
        # feedback：同样按时间分
        fb = cur_dir / "feedback.jsonl"
        if fb.exists():
            keep, old = [], []
            for line in fb.read_text(encoding="utf-8").splitlines():
                try:
                    ts = norm_ts(json.loads(line).get("ts"))
                except ValueError:
                    ts = ""
                (old if ts and ts < a.split_before else keep).append(line)
            if old:
                with open(split_dir / "feedback.jsonl", "a", encoding="utf-8", newline="\n") as f:
                    f.write("\n".join(old) + "\n")
                fb.write_text("".join(l + "\n" for l in keep), encoding="utf-8")
                print(f"  feedback：{len(old)} 条挪给 {a.split_to}")
        # memory.json：带过的回音是旧话的去掉
        mem_path = cur_dir / "memory.json"
        mem = read_json(mem_path, None)
        if isinstance(mem, dict) and isinstance(mem.get("echoed"), dict) and moved_words:
            for name, items in list(mem["echoed"].items()):
                kept = [i for i in items if not (isinstance(i, dict) and i.get("text") in moved_words)]
                if kept:
                    mem["echoed"][name] = kept
                else:
                    del mem["echoed"][name]
            write_json(mem_path, mem)
    # 搬进来的玩家从 chat.jsonl 现在的末尾收话（之前的话没记是谁说的，不收）
    chat_size = (a.data / "chat.jsonl").stat().st_size if (a.data / "chat.jsonl").exists() else 0
    for name, d in plan.items():
        if not (d / "memory.json").exists():
            write_json(d / "memory.json", {"chat": chat_size, "echoed": {}})

    write_json(sp / "players.json", {"players": {name: {"dir": d.relative_to(sp).as_posix(), "first": first_ts(d)}
                                                 for name, d in plan.items()}})
    write_json(sp / "active.json", {"player": "", "dir": "", "since": now()})
    print("搬好了。再开游戏；下一个来的（wait 或 hello）接手时，聊天服务会把它记进 players.json、写进 active.json。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
