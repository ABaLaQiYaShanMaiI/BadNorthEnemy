# -*- coding: utf-8 -*-
"""分析长矛精灵手部区域（y8-12）的完整颜色分布，定位"蓝色手"的精确色值，设计压黑阈值。"""
import os
from collections import Counter
from PIL import Image

BASE = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"

def classify(c):
    r, g, b, a = c
    if a <= 8: return '透明'
    if r > 150 and g > 150 and b > 150: return '亮白/银'
    if b > r and b > g and b > 80: return '蓝'      # 蓝色系
    if r - b > 25 and r > 130: return '暖肤'        # 暖肤
    if g > r and g > b and g > 80: return '绿'      # 绿色系
    if r > g > b and r > 100: return '暖棕/红棕'
    if r > 60 and g > 40 and b < 60: return '暗红棕'
    if 40 <= r <= 100 and abs(r-b) <= 25: return '暗灰'
    if r > 100 and g > 80 and b > 60: return '亮棕/中灰'
    return '其它'

for name in ["Spear_0.png", "Spear_1.png", "Spear_2.png"]:
    im = Image.open(os.path.join(BASE, name)).convert("RGBA")
    w, h = im.size
    px = im.load()
    print(f"\n===== {name} ({w}x{h}) =====")
    # 手部区域 y6-13（含手）；统计颜色分类 + 蓝色像素精确色值
    cat = Counter()
    blue_samples = []
    for y in range(6, 14):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a <= 8: continue
            cat[classify((r, g, b, a))] += 1
            if b > r and b > g and b > 80:
                blue_samples.append((x, y, r, g, b))
    print("手部区域颜色分类:", dict(cat))
    if blue_samples:
        # 蓝色像素的色值范围
        rs = [s[2] for s in blue_samples]; gs = [s[3] for s in blue_samples]; bs = [s[4] for s in blue_samples]
        print(f"蓝色像素 {len(blue_samples)} 个: R {min(rs)}-{max(rs)} G {min(gs)}-{max(gs)} B {min(bs)}-{max(bs)}")
        # 最常见的蓝色
        blue_counter = Counter((s[2]//16, s[3]//16, s[4]//16) for s in blue_samples)
        top = blue_counter.most_common(5)
        print("蓝色常见色桶(每16):", [(f"{k[0]*16}-{k[0]*16+15},{k[1]*16}-{k[1]*16+15},{k[2]*16}-{k[2]*16+15}", v) for k, v in top])
    # 手部 ASCII（B=蓝 s=暖肤 .=其它 空格=透明）
    print("手部区域图例: B=蓝 s=暖肤 .=其它 空格=透明")
    for y in range(6, 14):
        row = ''
        for x in range(w):
            r, g, b, a = px[x, y]
            if a <= 8: row += ' '
            elif b > r and b > g and b > 80: row += 'B'
            elif r - b > 25 and r > 130: row += 's'
            else: row += '.'
        if row.strip(): print(f"  y{y}|{row}")
