# /// script
# requires-python = ">=3.10"
# dependencies = ["pillow>=10.1"]
# ///
"""给自己玩的截图画上按钮编号。

用法：uv run tools/mark_shot.py <NNNN.png> [...]
每张图旁边要有插件写的 NNNN.json：截图那一刻的清单（w、h 是游戏画面大小，snap 是清单版本，
items 里每个按钮的 n、x、y、off；x、y 以左上角为原点）。画好存成 NNNN-marked.png，原图不动。
vp_player 换场景和 shot 时会自己调。要 Pillow：脚本头写着依赖，vp_player 借 uv run 跑它，系统的 python 不用装
（第一次跑时 uv 下载 Pillow，之后用缓存）。

编号贴在按钮上点下去的那个位置：黄底是能点的，灰底是灰按钮。几个编号挤在一起时往旁边挪，拉一条线指回去。
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

FONTS = ["arialbd.ttf", "segoeuib.ttf", "DejaVuSans-Bold.ttf"]
FILL = (255, 214, 0, 235)
FILL_OFF = (165, 165, 165, 235)
INK = (0, 0, 0, 255)


def marked_path(png: Path) -> Path:
    return png.with_name(png.stem + "-marked.png")


def load_font(size: int):
    for name in FONTS:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default(size)


def overlaps(a, b) -> bool:
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]


def place(px: float, py: float, w: int, h: int, taken: list, W: int, H: int) -> tuple:
    """编号框放哪：先放在点上，挤了就按上、下、右、左、四角，一圈比一圈远地找空地。都没有就叠着放。"""
    def box(cx, cy):
        x0 = min(max(cx - w / 2, 0), W - w)
        y0 = min(max(cy - h / 2, 0), H - h)
        return (x0, y0, x0 + w, y0 + h)

    dirs = [(0, 0), (0, -1), (0, 1), (1, 0), (-1, 0), (1, -1), (-1, -1), (1, 1), (-1, 1)]
    for ring in (1, 2, 3):
        for dx, dy in dirs if ring == 1 else dirs[1:]:
            b = box(px + dx * (w + 2) * ring, py + dy * (h + 2) * ring)
            if not any(overlaps(b, t) for t in taken):
                return b
    return box(px, py)


def mark(png: Path) -> Path:
    meta = json.loads(png.with_suffix(".json").read_text(encoding="utf-8"))
    img = Image.open(png).convert("RGBA")
    W, H = img.size
    sx, sy = W / (meta.get("w") or W), H / (meta.get("h") or H)  # 截图和游戏画面大小不一样时（超采样之类）按比例换算
    font = load_font(max(14, round(24 * W / 1920)))
    pad = max(3, round(5 * W / 1920))
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    taken = []
    for it in meta.get("items") or []:
        if "x" not in it or "y" not in it:
            continue  # 滚动列表里滚出可视区的：画面上看不到
        px, py = it["x"] * sx, it["y"] * sy
        if not (0 <= px < W and 0 <= py < H):
            continue
        text = str(it["n"])
        l, t, r, b = d.textbbox((0, 0), text, font=font)
        w, h = r - l + 2 * pad, b - t + 2 * pad
        x0, y0, x1, y1 = place(px, py, w, h, taken, W, H)
        taken.append((x0, y0, x1, y1))
        if not (x0 <= px <= x1 and y0 <= py <= y1):  # 挪开了：画一条线、一个点指回按钮
            cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
            d.line([(cx, cy), (px, py)], fill=INK, width=3)
            d.line([(cx, cy), (px, py)], fill=FILL[:3] + (255,), width=1)
            d.ellipse([px - 4, py - 4, px + 4, py + 4], fill=FILL[:3] + (255,), outline=INK, width=2)
        d.rounded_rectangle([x0, y0, x1, y1], radius=pad, fill=FILL_OFF if it.get("off") else FILL, outline=INK, width=2)
        d.text((x0 + pad - l, y0 + pad - t), text, font=font, fill=INK)
    out = marked_path(png)
    Image.alpha_composite(img, layer).convert("RGB").save(out)
    return out


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__, file=sys.stderr)
        return 2
    failed = 0
    for arg in argv:
        png = Path(arg)
        try:
            print(mark(png))
        except Exception as e:  # 一张画不了不影响别的
            print(f"画不了 {png.name}：{type(e).__name__}：{e}", file=sys.stderr)
            failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
