# -*- coding: utf-8 -*-
"""精确映射：渲染后的 Swordsman 帧每一行 → 采样到 PartTex 的哪个区域（y 范围）+ 原色分类。
用于设计"分区压暗"：头盔区保留原色、躯干/手压黑。"""
import os
from collections import Counter
from PIL import Image

BASE = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
part = Image.open(os.path.join(BASE, "PartTex_SwordShield.png")).convert("RGBA")
pw, ph = part.size
ppx = part.load()

for frame_name in ["Swordsman0001.png", "Swordsman0002.png", "Swordsman0003.png", "Swordsman0005.png"]:
    frame = Image.open(os.path.join(BASE, frame_name)).convert("RGBA")
    fw, fh = frame.size
    fpx = frame.load()
    print(f"\n========== {frame_name} ({fw}x{fh}) ==========")
    print("每行：该行不透明像素采样到的 PartTex y 范围 + 主要区域 + 原色分类计数")
    for y in range(fh):
        ys = Counter()
        cats = Counter()
        for x in range(fw):
            fr, fg, fb, fa = fpx[x, y]
            if fa <= 8: continue
            py = min(int(fg / 255.0 * ph), ph - 1)
            ys[py] += 1
            pr, pg, pb, pa = ppx[min(int(fr/255.0*pw), pw-1), py]
            if pa <= 8: cats['透'] += 1
            elif pr > 150 and pg > 150 and pb > 150: cats['#亮银'] += 1
            elif pr - pb > 25 and pr > 130: cats['s暖肤'] += 1
            elif pr > 100 and pg > 90 and pb > 70: cats['W暖棕'] += 1
            elif 40 <= pr <= 100 and abs(pr-pb) <= 25: cats['g暗灰'] += 1
            else: cats['.暗'] += 1
        if not ys: continue
        ymin = min(ys); ymax = max(ys)
        dom = cats.most_common(2)
        print(f" 帧y={y:2d}: PartTex y={ymin:3d}-{ymax:3d} 像素={sum(ys.values()):3d} 分类={dom}")
