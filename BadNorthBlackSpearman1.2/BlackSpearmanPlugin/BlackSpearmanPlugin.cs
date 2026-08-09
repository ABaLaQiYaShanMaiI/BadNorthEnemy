using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.RaidGeneration;
using Voxels.TowerDefense.SpriteMagic;
using BadNorthBlackSpearman1_1;
using Voxels.TowerDefense.CampaignGeneration.CampaignAc3;

namespace BadNorthBlackSpearman1_2
{
    [BepInPlugin("badnorth.blackspearman.v1.2", "BlackSpearman v1.2", "1.2.0")]
    public class BlackSpearmanPlugin : BaseUnityPlugin
    {
        public static BlackSpearmanPlugin Instance;
        public static ManualLogSource Log;
        const string SRC_VR = "Viking_SwordShield";
        const string NEW_VR = "Viking_BlackSpearman";
        const int NEW_TYPE = 8;
        const int NEW_BOUNTY = 8;
        const float DMG = 1.6f, KB = 2.5f, SCL = 1.05f, RNG = 3.5f;
        static HashSet<Agent> _done = new HashSet<Agent>();
        bool _vrRegistered;
        Harmony _h;

        void Awake()
        {
            Instance = this; Log = Logger;
            _h = new Harmony("badnorth.blackspearman.v1.2");
            On.Voxels.TowerDefense.GameSetup.Awake += OnGameSetupAwake;
            On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn += OnLandingSpawn;
            PatchCombat();
            Log.LogInfo("[BS v1.2] Ready");
        }

        void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake orig, GameSetup self)
        {
            orig(self);
            if (_vrRegistered) return;
            if (!LevelStateObjectReferences.dict.TryGetValue(SRC_VR, out var s)) return;
            var src = s as VikingReference; if (src == null) return;
            try
            {
                var go = Instantiate(src.gameObject);
                DontDestroyOnLoad(go); go.name = NEW_VR;
                var vr = go.GetComponent<VikingReference>();
                vr.type = (VikingAgent.Type)NEW_TYPE; vr.bounty = NEW_BOUNTY;
                SetRuleCondition(go);
                SetGuessableProb(go);
                LevelStateObjectReferences.dict[NEW_VR] = vr;
                _vrRegistered = true;
                Log.LogInfo($"[BS] Registered {NEW_VR}");
                StartCoroutine(ModPrefab(vr));
            }
            catch (Exception e) { Log.LogError($"[BS] VR: {e}"); }
        }

        IEnumerator ModPrefab(VikingReference vr)
        {
            yield return new WaitForSeconds(2f);
            var c = vr?.vikingClone; if (c == null) yield break;
            if (c.agent != null) c.agent.scale *= SCL;
            var sw = c.GetComponent<Swordsman>();
            if (sw != null) { ScaleArr(sw.damageLevels, DMG); ScaleArr(sw.knockbackLevels, KB); }
            Recolor(c.transform);
        }

        static Longship OnLandingSpawn(On.Voxels.TowerDefense.RaidGeneration.Landing.orig_Spawn orig, Landing self)
        {
            var ship = orig(self); if (ship?.agents == null) return ship;
            foreach (var a in ship.agents)
            {
                if (a == null) continue;
                var va = a.GetComponent<VikingAgent>();
                if (va == null || (int)va.type != NEW_TYPE || !_done.Add(a)) continue;
                var ch = a.gameObject.AddComponent<SpearChargeComponent>(); ch?.Setup(a);
                a.gameObject.AddComponent<SpearStabAction>();
                var br = a.brain as Swordsman;
                if (br != null)
                {
                    var af = typeof(Brain).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (af?.GetValue(br) is System.Collections.IList acts)
                    {
                        if (ch != null && !acts.Contains(ch)) acts.Add(ch);
                        var st = a.GetComponent<SpearStabAction>();
                        if (st != null && !acts.Contains(st)) acts.Add(st);
                    }
                }
            }
            return ship;
        }

