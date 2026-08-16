# -*- coding: utf-8 -*-
"""Render synthetic composites under each cand formula, save PNG + analyze."""
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

def part_uv(frame_rgba, cand):
    r, g, b, a = frame_rgba
    ur, ug = r / 255.0, g / 255.0
    if cand == 0:   u, v = ur, ug
    elif cand == 1: u, v = ug, ur
    elif cand == 2: u, v = ur, 1 - ug
    else:           u, v = 1 - ur, 1 - ug
    return (int(pcx + u * pcw), int(pcy + v * pch))

OUT = os.path.join(TEX, "..", "composites")
os.makedirs(OUT, exist_ok=True)

for cand in range(4):
    img = Image.new("RGBA", (43, 70), (0, 0, 0, 0))
    usage = [[0]*64 for _ in range(126)]
    for yy in range(70):
        for xx in range(43):
            fr = px(fa, fx0 + xx, fy0 + yy)
            if fr[3] <= 8:
                continue
            p = part_uv(fr, cand)
            pr = px(part, p[0], p[1])
            img.putpixel((xx, 69 - yy), pr)
            if pr[3] > 8:
                usage[p[1]][p[0] - pcx] += 1
    img.save(os.path.join(OUT, f"composite_cand{cand}.png"))
    # usage map of the cell
    um = "\n".join("".join("X" if usage[y][x] > 0 else "." for x in range(64)) for y in range(125, -1, -2))
    open(os.path.join(OUT, f"usage_cand{cand}.txt"), "w").write(um)
    print(f"cand{cand} saved; used cell rows:")

print("done")
