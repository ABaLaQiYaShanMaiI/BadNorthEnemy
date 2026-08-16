# -*- coding: utf-8 -*-
"""离线分析 PartTex_SwordShield 与 Swordsman0002 帧：剑柄/护手/盾牌的像素类与连通关系。
回答：为何运行时 flood 擦不到剑柄（剑柄是否与亮银剑刃 8 连通）。"""
import sys, os
from collections import deque
from PIL import Image

SPR = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"

def classify(r, g, b, a):
    if a <= 8:
        return ' '            # 透明
    if r > 150 and g > 150 and b > 150 and a > 128:
        return '#'            # 亮银（运行时亮采样阈值）
    if 40 <= r <= 100 and abs(r - b) <= 25:
        return 'g'            # 暗灰剑柄（诊断分类）
    if 100 < r < 150:
        return 'G'            # 亮灰护手/盾沿
    if r > 100 and g > 60 and b > 40 and r - b > 30:
        return 's'            # 暖色皮肤
    if r < 45 and g < 38 and b < 33:
        return 'b'            # 身体暗色
    return '.'                # 其他

def ascii_map(path, title, x0=0, y0=0, x1=None, y1=None):
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    px = im.load()
    x1 = x1 if x1 is not None else w
    y1 = y1 if y1 is not None else h
    print(f"== {title} ({os.path.basename(path)}) {w}x{h} 显示区域 x[{x0},{x1}) y[{y0},{y1}) ==")
    # 与日志一致：y 自上而下打印（日志显示 y 递增），但为直观起见 y 从 y0 到 y1
    for y in range(y0, y1):
        row = []
        for x in range(x0, x1):
            r, g, b, a = px[x, y]
            row.append(classify(r, g, b, a))
        print(f"{y:3d}|{''.join(row)}")
    print()

def analyze_part():
    path = os.path.join(SPR, "PartTex_SwordShield.png")
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    px = im.load()
    print(f"PartTex_SwordShield 尺寸={w}x{h}")
    # 全类统计
    from collections import Counter
    cnt = Counter()
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            cnt[classify(r, g, b, a)] += 1
    print("像素类计数:", dict(cnt))

    # 连通组件：亮银 + 暗灰 + 亮灰 + 皮肤 视为"剑相关"候选；身体暗色/透明为屏障
    # 我们关心：亮银(#)与暗灰(g)是否8连通
    bright = set()
    darkg = set()
    shield_like = set()
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            c = classify(r, g, b, a)
            if c == '#': bright.add((x, y))
            elif c == 'g': darkg.add((x, y))
            elif c == 'G': shield_like.add((x, y))

    def components(pixels):
        comps = []
        rem = set(pixels)
        while rem:
            seed = rem.pop()
            stack = [seed]
            comp = []
            while stack:
                p = stack.pop()
                comp.append(p)
                x, y = p
                for dx in (-1, 0, 1):
                    for dy in (-1, 0, 1):
                        if dx == 0 and dy == 0: continue
                        q = (x + dx, y + dy)
                        if q in rem:
                            rem.discard(q)
                            stack.append(q)
            comps.append(comp)
        return comps

    print("\n-- 亮银(#)连通组件 --")
    bc = components(bright)
    for i, c in enumerate(bc):
        xs = [p[0] for p in c]; ys = [p[1] for p in c]
        print(f"  组件{i}: n={len(c)} bbox=({min(xs)},{min(ys)})-({max(xs)},{max(ys)})")
    print("\n-- 暗灰(g)连通组件 --")
    gc = components(darkg)
    for i, c in enumerate(gc):
        xs = [p[0] for p in c]; ys = [p[1] for p in c]
        # 与亮银是否邻接
        adj_bright = 0
        for (x, y) in c:
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    if (x+dx, y+dy) in bright: adj_bright += 1
        print(f"  组件{i}: n={len(c)} bbox=({min(xs)},{min(ys)})-({max(xs)},{max(ys)}) 邻接亮银像素数={adj_bright}")
    # 亮灰护手
    print(f"\n亮灰(G)像素数={len(shield_like)}")
    if shield_like:
        xs = [p[0] for p in shield_like]; ys = [p[1] for p in shield_like]
        print(f"  亮灰bbox=({min(xs)},{min(ys)})-({max(xs)},{max(ys)})")

    # 完整 ASCII（y 从上到下）
    print()
    ascii_map(path, "PartTex_SwordShield 全图", y1=h)

def analyze_frame():
    path = os.path.join(SPR, "Swordsman0002.png")
    im = Image.open(path).convert("RGBA")
    w, h = im.size
    px = im.load()
    print(f"Swordsman0002 尺寸={w}x{h}")
    ascii_map(path, "Swordsman0002 帧", y1=h)

if __name__ == "__main__":
    analyze_part()
    print()
    analyze_frame()
