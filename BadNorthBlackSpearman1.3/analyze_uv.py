# -*- coding: utf-8 -*-
"""解码 Swordsman0002 帧的 RGB→UV：帧像素的(R,G)编码部件贴图UV。
回答：哪些帧像素会渲染出"剑柄暗灰(54,50,49)"？这些帧像素在屏幕上的位置？"""
import os
from collections import Counter
from PIL import Image

SPR = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
frame_path = os.path.join(SPR, "Swordsman0002.png")
part_path = os.path.join(SPR, "PartTex_SwordShield.png")

im_f = Image.open(frame_path).convert("RGBA")
im_p = Image.open(part_path).convert("RGBA")
fw, fh = im_f.size
pw, ph = im_p.size
pf = im_f.load()
pp = im_p.load()

print(f"帧 {fw}x{fh}, 部件 {pw}x{ph}")

def classify(r, g, b, a):
    if a <= 8: return ' '
    if r > 150 and g > 150 and b > 150 and a > 128: return '#'  # 亮
    if 40 <= r <= 100 and abs(r - b) <= 25: return 'g'          # 暗灰剑柄
    if 100 < r < 150: return 'G'                                 # 亮灰护手
    if r > 100 and g > 60 and b > 40 and r - b > 30: return 's'  # 皮肤
    if r < 45 and g < 38 and b < 33: return 'b'                  # 身体暗色
    return '.'

# 1) 统计帧像素解码 UV 采样到的部件像素类别分布
part_class_count = Counter()
# 2) 渲染图：按帧像素位置，采样到的部件像素的类别（模拟"白框/剑柄残留"从哪来）
render_map = {}  # (fx,fy) -> part category
for fy in range(fh):
    for fx in range(fw):
        r, g, b, a = pf[fx, fy]
        if a <= 8: continue
        # 解码 UV
        cx = int((r / 255.0) * pw)
        cy = int((g / 255.0) * ph)
        if cx < 0 or cy < 0 or cx >= pw or cy >= ph:
            continue
        pr, pg, pb, pa = pp[cx, cy]
        c = classify(pr, pg, pb, pa)
        part_class_count[c] += 1
        render_map[(fx, fy)] = (c, (cx, cy))

print("帧不透明像素解码采样部件类别分布:", dict(part_class_count))

# 3) 打印帧渲染图：每个帧像素显示其采样的部件像素类别
print("\n帧渲染采样图（帧像素位置 → 部件像素类别）：")
for fy in range(fh):
    row = []
    for fx in range(fw):
        r, g, b, a = pf[fx, fy]
        if a <= 8:
            row.append(' ')
        else:
            c, (cx, cy) = render_map.get((fx, fy), ('?', (-1, -1)))
            # 特殊标注：命中"暗灰剑柄"
            row.append(c)
    print(f"{fy:3d}|{''.join(row)}")

# 4) 列出所有采样到"暗灰剑柄(g)"的帧像素的部件坐标范围
g_frame_px = [(fx, fy, cx, cy) for (fx, fy), (c, (cx, cy)) in render_map.items() if c == 'g']
if g_frame_px:
    fxs = [p[0] for p in g_frame_px]; fys = [p[1] for p in g_frame_px]
    cxs = [p[2] for p in g_frame_px]; cys = [p[3] for p in g_frame_px]
    print(f"\n采样到暗灰剑柄(g)的帧像素数={len(g_frame_px)}  帧bbox=({min(fxs)},{min(fys)})-({max(fxs)},{max(fys)})  部件bbox=({min(cxs)},{min(cys)})-({max(cxs)},{max(cys)})")

# 5) 列出所有采样到"亮灰护手(G)"的帧像素
G_frame_px = [(fx, fy, cx, cy) for (fx, fy), (c, (cx, cy)) in render_map.items() if c == 'G']
if G_frame_px:
    fxs = [p[0] for p in G_frame_px]; fys = [p[1] for p in G_frame_px]
    cxs = [p[2] for p in G_frame_px]; cys = [p[3] for p in G_frame_px]
    print(f"采样到亮灰护手(G)的帧像素数={len(G_frame_px)}  帧bbox=({min(fxs)},{min(fys)})-({max(fxs)},{max(fys)})  部件bbox=({min(cxs)},{min(cys)})-({max(cxs)},{max(cys)})")

# 6) 采样到亮银(#)的帧像素（白框源）
H_frame_px = [(fx, fy, cx, cy) for (fx, fy), (c, (cx, cy)) in render_map.items() if c == '#']
if H_frame_px:
    fxs = [p[0] for p in H_frame_px]; fys = [p[1] for p in H_frame_px]
    cxs = [p[2] for p in H_frame_px]; cys = [p[3] for p in H_frame_px]
    print(f"采样到亮银(#)的帧像素数={len(H_frame_px)}  帧bbox=({min(fxs)},{min(fys)})-({max(fxs)},{max(fys)})  部件bbox=({min(cxs)},{min(cys)})-({max(cxs)},{max(cys)})")
