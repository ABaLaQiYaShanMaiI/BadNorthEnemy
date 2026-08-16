# -*- coding: utf-8 -*-
"""检查运行时 PartTex 单元 (128,0,64,126)：布局 + 改色对盾牌区(y90-125)的影响。"""
import os
from PIL import Image

TEX = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/tex"
part = Image.open(os.path.join(TEX, "PartTex_Median_BlurAlpha__512x256.png")).convert("RGBA")
px = part.load()
BODY = (33, 26, 24)

def cls(r, g, b, a):
    if a <= 8: return ' '
    if r > 150 and g > 150 and b > 150 and a > 128: return '#'
    if 40 <= r <= 100 and abs(r - b) <= 25: return 'g'
    if 100 < r < 150 and abs(r - b) <= 25 and abs(g - b) <= 25: return 'G'
    if r > 100 and g > 60 and b > 40 and r - b > 30: return 's'
    if r < 45 and g < 38 and b < 33: return 'b'
    return '.'

print("== 运行时 PartTex_SwordShield 单元 (128,0,64,126) 现状 ASCII ==")
for y in range(0, 126):
    row = []
    for x in range(128, 192):
        r, g, b, a = px[x, y]
        row.append(cls(r, g, b, a))
    print(f"{y:3d}|{''.join(row)}")

# 统计改色会改动盾牌区的像素
print("\n== 改色影响统计（把 g/G 改身体色）==")
for (label, y0, y1) in [("剑区 y20-88", 20, 88), ("盾牌区 y89-125", 89, 125)]:
    hit = 0; keep = 0
    for y in range(y0, y1):
        for x in range(128, 192):
            r, g, b, a = px[x, y]
            if a <= 8: continue
            if (40 <= r <= 100 and abs(r - b) <= 25) or (100 < r < 150 and abs(r - b) <= 25 and abs(g - b) <= 25):
                hit += 1
            else:
                keep += 1
    print(f"  {label}: 改色命中={hit} 保留={keep}")
