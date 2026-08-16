# -*- coding: utf-8 -*-
"""Verify white-box source + feasibility of 'delete the hand' (y-cut) for Swordsman frames.
Uses cand0 formula (uv=(R/255, G/255) within part cell) — the in-game decode.
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

def part_uv(r, g):
    u = r / 255.0
    v = g / 255.0
    return (int(pcx + u * pcw), int(pcy + v * pch))

def is_bright(r, g, b, a):
    return a > 128 and r > 150 and g > 150 and b > 150

FRAMES = [(601, 642, 43, 70)]  # Swordsman0002; extend with more if needed

for (fx0, fy0, fw, fh) in FRAMES:
    total_opaque = 0
    bright_render = 0          # opaque frame px whose part sample is bright
    reddark = 0                # frame px matching current red-dark erase
    bright_not_reddark = 0     # white-box px NOT caught by current erase
    reddark_not_bright = 0     # current-erase px that are NOT bright (would be fine)
    cell_y_hist = {}           # decoded cell y histogram of ALL opaque frame px
    body_cell_y_below52 = []   # frame px (that sample dark/tan part) with cell y<52

    for yy in range(fh):
        for xx in range(fw):
            r, g, b, a = px(fa, fx0 + xx, fy0 + yy)
            if a <= 8:
                continue
            total_opaque += 1
            p = part_uv(r, g)
            pr, pg, pb, pa = px(part, p[0], p[1])
            cy = p[1] - pcy
            cell_y_hist[cy] = cell_y_hist.get(cy, 0) + 1
            isRed = (r > 70 and g < 40 and b < 20)
            isBri = is_bright(pr, pg, pb, pa)
            if isRed:
                reddark += 1
                if not isBri:
                    reddark_not_bright += 1
            if isBri:
                bright_render += 1
                if not isRed:
                    bright_not_reddark += 1
            # body px (dark/tan part sample) that decode to cell y<52 -> would be cut by y-cut
            if not isBri and cy < 52:
                body_cell_y_below52.append((xx, yy, (r, g, b, a), p, (pr, pg, pb, pa)))

    print(f"=== frame at ({fx0},{fy0}) {fw}x{fh} ===")
    print(f"  opaque={total_opaque}  red-dark(frame)={reddark}  bright-rendered={bright_render}")
    print(f"  WHITE-BOX px (bright but NOT red-dark)={bright_not_reddark}")
    print(f"  red-dark but not bright (harmless over-erase)={reddark_not_bright}")
    print(f"  cell-y range of opaque frame px: {min(cell_y_hist)}..{max(cell_y_hist)}")
    low = sorted(cell_y_hist.keys())
    print(f"  cell-y<52 count (would be cut by y-cut): {sum(cell_y_hist.get(y,0) for y in range(52))}")
    if body_cell_y_below52:
        print(f"  !! dark/tan-sampled frame px with cell y<52: {len(body_cell_y_below52)}")
        print("     first 10:", body_cell_y_below52[:10])
    else:
        print("  OK: no body(dark/tan)-sampling frame px decode to cell y<52 → y-cut would only cut sword/hand/shield")

    # ASCII map of where bright-rendered px are (B) vs red-dark sword (S) vs body (.)
    print("  bright-map (B=bright-rendered, S=reddark, .=opaque body):")
    for yy in range(fh - 1, -1, -2):
        row = []
        for xx in range(fw):
            r, g, b, a = px(fa, fx0 + xx, fy0 + yy)
            if a <= 8:
                row.append(' ')
                continue
            p = part_uv(r, g)
            pr, pg, pb, pa = px(part, p[0], p[1])
            if is_bright(pr, pg, pb, pa):
                row.append('B')
            elif r > 70 and g < 40 and b < 20:
                row.append('S')
            else:
                row.append('.')
        print("   " + "".join(row))
