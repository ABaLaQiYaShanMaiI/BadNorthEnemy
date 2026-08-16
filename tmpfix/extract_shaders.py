# -*- coding: utf-8 -*-
"""Extract shaders + materials from Bad North to find the body composite shader."""
import os, UnityPy

DATA = r"D:\Steam\steamapps\common\BadNorth\BadNorth_Data"
files = [os.path.join(DATA, "sharedassets1.resource"), os.path.join(DATA, "data.unity3d")]

for f in files:
    env = UnityPy.load(f)
    for obj in env.objects:
        if obj.type.name not in ("Shader", "Material"):
            continue
        try:
            data = obj.read()
        except Exception as e:
            continue
        if obj.type.name == "Shader":
            name = getattr(data, "m_ParsedForm", None)
            nm = getattr(data, "name", None)
            try:
                txt = data.export()
            except Exception:
                txt = b""
            if isinstance(txt, bytes):
                txts = txt.decode("utf-8", "replace")
            else:
                txts = txt
            print(f"SHADER: {nm}  export_len={len(txts) if txts else 0}")
            if txts and "_PartTex" in txts:
                print("   >>> CONTAINS _PartTex <<<")
                open("c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/tmpfix/shader_parttex.txt", "w", encoding="utf-8", errors="replace").write(txts)
            elif txts and "_MainTex" in txts:
                print("   (has _MainTex)")
        else:
            shader = getattr(data, "m_Shader", None)
            sn = getattr(shader, "name", None) if shader is not None else None
            print(f"MATERIAL: {getattr(data,'name',None)} shader={sn}")
