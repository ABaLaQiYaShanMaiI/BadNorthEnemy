# -*- coding: utf-8 -*-
"""Simulate the actual game render for Swordsman frames:
   visible color = sample PartTex at cand0 (uv=(R/255,G/255) within cell) 
   then multiply by black tint (0, 0.25, 0.01) — mirroring the game's vertex color.
   Prints an ASCII map: '.'=dark body, 'B'=bright(unmultiplied) px, 'W'=near-white after tint,
   'S'=red-dark sword px, ' '=transparent.
"""
from PIL import Image
import os

TEX = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/tex"
part = Image.open(os.path.join(TEX, "PartTex_Median_BlurAlpha__512x256.png")).convert("RGBA")
fa = Image.open(os.path.join(TEX, "SpriteAtlasTexture-Sprites (Group 0)-2048x1024-fmt4__2048x1024.png")).convert("RGBA")

def px(im, x, y):
    w, h = im.size
    return im.getpixel((max(0, min(w-1, x)), max(0, min(h-1, y))))

pcx, pcy, pcw, pch = 128, 0, 64, 126
TINT = (0, 0.25, 0.01)   # black tint (r,g,b) from game

def part_uv(r, g):
    return (int(pcx + (r/255.0)*pcw), int(pcy + (g/255.0)*pch))

def render_frame(fname, fx0, fy0, fw, fh, label):
    print("="*70)
    print(f"frame {label} at ({fx0},{fy0}) {fw}x{fh}")
    bright_count = 0
    nearwhite = 0
    reddark = 0
    for yy in range(fh-1, -1, -2):
        row = []
        for xx in range(fw):
            r, g, b, a = px(fa, fx0+xx, fy0+yy)
            if a <= 8:
                row.append(' ')
                continue
            p = part_uv(r, g)
            pr, pg, pb, pa = px(part, p[0], p[1])
            if pa <= 8:
                row.append('-')   # part transparent
                continue
            # game tint
            tr = pr/255.0 * TINT[0]
            tg = pg/255.0 * TINT[1]
            tb = pb/255.0 * TINT[2]
            lum = tr + tg + tb
            if lum > 0.45:
                nearwhite += 1
                row.append('W')
            elif pr > 150 and pg > 150 and pb > 150:
                bright_count += 1
                row.append('B')
            elif r > 70 and g < 40 and b < 20:
                reddark += 1
                row.append('S')
            else:
                row.append('.')
        print("   " + "".join(row))
    print(f"   bright(untinted)={bright_count} nearwhite_after_tint={nearwhite} reddark={reddark}")

# Swordsman0002 rect from the game log: (x:601.01, y:642.05, width:42.96, height:69.88)
render_frame("Swordsman0002", 601, 642, 43, 70, "Swordsman0002")
# Swordsman0001 rect from log: (x:311.01, y:393.05, width:53.96, height:69.88)
render_frame("Swordsman0001", 311, 393, 54, 70, "Swordsman0001")

# ===== simulate MODE 2: erase bright part pixels, re-render =====
print("="*70)
print("MODE 2 SIMULATION (bright part px erased from a clone)")
part2 = part.copy()
for yy in range(pcy, pcy+pch):
    for xx in range(pcx, pcx+pcw):
        pr, pg, pb, pa = part2.getpixel((xx, yy))
        if pa > 128 and pr > 150 and pg > 150 and pb > 150:
            part2.putpixel((xx, yy), (0, 0, 0, 0))
for (fname, fx0, fy0, fw, fh, label) in [
        ("Swordsman0002", 601, 642, 43, 70, "Swordsman0002 MODE2"),
        ("Swordsman0001", 311, 393, 54, 70, "Swordsman0001 MODE2")]:
    print("-"*70)
    print(f"frame {label}")
    holes = 0          # frame opaque px that become transparent (part erased)
    body_dark = 0      # frame px rendering dark body
    for yy in range(fh-1, -1, -2):
        row = []
        for xx in range(fw):
            r, g, b, a = px(fa, fx0+xx, fy0+yy)
            if a <= 8:
                row.append(' ')
                continue
            p = part_uv(r, g)
            pr, pg, pb, pa = px(part2, p[0], p[1])
            if pa <= 8:
                holes += 1
                row.append('H')   # hole (was bright art, now erased)
                continue
            if r > 70 and g < 40 and b < 20:
                row.append('S')
                continue
            body_dark += 1
            row.append('.')
        print("   " + "".join(row))
    print(f"   holes={holes} (frame px now transparent)  body_dark={body_dark}")
