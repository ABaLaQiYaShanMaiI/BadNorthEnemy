# -*- coding: utf-8 -*-
"""Map the frame's R/G UV codes to part pixels and visualize."""
from PIL import Image
import os

TEX = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/tex"
part = Image.open(os.path.join(TEX, "PartTex_Median_BlurAlpha__512x256.png")).convert("RGBA")
fa = Image.open(os.path.join(TEX, "SpriteAtlasTexture-Sprites (Group 0)-2048x1024-fmt4__2048x1024.png")).convert("RGBA")

def px(im, x, y):
    w, h = im.size
    return im.getpixel((max(0, min(w-1, x)), max(0, min(h-1, y))))

fx0, fy0 = 601, 642
pcx, pcy, pcw, pch = 128, 0, 64, 126

# Try BOTH the standalone Swordsman0001 sprite and the atlas rect.
# First: which rect actually contains the viking?  Check frame R/G grids.
print("=== frame R map (atlas 601,642) rows sampled, step=6 ===")
for yy in range(0, 60, 6):
    row = "".join(f"{px(fa, fx0+xx, fy0+yy)[0]:3d}" for xx in range(0, 43, 6))
    print(f"y{yy:02d}: {row}")
print("\n=== frame G map ===")
for yy in range(0, 60, 6):
    row = "".join(f"{px(fa, fx0+xx, fy0+yy)[1]:3d}" for xx in range(0, 43, 6))
    print(f"y{yy:02d}: {row}")

print("\n=== frame A map (alpha) ===")
for yy in range(0, 60, 6):
    row = "".join(f"{px(fa, fx0+xx, fy0+yy)[3]:3d}" for xx in range(0, 43, 6))
    print(f"y{yy:02d}: {row}")

# Now try the standalone Swordsman0001 sprite
print("\n=== standalone Swordsman0001 (128x128) R map ===")
sf = Image.open(os.path.join(TEX, "Swordsman0001__128x128.png")).convert("RGBA")
for yy in range(0, 128, 8):
    row = "".join(f"{px(sf, xx, yy)[0]:3d}" for xx in range(0, 128, 8))
    print(f"y{yy:02d}: {row}")

# Render a synthetic image: for each frame pixel, sample the part under cand0 formula.
print("\n=== synthetic composite under cand0: frame -> part sample (ASCII) ===")
out_rows = []
for yy in range(0, 60, 2):
    row_chars = []
    for xx in range(0, 43, 1):
        r, g, b, a = px(fa, fx0+xx, fy0+yy)
        if a <= 8:
            row_chars.append(' ')
            continue
        u = r/255.0; v = g/255.0
        pxx = int(pcx + u * pcw); pyy = int(pcy + v * pch)
        pr, pg, pb, pa = px(part, pxx, pyy)
        if pa <= 8:
            row_chars.append('~')  # transparent
        elif pr > 150 and pg > 150 and pb > 150:
            row_chars.append('#')
        elif pr > 90 and pg < 40 and pb < 30:
            row_chars.append('r')
        else:
            row_chars.append('.')
    out_rows.append(''.join(row_chars))
print('\n'.join(out_rows))
