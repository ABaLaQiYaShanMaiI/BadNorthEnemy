# -*- coding: utf-8 -*-
"""全 Swordsman 帧分析：哪些帧像素会采样到部件贴图"暗灰剑柄"区。
回答：把部件暗灰区加入 UV 擦除掩码后，是否会误擦身体像素？"""
import os, glob
from collections import Counter
from PIL import Image

SPR = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
part_path = os.path.join(SPR, "PartTex_SwordShield.png")
im_p = Image.open(part_path).convert("RGBA")
pw, ph = im_p.size
pp = im_p.load()

# 部件暗灰剑柄区掩码（离线 64x126）
grip_mask = [[False]*pw for _ in range(ph)]
bright_mask = [[False]*pw for _ in range(ph)]
for y in range(ph):
    for x in range(pw):
        r, g, b, a = pp[x, y]
        if a > 128 and 40 <= r <= 100 and abs(r - b) <= 25:
            grip_mask[y][x] = True
        if a > 128 and r > 150 and g > 150 and b > 150:
            bright_mask[y][x] = True

def classify(r, g, b, a):
    if a <= 8: return ' '
    if r > 150 and g > 150 and b > 150 and a > 128: return '#'
    if 40 <= r <= 100 and abs(r - b) <= 25: return 'g'
    if 100 < r < 150: return 'G'
    if r > 100 and g > 60 and b > 40 and r - b > 30: return 's'
    if r < 45 and g < 38 and b < 33: return 'b'
    return '.'

print(f"{'帧':<18}{'不透明':>5}{'采样亮(#)':>9}{'采样暗灰(g)':>12}{'采样皮肤(s)':>12}{'g帧bbox':>20}{'g部件bbox':>22}")

total_g = 0
body_hit = 0
for path in sorted(glob.glob(os.path.join(SPR, "Swordsman*.png"))):
    name = os.path.basename(path)
    im = Image.open(path).convert("RGBA")
    fw, fh = im.size
    px = im.load()
    opaque = 0
    g_frame = []
    b_frame = []
    s_frame = []
    for fy in range(fh):
        for fx in range(fw):
            r, g, b, a = px[fx, fy]
            if a <= 8: continue
            opaque += 1
            cx = int((r / 255.0) * pw); cy = int((g / 255.0) * ph)
            if cx < 0 or cy < 0 or cx >= pw or cy >= ph: continue
            if bright_mask[cy][cx]: b_frame.append((fx, fy))
            if grip_mask[cy][cx]: g_frame.append((fx, fy))
            if classify(pp[cx, cy][0], pp[cx, cy][1], pp[cx, cy][2], pp[cx, cy][3]) == 's':
                s_frame.append((fx, fy))
    gbbox = ""
    pbbox = ""
    if g_frame:
        fxs = [p[0] for p in g_frame]; fys = [p[1] for p in g_frame]
        gbbox = f"({min(fxs)},{min(fys)})-({max(fxs)},{max(fys)})"
        cxs = [int((px[x, y][0]/255.0)*pw) for (x, y) in g_frame]
        cys = [int((px[x, y][1]/255.0)*ph) for (x, y) in g_frame]
        pbbox = f"({min(cxs)},{min(cys)})-({max(cxs)},{max(cys)})"
        total_g += len(g_frame)
    print(f"{name:<18}{opaque:>5}{len(b_frame):>9}{len(g_frame):>12}{len(s_frame):>12}{gbbox:>20}{pbbox:>22}")

print(f"\n总采样暗灰帧像素={total_g}")
print("\n说明：'g帧bbox' 是采样到暗灰区的帧像素位置。若 bbox 大体集中在角色上半身（剑柄带）而非全身，则安全。")
