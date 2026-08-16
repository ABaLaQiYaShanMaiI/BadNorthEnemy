# -*- coding: utf-8 -*-
"""Deep-analyze the PartTex + frame atlas to find the white-box source."""
from PIL import Image
import collections, os

TEX = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/tex"

part = Image.open(os.path.join(TEX, "PartTex_Median_BlurAlpha__512x256.png")).convert("RGBA")
frame_atlas = Image.open(os.path.join(TEX, "SpriteAtlasTexture-Sprites (Group 0)-2048x1024-fmt4__2048x1024.png")).convert("RGBA")

def px(im, x, y):
    return im.getpixel((x, y))

def rect_hist(im, x0, y0, x1, y1, step=1):
    c = collections.Counter()
    for y in range(y0, y1, step):
        for x in range(x0, x1, step):
            c[px(im, x, y)] += 1
    return c

def ascii_map(im, x0, y0, x1, y1, step=2, thresh_bright=150):
    """ASCII map: . opaque dark, # bright, S red-dark, space transparent."""
    out = []
    for y in range(y1 - 1, y0 - 1, -step):
        row = []
        for x in range(x0, x1, step):
            r, g, b, a = px(im, x, y)
            ch = ' '
            if a > 8:
                if r > 90 and g < 25 and b < 10: ch = 'S'
                elif r > 70 and g < 40 and b < 20: ch = 's'
                elif r > thresh_bright and g > thresh_bright and b > thresh_bright: ch = '#'
                else: ch = '.'
            row.append(ch)
        out.append(''.join(row))
    return '\n'.join(out)

print("==== PartTex atlas 512x256 — cell structure ====")
# sample a grid of 64x64 blocks to see which are non-empty
for by in range(0, 256, 64):
    row = []
    for bx in range(0, 512, 64):
        hist = rect_hist(part, bx, by, bx+64, by+64, step=4)
        opaque = sum(n for (col, n) in hist.items() if col[3] > 8)
        row.append(f"[{bx},{by}: {opaque}px]")
    print("  ".join(row))

print("\n==== PartTex_SwordShield cell rect=(128,0,64,126) ASCII (step=2) ====")
print(ascii_map(part, 128, 0, 192, 126))

print("\n==== PartTex_SwordShield cell color histogram (step=2, top 15) ====")
h = rect_hist(part, 128, 0, 192, 126, step=1)
for col, n in h.most_common(15):
    print("  ", col, n)

print("\n==== PartTex_SwordShield: bright(>150,>150,>150) bbox ====")
bx0, by0, bx1, by1 = 999, 999, -1, -1
cnt = 0
for y in range(0, 126):
    for x in range(128, 192):
        r, g, b, a = px(part, x, y)
        if a > 8 and r > 150 and g > 150 and b > 150:
            cnt += 1
            bx0 = min(bx0, x); bx1 = max(bx1, x); by0 = min(by0, y); by1 = max(by1, y)
print(f"  bright count={cnt} bbox=({bx0},{by0})-({bx1},{by1})")
# bright column profile per row
rows = collections.Counter()
for y in range(0, 126):
    for x in range(128, 192):
        r, g, b, a = px(part, x, y)
        if a > 8 and r > 150 and g > 150 and b > 150:
            rows[y] += 1
print("  bright rows (y:count):", sorted(rows.items())[:10], "...", sorted(rows.items())[-10:])

print("\n==== Swordsman0002 frame rect=(601,642,43,70) ASCII ====")
print(ascii_map(frame_atlas, 601, 642, 644, 712))
