using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 去剑组件：原版 Viking 的"剑"烘焙位置随基底不同——
    ///   旧基底 Viking_Sword = OnehandedXXXX 动画帧里的暗红剑刃（R>70,G<40,B<20）+ sprite2 PartTex_Sword 亮银剑柄；
    ///   新基底 Viking_SwordShield = SwordsmanXXXX 帧 + sprite2 PartTex_SwordShield（剑+盾 2D 部件）。
    /// 原理（第十七轮用户回退）：部件贴图(sprite2) → 换为"亮银剑身擦透明"的克隆（模式2：亮银 + bbox 内接壤像素
    /// 擦透明，剑区预期挖洞、身体其余保留——用户明确回退"擦除"方案，不要"改身体色"的黑色剑影）；帧内剑刃 →
    /// 材质块 _MainTex 换去剑克隆（帧级红暗 + UV 亮采样擦除照常执行）。
    /// ⚠️ 安全阀：单帧擦除占比超阈值判定误擦并跳过；帧纹理是共享图集，只擦当前帧 rect。
    /// </summary>
    public class SwordRemover : MonoBehaviour
    {
        // 剑红像素颜色签名（来自 extracted_assets 逐帧统计：R90~125, G8~15, B0）。
        // ⚠️ 运行时 ETC2 图集颜色与提取 PNG 有偏差，阈值过宽会误擦身体暗红衣物——
        // 因此收紧阈值 + 加"安全阀"：单帧擦除占比超过 SafetyEraseRatio 即判定误擦，跳过该帧。
        const int SwordRMin = 70;
        const int SwordGMax = 40;
        const int SwordBMax = 20;
        const int OuterBandPx = 6;           // 剑柄/护手纵向带：剑刃 bbox 上下各扩展 6 像素（只擦剑的外侧，绝不碰身体）
        const int OuterMarginPx = 2;         // 外侧水平回退：剑柄与剑刃右/左缘重叠 ≤2px 的部分也一并擦
        const int HiltBandPx = -1;           // ⚠️ 已禁用（2026-08-15）：帧内剑柄与身体重叠，擦剑柄必伤身体。待部件贴图(sprite2)方案移除剑柄。
        const float OuterMinOffsetPx = 5f;   // 剑心与帧心偏移 <5px（剑居中）→ 不擦外侧（避免误擦居中持剑的身体）
        const float SafetyEraseRatio = 0.2f;
        // ★ sprite2(PartTex) 亮银剑区阈值 —— 运行时探针实测：剑=亮银(159~189,144~186,137~189)、身体=暗(33,26,24)。
        //   亮银=中性灰（|r-b|、|g-b| 都 < 容差）→ 排除暖色皮肤与暗色衣物；这是剑柄残留"改部件贴图"方案落点。
        const int SilverRMin = 110;
        const int SilverGMin = 100;
        const int SilverBMin = 90;
        const int SilverNeutralTol = 60;     // 中性灰容差：|r-b|、|g-b| 都 <60 才算"金属银"
        const int SilverAlphaMin = 128;      // 只擦实心像素，忽略半透明边缘
        const float Sprite2SafetyRatio = 0.35f; // ★ 第十七轮回退：恢复“擦透明”方案，ETC2 增亮身体像素被擦会挖洞 → 收紧到 35% 防误擦（>35% 亮判定贴图异常）
        const int SwordBrightMin = 150;      // "纯亮"阈值：剑刃金属（r,g,b>150 中性亮色）。亮银擦除只用它，绝不碰暗色/肤色身体。

        static readonly Dictionary<int, Texture2D> _textureCache = new Dictionary<int, Texture2D>();  // 源纹理 → 去剑克隆
        static readonly Dictionary<int, Sprite> _frameCache = new Dictionary<int, Sprite>();          // 源帧精灵 → 去剑精灵
        static readonly Dictionary<int, Sprite> _sprite2Cache = new Dictionary<int, Sprite>();        // 源 sprite2 → 去剑 sprite2
        static readonly HashSet<int> _skippedFrames = new HashSet<int>();                            // 因安全阀跳过的帧（不再重试）
        static readonly HashSet<int> _erasedRects = new HashSet<int>();                              // 已擦除的帧 rect（每 rect 只擦一次）
        static int _colorDiagDone;                                                                   // 帧颜色直方图诊断（限制次数）
        static bool _sprite2DiagDone;                                                                // sprite2 单元 ASCII 诊断（全局仅一次）
        static readonly HashSet<int> _preErasedTex = new HashSet<int>();                            // 已预擦除的源纹理（按源纹理实例 ID；消除动画播放时剑闪回）
        static int _blockDiagCount;                                                                  // 材质块修复详细转储次数（限前 2 只，避免刷屏）

        /// <summary>sprite2(部件贴图)处理模式（由 Plugin.RemoveSwordSprite2Mode 配置）：
        /// 0=保留原部件贴图、只靠帧擦除去剑（帧擦会挖洞/残留剑柄，弃用）；
        /// 1=整块清空部件单元（旧方案，会致身体白框，勿用）；
        /// 2=★第十七轮（用户回退）亮银剑身擦透明：亮银(剑刃+2D盾)与 bbox 内接壤像素擦透明、身体其余保留
        ///（剑区预期挖洞；剑柄带改身体色——第十八轮用户指定“剑柄颜色与黑矛兵身躯颜色一致”，GripBand&gt;0）。</summary>
        public static int Sprite2Mode;

        // ★★ UV 感知亮采样擦除（第十二轮，白框根治）：
        // 运行时 ETC2 压缩的 PartTex_SwordShield 单元比离线亮（亮像素 bbox 从 y2~50 膨胀到 y0~105），
        // 部分"身体帧像素"（G 高、不满足红暗阈值）解码 UV 后采样到亮银部件像素 → 渲染成白框，旧红暗擦除抓不到。
        // 解法：擦除任何"解码 UV 采样到亮(r,g,b>150)部件像素"的帧像素——白框像素无论帧色如何都被擦，暗身体不受影响。
        public static bool UVErase = true;   // 配置 RemoveSwordFrameUVErase
        public static int UVHalo = 0;        // 配置 RemoveSwordFrameUVHalo：亮像素光晕(部件像素距离)，吃持剑的手/护手
        public static int GripFloodPx = 2;   // 配置 RemoveSwordSprite2GripBand：第十八轮默认 2（用户指定“剑柄颜色与黑矛兵身躯颜色一致”）；>0 启用“暗灰剑柄/亮灰护手改身体暗色(33,26,24)”，0=不改

        // ★ 部件单元像素缓存（静态共享）：供帧擦除按"UV→部件采样"判定白框像素（全黑矛兵共用一份）
        static bool _partReady;             // 部件缓存已就绪
        static Texture2D _partTexClone;     // sprite2 部件贴图克隆（CloneTexture 一次）
        static Color32[] _partPx;           // 克隆像素数组
        static int _partW, _partH;          // 克隆尺寸
        static Rect _partRect;              // 部件单元 rect（sprite2.textureRect，UV 解码基准）
        static int _partKey;                // 缓存键 = 部件纹理实例 ID（sprite2 换了就重建）
        static bool[] _partEraseMask;       // 部件单元内"亮 or 距亮≤UVHalo"掩码（cell 局部坐标）
        static bool[] _partBrightMask;      // 部件单元内"纯亮"掩码（不含光晕，安全阀用）
        static int _brightCount;            // 单元内亮像素数（诊断）
        static bool _uvDiagDone;            // 一次性 UV 采样诊断（首帧输出）

        Agent _agent;
        SpriteAnimator _sa;
        Sprite _blankSprite2;   // 新基底整块清空/亮银擦除后的 sprite2（烘焙重置时可重应用）
        bool _eraseEnabled;
        bool _dumped;      // 运行时诊断已输出（处理第一帧时）
        bool _partKeepLogged;   // 模式0：保留原部件贴图的体检日志已输出（避免每帧刷屏）
        bool _blocksRepaired;   // 身体材质块修复+详细转储已输出（每个黑矛兵一次）

        public void Setup(Agent agent, bool eraseEnabled)
        {
            _agent = agent;
            _eraseEnabled = eraseEnabled;
            if (_agent == null) { Destroy(this); return; }
            // 找到身体 SpriteAnimator：旧基底 Viking_Sword 用 Onehanded 帧，新基底 Viking_SwordShield 用 Swordsman 帧
            var sas = agent.GetComponentsInChildren<SpriteAnimator>(true);
            for (int i = 0; i < sas.Length; i++)
            {
                var sa = sas[i];
                if (sa == null) continue;
                if (IsSwordFrameSprite(sa.sprite)) { _sa = sa; break; }
            }
            if (_sa == null)
            {
                BSLog.Warn("[去剑] 未找到剑帧 SpriteAnimator（Onehanded/Swordsman 帧都没有），组件停用");
                Destroy(this);
            }
            else
            {
                // ★ 部件单元缓存：帧擦除按"解码 UV→部件采样"判定白框像素，必须先有部件贴图
                EnsurePartCache(_sa.sprite2);
            }
        }

        void LateUpdate()
        {
            if (_sa == null) return;

            var cur = _sa.sprite;
            if (cur != null && cur.texture != null && IsSwordFrameSprite(cur))
            {
                // ★ 运行时诊断：处理第一帧时输出身体像素 ASCII 图 + sprite2 + 网格状态（无论开关，用于校准剑签名）
                if (!_dumped) { _dumped = true; DumpBodyRuntime(cur); }

                // 1) 主动画帧：当前帧是 Onehanded/Swordsman 帧 → 只把材质块的 _MainTex 换成去剑克隆纹理
                //    ★ 关键修复：绝不交换 bSprite/sprite/网格 —— 实测 bSprite 交换会破坏身体渲染（躯干透明），
                //    尽管顶点色/UV 都正常。网格 UV 本来就指向图集单元；克隆纹理与图集同尺寸，
                //    让 _MainTex 直接采样克隆的同一单元即可渲染"去剑帧"，完全避开 sprite 对象替换。
                //    ★ 第十七轮（用户回退）：不再有"路线A 跳过帧擦"分支——帧级擦透明恢复（与第十四轮一致）。
                if (_eraseEnabled)
                {
                    Texture2D erasedTex = EnsureErasedTexture(cur);
                    if (erasedTex != null)
                    {
                        // 先按原逻辑更新 SpriteAnimator 自己的 block（保留 _PartTex 不丢）
                        _sa.block.SetTexture("_MainTex", erasedTex);
                        if (_sa.sprite2 != null && _sa.sprite2.texture != null)
                            _sa.block.SetTexture("_PartTex", _sa.sprite2.texture);
                        _sa.ComittBlock();
                        // ★ 第十三轮：把去剑克隆 + 部件贴图强制写入全部身体 MeshRenderer 的材质块——
                        //   空块渲染器（_MainTex/_PartTex 为 null → 着色器默认白色）会渲染成白框/白板，
                        //   且游戏每帧会用原图集覆盖 block，必须每帧重写（我们组件最后 Add，LateUpdate 最后执行）。
                        RepairBodyMaterialBlocks(erasedTex);
                    }
                }
            }

            // 2) sprite2（部件贴图）：旧基底 PartTex_Sword → 亮银剑柄擦除；新基底 PartTex_SwordShield → 按 Sprite2Mode：
            //    0=保留原部件（只靠帧擦除去剑，身体最完整，避免白框）；1=整块清空（旧方案，会致身体白框）；
            //    2=只擦亮银剑身（去剑+保留身体折中）。可重入：烘焙若重置 sprite2 会再次处理。
            if (_eraseEnabled && _sa.sprite2 != null && _sa.sprite2.texture != null)
            {
                bool swordShieldPart =
                    (_sa.sprite2.name != null && _sa.sprite2.name.IndexOf("SwordShield", StringComparison.Ordinal) >= 0) ||
                    (_sa.sprite2.texture != null && _sa.sprite2.texture.name != null &&
                     _sa.sprite2.texture.name.IndexOf("SwordShield", StringComparison.Ordinal) >= 0);
                if (swordShieldPart)
                {
                    if (Sprite2Mode == 1)
                    {
                        if (!(_blankSprite2 != null && ReferenceEquals(_sa.sprite2, _blankSprite2)))
                        {
                            _blankSprite2 = GetBlankSprite2(_sa.sprite2);
                            if (_blankSprite2 != null) _sa.SetSprite2(_blankSprite2);
                        }
                    }
                    else if (Sprite2Mode == 2)
                    {
                        if (!(_blankSprite2 != null && ReferenceEquals(_sa.sprite2, _blankSprite2)))
                        {
                            _blankSprite2 = GetBrightErasedSprite2(_sa.sprite2);
                            if (_blankSprite2 != null) _sa.SetSprite2(_blankSprite2);
                        }
                    }
                    else
                    {
                        if (!_partKeepLogged)
                        {
                            _partKeepLogged = true;
                            LogPartCellStats(_sa.sprite2);
                        }
                    }
                }
                else
                {
                    if (!(_blankSprite2 != null && ReferenceEquals(_sa.sprite2, _blankSprite2)))
                    {
                        Sprite erased2 = GetErasedSprite2(_sa.sprite2);
                        if (erased2 != null && !ReferenceEquals(_sa.sprite2, erased2))
                            _sa.SetSprite2(erased2);   // 同步更新 part 纹理 + RG 图集编码
                    }
                }
            }
        }

        /// <summary>
        /// 运行时诊断（你要求的"日志暴露问题"）：把身体当前帧画成 ASCII 像素图打进日志，
        /// 并输出 sprite2（部件贴图/身体外观来源）、color、网格顶点色/UV 状态。
        /// 图例：S=红暗窄阈值(90/25/10)  s=红暗宽阈值(70/40/20)  #=亮色  .=不透明  空格=透明
        /// </summary>
        void DumpBodyRuntime(Sprite frame)
        {
            try
            {
                var srcTex = frame.texture as Texture2D;
                if (srcTex == null) return;
                Rect r = frame.textureRect;
                BSLog.Diag("\n===== 去剑·运行时诊断 =====");
                BSLog.Diag("sprite=" + frame.name + " tex=" + srcTex.name + " rect=" + r +
                    " texSize=" + srcTex.width + "x" + srcTex.height);
                BSLog.Diag("sprite2=" + (_sa.sprite2 != null ? _sa.sprite2.name + "/" +
                    (_sa.sprite2.texture != null ? _sa.sprite2.texture.name : "null") : "null") +
                    (_sa.sprite2 != null ? " rect=" + _sa.sprite2.textureRect.ToString() : ""));
                BSLog.Diag("color=" + _sa.color.ToString("F3") + " " + ReadMeshState(_sa));

                Texture2D clone = GetSharedClone(srcTex);
                if (clone == null) { BSLog.Diag("克隆失败，无法出图"); BSLog.Diag("===== 去剑·运行时诊断结束 ====="); return; }
                Color32[] px = clone.GetPixels32();
                int w = clone.width;
                int x0 = Mathf.FloorToInt(r.xMin), y0 = Mathf.FloorToInt(r.yMin);
                int x1 = Mathf.CeilToInt(r.xMax), y1 = Mathf.CeilToInt(r.yMax);
                BSLog.Diag("— 帧像素图（隔行采样；S=红暗窄阈值(90/25/10) s=红暗宽阈值(70/40/20) #=亮色 .=不透明 空格=透明）—");
                for (int y = y1 - 1; y >= y0; y -= 2)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int x = x0; x < x1; x++)
                    {
                        Color32 c = px[y * w + x];
                        char ch = ' ';
                        if (c.a > 8)
                        {
                            if (c.r > 90 && c.g < 25 && c.b < 10) ch = 'S';
                            else if (c.r > 70 && c.g < 40 && c.b < 20) ch = 's';
                            else if (c.r > 150 && c.g > 150 && c.b > 150) ch = '#';
                            else ch = '.';
                        }
                        sb.Append(ch);
                    }
                    BSLog.Diag("  " + sb.ToString());
                }

                // ★★ 第十二轮：UV 亮采样分析（白框定位）——统计本帧里"解码 UV 采样到亮部件像素"的不透明像素
                //    这些像素渲染出来是白/亮色，且不满足红暗阈值（G 高），是模式0下白框的直接来源。
                //    图例：B=亮采样(白框像素,将被 UVErase 擦除) S=红暗剑刃 .=身体 空格=透明
                EnsurePartCache(_sa.sprite2);
                if (_partReady && !_uvDiagDone)
                {
                    _uvDiagDone = true;
                    int uvb = 0, redD = 0, opaqueN = 0;
                    BSLog.Diag("— UV亮采样图（B=解码UV采样到亮部件像素=白框源，S=红暗剑刃，.=暗身体）—");
                    for (int y = y1 - 1; y >= y0; y -= 2)
                    {
                        var sb = new System.Text.StringBuilder();
                        for (int x = x0; x < x1; x++)
                        {
                            if (x < 0 || x >= w) { sb.Append(' '); continue; }
                            Color32 c = px[y * w + x];
                            char ch = ' ';
                            if (c.a > 8)
                            {
                                opaqueN++;
                                int i = y * w + x;
                                if (c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax) { ch = 'S'; redD++; }
                                else if (IsPartErase(px, i, w)) { ch = 'B'; uvb++; }
                                else ch = '.';
                            }
                            sb.Append(ch);
                        }
                        BSLog.Diag("  " + sb.ToString());
                    }
                    BSLog.Diag("[去剑] 亮采样分析: 不透明=" + opaqueN + " 红暗=" + redD +
                        " 亮采样(白框源)=" + uvb + " ← " + (uvb > 0 ? "旧擦除抓不到这些，UVErase 将把它们擦掉" : "本帧无白框像素") +
                        (UVHalo > 0 ? "（光晕=" + UVHalo + "）" : ""));
                }

                // ★★ 网格子对象材质块状态：验证去剑克隆 _MainTex 是否覆盖全部 MeshRenderer（含 _MIRROR_ON 变体）
                DumpMeshBlocks(_sa);

                // ★ 一次性输出 sprite2（PartTex_Sword 外观）单元 ASCII：验证剑柄/剑身是否也画在外观里，
                //   为"若帧擦除后仍有残留 → 改 sprite2"的兜底方案提供坐标。
                if (!_sprite2DiagDone && _sa.sprite2 != null && _sa.sprite2.texture != null)
                {
                    _sprite2DiagDone = true;
                    DumpSprite2Cell(_sa.sprite2);
                }
                // ★ PartTex 采样探针：用顶点色解码（SetSprite2 编码：g=rect.x/256单位、r=rect.y/256单位）
                //   采样 _PartTex，验证"剑=亮银、身体=暗"是否成立 → 剑柄残留"改部件贴图"正解的前置依据
                try
                {
                    var s2 = _sa.sprite2;
                    if (s2 != null && s2.texture != null && s2.texture is Texture2D)
                    {
                        var ptex = (Texture2D)s2.texture;
                        Color32[] ppx = ptex.GetPixels32();
                        int pw = ptex.width, ph = ptex.height;
                        Rect pr = s2.textureRect;
                        int pnx = Mathf.Max(1, ptex.width / 256);
                        int pny = Mathf.Max(1, ptex.height / 256);
                        Color vc = _sa.color;
                        int orx = Mathf.RoundToInt(vc.g * 255f * pnx);   // 部件 rect 原点 x
                        int ory = Mathf.RoundToInt(vc.r * 255f * pny);   // 部件 rect 原点 y
                        // 帧内取样：剑 = 红暗 bbox 中心；身体 = 帧左中部
                        int sx = x0 + 2, sy = y0 + 8; Color32 cs = px[sy * w + sx];
                        int rx0, ry0, rx1, ry1; bool oR;
                        if (GetSwordBounds(px, w, clone.height, r, out rx0, out ry0, out rx1, out ry1, out oR))
                        {
                            sx = (rx0 + rx1) / 2; sy = (ry0 + ry1) / 2; cs = px[sy * w + sx];
                        }
                        int bx = x0 + 2, by = Mathf.Min(y1 - 1, y0 + Mathf.RoundToInt(r.height * 0.7f));
                        Color32 cb = px[by * w + bx];
                        // 剑柄探针：剑刃 bbox 垂直中线，从剑刃基部朝身体侧找第一个"非红不透明"像素（护手/柄）
                        int hx = -1, hy = -1; Color32 ch = new Color32(0, 0, 0, 0); bool gotHilt = false;
                        if (rx1 >= 0)
                        {
                            int midY = (ry0 + ry1) / 2;
                            int dir = oR ? -1 : 1;   // 剑偏右→柄在基部左侧；剑偏左→柄在右侧
                            for (int x = (oR ? rx0 : rx1); x >= x0 - 6 && x <= x1 + 6; x += dir)
                            {
                                if (x < 0 || x >= w) break;
                                Color32 cc = px[midY * w + x];
                                if (cc.a > 8 && !(cc.r > SwordRMin && cc.g < SwordGMax && cc.b < SwordBMax))
                                { ch = cc; hx = x; hy = midY; gotHilt = true; break; }
                            }
                        }
                        BSLog.Diag("[去剑·PartTex探针] sprite2=" + s2.name + " 顶点色=(" + vc.r.ToString("F3") + "," + vc.g.ToString("F3") +
                            ") 解码原点=(" + orx + "," + ory + ") rect=" + pr +
                            (gotHilt ? " 剑柄帧色=(" + ch.r + "," + ch.g + ")@" + hx + "," + hy : " 剑柄未找到"));
                        for (int cand = 0; cand < 4; cand++)
                        {
                            Color32 pS = SamplePartTex(ppx, pw, ph, pr, cs, cand);
                            Color32 pB = SamplePartTex(ppx, pw, ph, pr, cb, cand);
                            string hS = "";
                            if (gotHilt)
                            {
                                Color32 pH = SamplePartTex(ppx, pw, ph, pr, ch, cand);
                                hS = "  剑柄→Part=(" + pH.r + "," + pH.g + "," + pH.b + "," + pH.a + ")";
                            }
                            BSLog.Diag("[去剑·PartTex探针] cand" + cand + " 剑帧色=(" + cs.r + "," + cs.g + ")→Part=(" +
                                pS.r + "," + pS.g + "," + pS.b + "," + pS.a + ")  身体帧色=(" + cb.r + "," + cb.g + ")→Part=(" +
                                pB.r + "," + pB.g + "," + pB.b + "," + pB.a + ")" + hS);
                        }
                    }
                }
                catch (Exception e) { BSLog.Warn("[去剑] PartTex 探针异常: " + e); }
                BSLog.Diag("===== 去剑·运行时诊断结束 =====");
            }
            catch (Exception e) { BSLog.Warn("[去剑] 运行时诊断异常: " + e); }
        }

        /// <summary>按 4 种候选 UV 解码采样部件贴图：剑柄残留"改部件贴图"方案的前置探针。
        /// cand0: uv=(r,g) cand1: uv=(g,r) cand2: uv=(r,1-g) cand3: uv=(1-r,1-g)，映射进 sprite2 rect。</summary>
        static Color32 SamplePartTex(Color32[] ppx, int pw, int ph, Rect pr, Color32 frameC, int cand)
        {
            if (ppx == null) return new Color32(0, 0, 0, 0);
            float u, v;
            if (cand == 0) { u = frameC.r / 255f; v = frameC.g / 255f; }
            else if (cand == 1) { u = frameC.g / 255f; v = frameC.r / 255f; }
            else if (cand == 2) { u = frameC.r / 255f; v = 1f - frameC.g / 255f; }
            else { u = 1f - frameC.r / 255f; v = 1f - frameC.g / 255f; }
            int px = Mathf.Clamp((int)(pr.x + u * pr.width), 0, pw - 1);
            int py = Mathf.Clamp((int)(pr.y + v * pr.height), 0, ph - 1);
            return ppx[py * pw + px];
        }

        /// <summary>把 sprite2（身体外观/PartTex_Sword）单元画成 ASCII 图打进日志（全局仅一次）。
        /// 用途：验证剑身/剑柄是否也烘焙在外观图里——若帧擦除后仍有残留，这里是兜底方案（改 sprite2）。</summary>
        static void DumpSprite2Cell(Sprite s2)
        {
            try
            {
                var st = s2.texture as Texture2D;
                if (st == null) return;
                Texture2D clone = CloneTexture(st);
                if (clone == null) return;
                Rect r = s2.textureRect;
                Color32[] px = clone.GetPixels32();
                if (px == null) { UnityEngine.Object.Destroy(clone); return; }
                int w = clone.width;
                int x0 = Mathf.FloorToInt(r.xMin), y0 = Mathf.FloorToInt(r.yMin);
                int x1 = Mathf.CeilToInt(r.xMax), y1 = Mathf.CeilToInt(r.yMax);
                BSLog.Diag("— sprite2 单元像素图（" + s2.name + " rect=" + r + "，S=红窄 s=红宽 #=亮 .=不透明）—");
                for (int y = y1 - 1; y >= y0; y -= 2)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int x = x0; x < x1; x++)
                    {
                        Color32 c = px[y * w + x];
                        char ch = ' ';
                        if (c.a > 8)
                        {
                            if (c.r > 90 && c.g < 25 && c.b < 10) ch = 'S';
                            else if (c.r > 70 && c.g < 40 && c.b < 20) ch = 's';
                            else if (c.r > 150 && c.g > 150 && c.b > 150) ch = '#';
                            else ch = '.';
                        }
                        sb.Append(ch);
                    }
                    BSLog.Diag("  " + sb.ToString());
                }
                BSLog.Diag("— sprite2 单元结束 —");
                UnityEngine.Object.Destroy(clone);
            }
            catch (Exception e) { BSLog.Warn("[去剑] sprite2 单元诊断异常: " + e); }
        }

        /// <summary>用反射读取私有 mesh 的顶点色/UV（检测 bSprite 交换后渲染状态是否完好）。
        /// ⚠️ 注意：Mono 2.0 没有 FieldInfo.op_Equality，null 判断必须用 ReferenceEquals。</summary>
        static string ReadMeshState(SpriteAnimator sa)
        {
            try
            {
                var f = typeof(BatchedSprite).GetField("mesh",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (ReferenceEquals(f, null)) return "mesh字段=null";
                var mesh = f.GetValue(sa) as Mesh;
                if (mesh == null) return "mesh=null";
                var cs = mesh.colors32;
                var uv = mesh.uv;
                string c0 = (cs != null && cs.Length > 0) ?
                    "(" + cs[0].r + "," + cs[0].g + "," + cs[0].b + "," + cs[0].a + ")" : "无顶点色";
                string u0 = (uv != null && uv.Length > 0) ? uv[0].ToString("F3") : "无UV";
                return "meshColors0=" + c0 + " meshUV0=" + u0;
            }
            catch (Exception e) { return "mesh读取失败:" + e.Message; }
        }

        /// <summary>转储 SpriteAnimator 下所有 MeshRenderer 子对象的材质块纹理：
        /// 验证去剑克隆 _MainTex 是否覆盖全部网格（含 _MIRROR_ON 变体，镜像朝左时若用旧纹理就会露出剑/白框）。</summary>
        static void DumpMeshBlocks(SpriteAnimator sa)
        {
            try
            {
                if (sa == null) return;
                var mrs = sa.GetComponentsInChildren<MeshRenderer>(true);
                BSLog.Diag("— 网格子对象材质块（MeshRenderer x" + mrs.Length + "）—");
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    var block = new MaterialPropertyBlock();
                    mr.GetPropertyBlock(block);
                    Texture mt = null, pt = null;
                    try { mt = block.GetTexture("_MainTex"); } catch { }
                    try { pt = block.GetTexture("_PartTex"); } catch { }
                    BSLog.Diag("  · " + mr.gameObject.name + " enabled=" + mr.enabled +
                        " sharedMat=" + (mr.sharedMaterial != null ? mr.sharedMaterial.name + "/" + mr.sharedMaterial.shader.name : "null") +
                        " block._MainTex=" + (mt != null ? mt.name : "null") +
                        " block._PartTex=" + (pt != null ? pt.name : "null"));
                }
                BSLog.Diag("— 网格子对象结束 —");
            }
            catch (Exception e) { BSLog.Warn("[去剑] 网格块诊断异常: " + e); }
        }

        /// <summary>★ 第十三轮（白框根治+去剑）：把去剑克隆 + 部件贴图强制写入全部身体 MeshRenderer 的材质块。
        /// 实测现象：身体 SpriteAnimator 下 4 个 MeshRenderer 里前 2 个 block._MainTex/_PartTex 全为 null
        ///（Unlit/ColoredCharacter 对 null 纹理默认采样白色 → 若其网格有几何就渲染成白框/白板）；
        /// 且游戏每帧会用原图集重写 block，去剑克隆必须每帧重写才能上屏。
        /// GetPropertyBlock 会把渲染器当前全部属性拷进新块，我们只覆盖 _MainTex/_PartTex，
        /// 其余属性（_BloodTex/_Mirror 开关等）原样保留。</summary>
        void RepairBodyMaterialBlocks(Texture2D erasedTex)
        {
            try
            {
                var mrs = _sa.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs == null) return;
                Texture partTex = (_sa.sprite2 != null && _sa.sprite2.texture != null) ? _sa.sprite2.texture : null;
                int repaired = 0;
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    // 只修"ColoredCharacter"身体着色器（不动 Shadow/Spear/Shield 的其它着色器）
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    var block = new MaterialPropertyBlock();
                    mr.GetPropertyBlock(block);
                    bool hadMain = false, hadPart = false;
                    try { hadMain = block.GetTexture("_MainTex") != null; } catch { }
                    try { hadPart = block.GetTexture("_PartTex") != null; } catch { }
                    if (erasedTex != null) block.SetTexture("_MainTex", erasedTex);
                    if (partTex != null) block.SetTexture("_PartTex", partTex);
                    mr.SetPropertyBlock(block);
                    if (!hadMain || !hadPart) repaired++;
                }
                if (!_blocksRepaired)
                {
                    _blocksRepaired = true;
                    if (_blockDiagCount < 2)
                    {
                        _blockDiagCount++;
                        BSLog.Info("[去剑] 身体材质块修复: 补纹理渲染器 " + repaired + " 个（空块=白框源，已写入去剑克隆+部件贴图）");
                        DumpBodyBlocksDetailed(erasedTex, partTex);
                    }
                }
            }
            catch (Exception e) { BSLog.Warn("[去剑] 材质块修复异常: " + e); }
        }

        /// <summary>★ 第十三轮详细转储：每个身体 MeshRenderer 的 mesh 顶点数 / isVisible / _MainTex 实例 ID
        ///（是否为去剑克隆 vs 原始图集，终于能区分）——直接回答"白框是否来自空几何渲染器 / 剑是否因原始图集残留"。</summary>
        void DumpBodyBlocksDetailed(Texture2D clone, Texture partTex)
        {
            try
            {
                BSLog.Diag("— 身体网格详细（mesh 顶点数 / isVisible / _MainTex 实例 ID）—");
                var mrs = _sa.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs == null) return;
                for (int j = 0; j < mrs.Length; j++)
                {
                    var mr = mrs[j];
                    if (mr == null) continue;
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    int verts = -1;
                    if (mf != null && mf.sharedMesh != null) verts = mf.sharedMesh.vertexCount;
                    var block = new MaterialPropertyBlock();
                    try { mr.GetPropertyBlock(block); } catch { }
                    Texture mt = null, pt = null;
                    try { mt = block.GetTexture("_MainTex"); } catch { }
                    try { pt = block.GetTexture("_PartTex"); } catch { }
                    string mtInfo = "null";
                    if (mt != null)
                    {
                        bool isClone = clone != null && mt.GetInstanceID() == clone.GetInstanceID();
                        mtInfo = mt.name + " id=" + mt.GetInstanceID() + (isClone ? " ←去剑克隆✓" : " ←⚠非克隆(原始图集!)");
                    }
                    BSLog.Diag("  · " + mr.gameObject.name + " enabled=" + mr.enabled +
                        " verts=" + verts + " isVisible=" + mr.isVisible +
                        " block._MainTex=" + mtInfo +
                        " block._PartTex=" + (pt != null ? pt.name + " id=" + pt.GetInstanceID() : "null"));
                }
                BSLog.Diag("— 身体网格详细结束 —");
            }
            catch (Exception e) { BSLog.Warn("[去剑] 网格详细转储异常: " + e); }
        }

        /// <summary>供 Diagnostics F8 使用：某纹理是否为去剑共享克隆（实例 ID 比对）。</summary>
        public static bool IsSharedClone(Texture t)
        {
            if (t == null) return false;
            var e = _textureCache.GetEnumerator();
            while (e.MoveNext())
            {
                if (e.Current.Value != null && e.Current.Value.GetInstanceID() == t.GetInstanceID()) return true;
            }
            return false;
        }

        // ============ 帧擦除 ============

        /// <summary>新旧基底都认：Viking_Sword=Onehanded 帧，Viking_SwordShield=Swordsman 帧。</summary>
        static bool IsSwordFrameSprite(Sprite s)
        {
            if (s == null) return false;
            if (s.name != null)
            {
                if (s.name.StartsWith("Onehanded", StringComparison.Ordinal)) return true;
                if (s.name.StartsWith("Swordsman", StringComparison.Ordinal)) return true;
            }
            if (s.texture != null && s.texture.name != null)
            {
                if (s.texture.name.StartsWith("Onehanded", StringComparison.Ordinal)) return true;
                if (s.texture.name.StartsWith("Swordsman", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static Sprite GetErasedFrame(Sprite src)
        {
            int key = src.GetInstanceID();
            if (_skippedFrames.Contains(key)) return null;
            Sprite cached;
            if (_frameCache.TryGetValue(key, out cached)) return cached;
            try
            {
                var srcTex = src.texture as Texture2D;
                if (srcTex == null) return null;
                Texture2D erasedTex = GetSharedClone(srcTex);
                if (erasedTex == null) return null;
                Rect rect = src.textureRect;
                // ★ 颜色直方图诊断（每个源纹理一次）：告诉我们运行时真实颜色分布，用于校准剑签名
                if (_colorDiagDone < 8) { _colorDiagDone++; DumpFrameColorStats(src, erasedTex, rect); }

                // ★ 安全阀：先统计 rect 内不透明数与"剑红命中数"，命中占比过高 → 判定误擦身体 → 跳过该帧
                int opaque = CountOpaque(erasedTex, rect);
                int redDark, partUV, haloUV;
                int matched = CountEraseScan(erasedTex, rect, out redDark, out partUV, out haloUV);
                if (opaque > 0 && (redDark > opaque * SafetyEraseRatio ||
                    partUV > opaque * 0.45f || haloUV > opaque * 0.15f))
                {
                    BSLog.Warn("[去剑] 帧 " + src.name + " 命中 " + matched + "/" + opaque +
                        " (>=" + (SafetyEraseRatio * 100f).ToString("F0") + "%)，疑似误擦身体 → 跳过该帧");
                    _skippedFrames.Add(key);
                    return null;
                }
                if (matched > 0)
                {
                    int uv, halo;
                    EraseSwordInFrame(erasedTex, rect, out uv, out halo);
                }
                var spr = Sprite.Create(erasedTex, rect, src.pivot, src.pixelsPerUnit,
                    0, SpriteMeshType.FullRect, src.border);
                spr.name = src.name + "_NoSword";
                _frameCache[key] = spr;
                if (_skippedFrames.Count == 0 && _frameCache.Count <= 4)
                    BSLog.Info("[去剑] 帧 " + src.name + " 去剑成功 擦除=" + matched);
                return spr;
            }
            catch (Exception e) { BSLog.Warn("[去剑] 帧擦除失败: " + e); return null; }
        }

        static Sprite GetErasedSprite2(Sprite s2)
        {
            int key = s2.GetInstanceID();
            Sprite cached;
            if (_sprite2Cache.TryGetValue(key, out cached)) return cached;
            try
            {
                var srcTex = s2.texture as Texture2D;
                if (srcTex == null) return null;
                Texture2D tex = CloneTexture(srcTex);
                int opaque = CountOpaque(tex, s2.textureRect);
                int minX, maxX, minY, maxY;
                int erased = EraseSilverPixels(tex, s2.textureRect, out minX, out maxX, out minY, out maxY);
                // ★ 亮银擦除：PartTex 里剑区签名是"金属亮银"（运行时探针：剑→(159~189,144~186,137~189)、
                //   身体→暗(33,26,24)）。旧 EraseSwordPixels 用红暗阈值对 PartTex 永远命中 0 → sprite2 去剑从未生效。
                // 安全阀：擦除占比过高 → 判定误擦（sprite2 也可能是大图集），丢弃
                //   ★ sprite2 单元以剑为主体（PartTex_Sword），阈值放宽到 35%（帧级仍用 0.2）。
                if (erased == 0 || (opaque > 0 && erased > opaque * Sprite2SafetyRatio))
                {
                    UnityEngine.Object.Destroy(tex);
                    BSLog.Warn("[去剑] sprite2 " + s2.name + " 亮银命中 " + erased + "/" + opaque +
                        (erased == 0 ? "（PartTex 无亮银 → 剑柄可能不是银白，需换几何方案）" : "，疑似误擦 → 跳过"));
                    return null;
                }
                var spr = Sprite.Create(tex, s2.textureRect, s2.pivot, s2.pixelsPerUnit,
                    0, SpriteMeshType.FullRect, s2.border);
                spr.name = s2.name + "_NoSword";
                _sprite2Cache[key] = spr;
                BSLog.Info("[去剑] sprite2 已去剑(亮银) " + s2.name + " 擦除=" + erased + "/" + opaque +
                    " bbox=(" + minX + "," + minY + ")-(" + maxX + "," + maxY + ")");
                return spr;
            }
            catch (Exception e) { BSLog.Warn("[去剑] sprite2 擦除失败: " + e); return null; }
        }

        /// <summary>整块清空 sprite2 部件贴图单元（新基底 PartTex_SwordShield：剑+盾 2D 部件全部不要）。
        /// 黑矛兵视觉 = 身体帧 + 3D 盾网格 + 长矛。空单元精灵按源 sprite2 缓存复用（所有黑矛兵共享一份）。</summary>
        static Sprite GetBlankSprite2(Sprite s2)
        {
            int key = s2.GetInstanceID();
            Sprite cached;
            if (_sprite2Cache.TryGetValue(key, out cached)) return cached;
            try
            {
                var srcTex = s2.texture as Texture2D;
                if (srcTex == null) return null;
                Texture2D tex = CloneTexture(srcTex);
                if (tex == null) return null;
                Rect r = s2.textureRect;
                Color32[] px = tex.GetPixels32();
                int w = tex.width, h = tex.height;
                int x0 = Mathf.FloorToInt(r.xMin), y0 = Mathf.FloorToInt(r.yMin);
                int x1 = Mathf.CeilToInt(r.xMax), y1 = Mathf.CeilToInt(r.yMax);
                int erased = 0;
                for (int y = y0; y < y1; y++)
                {
                    if (y < 0 || y >= h) continue;
                    for (int x = x0; x < x1; x++)
                    {
                        if (x < 0 || x >= w) continue;
                        int i = y * w + x;
                        if (px[i].a > 8) { px[i] = new Color32(0, 0, 0, 0); erased++; }
                    }
                }
                if (erased == 0) { UnityEngine.Object.Destroy(tex); return null; }
                tex.SetPixels32(px); tex.Apply();
                var spr = Sprite.Create(tex, r, s2.pivot, s2.pixelsPerUnit, 0, SpriteMeshType.FullRect, s2.border);
                spr.name = s2.name + "_Blank";
                _sprite2Cache[key] = spr;
                BSLog.Info("[去剑] sprite2 整块清空(剑盾基底) " + s2.name + " 清空=" + erased + "px");
                return spr;
            }
            catch (Exception e) { BSLog.Warn("[去剑] sprite2 清空失败: " + e); return null; }
        }

        /// <summary>★ 第十七轮（用户回退）亮银剑身擦透明（模式2，剑盾基底）：克隆部件贴图，把单元内"纯亮中性灰"像素
        /// （r,g,b&gt;150 的剑刃金属/2D盾）与亮银 bbox 内接壤像素（bbox 面积 ≤ 不透明 45% 才允许）直接擦透明——
        /// 用户回退到"擦除剑刃"（不要"改身体色"的黑色剑影）；剑区预期挖洞（离线验证新增 29/65 洞，可接受）。
        /// 剑柄由 RemoveSwordSprite2GripBand 控制：&gt;0（第十八轮默认 2）时 RecolorGripToBody 把暗灰剑柄/亮灰护手改身体色（用户指定“与黑矛兵身躯颜色一致”）。</summary>
        static Sprite GetBrightErasedSprite2(Sprite s2)
        {
            int key = s2.GetInstanceID();
            Sprite cached;
            if (_sprite2Cache.TryGetValue(key, out cached)) return cached;
            try
            {
                var srcTex = s2.texture as Texture2D;
                if (srcTex == null) return null;
                Texture2D tex = CloneTexture(srcTex);
                if (tex == null) return null;
                Rect r = s2.textureRect;
                Color32[] px = tex.GetPixels32();
                int w = tex.width, h = tex.height;
                int x0 = Mathf.FloorToInt(r.xMin), y0 = Mathf.FloorToInt(r.yMin);
                int x1 = Mathf.CeilToInt(r.xMax), y1 = Mathf.CeilToInt(r.yMax);
                int opaque = 0, bright = 0;
                int minX = 999999, maxX = -1, minY = 999999, maxY = -1;
                // 第一遍：统计不透明数与亮银剑身 bbox
                for (int y = y0; y < y1; y++)
                {
                    if (y < 0 || y >= h) continue;
                    for (int x = x0; x < x1; x++)
                    {
                        if (x < 0 || x >= w) continue;
                        Color32 c = px[y * w + x];
                        if (c.a <= 8) continue;
                        opaque++;
                        if (c.r > SwordBrightMin && c.g > SwordBrightMin && c.b > SwordBrightMin)
                        {
                            bright++;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
                if (bright == 0)
                {
                    UnityEngine.Object.Destroy(tex);
                    BSLog.Info("[去剑] sprite2 剑盾基底无亮银像素 → 保留原部件（模式2退化为模式0）");
                    return null;
                }
                // ★ 第十七轮回退安全阀：恢复 35% —— 现在是"擦透明"而非"改身体色"，ETC2 增亮的身体像素被擦会挖洞，
                //   因此亮银占比 >35% 判定贴图异常（不是正常剑盾单元），退化模式0
                if (opaque > 0 && bright > opaque * Sprite2SafetyRatio)
                {
                    UnityEngine.Object.Destroy(tex);
                    BSLog.Warn("[去剑] sprite2 剑盾基底亮银占比过高 " + bright + "/" + opaque +
                        " (>35%)，疑似贴图异常 → 模式2退化为模式0（保留原部件，靠帧擦除）");
                    return null;
                }
                int bboxArea = (maxX - minX + 1) * (maxY - minY + 1);
                bool recolorBbox = opaque > 0 && bboxArea <= opaque * 0.45f;
                int erased2 = 0;
                // ★ 第十七轮（用户回退）：第二遍——亮银剑身（+bbox 内接壤像素）擦透明（恢复第十四轮行为）。
                //   擦透明让剑区（含与剑重叠的身体像素）变洞（离线验证新增 29/65 洞），但用户明确要求"用回擦除"：
                //   不要黑色剑影、也不要改剑柄颜色。bbox 只罩住剑区（bbox 面积 ≤ 不透明 45% 才允许接壤擦）。
                for (int y = y0; y < y1; y++)
                {
                    if (y < 0 || y >= h) continue;
                    for (int x = x0; x < x1; x++)
                    {
                        if (x < 0 || x >= w) continue;
                        int i = y * w + x;
                        Color32 c = px[i];
                        if (c.a <= 8) continue;
                        bool isBright = c.r > SwordBrightMin && c.g > SwordBrightMin && c.b > SwordBrightMin;
                        bool inBbox = recolorBbox && x >= minX && x <= maxX && y >= minY && y <= maxY;
                        if (isBright || inBbox)
                        {
                            px[i] = new Color32(0, 0, 0, 0);   // 擦透明（挖剑，无黑色剑影）
                            erased2++;
                        }
                    }
                }
                if (erased2 == 0)
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                // ★ 第十八轮：剑柄改身体色（用户指定“剑柄颜色与黑矛兵身躯颜色一致”）——GripFloodPx 默认 2。
                //   RecolorGripToBody 把暗灰剑柄(40≤r≤100,|r-b|≤25)/亮灰护手(100<r<150 中性)改身体暗色(33,26,24)，保留 alpha 不挖洞。
                int gripPainted = 0;
                if (GripFloodPx > 0)
                    gripPainted = RecolorGripToBody(px, w, h, x0, y0, x1, y1);
                tex.SetPixels32(px); tex.Apply();
                var spr = Sprite.Create(tex, r, s2.pivot, s2.pixelsPerUnit, 0, SpriteMeshType.FullRect, s2.border);
                spr.name = s2.name + "_NoSword";
                _sprite2Cache[key] = spr;
                BSLog.Info("[去剑] sprite2 亮银剑身擦除(剑盾基底) " + s2.name + " 擦除=" + erased2 + "/" + opaque +
                    " bbox=(" + minX + "," + minY + ")-(" + maxX + "," + maxY + ") 擦bbox=" + recolorBbox +
                    " 剑柄改色=" + gripPainted + "px（第十八轮：改身体色，预期≈1900px 归入身体色；剑区预期挖洞）");
                // ★ 第十四轮：剑柄残留诊断——擦完后原亮银 bbox 内仍不透明的像素统计 + 定位图（回答"剑柄/手到底剩在哪"）
                DumpGripResidue(px, w, h, minX, maxX, minY, maxY, s2.name);
                return spr;
            }
            catch (Exception e) { BSLog.Warn("[去剑] sprite2 亮银擦除失败: " + e); return null; }
        }

        /// <summary>★ 第十五轮引入 / 第十八轮恢复默认启用：剑柄/护手改色融入身体（部件贴图层，模式2配套）。
        /// 运行时探针实测：剑柄=暗灰(54,50,49)、身体=暗(33,26,24)；着色器以部件贴图为颜色源，
        /// 把单元内"暗灰剑柄(40≤r≤100,|r-b|≤25)+亮灰护手(100<r<150 中性)"改为身体暗色(33,26,24)——
        /// 保留原 alpha（不挖洞不白框），使胸口剑柄带与黑矛兵身躯颜色一致。由 RemoveSwordSprite2GripBand&gt;0 启用（第十八轮默认 2）。</summary>
        static int RecolorGripToBody(Color32[] px, int w, int h, int x0, int y0, int x1, int y1)
        {
            if (px == null) return 0;
            int x0c = Mathf.Clamp(x0, 0, w - 1), x1c = Mathf.Clamp(x1, 0, w);
            int y0c = Mathf.Clamp(y0, 0, h - 1), y1c = Mathf.Clamp(y1, 0, h);
            int painted = 0;
            for (int y = y0c; y < y1c; y++)
            {
                for (int x = x0c; x < x1c; x++)
                {
                    int i = y * w + x;
                    Color32 c = px[i];
                    if (c.a <= 8) continue;                                   // 透明
                    bool darkGray = c.r >= 40 && c.r <= 100 && Mathf.Abs(c.r - c.b) <= 25;
                    bool lightGray = c.r > 100 && c.r < 150 &&
                        Mathf.Abs(c.r - c.b) <= 25 && Mathf.Abs(c.g - c.b) <= 25;
                    if (darkGray || lightGray)
                    {
                        px[i] = new Color32(33, 26, 24, c.a);                 // 改身体暗色，保留 alpha（防挖洞）
                        painted++;
                    }
                }
            }
            return painted;
        }

        /// <summary>★ 第十四轮：剑柄残留诊断——亮银擦除后，原亮银 bbox 内仍不透明的像素统计与定位图。
        /// 分类：g=暗灰(40≤r≤100,|r-b|≤25)疑似剑柄/护手、G=亮灰(100<r<150)疑似护手/盾沿、s=暖色皮肤(持剑手)、
        /// b=身体暗色、#=亮银残(>150)、.=其他不透明。取\"暗灰最多的一行\"（剑柄带）上下各 8 行打印，定位剑柄。
        /// ★ 第十七轮预期（用户选择不改剑柄颜色）：暗灰剑柄/亮灰护手仍 >0（剑柄带保留）；亮银残≈0；皮肤(手)保留。</summary>
        static void DumpGripResidue(Color32[] px, int w, int h,
            int minX, int maxX, int minY, int maxY, string name)
        {
            try
            {
                if (px == null) return;
                int x0c = Mathf.Clamp(minX, 0, w - 1), x1c = Mathf.Clamp(maxX + 1, 0, w);
                int y0c = Mathf.Clamp(minY, 0, h - 1), y1c = Mathf.Clamp(maxY + 1, 0, h);
                int opaque = 0, darkGray = 0, lightGray = 0, warm = 0, bright = 0;
                int rows = y1c - y0c;
                if (rows <= 0) return;
                int[] rowGray = new int[rows];
                for (int y = y0c; y < y1c; y++)
                {
                    int rowIdx = y - y0c;
                    for (int x = x0c; x < x1c; x++)
                    {
                        Color32 c = px[y * w + x];
                        if (c.a <= 8) continue;
                        opaque++;
                        bool isDarkGray = c.r >= 40 && c.r <= 100 && Mathf.Abs(c.r - c.b) <= 25;
                        bool isLightGray = c.r > 100 && c.r < 150 && Mathf.Abs(c.r - c.b) <= 30;
                        if (isDarkGray) { darkGray++; rowGray[rowIdx]++; }
                        else if (isLightGray) lightGray++;
                        else if (c.r - c.b > 30) warm++;
                        else if (c.r > 150 && c.g > 150 && c.b > 150) bright++;
                    }
                }
                BSLog.Diag("[去剑] 剑柄残留诊断 " + name + " bbox内剩余不透明=" + opaque +
                    " 暗灰剑柄=" + darkGray + " 亮灰护手/盾沿=" + lightGray +
                    " 暖色皮肤(手)=" + warm + " 亮银残=" + bright +
                    " ← 暗灰/亮灰>0 = 剑柄/护手仍在，GripBand 需加大或靠盾牌遮挡");
                int best = 0;
                for (int i = 1; i < rows; i++) if (rowGray[i] > rowGray[best]) best = i;
                if (darkGray == 0) return;
                int yTop = Mathf.Max(0, best - 8), yBot = Mathf.Min(rows - 1, best + 8);
                BSLog.Diag("[去剑] 剑柄残留·定位图（g=暗灰 G=亮灰 s=皮肤 b=身体 #=亮银 .=其他 空格=透明，行 " +
                    (y0c + yTop) + "~" + (y0c + yBot) + "）");
                for (int y = yTop; y <= yBot; y++)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int x = x0c; x < x1c; x++)
                    {
                        Color32 c = px[y * w + x];
                        char ch = ' ';
                        if (c.a > 8)
                        {
                            if (c.r >= 40 && c.r <= 100 && Mathf.Abs(c.r - c.b) <= 25) ch = 'g';
                            else if (c.r > 100 && c.r < 150 && Mathf.Abs(c.r - c.b) <= 30) ch = 'G';
                            else if (c.r - c.b > 30) ch = 's';
                            else if (c.r > 150 && c.g > 150 && c.b > 150) ch = '#';
                            else if (c.r < 45 && c.g < 38 && c.b < 33) ch = 'b';
                            else ch = '.';
                        }
                        sb.Append(ch);
                    }
                    BSLog.Diag("  " + sb.ToString());
                }
                BSLog.Diag("[去剑] 剑柄残留·定位图结束");
            }
            catch (Exception e) { BSLog.Warn("[去剑] 剑柄残留诊断异常: " + e); }
        }

        /// <summary>模式0保留部件贴图时的单元体检：不透明数/亮银数/bbox（校准剑区，判断是否需要切模式2）。</summary>
        static void LogPartCellStats(Sprite s2)
        {
            try
            {
                var srcTex = s2.texture as Texture2D;
                if (srcTex == null) return;
                Texture2D tex = CloneTexture(srcTex);
                if (tex == null) return;
                Rect r = s2.textureRect;
                Color32[] px = tex.GetPixels32();
                int w = tex.width, h = tex.height;
                int x0 = Mathf.FloorToInt(r.xMin), y0 = Mathf.FloorToInt(r.yMin);
                int x1 = Mathf.CeilToInt(r.xMax), y1 = Mathf.CeilToInt(r.yMax);
                int opaque = 0, bright = 0;
                int minX = 999999, maxX = -1, minY = 999999, maxY = -1;
                for (int y = y0; y < y1; y++)
                {
                    if (y < 0 || y >= h) continue;
                    for (int x = x0; x < x1; x++)
                    {
                        if (x < 0 || x >= w) continue;
                        Color32 c = px[y * w + x];
                        if (c.a <= 8) continue;
                        opaque++;
                        if (c.r > SwordBrightMin && c.g > SwordBrightMin && c.b > SwordBrightMin)
                        {
                            bright++;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
                UnityEngine.Object.Destroy(tex);
                BSLog.Info("[去剑] sprite2 模式0保留原部件(剑盾基底) " + s2.name + " 不透明=" + opaque +
                    " 亮银(>" + SwordBrightMin + ")=" + bright +
                    (bright > 0 ? " bbox=(" + minX + "," + minY + ")-(" + maxX + "," + maxY + ")" : "") +
                    " ← 若剑仍可见可切 RemoveSwordSprite2Mode=2（只擦亮银剑身）");
            }
            catch (Exception e) { BSLog.Warn("[去剑] 部件体检异常: " + e); }
        }

        /// <summary>共享克隆：同一源纹理只克隆一次（不整图擦除——擦除按帧 rect 单独进行 + 安全阀）。</summary>
        static Texture2D GetSharedClone(Texture2D srcTex)
        {
            int texKey = srcTex.GetInstanceID();
            Texture2D cached;
            if (_textureCache.TryGetValue(texKey, out cached)) return cached;
            Texture2D tex = CloneTexture(srcTex);
            if (tex == null) return null;
            _textureCache[texKey] = tex;
            BSLog.Info("[去剑] 已克隆帧纹理: " + srcTex.name + " " + srcTex.width + "x" + srcTex.height);
            return tex;
        }

        /// <summary>获取共享去剑克隆并擦除当前帧 rect（每个 rect 只擦一次）。
        /// 不创建新 Sprite —— 调用方把返回值直接设为材质块的 _MainTex，规避 bSprite 交换的渲染破坏。
        /// 第十二轮：擦除分两类——红暗剑刃(旧) + 部件亮采样(新，白框像素=解码 UV 采样到亮部件像素)。</summary>
        Texture2D EnsureErasedTexture(Sprite cur)
        {
            try
            {
                var srcTex = cur.texture as Texture2D;
                if (srcTex == null) return null;
                // ★ 部件单元缓存：只有拿到 sprite2 部件贴图才能按 UV 判定白框像素（sprite2 未就绪就重试）
                if (_sa != null) EnsurePartCache(_sa.sprite2);
                Texture2D tex = GetSharedClone(srcTex);
                if (tex == null) return null;
                // ★ 首次：一次性预擦除图集里全部 Onehanded/Swordsman 帧 → 动画播放时无"首帧剑闪回"
                //   第十九轮修复根因：必须传**源纹理**（sprite.texture 与源纹理 ReferenceEquals）。
                //   旧代码误传共享克隆（GetSharedClone 产物）→ 任何 sprite 的 texture 都不等于克隆 → 帧列表恒空 →
                //   预擦除被"空结果=完成"提前标记，全部帧退回运行时逐帧擦除 = 每帧首显剑闪回（用户所见"美术素材的闪亮"）。
                int srcKey = srcTex.GetInstanceID();
                if (!_preErasedTex.Contains(srcKey) && PreEraseAllOnehanded(srcTex))
                    _preErasedTex.Add(srcKey);
                int key = cur.GetInstanceID();
                if (_erasedRects.Contains(key)) return tex;
                _erasedRects.Add(key);
                int opaque = CountOpaque(tex, cur.textureRect);
                int redDark, partUV, haloUV;
                int matched = CountEraseScan(tex, cur.textureRect, out redDark, out partUV, out haloUV);
                // ★ 安全阀（三指标）：红暗命中 >20%（身体暗红衣物的误擦信号）、亮采样 >45%（白框像素=采样到亮部件，
                //   可放宽；超过说明部件缓存异常/贴图被替换）、光晕擦除 >15%（光晕吃手，但过量说明误擦身体）
                if (opaque > 0 && (redDark > opaque * SafetyEraseRatio ||
                    partUV > opaque * 0.45f || haloUV > opaque * 0.15f))
                {
                    BSLog.Warn("[去剑] 帧 " + cur.name + " 擦除疑似误擦 → 跳过: 红暗=" + redDark +
                        " 亮采样=" + partUV + " 光晕=" + haloUV + " /不透明=" + opaque +
                        (partUV > opaque * 0.45f ? "（亮采样过高，可能部件贴图被替换/缓存异常）" : "") +
                        (haloUV > opaque * 0.15f ? "（光晕过量，UVHalo 太大或部件掩码误扩）" : ""));
                    return null;
                }
                if (matched > 0)
                {
                    int uvB, haloB;
                    EraseSwordInFrame(tex, cur.textureRect, out uvB, out haloB);
                    BSLog.Info("[去剑] 帧 " + cur.name + " 去剑成功 擦除=" + matched +
                        "（红暗=" + redDark + " 亮采样=" + uvB + " 光晕=" + haloB + "）");
                }
                return tex;
            }
            catch (Exception e) { BSLog.Warn("[去剑] EnsureErasedTexture 异常: " + e); return null; }
        }

        // ============ UV 感知亮采样擦除（第十二轮：白框像素 = 解码 UV 采样到亮部件像素） ============

        /// <summary>初始化部件单元像素缓存（静态共享，按部件纹理实例 ID 缓存；sprite2 换了会重建）。
        /// 帧擦除前必须调用——只有拿到部件贴图才能按 UV 判定"该帧像素渲染出来是不是白框"。</summary>
        static void EnsurePartCache(Sprite s2)
        {
            try
            {
                if (s2 == null || s2.texture == null || !(s2.texture is Texture2D)) return;
                var st = (Texture2D)s2.texture;
                int key = st.GetInstanceID();
                if (_partReady && key == _partKey) return;
                _partKey = key;
                Texture2D clone = CloneTexture(st);
                if (clone == null) return;
                if (_partTexClone != null) UnityEngine.Object.Destroy(_partTexClone);
                _partTexClone = clone;
                _partPx = clone.GetPixels32();
                if (_partPx == null) return;
                _partW = clone.width;
                _partH = clone.height;
                _partRect = s2.textureRect;
                _brightCount = 0;
                BuildPartEraseMask();
                _partReady = true;
                BSLog.Info("[去剑] UV部件缓存就绪: " + s2.name + "/" + st.name + " " + _partW + "x" + _partH +
                    " rect=" + _partRect + " 亮(>" + SwordBrightMin + ")=" + _brightCount +
                    (UVHalo > 0 ? " 光晕=" + UVHalo + "px" : ""));
            }
            catch (Exception e) { BSLog.Warn("[去剑] UV部件缓存失败: " + e); }
        }

        /// <summary>构建部件单元内"亮 or 距亮≤UVHalo"擦除掩码（cell 局部坐标 0..w-1 / 0..h-1）。</summary>
        static void BuildPartEraseMask()
        {
            int cw = Mathf.FloorToInt(_partRect.width), ch = Mathf.FloorToInt(_partRect.height);
            if (cw <= 0 || ch <= 0) return;
            int x0 = Mathf.FloorToInt(_partRect.xMin), y0 = Mathf.FloorToInt(_partRect.yMin);
            _partEraseMask = new bool[cw * ch];
            _partBrightMask = new bool[cw * ch];
            List<int> brightIdx = null;
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int ax = x0 + x, ay = y0 + y;
                    if (ax < 0 || ay < 0 || ax >= _partW || ay >= _partH) continue;
                    Color32 c = _partPx[ay * _partW + ax];
                    if (c.a > 128 && c.r > SwordBrightMin && c.g > SwordBrightMin && c.b > SwordBrightMin)
                    {
                        _partEraseMask[y * cw + x] = true;
                        _partBrightMask[y * cw + x] = true;
                        _brightCount++;
                        if (brightIdx == null) brightIdx = new List<int>();
                        brightIdx.Add(y * cw + x);
                    }
                }
            }
            // 光晕：距亮像素 Chebyshev ≤ UVHalo 的部件像素也标记（吃持剑的手/护手/剑刃边缘）
            if (UVHalo > 0 && brightIdx != null)
            {
                for (int i = 0; i < brightIdx.Count; i++)
                {
                    int bi = brightIdx[i];
                    int bx = bi % cw, by = bi / cw;
                    for (int dy = -UVHalo; dy <= UVHalo; dy++)
                    {
                        int ny = by + dy;
                        if (ny < 0 || ny >= ch) continue;
                        for (int dx = -UVHalo; dx <= UVHalo; dx++)
                        {
                            int nx = bx + dx;
                            if (nx < 0 || nx >= cw) continue;
                            _partEraseMask[ny * cw + nx] = true;
                        }
                    }
                }
            }
        }

        /// <summary>帧像素是否命中部件擦除掩码：解码 UV(R/255,G/255) → 部件单元坐标 → 查掩码（含光晕）。</summary>
        static bool IsPartErase(Color32[] framePx, int i, int w)
        {
            if (!_partReady || _partEraseMask == null) return false;
            int cw = Mathf.FloorToInt(_partRect.width), ch = Mathf.FloorToInt(_partRect.height);
            if (cw <= 0 || ch <= 0) return false;
            int cx = (int)((framePx[i].r / 255f) * cw);
            int cy = (int)((framePx[i].g / 255f) * ch);
            if (cx < 0 || cy < 0 || cx >= cw || cy >= ch) return false;
            return _partEraseMask[cy * cw + cx];
        }

        /// <summary>只查"纯亮"掩码（不含光晕）——安全阀区分亮采样与光晕擦除用。</summary>
        static bool IsPartBrightExact(Color32[] framePx, int i, int w)
        {
            if (!_partReady || _partBrightMask == null) return false;
            int cw = Mathf.FloorToInt(_partRect.width), ch = Mathf.FloorToInt(_partRect.height);
            if (cw <= 0 || ch <= 0) return false;
            int cx = (int)((framePx[i].r / 255f) * cw);
            int cy = (int)((framePx[i].g / 255f) * ch);
            if (cx < 0 || cy < 0 || cx >= cw || cy >= ch) return false;
            return _partBrightMask[cy * cw + cx];
        }

        /// <summary>单遍统计帧 rect 内：红暗剑刃 / 部件亮采样 / 光晕擦除 三类命中数（安全阀用）。</summary>
        static int CountEraseScan(Texture2D tex, Rect rect, out int redDark, out int partUV, out int haloUV)
        {
            redDark = 0; partUV = 0; haloUV = 0;
            if (tex == null) return 0;
            Color32[] px = tex.GetPixels32();
            if (px == null) return 0;
            int w = tex.width, h = tex.height;
            int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
            int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
            for (int y = y0; y < y1; y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = x0; x < x1; x++)
                {
                    if (x < 0 || x >= w) continue;
                    int i = y * w + x;
                    Color32 c = px[i];
                    if (c.a <= 8) continue;
                    if (c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax) { redDark++; continue; }
                    if (UVErase && IsPartErase(px, i, w))
                    {
                        if (IsPartBrightExact(px, i, w)) partUV++;
                        else haloUV++;
                    }
                }
            }
            return redDark + partUV + haloUV;
        }

        static int CountOpaque(Texture2D tex, Rect rect)
        {
            int n = 0;
            Color32[] px = tex.GetPixels32();
            int w = tex.width;
            int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
            int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
            for (int y = y0; y < y1; y++)
            {
                if (y < 0 || y >= tex.height) continue;
                for (int x = x0; x < x1; x++)
                {
                    if (x < 0 || x >= w) continue;
                    if (px[y * w + x].a > 8) n++;
                }
            }
            return n;
        }

        /// <summary>计算帧内"剑"的红暗 bbox 与外侧面方向（基于已取出的像素数组，避免重复 GetPixels32）。
        /// 返回 false 表示本帧无红暗像素（无剑可擦）。
        /// outerRight=true → 剑偏右（尖端在右）→ 剑柄/护手在剑刃基部左侧；false → 剑偏左 → 剑柄在基部右侧。</summary>
        static bool GetSwordBounds(Color32[] px, int w, int h, Rect rect,
            out int rx0, out int ry0, out int rx1, out int ry1, out bool outerRight)
        {
            rx0 = 999; ry0 = 999; rx1 = -1; ry1 = -1; outerRight = true;
            if (px == null) return false;
            int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
            int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
            for (int y = y0; y < y1; y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = x0; x < x1; x++)
                {
                    if (x < 0 || x >= w) continue;
                    Color32 c = px[y * w + x];
                    if (c.a > 8 && c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax)
                    {
                        if (x < rx0) rx0 = x;
                        if (x > rx1) rx1 = x;
                        if (y < ry0) ry0 = y;
                        if (y > ry1) ry1 = y;
                    }
                }
            }
            if (rx1 < 0) return false;
            float bladeCenter = (rx0 + rx1) * 0.5f;
            float frameCenter = (rect.xMin + rect.xMax) * 0.5f;
            outerRight = bladeCenter >= frameCenter;   // 剑偏右 → 尖端在右 → 剑柄在基部左侧
            return true;
        }

        /// <summary>预擦除：把图集里所有已加载的 OnehandedXXXX/SwordsmanXXXX 帧一次性擦除，
        /// 避免动画播放时每帧"首次显示后才擦"（晚一帧 → 慢放可见的剑闪回）。所有帧共享一个像素数组，只上传一次。
        /// 第十二轮：新基底是 Swordsman 帧（旧代码只擦 Onehanded → 新基底从未预擦，剑闪回仍在）；并加部件亮采样擦除。
        /// 第十九轮：入参改为**源纹理**（匹配 sprite.texture），像素读写用共享克隆（调用方 _MainTex 用的同一份）。
        /// 返回 false = 因部件缓存未就绪推迟（调用方 _preErasedTex 不标记，以便重试）。</summary>
        static bool PreEraseAllOnehanded(Texture2D srcTex)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<Sprite>();
                List<Sprite> frames = null;
                for (int i = 0; i < all.Length; i++)
                {
                    var s = all[i];
                    if (s == null || s.texture == null) continue;
                    if (!ReferenceEquals(s.texture, srcTex)) continue;
                    if (string.IsNullOrEmpty(s.name)) continue;
                    if (!s.name.StartsWith("Onehanded") && !s.name.StartsWith("Swordsman")) continue;
                    if (_erasedRects.Contains(s.GetInstanceID())) continue;
                    if (frames == null) frames = new List<Sprite>();
                    frames.Add(s);
                    // 不在收集时标记 _erasedRects：被安全阀跳过的帧应留给逐帧路径重试（擦成功或无可擦才标记）
                }
                if (frames == null || frames.Count == 0) return true;   // 无待擦帧也算完成
                // ★ 安全护栏：UV 擦除开启但部件缓存未就绪（sprite2 尚未烘焙）时，不做预擦也不标记，
                //   留给逐帧路径（那时缓存已就绪）做完整擦除，避免白框像素被漏掉。
                if (UVErase && !_partReady)
                {
                    BSLog.Info("[去剑] 预擦除推迟：UV部件缓存未就绪（sprite2 未烘焙），交给逐帧路径");
                    return false;
                }

                Texture2D clone = GetSharedClone(srcTex);
                if (clone == null) return false;
                Color32[] px = clone.GetPixels32();
                if (px == null) return true;
                int w = clone.width, h = clone.height;
                int erasedCount = 0, uvTotal = 0;
                for (int i = 0; i < frames.Count; i++)
                {
                    Rect rect = frames[i].textureRect;
                    int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
                    int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
                    int rx0, ry0, rx1, ry1; bool outerRight;
                    bool hasBounds = GetSwordBounds(px, w, h, rect, out rx0, out ry0, out rx1, out ry1, out outerRight);
                    int v0 = 0, v1 = -1, ex0 = 0, ex1 = -1;
                    bool outerOk = false;
                    if (hasBounds)
                    {
                        v0 = ry0 - OuterBandPx; v1 = ry1 + OuterBandPx;
                        outerOk = HiltBandPx > 0 &&
                            Mathf.Abs((rx0 + rx1) * 0.5f - (rect.xMin + rect.xMax) * 0.5f) >= OuterMinOffsetPx;
                        if (outerRight) { ex0 = Mathf.Max(x0, rx0 - HiltBandPx); ex1 = rx0 + OuterMarginPx + 1; }
                        else { ex0 = rx1 - OuterMarginPx; ex1 = Mathf.Min(x1, rx1 + HiltBandPx + 1); }
                    }
                    // 安全阀：红暗 >20%、亮采样 >45%、光晕 >15%
                    int opaque = 0, redMatched = 0, partUV = 0, haloUV = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        if (y < 0 || y >= h) continue;
                        for (int x = x0; x < x1; x++)
                        {
                            if (x < 0 || x >= w) continue;
                            int idx = y * w + x;
                            Color32 c = px[idx];
                            if (c.a <= 8) continue;
                            opaque++;
                            if (c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax) { redMatched++; continue; }
                            if (UVErase && IsPartErase(px, idx, w))
                            {
                                if (IsPartBrightExact(px, idx, w)) partUV++;
                                else haloUV++;
                            }
                        }
                    }
                    if (opaque > 0 && (redMatched > opaque * SafetyEraseRatio ||
                        partUV > opaque * 0.45f || haloUV > opaque * 0.15f))
                    {
                        BSLog.Warn("[去剑] 预擦除跳过(疑似误擦) 帧 " + frames[i].name +
                            " 红暗=" + redMatched + " 亮采样=" + partUV + " 光晕=" + haloUV + " /不透明=" + opaque);
                        continue;
                    }
                    // 擦除
                    int erased = 0, uvErased = 0, haloErased = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        if (y < 0 || y >= h) continue;
                        for (int x = x0; x < x1; x++)
                        {
                            if (x < 0 || x >= w) continue;
                            int idx = y * w + x;
                            Color32 c = px[idx];
                            if (c.a <= 8) continue;
                            if (c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax)
                            {
                                px[idx] = new Color32(0, 0, 0, 0); erased++; continue;
                            }
                            if (hasBounds && outerOk && y >= v0 && y <= v1 && x >= ex0 && x < ex1)
                            {
                                px[idx] = new Color32(0, 0, 0, 0); erased++; continue;
                            }
                            if (UVErase && IsPartErase(px, idx, w))
                            {
                                // ⚠️ 先判"纯亮"再清零（清零后 UV 解码变 cell(0,0)，亮采样会被误标成光晕）
                                bool brightExact = IsPartBrightExact(px, idx, w);
                                px[idx] = new Color32(0, 0, 0, 0); erased++;
                                if (brightExact) uvErased++;
                                else haloErased++;
                            }
                        }
                    }
                    if (erased > 0) { erasedCount++; uvTotal += uvErased + haloErased; _erasedRects.Add(frames[i].GetInstanceID()); }
                    else _erasedRects.Add(frames[i].GetInstanceID());   // 无可擦像素，也标记避免重复扫描
                }
                if (erasedCount > 0) { clone.SetPixels32(px); clone.Apply(); }
                BSLog.Info("[去剑] 预擦除 Onehanded/Swordsman 帧 " + erasedCount + " 张" +
                    (uvTotal > 0 ? "（其中亮采样 " + uvTotal + "px）" : "") + "（消除动画播放时的剑闪回）");
                return true;
            }
            catch (Exception e) { BSLog.Warn("[去剑] 预擦除异常: " + e); return false; }
        }



        /// <summary>帧颜色直方图诊断（每个源纹理限几次）：输出当前帧 rect 的运行时真实颜色分布，用于校准剑签名。</summary>
        static void DumpFrameColorStats(Sprite src, Texture2D tex, Rect rect)
        {
            try
            {
                Color32[] px = tex.GetPixels32();
                int w = tex.width;
                int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
                int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
                int opaque = 0, bright = 0, redWide = 0, redNarrow = 0;
                int minX = 999, minY = 999, maxX = -1, maxY = -1;
                for (int y = y0; y < y1; y++)
                {
                    if (y < 0 || y >= tex.height) continue;
                    for (int x = x0; x < x1; x++)
                    {
                        if (x < 0 || x >= w) continue;
                        Color32 c = px[y * w + x];
                        if (c.a <= 8) continue;
                        opaque++;
                        if (c.r > 150 && c.g > 150 && c.b > 150) bright++;
                        if (c.r > 70 && c.g < 40 && c.b < 20)
                        {
                            redWide++;
                            if (c.r > 90 && c.g < 25 && c.b < 10) redNarrow++;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
                BSLog.Diag("[去剑诊断] 帧 " + src.name + " rect=" + rect + " 不透明=" + opaque +
                    " 亮色=" + bright + " 宽阈值红暗(70/40/20)=" + redWide +
                    " 窄阈值红暗(90/25/10)=" + redNarrow +
                    (redWide > 0 ? " 红暗bbox=(" + minX + "," + minY + ")-(" + maxX + "," + maxY + ")" : ""));
            }
            catch (Exception e) { BSLog.Warn("[去剑] 直方图诊断异常: " + e); }
        }

        static int EraseSwordPixels(Texture2D tex, Rect? region)
        {
            if (tex == null) return 0;
            Color32[] px = tex.GetPixels32();
            if (px == null) return 0;
            int w = tex.width, h = tex.height;
            int x0 = region.HasValue ? Mathf.FloorToInt(region.Value.xMin) : 0;
            int y0 = region.HasValue ? Mathf.FloorToInt(region.Value.yMin) : 0;
            int x1 = region.HasValue ? Mathf.CeilToInt(region.Value.xMax) : w;
            int y1 = region.HasValue ? Mathf.CeilToInt(region.Value.yMax) : h;
            int erased = 0;
            for (int y = y0; y < y1; y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = x0; x < x1; x++)
                {
                    if (x < 0 || x >= w) continue;
                    int i = y * w + x;
                    Color32 c = px[i];
                    if (c.a > 8 && c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax)
                    {
                        px[i] = new Color32(0, 0, 0, 0);
                        erased++;
                    }
                }
            }
            if (erased > 0) { tex.SetPixels32(px); tex.Apply(); }
            return erased;
        }

        /// <summary>擦除 sprite2(PartTex) 里的"金属亮银"像素 —— 剑区签名（运行时探针实测：剑=亮银(159~189,144~186,137~189)、
        /// 身体=暗(33,26,24)）。亮银=中性灰（|r-b| 与 |g-b| 都 < 容差）→ 排除暖色皮肤与暗色衣物。
        /// 输出擦除区 bbox 供日志校准。返回擦除数。</summary>
        static int EraseSilverPixels(Texture2D tex, Rect? region,
            out int minX, out int maxX, out int minY, out int maxY)
        {
            minX = int.MaxValue; maxX = -1; minY = int.MaxValue; maxY = -1;
            if (tex == null) return 0;
            Color32[] px = tex.GetPixels32();
            if (px == null) return 0;
            int w = tex.width, h = tex.height;
            int x0 = region.HasValue ? Mathf.FloorToInt(region.Value.xMin) : 0;
            int y0 = region.HasValue ? Mathf.FloorToInt(region.Value.yMin) : 0;
            int x1 = region.HasValue ? Mathf.CeilToInt(region.Value.xMax) : w;
            int y1 = region.HasValue ? Mathf.CeilToInt(region.Value.yMax) : h;
            int erased = 0;
            for (int y = y0; y < y1; y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = x0; x < x1; x++)
                {
                    if (x < 0 || x >= w) continue;
                    int i = y * w + x;
                    Color32 c = px[i];
                    if (c.a <= SilverAlphaMin) continue;
                    if (c.r > SilverRMin && c.g > SilverGMin && c.b > SilverBMin &&
                        Mathf.Abs(c.r - c.b) < SilverNeutralTol && Mathf.Abs(c.g - c.b) < SilverNeutralTol)
                    {
                        px[i] = new Color32(0, 0, 0, 0);
                        erased++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (erased > 0) { tex.SetPixels32(px); tex.Apply(); }
            return erased;
        }

        /// ② 剑柄/护手 = 剑刃基部靠身体一侧的非红不透明像素（仅剑刃 bbox 上下 ±OuterBandPx 纵向带
        ///    与基部向外 ±HiltBandPx 水平带）。⚠️ HiltBandPx<0 时剑柄带已禁用（帧内剑柄与身体重叠）。
        /// ③（第十二轮新增）部件亮采样：解码 UV(R/255,G/255) 采样到亮部件像素(+光晕)的帧像素一并擦——
        ///    白框像素不满足红暗阈值（G 高），只有按 UV 判定才抓得到。out partUV/haloUV 返回两项擦除数。</summary>
        static int EraseSwordInFrame(Texture2D tex, Rect rect, out int partUV, out int haloUV)
        {
            partUV = 0; haloUV = 0;
            if (tex == null) return 0;
            Color32[] px = tex.GetPixels32();
            if (px == null) return 0;
            int w = tex.width, h = tex.height;
            int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
            int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
            int rx0, ry0, rx1, ry1; bool outerRight;
            bool hasBounds = GetSwordBounds(px, w, h, rect, out rx0, out ry0, out rx1, out ry1, out outerRight);
            int v0 = 0, v1 = -1, ex0 = 0, ex1 = -1;
            bool outerOk = false;
            if (hasBounds)
            {
                v0 = ry0 - OuterBandPx; v1 = ry1 + OuterBandPx;
                outerOk = HiltBandPx > 0 &&
                    Mathf.Abs((rx0 + rx1) * 0.5f - (rect.xMin + rect.xMax) * 0.5f) >= OuterMinOffsetPx;
                if (outerRight) { ex0 = Mathf.Max(x0, rx0 - HiltBandPx); ex1 = rx0 + OuterMarginPx + 1; }
                else { ex0 = rx1 - OuterMarginPx; ex1 = Mathf.Min(x1, rx1 + HiltBandPx + 1); }
            }
            int erased = 0;
            for (int y = y0; y < y1; y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = x0; x < x1; x++)
                {
                    if (x < 0 || x >= w) continue;
                    int i = y * w + x;
                    Color32 c = px[i];
                    if (c.a <= 8) continue;
                    if (c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax)
                    {
                        px[i] = new Color32(0, 0, 0, 0);
                        erased++;
                        continue;
                    }
                    if (hasBounds && outerOk && y >= v0 && y <= v1 && x >= ex0 && x < ex1)
                    {
                        px[i] = new Color32(0, 0, 0, 0);
                        erased++;
                        continue;
                    }
                    // ★ 部件亮采样：白框像素（帧色不红暗，但解码 UV 采样到亮部件像素）
                    if (UVErase && IsPartErase(px, i, w))
                    {
                        // ⚠️ 先判"纯亮"再清零——清零后再解码 UV 会变成 (0,0)→cell(0,0)，把亮采样误标成光晕
                        bool brightExact = IsPartBrightExact(px, i, w);
                        px[i] = new Color32(0, 0, 0, 0);
                        erased++;
                        if (brightExact) partUV++;
                        else haloUV++;
                    }
                }
            }
            if (erased > 0) { tex.SetPixels32(px); tex.Apply(); }
            return erased;
        }

        /// <summary>用 RenderTexture 复制纹理（不依赖源纹理的 Read/Write 设置，AssetBundle 纹理也能读）。</summary>
        static Texture2D CloneTexture(Texture2D src)
        {
            try
            {
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
                var prevActive = RenderTexture.active;
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                tex.filterMode = src.filterMode;
                tex.wrapMode = src.wrapMode;
                return tex;
            }
            catch (Exception e)
            {
                BSLog.Warn("[去剑] 纹理克隆失败: " + e);
                return null;
            }
        }
    }
}

