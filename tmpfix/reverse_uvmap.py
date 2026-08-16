# -*- coding: utf-8 -*-
"""反向映射：部件单元每一行(按颜色分类)被 Swordsman 帧的哪些行采样。
用于精确定位：头盔可见的颜色源 = 部件哪些行；剑柄/剑刃源 = 部件哪些行。"""
import os
from collections import Counter, defaultdict
from PIL import Image

BASE = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
part = Image.open(os.path.join(BASE, "PartTex_SwordShield.png")).convert("RGBA")
pw, ph = part.size   # 提取图即单元: 64x126
ppx = part.load()

def cls(c):
    r, g, b, a = c
    if a <= 8: return 'T'
    if r > 150 and g > 150 and b > 150: return '#'   # 亮银
    if r - b > 25 and r > 130: return 's'            # 暖肤
    if r > 100 and g > 90 and b > 70: return 'W'     # 暖棕
    if 40 <= r <= 100 and abs(r - b) <= 25: return 'g'  # 暗灰
    return '.'

for frame_name in ["Swordsman0001.png", "Swordsman0002.png", "Swordsman0003.png", "Swordsman0005.png"]:
    frame = Image.open(os.path.join(BASE, frame_name)).convert("RGBA")
    fw, fh = frame.size
    fpx = frame.load()
    byrow = defaultdict(Counter)   # (py, cls) -> Counter(帧y)
    for fy in range(fh):
        for fx in range(fw):
            fr, fg, fb, fa = fpx[fx, fy]
            if fa <= 8: continue
            pxx = min(int(fr / 255.0 * pw), pw - 1)
            pyy = min(int(fg / 255.0 * ph), ph - 1)
            if not (0 <= pxx < pw and 0 <= pyy < ph): continue
            byrow[(pyy, cls(ppx[pxx, pyy]))][fy] += 1
    print("\n===== %s 反向映射（单元行 <- 被帧哪些y行采样）=====" % frame_name)
    bypy = defaultdict(list)
    for (cy, c), cnt in byrow.items():
        bypy[cy].append((c, cnt))
    for cy in sorted(bypy.keys()):
        items = bypy[cy]
        fys = Counter()
        for (c, cnt) in items:
            for fy, n in cnt.items():
                fys[fy] += n
        fy_min, fy_max = (min(fys), max(fys)) if fys else (-1, -1)
        total = sum(fys.values())
        dom = Counter()
        for (c, cnt) in items:
            dom[c] += sum(cnt.values())
        top = dom.most_common(3)
        s = ' '.join('%s=%d' % (c, n) for c, n in top)
        print("  partY=%3d: 帧y %3d-%3d 像素=%4d  分类[%s]" % (cy, fy_min, fy_max, total, s))

