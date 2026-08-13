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
            SourceVikingName = Config.Bind("General", "SourceVikingName", "Viking_SwordShield",
                "借用其 VikingAgent 预制体作为视觉/行为模板（仅借用引用，不克隆整个 VikingReference）。");
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
            EnableCharge = Config.Bind("Skills", "EnableCharge", true, "是否注入冲刺技能。");
            EnableStab = Config.Bind("Skills", "EnableStab", true, "是否注入刺击技能。");
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

            if (!ReferenceEquals(vikingField, null))
                vikingField.SetValue(vr, _sourceViking);

            // ★ 手动实例化 vikingClone（vikingClone 是 public），确保 agent 在 Start() 之前即可用。
            //    原版 Start() 是 private，Unity 会在下一帧调用并重复实例化（覆盖此手动副本，无害）。
            var container = new GameObject("Container");
            container.transform.SetParent(go.transform, false);
            container.SetActive(false);
            vr.vikingClone = UnityEngine.Object.Instantiate<VikingAgent>(_sourceViking, container.transform);

            LevelStateObjectReferences.dict[NewVikingName.Value] = vr;
            _blackSpearman = vr;

            BSLog.Info($"[REGISTER] 已新建并注册 {NewVikingName.Value} (type={vr.type}, bounty={vr.bounty})");
            BSLog.Raw($"[REGISTER] 注册后 dict 键: {BSLog.Join(LevelStateObjectReferences.dict.Keys)}");
            StartCoroutine(ApplyArtDelayed(vr));
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
