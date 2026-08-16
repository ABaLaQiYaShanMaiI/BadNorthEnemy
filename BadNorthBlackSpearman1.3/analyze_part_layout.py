# -*- coding: utf-8 -*-
"""分析 PartTex_SwordShield 单元 + Swordsman 帧：标出头盔/剑柄/剑刃/盾牌区域（按颜色分类 ASCII 图）。"""
import os
from PIL import Image

BASE = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
part = Image.open(os.path.join(BASE, "PartTex_SwordShield.png")).convert("RGBA")
print("PartTex_SwordShield.png 尺寸:", part.size, "模式:", part.mode)
pw, ph = part.size
ppx = part.load()

# 找不透明 bbox
xs = [x for x in range(pw) for y in range(ph) if ppx[x, y][3] > 8]
if xs:
    print("不透明 bbox: x", min(x for x in range(pw) for y in range(ph) if ppx[x,y][3]>8),
          "-", max(x for x in range(pw) for y in range(ph) if ppx[x,y][3]>8),
          " y", min(y for x in range(pw) for y in range(ph) if ppx[x,y][3]>8),
          "-", max(y for x in range(pw) for y in range(ph) if ppx[x,y][3]>8))

def cls(c):
    r, g, b, a = c
    if a <= 8: return ' '          # 透明
    if r > 150 and g > 150 and b > 150: return '#'  # 亮银
    if 40 <= r <= 100 and abs(r - b) <= 25: return 'g'  # 暗灰(剑柄/头盔?)
    if 100 < r < 150 and abs(r-b) <= 25 and abs(g-b) <= 25: return 'G'  # 亮灰护手/盾沿
    if r - b > 30 and r > 110: return 's'  # 暖色皮肤
    if r > 100 and g > 80 and b > 70: return 'W'  # 暖棕/亮身
    return '.'                       # 暗身

print("\n===== PartTex_SwordShield 全图 (每 2px 采样) =====")
print("图例: #=亮银 g=暗灰 G=亮灰 s=暖肤 W=暖棕 .=暗身 空格=透明")
for y in range(0, ph, 2):
    row = ''.join(cls(ppx[x, y]) for x in range(0, pw, 2))
    print(f"{y:3d}|{row}")

# 逐行统计暗灰像素（定位剑柄带 vs 头盔）
print("\n===== 逐行统计 (g=暗灰 #=亮银 G=亮灰 s=暖肤 W=暖棕) =====")
for y in range(ph):
    row = [ppx[x, y] for x in range(pw)]
    gc = sum(1 for c in row if c[3] > 8 and 40 <= c[0] <= 100 and abs(c[0]-c[2]) <= 25)
    bc = sum(1 for c in row if c[3] > 8 and c[0] > 150 and c[1] > 150 and c[2] > 150)
    Gc = sum(1 for c in row if c[3] > 8 and 100 < c[0] < 150 and abs(c[0]-c[2]) <= 25 and abs(c[1]-c[2]) <= 25)
    sc = sum(1 for c in row if c[3] > 8 and c[0]-c[2] > 30 and c[0] > 110)
    Wc = sum(1 for c in row if c[3] > 8 and c[0] > 100 and c[1] > 80 and c[2] > 70)
    if gc or bc or Gc or sc or Wc:
        print(f"y={y:3d}: g={gc:3d} #={bc:3d} G={Gc:3d} s={sc:3d} W={Wc:3d}")
