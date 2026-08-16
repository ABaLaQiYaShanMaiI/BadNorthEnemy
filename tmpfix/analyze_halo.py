# -*- coding: utf-8 -*-
"""Simulate the new UV erase + halo on the offline Swordsman0002 frame.
Answer: with UVHalo=1, how many extra frame pixels get erased, and are any of
them BODY pixels (dark/tan part sample, i.e. NOT bright)?"""
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
    return (int(pcx + (r/255.0)*pcw), int(pcy + (g/255.0)*pch))

def bright(r, g, b, a):
    return a > 128 and r > 150 and g > 150 and b > 150

# build part-cell bright mask + halo mask
cw, ch = 64, 126
bm = [[False]*cw for _ in range(ch)]
for y in range(ch):
    for x in range(cw):
        pr, pg, pb, pa = px(part, pcx+x, pcy+y)
        if bright(pr, pg, pb, pa):
            bm[y][x] = True
halo = 1
hm = [[bm[y][x] for x in range(cw)] for y in range(ch)]
for y in range(ch):
    for x in range(cw):
        if not bm[y][x]:
            continue
        for dy in range(-halo, halo+1):
            for dx in range(-halo, halo+1):
                ny, nx = y+dy, x+dx
                if 0 <= ny < ch and 0 <= nx < cw:
                    hm[ny][nx] = True

fx0, fy0, fw, fh = 601, 642, 43, 70
reddark = 0; bright_exact = 0; halo_only = 0; body_dark = 0
body_halo_hits = []
for yy in range(fh):
    for xx in range(fw):
        r, g, b, a = px(fa, fx0+xx, fy0+yy)
        if a <= 8:
            continue
        if r > 70 and g < 40 and b < 20:
            reddark += 1
            continue
        p = part_uv(r, g)
        cx, cy = p[0]-pcx, p[1]-pcy
        if not (0 <= cx < cw and 0 <= cy < ch):
            continue
        pr, pg, pb, pa = px(part, p[0], p[1])
        if bright(pr, pg, pb, pa):
            bright_exact += 1
        elif hm[cy][cx]:
            halo_only += 1
            if not bright(pr, pg, pb, pa):
                body_dark += 1
                body_halo_hits.append((xx, yy, (r, g, b, a), p, (pr, pg, pb, pa)))

print(f"offline Swordsman0002: reddark={reddark} bright_exact={bright_exact} halo_only={halo_only}")
print(f"halo-only pixels that sample DARK/tan part (would erase body pixels): {body_dark}")
for h in body_halo_hits[:15]:
    print("   ", h)
