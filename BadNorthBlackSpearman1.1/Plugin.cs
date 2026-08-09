using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.CampaignGeneration.CampaignAc3;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_1
{
    [BepInPlugin("black.spearman.v1.1", "Bad North - Black Spearman v1.1", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static BepInEx.Logging.ManualLogSource SharedLogger;

        internal const string BlackSpearmanRefName = "Viking_BlackSpearman";
        internal const string TemplateRefName = "Viking_SwordShield";

        internal const float DamageMultiplier = 1.6f;
        internal const float KnockbackMultiplier = 2.5f;
        internal const float ArmorMultiplier = 1.3f;
        internal const float ScaleMultiplier = 1.05f;
        internal const float RangeMultiplier = 3.5f;
        internal const float ConversionChance = 0.4f;

        internal static readonly HashSet<Agent> BlackSpearmanAgents = new HashSet<Agent>();

        private Harmony _harmony;
        private bool _enemyRegistered;

        private void Start()
        {
            Instance = this;
            SharedLogger = Logger;
            Logger.LogInfo("[BlackSpearman1.1] ====== v1.1 Traditional Enemy Pool ======");

            _harmony = new Harmony("black.spearman.v1.1");
            On.Voxels.TowerDefense.GameSetup.Awake += OnGameSetupAwake;
            PatchGetAttack();
            PatchRangeGetter();
            SubscribeAgentSpawned();

            Logger.LogInfo("[BlackSpearman1.1] All hooks registered. Waiting for GameSetup.Awake...");
        }

        // ============ MMHOOK：GameSetup.Awake 注册 ============

        private void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake orig, GameSetup self)
        {
            orig(self);
            if (_enemyRegistered) return;
            _enemyRegistered = true;
            Logger.LogInfo("[BlackSpearman1.1] GameSetup.Awake done - registering new enemy type...");
            try { RegisterBlackSpearmanEnemy(); }
            catch (Exception ex) { Logger.LogError("[BlackSpearman1.1] Register failed: " + ex); }
        }

        private void RegisterBlackSpearmanEnemy()
        {
            if (LevelStateObjectReferences.dict.ContainsKey(BlackSpearmanRefName))
            { Logger.LogInfo("[BlackSpearman1.1] Already registered"); return; }

            UnityEngine.Object obj;
            if (!LevelStateObjectReferences.dict.TryGetValue(TemplateRefName, out obj))
            { Logger.LogError("[BlackSpearman1.1] Template not found!"); DumpDictKeys(); return; }

            var templateVR = obj as VikingReference;
            if (ReferenceEquals(templateVR, null)) { Logger.LogError("[BlackSpearman1.1] Not VR!"); return; }

            var vikingField = typeof(VikingReference).GetField("viking",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            GameObject templatePrefab = null;
            if (!ReferenceEquals(vikingField, null))
            {
                var val = vikingField.GetValue(templateVR);
                templatePrefab = val as GameObject;
                if (ReferenceEquals(templatePrefab, null) && val is Component comp) templatePrefab = comp.gameObject;
            }
            if (ReferenceEquals(templatePrefab, null) && !ReferenceEquals(templateVR.vikingClone, null))
                templatePrefab = templateVR.vikingClone.gameObject;
            if (ReferenceEquals(templatePrefab, null)) { Logger.LogError("[BlackSpearman1.1] No prefab!"); return; }

            // Clone + recolor prefab
            var clonedPrefab = Instantiate(templatePrefab);
            clonedPrefab.name = "BlackSpearman_Prefab";
            DontDestroyOnLoad(clonedPrefab);
            clonedPrefab.SetActive(false);
            int recolored = DeepRecolorToBlack(clonedPrefab.transform);
            int swordsOff = DisableSwordRenderers(clonedPrefab.transform);
            Logger.LogInfo(string.Format("[BlackSpearman1.1] Recolored: {0}, Swords off: {1}", recolored, swordsOff));

            // Clone VR GO + replace viking field
            var clonedVRGO = Instantiate(templateVR.gameObject);
            clonedVRGO.name = BlackSpearmanRefName;
            DontDestroyOnLoad(clonedVRGO);
            clonedVRGO.SetActive(false);
            var clonedVR = clonedVRGO.GetComponent<VikingReference>();
            if (ReferenceEquals(clonedVR, null)) { Destroy(clonedVRGO); return; }

            if (!ReferenceEquals(vikingField, null))
            {
                var va = clonedPrefab.GetComponent<VikingAgent>();
                vikingField.SetValue(clonedVR, !ReferenceEquals(va, null) ? (object)va : clonedPrefab);
            }
            clonedVR.bounty = Mathf.Max(templateVR.bounty + 1, 4);
        }

        private int DeepRecolorToBlack(Transform root)
        {
            int count = 0;
            var allBS = root.GetComponentsInChildren<BatchedSprite>(true);
            if (allBS == null) return 0;
            var colorProp = typeof(BatchedSprite).GetProperty("color",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ReferenceEquals(colorProp, null)) return 0;
            foreach (var bs in allBS)
            {
                if (ReferenceEquals(bs, null)) continue;
                try { var c = (Color)colorProp.GetValue(bs, null); colorProp.SetValue(bs, new Color(c.r, c.g, 0.01f, c.a), null); count++; }
                catch { }
            }
            return count;
        }

        private int DisableSwordRenderers(Transform root)
        {
            int count = 0;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var c = root.GetChild(i);
                var cn = c.name.ToLower();
                if (cn.Contains("sword") || cn.Contains("weapon") || cn.Contains("blade")
                    || cn.Contains("r_weapon") || cn.Contains("l_weapon"))
                { c.gameObject.SetActive(false); count++; continue; }
                count += DisableSwordRenderers(c);
            }
            return count;
        }
        private void ConfigureCampaignAppearance(GameObject vrGO)
        {
            try
            {
                var lr = vrGO.GetComponent<LevelRule>();
                if (!ReferenceEquals(lr, null))
                {
                    var cf = typeof(LevelRule).GetField("condition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(cf, null))
                    {
                        var cond = cf.GetValue(lr);
                        if (!ReferenceEquals(cond, null))
                        {
                            var ef = cond.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (!ReferenceEquals(ef, null)) ef.SetValue(cond, "(fraction > 0.35 && fraction < 0.8)");
                        }
                    }
                }
                var lg = vrGO.GetComponent<LevelGuessable>();
                if (!ReferenceEquals(lg, null))
                {
                    var pf = typeof(LevelGuessable).GetField("probability", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(pf, null))
                    {
                        var prob = pf.GetValue(lg);
                        if (!ReferenceEquals(prob, null))
                        {
                            var ef = prob.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (!ReferenceEquals(ef, null)) ef.SetValue(prob, "0.8");
                        }
                    }
                }
                Logger.LogInfo("[BlackSpearman1.1] Campaign appearance configured");
            }
            catch (Exception ex) { Logger.LogWarning("[BlackSpearman1.1] Campaign config: " + ex.Message); }
        }

        private void SubscribeAgentSpawned()
        {
            try
            {
                var m = typeof(Squad).GetMethod("AddAgent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(m, null))
                {
                    _harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin).GetMethod("SquadAddAgentPostfix", BindingFlags.NonPublic | BindingFlags.Static)));
                    Logger.LogInfo("[BlackSpearman1.1] Patched Squad.AddAgent");
                    return;
                }
            }
            catch (Exception ex) { Logger.LogError("[BlackSpearman1.1] Squad.AddAgent: " + ex.Message); }
        }

        private static void SquadAddAgentPostfix(Squad __instance, Agent agent) { OnAgentSpawned(agent); }

        private static void OnAgentSpawned(Agent agent)
        {
            if (ReferenceEquals(agent, null) || !agent.isViking) return;
            var va = agent.GetComponent<VikingAgent>();
            if (ReferenceEquals(va, null) || va.type != VikingAgent.Type.SwordShield) return;
            if (BlackSpearmanAgents.Contains(agent)) return;
            if (UnityEngine.Random.value > ConversionChance) return;
            ApplyBlackSpearmanMods(agent);
        }

        internal static void ApplyBlackSpearmanMods(Agent agent)
        {
            if (ReferenceEquals(agent, null) || BlackSpearmanAgents.Contains(agent)) return;
            BlackSpearmanAgents.Add(agent);
            LogInfo("[SPAWN] Converting " + agent.name);
            DeepRecolorAgentToBlack(agent);
            DisableAgentSwords(agent);
            agent.scale *= ScaleMultiplier;
            var s = agent.brain as Swordsman;
            if (!ReferenceEquals(s, null))
            {
                ScaleFloatArray(s.damageLevels, DamageMultiplier);
                ScaleFloatArray(s.knockbackLevels, KnockbackMultiplier);
            }
            ApplyArmorMod(agent);
            var charge = SpearChargeComponent.AddTo(agent);
            if (!ReferenceEquals(charge, null)) charge.Setup(agent);
            agent.gameObject.AddComponent<SpearStabAction>();
            RegisterBrainActions(agent);
        }

        private static void DeepRecolorAgentToBlack(Agent agent)
        {
            var allBS = agent.GetComponentsInChildren<BatchedSprite>(true);
            if (allBS == null) return;
            var cp = typeof(BatchedSprite).GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ReferenceEquals(cp, null)) return;
            foreach (var bs in allBS)
            {
                if (ReferenceEquals(bs, null)) continue;
                try { var c = (Color)cp.GetValue(bs, null); cp.SetValue(bs, new Color(c.r, c.g, 0.01f, c.a), null); } catch { }
            }
        }

        private static void DisableAgentSwords(Agent agent) { DisableSwordRecursive(agent.transform); }

        private static int DisableSwordRecursive(Transform root)
        {
            int c = 0;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var ch = root.GetChild(i);
                var cn = ch.name.ToLower();
                if (cn.Contains("sword") || cn.Contains("weapon") || cn.Contains("blade") || cn.Contains("r_weapon") || cn.Contains("l_weapon"))
                { ch.gameObject.SetActive(false); c++; continue; }
                c += DisableSwordRecursive(ch);
            }
            return c;
        }

        private void PatchGetAttack()
        {
            try
            {
                var m = typeof(Swordsman).GetMethod("GetAttack",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy,
                    null, new[] { typeof(Agent) }, null);
                if (!ReferenceEquals(m, null))
                {
                    _harmony.Patch(m, new HarmonyMethod(typeof(Plugin).GetMethod("GetAttackPrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    Logger.LogInfo("[BlackSpearman1.1] Patched GetAttack");
                }
            }
            catch (Exception ex) { Logger.LogError("[BlackSpearman1.1] GetAttack: " + ex.Message); }
        }

        private void PatchRangeGetter()
        {
            try
            {
                var p = typeof(Swordsman).GetProperty("range", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(p, null))
                {
                    var g = p.GetGetMethod(true);
                    if (!ReferenceEquals(g, null))
                    {
                        _harmony.Patch(g, new HarmonyMethod(typeof(Plugin).GetMethod("RangePrefix", BindingFlags.NonPublic | BindingFlags.Static)));
                    }
                }
            }
            catch (Exception ex) { Logger.LogWarning("[BlackSpearman1.1] Range: " + ex.Message); }
        }

        private static bool GetAttackPrefix(Swordsman __instance, Agent target, ref Attack __result)
        {
            if (!BlackSpearmanAgents.Contains(__instance.agent)) return true;
            if (ReferenceEquals(target, null)) { __result = default(Attack); return false; }
            try
            {
                int lv = (__instance.agent.squad != null) ? Mathf.Clamp(__instance.agent.squad.level, 0, int.MaxValue) : 0;
                float dmg = 2.5f, kb = 1.2f, st = 6f;
                var df = typeof(Swordsman).GetField("damageLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(df, null)) { var a = df.GetValue(__instance) as float[]; if (a != null && lv < a.Length) dmg = Mathf.Max(dmg, a[lv]); }
                var kf = typeof(Swordsman).GetField("knockbackLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(kf, null)) { var a = kf.GetValue(__instance) as float[]; if (a != null && lv < a.Length) kb = Mathf.Max(kb, a[lv]); }
                var sf = typeof(Swordsman).GetField("stunLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(sf, null)) { var a = sf.GetValue(__instance) as float[]; if (a != null && lv < a.Length) st = Mathf.Max(st, a[lv]); }
                Vector3 dir = (target.chestPos - __instance.agent.chestPos).normalized; dir.y = 0f;
                if (dir.sqrMagnitude < 0.001f) dir = __instance.transform.forward;
                __result = new Attack(new AttackSettings(dmg, kb, 0f, st), dir,
                    (target.wChestPos + __instance.agent.wChestPos) / 2f, __instance, __instance.agent.squad, "Sfx/English/Spear");
                return false;
            }
            catch (Exception ex) { LogErr("[ATTACK] " + ex.Message); return true; }
        }

        private static bool RangePrefix(Swordsman __instance, ref float __result)
        {
            if (!BlackSpearmanAgents.Contains(__instance.agent)) return true;
            __result = __instance.agent.radius * 0.7f * RangeMultiplier;
            return false;
        }

        private static void RegisterBrainActions(Agent agent)
        {
            try
            {
                var s = agent.brain as Swordsman;
                if (ReferenceEquals(s, null)) return;
                var af = typeof(Brain).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(af, null)) return;
                var actions = af.GetValue(s) as System.Collections.IList;
                if (ReferenceEquals(actions, null)) return;
                var charge = agent.GetComponent<SpearChargeComponent>();
                if (!ReferenceEquals(charge, null) && !actions.Contains(charge)) actions.Add(charge);
                var stab = agent.GetComponent<SpearStabAction>();
                if (!ReferenceEquals(stab, null) && !actions.Contains(stab)) actions.Add(stab);
            }
            catch (Exception ex) { LogErr("[BRAIN] " + ex.Message); }
        }

        private static void ApplyArmorMod(Agent agent)
        {
            var a = agent.GetComponent<Armor>();
            if (ReferenceEquals(a, null)) return;
            var af = typeof(Armor).GetField("armor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ReferenceEquals(af, null)) return;
            var orig = af.GetValue(a) as float[];
            if (ReferenceEquals(orig, null)) return;
            var copy = new float[orig.Length];
            Array.Copy(orig, copy, orig.Length);
            for (int i = 0; i < copy.Length; i++) copy[i] *= ArmorMultiplier;
            af.SetValue(a, copy);
        }

        private static void ScaleFloatArray(float[] arr, float m)
        { if (arr == null) return; for (int i = 0; i < arr.Length; i++) arr[i] *= m; }

        private static void DumpDictKeys()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var k in LevelStateObjectReferences.dict.Keys) { if (sb.Length > 0) sb.Append(", "); sb.Append(k); }
                LogInfo("[DIAG] Dict keys: [" + sb.ToString() + "]");
            }
            catch (Exception ex) { LogErr("[DIAG] " + ex.Message); }
        }

        internal static void LogInfo(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogInfo("[BS1.1] " + msg); }
        internal static void LogWarn(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogWarning("[BS1.1] " + msg); }
        internal static void LogErr(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogError("[BS1.1] " + msg); }

        private void OnDestroy()
        {
            try { On.Voxels.TowerDefense.GameSetup.Awake -= OnGameSetupAwake; } catch { }
        }
    }
}