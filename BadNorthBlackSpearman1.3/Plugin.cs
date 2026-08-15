using System;
using System.Collections;
using System.Collections.Generic;
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
    /// BadNorthBlackSpearman v1.3 —— 新思路（特质 Mod 式注入，而非克隆）。
    /// 与 v1.0/v1.1（运行时劫持）、v1.2（Instantiate 克隆 + 外部工具链）不同，
    /// 本版本照搬“特质 Mod”的注入模式：
    ///   1. MMHOOK Hook GameSetup.Awake，新建一个干净的 VikingReference
    ///      （new GameObject + AddComponent，绝不 Instantiate 克隆），
    ///      反射配置私有字段，注册进 LevelStateObjectReferences.dict（敌人生成池注册表）。
    ///   2. Harmony Patch LevelNode.Setup，把新单位加入每关 enemies（真正的敌人生成池）。
    ///   3. MMHOOK Hook Landing.Spawn，施加黑色外观 / 数值强化 / 冲刺与刺击技能。
    ///   4. 像特质 Mod 一样，从 Resources/ 加载美术图标 + 注册 I2 本地化。
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
        public static ConfigEntry<bool> EnableStab;
        public static ConfigEntry<bool> RemoveSword;

        static VikingReference _blackSpearman;
        static VikingAgent _sourceViking;
        static readonly HashSet<Agent> _done = new HashSet<Agent>();

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
                gameObject.AddComponent<DiagnosticsComponent>();

                On.Voxels.TowerDefense.GameSetup.Awake += OnGameSetupAwake;
                On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn += OnLandingSpawn;

                var harmony = new Harmony(GUID);
                PatchLevelNodeSetup(harmony);
                PatchSwordsman(harmony);

                BSLog.Info($"[BS v1.3] Ready. 新单位: {NewVikingName.Value}");
                BSLog.Info($"[配置] Source={SourceVikingName.Value} New={NewVikingName.Value} Bounty={Bounty.Value} " +
                    $"SpawnChance={SpawnChance.Value} ForceFirstWave={ForceFirstWave.Value} " +
                    $"DMG={DamageMult.Value} KB={KnockbackMult.Value} Stun={StunMult.Value} Scale={ScaleMult.Value} " +
                    $"Recolor={EnableRecolor.Value} WeaponSwap={EnableWeaponSwap.Value} Charge={EnableCharge.Value} Stab={EnableStab.Value}");
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
            SourceVikingName = Config.Bind("General", "SourceVikingName", "Viking_Sword",
                "借用其 VikingAgent 预制体作为视觉/行为模板（仅借用引用，不克隆整个 VikingReference）。\n" +
                "v1.3 稳定基底：Viking_Sword（单手持剑，无额外技能残留，GetAttack/range patch 兼容）。\n" +
                "⚠️ 曾试 Viking_Twohanded（双手持械观感更好但自带技能组件，且身体帧处理麻烦），已回退。\n" +
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
            // ★ 去剑（2026-08-14 研究修正）：剑是烘焙在 OnehandedXXXX 动画帧里的暗红像素
            // （颜色签名 R>70,G<40,B<20，实测随挥剑动画绕身体移动），由 SwordRemover 运行时擦除帧像素。
            // sprite2（PartTex_Median_BlurAlpha）是身体遮罩，不去动它。
            EnableCharge = Config.Bind("Skills", "EnableCharge", true, "是否注入冲刺技能。");
            EnableStab = Config.Bind("Skills", "EnableStab", true, "是否注入刺击技能。");
            RemoveSword = Config.Bind("Visual", "RemoveSword", false,
                "是否移除烘焙在身体动画帧（OnehandedXXXX）里的剑（默认关闭：颜色签名需先用日志诊断校准，" +
                "直接开启会误擦身体暗红衣物导致身体透明）。");
        }

        void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake orig, GameSetup self)
        {
            orig(self);
            BSLog.Info($"[BOOT] GameSetup.Awake 完成，dict 现有 {LevelStateObjectReferences.dict.Count} 个条目: {BSLog.Join(LevelStateObjectReferences.dict.Keys)}");
            EnsureBlackSpearmanRegistered();
        }

        /// <summary>
        /// 确保黑矛兵已注册。dict 在菜单阶段为空、进入游戏后才被原版维京人填充，
        /// 因此这里采用"重试直到成功"：GameSetup.Awake 与 LevelNode.Setup 都会调用它。
        /// </summary>
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

            // ★ 关键：新建一个干净对象，而不是 Instantiate 克隆现有 prefab。
            //    作为根对象存在（不挂在 GameSetup/LevelArcConsistency 下），
            //    避免被 LevelArcConsistency 的 LevelAssigner 扫描命中，
            //    从而避开自动添加的 DomainBool.values==null 引发的 NPE。
            var go = new GameObject(NewVikingName.Value);
            DontDestroyOnLoad(go);

            var vr = go.AddComponent<VikingReference>();
            vr.type = VikingAgent.Type.SwordShield; // 运行时无法新增枚举值，复用近战类型
            vr.bounty = Bounty.Value;
            vr.approachAudioId = src.approachAudioId;
            vr.arriveAudioId = src.arriveAudioId;

            // ★ 预制体层面剥离（层次 A）：不直接借用源预制体，而是克隆一份"干净模板"，
            //    提前销毁逻辑残留组件（实测 Viking_Sword 基底也带 Arsonist！它会抢占
            //    brain.actions，导致黑矛兵去烧房子、冲锋永不触发），再交给
            //    VikingReference.Start() 实例化。这样 Agent.Setup()/Brain.Setup() 收集
            //    IBrainAction/IAttackResponder/IAgentOrder 时根本不会有残留组件。
            var stripped = BuildStrippedTemplate(_sourceViking);
            if (!ReferenceEquals(vikingField, null))
            {
                // 优先用剥离模板；剥离失败则退回源预制体（至少保证能生成）
                vikingField.SetValue(vr, stripped != null ? stripped.GetComponent<VikingAgent>() : _sourceViking);
            }

            // 不手动实例化 vikingClone —— 原版 VikingReference.Start() 会在下一帧创建唯一副本。
            //（此前手动 Instantiate 导致"双 Container + 孤儿克隆"：手动副本永远无人引用。）

            LevelStateObjectReferences.dict[NewVikingName.Value] = vr;
            _blackSpearman = vr;

            BSLog.Info($"[REGISTER] 已新建并注册 {NewVikingName.Value} (type={vr.type}, bounty={vr.bounty})");
            BSLog.Raw($"[REGISTER] 注册后 dict 键: {BSLog.Join(LevelStateObjectReferences.dict.Keys)}");
            StartCoroutine(ApplyArtDelayed(vr));
        }

        /// <summary>
        /// 克隆源 VikingAgent 预制体并剥离逻辑残留组件，得到"干净模板"。
        /// 只删除会抢占/干扰行为的组件；Pirate（下船）/KillAllEnemies（寻敌）/Swordsman（近战大脑）必须保留。
        /// </summary>
        static VikingAgent BuildStrippedTemplate(VikingAgent src)
        {
            if (src == null) return null;
            try
            {
                GameObject stripped = UnityEngine.Object.Instantiate(src.gameObject);
                stripped.name = "BlackSpearman_StrippedTemplate";
                // ★ 自身保持 activeSelf=true（Unity Instantiate 会保留原对象 active 状态，
                //    Start() 从它克隆出的 vikingClone 才会是 active，否则敌人不可见），
                //    挂在 inactive 的 holder 下避免它作为运行时对象出现在场景里。
                var holder = new GameObject("BlackSpearman_StrippedHolder");
                holder.SetActive(false);
                stripped.transform.SetParent(holder.transform, false);

                // ① 逻辑残留组件：DestroyImmediate 立即销毁！
                //    ⚠️ 不能用 Destroy()（延迟到帧末）——AddComponent<VikingReference> 后
                //    Unity 在同帧的 Start 阶段就会执行 VR.Start() 克隆本模板，届时 Destroy 还没生效，
                //    Arsonist 会被复制进 vikingClone（实测组件树再次出现 Arsonist）。
                var arsonist = stripped.GetComponent<Arsonist>();
                if (arsonist != null) UnityEngine.Object.DestroyImmediate(arsonist);
                var shield = stripped.GetComponent<Shield>();
                if (shield != null) UnityEngine.Object.DestroyImmediate(shield);

                // ② 视觉残留子对象（盾/剑/aimer/weapon）—— 与 BlackSpearmanWeapon.RemoveSwordShield 共用同一工具
                int removedVisuals = BlackSpearmanWeapon.DisableChildrenByNames(stripped.transform, BlackSpearmanWeapon.VisualChildNameKeys);

                BSLog.Info("[REGISTER] 已生成剥离模板: 删除Arsonist=" + (arsonist != null) +
                    " 删除Shield=" + (shield != null) + "，子对象已按名称禁用 " + removedVisuals + " 个");
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
            var method = typeof(LevelNode).GetMethod("Setup",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(LevelState) }, null);
            if (ReferenceEquals(method, null))
            {
                BSLog.Error("[PATCH] 未找到 LevelNode.Setup");
                return;
            }
            harmony.Patch(method, postfix: new HarmonyMethod(
                typeof(Plugin).GetMethod("LevelNodeSetupPostfix",
                    BindingFlags.NonPublic | BindingFlags.Static)));
            BSLog.Info("[PATCH] 已 Patch LevelNode.Setup（用于把新单位注入敌人生成池）");
        }

        void PatchSwordsman(Harmony harmony)
        {
            // 1) 长矛攻击距离：Patch Swordsman.range getter（默认 radius*0.7，长矛更长）
            PropertyInfo rangeProp = typeof(Swordsman).GetProperty("range");
            if (!ReferenceEquals(rangeProp, null))
            {
                MethodInfo rangeGet = rangeProp.GetGetMethod();
                if (!ReferenceEquals(rangeGet, null))
                {
                    harmony.Patch(rangeGet, postfix: new HarmonyMethod(
                        typeof(Plugin).GetMethod("SwordsmanRangePostfix",
                            BindingFlags.NonPublic | BindingFlags.Static)));
                    BSLog.Info("[PATCH] 已 Patch Swordsman.range（长矛攻击距离）");
                }
                else BSLog.Error("[PATCH] Swordsman.range 无 getter");
            }
            else BSLog.Error("[PATCH] 未找到 Swordsman.range");

            // 2) 长矛攻击表现：Patch Swordsman.GetAttack（换成长矛音效/方向）
            MethodInfo getAttack = typeof(Swordsman).GetMethod("GetAttack",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(Agent) }, null);
            if (!ReferenceEquals(getAttack, null))
            {
                harmony.Patch(getAttack, prefix: new HarmonyMethod(
                    typeof(Plugin).GetMethod("SwordsmanGetAttackPrefix",
                        BindingFlags.NonPublic | BindingFlags.Static)));
                BSLog.Info("[PATCH] 已 Patch Swordsman.GetAttack（长矛攻击）");
            }
            else BSLog.Error("[PATCH] 未找到 Swordsman.GetAttack");
        }

        static void SwordsmanRangePostfix(Swordsman __instance, ref float __result)
        {
            try
            {
                if (__instance == null || __instance.agent == null) return;
                if (!_done.Contains(__instance.agent)) return;
                // ★ 长矛攻击距离：原"radius*0.7*1.3 ≈ 0.118m"仍是剑的贴脸距离——这就是"攻击逻辑还是剑"的根源
                //   （黑矛兵必须站到剑的距离才出手）。改为"矛长 0.6m + 身体半程"≈0.69m，
                //   让攻击在矛尖够得到的地方触发，才是长矛刺击手感。
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
                return false; // 跳过原版剑击
            }
            catch (Exception e)
            {
                BSLog.Warn("[PATCH] 长矛攻击改写异常: " + e);
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
                BSLog.Info($"[AGENT] 黑矛兵 {a.name} 攻击范围 range={sw.range.ToString("F2")} radius={a.radius.ToString("F2")} dmg={sw.damage.ToString("F1")} kb={sw.knockback.ToString("F1")}");
            }

            if (EnableCharge.Value)
            {
                var ch = a.gameObject.AddComponent<SpearChargeComponent>();
                if (ch != null) ch.Setup(a);
                RegisterBrainAction(a, ch);
            }
            if (EnableStab.Value)
            {
                var st = a.gameObject.AddComponent<SpearStabAction>();
                RegisterBrainAction(a, st);
            }
            // ★ 去剑组件：无论 RemoveSword 开关都挂载（用于运行时诊断输出），擦除动作按开关执行
            var remover = a.gameObject.AddComponent<SwordRemover>();
            if (remover != null) remover.Setup(a, RemoveSword.Value);
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
