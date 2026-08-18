using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthMixedSquad1_0
{
    /// <summary>
    /// 去剑组件：剑烘焙在动画帧（旧基底 Onehanded 红暗剑刃 / 新基底 Swordsman 帧 + sprite2 剑盾部件）。
    /// 原理：sprite2 换"分区压暗"克隆 + 帧内材质块 _MainTex 换去剑克隆（红暗 + UV 亮采样擦除）。
    /// ⚠️ 绝不动 bSprite（交换会破坏身体渲染）；安全阀：单帧擦除占比超阈值判误擦跳过，帧纹理是共享图集只擦当前 rect。
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
        const int HiltBandPx = -1;           // ⚠️ 已禁用（当前为死路径勿启用）：帧内剑柄与身体重叠，擦剑柄必伤身体。待部件贴图(sprite2)方案移除剑柄。
        const float OuterMinOffsetPx = 5f;   // 剑心与帧心偏移 <5px（剑居中）→ 不擦外侧（避免误擦居中持剑的身体）
        const float SafetyEraseRatio = 0.2f;
        // sprite2(PartTex) 亮银剑区阈值 —— 运行时探针实测：剑=亮银(159~189,144~186,137~189)、身体=暗(33,26,24)。
        // 亮银=中性灰（|r-b|、|g-b| 都 < 容差）→ 排除暖色皮肤与暗色衣物；这是剑柄残留"改部件贴图"方案落点。
        const int SilverRMin = 110;
        const int SilverGMin = 100;
        const int SilverBMin = 90;
        const int SilverNeutralTol = 60;     // 中性灰容差：|r-b|、|g-b| 都 <60 才算"金属银"
        const int SilverAlphaMin = 128;      // 只擦实心像素，忽略半透明边缘
        const float Sprite2SafetyRatio = 0.35f; // 旧基底擦除路径专用：ETC2 增亮身体像素被擦会挖洞 → 收紧到 35% 防误擦（>35% 亮判定贴图异常）
        const int SwordBrightMin = 150;      // "纯亮"阈值：剑刃金属（r,g,b>150 中性亮色）。亮银擦除只用它，绝不碰暗色/肤色身体。
        // PartTex_SwordShield 单元（64x126）的头部/头盔在单元 y<45（含亮银冠饰 y20-44，
        // 剑刃在 y45-69、胸甲在 y45-89、手/盾在 y94+）。帧擦除的"亮采样"掩码跳过头盔区，避免把头盔冠饰擦透明。
        const int HelmetMaxY = 45;
        // 帧头盔带(帧y10-30)采样的单元颜色源 = y47-88 的暗灰 + y44-59 的暖棕
        // （反向 UV 映射实测：帧剑刃/盾(y45-70)采样单元 y20-24 亮银 → 亮银必须压黑；
        // 单元 y21-47 暖棕被帧躯干带(y30-55)大量采样 → 必须压黑；y47-88 的暗灰/暖棕才是头盔专属源）。
        const int HelmSrcY0 = 47;
        const int HelmSrcY1 = 88;

        static readonly Dictionary<int, Texture2D> _textureCache = new Dictionary<int, Texture2D>();  // 源纹理 → 去剑克隆
        static readonly Dictionary<int, Sprite> _sprite2Cache = new Dictionary<int, Sprite>();        // 源 sprite2 → 去剑 sprite2
        static readonly HashSet<int> _erasedRects = new HashSet<int>();                              // 已擦除的帧 rect（每 rect 只擦一次）
        static bool _sprite2DiagDone;                                                                // sprite2 单元 ASCII 诊断（全局仅一次）
        static readonly HashSet<int> _preErasedTex = new HashSet<int>();                            // 已预擦除的源纹理（按源纹理实例 ID；消除动画播放时剑闪回）
        static int _blockDiagCount;                                                                  // 材质块修复详细转储次数（限前 2 只，避免刷屏）
        static bool _headReadWarned;                                                                 // 头部带擦除追踪：源纹理不可读时的一次性告警（见 CountHeadBandErase）

        /// <summary>sprite2(部件贴图)处理模式（由 ModConfig.RemoveSwordSprite2Mode 配置）：
        /// 0=保留原部件贴图、只靠帧擦除去剑（帧擦会挖洞/残留剑柄，弃用）；
        /// 1=整块清空部件单元（旧方案，会致身体白框，勿用）；
        /// 2=定稿：分区压暗——亮银(>150)×0.15 防剑/盾显形、暗灰×0.8 保留头盔/肩甲、躯干/手/脸烘黑
        /// （着色器为 LERP，b=0.02 时屏幕色≈克隆色；不擦不涂，零洞无白框）。</summary>
        public static int Sprite2Mode;

        // UV 感知亮采样擦除（白框根治）：
        // 运行时 ETC2 压缩的 PartTex_SwordShield 单元比离线亮（亮像素 bbox 从 y2~50 膨胀到 y0~105），
        // 部分"身体帧像素"（G 高、不满足红暗阈值）解码 UV 后采样到亮银部件像素 → 渲染成白框，旧红暗擦除抓不到。
        // 解法：擦除任何"解码 UV 采样到亮(r,g,b>150)部件像素"的帧像素——白框像素无论帧色如何都被擦，暗身体不受影响。
        public static bool UVErase = true;   // 配置 RemoveSwordFrameUVErase
        public static int UVHalo = 0;        // 配置 RemoveSwordFrameUVHalo：亮像素光晕(部件像素距离)，吃持剑的手/护手
        // 剑柄改色（RecolorGripToBody/GripFloodPx）已删除——会误涂肩甲/胸甲/头盔同色像素，
        // 且顶点色 B 恒 0.02 时剑柄本就是黑色剪影，无需改色。对应 cfg 键 RemoveSwordSprite2GripBand 已从 ModConfig 移除。

        // 部件单元像素缓存（静态共享）：供帧擦除按"UV→部件采样"判定白框像素（全黑矛兵共用一份）
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
        Sprite _blankSprite2;   // 按模式处理后的 sprite2 克隆（烘焙重置时可重应用）
        bool _eraseEnabled;
        bool _dumped;      // 运行时诊断已输出（处理第一帧时）
        bool _partKeepLogged;   // 模式0：保留原部件贴图的体检日志已输出（避免每帧刷屏）
        bool _blocksRepaired;   // 身体材质块修复+详细转储已输出（每个黑矛兵一次）
        Sprite _frameSprite;    // Update 阶段采样的当前精灵（=原版 SpriteAnimator.SetSprite 本帧提交的精灵）。
                                // 动画系统在 Update→LateUpdate 之间把 sprite 字段推进到下一帧；LateUpdate 若直接用
                                // _sa.sprite（已是新帧），会把"新帧的去剑纹理"贴到"旧帧的网格 UV"上 → 每换一帧动画闪一帧
                                // （=用户所见"身子闪烁"）。改用 Update 采样值可保证去剑纹理与网格 UV 永远一致。
        bool _partAppliedDiagDone;   // sprite2 克隆应用诊断已输出（每个黑矛兵一次）
        Sprite _partCacheSprite;     // 帧擦除 UV 掩码的部件源 = 原版 sprite2（克隆前的原件）。
                                     // 运行时 sprite2 会换成去剑/改色克隆（剑区透明）；掩码必须永远按原件构建，
                                     // 否则换成克隆后掩码变空、UV 亮采样擦除失效。
        int _partDiagFrame = -1;     // sprite2 应用诊断的延迟帧（克隆上块后等 5 帧再判读稳态）
        // 渲染状态周期诊断（定位"黑色身躯闪白"）——每 2s 打印 4 个身体渲染器的块状态 +
        // 顶点色 B/alpha + 当前帧 + 生命值。若 _PartTex 周期变 null（→ 着色器默认白 = 闪白）会在这里现形。
        float _renderDiagTimer;
        float _bSpikeTimer;            // 受击白闪（顶点色B尖峰）追踪节流（0.2s）
        bool _wasAlive = true;         // 上一帧存活状态（死亡瞬间转储用）
        bool _deathDumped;             // 死亡转储已输出（每个黑矛兵一次）
        bool _preRenderLogged;         // onPreRender 渲染前补块首次触发日志
        // 头部带逐帧擦除追踪（非屏幕、动画帧级）+ 白帧转换检测
        int _lastHeadErase = -1;       // 上一帧头部带"将被擦除"像素数
        string _lastHeadEraseFrame;    // 上一帧名
        int _headEraseFlips;           // 头部擦除 0↔N 交替翻转计数
        bool _headEraseWarned;         // 闪白实锤告警已输出（每黑矛兵一次）
        bool _whiteFrameActive;        // 当前是否处于"空块或网格塌缩"白帧状态
        int _whiteFrameScanTick;       // 白帧检测节流（隔帧扫描）

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
                // 部件单元缓存：帧擦除按"解码 UV→部件采样"判定白框像素，必须先有部件贴图
                _partCacheSprite = _sa.sprite2;   // 锁定原版部件（掩码永远按原件构建）
                EnsurePartCache(_partCacheSprite);
            }
        }

        void OnEnable()
        {
            Camera.onPreRender += OnPreRenderReblock;
        }

        void OnDisable()
        {
            Camera.onPreRender -= OnPreRenderReblock;
        }

        /// <summary>渲染前最后一刻补块——游戏死亡重烘焙在比 LateUpdate 更晚的阶段清空 _MainTex/_PartTex，
        /// 把补块覆盖掉（腾空期白影/影分身）。onPreRender 在相机渲染前触发，抢在渲染前最后时刻写回克隆。</summary>
        void OnPreRenderReblock(Camera cam)
        {
            if (_deathDumped)
            {
                if (!_preRenderLogged) { _preRenderLogged = true; BSLog.Warn("[影分身] onPreRender 渲染前补块已触发（死亡后最后一刻写回克隆）"); }
                ReblockCorpseOnce();
            }
        }

        void Update()
        {
            // 在 Update 阶段采样当前精灵——与原版 SpriteAnimator.SetSprite() 同一时刻、同一值。
            // 原版把"动画系统写进 sprite 字段的帧"与"网格 UV"在它的 Update 里一起提交；本组件的 LateUpdate 再用同一帧精灵
            // 覆盖 _MainTex 为去剑克隆，两者始终匹配。旧代码 LateUpdate 直接用 _sa.sprite（动画在 Update→LateUpdate 之间
            // 已推进到下一帧）→ 去剑纹理(新帧) vs 网格 UV(旧帧) 错位一帧 = 每次换动画帧都闪一下。
            if (_sa != null && _sa.sprite != null) _frameSprite = _sa.sprite;

            // 死亡瞬间渲染器转储（抓"击杀分裂"）——aliveState 由活→死时，打印 4 个身体渲染器的
            // 位置/网格UV/材质块，确认分裂是"渲染器各奔东西"还是"纹理/uv 错位"。
            if (_agent != null && _agent.aliveState != null)
            {
                bool aliveNow = _agent.aliveState.active;
                if (_wasAlive && !aliveNow && !_deathDumped)
                {
                    _deathDumped = true;
                    DumpDeathSplit();
                    // 死亡瞬间游戏重烘焙清空 _MainTex/_PartTex → 主 + _MIRROR_ON 镜像
                    // 都渲染默认白、ragdoll 腾空偏移 → 两个重叠白影 = 影分身。原 ReblockAfterDeath 协程首个 yield return null
                    // 要等下一帧才补块，死亡当帧仍是白影。现在**当帧同步补块**，让死亡当帧就是黑单尸；30 帧协程继续兜底。
                    ReblockCorpseOnce();
                    StartCoroutine(ScanKillHelmets());   // 击杀时头盔计数（读屏数暗身/头盔灰团块）
                    StartCoroutine(ReblockAfterDeath());
                }
                _wasAlive = aliveNow;
            }
        }

        /// <summary>击杀时头盔计数——死亡后第 3 帧读屏，在尸体屏坐标 ±160/±200 窗口内
        /// 用连通域统计 ①暗身团块 ②头盔灰团块 ③亮白团块（默认白影）：
        /// 正常=暗身1+头盔灰1+亮白0；若 ≥2 个暗身 或 ≥2 个头盔灰团块 = 凭空多生成身影/头盔（影分身实锤）。
        /// 改进：死亡当帧游戏把身体网格顶点全部重置为 (0,0,0)（[死亡分裂] 前顶点全零）→ 身体塌缩不可见，
        /// 当帧读屏必得"暗身=0"（误报）。改为等 3 帧（网格重建 + onPreRender 补块生效）后再扫描。</summary>
        IEnumerator ScanKillHelmets()
        {
            for (int i = 0; i < 3; i++) yield return null;   // 等网格重建 + 补块生效
            yield return new WaitForEndOfFrame();
            try
            {
                Camera cam = Camera.main;
                if (ReferenceEquals(cam, null))
                {
                    var cams = Camera.allCameras;
                    if (cams != null && cams.Length > 0) cam = cams[0];
                }
                if (ReferenceEquals(cam, null) || _agent == null) yield break;
                Vector3 sp = cam.WorldToScreenPoint(_agent.transform.position);
                if (sp.z <= 0f) yield break;
                int cx = Mathf.RoundToInt(sp.x), cy = Mathf.RoundToInt(sp.y);
                // 尸体出屏/贴屏边时读屏不可靠（旧日志"暗身px=0"多是尸体在屏幕边缘采到背景），跳过
                if (cx < 80 || cx > Screen.width - 80 || cy < 80 || cy > Screen.height - 80)
                {
                    BSLog.Warn("[击杀头盔计数] 尸体出屏/贴边(屏=(" + cx + "," + cy + "))，跳过读屏（防误报）");
                    yield break;
                }
                int x0 = Mathf.Clamp(cx - 160, 0, Screen.width - 1), x1 = Mathf.Clamp(cx + 160, 0, Screen.width - 1);
                int y0 = Mathf.Clamp(cy - 200, 0, Screen.height - 1), y1 = Mathf.Clamp(cy + 200, 0, Screen.height - 1);
                int w = x1 - x0 + 1, h = y1 - y0 + 1;
                if (w < 250 || h < 380) yield break;   // ±160/±200 窗口被屏幕边大幅夹断 → 不可靠

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(x0, y0, w, h), 0, 0);
                tex.Apply();
                Color[] px = tex.GetPixels();
                UnityEngine.Object.Destroy(tex);

                bool[] dark = new bool[w * h];
                bool[] helm = new bool[w * h];
                bool[] white = new bool[w * h];
                int darkN = 0, helmN = 0, whiteN = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color c = px[y * w + x];
                        float br = (c.r + c.g + c.b) / 3f;
                        bool isDark = br < 0.35f;
                        // 头盔灰收紧：头盔源 avg=71/255≈0.28 max=132≈0.52，屏幕色≈克隆色 → 0.18~0.55 才像头盔暗灰；
                        // 旧阈值 0.85 把岛屿中灰/英文兵全算进去（上次日志头盔灰≈整个窗口=背景误分类）。
                        bool isHelm = Mathf.Abs(c.r - c.b) < 0.20f && Mathf.Abs(c.g - c.b) < 0.20f &&
                            br >= 0.18f && br <= 0.55f;
                        bool isWhite = br > 0.85f;   // 默认白渲染/白影
                        dark[y * w + x] = isDark; if (isDark) darkN++;
                        helm[y * w + x] = isHelm; if (isHelm) helmN++;
                        white[y * w + x] = isWhite; if (isWhite) whiteN++;
                    }
                }

                List<int[]> dBlobs = new List<int[]>();   // 暗身团块（实测：身体已确认渲染黑 → 以暗身团块为唯一判据）
                List<int[]> hBlobs = new List<int[]>();   // 头盔灰（岛屿中灰背景污染，仅留计数参考）
                List<int[]> wBlobs = new List<int[]>();   // 亮白（海面/沙地/英文兵背景，仅留计数参考）
                int dCnt = CountScreenBlobs(dark, w, h, 60, dBlobs);
                int hCnt = CountScreenBlobs(helm, w, h, 20, hBlobs);
                int wCnt = CountScreenBlobs(white, w, h, 100, wBlobs);
                string verdict;
                if (dCnt >= 2)
                    verdict = "⚠️异常: ≥2 个暗身团块=真复制/影分身";
                else if (dCnt == 1)
                    verdict = "单尸=正常（黑身已渲染）";
                else
                    verdict = "⚠️观察: 无暗身团块（身体未渲染/出屏/被遮挡）";
                BSLog.Warn("[击杀头盔计数] 尸体屏=(" + cx + "," + cy + ") 窗口=" + w + "x" + h +
                    " 暗身px=" + darkN + " 团块=" + dCnt +
                    " 头盔灰px=" + helmN + " 团块=" + hCnt +
                    " 亮白px=" + whiteN + " 团块=" + wCnt + " → " + verdict);
                // 团块明细（刷屏源）归入 VerboseDumps；平时只留一行结论
                if (BSLog.VerboseDumps)
                {
                    for (int i = 0; i < dBlobs.Count; i++)
                        BSLog.Warn("  [暗身团块] bbox=(" + (x0 + dBlobs[i][0]) + "," + (y0 + dBlobs[i][1]) + ")-(" +
                            (x0 + dBlobs[i][2]) + "," + (y0 + dBlobs[i][3]) + ") 面积=" + dBlobs[i][4]);
                    for (int i = 0; i < hBlobs.Count; i++)
                        BSLog.Warn("  [头盔团块] bbox=(" + (x0 + hBlobs[i][0]) + "," + (y0 + hBlobs[i][1]) + ")-(" +
                            (x0 + hBlobs[i][2]) + "," + (y0 + hBlobs[i][3]) + ") 面积=" + hBlobs[i][4]);
                    for (int i = 0; i < wBlobs.Count; i++)
                        BSLog.Warn("  [亮白团块] bbox=(" + (x0 + wBlobs[i][0]) + "," + (y0 + wBlobs[i][1]) + ")-(" +
                            (x0 + wBlobs[i][2]) + "," + (y0 + wBlobs[i][3]) + ") 面积=" + wBlobs[i][4]);
                }   // VerboseDumps 团块明细结束
            }
            catch { }
        }

        /// <summary>4 连通域统计（阈值面积内的团块 bbox+面积），供击杀头盔计数用。</summary>
        static int CountScreenBlobs(bool[] mask, int w, int h, int minArea, List<int[]> blobs)
        {
            bool[] seen = new bool[w * h];
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i] || seen[i]) continue;
                var stack = new Stack<int>();
                stack.Push(i); seen[i] = true;
                int area = 0, x0 = i % w, y0 = i / w, x1 = x0, y1 = y0;
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    int cx = cur % w, cy = cur / w;
                    area++;
                    if (cx < x0) x0 = cx; if (cx > x1) x1 = cx;
                    if (cy < y0) y0 = cy; if (cy > y1) y1 = cy;
                    if (cx > 0 && mask[cur - 1] && !seen[cur - 1]) { seen[cur - 1] = true; stack.Push(cur - 1); }
                    if (cx < w - 1 && mask[cur + 1] && !seen[cur + 1]) { seen[cur + 1] = true; stack.Push(cur + 1); }
                    if (cy > 0 && mask[cur - w] && !seen[cur - w]) { seen[cur - w] = true; stack.Push(cur - w); }
                    if (cy < h - 1 && mask[cur + w] && !seen[cur + w]) { seen[cur + w] = true; stack.Push(cur + w); }
                }
                if (area >= minArea)
                {
                    count++;
                    blobs.Add(new[] { x0, y0, x1, y1, area });
                }
            }
            return count;
        }

        void LateUpdate()
        {
            if (_sa == null) return;

            var cur = _frameSprite != null ? _frameSprite : _sa.sprite;

            // 先把 sprite2 部件贴图换成去剑/改色克隆，再执行帧擦除写材质块——
            // 旧顺序"先写块再换 sprite2"：帧擦除/RepairBodyMaterialBlocks 写入的 _PartTex 是原部件贴图，
            // 随后 SetSprite2 的 ComittBlock 只把克隆提交给 BatchedSprite 的 rends（2 个渲染器），
            // 其余身体渲染器块 _PartTex 仍是原部件 → 剑柄改色/亮银擦除实际没上大部分块（用户仍见剑柄）。
            // 现在先换 sprite2，下面所有块写入都会用克隆纹理。
            if (_eraseEnabled) ApplySprite2Erase();

            if (cur != null && cur.texture != null && IsSwordFrameSprite(cur))
            {
                // 运行时诊断：处理第一帧时输出身体像素 ASCII 图 + sprite2 + 网格状态（无论开关，用于校准剑签名）
                // 该 ASCII 转储每黑矛兵 ~2KB，归入 VerboseDumps 门控；平时想校准剑签名按 F8 即可
                if (!_dumped && BSLog.VerboseDumps) { _dumped = true; DumpBodyRuntime(cur); }

                // 1) 主动画帧：当前帧是 Onehanded/Swordsman 帧 → 只把材质块的 _MainTex 换成去剑克隆纹理
                // 关键修复：绝不交换 bSprite/sprite/网格 —— 实测 bSprite 交换会破坏身体渲染（躯干透明），
                // 尽管顶点色/UV 都正常。网格 UV 本来就指向图集单元；克隆纹理与图集同尺寸，
                // 让 _MainTex 直接采样克隆的同一单元即可渲染"去剑帧"，完全避开 sprite 对象替换。
                // 不再有"路线A 跳过帧擦"分支——帧级擦透明恢复。
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
                        // 把去剑克隆 + 部件贴图强制写入全部身体 MeshRenderer 的材质块——
                        // 空块渲染器（_MainTex/_PartTex 为 null → 着色器默认白色）会渲染成白框/白板，
                        // 且游戏每帧会用原图集覆盖 block，必须每帧重写（我们组件最后 Add，LateUpdate 最后执行）。
                        RepairBodyMaterialBlocks(erasedTex);
                    }
                }
            }

            // 头部带逐帧擦除追踪——动画帧级、非屏幕。
            // 帧头盔带（帧 y12-22，约 rect 顶 40%）若含"将被擦除"像素（红暗剑刃 或 UV 亮采样），
            // 则该帧头盔在屏幕上被擦透明→露背景=亮；相邻帧 0↔N 交替 = 用户所见"头部闪白/抽搐"。
            if (BSLog.HeadTrace && cur != null && cur.texture != null)
            {
                int he = CountHeadBandErase(cur);
                if (_lastHeadErase >= 0 && _lastHeadEraseFrame != cur.name && ((_lastHeadErase == 0) != (he == 0)))
                {
                    _headEraseFlips++;
                    if (!_headEraseWarned && _headEraseFlips >= 3)
                    {
                        _headEraseWarned = true;
                        BSLog.Warn("[头部·帧擦] ⚠️ 头部带擦除在动画帧间 0↔" + he + " 交替 ≥3 次" +
                            "（帧 " + _lastHeadEraseFrame + "→" + cur.name + "，累计擦=" + _lastHeadErase + "→" + he + "）" +
                            " = 头盔在'被擦透明(露背景=亮)'与'未擦(黑盔)'间逐帧切换 = 闪白/抽搐实锤");
                    }
                }
                _lastHeadErase = he;
                _lastHeadEraseFrame = cur.name;
            }

            // 白帧转换检测——死亡/受击重烘焙瞬间材质块被清空或网格塌缩 = 1~2 帧默认白/不可见。
            // 转换沿即报（进入/恢复），隔帧扫描节流。
            if (BSLog.DeathTrace && (++_whiteFrameScanTick & 1) == 0)
            {
                bool bad = IsBodyWhiteFrame();
                if (bad != _whiteFrameActive)
                {
                    _whiteFrameActive = bad;
                    if (bad)
                        BSLog.Warn("[白帧] ⚠️ 身体进入'空块或网格塌缩'状态（将渲染默认白/不可见）帧=" +
                            (cur != null ? cur.name : "?") +
                            " alive=" + (_agent != null && _agent.aliveState != null ? _agent.aliveState.active.ToString() : "?"));
                    else
                        BSLog.Info("[白帧] 身体恢复（补块/网格重建完成）");
                }
            }

            // sprite2 应用诊断见下方（克隆上块后延迟 5 帧判读稳态）

            // sprite2 应用诊断——确认"剑柄改身体色/亮银擦除"的克隆确实写进了渲染器块（回答"剑柄改色为何没生效"）
            // 延迟到克隆上块后 5 帧再判读稳态，且统计全部 ColoredCharacter 身体渲染器里
            // 块 _PartTex == 克隆 的数量（旧版当帧只读 mrs[0]，会命中一个未被 SpriteAnimator.ComittBlock 更新的
            // 渲染器而误判"块里不是克隆"）。
            if (_eraseEnabled && !_partAppliedDiagDone && _sa.sprite2 != null &&
                _blankSprite2 != null && _partDiagFrame >= 0 && Time.frameCount >= _partDiagFrame)
            {
                _partAppliedDiagDone = true;
                try
                {
                    bool isClone = ReferenceEquals(_sa.sprite2, _blankSprite2);
                    Texture2D partTex = _sa.sprite2.texture as Texture2D;
                    string blockPart = "?";
                    int matchCount = 0, bodyCount = 0;
                    var mrs = _sa.GetComponentsInChildren<MeshRenderer>(true);
                    if (mrs != null)
                    {
                        var b = new MaterialPropertyBlock();
                        for (int i = 0; i < mrs.Length; i++)
                        {
                            var mr = mrs[i];
                            if (mr == null) continue;
                            var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                            if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                            bodyCount++;
                            try { mr.GetPropertyBlock(b); } catch { }
                            Texture pt = null;
                            try { pt = b.GetTexture("_PartTex"); } catch { }
                            if (bodyCount == 1)
                            {
                                blockPart = pt != null ? pt.name + " id=" + pt.GetInstanceID() : "null";
                            }
                            if (partTex != null && pt != null && pt.GetInstanceID() == partTex.GetInstanceID()) matchCount++;
                        }
                    }
                    string verdict = (isClone && partTex != null && bodyCount > 0 && matchCount == bodyCount)
                        ? " ← 改色克隆已上块（全部 " + bodyCount + " 个身体渲染器匹配，剑柄应已改身体色）"
                        : " ← ⚠️ 块里不是改色克隆（匹配 " + matchCount + "/" + bodyCount +
                          " 个，剑柄仍显示原色，需查 SetSprite2/烘焙重置）";
                    BSLog.Info("[去剑] sprite2 应用诊断: sprite2=" + _sa.sprite2.name +
                        " 纹理=" + (partTex != null ? partTex.name + " id=" + partTex.GetInstanceID() : "null") +
                        " 已是改色克隆=" + isClone + " | 渲染器块_PartTex=" + blockPart +
                        " 匹配克隆=" + matchCount + "/" + bodyCount + verdict);
                }
                catch (Exception e) { BSLog.Warn("[去剑] sprite2 应用诊断异常: " + e); }
            }

            // 渲染状态高频异常检测（闪白定位）——每 0.5s 采样、**持续**（不再限 3 次），
            // 仅当发现异常才打印（WARN）：_PartTex=NULL（→着色器默认白=闪白直接来源）/_MainTex 非克隆（剑闪回）/顶点色 B>0.3。
            _renderDiagTimer -= Time.deltaTime;
            if (_renderDiagTimer <= 0f)
            {
                _renderDiagTimer = 0.5f;
                DumpRenderState();
            }

            // 受击白闪追踪——_sa.color.b 是游戏 UpdateColor(Agent.cs:829) 每帧写入的受击值
            // （=1-healthFraction，健康=0、掉血→1；我们的 LateUpdate 随后压回 0.02）。若此处 b>0.05 说明
            // 该帧正处于受击白闪窗口：与 [像素采样] 时间戳关联即可判定闪白是否=受击白闪。
            _bSpikeTimer -= Time.deltaTime;
            if (_bSpikeTimer <= 0f)
            {
                _bSpikeTimer = 0.2f;
                if (_sa != null && _sa.color.b > 0.05f)
                {
                    float hf = _agent != null ? _agent.health / Mathf.Max(0.0001f, _agent.maxHealth) : -1f;
                    BSLog.Warn("[B尖峰] 受击白闪窗口 顶点色B=" + _sa.color.b.ToString("F3") +
                        " healthFraction=" + hf.ToString("F2") + " 帧=" +
                        (_sa.sprite != null ? _sa.sprite.name : "?"));
                }
            }
        }

        /// <summary>高频采样 4 个身体渲染器的块状态 + 顶点色 B。**仅当发现异常才打印**（防刷屏且能抓到瞬态白闪）：
        /// _PartTex=NULL → 着色器默认白 = 闪白直接来源；_MainTex 非克隆 → 剑闪回来源；顶点色 B>0.3 → 受击白闪。</summary>
        void DumpRenderState()
        {
            try
            {
                string frame = "?";
                try { if (_sa != null && _sa.sprite != null) frame = _sa.sprite.name; } catch { }
                float colorB = -1f;
                float colorA = -1f;
                try { if (_sa != null) { colorB = _sa.color.b; colorA = _sa.color.a; } } catch { }
                string health = "?";
                try { if (_agent != null) health = (_agent.health / Mathf.Max(0.0001f, _agent.maxHealth)).ToString("F2"); } catch { }
                var mrs = _sa != null ? _sa.GetComponentsInChildren<MeshRenderer>(true) : null;
                if (mrs == null) return;
                int mainClone = 0, partClone = 0, partNull = 0, total = 0;
                string anomaly = "";
                Texture2D erasedTex = null;
                try
                {
                    var cur = _frameSprite != null ? _frameSprite : _sa.sprite;
                    if (cur != null) erasedTex = EnsureErasedTexture(cur);
                }
                catch { }
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    total++;
                    var block = new MaterialPropertyBlock();
                    try { mr.GetPropertyBlock(block); } catch { }
                    Texture mt = null, pt = null;
                    try { mt = block.GetTexture("_MainTex"); } catch { }
                    try { pt = block.GetTexture("_PartTex"); } catch { }
                    bool mIsClone = erasedTex != null && mt != null && mt.GetInstanceID() == erasedTex.GetInstanceID();
                    bool pIsClone = _sa != null && _sa.sprite2 != null && _sa.sprite2.texture != null &&
                        pt != null && pt.GetInstanceID() == _sa.sprite2.texture.GetInstanceID();
                    if (mIsClone) mainClone++;
                    if (pIsClone) partClone++;
                    if (pt == null) partNull++;
                    if (!mIsClone || !pIsClone)
                        anomaly += "[" + mr.gameObject.name + " _MainTex=" +
                            (mt != null ? (mIsClone ? "✓" : "✗原图集") : "NULL") + " _PartTex=" +
                            (pt != null ? (pIsClone ? "✓" : "✗") : "NULL") + "]";
                }
                bool bAnomaly = colorB > 0.3f;
                bool aAnomaly = colorA >= 0f && colorA < 0.9f;   // 身体顶点 alpha<0.9 = 半透明/透明 = 闪白源
                bool any = partNull > 0 || mainClone < total || partClone < total || bAnomaly || aAnomaly;
                if (!any) return;   // 一切正常：不打印（防刷屏）
                BSLog.Warn("[渲染诊断⚠️] 帧=" + frame + " 顶点B=" + colorB.ToString("F3") +
                    "/a=" + colorA.ToString("F2") + " healthFraction=" + health +
                    " sprite2=" + (_sa != null && _sa.sprite2 != null ? _sa.sprite2.name : "null") +
                    " 渲染器 " + total + "：_MainTex克隆=" + mainClone + " _PartTex克隆=" + partClone +
                    " _PartTex=NULL=" + partNull +
                    (bAnomaly ? " ⚠️顶点色B异常(受击白闪)" : "") +
                    (aAnomaly ? " ⚠️顶点alpha<0.9(身体透明=闪白源)" : "") + anomaly);
            }
            catch (Exception e) { BSLog.Warn("[渲染诊断] 异常: " + e); }
        }

        /// <summary>死亡后持续补块——死亡瞬间游戏可能重烘焙身体、把材质块清空（白尸）；
        /// 死亡后 30 帧内每帧把去剑克隆+部件克隆重新写回 4 个身体渲染器，最后转储尸体最终块状态。
        /// 补块逻辑抽成 ReblockCorpseOnce()，死亡当帧先同步调用一次（见 Update），协程再兜底 30 帧。</summary>
        IEnumerator ReblockAfterDeath()
        {
            for (int i = 0; i < 30; i++)
            {
                yield return null;
                ReblockCorpseOnce();
                // 死亡腾空期每 ~3 帧转储所有渲染器世界/本地坐标，
                // 看"上半身/下半身"或"影子/长矛"是否在 ragdoll 期分离错位 = 影分身的第二身影。
                // 降频到每 5 帧 + 由 Diag.DeathTrace 门控；
                // 紧凑轨迹已由 BlackSpearmanDiagProbe.[腾空] 负责，这里只留 agent 内部渲染器（去掉了全场景扫描）。
                if (BSLog.DeathTrace && i % 5 == 0) DumpCorpseRenderers(i);
            }
            DumpCorpseState();
        }

        /// <summary>转储 agent 下全部 MeshRenderer 的世界/本地坐标 + enabled，
        /// 暴露死亡腾空期"两个重叠偏移身影"到底由哪个渲染器造成（上半身 vs 下半身 / 影子 / 长矛）。</summary>
        void DumpCorpseRenderers(int frameIdx)
        {
            try
            {
                var root = _agent != null ? _agent.transform : (_sa != null ? _sa.transform : null);
                if (root == null) return;
                var sb = new System.Text.StringBuilder();
                sb.Append("[尸体部位] f=" + frameIdx);
                // 1) agent 内所有 MeshRenderer
                var mrs = root.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs != null)
                {
                    for (int i = 0; i < mrs.Length; i++)
                    {
                        var mr = mrs[i];
                        if (mr == null) continue;
                        sb.Append(" | " + mr.gameObject.name +
                            " w=" + mr.transform.position.ToString("F2") +
                            " l=" + mr.transform.localPosition.ToString("F2") +
                            " en=" + mr.enabled);
                    }
                }
                // 2) agent 内所有 SpriteRenderer（查 SpriteRenderer + MeshRenderers 双重渲染）
                var srs = root.GetComponentsInChildren<SpriteRenderer>(true);
                if (srs != null)
                {
                    for (int i = 0; i < srs.Length; i++)
                    {
                        var sr = srs[i];
                        if (sr == null) continue;
                        sb.Append(" | [Sprite]" + sr.gameObject.name +
                            " en=" + sr.enabled +
                            " spr=" + (sr.sprite != null ? sr.sprite.name : "null"));
                    }
                }
                // 去掉"全场景 0.8m 内所有 MeshRenderer"扫描（刷屏元凶）——
                // 第二尸体/静态尸体的检测由 BlackSpearmanDiagProbe（[腾空] 静态尸= 字段 + AddCorpse 钩子）负责。
                BSLog.Warn(sb.ToString());
            }
            catch { }
        }

        /// <summary>把去剑克隆 + 部件克隆同步写回全部身体渲染器（主 + _MIRROR_ON 镜像），
        /// 并重设 SpriteAnimator 自己的 block。死亡当帧同步调用可让尸体当帧就是黑单尸（不再白影/影分身）。</summary>
        void ReblockCorpseOnce()
        {
            try
            {
                if (_sa == null) return;
                var cur = _frameSprite != null ? _frameSprite : _sa.sprite;
                if (cur == null) return;
                Texture2D erasedTex = EnsureErasedTexture(cur);
                if (erasedTex != null)
                {
                    if (_sa.sprite2 != null && _sa.sprite2.texture != null)
                        _sa.block.SetTexture("_PartTex", _sa.sprite2.texture);
                    _sa.block.SetTexture("_MainTex", erasedTex);
                    _sa.ComittBlock();
                    RepairBodyMaterialBlocks(erasedTex);
                }
                // 死亡/ragdoll 期 _MIRROR_ON 镜像渲染器与主渲染器
                // 翻面偏移叠加 → 双影。补块后禁用镜像渲染器，只留主渲染器 → 单尸。死亡后不会复活，可永久禁用。
                DisableMirrorRenderers();
            }
            catch { }
        }

        /// <summary>禁用 _MIRROR_ON 镜像渲染器（死亡补块时调用），消除 ragdoll 腾空期的影分身。
        /// 扫描整个 agent（不只 _sa）→ 把长矛的 _MIRROR_ON 镜像也禁用；同时禁用 Shadow 地面阴影渲染器。
        /// 继续禁用 Shield（盾牌）与 Spear（长矛主渲染器）——它们是 agent 子节点（BodyAnim 的兄弟），
        /// 死亡腾空期不会随身体翻滚而悬在上方，与翻滚的身体形成"两个重叠偏移的身影"=影分身的真正来源。</summary>
        void DisableMirrorRenderers()
        {
            try
            {
                var root = _agent != null ? _agent.transform : (_sa != null ? _sa.transform : null);
                if (root == null) return;
                var mrs = root.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs == null) return;
                int disabled = 0;
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    if (mr.gameObject.name == null) continue;
                    bool isMirror = mr.gameObject.name.IndexOf("_MIRROR_ON", StringComparison.Ordinal) >= 0;
                    bool isShadow = mr.gameObject.name.IndexOf("Shadow", StringComparison.Ordinal) >= 0;
                    bool isShield = mr.gameObject.name.IndexOf("Shield", StringComparison.Ordinal) >= 0;
                    bool isSpear = mr.gameObject.name.IndexOf("Spear", StringComparison.Ordinal) >= 0;
                    if (isMirror || isShadow || isShield || isSpear)
                    {
                        if (mr.enabled) { mr.enabled = false; disabled++; }
                    }
                }
                if (disabled > 0) BSLog.Warn("[影分身] 已禁用镜像/阴影/盾牌/长矛渲染器 " + disabled + " 个（ragdoll 期只留身体=单尸）");
            }
            catch { }
        }

        /// <summary>转储尸体最终材质块状态（确认补块后尸体是暗的，不再白身）。</summary>
        void DumpCorpseState()
        {
            try
            {
                BSLog.Warn("[死亡后] 尸体块状态 sprite=" + (_sa != null && _sa.sprite != null ? _sa.sprite.name : "?") +
                    " 顶点色=" + (_sa != null ? _sa.color.ToString("F2") : "?"));
                var mrs = _sa != null ? _sa.GetComponentsInChildren<MeshRenderer>(true) : null;
                if (mrs == null) return;
                int clone = 0, nullPart = 0, total = 0;
                Texture2D erasedTex = null;
                try
                {
                    var cur = _frameSprite != null ? _frameSprite : _sa.sprite;
                    if (cur != null) erasedTex = EnsureErasedTexture(cur);
                }
                catch { }
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    total++;
                    var block = new MaterialPropertyBlock();
                    try { mr.GetPropertyBlock(block); } catch { }
                    Texture mt = null, pt = null;
                    try { mt = block.GetTexture("_MainTex"); } catch { }
                    try { pt = block.GetTexture("_PartTex"); } catch { }
                    bool mIsClone = erasedTex != null && mt != null && mt.GetInstanceID() == erasedTex.GetInstanceID();
                    bool pIsClone = _sa != null && _sa.sprite2 != null && _sa.sprite2.texture != null &&
                        pt != null && pt.GetInstanceID() == _sa.sprite2.texture.GetInstanceID();
                    if (mIsClone && pIsClone) clone++;
                    if (pt == null) nullPart++;
                    BSLog.Warn("  · " + mr.gameObject.name +
                        " _MainTex=" + (mt != null ? (mIsClone ? "克隆✓" : mt.name) : "NULL") +
                        " _PartTex=" + (pt != null ? (pIsClone ? "克隆✓" : pt.name) : "NULL"));
                }
                BSLog.Warn("  → 尸体渲染器 " + total + "：双克隆=" + clone + " _PartTex=NULL=" + nullPart +
                    (clone == total ? " ← 补块成功，尸体为黑" : (nullPart > 0 ? " ⚠️仍有空块=白尸" : "")));
            }
            catch (Exception e) { BSLog.Warn("[死亡后] 转储异常: " + e); }
        }

        /// <summary>死亡瞬间渲染器转储——打印 4 个身体渲染器的位置/网格UV/材质块 + Ragdoller 状态，
        /// 确认"击杀分裂"是渲染器各奔东西 / 纹理或 UV 错位 / 还是原版 ragdoll 的正常解体。</summary>
        void DumpDeathSplit()
        {
            try
            {
                BSLog.Warn("[死亡分裂] " + (_agent != null ? _agent.name : "?") +
                    " pos=" + (_agent != null ? _agent.transform.position.ToString("F2") : "?") +
                    " ragdoll=" + (_agent != null && _agent.ragdoller != null ? _agent.ragdoller.enabled.ToString() : "无ragdoller") +
                    " sprite=" + (_sa != null && _sa.sprite != null ? _sa.sprite.name : "?"));
                DumpTransformHierarchy(_agent != null ? _agent.transform : null, "    ", 0);
                var mrs = _sa != null ? _sa.GetComponentsInChildren<MeshRenderer>(true) : null;
                if (mrs == null) return;
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    string uv0 = "?", verts = "?", bounds = "?", vpos = "?";
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        verts = mf.sharedMesh.vertexCount.ToString();
                        var uv = mf.sharedMesh.uv;
                        if (uv != null && uv.Length > 0) uv0 = uv[0].ToString("F3");
                        bounds = "c=" + mf.sharedMesh.bounds.center.ToString("F2") + " size=" + mf.sharedMesh.bounds.size.ToString("F2");
                        var vv = mf.sharedMesh.vertices;
                        if (vv != null && vv.Length > 0)
                        {
                            var sb = new System.Text.StringBuilder();
                            int n = Mathf.Min(4, vv.Length);
                            for (int k = 0; k < n; k++) { if (k > 0) sb.Append(' '); sb.Append(vv[k].ToString("F2")); }
                            vpos = sb.ToString();
                        }
                    }
                    var block = new MaterialPropertyBlock();
                    try { mr.GetPropertyBlock(block); } catch { }
                    Texture mt = null, pt = null;
                    try { mt = block.GetTexture("_MainTex"); } catch { }
                    try { pt = block.GetTexture("_PartTex"); } catch { }
                    BSLog.Warn("  · " + mr.gameObject.name +
                        " shader=" + (sh != null ? sh.name : "null") +
                        " localPos=" + mr.transform.localPosition.ToString("F2") +
                        " worldPos=" + mr.transform.position.ToString("F2") +
                        " 顶点=" + verts + " UV0=" + uv0 +
                        " bounds=" + bounds + " 前顶点=" + vpos +
                        " _MainTex=" + (mt != null ? mt.name : "NULL") +
                        " _PartTex=" + (pt != null ? pt.name : "NULL") +
                        " isVisible=" + mr.isVisible);
                }
            }
            catch (Exception e) { BSLog.Warn("[死亡分裂] 转储异常: " + e); }
        }

        /// <summary>转储 transform 层级（名字 + localPosition/localScale，最多 2 层），
        /// 暴露 ragdoll 腾空期主/镜像子节点的相对偏移（影分身=镜像翻面+偏移的来源）。</summary>
        static void DumpTransformHierarchy(Transform t, string indent, int depth)
        {
            if (t == null || depth > 2) return;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c == null) continue;
                BSLog.Warn(indent + c.name + " localPos=" + c.localPosition.ToString("F2") +
                    " localScale=" + c.localScale.ToString("F2"));
                DumpTransformHierarchy(c, indent + "  ", depth + 1);
            }
        }

        /// <summary>sprite2（部件贴图）处理：旧基底 PartTex_Sword → 亮银剑柄擦除；新基底 PartTex_SwordShield → 按 Sprite2Mode：
        /// 0=保留原部件（只靠帧擦除去剑，身体最完整，避免白框）；1=整块清空（旧方案，会致身体白框）；
        /// 2=分区压暗（亮银/躯干烘黑、暗灰头盔保留）。可重入：烘焙若重置 sprite2 会再次处理。
        /// 必须在帧擦除写材质块之前执行——先换好克隆，块写入才用克隆纹理。</summary>
        void ApplySprite2Erase()
        {
            if (_sa == null || _sa.sprite2 == null || _sa.sprite2.texture == null) return;
            // 记录原版部件（仅首次；掩码按原件构建，见 EnsureErasedTexture）
            if (_partCacheSprite == null) _partCacheSprite = _sa.sprite2;
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
                        if (_blankSprite2 != null) { _sa.SetSprite2(_blankSprite2); _partDiagFrame = Time.frameCount + 5; }
                    }
                }
                else if (Sprite2Mode == 2)
                {
                    if (!(_blankSprite2 != null && ReferenceEquals(_sa.sprite2, _blankSprite2)))
                    {
                        _blankSprite2 = GetBrightErasedSprite2(_sa.sprite2);
                        if (_blankSprite2 != null) { _sa.SetSprite2(_blankSprite2); _partDiagFrame = Time.frameCount + 5; }
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
                    {
                        _sa.SetSprite2(erased2);   // 同步更新 part 纹理 + RG 图集编码
                        _partDiagFrame = Time.frameCount + 5;
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

                // UV 亮采样分析（白框定位）——统计本帧里"解码 UV 采样到亮部件像素"的不透明像素
                // 这些像素渲染出来是白/亮色，且不满足红暗阈值（G 高），是模式0下白框的直接来源。
                // 图例：B=亮采样(白框像素,将被 UVErase 擦除) S=红暗剑刃 .=身体 空格=透明
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

                // 网格子对象材质块状态：验证去剑克隆 _MainTex 是否覆盖全部 MeshRenderer（含 _MIRROR_ON 变体）
                DumpMeshBlocks(_sa);

                // 一次性输出 sprite2（PartTex_Sword 外观）单元 ASCII：验证剑柄/剑身是否也画在外观里，
                // 为"若帧擦除后仍有残留 → 改 sprite2"的兜底方案提供坐标。
                if (!_sprite2DiagDone && _sa.sprite2 != null && _sa.sprite2.texture != null)
                {
                    _sprite2DiagDone = true;
                    DumpSprite2Cell(_sa.sprite2);
                }
                // PartTex 采样探针：用顶点色解码（SetSprite2 编码：g=rect.x/256单位、r=rect.y/256单位）
                // 采样 _PartTex，验证"剑=亮银、身体=暗"是否成立 → 剑柄残留"改部件贴图"正解的前置依据
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

        /// <summary>把去剑克隆 + 部件贴图写入全部身体渲染器的材质块。
        /// 实测前 2 个块 _MainTex/_PartTex 为 null（着色器默认白=白框），且游戏每帧用原图集重写 → 必须每帧补写。
        /// GetPropertyBlock 拷入全部属性，只覆盖 _MainTex/_PartTex，其余（_BloodTex/_Mirror 等）原样保留。</summary>
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

        /// <summary>详细转储：每个身体 MeshRenderer 的 mesh 顶点数 / isVisible / _MainTex 实例 ID
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

        static Sprite GetErasedSprite2(Sprite s2)
        {
            int key = s2.GetInstanceID();
            Sprite cached;
            // 加固：缓存命中但对象已销毁（跨场景）→ 移除并重建
            if (_sprite2Cache.TryGetValue(key, out cached) && cached != null) return cached;
            
            try
            {
                var srcTex = s2.texture as Texture2D;
                if (srcTex == null) return null;
                Texture2D tex = CloneTexture(srcTex);
                int opaque = CountOpaque(tex, s2.textureRect);
                int minX, maxX, minY, maxY;
                int erased = EraseSilverPixels(tex, s2.textureRect, out minX, out maxX, out minY, out maxY);
                // 亮银擦除：PartTex 里剑区签名是"金属亮银"（运行时探针：剑→(159~189,144~186,137~189)、
                // 身体→暗(33,26,24)）。历史教训：旧 EraseSwordPixels（红暗阈值）对 PartTex 永远命中 0 → 已删除。
                // 安全阀：擦除占比过高 → 判定误擦（sprite2 也可能是大图集），丢弃
                // sprite2 单元以剑为主体（PartTex_Sword），阈值放宽到 35%（帧级仍用 0.2）。
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
            // 加固：缓存命中但对象已销毁（跨场景）→ 移除并重建
            if (_sprite2Cache.TryGetValue(key, out cached) && cached != null) return cached;
            
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

        /// <summary>部件贴图**分区压暗**——不擦不涂，按颜色分类压暗：
        /// 亮银(>150) ×0.15（近白像素经未擦除帧会白闪，但白闪根因=身体透明已修复，这里仍压暗防剑刃/盾显形）；
        /// 暗灰(40≤r≤100 中性) ×0.8（头盔/肩甲/胸甲可见暗灰——从 ×0.45 调高，让头盔样式不被染黑）；
        /// 暖棕/暖肤/其它 ×0.15（躯干/手臂/手/脸 烘黑）。着色器为 LERP：b=0.02 时屏幕色≈克隆色（黑躯+可见暗灰头盔）。
        /// 由 Sprite2Mode=2（RemoveSwordSprite2Mode）启用。</summary>
        static Sprite GetBrightErasedSprite2(Sprite s2)
        {
            int key = s2.GetInstanceID();
            Sprite cached;
            // 加固：缓存命中但对象已销毁（跨场景）→ 移除并重建
            if (_sprite2Cache.TryGetValue(key, out cached) && cached != null) return cached;
            
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
                int darkStrong = 0, darkMid = 0, darkBright = 0;
                int helmKeep = 0;     // 头盔源(y47-88)暗灰/暖棕 保留原色像素数
                long sSum = 0; int sN = 0, sMax = 0;   // 躯干/亮银 压暗后 max 通道亮度统计（LERP b=0.02 → 屏幕色≈克隆色）
                long mSum = 0; int mN = 0, mMax = 0;   // 肩胸灰(×0.8) 压暗后 max 通道亮度统计
                long hSum = 0; int hN = 0, hMax = 0;   // 头盔保留区 max 通道亮度统计（验证头盔可见性）
                for (int y = y0; y < y1; y++)
                {
                    if (y < 0 || y >= h) continue;
                    int cy = y - y0;   // 单元相对 y
                    for (int x = x0; x < x1; x++)
                    {
                        if (x < 0 || x >= w) continue;
                        int i = y * w + x;
                        Color32 c = px[i];
                        if (c.a <= 8) continue;
                        if (cy >= HelmSrcY0 && cy < HelmSrcY1 && !(c.r - c.b > 25 && c.r > 130))
                        {
                            // 头盔源内的 >150 近白像素（银饰高光，max=190）
                            // 压暗到 <150（×0.7 → 190→133），消除"头部闪白"（银饰在动画帧间时现时隐 = 视觉闪白）。
                            if (c.r > SwordBrightMin && c.g > SwordBrightMin && c.b > SwordBrightMin)
                            {
                                Color32 d4 = new Color32((byte)(c.r * 0.7f), (byte)(c.g * 0.7f), (byte)(c.b * 0.7f), c.a);
                                px[i] = d4;
                                helmKeep++;
                                int b4 = Mathf.Max(d4.r, Mathf.Max(d4.g, d4.b));
                                hSum += b4; hN++; if (b4 > hMax) hMax = b4;
                                continue;
                            }
                            if (c.r > 100 && c.g > 90 && c.b > 70)
                            {
                                // 头盔源内的暖棕高光（盔沿/皮饰，r>100 g>90 b>70，max=173）
                                // 与暗灰主体(40-100)亮度跨度大 → 动画换帧时头部 UV 在两者间横跳 → 亮度闪动=抽搐/闪白。
                                // 现把暖棕高光压暗 ×0.5（173→87），与暗灰主体(40-100)亮度接轨 → 头部亮度趋于均匀、不再闪动。
                                Color32 d5 = new Color32((byte)(c.r * 0.5f), (byte)(c.g * 0.5f), (byte)(c.b * 0.5f), c.a);
                                px[i] = d5;
                                helmKeep++;
                                int b5 = Mathf.Max(d5.r, Mathf.Max(d5.g, d5.b));
                                hSum += b5; hN++; if (b5 > hMax) hMax = b5;
                                continue;
                            }
                            if (c.r >= 40 && c.r <= 100 && Mathf.Abs(c.r - c.b) <= 25)
                            {
                                // 头盔源 = 单元 y47-88 的暗灰（帧头盔带 y10-30 采样），
                                // 保留原色 → 头盔显示原版灰。亮银/暖棕已分别压暗，避免亮度跳变。
                                helmKeep++;
                                int b0 = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                                hSum += b0; hN++; if (b0 > hMax) hMax = b0;
                                continue;
                            }
                        }
                        if (c.r > SwordBrightMin && c.g > SwordBrightMin && c.b > SwordBrightMin)
                        {
                            // 亮银（剑刃/盾/冠饰）不再保留——其近白像素(189,190,189)
                            // 一旦经"帧擦除覆盖不到的帧像素"渲染出来就是白闪（用户实测：身体快速变白又恢复）。
                            // 全部重度压暗 ×0.15 → 克隆内不再有近白像素，白闪源头彻底消除。
                            Color32 d1 = new Color32((byte)(c.r * 0.15f), (byte)(c.g * 0.15f), (byte)(c.b * 0.15f), c.a);
                            px[i] = d1;
                            darkBright++;
                            int b1 = Mathf.Max(d1.r, Mathf.Max(d1.g, d1.b));
                            sSum += b1; sN++; if (b1 > sMax) sMax = b1;
                            continue;
                        }
                        if (c.r >= 40 && c.r <= 100 && Mathf.Abs(c.r - c.b) <= 25)
                        {
                            // 非头盔区暗灰（肩甲 y45-69 / 胸甲 y70-89）×0.8 可见暗灰；头盔区已在上方独立保留。
                            Color32 d2 = new Color32((byte)(c.r * 0.8f), (byte)(c.g * 0.8f), (byte)(c.b * 0.8f), c.a);
                            px[i] = d2;
                            darkMid++;
                            int b2 = Mathf.Max(d2.r, Mathf.Max(d2.g, d2.b));
                            mSum += b2; mN++; if (b2 > mMax) mMax = b2;
                        }
                        else
                        {
                            // 暖棕/暖肤/其它：重度压暗 → 躯干/手/脸 烘黑
                            Color32 d3 = new Color32((byte)(c.r * 0.15f), (byte)(c.g * 0.15f), (byte)(c.b * 0.15f), c.a);
                            px[i] = d3;
                            darkStrong++;
                            int b3 = Mathf.Max(d3.r, Mathf.Max(d3.g, d3.b));
                            sSum += b3; sN++; if (b3 > sMax) sMax = b3;
                        }
                    }
                }
                tex.SetPixels32(px); tex.Apply();
                // 校验**单元 rect 内**（不是整个图集！其他格子的亮像素与黑矛兵无关）不再有近白像素
                int remainBright = 0;
                for (int y = y0; y < y1; y++)
                {
                    if (y < 0 || y >= h) continue;
                    for (int x = x0; x < x1; x++)
                    {
                        if (x < 0 || x >= w) continue;
                        Color32 c = px[y * w + x];
                        if (c.a > 8 && c.r > SwordBrightMin && c.g > SwordBrightMin && c.b > SwordBrightMin) remainBright++;
                    }
                }
                var spr = Sprite.Create(tex, r, s2.pivot, s2.pixelsPerUnit, 0, SpriteMeshType.FullRect, s2.border);
                spr.name = s2.name + "_NoSword";
                _sprite2Cache[key] = spr;
                BSLog.Info("[去剑] sprite2 分区压暗(剑盾基底) " + s2.name + " 重压=" + darkStrong +
                    " 中压(暗灰×0.8)=" + darkMid + " 亮银压暗=" + darkBright + " 头盔源保留=" + helmKeep +
                    " 残留亮银=" + remainBright +
                    "px（头盔源(y47-88)暗灰/暖棕保留原色、剑刃/盾/躯干/手/脸烘黑；残留=0）" +
                    " ｜ 压暗后max通道亮度（屏幕色≈克隆色）：躯干avg=" + (sN > 0 ? sSum / sN : 0) + "(max=" + sMax +
                    ") 暗灰avg=" + (mN > 0 ? mSum / mN : 0) + "(max=" + mMax + ") 头盔源avg=" +
                    (hN > 0 ? hSum / hN : 0) + "(max=" + hMax + ")");
                return spr;
            }
            catch (Exception e) { BSLog.Warn("[去剑] sprite2 压暗失败: " + e); return null; }
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
                    " ← 若剑仍可见可切 RemoveSwordSprite2Mode=2（分区压暗）");
            }
            catch (Exception e) { BSLog.Warn("[去剑] 部件体检异常: " + e); }
        }

        /// <summary>共享克隆：同一源纹理只克隆一次（不整图擦除——擦除按帧 rect 单独进行 + 安全阀）。</summary>
        static Texture2D GetSharedClone(Texture2D srcTex)
        {
            int texKey = srcTex.GetInstanceID();
            Texture2D cached;
            // 加固：跨场景静态缓存可能残留已销毁的 Unity 对象（==null 为 true 但 C# 引用非空），
            // 取出后补一次 Unity 空判断，命中销毁对象则丢弃旧值直接重建，避免下游 GetPixels32 抛异常被吞 → 去剑静默失效。
            if (_textureCache.TryGetValue(texKey, out cached) && cached != null) return cached;
            
            Texture2D tex = CloneTexture(srcTex);
            if (tex == null) return null;
            _textureCache[texKey] = tex;
            BSLog.Info("[去剑] 已克隆帧纹理: " + srcTex.name + " " + srcTex.width + "x" + srcTex.height);
            return tex;
        }

        /// <summary>获取共享去剑克隆并擦除当前帧 rect（每个 rect 只擦一次）。
        /// 不创建新 Sprite —— 调用方把返回值直接设为材质块的 _MainTex，规避 bSprite 交换的渲染破坏。
        /// 擦除分两类——红暗剑刃(旧) + 部件亮采样(新，白框像素=解码 UV 采样到亮部件像素)。</summary>
        Texture2D EnsureErasedTexture(Sprite cur)
        {
            try
            {
                var srcTex = cur.texture as Texture2D;
                if (srcTex == null) return null;
                // 部件单元缓存：只有拿到 sprite2 部件贴图才能按 UV 判定白框像素（sprite2 未就绪就重试）
                // 必须用原版部件（_partCacheSprite）构建掩码——运行时 sprite2 已是去剑/改色克隆，
                // 剑区已透明，掩码会变空、UV 亮采样擦除失效。
                if (_sa != null) EnsurePartCache(_partCacheSprite != null ? _partCacheSprite : _sa.sprite2);
                Texture2D tex = GetSharedClone(srcTex);
                if (tex == null) return null;
                // 首次：一次性预擦除图集里全部 Onehanded/Swordsman 帧 → 动画播放时无"首帧剑闪回"
                // 修复根因：必须传**源纹理**（sprite.texture 与源纹理 ReferenceEquals）。
                // 旧代码误传共享克隆（GetSharedClone 产物）→ 任何 sprite 的 texture 都不等于克隆 → 帧列表恒空 →
                // 预擦除被"空结果=完成"提前标记，全部帧退回运行时逐帧擦除 = 每帧首显剑闪回（用户所见"美术素材的闪亮"）。
                int srcKey = srcTex.GetInstanceID();
                if (!_preErasedTex.Contains(srcKey) && PreEraseAllOnehanded(srcTex))
                    _preErasedTex.Add(srcKey);
                int key = cur.GetInstanceID();
                if (_erasedRects.Contains(key)) return tex;
                _erasedRects.Add(key);
                int opaque = CountOpaque(tex, cur.textureRect);
                int redDark, partUV, haloUV;
                int matched = CountEraseScan(tex, cur.textureRect, out redDark, out partUV, out haloUV);
                // 安全阀（三指标）：红暗命中 >20%（身体暗红衣物的误擦信号）、亮采样 >45%（白框像素=采样到亮部件，
                // 可放宽；超过说明部件缓存异常/贴图被替换）、光晕擦除 >15%（光晕吃手，但过量说明误擦身体）
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

        // ============ UV 感知亮采样擦除（白框像素 = 解码 UV 采样到亮部件像素） ============

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
                // 头盔保护：擦除掩码跳过头盔区（y<HelmetMaxY）与头盔源（y≥HelmSrcY0），
                // 避免把头盔冠饰/银饰擦透明 → 头部帧像素永不被亮采样擦除（剑刃仍走红暗擦除）。
                if (y < HelmetMaxY || y >= HelmSrcY0) continue;
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

        /// <summary>单像素 UV 解码 → 部件擦除掩码判定（Color 版，供头部带擦除计数用；
        /// 与 IsPartErase 等价，只是输入为 0~1 的 Color 而非 Color32[]+下标）。</summary>
        static bool IsPartEraseAt(Color c)
        {
            if (!_partReady || _partEraseMask == null) return false;
            int cw = Mathf.FloorToInt(_partRect.width), ch = Mathf.FloorToInt(_partRect.height);
            if (cw <= 0 || ch <= 0) return false;
            int cx = (int)(c.r * cw);
            int cy = (int)(c.g * ch);
            if (cx < 0 || cy < 0 || cx >= cw || cy >= ch) return false;
            return _partEraseMask[cy * cw + cx];
        }

        /// <summary>统计当前动画帧的"头部带"内将被擦除的像素数。
        /// 帧头盔带 = relY(自底) 0.10~0.50（实测：面部 y2-10、头盔 y12-22、帧高 ~70px）。
        /// 用原图集像素（擦除前的真值）判红暗/UV亮采样；只读子矩形，不做全图集扫描。
        /// 该数在动画帧间 0↔N 交替 = 头盔在"擦透明(露背景=亮)"与"未擦(黑盔)"间切换 = 闪白。</summary>
        static int CountHeadBandErase(Sprite cur)
        {
            try
            {
                var srcTex = cur.texture as Texture2D;
                if (srcTex == null || !_partReady || _partEraseMask == null) return 0;
                Rect r = cur.textureRect;
                int x0 = Mathf.FloorToInt(r.xMin), y0 = Mathf.FloorToInt(r.yMin);
                int x1 = Mathf.CeilToInt(r.xMax), y1 = Mathf.CeilToInt(r.yMax);
                int hh = y1 - y0;
                // 反向 UV 映射实测：帧头盔带 = 帧 y10-30（面部 y2-10、头盔 y12-22），
                // rect 高 ~70px → relY(自底) 0.10~0.43。取 [0.10, 0.50] 覆盖头/盔。
                int yHead = y0 + (int)(hh * 0.10f);
                int yHeadEnd = y0 + (int)(hh * 0.50f);
                if (yHead >= y1 || yHeadEnd <= y0) return 0;
                int bw = x1 - x0, bh = yHeadEnd - yHead;
                if (bw <= 0 || bh <= 0) return 0;
                Color[] px;
                try { px = srcTex.GetPixels(x0, yHead, bw, bh); }
                catch
                {
                    // 加固：源图集纹理（AssetBundle）通常不可读 → GetPixels 抛异常。
                    // 旧代码静默返回 0 → 头部带擦除追踪恒为"无擦除"，闪白检测静默失效且无日志。不可读时打一次性告警。
                    if (!_headReadWarned)
                    {
                        _headReadWarned = true;
                        BSLog.Warn("[头部·帧擦] 源纹理不可读（GetPixels 失败），头部带擦除追踪停用: " + srcTex.name);
                    }
                    return 0;
                }
                int n = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    Color c = px[i];
                    if (c.a <= 8f / 255f) continue;
                    byte rr = (byte)(c.r * 255f), g = (byte)(c.g * 255f), b = (byte)(c.b * 255f);
                    if ((rr > SwordRMin && g < SwordGMax && b < SwordBMax) ||
                        (UVErase && IsPartEraseAt(c)))
                        n++;
                }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>身体是否处于"空块"状态（→ 渲染默认白）。
        /// **移除"4顶点全零"判据**——BatchedSprite.cs 实测角色网格顶点恒为 (0,0,0)，
        /// 四边形由 shader 用 uv2/tangent/bounds 展开，顶点全零是正常 billboard 表示，之前"塌缩"是误报。
        /// 死亡/受击重烘焙瞬间 _MainTex/_PartTex 被清空才是真白帧窗口。</summary>
        bool IsBodyWhiteFrame()
        {
            try
            {
                if (_sa == null) return false;
                var mrs = _sa.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs == null) return false;
                var block = new MaterialPropertyBlock();
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    try { mr.GetPropertyBlock(block); } catch { }
                    Texture mt = null, pt = null;
                    try { mt = block.GetTexture("_MainTex"); } catch { }
                    try { pt = block.GetTexture("_PartTex"); } catch { }
                    if (mt == null || pt == null) return true;
                }
                return false;
            }
            catch { return false; }
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
        /// 新基底是 Swordsman 帧（旧代码只擦 Onehanded → 新基底从未预擦，剑闪回仍在）；并加部件亮采样擦除。
        /// 入参改为**源纹理**（匹配 sprite.texture），像素读写用共享克隆（调用方 _MainTex 用的同一份）。
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
                // 安全护栏：UV 擦除开启但部件缓存未就绪（sprite2 尚未烘焙）时，不做预擦也不标记，
                // 留给逐帧路径（那时缓存已就绪）做完整擦除，避免白框像素被漏掉。
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
        /// 与基部向外 ±HiltBandPx 水平带）。⚠️ HiltBandPx<0 时剑柄带已禁用（帧内剑柄与身体重叠）。
        /// ③（新增）部件亮采样：解码 UV(R/255,G/255) 采样到亮部件像素(+光晕)的帧像素一并擦——
        /// 白框像素不满足红暗阈值（G 高），只有按 UV 判定才抓得到。out partUV/haloUV 返回两项擦除数。</summary>
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
                    // 部件亮采样：白框像素（帧色不红暗，但解码 UV 采样到亮部件像素）
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

