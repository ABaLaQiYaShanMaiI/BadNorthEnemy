# -*- coding: utf-8 -*-
"""Dump ALL shader exports to files, flag ones with _PartTex."""
import os, UnityPy

DATA = r"D:\Steam\steamapps\common\BadNorth\BadNorth_Data"
OUT = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/shaders"
os.makedirs(OUT, exist_ok=True)

files = [os.path.join(DATA, "sharedassets1.resource"), os.path.join(DATA, "data.unity3d")]
idx = 0
for f in files:
    try:
        env = UnityPy.load(f)
    except Exception as e:
        print("load fail", f, e)
        continue
    for obj in env.objects:
        if obj.type.name != "Shader":
            continue
        try:
            data = obj.read()
        except Exception:
            continue
        try:
            txt = data.export()
        except Exception:
            txt = b""
        if isinstance(txt, bytes):
            txts = txt.decode("utf-8", "replace")
        else:
            txts = txt
        idx += 1
        fn = os.path.join(OUT, f"shader_{idx:03d}.txt")
        with open(fn, "w", encoding="utf-8", errors="replace") as fh:
            fh.write(txts)
        flag = ""
        if "_PartTex" in txts:
            flag = " <<< _PartTex <<<"
        elif "PartTex" in txts:
            flag = " < PartTex"
        print(f"{os.path.basename(f)} shader#{idx} len={len(txts)} {flag}")
