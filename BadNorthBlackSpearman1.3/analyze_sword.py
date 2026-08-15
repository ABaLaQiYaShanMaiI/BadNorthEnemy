# -*- coding: utf-8 -*-
"""离线分析 Onehanded 帧：剑刃(红暗)与剑柄(非红不透明)像素坐标，及 PartTex_Sword 全身像。"""
import sys, os, glob
from PIL import Image

SPR = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"

def analyze_frame(path):
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    px = im.load()
    red = []      # 红暗(70/40/20)
    red_n = []    # 窄(90/25/10)
    other_opaque = []  # 不透明非红暗
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a <= 8:
                continue
            if r > 70 and g < 40 and b < 20:
                red.append((x, y, (r, g, b)))
                if r > 90 and g < 25 and b < 10:
                    red_n.append((x, y, (r, g, b)))
            else:
                other_opaque.append((x, y, (r, g, b)))
    # 剑柄 = 非红不透明，且紧邻红暗像素（8邻域）或落在红暗包围盒的水平带内、靠右侧
    if red:
        rx0 = min(p[0] for p in red); rx1 = max(p[0] for p in red)
        ry0 = min(p[1] for p in red); ry1 = max(p[1] for p in red)
    else:
        rx0 = rx1 = ry0 = ry1 = -1
    red_set = set((x, y) for x, y, _ in red)
    hilt = []
    for x, y, c in other_opaque:
        if x >= rx0 - 3 and x <= rx1 + 3 and y >= ry0 - 3 and y <= ry1 + 6:
            # 8邻域贴近红暗 or 在包围盒内偏右
            near = any((x+dx, y+dy) in red_set for dx in (-1,0,1) for dy in (-1,0,1))
            if near or x >= rx1 - 2:
                hilt.append((x, y, c))
    return w, h, red, red_n, other_opaque, hilt, (rx0, rx1, ry0, ry1)

def ascii_map(path):
    w, h, red, red_n, other, hilt, bbox = analyze_frame(path)
    im = Image.open(path).convert("RGBA")
    px = im.load()
    red_set = set((x, y) for x, y, _ in red)
    hilt_set = set((x, y) for x, y, _ in hilt)
    lines = []
    for y in range(h - 1, -1, -1):
        row = []
        for x in range(w):
            r, g, b, a = px[x, y]
            if a <= 8:
                row.append(' ')
            elif (x, y) in hilt_set:
                row.append('H')          # 剑柄/疑似柄
            elif r > 90 and g < 25 and b < 10:
                row.append('S')
            elif r > 70 and g < 40 and b < 20:
                row.append('s')
            elif r > 150 and g > 150 and b > 150:
                row.append('#')
            else:
                row.append('.')
        lines.append(''.join(row).rstrip())
    return w, h, red, red_n, other, hilt, bbox, lines

def main():
    files = sorted(glob.glob(os.path.join(SPR, "Onehanded*.png")))
    print("== 全部帧统计 ==")
    for f in files:
        w, h, red, red_n, other, hilt, bbox = analyze_frame(f)
        name = os.path.basename(f)
        r_xr = (min(p[0] for p in red), max(p[0] for p in red)) if red else (-1, -1)
        r_yr = (min(p[1] for p in red), max(p[1] for p in red)) if red else (-1, -1)
        h_xr = (min(p[0] for p in hilt), max(p[0] for p in hilt)) if hilt else (-1, -1)
        h_yr = (min(p[1] for p in hilt), max(p[1] for p in hilt)) if hilt else (-1, -1)
        print("%s  %dx%d  红暗=%d(x%d-%d,y%d-%d) 窄=%d 剑柄=%d(x%d-%d,y%d-%d)" % (
            name, w, h, len(red), r_xr[0], r_xr[1], r_yr[0], r_yr[1], len(red_n),
            len(hilt), h_xr[0], h_xr[1], h_yr[0], h_yr[1]))

    print("\n== Onehanded0002 ASCII (S=红窄 s=红宽 H=剑柄 #=亮 .=其他不透明) ==")
    w, h, red, red_n, other, hilt, bbox, lines = ascii_map(files[0])
    for i, ln in enumerate(lines):
        print("%3d|%s" % (i, ln))

    print("\n== PartTex_Sword.png ASCII ==")
    pt = os.path.join(SPR, "PartTex_Sword.png")
    if os.path.exists(pt):
        im = Image.open(pt).convert("RGBA")
        pw, ph = im.size
        pxx = im.load()
        for y in range(ph - 1, -1, -1):
            row = []
            for x in range(pw):
                r, g, b, a = pxx[x, y]
                if a <= 8:
                    row.append(' ')
                elif r > 90 and g < 25 and b < 10:
                    row.append('S')
                elif r > 70 and g < 40 and b < 20:
                    row.append('s')
                elif r > 150 and g > 150 and b > 150:
                    row.append('#')
                else:
                    row.append('.')
            print("%3d|%s" % (y, ''.join(row).rstrip()))
        # 统计剑红像素
        red2 = [(x, y) for y in range(ph) for x in range(pw)
                for (r, g, b, a) in [pxx[x, y]] if a > 8 and r > 70 and g < 40 and b < 20]
        print("PartTex_Sword 尺寸=%dx%d 红暗=%d" % (pw, ph, len(red2)))

if __name__ == "__main__":
    main()
