using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵黑色外观：每帧在 LateUpdate 强制顶点色 B 通道。
    /// 原版 `Agent.aliveAndGrounded.OnUpdate += UpdateColor`（Agent.cs:418）每帧把
    /// `spriteAnimator.color.b = 1 - healthFraction` —— B 通道是游戏的"受击白闪"通道。旧版周期写 B=0.01
    /// （60/30 帧间隔）打不过它：受击时 b→1 → 身体闪白/暖色 = 用户所见"颜色不对劲+闪烁"。
    /// 现在每帧在 LateUpdate 重写（渲染前最后阶段，必然赢）：
    /// - 身体（SpriteAnimator）：b 强制 0.02 → 恒黑，抑制受击白闪；
    /// - 长矛/阴影（普通 BatchedSprite）：b 强制 1.0 → 保持原色（长矛可见、阴影正常）。
    /// 黑色本体由 SwordRemover 烘进部件贴图克隆（整体压暗 ×0.15），双保险。
    /// </summary>
    public class BlackSpearmanVisual : MonoBehaviour
    {
        Agent _agent;
        int _frames;
        static int _unitIdCounter;       // 单位编号计数器（区分多个黑矛兵的采样日志）
        int _unitId;                     // 本黑矛兵编号
        // 长矛手部压暗 —— 玩家长矛精灵（Spear_0/1/2）自带两只暖肤的手（128x18 精灵 y8-12），
        // 克隆纹理并压暗全部暖肤像素（r-b>25 且 r>130），手变黑、矛杆保持原色。静态缓存按纹理实例 ID 共享。
        static readonly Dictionary<int, Texture2D> _spearTexCache = new Dictionary<int, Texture2D>();

        public void ApplyOnce(Agent agent)
        {
            _agent = agent;
            _unitId = ++_unitIdCounter;
            Recolor();
        }

        void LateUpdate()
        {
            Recolor();
            // 屏幕像素回读诊断——**只在登岛(onMain)后采样**（敌舰上身体 alpha=0 透明，采到的是海水；
            // 上轮 20 次采样全浪费在登岛前）。每 0.5s 采样第一只登岛黑矛兵的胸口实际渲染色，直到 30 次。
            // 归入 VerboseDumps 门控（该采样点常被英文兵/背景遮挡误报"整条偏亮"，默认不再刷）
            if (BSLog.VerboseDumps && _pixelSampleCount < 30)
            {
                bool onMain = _agent != null && _agent.navPos.valid && _agent.navPos.onMain;
                if (onMain)
                {
                    _pixelSampleTimer -= Time.deltaTime;
                    if (_pixelSampleTimer <= 0f)
                    {
                        _pixelSampleTimer = 0.5f;
                        StartCoroutine(SampleRenderedPixel());
                    }
                }
            }

            // 高频头部亮度采样——每 0.016s（≈每帧）采盔顶实际渲染色，
            // 记录亮度时间序列 + 相邻跳变（>0.25）+ 屏坐标跳动（>5px），把"头部闪白/抽搐"量化成可判读的曲线。
            // 采样间隔 0.016s（抓 60Hz 高频闪动），限 600 次 ≈ 10s。
            // 由 cfg Diag.HeadTrace 门控（默认开；逐帧行只在 VerboseDumps 时打）
            if (BSLog.HeadTrace && _headSampleCount < 600)
            {
                bool onMain = _agent != null && _agent.navPos.valid && _agent.navPos.onMain;
                if (onMain)
                {
                    _headSampleTimer -= Time.unscaledDeltaTime;   // 改用未缩放真实时间——慢放(空格)时采样
                    if (_headSampleTimer <= 0f)                   // 仍按真实帧率，频率对比才有效（Time.deltaTime 会被 timeScale 拖慢）
                    {
                        _headSampleTimer = 0.016f;
                        StartCoroutine(SampleHeadBrightness());
                    }
                }
            }
        }

        float _pixelSampleTimer;
        static int _pixelSampleCount;   // 限前 30 次采样（防刷屏）
        bool _pixelNoCamLogged;         // 找不到相机时打印一次原因
        float _headSampleTimer;         // 头部高频采样计时器
        int _headSampleCount;           // 头部采样计数（限 600 次 ≈ 10s，留出"正常→空格慢放→恢复"对比窗口）
        float _prevHeadBright = -1f;    // 上一次头部亮度（跳变检测）
        int _prevHeadSX = -1, _prevHeadSY = -1;   // 上一次头部屏坐标（几何跳动检测）
        // 头盔变动频率统计——每窗口(30采样≈0.5真实秒)输出亮度/位移跳变率(按真实秒)+timeScale，
        // 供"空格慢放"对比：跳变率(次/真实秒)不随 ts 下降 = 每渲染帧级变动(渲染层问题)；随 ts 同降 = 游戏时间(动画/状态机)驱动。
        float _hWinStart;               // 统计窗口起点(真实秒)
        int _hWinSamples;               // 窗口内采样数
        int _hWinBJumps;                // 窗口内亮度跳变数
        int _hWinPJumps;                // 窗口内位移跳变数
        float _hWinBrightSum;           // 窗口内亮度累加（平均亮度）
        // 暗/亮像素计数 + 暗↔亮交替计数
        int _hWinDark;                  // 窗口内"暗盔"判定次数
        int _hWinBrightN;               // 窗口内"亮盔/露背景"判定次数
        int _hWinAlt;                   // 窗口内 暗↔亮 交替次数（≥3 = 闪白实锤）
        bool _prevHeadDarkValid;
        bool _prevHeadDark;
        // 垂直条带最暗点统计——顶点全零=正常 billboard，之前"网格塌缩"误报。
        // 条带最暗点 ≤0.35 = 头部某高度有黑盔（单点 0.78 落在透明 padding 的恒亮是采样落空）；
        // 整条 >0.55 = 头部真亮。
        float _hWinDarkestSum;          // 窗口内"最暗采样点"亮度累加
        int _hWinDarkestN;              // 窗口内有效最暗点采样数
        float _lastDarkestPt = -1f;     // 最近一次最暗点亮度
        string _lastDarkestRel = "?";   // 最近一次最暗点相对高度

        /// <summary>帧末读屏幕：采样黑矛兵脚→盔顶垂直条 5 点（各 3x3），输出最暗/最亮点亮度。
        /// 最暗≤0.35=黑身正常渲染（✓）；整条>0.35=被英文兵遮挡或身体透明（✗=闪白回归信号）。
        /// 相机兜底：游戏主相机可能未标记 MainCamera → Camera.main 为 null 时退回 allCameras[0]；越界钳制；跳过原因打印一次。</summary>
        IEnumerator SampleRenderedPixel()
        {
            yield return new WaitForEndOfFrame();
            try
            {
                if (_agent == null) yield break;
                Camera cam = Camera.main;
                if (ReferenceEquals(cam, null))
                {
                    var cams = Camera.allCameras;
                    if (cams != null && cams.Length > 0) cam = cams[0];
                }
                if (ReferenceEquals(cam, null))
                {
                    if (!_pixelNoCamLogged) { _pixelNoCamLogged = true; BSLog.Warn("[像素采样] 未找到任何相机（Camera.allCameras 空），跳过"); }
                    yield break;
                }
                Vector3 body = _agent.chestPos;
                Vector3 sp = cam.WorldToScreenPoint(body);
                int px = Mathf.Clamp(Mathf.RoundToInt(sp.x), 3, Screen.width - 4);
                int py = Mathf.Clamp(Mathf.RoundToInt(sp.y), 3, Screen.height - 4);
                if (sp.z <= 0f || sp.x < 0 || sp.y < 0 || sp.x > Screen.width || sp.y > Screen.height)
                {
                    // 离屏：不计采样，等下次（不打印，防刷屏）
                    yield break;
                }
                // 旧版只采胸口 1 点 5x5——登岛后黑矛兵常被我方英文兵遮挡，
                // 或 chestPos 与渲染精灵错位 → 45 次全采到英文兵/地形（亮色误报）。现改为**脚→盔顶垂直条 5 点**：
                // - 最暗点 ≤0.35 → 黑身正在渲染（✓）；整条 >0.35 → 被遮挡或身体透明（✗=闪白回归信号）；
                // - 最亮点=盔顶/肩甲 → 辅助量化头盔灰可见性（×0.8 后期望 0.15~0.33，黑躯 0.10~0.18）。
                var tex = new Texture2D(3, 3, TextureFormat.RGBA32, false);
                float[] offs = { -0.30f, -0.10f, 0.10f, 0.30f, 0.45f };
                float minB = 99f, maxB = -1f;
                string minAt = "", maxAt = "";
                for (int k = 0; k < offs.Length; k++)
                {
                    Vector3 p = body + Vector3.up * offs[k];
                    Vector3 s2 = cam.WorldToScreenPoint(p);
                    if (s2.z <= 0f) continue;
                    int sx = Mathf.Clamp(Mathf.RoundToInt(s2.x), 1, Screen.width - 2);
                    int sy = Mathf.Clamp(Mathf.RoundToInt(s2.y), 1, Screen.height - 2);
                    tex.ReadPixels(new Rect(sx - 1, sy - 1, 3, 3), 0, 0);
                    tex.Apply();
                    Color[] cs = tex.GetPixels();
                    float sum = 0f;
                    for (int i = 0; i < cs.Length; i++) sum += cs[i].r + cs[i].g + cs[i].b;
                    float br = sum / (cs.Length * 3f);
                    string tag = (offs[k] >= 0f ? "+" : "") + offs[k].ToString("0.00");
                    if (br < minB) { minB = br; minAt = tag; }
                    if (br > maxB) { maxB = br; maxAt = tag; }
                }
                UnityEngine.Object.Destroy(tex);
                string flag = minB > 0.35f ? " ⚠️整条偏亮(遮挡或身体透明!)" : "";
                _pixelSampleCount++;
                BSLog.Info("[像素采样] 胸屏幕(" + px + "," + py + ") 垂直条" + offs.Length + "点 最暗=" +
                    minB.ToString("F3") + "@" + minAt + " 最亮=" + maxB.ToString("F3") + "@" + maxAt +
                    " → 黑身=" + (minB <= 0.35f ? "✓正常渲染" : "✗未采到黑身") + flag);
            }
            catch { }
        }

        /// <summary>
        /// 头盔采样点改用 **Sprite 真实盔顶**（sprite.bounds 顶部 86% 高度、水平 3 点 × 3x3），
        /// 旧 chestPos+up*0.45 实测常落空采到背景（恒 0.62~0.97），永远测不到头盔。
        /// 窗口统计新增：暗/亮判定 + 暗↔亮交替计数（≥3=闪白实锤：头盔在"黑盔↔透明露背景"间逐帧切换）。
        /// 逐帧 [头部采样] 行由 BSLog.VerboseDumps 门控（默认关，防刷屏）。</summary>
        IEnumerator SampleHeadBrightness()
        {
            yield return new WaitForEndOfFrame();
            try
            {
                if (_agent == null) yield break;
                Camera cam = Camera.main;
                if (ReferenceEquals(cam, null))
                {
                    var cams = Camera.allCameras;
                    if (cams != null && cams.Length > 0) cam = cams[0];
                }
                if (ReferenceEquals(cam, null)) yield break;

                // 头盔条带：SpriteAnimator 局部包围盒底→顶，取 5 个相对高度做垂直采样
                // 0.45=脸/盔下沿，0.60/0.72=盔带，0.84/0.95=盔顶/头顶透明 padding
                // 旧单点 0.78 若落在头顶透明 padding 会恒亮误报；条带最暗点可区分"头盔真亮"与"采样落空"。
                // 实测已确认"身体渲染黑"（死亡窗口扫描 暗身px=1760），但条带仍恒亮
                // → 条带点全部落在身体上方透明 padding/地面 = 采样落空。**改用窗口扫描最暗点**（同死亡扫描思路）。
                Vector3 head = _agent.chestPos + Vector3.up * 0.45f;   // 兜底（找不到 sprite 时）
                bool usedSprite = false;
                Transform saT = null;
                Bounds sb = default(Bounds);
                try
                {
                    var sa = _agent.GetComponentInChildren<SpriteAnimator>();
                    if (sa != null && sa.sprite != null)
                    {
                        Bounds b = sa.sprite.bounds;
                        if (b.size.sqrMagnitude > 0.0001f)
                        {
                            sb = b; saT = sa.transform; usedSprite = true;
                        }
                    }
                }
                catch { }

                Vector3 WorldAt(float rel)
                {
                    if (saT != null)
                        return saT.TransformPoint(new Vector3(0f, sb.min.y + sb.size.y * rel, (sb.min.z + sb.max.z) * 0.5f));
                    return _agent.chestPos + Vector3.up * (0.45f * rel / 0.78f);
                }

                // 参考点（0.78）用于屏坐标跳动检测
                Vector3 sp = cam.WorldToScreenPoint(WorldAt(0.78f));
                if (sp.z <= 0f || sp.x < 3 || sp.y < 3 || sp.x > Screen.width - 4 || sp.y > Screen.height - 4)
                    yield break;
                int sx = Mathf.Clamp(Mathf.RoundToInt(sp.x), 3, Screen.width - 4);
                int sy = Mathf.Clamp(Mathf.RoundToInt(sp.y), 3, Screen.height - 4);

                // 窗口扫描（60x90，以参考点为中心向上偏 15px 罩住头/肩）——
                // 找出窗口内 3x3 平均最暗的点 + 统计暗像素数。身体渲染黑 → 窗口必有大量暗像素。
                int ww = 60, wh = 90;
                int wx0 = Mathf.Clamp(sx - ww / 2, 0, Screen.width - ww);
                int wy0 = Mathf.Clamp(sy - wh / 2 - 15, 0, Screen.height - wh);
                var tex = new Texture2D(ww, wh, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(wx0, wy0, ww, wh), 0, 0);
                tex.Apply();
                Color[] cpx = tex.GetPixels();
                UnityEngine.Object.Destroy(tex);
                float sum = 0f; int n = 0, darkN = 0, brightN = 0;
                float darkestPt = 99f; string darkestRel = "?";
                // 3x3 平均扫一遍（步长 1 太慢，用步长 2）
                for (int y = 1; y < wh - 2; y += 2)
                {
                    for (int x = 1; x < ww - 2; x += 2)
                    {
                        float s3 = 0f;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                Color c = cpx[(y + dy) * ww + (x + dx)];
                                s3 += c.r + c.g + c.b;
                            }
                        float b3 = s3 / 27f;
                        sum += b3; n++;
                        if (b3 <= 0.35f) darkN++;
                        else if (b3 > 0.55f) brightN++;
                        if (b3 < darkestPt)
                        {
                            darkestPt = b3;
                            darkestRel = "(" + (wx0 + x - sx) + "," + (wy0 + y - sy) + ")px";
                        }
                    }
                }
                float avg = n > 0 ? sum / n : 0f;
                // isDark：窗口内暗像素占比高 = 身体/头确实在渲染黑
                bool isDark = darkN >= n * 0.10f;      // ≥10% 像素 ≤0.35 = 有大块黑（身体）
                bool isBright = darkN == 0 && brightN >= n * 0.5f;   // 完全无暗 = 身体没在窗口/不黑
                _lastDarkestPt = darkestPt; _lastDarkestRel = darkestRel;
                if (_hWinStart <= 0f) _hWinStart = Time.realtimeSinceStartup;
                _hWinSamples++;
                _hWinBrightSum += avg;
                if (isDark) _hWinDark++;        // 窗口有大块黑 = 黑身可见
                if (isBright) _hWinBrightN++;
                _hWinDarkestSum += darkestPt; _hWinDarkestN++;
                bool headDark = isDark;
                if (_prevHeadDarkValid && headDark != _prevHeadDark) _hWinAlt++;
                _prevHeadDark = headDark; _prevHeadDarkValid = true;
                string jump = "";
                bool bJump = false;
                if (_prevHeadBright >= 0f && Mathf.Abs(avg - _prevHeadBright) > 0.25f)
                {
                    bJump = true;
                    jump = " ⚠️跳变" + (avg > _prevHeadBright ? "↑变亮" : "↓变暗");
                }
                string frame = "?";
                try
                {
                    var sa = _agent.GetComponentInChildren<SpriteAnimator>();
                    if (sa != null && sa.sprite != null) frame = sa.sprite.name;
                }
                catch { }
                _headSampleCount++;
                string posJump = "";
                bool pJump = false;
                if (_prevHeadSX >= 0 && (Mathf.Abs(sx - _prevHeadSX) > 5 || Mathf.Abs(sy - _prevHeadSY) > 5))
                {
                    pJump = true;
                    posJump = " ⚠️跳动";
                }
                // 逐帧行默认关（刷屏源之一）；开 BSLog.VerboseDumps 才打
                if (BSLog.VerboseDumps)
                {
                    BSLog.Info("[头部采样#" + _unitId + "] 亮度=" + avg.ToString("F2") + jump + posJump +
                        " 帧=" + frame + " 屏=(" + sx + "," + sy + ") ts=" + Time.timeScale.ToString("F2") +
                        (usedSprite ? " 点=sprite盔顶" : " 点=chest+up(兜底)"));
                }
                _prevHeadBright = avg;
                _prevHeadSX = sx; _prevHeadSY = sy;
                if (bJump) _hWinBJumps++;
                if (pJump) _hWinPJumps++;
                if (_hWinSamples >= 30)
                {
                    float realSec = Mathf.Max(0.0001f, Time.realtimeSinceStartup - _hWinStart);
                    float avgWin = _hWinBrightSum / _hWinSamples;
                    float darkestWin = _hWinDarkestN > 0 ? _hWinDarkestSum / _hWinDarkestN : 99f;
                    string verdict = "";
                    if (_hWinAlt >= 3)
                        verdict = " ⚠️黑身↔背景交替=" + _hWinAlt + "次(≥3)=闪白实锤";
                    else if (_hWinDark > 0)
                        verdict = " ✓窗口内有大块黑(暗窗=" + _hWinDark + "/" + _hWinSamples + ") 黑身可见 最暗=" + darkestWin.ToString("F2") + "@" + _lastDarkestRel;
                    else if (darkestWin > 0.35f)
                        verdict = " ⚠️窗口完全无暗(最暗=" + darkestWin.ToString("F2") + ") 身体不在窗口或未渲染黑";
                    else
                        verdict = " ✓黑盔稳定";
                    BSLog.Info("[头盔统计#" + _unitId + "] 真实秒=" + realSec.ToString("F2") +
                        " ts=" + Time.timeScale.ToString("F2") + " 采样=" + _hWinSamples +
                        " 暗=" + _hWinDark + " 亮=" + _hWinBrightN + " 交替=" + _hWinAlt +
                        " 最暗点=" + darkestWin.ToString("F2") + "@h" + _lastDarkestRel +
                        " 亮度跳变=" + _hWinBJumps + "(" + (_hWinBJumps / realSec).ToString("F1") + "/真实秒)" +
                        " 位移跳变=" + _hWinPJumps + "(" + (_hWinPJumps / realSec).ToString("F1") + "/真实秒)" +
                        " 平均亮度=" + avgWin.ToString("F2") +
                        " 采样点=" + (usedSprite ? "sprite条带" : "chest+up") +
                        verdict +
                        " → 慢放对比: 频率随ts降=动画/游戏时间驱动; 频率不降=每帧渲染层变动");
                    _hWinStart = 0f; _hWinSamples = 0; _hWinBJumps = 0; _hWinPJumps = 0; _hWinBrightSum = 0f;
                    _hWinDark = 0; _hWinBrightN = 0; _hWinAlt = 0; _hWinDarkestSum = 0f; _hWinDarkestN = 0;
                }
            }
            catch { }
        }

        void Recolor()
        {
            if (_agent == null) return;
            try
            {
                var all = _agent.GetComponentsInChildren<BatchedSprite>(true);
                if (all == null) return;
                if (_frames < 5)
                {
                    _frames++;
                    BSLog.Info("[VISUAL] 染色帧#" + _frames + ": " + all.Length + " 个 BatchedSprite");
                    foreach (var bs in all)
                    {
                        if (bs == null) continue;
                        Color c = bs.color;
                        BSLog.Info("[VISUAL]   · " + bs.name + ": color=(" +
                            c.r.ToString("F2") + "," + c.g.ToString("F2") + "," + c.b.ToString("F2") + "," + c.a.ToString("F2") + ")");
                    }
                }
                foreach (var bs in all)
                {
                    if (bs == null) continue;
                    try
                    {
                        var c = bs.color;
                        bool isBody = bs is SpriteAnimator;      // 身体 SpriteAnimator 的 R/G 存 UV 编码
                        if (isBody)
                        {
                            // 强制身体顶点 **alpha=1**（不透明）——
                            // `Body.SetGrass` 只在"grass != shadow.a"时写 sprite 颜色，黑矛兵 shadow.a=0 时
                            // 若 grass 判定短路，身体 alpha 恒 0 → AlphaToMask 整块丢弃 → 身体透明、露出背景
                            // （实测 [像素采样] 战斗中亮度 0.5~0.9 = 采到身后岛屿/海水 = 用户所见"闪白"）。
                            // B 恒 0.02（黑色由部件贴图分区压暗烘进，B 只做受击白闪抑制）。
                            if (Mathf.Abs(c.b - 0.02f) > 0.02f || Mathf.Abs(c.a - 1f) > 0.02f)
                                bs.color = new Color(c.r, c.g, 0.02f, 1f);
                        }
                        else
                        {
                            // 长矛/阴影：B=1 保持原色；长矛额外压暗手部纹理
                            if (Mathf.Abs(c.b - 1f) > 0.02f)
                                bs.color = new Color(c.r, c.g, 1f, c.a);
                            if (bs.name != null && bs.name.IndexOf("Spear", StringComparison.Ordinal) >= 0)
                                DarkenSpearHand(bs);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>压暗长矛精灵纹理里的暖肤像素（持矛的手）→ 黑手；矛杆/矛头保持原色。
        /// 克隆共享纹理后，通过渲染器材质块 _MainTex 换克隆（与 SwordRemover 同款思路）。</summary>
        static void DarkenSpearHand(BatchedSprite bs)
        {
            try
            {
                Sprite sp = GetSpriteOf(bs);
                if (sp == null || sp.texture == null) return;
                var tex = sp.texture as Texture2D;
                if (tex == null) return;
                int key = tex.GetInstanceID();
                Texture2D clone;
                if (!_spearTexCache.TryGetValue(key, out clone))
                {
                    clone = CloneTex(tex);
                    if (clone == null) return;
                    Color32[] px = clone.GetPixels32();
                    int w = clone.width, h = clone.height;
                    int n = 0, nSkin = 0, nBlue = 0;
                    long blueSum = 0; int blueN = 0, blueMax = 0;   // 蓝手压暗后 max 通道亮度（黑躯≤40 同量级=已融合）
                    for (int i = 0; i < px.Length; i++)
                    {
                        Color32 c = px[i];
                        if (c.a <= 8) continue;
                        // 我方 Pikeman 长矛精灵的手部除了暖肤，还有一条蓝色竖带
                        // （实测 R 41-87 / G 81-119 / B 99-123，x~17-31 y6-13）。暖肤阈值压不到蓝色 → 手仍蓝。
                        // 现在暖肤 OR 蓝系都重度压暗 → 整只手变黑，与黑身融合后"脱开/橡皮筋"不可见。
                        // 分暖肤/蓝系计数 + 蓝手压暗后亮度统计（量化确认蓝手已黑，无需再靠肉眼）。
                        bool isSkin = c.r - c.b > 25 && c.r > 130;
                        bool isBlue = c.b > 80 && c.b > c.r && c.b > c.g;
                        if (isSkin || isBlue)
                        {
                            byte nr = (byte)(c.r * 0.15f), ng = (byte)(c.g * 0.15f), nb = (byte)(c.b * 0.15f);
                            px[i] = new Color32(nr, ng, nb, c.a);
                            n++;
                            if (isSkin && !isBlue) nSkin++;
                            if (isBlue)
                            {
                                nBlue++;
                                int bMax = Mathf.Max(nr, Mathf.Max(ng, nb));
                                blueSum += bMax; blueN++;
                                if (bMax > blueMax) blueMax = bMax;
                            }
                        }
                    }
                    if (n > 0) { clone.SetPixels32(px); clone.Apply(); }
                    _spearTexCache[key] = clone;
                    BSLog.Info("[渲染] 长矛手部压暗 " + n + "px（" + sp.name + "）暖肤=" + nSkin + " 蓝系=" + nBlue +
                        " → 压暗后max通道亮度：蓝手avg=" + (blueN > 0 ? blueSum / blueN : 0) + "(max=" + blueMax +
                        ") 黑躯≤40 同量级 → 手融入黑身、矛杆保持原色");
                }
                // 换材质块 _MainTex 为克隆（所有渲染器）
                var mrs = bs.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs == null) return;
                var block = new MaterialPropertyBlock();
                for (int i = 0; i < mrs.Length; i++)
                {
                    if (mrs[i] == null) continue;
                    try
                    {
                        mrs[i].GetPropertyBlock(block);
                        block.SetTexture("_MainTex", clone);
                        mrs[i].SetPropertyBlock(block);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static Sprite GetSpriteOf(BatchedSprite bs)
        {
            var sa = bs as SpriteAnimator;
            if (sa != null) return sa.sprite;
            try
            {
                var f = typeof(BatchedSprite).GetField("_spriteRenderer",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (ReferenceEquals(f, null)) return null;
                var sr = f.GetValue(bs) as SpriteRenderer;
                return sr != null ? sr.sprite : null;
            }
            catch { return null; }
        }

        static Texture2D CloneTex(Texture2D src)
        {
            try
            {
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
                var old = RenderTexture.active;
                RenderTexture.active = rt;
                Graphics.Blit(src, rt);
                var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                tex.Apply();
                RenderTexture.active = old;
                RenderTexture.ReleaseTemporary(rt);
                return tex;
            }
            catch { return null; }
        }
    }
}
