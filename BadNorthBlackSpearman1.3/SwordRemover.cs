using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 去剑组件：原版 Viking 的"剑"（剑刃+剑柄）烘焙在 OnehandedXXXX 动画帧里。
    /// 原理：把材质块 _MainTex 换成"擦除剑像素"的克隆纹理（与图集同尺寸，UV 不变，绝不动 bSprite/网格）。
    /// 剑刃 = 暗红像素（R>70,G<40,B<20）；剑柄与身体同色、无法像素分离（已知遗留，盾牌遮挡）。
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
        const float Sprite2SafetyRatio = 0.35f; // sprite2 单元以剑为主体（PartTex_Sword），阈值放宽到 35%（帧级仍用 0.2） // 单帧擦除占比上限（超过则视为误擦，放弃该帧）

        static readonly Dictionary<int, Texture2D> _textureCache = new Dictionary<int, Texture2D>();  // 源纹理 → 去剑克隆
        static readonly Dictionary<int, Sprite> _frameCache = new Dictionary<int, Sprite>();          // 源帧精灵 → 去剑精灵
        static readonly Dictionary<int, Sprite> _sprite2Cache = new Dictionary<int, Sprite>();        // 源 sprite2 → 去剑 sprite2
        static readonly HashSet<int> _skippedFrames = new HashSet<int>();                            // 因安全阀跳过的帧（不再重试）
        static readonly HashSet<int> _erasedRects = new HashSet<int>();                              // 已擦除的帧 rect（每 rect 只擦一次）
        static int _colorDiagDone;                                                                   // 帧颜色直方图诊断（限制次数）
        static bool _sprite2DiagDone;                                                                // sprite2 单元 ASCII 诊断（全局仅一次）
        static bool _preErased;                                                                      // 预擦除全部 Onehanded 帧已完成（消除动画播放时剑闪回）

        Agent _agent;
        SpriteAnimator _sa;
        bool _sprite2Done;
        bool _eraseEnabled;
        bool _dumped;      // 运行时诊断已输出（处理第一帧时）

        public void Setup(Agent agent, bool eraseEnabled)
        {
            _agent = agent;
            _eraseEnabled = eraseEnabled;
            if (_agent == null) { Destroy(this); return; }
            // 找到身体 SpriteAnimator：当前帧精灵名以 Onehanded 开头（Viking_Sword 基底动画帧）
            var sas = agent.GetComponentsInChildren<SpriteAnimator>(true);
            for (int i = 0; i < sas.Length; i++)
            {
                var sa = sas[i];
                if (sa == null) continue;
                if (IsOnehandedSprite(sa.sprite)) { _sa = sa; break; }
            }
            if (_sa == null)
            {
                BSLog.Warn("[去剑] 未找到 Onehanded 帧的 SpriteAnimator（可能基底不是 Viking_SwordShield），组件停用");
                Destroy(this);
            }
        }

        void LateUpdate()
        {
            if (_sa == null) return;

            var cur = _sa.sprite;
            if (cur != null && cur.texture != null && IsOnehandedSprite(cur))
            {
                // ★ 运行时诊断：处理第一帧时输出身体像素 ASCII 图 + sprite2 + 网格状态（无论开关，用于校准剑签名）
                if (!_dumped) { _dumped = true; DumpBodyRuntime(cur); }

                // 1) 主动画帧：当前帧是 Onehanded 帧 → 只把材质块的 _MainTex 换成去剑克隆纹理
                //    ★ 关键修复：绝不交换 bSprite/sprite/网格 —— 实测 bSprite 交换会破坏身体渲染（躯干透明），
                //    尽管顶点色/UV 都正常。网格 UV 本来就指向图集单元；克隆纹理与图集同尺寸，
                //    让 _MainTex 直接采样克隆的同一单元即可渲染"去剑帧"，完全避开 sprite 对象替换。
                if (_eraseEnabled)
                {
                    Texture2D erasedTex = EnsureErasedTexture(cur);
                    if (erasedTex != null)
                    {
                        _sa.block.SetTexture("_MainTex", erasedTex);
                        _sa.ComittBlock();
                    }
                }
            }

            // 2) sprite2（部件贴图）：只处理一次；若含剑红像素则替换为去剑版本
            if (_eraseEnabled && !_sprite2Done && _sa.sprite2 != null && _sa.sprite2.texture != null)
            {
                _sprite2Done = true;
                Sprite erased2 = GetErasedSprite2(_sa.sprite2);
                if (erased2 != null && !ReferenceEquals(_sa.sprite2, erased2))
                    _sa.SetSprite2(erased2);   // 同步更新 part 纹理 + RG 图集编码
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

        // ============ 帧擦除 ============

        static bool IsOnehandedSprite(Sprite s)
        {
            if (s == null) return false;
            if (s.name != null && s.name.StartsWith("Onehanded", StringComparison.Ordinal)) return true;
            if (s.texture != null && s.texture.name != null &&
                s.texture.name.StartsWith("Onehanded", StringComparison.Ordinal)) return true;
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
                int matched = CountMatch(erasedTex, rect);
                if (opaque > 0 && matched > opaque * SafetyEraseRatio)
                {
                    BSLog.Warn("[去剑] 帧 " + src.name + " 命中 " + matched + "/" + opaque +
                        " (>=" + (SafetyEraseRatio * 100f).ToString("F0") + "%)，疑似误擦身体 → 跳过该帧");
                    _skippedFrames.Add(key);
                    return null;
                }
                if (matched > 0) EraseSwordInFrame(erasedTex, rect);
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
        /// 不创建新 Sprite —— 调用方把返回值直接设为材质块的 _MainTex，规避 bSprite 交换的渲染破坏。</summary>
        Texture2D EnsureErasedTexture(Sprite cur)
        {
            try
            {
                var srcTex = cur.texture as Texture2D;
                if (srcTex == null) return null;
                Texture2D tex = GetSharedClone(srcTex);
                if (tex == null) return null;
                // ★ 首次：一次性预擦除图集里全部 Onehanded 帧 → 动画播放时无"首帧剑闪回"
                if (!_preErased) { _preErased = true; PreEraseAllOnehanded(tex); }
                int key = cur.GetInstanceID();
                if (_erasedRects.Contains(key)) return tex;
                _erasedRects.Add(key);
                int opaque = CountOpaque(tex, cur.textureRect);
                int matched = CountMatch(tex, cur.textureRect);
                int redMatched = CountRed(tex, cur.textureRect);
                // ★ 安全阀只看"红暗"命中（身体暗红衣物的误擦信号）；剑柄带是剑刃基部窄条，不计入
                if (opaque > 0 && redMatched > opaque * SafetyEraseRatio)
                {
                    BSLog.Warn("[去剑] 帧 " + cur.name + " 红暗命中 " + redMatched + "/" + opaque + " 疑似误擦 → 跳过该帧");
                    return null;
                }
                if (matched > 0)
                {
                    EraseSwordInFrame(tex, cur.textureRect);
                    BSLog.Info("[去剑] 帧 " + cur.name + " 去剑成功 擦除=" + matched);
                }
                return tex;
            }
            catch (Exception e) { BSLog.Warn("[去剑] EnsureErasedTexture 异常: " + e); return null; }
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

        /// <summary>计算"剑刃 + 剑柄/护手"命中像素数（安全阀用）。方向感知：剑柄在剑刃基部靠身体一侧。
        /// ⚠️ HiltBandPx<0 时剑柄带已禁用（帧内剑柄与身体重叠，擦剑柄伤身体），只计红暗剑刃。</summary>
        static int CountMatch(Texture2D tex, Rect rect)
        {
            if (tex == null) return 0;
            Color32[] px = tex.GetPixels32();
            if (px == null) return 0;
            int w = tex.width, h = tex.height;
            int rx0, ry0, rx1, ry1; bool outerRight;
            if (!GetSwordBounds(px, w, h, rect, out rx0, out ry0, out rx1, out ry1, out outerRight)) return 0;
            int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
            int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
            // 剑柄/护手纵向带（剑刃 bbox 上下扩展 OuterBandPx）与水平擦除范围（剑刃基部向外 HiltBandPx）
            int v0 = ry0 - OuterBandPx, v1 = ry1 + OuterBandPx;
            bool outerOk = HiltBandPx > 0 &&
                Mathf.Abs((rx0 + rx1) * 0.5f - (rect.xMin + rect.xMax) * 0.5f) >= OuterMinOffsetPx;
            int ex0, ex1;
            if (outerRight) { ex0 = Mathf.Max(x0, rx0 - HiltBandPx); ex1 = rx0 + OuterMarginPx + 1; }
            else { ex0 = rx1 - OuterMarginPx; ex1 = Mathf.Min(x1, rx1 + HiltBandPx + 1); }
            int n = 0;
            for (int y = y0; y < y1; y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = x0; x < x1; x++)
                {
                    if (x < 0 || x >= w) continue;
                    Color32 c = px[y * w + x];
                    if (c.a <= 8) continue;
                    // ① 红暗像素（剑刃）：整帧 rect 内都算，剑在身体左侧/右侧都能擦
                    if (c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax) { n++; continue; }
                    // ② 剑柄/护手：剑刃基部靠身体一侧的非红不透明像素（仅纵向带内）——HiltBandPx<0 时禁用
                    if (outerOk && y >= v0 && y <= v1 && x >= ex0 && x < ex1) n++;
                }
            }
            return n;
        }
        /// <summary>只统计红暗（剑刃）命中像素数 —— 安全阀专用：红暗是"身体暗红衣物的误擦信号"，
        /// 剑柄带是剑刃基部附近的窄条，几何有界、不可能成片误擦身体，故不计入安全阀。</summary>
        static int CountRed(Texture2D tex, Rect rect)
        {
            if (tex == null) return 0;
            Color32[] px = tex.GetPixels32();
            if (px == null) return 0;
            int w = tex.width;
            int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
            int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
            int n = 0;
            for (int y = y0; y < y1; y++)
            {
                if (y < 0 || y >= tex.height) continue;
                for (int x = x0; x < x1; x++)
                {
                    if (x < 0 || x >= w) continue;
                    Color32 c = px[y * w + x];
                    if (c.a > 8 && c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax) n++;
                }
            }
            return n;
        }

        /// <summary>预擦除：把图集里所有已加载的 OnehandedXXXX 帧一次性擦除，
        /// 避免动画播放时每帧"首次显示后才擦"（晚一帧 → 慢放可见的剑闪回）。所有帧共享一个像素数组，只上传一次。</summary>
        static void PreEraseAllOnehanded(Texture2D tex)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<Sprite>();
                List<Sprite> frames = null;
                for (int i = 0; i < all.Length; i++)
                {
                    var s = all[i];
                    if (s == null || s.texture == null) continue;
                    if (!ReferenceEquals(s.texture, tex)) continue;
                    if (string.IsNullOrEmpty(s.name) || !s.name.StartsWith("Onehanded")) continue;
                    if (_erasedRects.Contains(s.GetInstanceID())) continue;
                    if (frames == null) frames = new List<Sprite>();
                    frames.Add(s);
                    _erasedRects.Add(s.GetInstanceID());   // 预擦后该帧不再逐帧处理
                }
                if (frames == null || frames.Count == 0) return;

                Color32[] px = tex.GetPixels32();
                if (px == null) return;
                int w = tex.width, h = tex.height;
                int erasedCount = 0;
                for (int i = 0; i < frames.Count; i++)
                {
                    Rect rect = frames[i].textureRect;
                    int rx0, ry0, rx1, ry1; bool outerRight;
                    if (!GetSwordBounds(px, w, h, rect, out rx0, out ry0, out rx1, out ry1, out outerRight)) continue;
                    int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
                    int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
                    int v0 = ry0 - OuterBandPx, v1 = ry1 + OuterBandPx;
                    bool outerOk = HiltBandPx > 0 &&
                        Mathf.Abs((rx0 + rx1) * 0.5f - (rect.xMin + rect.xMax) * 0.5f) >= OuterMinOffsetPx;
                    int ex0, ex1;
                    if (outerRight) { ex0 = Mathf.Max(x0, rx0 - HiltBandPx); ex1 = rx0 + OuterMarginPx + 1; }
                    else { ex0 = rx1 - OuterMarginPx; ex1 = Mathf.Min(x1, rx1 + HiltBandPx + 1); }
                    // 安全阀：只看"红暗"命中（身体暗红衣物的误擦信号）；剑柄带窄条不计入
                    int opaque = 0, redMatched = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        if (y < 0 || y >= h) continue;
                        for (int x = x0; x < x1; x++)
                        {
                            if (x < 0 || x >= w) continue;
                            Color32 c = px[y * w + x];
                            if (c.a <= 8) continue;
                            opaque++;
                            if (c.r > SwordRMin && c.g < SwordGMax && c.b < SwordBMax) redMatched++;
                        }
                    }
                    if (opaque > 0 && redMatched > opaque * SafetyEraseRatio)
                    {
                        BSLog.Warn("[去剑] 预擦除跳过(疑似误擦) 帧 " + frames[i].name + " 红暗命中 " + redMatched + "/" + opaque);
                        continue;
                    }
                    // 擦除
                    int erased = 0;
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
                            if (outerOk && y >= v0 && y <= v1 && x >= ex0 && x < ex1)
                            {
                                px[idx] = new Color32(0, 0, 0, 0); erased++;
                            }
                        }
                    }
                    if (erased > 0) erasedCount++;
                }
                if (erasedCount > 0) { tex.SetPixels32(px); tex.Apply(); }
                BSLog.Info("[去剑] 预擦除 Onehanded 帧 " + erasedCount + " 张（消除动画播放时的剑闪回）");
            }
            catch (Exception e) { BSLog.Warn("[去剑] 预擦除异常: " + e); }
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
        ///    与基部向外 ±HiltBandPx 水平带）。⚠️ HiltBandPx<0 时剑柄带已禁用（帧内剑柄与身体重叠）。</summary>
        static int EraseSwordInFrame(Texture2D tex, Rect rect)
        {
            if (tex == null) return 0;
            Color32[] px = tex.GetPixels32();
            if (px == null) return 0;
            int w = tex.width, h = tex.height;
            int rx0, ry0, rx1, ry1; bool outerRight;
            if (!GetSwordBounds(px, w, h, rect, out rx0, out ry0, out rx1, out ry1, out outerRight)) return 0;
            int x0 = Mathf.FloorToInt(rect.xMin), y0 = Mathf.FloorToInt(rect.yMin);
            int x1 = Mathf.CeilToInt(rect.xMax), y1 = Mathf.CeilToInt(rect.yMax);
            int v0 = ry0 - OuterBandPx, v1 = ry1 + OuterBandPx;
            bool outerOk = HiltBandPx > 0 &&
                Mathf.Abs((rx0 + rx1) * 0.5f - (rect.xMin + rect.xMax) * 0.5f) >= OuterMinOffsetPx;
            int ex0, ex1;
            if (outerRight) { ex0 = Mathf.Max(x0, rx0 - HiltBandPx); ex1 = rx0 + OuterMarginPx + 1; }
            else { ex0 = rx1 - OuterMarginPx; ex1 = Mathf.Min(x1, rx1 + HiltBandPx + 1); }
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
                    if (outerOk && y >= v0 && y <= v1 && x >= ex0 && x < ex1)
                    {
                        px[i] = new Color32(0, 0, 0, 0);
                        erased++;
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

