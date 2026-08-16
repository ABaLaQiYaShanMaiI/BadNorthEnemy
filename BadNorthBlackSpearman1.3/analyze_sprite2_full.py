# -*- coding: utf-8 -*-
"""打印 PartTex_SwordShield 与 Swordsman0002 的完整 ASCII 到文本文件（避免终端截断），
并模拟"亮银擦除 + flood"在离线 PNG 上的效果，与运行时日志对照。"""
import os
from PIL import Image

SPR = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sprite2_full_map.txt")

def classify(r, g, b, a):
    if a <= 8: return ' '
    if r > 150 and g > 150 and b > 150 and a > 128: return '#'
    if 40 <= r <= 100 and abs(r - b) <= 25: return 'g'
    if 100 < r < 150: return 'G'
    if r > 100 and g > 60 and b > 40 and r - b > 30: return 's'
    if r < 45 and g < 38 and b < 33: return 'b'
    return '.'

def dump(path, title, out):
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    px = im.load()
    out.write(f"== {title} {os.path.basename(path)} {w}x{h} ==\n")
    for y in range(h):
        row = []
        for x in range(w):
            r, g, b, a = px[x, y]
            row.append(classify(r, g, b, a))
        out.write(f"{y:3d}|{''.join(row)}\n")
    out.write("\n")

def raw_colors(path, title, out, y0, y1, x0, x1):
    im = Image.open(path).convert("RGBA")
    px = im.load()
    out.write(f"== {title} 原始RGBA x[{x0},{x1}) y[{y0},{y1}) ==\n")
    for y in range(y0, y1):
        out.write(f"{y:3d}|")
        for x in range(x0, x1):
            r, g, b, a = px[x, y]
            out.write(f"({r:3d},{g:3d},{b:3d},{a:3d}) ")
        out.write("\n")
    out.write("\n")

with open(OUT, "w", encoding="utf-8") as f:
    dump(os.path.join(SPR, "PartTex_SwordShield.png"), "PartTex_SwordShield", f)
    dump(os.path.join(SPR, "Swordsman0002.png"), "Swordsman0002", f)
    # 原始RGBA：剑柄/护手区（暗灰带 47-90）与盾牌区（111-125）
    raw_colors(os.path.join(SPR, "PartTex_SwordShield.png"), "PartTex 暗灰带 44-92", f, 44, 92, 0, 64)
    raw_colors(os.path.join(SPR, "PartTex_SwordShield.png"), "PartTex 盾牌区 108-126", f, 108, 126, 0, 64)

print("written:", OUT)
