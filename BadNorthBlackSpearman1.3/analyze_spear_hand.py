# -*- coding: utf-8 -*-
"""分析玩家长矛精灵 Spear_0/1/2：是否含"手"（暖肤像素）？手在哪个区域？"""
import os
from PIL import Image

BASE = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
for name in ["Spear_0.png", "Spear_1.png", "Spear_2.png"]:
    im = Image.open(os.path.join(BASE, name)).convert("RGBA")
    w, h = im.size
    px = im.load()
    warm = 0
    xs = []
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a > 8 and r - b > 25 and r > 130:
                warm += 1
                xs.append((x, y))
    print(f"\n===== {name} ({w}x{h}) =====")
    print(f"暖肤(手/脸)像素: {warm}")
    if xs:
        x0 = min(p[0] for p in xs); x1 = max(p[0] for p in xs)
        y0 = min(p[1] for p in xs); y1 = max(p[1] for p in xs)
        print(f"暖肤 bbox: x{x0}-{x1} y{y0}-{y1}")
        # ASCII 图（每2px）
        print("图例: s=暖肤 .=其他不透明 空格=透明")
        for y in range(0, h, 2):
            row = ''
            for x in range(0, w, 2):
                r, g, b, a = px[x, y]
                if a <= 8: row += ' '
                elif r - b > 25 and r > 130: row += 's'
                else: row += '.'
            if row.strip():
                print(f"{y:3d}|{row}")