        void PatchCombat()
        {
            var ga = typeof(Swordsman).GetMethod("GetAttack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Agent) }, null);
            if (ga != null) _h.Patch(ga, prefix: new HarmonyMethod(typeof(BlackSpearmanPlugin).GetMethod("GetAttack_Pre", BindingFlags.NonPublic | BindingFlags.Static)));
            var rp = typeof(Swordsman).GetProperty("range", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (rp?.GetGetMethod(true) is MethodInfo rg) _h.Patch(rg, prefix: new HarmonyMethod(typeof(BlackSpearmanPlugin).GetMethod("Range_Pre", BindingFlags.NonPublic | BindingFlags.Static)));
        }
        static bool GetAttack_Pre(Swordsman s, Agent t, ref Attack r)
        {
            if (!_done.Contains(s.agent)) return true;
            int lv = s.agent.squad?.level ?? 0;
            float d = 2.5f, k = 1.2f, st = 6f;
            if (lv < s.damageLevels.Length) d = Mathf.Max(d, s.damageLevels[lv]);
            if (lv < s.knockbackLevels.Length) k = Mathf.Max(k, s.knockbackLevels[lv]);
            if (lv < s.stunLevels.Length) st = Mathf.Max(st, s.stunLevels[lv]);
            var dir = (t.chestPos - s.agent.chestPos).normalized;
            dir.y = 0f; if (dir.sqrMagnitude < 0.001f) dir = s.transform.forward;
            r = new Attack(new AttackSettings(d, k, 0f, st), dir, (t.wChestPos + s.agent.wChestPos) / 2f, s, s.agent.squad, "Sfx/English/Spear");
            return false;
        }
        static bool Range_Pre(Swordsman s, ref float r) { if (!_done.Contains(s.agent)) return true; r = s.agent.radius * 0.7f * RNG; return false; }
        static void ScaleArr(float[] a, float m) { if (a != null) for (int i = 0; i < a.Length; i++) a[i] *= m; }

        static void Recolor(Transform t)
        {
            var cp = typeof(BatchedSprite).GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (cp == null) return;
            foreach (var bs in t.GetComponentsInChildren<BatchedSprite>(true))
            { if (bs == null) continue; try { var c = (Color)cp.GetValue(bs, null); cp.SetValue(bs, new Color(c.r, c.g, 0.01f, c.a), null); } catch { } }
        }

        static void SetRuleCondition(GameObject go)
        {
            var rule = go.GetComponent<LevelRule>(); if (rule == null) return;
            var cf = typeof(LevelRule).GetField("condition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (cf == null) return;
            var cond = cf.GetValue(rule); if (cond == null) return;
            var ef = cond.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ef != null) ef.SetValue(cond, "(fraction > 0.10 && fraction < 0.50) || (fraction > 0.65 && fraction < 0.95)");
        }

        static void SetGuessableProb(GameObject go)
        {
            var guess = go.GetComponent<LevelGuessable>(); if (guess == null) return;
            if (!LevelStateObjectReferences.dict.TryGetValue("Viking_AxeThrower", out var axe) || !(axe is VikingReference a)) return;
            var ag = a.GetComponent<LevelGuessable>(); if (ag == null) return;
            var pf = typeof(LevelGuessable).GetField("probability", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pf != null) pf.SetValue(guess, pf.GetValue(ag));
        }
    }

    public static class PluginShim
    {
        public static void LogI(string m) => BlackSpearmanPlugin.Log?.LogInfo(m);
        public static void LogE(string m) => BlackSpearmanPlugin.Log?.LogError(m);
        public static void LogW(string m) => BlackSpearmanPlugin.Log?.LogWarning(m);
    }
}

namespace BadNorthBlackSpearman1_1
{
    public static class Plugin
    {
        public static void LogI(string m) => BadNorthBlackSpearman1_2.PluginShim.LogI(m);
        public static void LogE(string m) => BadNorthBlackSpearman1_2.PluginShim.LogE(m);
        public static void LogW(string m) => BadNorthBlackSpearman1_2.PluginShim.LogW(m);
    }
}