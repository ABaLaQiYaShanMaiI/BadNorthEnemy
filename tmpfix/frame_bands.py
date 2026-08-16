# -*- coding: utf-8 -*-
"""全帧摘要 v3：头盔带(y10-30) 与 躯干带(y30-55) 采样单元的暗灰/暖棕源，确认两带不重叠。"""
import os, glob
from PIL import Image

BASE = r"C:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthDatabase-main\extracted_assets\Sprite"
part = Image.open(os.path.join(BASE, "PartTex_SwordShield.png")).convert("RGBA")
pw, ph = part.size
ppx = part.load()

def band_src(frame, fy0, fy1):
    fw, fh = frame.size
    fpx = frame.load()
    ys_g = []; ys_w = []
    for fy in range(fy0, min(fy1, fh)):
        for fx in range(fw):
            fr, fg, fb, fa = fpx[fx, fy]
            if fa <= 8: continue
            pxx = min(int(fr / 255.0 * pw), pw - 1)
            pyy = min(int(fg / 255.0 * ph), ph - 1)
            if not (0 <= pxx < pw and 0 <= pyy < ph): continue
            pr, pg, pb, pa = ppx[pxx, pyy]
            if pa <= 8: continue
            if 40 <= pr <= 100 and abs(pr - pb) <= 25:
                ys_g.append(pyy)
            elif pr > 100 and pg > 90 and pb > 70 and not (pr - pb > 25 and pr > 130):
                ys_w.append(pyy)
    def rng(a):
        return "none" if not a else "%3d-%3d(%d)" % (min(a), max(a), len(a))
    return "g " + rng(ys_g) + "  W " + rng(ys_w)

names = sorted(glob.glob(os.path.join(BASE, "Swordsman*.png")))
print("frame        helm(y10-30) g/W src                    torso(y30-55) g/W src")
for fn in names:
    fr = Image.open(fn).convert("RGBA")
    n = os.path.basename(fn).replace(".png", "")
    h = band_src(fr, 10, 30)
    t = band_src(fr, 30, 55)
    print("%-11s  %-40s | %s" % (n, h, t))
