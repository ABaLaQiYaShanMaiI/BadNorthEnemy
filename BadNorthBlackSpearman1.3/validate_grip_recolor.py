# -*- coding: utf-8 -*-
"""验证"剑柄改色"方案（运行时纹理）：
渲染模型 = 帧像素 R/G 编码 UV → 采样部件贴图（cand0），乘黑染色。
对比 Swordsman0002 在 ①现状(亮银擦除) ②改色(亮银擦除+剑柄改身体色) 下的胸口剑柄带。
"""
import os
from PIL import Image

TEX = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/tex"
part = Image.open(os.path.join(TEX, "PartTex_Median_BlurAlpha__512x256.png")).convert("RGBA")
fa = Image.open(os.path.join(TEX, "SpriteAtlasTexture-Sprites (Group 0)-2048x1024-fmt4__2048x1024.png")).convert("RGBA")

pcx, pcy, pcw, pch = 128, 0, 64, 126
BODY = (33, 26, 24, 255)   # 身体暗色（改色目标）

def part_uv(r, g):
    return (int(pcx + (r / 255.0) * pcw), int(pcy + (g / 255.0) * pch))

def is_bright(pr, pg, pb, pa):
    return pa > 128 and pr > 150 and pg > 150 and pb > 150

def is_grip(pr, pg, pb, pa):
    # 与运行时诊断分类一致：暗灰剑柄 40≤r≤100 |r-b|≤25；亮灰护手 100<r<150 中性
    return pa > 8 and 40 <= pr <= 100 and abs(pr - pb) <= 25

def is_guard(pr, pg, pb, pa):
    return pa > 8 and 100 < pr < 150 and abs(pr - pb) <= 25 and abs(pg - pb) <= 25

def make_part(bright_erase, grip_recolor):
    """克隆部件贴图；bright_erase=擦亮银；grip_recolor=剑柄/护手改身体色"""
    p2 = part.copy()
    px = p2.load()
    for yy in range(pcy, pcy + pch):
        for xx in range(pcx, pcx + pcw):
            pr, pg, pb, pa = px[xx, yy]
            if bright_erase and is_bright(pr, pg, pb, pa):
                px[xx, yy] = (0, 0, 0, 0)
            elif grip_recolor and (is_grip(pr, pg, pb, pa) or is_guard(pr, pg, pb, pa)):
                px[xx, yy] = BODY
    return p2

def render_frame(p2, fx0, fy0, fw, fh, title):
    """渲染帧：返回 ASCII + 计数。g=渲染出剑柄色(改色前) b=身体色 h=洞(部件透明)"""
    rows = []
    grip_cnt = body_cnt = hole_cnt = bright_cnt = 0
    pxx = p2.load()
    for yy in range(fh - 1, -1, -1):
        row = []
        for xx in range(fw):
            r, g, b, a = fa.getpixel((fx0 + xx, fy0 + yy))
            if a <= 8:
                row.append(' ')
                continue
            u = part_uv(r, g)
            pr, pg, pb, pa = pxx[u[0], u[1]]
            if pa <= 8:
                hole_cnt += 1
                row.append('H')
                continue
            if is_grip(pr, pg, pb, pa) or is_guard(pr, pg, pb, pa):
                grip_cnt += 1
                row.append('g')
            elif is_bright(pr, pg, pb, pa):
                bright_cnt += 1
                row.append('B')
            else:
                body_cnt += 1
                row.append('.')
        rows.append(f"{yy:3d}|{''.join(row)}")
    print(f"== {title}: 剑柄色像素={grip_cnt} 身体色={body_cnt} 洞(部件透明)={hole_cnt} 亮={bright_cnt} ==")
    for r in rows:
        print(r)
    print()
    return grip_cnt

# 帧 rect（运行时日志）
FRAMES = [
    ("Swordsman0002", 601, 642, 43, 70),
    ("Swordsman0001", 311, 393, 54, 70),
    ("Swordsman0017", 601, 642, 43, 70),   # 走路帧（rect 未必准，仅示意）
]
for name, fx0, fy0, fw, fh in FRAMES:
    p_now = make_part(True, False)    # 现状：亮银擦除
    p_fix = make_part(True, True)     # 改色：亮银擦除 + 剑柄改身体色
    print("=" * 74)
    render_frame(p_now, fx0, fy0, fw, fh, f"{name} 现状(亮银擦除)")
    render_frame(p_fix, fx0, fy0, fw, fh, f"{name} 改色(亮银擦除+剑柄改身体色)")
