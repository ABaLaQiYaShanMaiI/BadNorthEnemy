# -*- coding: utf-8 -*-
"""离线验证新擦除算法：红暗(整帧) + 外侧非红不透明(bbox 纵向带 ±6px)。输出每帧擦除统计 + 关键帧前后 ASCII。"""
import os, glob
from PIL import Image

SPR = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
OUTER = 6
HILT = 5   # 剑柄/护手水平带：剑刃基部向"身体侧"扩展
RMIN, GMAX, BMAX = 70, 40, 20

def bounds(px, w, h):
    rx0, ry0, rx1, ry1 = 999, 999, -1, -1
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[y][x]
            if a > 8 and r > RMIN and g < GMAX and b < BMAX:
                if x < rx0: rx0 = x
                if x > rx1: rx1 = x
                if y < ry0: ry0 = y
                if y > ry1: ry1 = y
    return None if rx1 < 0 else (rx0, ry0, rx1, ry1)

def erase(px, w, h):
    b = bounds(px, w, h)
    if not b: return 0, 0
    rx0, ry0, rx1, ry1 = b
    blade_center = (rx0 + rx1) * 0.5
    frame_center = w * 0.5
    outer_right = blade_center >= frame_center   # 剑偏右（尖在右）→ 剑柄/护手在基部左侧
    v0, v1 = ry0 - OUTER, ry1 + OUTER
    # 剑柄/护手水平带：剑刃基部向"身体侧"（尖端反侧）扩展 HILT px
    if outer_right: ex0, ex1 = max(0, rx0 - HILT), rx0 + 2 + 1
    else:           ex0, ex1 = rx1 - 2, min(w, rx1 + HILT + 1)
    red_e = nonred_e = 0
    for y in range(h):
        for x in range(w):
            r, g, bl, a = px[y][x]
            if a <= 8: continue
            if r > RMIN and g < GMAX and bl < BMAX:
                px[y][x] = (0, 0, 0, 0); red_e += 1; continue
            # 居中剑不擦外侧（避免误擦居中持剑的身体）
            if abs(blade_center - frame_center) >= 5 and v0 <= y <= v1 and ex0 <= x < ex1:
                px[y][x] = (0, 0, 0, 0); nonred_e += 1
    return red_e, nonred_e

def ascii_of(px, w, h):
    lines = []
    for y in range(h - 1, -1, -1):
        row = []
        for x in range(w):
            r, g, b, a = px[y][x]
            if a <= 8: row.append(' ')
            elif r > 90 and g < 25 and b < 10: row.append('S')
            elif r > RMIN and g < GMAX and b < BMAX: row.append('s')
            elif r > 150 and g > 150 and b > 150: row.append('#')
            else: row.append('.')
        lines.append(''.join(row).rstrip())
    return lines

def main():
    files = sorted(glob.glob(os.path.join(SPR, "Onehanded*.png")))
    print("== 新算法模拟（每帧：擦除红暗 + 外侧非红）==")
    body_issues = []
    for f in files:
        im = Image.open(f).convert("RGBA")
        w, h = im.size
        src = im.load()
        orig = [[src[x, y] for x in range(w)] for y in range(h)]
        work = [list(row) for row in orig]
        red_e, nonred_e = erase(work, w, h)
        name = os.path.basename(f)
        opaque = sum(1 for y in range(h) for x in range(w) if orig[y][x][3] > 8)
        ratio = (red_e + nonred_e) * 100.0 / max(1, opaque)
        if ratio > 19.0: flag_ratio = " ⚠️占比%.0f%%(超19%%)" % ratio
        else: flag_ratio = " 占比%.1f%%" % ratio
        b = bounds(orig, w, h)
        rx0, ry0, rx1, ry1 = b
        blade_center = (rx0 + rx1) * 0.5
        frame_center = w * 0.5
        outer_ok = abs(blade_center - frame_center) >= 5
        # 身体侧超带误擦：被擦掉的非红像素超出剑柄带（右偏剑 x<rx0-HILT，左偏剑 x>rx1+HILT）
        over_erase = 0
        outer_right = blade_center >= frame_center
        v0, v1 = ry0 - OUTER, ry1 + OUTER
        for y in range(h):
            if not (v0 <= y <= v1): continue
            for x in range(w):
                r, g, bl, a = orig[y][x]
                if a <= 8 or (r > RMIN and g < GMAX and bl < BMAX): continue
                if work[y][x][3] > 8: continue   # 未被擦
                if outer_right and x < rx0 - HILT: over_erase += 1
                if not outer_right and x > rx1 + HILT: over_erase += 1
        # 剑柄带残留：擦除后剑柄带内剩余的非红不透明数（应尽量小）
        remain = 0
        if abs(blade_center - frame_center) >= 5:
            if outer_right: ex0, ex1 = max(0, rx0 - HILT), rx0 + 3
            else:           ex0, ex1 = rx1 - 2, min(w, rx1 + HILT + 1)
            for y in range(max(0, v0), min(h, v1 + 1)):
                for x in range(ex0, ex1):
                    r, g, bl, a = orig[y][x]
                    if a <= 8 or work[y][x][3] <= 8: continue
                    if r > RMIN and g < GMAX and bl < BMAX: continue
                    remain += 1
        flag = ""
        if over_erase > 8: flag += " ⚠️身体超带误擦%d" % over_erase
        if remain > 8: flag += " ⚠️剑柄带残留%d" % remain
        print("%s 红暗擦=%d 剑柄擦=%d 身体超带误擦=%d 剑柄带残留=%d%s%s" % (
            name, red_e, nonred_e, over_erase, remain, flag_ratio, flag))
        if flag: body_issues.append((name, over_erase, remain))

    print("\n== 关键帧前后对比（前 | 后）==")
    for key in ("Onehanded0002.png", "Onehanded0030.png", "Onehanded0031.png", "Onehanded0034.png", "Onehanded0035.png", "Onehanded0061.png"):
        path = os.path.join(SPR, key)
        if not os.path.exists(path): continue
        im = Image.open(path).convert("RGBA")
        w, h = im.size
        src = im.load()
        orig = [[src[x, y] for x in range(w)] for y in range(h)]
        work = [list(row) for row in orig]
        red_e, nonred_e = erase(work, w, h)
        a1, a2 = ascii_of(orig, w, h), ascii_of(work, w, h)
        print("\n--- %s  擦除=红%d+外%d ---" % (key, red_e, nonred_e))
        for i in range(len(a1)):
            print(" %s | %s" % (a1[i].ljust(w), a2[i]))

if __name__ == "__main__":
    main()
