using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.RaidGeneration;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// BadNorthBlackSpearman v1.3 —— 特质 Mod 式注入：GameSetup.Awake 新建干净 VikingReference 并注册进
    /// 敌人生成池 dict；LevelNode.Setup 注入每关敌人；Landing.Spawn 施加黑色外观/数值强化/冲锋/刺击技能。
    /// </summary>
    [BepInPlugin(Plugin.GUID, Plugin.NAME, Plugin.VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "badnorth.blackspearman.v1.3";
        public const string NAME = "Bad North - Black Spearman v1.3";
        public const string VERSION = "1.3.0";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public static ConfigEntry<string> SourceVikingName;
        public static ConfigEntry<string> NewVikingName;
        public static ConfigEntry<int> Bounty;
        public static ConfigEntry<float> SpawnChance;
        public static ConfigEntry<bool> ForceFirstWave;
        public static ConfigEntry<float> DamageMult;
        public static ConfigEntry<float> KnockbackMult;
        public static ConfigEntry<float> StunMult;
        public static ConfigEntry<float> ScaleMult;
        public static ConfigEntry<bool> EnableRecolor;
        public static ConfigEntry<bool> EnableWeaponSwap;
        public static ConfigEntry<bool> EnableCharge;
        public static ConfigEntry<bool> RemoveSword;
        public static ConfigEntry<int> RemoveSwordSprite2Mode;
        public static ConfigEntry<bool> RemoveSwordFrameUVErase;
        public static ConfigEntry<int> RemoveSwordFrameUVHalo;
        public static ConfigEntry<bool> EnableShield;

        static VikingReference _blackSpearman;
        static VikingAgent _sourceViking;
        static readonly HashSet<Agent> _done = new HashSet<Agent>();

        // ★ Patch 生效状态（供启动总览与运行期检查）：启动日志里每一行都必须看到 OK
        static bool _patchLevelNode, _patchRange, _patchGetAttack, _patchAttack, _patchAttackUpdate, _patchPlayAnimation;
        static readonly HashSet<Agent> _attackUpdateSeen = new HashSet<Agent>();
        static readonly int AttackAnimHash = Animator.StringToHash("Attack");
        static readonly int ClashAnimHash = Animator.StringToHash("Clash");
        static float _lastAnimLogTime = -999f;

        /// <summary>诊断探针读取：当前已注册的新单位。</summary>
        public static VikingReference BlackSpearman => _blackSpearman;
        /// <summary>诊断探针读取：已处理的黑矛兵 Agent 数量。</summary>
        public static int TrackedAgentCount => _done.Count;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            try
            {
                BindConfig();

                // 初始化日志系统（控制台 + 文件 + 全局异常捕获）+ 诊断探针
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                BSLog.Init(dir);
                // 装配身份：确认真正加载的 DLL 是刚构建/部署的那一份（防"改了源码却没部署/部署错位"）
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var fi = new FileInfo(asm.Location);
                    BSLog.Info("[装配] " + asm.FullName);
                    BSLog.Info("[装配] 路径=" + asm.Location);
                    BSLog.Info("[装配] 大小=" + fi.Length + "B 修改时间=" + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                }
                catch { }
                gameObject.AddComponent<DiagnosticsComponent>();

                On.Voxels.TowerDefense.GameSetup.Awake += OnGameSetupAwake;
                On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn += OnLandingSpawn;

                var harmony = new Harmony(GUID);
                PatchLevelNodeSetup(harmony);
                PatchSwordsman(harmony);

                // ★ 启动总览：每条 Patch 的生效状态一眼可见；任一 FAIL 都意味着原版逻辑仍残留
                BSLog.Info("[PATCH·总览] " +
                    "LevelNode=" + (_patchLevelNode ? "OK" : "FAIL") +
                    " range=" + (_patchRange ? "OK" : "FAIL") +
                    " GetAttack=" + (_patchGetAttack ? "OK" : "FAIL") +
                    " Attack=" + (_patchAttack ? "OK" : "FAIL") +
                    " AttackUpdate=" + (_patchAttackUpdate ? "OK" : "FAIL") +
                    " PlayAnim=" + (_patchPlayAnimation ? "OK" : "FAIL") +
                    " ← 全 OK 才代表长矛穿刺真正接管；Attack/AttackUpdate 任一 FAIL 则普通攻击仍是原版挥剑+跳扑");

                BSLog.Info($"[BS v1.3] Ready. 新单位: {NewVikingName.Value}");
                BSLog.Info($"[配置] Source={SourceVikingName.Value} New={NewVikingName.Value} Bounty={Bounty.Value} " +
                    $"SpawnChance={SpawnChance.Value} ForceFirstWave={ForceFirstWave.Value} " +
                    $"DMG={DamageMult.Value} KB={KnockbackMult.Value} Stun={StunMult.Value} Scale={ScaleMult.Value} " +
                    $"Recolor={EnableRecolor.Value} WeaponSwap={EnableWeaponSwap.Value} Charge={EnableCharge.Value} Shield={EnableShield.Value} " +
                    $"RemoveSword={RemoveSword.Value} Sprite2Mode={RemoveSwordSprite2Mode.Value} UVErase={RemoveSwordFrameUVErase.Value} UVHalo={RemoveSwordFrameUVHalo.Value}");
            }
            catch (Exception e)
            {
                try { Logger.LogError("[BS v1.3] Awake 初始化异常: " + e); }
                catch { }
                try { BSLog.Error("[BS v1.3] Awake 初始化异常: " + e); }
                catch { }
            }
        }

        void BindConfig()
        {
            SourceVikingName = Config.Bind("General", "SourceVikingName", "Viking_SwordShield",
                "借用其 VikingAgent 预制体作为视觉/行为模板（仅借用引用，不克隆整个 VikingReference）。\n" +
                "v1.3 基底：Viking_SwordShield（保留其真实盾牌美术，仅移除剑视觉并挂长矛）。\n" +
                "⚠️ 如果旧 cfg 里有其它值会覆盖此默认值，请删除或更新 cfg。");
            NewVikingName = Config.Bind("General", "NewVikingName", "Viking_BlackSpearman",
                "新单位在敌人生成池中的名字。");
            Bounty = Config.Bind("General", "Bounty", 8,
                "赏金（决定该单位占用的敌舰配额）。");

            SpawnChance = Config.Bind("Spawn", "SpawnChance", 0.7f,
                "每关把新单位加入敌人生成池的概率 (0~1)。");
            ForceFirstWave = Config.Bind("Spawn", "ForceFirstWave", false,
                "是否强制在第一波出现（便于测试）。");

            DamageMult = Config.Bind("Combat", "DamageMult", 1.6f, "伤害倍率。");
            KnockbackMult = Config.Bind("Combat", "KnockbackMult", 2.5f, "击退倍率。");
            StunMult = Config.Bind("Combat", "StunMult", 1.2f, "眩晕倍率。");
            ScaleMult = Config.Bind("Combat", "ScaleMult", 1.05f, "体型倍率。");

            EnableRecolor = Config.Bind("Visual", "EnableRecolor", true, "是否把新单位染成黑色。");
            EnableWeaponSwap = Config.Bind("Visual", "EnableWeaponSwap", true, "是否移除剑盾并复用我方长矛（混搭武器）。");
            // 去剑：剑烘焙在 OnehandedXXXX 动画帧里的暗红像素（R>70,G<40,B<20），由 SwordRemover 运行时擦除帧像素。
            EnableCharge = Config.Bind("Skills", "EnableCharge", true, "是否注入冲锋技能。");
            EnableShield = Config.Bind("Skills", "EnableShield", true,
                "是否让黑矛兵身上的盾牌具备剑盾兵格挡效果（近战正面格挡、箭矢/飞斧减伤弹开）。\n" +
                "设为 false 则盾牌仅剩视觉、不参与格挡。");
            RemoveSword = Config.Bind("Visual", "RemoveSword", false,
                "是否移除烘焙在身体动画帧（OnehandedXXXX）里的剑（默认关闭：颜色签名需先用日志诊断校准，" +
                "直接开启会误擦身体暗红衣物导致身体透明）。");
            RemoveSwordSprite2Mode = Config.Bind("Visual", "RemoveSwordSprite2Mode", 2,
                "sprite2(部件贴图)处理模式——新基底 PartTex_SwordShield 的剑/盾/身体都在部件贴图里（剑盾=亮银亮色、身体=暗色）。\n" +
                "0=保留原部件贴图、只靠帧擦除去剑（剑盾亮色会经帧 UV 采样残留成白框，第十三轮已弃用）；\n" +
                "1=整块清空部件单元（身体一起消失会变白框，勿用）；\n" +
                "2=只擦亮银亮色像素（剑刃+2D盾从部件贴图抹掉、暗色身体保留，第十三轮默认——白框/亮剑根治）。");
            RemoveSwordFrameUVErase = Config.Bind("Visual", "RemoveSwordFrameUVErase", true,
                "帧擦除是否启用\"UV 感知亮采样擦除\"（第十二轮白框根治）：任何帧像素的 R/G UV 解码采样到\n" +
                "亮银部件像素(>150)都一并擦除——运行时 ETC2 压缩让部件贴图局部变亮，身体帧像素采样到亮像素 = 白框，\n" +
                "红暗阈值抓不到它们，只有按 UV 采样判定才抓得到。默认 true（暗身体像素采样暗部件像素，不受影响）。");
            RemoveSwordFrameUVHalo = Config.Bind("Visual", "RemoveSwordFrameUVHalo", 0,
                "UV 亮像素光晕（0~6，部件像素距离）：>0 时把\"解码 UV 落在距亮部件像素 ≤N 部件像素\"的帧像素也擦除，\n" +
                "用于连持剑的手/护手/剑刃边缘一起删。默认 0（只擦纯亮像素=白框）；若手/剑柄仍可见，逐步加大 1→2→3。");
        }

        void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake orig, GameSetup self)
        {
            orig(self);
            BSLog.Info($"[BOOT] GameSetup.Awake 完成，dict 现有 {LevelStateObjectReferences.dict.Count} 个条目: {BSLog.Join(LevelStateObjectReferences.dict.Keys)}");
            EnsureBlackSpearmanRegistered();
        }

        /// <summary>确保黑矛兵已注册（dict 菜单阶段为空，GameSetup.Awake 与 LevelNode.Setup 都调用，重试直到成功）。</summary>
        bool EnsureBlackSpearmanRegistered()
        {
            if (_blackSpearman != null) return true;
            if (!LevelStateObjectReferences.dict.TryGetValue(SourceVikingName.Value, out var srcObj))
            {
                BSLog.Info($"[REGISTER] 等待源单位 {SourceVikingName.Value} 注册（当前 dict 条目 {LevelStateObjectReferences.dict.Count}），稍后重试");
                return false;
            }
            var src = srcObj as VikingReference;
            if (src == null)
            {
                BSLog.Error("[REGISTER] 源对象不是 VikingReference");
                return false;
            }
            try { RegisterBlackSpearman(src); }
            catch (Exception e) { BSLog.Error("[REGISTER] 注册失败: " + e); }
            return _blackSpearman != null;
        }

        void RegisterBlackSpearman(VikingReference src)
        {
            var vikingField = typeof(VikingReference).GetField("viking",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _sourceViking = null;
            if (!ReferenceEquals(vikingField, null))
                _sourceViking = vikingField.GetValue(src) as VikingAgent;
            if (_sourceViking == null)
            {
                BSLog.Error("[REGISTER] 源 VikingReference 没有 viking 预制体引用");
                return;
            }

            // ★ 新建干净对象而非克隆 prefab：作为根对象存在，避开 LevelArcConsistency 扫描与 DomainBool NPE。
            var go = new GameObject(NewVikingName.Value);
            DontDestroyOnLoad(go);

            var vr = go.AddComponent<VikingReference>();
            vr.type = VikingAgent.Type.SwordShield; // 运行时无法新增枚举值，复用近战类型
            vr.bounty = Bounty.Value;
            vr.approachAudioId = src.approachAudioId;
            vr.arriveAudioId = src.arriveAudioId;

            // ★ 预制体层面剥离：克隆"干净模板"，提前销毁逻辑残留组件（实测 Arsonist 会抢占
            //    brain.actions 导致冲锋永不触发），再交给 VikingReference.Start() 实例化。
            var stripped = BuildStrippedTemplate(_sourceViking);
            if (!ReferenceEquals(vikingField, null))
            {
                // 优先用剥离模板；剥离失败则退回源预制体（至少保证能生成）
                vikingField.SetValue(vr, stripped != null ? stripped.GetComponent<VikingAgent>() : _sourceViking);
            }

            // 不手动实例化 vikingClone —— 原版 VikingReference.Start() 下一帧创建唯一副本（手动 Instantiate 曾致双 Container + 孤儿克隆）。

            LevelStateObjectReferences.dict[NewVikingName.Value] = vr;
            _blackSpearman = vr;

            BSLog.Info($"[REGISTER] 已新建并注册 {NewVikingName.Value} (type={vr.type}, bounty={vr.bounty})");
            BSLog.Raw($"[REGISTER] 注册后 dict 键: {BSLog.Join(LevelStateObjectReferences.dict.Keys)}");
            StartCoroutine(ApplyArtDelayed(vr));
        }

        /// <summary>克隆源 VikingAgent 预制体并剥离逻辑残留组件（Pirate/KillAllEnemies/Swordsman 必须保留）。</summary>
        static VikingAgent BuildStrippedTemplate(VikingAgent src)
        {
            if (src == null) return null;
            try
            {
                GameObject stripped = UnityEngine.Object.Instantiate(src.gameObject);
                stripped.name = "BlackSpearman_StrippedTemplate";
                // ★ 自身保持 active（Start 克隆的 vikingClone 才会可见），挂在 inactive holder 下避免出现在场景。
                var holder = new GameObject("BlackSpearman_StrippedHolder");
                holder.SetActive(false);
                stripped.transform.SetParent(holder.transform, false);

                // ① 逻辑残留：DestroyImmediate 立即销毁（Destroy 延迟到帧末，VR.Start() 同帧就会克隆本模板）。
                var arsonist = stripped.GetComponent<Arsonist>();
                if (arsonist != null) UnityEngine.Object.DestroyImmediate(arsonist);
                // ★ 销毁 Shield 逻辑组件前先记录盾牌子对象（美术资源维度：
                //   权威引用 Shield.shield 字段优先，名称关键字兜底——避免\"组件销毁后再也定位不到盾牌\"）。
                var shieldComp = stripped.GetComponent<Shield>();
                Transform shieldTf = (shieldComp != null && shieldComp.shield != null) ? shieldComp.shield : null;
                if (shieldTf == null) shieldTf = BlackSpearmanWeapon.FindShieldTransform(stripped.transform);
                if (shieldComp != null) UnityEngine.Object.DestroyImmediate(shieldComp);

                // ② 视觉残留：禁用剑/武器/瞄准骨子对象（盾牌保留——剑盾兵基底的盾牌美术即黑矛兵的盾牌）
                int removedVisuals = BlackSpearmanWeapon.DisableChildrenByNames(stripped.transform, BlackSpearmanWeapon.VisualChildNameKeys);

                BSLog.Info("[REGISTER] 已生成剥离模板: 删除Arsonist=" + (arsonist != null) +
                    " 删除Shield组件=" + (shieldComp != null) +
                    " 盾牌美术[美术资源]=" + (shieldTf != null
                        ? shieldTf.name + " active=" + shieldTf.gameObject.activeSelf
                        : "缺失（黑矛兵将无盾牌视觉!）") +
                    "，禁用剑/武器子对象 " + removedVisuals + " 个");
                return stripped.GetComponent<VikingAgent>();
            }
            catch (Exception e)
            {
                BSLog.Error("[REGISTER] 剥离模板失败: " + e);
                return null;
            }
        }

        IEnumerator ApplyArtDelayed(VikingReference vr)
        {
            yield return null;
            yield return null;
            try
            {
                var icon = BlackSpearmanArt.GetIcon();
                if (icon != null)
                {
                    vr.sprite2 = icon;
                    BSLog.Info($"[ART] 美术图标已应用: sprite2={(vr.sprite2 != null)}");
                }
                else
                {
                    BSLog.Warn("[ART] 图标加载失败，sprite2 保持为 Start() 提取值");
                }
            }
            catch (Exception e) { BSLog.Warn("[ART] 美术资源加载失败: " + e); }
            try { BlackSpearmanArt.RegisterLocalization(); }
            catch (Exception e) { BSLog.Warn("[ART] 本地化注册失败: " + e); }
            // 延迟到 vikingClone 生成后，转储完整字段，确认反射配置成功
            DiagnosticsComponent.DumpVikingReference(vr, "注册后·延迟检查");
        }

        void PatchLevelNodeSetup(Harmony harmony)
        {
            TryPatch("LevelNode.Setup（把新单位注入敌人生成池）", () =>
            {
                var method = typeof(LevelNode).GetMethod("Setup",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(LevelState) }, null);
                if (ReferenceEquals(method, null)) throw new Exception("LevelNode.Setup 不存在");
                harmony.Patch(method, postfix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("LevelNodeSetupPostfix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchLevelNode);
        }

        void PatchSwordsman(Harmony harmony)
        {
            // 1) 长矛攻击距离：Patch Swordsman.range getter（默认 radius*0.7，长矛更长）
            TryPatch("Swordsman.range（长矛攻击距离）", () =>
            {
                var prop = typeof(Swordsman).GetProperty("range");
                var m = !ReferenceEquals(prop, null) ? prop.GetGetMethod() : null;
                if (ReferenceEquals(m, null)) throw new Exception("range getter 不存在");
                harmony.Patch(m, postfix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanRangePostfix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchRange);

            // 2) 长矛攻击表现：Patch Swordsman.GetAttack（换成长矛音效/方向）
            TryPatch("Swordsman.GetAttack（长矛攻击）", () =>
            {
                var m = typeof(Swordsman).GetMethod("GetAttack",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Agent) }, null);
                if (ReferenceEquals(m, null)) throw new Exception("GetAttack 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanGetAttackPrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchGetAttack);

            // ⚠️ 勿按参数类型定位 Attack：Func<,> 引用会让 Unity Mono(mscorlib 2.0) JIT 抛 TypeLoadException，
            //    使整批 Patch 静默失效。按方法名查找（Swordsman 只有一个 public Attack，无歧义）。
            TryPatch("Swordsman.Attack（长矛穿刺·不播挥剑）", () =>
            {
                var m = typeof(Swordsman).GetMethod("Attack", BindingFlags.Instance | BindingFlags.Public);
                if (ReferenceEquals(m, null)) throw new Exception("Attack 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanAttackPrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchAttack);

            // 3) 长矛穿刺节奏：Patch Swordsman.AttackUpdate（用矛刺周期结束攻击，而非等挥剑动画播完）
            TryPatch("Swordsman.AttackUpdate（长矛穿刺节奏）", () =>
            {
                var m = typeof(Swordsman).GetMethod("AttackUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
                if (ReferenceEquals(m, null)) throw new Exception("AttackUpdate 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanAttackUpdatePrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchAttackUpdate);

            // 4) 挥剑动画溯源：Patch Agent.PlayAnimation(int)，黑矛兵播放 Attack/Clash 时打调用栈
            TryPatch("Agent.PlayAnimation（挥剑动画溯源）", () =>
            {
                var m = typeof(Agent).GetMethod("PlayAnimation",
                    BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(int) }, null);
                if (ReferenceEquals(m, null)) throw new Exception("PlayAnimation(int) 不存在");
                harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("AgentPlayAnimationPrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
            }, ref _patchPlayAnimation);
        }

        // ⚠️ 雷区（Unity Mono / mscorlib 2.0）：禁止 System.Action/System.Func（TypeLoadException 使整批
        //    Patch 失效）—— 用自定义委托；反射对象判空一律 ReferenceEquals（== 会引 op_Equality 导致
        //    MissingMethodException）。
        delegate void PatchJob();

        // 每个 Patch 独立 try/catch：单个失败不拖垮其余 Patch。
        static void TryPatch(string label, PatchJob apply, ref bool ok)
        {
            try
            {
                apply();
                ok = true;
                BSLog.Info("[PATCH] ✅ " + label);
            }
            catch (Exception e)
            {
                ok = false;
                BSLog.Error("[PATCH] ❌ " + label + " 失败: " + e);
            }
        }

        // 挥剑动画溯源：黑矛兵被要求播放 Attack/Clash 动画时打调用栈。
        static void AgentPlayAnimationPrefix(Agent __instance, int anim)
        {
            try
            {
                if (__instance == null || !_done.Contains(__instance)) return;
                if (anim != AttackAnimHash && anim != ClashAnimHash) return;
                if (Time.time - _lastAnimLogTime < 0.5f) return;
                _lastAnimLogTime = Time.time;
                BSLog.Warn("[动画·溯源] ⚠️ 黑矛兵被要求播放 " +
                    (anim == AttackAnimHash ? "Attack(挥剑)" : "Clash") + " 动画 hash=" + anim +
                    " —— 刺击期间出现即说明还有原版挥剑路径，调用栈：");
                try
                {
                    var st = new System.Diagnostics.StackTrace(1, false);
                    var frames = st.GetFrames();
                    int n = frames != null ? Math.Min(frames.Length, 6) : 0;
                    for (int i = 0; i < n; i++)
                    {
                        var m = frames[i].GetMethod();
                        if (ReferenceEquals(m, null)) continue;
                        string cls = !ReferenceEquals(m.DeclaringType, null) ? m.DeclaringType.Name : "?";
                        BSLog.Info("    ← " + cls + "." + m.Name);
                    }
                }
                catch { }
            }
            catch { }
        }

        static void SwordsmanRangePostfix(Swordsman __instance, ref float __result)
        {
            try
            {
                if (__instance == null || __instance.agent == null) return;
                if (!_done.Contains(__instance.agent)) return;
                // 长矛攻击距离：原 radius*0.7*1.3≈0.118m 是剑的贴脸距离；改为"矛长 0.6m + 身体半程"≈0.69m。
                __result = __instance.agent.radius * 0.7f + 0.6f;
            }
            catch { }
        }

        static bool SwordsmanGetAttackPrefix(Swordsman __instance, Agent target, ref Attack __result)
        {
            try
            {
                if (__instance == null || __instance.agent == null || target == null) return true;
                if (!_done.Contains(__instance.agent)) return true;

                Vector3 dir = (target.chestPos - __instance.agent.chestPos).normalized;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.001f) dir = __instance.transform.forward;

                var settings = new AttackSettings
                {
                    damage = __instance.damage,
                    knockback = __instance.knockback,
                    launchImpulse = 0f,
                    stun = __instance.stun
                };
                __result = new Attack(settings, dir, (target.wChestPos + __instance.agent.wChestPos) / 2f,
                    __instance, __instance.agent.squad, "Sfx/English/Spear");
                SpearVisual.AimAt(__instance.agent, target.chestPos);   // 视觉：长矛刺向目标（盖过挥剑动画）
                BSLog.Info("[近战] GetAttack target=" + target.name + " dmg=" + settings.damage.ToString("F1") +
                    " kb=" + settings.knockback.ToString("F1") + " stun=" + settings.stun.ToString("F1") +
                    " sound=Spear dist=" + Vector3.Distance(__instance.agent.transform.position, target.transform.position).ToString("F2"));
                return false; // 跳过原版剑击
            }
            catch (Exception e)
            {
                BSLog.Warn("[PATCH] 长矛攻击改写异常: " + e);
                return true;
            }
        }

        // 长矛穿刺：跳过原版挥剑动画（观感是"用长矛执行剑的劈砍"），矛的刺出/收回/命中由
        // SpearChargeComponent.UpdateMeleeThrust 驱动。
        static readonly FieldInfo StaminaField = AccessTools.Field(typeof(Swordsman), "stamina");
        static readonly FieldInfo StaminaCostField = AccessTools.Field(typeof(Swordsman), "attackStaminaCost");

        static bool SwordsmanAttackPrefix(Swordsman __instance, Agent targetAgent, ref bool __result)
        {
            try
            {
                if (__instance == null || __instance.agent == null || targetAgent == null) return true;
                if (!_done.Contains(__instance.agent)) return true;

                // 复制原版 Attack() 的存活/距离检查（去掉 body.hopping 前置——着地即可攻击）
                if (!__instance.agent.aliveAndGrounded.active) { __result = false; return false; }
                float num = __instance.agent.radius + targetAgent.radius + __instance.range;
                if ((targetAgent.navPos.pos - __instance.agent.navPos.pos).sqrMagnitude > num * num)
                { __result = false; return false; }

                __instance.attack.SetActive(true);
                __instance.target = targetAgent;
                IslandGameplayManager.RequestCombatAudio(__instance.swingSound, __instance.gameObject);
                // 保留原版：目标为剑盾兵时触发格挡
                var enemySw = targetAgent.brain as Swordsman;
                if (enemySw != null && enemySw.shield != null) enemySw.shield.MaybeParry(__instance);
                SpearChargeComponent.NotifyMeleeAttackStart(__instance.agent);
                // ★ 拦截证据：本行出现 = Attack 前缀确实在跑、原版挥剑确实被跳过
                //   （range 应为 0.69 而非原版 0.09 —— 同时是 range Patch 的活体探针）
                BSLog.Info("[近战·拦截] Attack() 已接管 target=" + targetAgent.name +
                    " range=" + __instance.range.ToString("F2") + " → 跳过原版挥剑/跳扑");
                __result = true;
                return false;   // 跳过原版：不播挥剑动画
            }
            catch (Exception e)
            {
                BSLog.Warn("[PATCH] 长矛穿刺 Attack 改写异常: " + e);
                return true;
            }
        }

        static bool SwordsmanAttackUpdatePrefix(Swordsman __instance)
        {
            try
            {
                if (__instance == null || __instance.agent == null) return true;
                if (!_done.Contains(__instance.agent)) return true;

                // ★ 接管证据：每个黑矛兵第一次进入 AttackUpdate 时打一行（证明前缀在跑）
                if (_attackUpdateSeen.Add(__instance.agent))
                    BSLog.Info("[近战·接管] Swordsman.AttackUpdate 已接管（黑矛兵 #" + _attackUpdateSeen.Count + "）——攻击结束改由矛刺周期判定");

                // 结束判定：动画播完（穿刺不播动画，恒 false）或 矛刺周期完成
                bool animDone = __instance.agent.animationDone;
                bool meleeDone = SpearChargeComponent.MeleeAttackDone(__instance.agent);
                if (animDone || meleeDone)
                {
                    float stam = (float)StaminaField.GetValue(__instance);
                    float cost = (float)StaminaCostField.GetValue(__instance);
                    stam -= cost;
                    StaminaField.SetValue(__instance, stam);
                    __instance.attack.SetActive(false);
                    SpearChargeComponent.NotifyMeleeAttackEnd(__instance.agent);
                    if (stam > 0f && __instance.agent.enemyAgent != null)
                    {
                        // ★ 连刺：不调用原版 Attack()（其签名含 System.Func<,>，Unity Mono 类型加载雷区），
                        //   直接重启攻击状态 + 矛刺周期。
                        __instance.target = __instance.agent.enemyAgent;
                        __instance.attack.SetActive(true);
                        SpearChargeComponent.NotifyMeleeAttackStart(__instance.agent);
                    }
                    return false;
                }

                // 攻击中站桩刺击：面向锁定突刺方向（不再每帧追目标当前位置 → 消除"小的抽动"），
                // 锁方向无效时退回面向目标当前位置。
                __instance.agent.walkDir = Vector3.zero;
                var stabCharge = __instance.agent.GetComponent<SpearChargeComponent>();
                if (stabCharge != null && stabCharge.IsThrustDirectionLocked)
                    __instance.agent.SetDirection(stabCharge.LockedThrustDirection);
                else if (__instance.target != null && __instance.target.aliveAndGrounded.active)
                    __instance.agent.SetDirection(__instance.target.transform.position - __instance.transform.position);
                return false;
            }
            catch (Exception e)
            {
                BSLog.Warn("[PATCH] 长矛穿刺 AttackUpdate 改写异常: " + e);
                return true;
            }
        }

        static void LevelNodeSetupPostfix(LevelNode __instance)
        {
            // 兜底：若此前尚未注册成功（dict 在菜单阶段为空），在这里重试——此时 dict 必定已填充
            if (_blackSpearman == null && Instance != null)
                Instance.EnsureBlackSpearmanRegistered();

            if (_blackSpearman == null) return;
            if (__instance.enemies == null) return;
            if (__instance.enemies.Contains(_blackSpearman)) return;
            if (UnityEngine.Random.value > SpawnChance.Value)
            {
                BSLog.Info("[CAMPAIGN] 本关未抽中新单位（SpawnChance 未命中）");
                return;
            }

            if (ForceFirstWave.Value && __instance.enemies.Count > 0)
                __instance.enemies.Insert(0, _blackSpearman);
            else
                __instance.enemies.Add(_blackSpearman);

            BSLog.Info($"[CAMPAIGN] 已将 {NewVikingName.Value} 加入本关敌人生成池 (count={__instance.enemies.Count})");
            DiagnosticsComponent.DumpEnemies(__instance.enemies, "CAMPAIGN·注入后");
        }

        static Longship OnLandingSpawn(
            On.Voxels.TowerDefense.RaidGeneration.Landing.orig_Spawn orig, Landing self)
        {
            var ship = orig(self);
            if (ship == null || ship.agents == null || _blackSpearman == null) return ship;

            int applied = 0;
            Agent first = null;
            foreach (var a in ship.agents)
            {
                if (a == null) continue;
                var va = a.GetComponent<VikingAgent>();
                if (va == null || va.vikingReference != _blackSpearman) continue;
                if (!_done.Add(a)) continue;
                ApplyToAgent(a);
                applied++;
                if (first == null) first = a;
            }

            if (applied > 0)
            {
                BSLog.Info($"[SPAWN] 本艘敌舰生成黑矛兵 {applied} 个（累计 {_done.Count}）");
                if (first != null) DiagnosticsComponent.DumpAgent(first, "SPAWN·黑矛兵首例");
            }
            return ship;
        }

        static void ApplyToAgent(Agent a)
        {
            if (EnableRecolor.Value)
            {
                var vis = a.gameObject.AddComponent<BlackSpearmanVisual>();
                if (vis != null) vis.ApplyOnce(a);
            }
            if (EnableWeaponSwap.Value)
                BlackSpearmanWeapon.Apply(a);
            if (Mathf.Abs(ScaleMult.Value - 1f) > 0.0001f)
                a.scale *= ScaleMult.Value;

            var sw = a.GetComponent<Swordsman>();
            if (sw != null)
            {
                ScaleArr(sw.damageLevels, DamageMult.Value);
                ScaleArr(sw.knockbackLevels, KnockbackMult.Value);
                ScaleArr(sw.stunLevels, StunMult.Value);
                // ★ 静默基底挥剑音效：Swordsman.Attack() 会播 swingSound("Sfx/English/Sword/Swing")，
                //   这是"剑劈砍特效"的听觉部分。换成长矛挥击音效（复用已确认存在的 Spear 攻击音效）。
                try { sw.swingSound = "Sfx/English/Spear"; } catch { }
                BSLog.Info($"[AGENT] 黑矛兵 {a.name} 攻击范围 range={sw.range.ToString("F2")} radius={a.radius.ToString("F2")} dmg={sw.damage.ToString("F1")} kb={sw.knockback.ToString("F1")}");
            }

            if (EnableCharge.Value)
            {
                var ch = a.gameObject.AddComponent<SpearChargeComponent>();
                if (ch != null) ch.Setup(a);
                RegisterBrainAction(a, ch);
            }
            // ★ 去剑组件：无论 RemoveSword 开关都挂载（用于运行时诊断输出），擦除动作按开关执行
            var remover = a.gameObject.AddComponent<SwordRemover>();
            if (remover != null) remover.Setup(a, RemoveSword.Value);
            // ★ sprite2(部件贴图)处理模式：0=保留原部件(默认，避免白框) 1=整块清空(旧) 2=只擦亮银剑身
            SwordRemover.Sprite2Mode = RemoveSwordSprite2Mode.Value;
            // ★★ UV 感知亮采样擦除（白框根治）+ 光晕（吃持剑的手）：模式0下按"帧 UV→部件采样"判定白框像素
            SwordRemover.UVErase = RemoveSwordFrameUVErase.Value;
            SwordRemover.UVHalo = RemoveSwordFrameUVHalo.Value;
        }

        static void RegisterBrainAction(Agent a, IBrainAction action)
        {
            var brain = a != null ? a.brain : null;
            if (brain == null || action == null) return;
            if (!brain.actions.Contains(action)) brain.actions.Add(action);
        }

        static void ScaleArr(float[] arr, float m)
        {
            if (arr == null || Mathf.Abs(m - 1f) < 0.0001f) return;
            for (int i = 0; i < arr.Length; i++) arr[i] *= m;
        }
    }
}
