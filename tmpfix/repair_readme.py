# -*- coding: utf-8 -*-
"""Repair README.md: remove the stray editor-tool artifact inserted into 十三次进展."""
import io

P = "c:/Users/ABaLaQiYaShanMaiI/OneDrive/Desktop/BadNorthProgram/BadNorthEnemy-main/BadNorthBlackSpearman1.3/README.md"
with io.open(P, "r", encoding="utf-8") as f:
    t = f.read()

MARKER = "顺带修诊断 bug"
idx = t.find(MARKER)
print("marker idx:", idx)
if idx >= 0:
    # find the corrupted region: from this line's start to the clean "  - 诊断：一次性" line start
    line_start = t.rfind("\n", 0, idx) + 1
    diag_line = "  - 诊断：一次性 `身体网格详细`"
    diag_idx = t.find(diag_line, idx)
    print("diag_idx:", diag_idx)
    if diag_idx >= 0:
        # keep the current (first) occurrence of the mislabel note, drop everything between it and the diag line
        clean_note = ("  - 顺带修诊断 bug：`IsPartBrightExact` 原在像素清零后调用（UV 解码变 cell(0,0)），"
                      "把真亮采样误标成\"光晕\"（上一轮 `UVHalo=0 仍报光晕=6` 的来源）→ 先判纯亮再清零。"
                      "实际模式0+UVErase 每帧约擦 6px 亮采样，远不足以消白框——白框主体是部件贴图亮剑，须模式2。\n")
        # find where the note's current text ends (at the next line start after the corrupted run)
        nl_after = t.find("\n", idx)
        # find the second occurrence (the correct one) and remove it + the corrupted block before diag_line
        second = t.find(MARKER, idx + 10)
        print("second marker idx:", second)
        # strategy: remove the corrupted run (from the first newline after first note to diag_idx)
        t = t[:line_start] + clean_note + t[diag_idx:]
        with io.open(P, "w", encoding="utf-8") as f:
            f.write(t)
        print("repaired")
    else:
        print("diag line not found")
else:
    print("marker not found")
