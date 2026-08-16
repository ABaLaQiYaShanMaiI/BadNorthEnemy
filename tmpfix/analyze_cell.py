# -*- coding: utf-8 -*-
"""Visualize the PartTex_SwordShield cell and the frame's part samples."""
from PIL import Image
import os

TEX = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/tex"
part = Image.open(os.path.join(TEX, "PartTex_Median_BlurAlpha__512x256.png")).convert("RGBA")

def px(im, x, y):
    w, h = im.size
    return im.getpixel((max(0, min(w-1, x)), max(0, min(h-1, y))))

pcx, pcy, pcw, pch = 128, 0, 64, 126

def classify(r, g, b, a):
    if a <= 8:
        return ' '          # transparent
    if r > 150 and g > 150 and b > 150:
        return '#'          # bright silver (sword)
    if r > 110 and g > 100 and b > 90 and abs(r-b) < 40 and abs(g-b) < 40:
        return '~'          # mid silver
    if r > 100 and g > 90 and b > 80:
        return 'T'          # tan/skin
    if r > 50 and g > 45 and b > 40:
        return 't'          # darker tan
    return 'd'              # dark

print("=== PartTex_SwordShield cell (128,0,64,126) colored ASCII, full res ===")
for y in range(125, -1, -2):
    row = "".join(classify(*px(part, pcx + x, pcy + y)) for x in range(64))
    print(f"{y:3d}: {row}")

# Also print the whole atlas cell map (which 64x64 cells are non-empty / white)
print("\n=== whole PartTex atlas 64px-cell occupancy (O=opaque W=white-ish .=trans) ===")
for cy in range(0, 256, 64):
    row = []
    for cx in range(0, 512, 64):
        op = wh = tr = 0
        for y in range(cy, cy + 64, 4):
            for x in range(cx, cx + 64, 4):
                r, g, b, a = px(part, x, y)
                if a > 8:
                    op += 1
                    if r > 200 and g > 200 and b > 200:
                        wh += 1
                else:
                    tr += 1
        tag = "O" if op > tr else ("." if tr > 0 else "?")
        if wh > op * 0.5:
            tag = "W"
        row.append(f"[{cx},{cy}]{tag}{op}")
    print("  ".join(row))
