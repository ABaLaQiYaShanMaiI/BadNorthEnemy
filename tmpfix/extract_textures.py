# -*- coding: utf-8 -*-
"""Extract the PartTex atlas + Sprite atlas textures from Bad North assets."""
import os, sys, io
import UnityPy

DATA = r"D:\Steam\steamapps\common\BadNorth\BadNorth_Data"
OUT = r"c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/tex"
os.makedirs(OUT, exist_ok=True)

WANT = ["PartTex", "SpriteAtlas", "Sprite", "Swordsman", "Onehanded"]

files = [os.path.join(DATA, "sharedassets1.resource"), os.path.join(DATA, "data.unity3d")]

found = []
for f in files:
    if not os.path.exists(f):
        print("missing", f)
        continue
    env = UnityPy.load(f)
    for obj in env.objects:
        if obj.type.name not in ("Texture2D", "Sprite"):
            continue
        try:
            data = obj.read()
            name = getattr(data, "m_Name", "") or getattr(data, "name", "") or ""
            if not name:
                continue
            if not any(w.lower() in name.lower() for w in WANT):
                continue
            if obj.type.name == "Texture2D":
                img = data.image
                w, h = data.m_Width, data.m_Height
                png = os.path.join(OUT, f"{name}__{w}x{h}.png")
                img.save(png)
                found.append((name, w, h, "texture"))
                print("TEX", name, w, h)
            else:
                # sprite metadata
                print("SPR", name, "rect=", getattr(data, "m_Rect", None))
        except Exception as e:
            print("ERR", f, obj.type.name, e)

print("\n=== found", len(found), "textures ===")
