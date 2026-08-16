# -*- coding: utf-8 -*-
"""分析 Swordsman 帧 PNG 的通道统计：R/G/B/A 分布，确认帧纹理里是否含"白/亮"成分（可能被着色器当白闪）。"""
import os
from collections import Counter
from PIL import Image

BASE = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
for name in ["Swordsman0001.png", "Swordsman0002.png", "Swordsman0003.png", "Swordsman0005.png"]:
    im = Image.open(os.path.join(BASE, name)).convert("RGBA")
    w, h = im.size
    px = im.load()
    # 统计不透明像素的 R/G/B 分布 + 全通道亮度
    rb = Counter(); gb = Counter(); bb = Counter(); ab = Counter()
    bright = 0; opaque = 0
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a <= 8: continue
            opaque += 1
            rb[r//32] += 1; gb[g//32] += 1; bb[b//32] += 1; ab[a//32] += 1
            if r > 150 and g > 150 and b > 150: bright += 1
    def top(c):
        return ', '.join(f"{k*32}-{k*32+31}:{v}" for k, v in c.most_common(5))
    print(f"\n===== {name} ({w}x{h}) 不透明={opaque} =====")
    print(f"  R分布: {top(rb)}")
    print(f"  G分布: {top(gb)}")
    print(f"  B分布: {top(bb)}")
    print(f"  A分布: {top(ab)}")
    print(f"  近白像素(r,g,b>150)={bright}")
    # 帧的 B 通道整体均值（不透明区）
    bsum = 0
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a > 8: bsum += b
    print(f"  不透明区 B 通道均值 = {bsum/opaque:.1f}（若显著高 → 帧带白/亮成分）")
