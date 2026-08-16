# -*- coding: utf-8 -*-
"""Analyze composite PNGs; find the true cand + whether the rendered body
samples only the lower (shield) half of the part cell."""
from PIL import Image
import os

TEX = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/tex"
part = Image.open(os.path.join(TEX, "PartTex_Median_BlurAlpha__512x256.png")).convert("RGBA")
fa = Image.open(os.path.join(TEX, "SpriteAtlasTexture-Sprites (Group 0)-2048x1024-fmt4__2048x1024.png")).convert("RGBA")
COMP = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/composites"

def px(im, x, y):
    w, h = im.size
    return im.getpixel((max(0, min(w-1, x)), max(0, min(h-1, y))))

fx0, fy0 = 601, 642
pcx, pcy, pcw, pch = 128, 0, 64, 126

def part_uv(frame_rgba, cand):
    r, g, b, a = frame_rgba
    ur, ug = r / 255.0, g / 255.0
    if cand == 0:   u, v = ur, ug
    elif cand == 1: u, v = ug, ur
    elif cand == 2: u, v = ur, 1 - ug
    else:           u, v = 1 - ur, 1 - ug
    return (int(pcx + u * pcw), int(pcy + v * pch))

# Which cell rows do RENDERED (alpha>8) frame pixels sample, per cand?
print("=== rendered-frame pixels -> part cell row histogram ===")
for cand in range(4):
    hist = {}
    for yy in range(70):
        for xx in range(43):
            fr = px(fa, fx0 + xx, fy0 + yy)
            if fr[3] <= 8:
                continue
            p = part_uv(fr, cand)
            row = p[1] - pcy
            hist[row] = hist.get(row, 0) + 1
    rows_used = sorted(hist.keys())
    top = [r for r in rows_used if r < 52]
    bot = [r for r in rows_used if r >= 52]
    print(f"cand{cand}: rows used {rows_used[:5]}...{rows_used[-5:]}  | rows<52(top/sword): {len(top)}  rows>=52(shield): {len(bot)}")
    print(f"   count above(sword region)={sum(hist.get(r,0) for r in top)}  below(shield)={sum(hist.get(r,0) for r in bot)}")

# For cand2 (likely candidate), sample the composite appearance
print("\n=== composite cand2 appearance (ASCII by color) ===")
img = Image.open(os.path.join(COMP, "composite_cand2.png")).convert("RGBA")
for yy in range(69, -1, -2):
    row = []
    for xx in range(43):
        r, g, b, a = img.getpixel((xx, yy))
        if a <= 8: row.append(' ')
        elif r > 150 and g > 150 and b > 150: row.append('#')
        elif r > 110 and g > 100 and b > 90: row.append('~')
        elif r > 90: row.append('T')
        else: row.append('d')
    print(f"y{yy:02d}: " + "".join(row))

print("\n=== composite cand0 appearance ===")
img = Image.open(os.path.join(COMP, "composite_cand0.png")).convert("RGBA")
for yy in range(69, -1, -2):
    row = []
    for xx in range(43):
        r, g, b, a = img.getpixel((xx, yy))
        if a <= 8: row.append(' ')
        elif r > 150 and g > 150 and b > 150: row.append('#')
        elif r > 110 and g > 100 and b > 90: row.append('~')
        elif r > 90: row.append('T')
        else: row.append('d')
    print(f"y{yy:02d}: " + "".join(row))
