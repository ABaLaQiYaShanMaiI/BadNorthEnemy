# -*- coding: utf-8 -*-
"""第四十一轮：BlackSpearmanVisual.cs 头盔变动频率统计——补 2 处：字段 + LateUpdate 计时(unscaledDeltaTime)。"""
import io, sys

P = r"c:\Users\ABaLaQiYaShanMaiI\OneDrive\Desktop\BadNorthProgram\BadNorthEnemy-main\BadNorthBlackSpearman1.3\BlackSpearmanVisual.cs"

with io.open(P, "r", encoding="utf-8", newline="") as f:
    t = f.read()

# ---------- 编辑1：字段 ----------
old1 = ("        int _headSampleCount;           // 头部采样计数（限 150 次 ≈ 2.5s，防刷屏）\n"
        "        float _prevHeadBright = -1f;    // 上一次头部亮度（跳变检测）\n"
        "        int _prevHeadSX = -1, _prevHeadSY = -1;   // ★ 第四十轮：上一次头部屏坐标（几何跳动检测）")
new1 = ("        int _headSampleCount;           // 头部采样计数（限 600 次 ≈ 10s，留出\"正常→空格慢放→恢复\"对比窗口）\n"
        "        float _prevHeadBright = -1f;    // 上一次头部亮度（跳变检测）\n"
        "        int _prevHeadSX = -1, _prevHeadSY = -1;   // ★ 第四十轮：上一次头部屏坐标（几何跳动检测）\n"
        "        // ★ 第四十一轮（用户建议）：头盔变动频率统计——每窗口(30采样≈0.5真实秒)输出亮度/位移跳变率(按真实秒)+timeScale，\n"
        "        //   供\"空格慢放\"对比：跳变率(次/真实秒)不随 ts 下降 = 每渲染帧级变动(渲染层问题)；随 ts 同降 = 游戏时间(动画/状态机)驱动。\n"
        "        float _hWinStart;               // 统计窗口起点(真实秒)\n"
        "        int _hWinSamples;               // 窗口内采样数\n"
        "        int _hWinBJumps;                // 窗口内亮度跳变数\n"
        "        int _hWinPJumps;                // 窗口内位移跳变数\n"
        "        float _hWinBrightSum;           // 窗口内亮度累加（平均亮度）")

# ---------- 编辑2：LateUpdate 计时 ----------
old2 = ("            if (_headSampleCount < 150)\n"
        "            {\n"
        "                bool onMain = _agent != null && _agent.navPos.valid && _agent.navPos.onMain;\n"
        "                if (onMain)\n"
        "                {\n"
        "                    _headSampleTimer -= Time.deltaTime;\n"
        "                    if (_headSampleTimer <= 0f)\n"
        "                    {\n"
        "                        _headSampleTimer = 0.016f;\n"
        "                        StartCoroutine(SampleHeadBrightness());\n"
        "                    }\n"
        "                }\n"
        "            }")
new2 = ("            if (_headSampleCount < 600)\n"
        "            {\n"
        "                bool onMain = _agent != null && _agent.navPos.valid && _agent.navPos.onMain;\n"
        "                if (onMain)\n"
        "                {\n"
        "                    _headSampleTimer -= Time.unscaledDeltaTime;   // ★ 第四十一轮：改用未缩放真实时间——慢放(空格)时采样\n"
        "                    if (_headSampleTimer <= 0f)                   //   仍按真实帧率，频率对比才有效（Time.deltaTime 会被 timeScale 拖慢）\n"
        "                    {\n"
        "                        _headSampleTimer = 0.016f;\n"
        "                        StartCoroutine(SampleHeadBrightness());\n"
        "                    }\n"
        "                }\n"
        "            }")

n1 = t.count(old1)
n2 = t.count(old2)
if n1 != 1:
    sys.exit("编辑1 匹配数=%d（期望1）" % n1)
if n2 != 1:
    sys.exit("编辑2 匹配数=%d（期望1）" % n2)
t = t.replace(old1, new1).replace(old2, new2)

with io.open(P, "w", encoding="utf-8", newline="") as f:
    f.write(t)
print("OK: 两处替换完成")
