# -*- coding: utf-8 -*-
"""Figure out frame->part UV mapping and locate the white box source."""
from PIL import Image
import os

TEX = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/tex"
part = Image.open(os.path.join(TEX, "PartTex_Median_BlurAlpha__512x256.png")).convert("RGBA")
fa = Image.open(os.path.join(TEX, "SpriteAtlasTexture-Sprites (Group 0)-2048x1024-fmt4__2048x1024.png")).convert("RGBA")

def px(im, x, y):
    w, h = im.size
    x = max(0, min(w - 1, x)); y = max(0, min(h - 1, y))
    return im.getpixel((x, y))

# frame rect in atlas
fx0, fy0, fw, fh = 601, 642, 43, 70
# part cell rect
pcx, pcy, pcw, pch = 128, 0, 64, 126

def sample_part(frame_rgba, cand):
    """map frame pixel -> part atlas pixel under 4 cands"""
    r, g, b, a = frame_rgba
    ur = r / 255.0
    ug = g / 255.0
    if cand == 0:   u, v = ur, ug
    elif cand == 1: u, v = ug, ur
    elif cand == 2: u, v = ur, 1 - ug
    else:           u, v = 1 - ur, 1 - ug
    # assume frame uv (0..1) maps across the whole part atlas, then rect origin shifts
    return (int(pcx + u * pcw), int(pcy + v * pch))

# The frame pixel -> part: two models:
#  Model A: frame.rg is a full-atlas-normalized UV (u=r/255, v=g/255 across 512x256 atlas) + rectOrigin offset
#  Model B: frame.rg maps across the 64x126 cell only
def part_uv_model(frame_rgba, cand, model):
    r, g, b, a = frame_rgba
    ur = r / 255.0
    ug = g / 255.0
    if cand == 0:   u, v = ur, ug
    elif cand == 1: u, v = ug, ur
    elif cand == 2: u, v = ur, 1 - ug
    else:           u, v = 1 - ur, 1 - ug
    if model == 'cell':
        return (int(pcx + u * pcw), int(pcy + v * pch))
    else:  # atlas + origin (rectOrigin in pixels / atlas size)
        return (int(128 + u * 512), int(0 + v * 256))

print("=== frame sample points ===")
# sword blade center (from ascii: S region cols ~12-16, rows ~8-13 within frame rect)
samples = {
    "sword": (601 + 15, 642 + 10),
    "sword2": (601 + 12, 642 + 13),
    "body_torso": (601 + 22, 642 + 22),
    "body_head": (601 + 20, 642 + 4),
    "body_leg": (601 + 20, 642 + 30),
    "body_left": (601 + 5, 642 + 22),
}
for name, (x, y) in samples.items():
    fr = px(fa, x, y)
    line = f"{name} frame_px=({x},{y}) rgba={fr}"
    for m in ('cell', 'atlas'):
        pts = []
        for cand in range(4):
            p = part_uv_model(fr, cand, m)
            pts.append(f"c{cand}={px(part, p[0], p[1])}@{p}")
        line += f"  {m}: " + " ".join(pts)
    print(line)

print("\n=== frame full-RGB check: is the frame a UV map or a real picture? ===")
# Check a horizontal strip through the torso: are colors flat/UV-like or detailed?
y = 642 + 20
row = []
for x in range(601, 644):
    r, g, b, a = px(fa, x, y)
    row.append(f"({r},{g},{b})")
print("row@torso:", " ".join(row))

print("\n=== frame rect opacity mask (rows 0-34, cols 0-42, step=2) ===")
for yy in range(34, -1, -2):
    s = ""
    for xx in range(0, 43, 2):
        r, g, b, a = px(fa, fx0 + xx, fy0 + yy)
        s += '#' if a > 8 else '.'
    print(f"y{yy:02d}: {s}")

