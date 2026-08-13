using System;
using System.Collections;
using System.Collections.Generic;
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
        public static ConfigEntry<bool> EnableCharge;
        public static ConfigEntry<bool> EnableStab;

        static VikingReference _blackSpearman;
        static VikingAgent _sourceViking;
        static readonly HashSet<Agent> _done = new HashSet<Agent>();

        void Awake()
        {
            Instance = this;
            Log = Logger;
            BindConfig();

            On.Voxels.TowerDefense.GameSetup.Awake += OnGameSetupAwake;
            On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn += OnLandingSpawn;

            var harmony = new Harmony(GUID);
            PatchLevelNodeSetup(harmony);

            Logger.LogInfo($"[BS v1.3] Ready. 新单位: {NewVikingName.Value}");
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
            EnableCharge = Config.Bind("Skills", "EnableCharge", true, "是否注入冲刺技能。");
            EnableStab = Config.Bind("Skills", "EnableStab", true, "是否注入刺击技能。");
        }

        void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake orig, GameSetup self)
        {
            orig(self);
            if (_blackSpearman != null) return;
            if (!LevelStateObjectReferences.dict.TryGetValue(SourceVikingName.Value, out var srcObj))
            {
                Logger.LogWarning($"[BS] 源单位 {SourceVikingName.Value} 不在注册表中");
                return;
            }
            var src = srcObj as VikingReference;
            if (src == null)
            {
                Logger.LogWarning("[BS] 源对象不是 VikingReference");
                return;
            }
            try { RegisterBlackSpearman(src, self); }
            catch (Exception e) { Logger.LogError($"[BS] 注册失败: {e}"); }
        }

        void RegisterBlackSpearman(VikingReference src, GameSetup self)
        {
            var vikingField = typeof(VikingReference).GetField("viking",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _sourceViking = vikingField?.GetValue(src) as VikingAgent;
            if (_sourceViking == null)
            {
                Logger.LogError("[BS] 源 VikingReference 没有 viking 预制体引用");
                return;
            }

            // ★ 关键：新建一个干净对象，而不是 Instantiate 克隆现有 prefab。
            var go = new GameObject(NewVikingName.Value);
            go.transform.SetParent(self.transform, false);
            DontDestroyOnLoad(go);

            var vr = go.AddComponent<VikingReference>();
            vr.type = VikingAgent.Type.SwordShield; // 运行时无法新增枚举值，复用近战类型
            vr.bounty = Bounty.Value;
            vr.approachAudioId = src.approachAudioId;
            vr.arriveAudioId = src.arriveAudioId;

            vikingField?.SetValue(vr, _sourceViking);

            LevelStateObjectReferences.dict[NewVikingName.Value] = vr;
            _blackSpearman = vr;

            Logger.LogInfo($"[BS] 已新建并注册 {NewVikingName.Value} (type={vr.type}, bounty={vr.bounty})");
            StartCoroutine(ApplyArtDelayed(vr));
        }

        IEnumerator ApplyArtDelayed(VikingReference vr)
        {
            yield return null;
            yield return null;
            try
            {
                var icon = BlackSpearmanArt.GetIcon();
                if (icon != null) vr.sprite2 = icon;
            }
            catch (Exception e) { Logger.LogWarning($"[BS] 美术资源加载失败: {e.Message}"); }
            try { BlackSpearmanArt.RegisterLocalization(); }
            catch { }
        }

        void PatchLevelNodeSetup(Harmony harmony)
        {
            var method = typeof(LevelNode).GetMethod("Setup",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                Logger.LogError("[BS] 未找到 LevelNode.Setup");
                return;
            }
            harmony.Patch(method, postfix: new HarmonyMethod(
                typeof(Plugin).GetMethod("LevelNodeSetupPostfix",
                    BindingFlags.NonPublic | BindingFlags.Static)));
        }

        static void LevelNodeSetupPostfix(LevelNode __instance)
        {
            if (_blackSpearman == null) return;
            if (__instance.enemies == null) return;
            if (__instance.enemies.Contains(_blackSpearman)) return;
            if (UnityEngine.Random.value > SpawnChance.Value) return;

            if (ForceFirstWave.Value && __instance.enemies.Count > 0)
                __instance.enemies.Insert(0, _blackSpearman);
            else
                __instance.enemies.Add(_blackSpearman);

            Log?.LogInfo($"[BS] 已将 {NewVikingName.Value} 加入本关敌人生成池 (count={__instance.enemies.Count})");
        }

        static Longship OnLandingSpawn(
            On.Voxels.TowerDefense.RaidGeneration.Landing.orig_Spawn orig, Landing self)
        {
            var ship = orig(self);
            if (ship == null || ship.agents == null || _blackSpearman == null) return ship;

            foreach (var a in ship.agents)
            {
                if (a == null) continue;
                var va = a.GetComponent<VikingAgent>();
                if (va == null || va.vikingReference != _blackSpearman) continue;
                if (!_done.Add(a)) continue;
                ApplyToAgent(a);
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
