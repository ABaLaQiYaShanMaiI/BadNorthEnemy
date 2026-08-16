# -*- coding: utf-8 -*-
"""验证路线 A：部件贴图"整剑改身体色"（替代"亮银擦透明"）。
渲染模型 = 帧像素 R/G 编码 UV → 采样部件贴图（cand0），乘黑染色。
对比 Swordsman0002 在 ①现状(亮银擦透明→挖洞) ②路线A(亮银+剑柄+护手全改身体色→零洞) 下的胸口/剑区。
判据：路线A 应满足 剑柄色像素=0、亮银残=0、洞=0、身体色像素≈原身体轮廓。
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
    return pa > 8 and 40 <= pr <= 100 and abs(pr - pb) <= 25

def is_guard(pr, pg, pb, pa):
    return pa > 8 and 100 < pr < 150 and abs(pr - pb) <= 25 and abs(pg - pb) <= 25

def is_body(pr, pg, pb, pa):
    return pa > 8 and pr < 45 and pg < 38 and pb < 33

def make_part(mode):
    """mode: 'erase'=现状(亮银擦透明+剑柄改色)  'routeA'=路线A(亮银+剑柄+护手全改身体色)"""
    p2 = part.copy()
    px = p2.load()
    for yy in range(pcy, pcy + pch):
        for xx in range(pcx, pcx + pcw):
            pr, pg, pb, pa = px[xx, yy]
            if mode == 'erase':
                if is_bright(pr, pg, pb, pa):
                    px[xx, yy] = (0, 0, 0, 0)
                elif is_grip(pr, pg, pb, pa) or is_guard(pr, pg, pb, pa):
                    px[xx, yy] = BODY
            else:  # routeA: 亮银也改身体色（保留 alpha），剑柄/护手同样改
                if is_bright(pr, pg, pb, pa) or is_grip(pr, pg, pb, pa) or is_guard(pr, pg, pb, pa):
                    px[xx, yy] = (BODY[0], BODY[1], BODY[2], pa if pa > 8 else 0)
    return p2

def render_frame(p2, fx0, fy0, fw, fh, title):
    rows = []
    grip_cnt = body_cnt = hole_cnt = bright_cnt = other_cnt = 0
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
            elif is_body(pr, pg, pb, pa):
                body_cnt += 1
                row.append('b')
            else:
                other_cnt += 1
                row.append('.')
        rows.append(f"{yy:3d}|{''.join(row)}")
    print(f"== {title}: 剑柄/护手色={grip_cnt} 身体色={body_cnt} 洞={hole_cnt} 亮银残={bright_cnt} 其他={other_cnt} ==")
    for rr in rows:
        print(rr)
    print()
    return hole_cnt, bright_cnt, grip_cnt

FRAMES = [
    ("Swordsman0002", 601, 642, 43, 70),
    ("Swordsman0001", 311, 393, 54, 70),
]
summary = {}
for name, fx0, fy0, fw, fh in FRAMES:
    for mode, label in [('erase', '现状(亮银擦透明+剑柄改色)'), ('routeA', '路线A(整剑改身体色)')]:
        p = make_part(mode)
        h, b, g = render_frame(p, fx0, fy0, fw, fh, f"{name} {label}")
        summary[(name, mode)] = (h, b, g)

print("=" * 74)
for k, (h, b, g) in summary.items():
    name, mode = k
    verdict = "✅ 零洞零亮零剑柄" if (h == 0 and b == 0 and g == 0) else ("⚠️ 有洞" if h > 0 else "")
    print(f"{name:16s} {mode:28s} 洞={h} 亮银残={b} 剑柄色={g} {verdict}")
