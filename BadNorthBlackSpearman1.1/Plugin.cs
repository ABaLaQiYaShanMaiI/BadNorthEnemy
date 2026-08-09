using System;
using System.Collections;
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

        internal const string NewKey = "Viking_BlackSpearman";
        internal const string TplKey = "Viking_SwordShield";
        internal const float DmgM = 1.6f, KbM = 2.5f, ArmM = 1.3f, ScM = 1.05f, RngM = 3.5f;
        internal static readonly HashSet<Agent> BlackAgents = new HashSet<Agent>();

        private Harmony _h;
        private bool _reg, _waiting, _patchesOk;
        private int _spn, _mod, _atk, _rng;
        private const int MaxRetry = 120;

        private void Awake()
        {
            Instance = this; SharedLogger = Logger;
            LogB("v1.1 NEW ENEMY TYPE — Awake");
            _h = new Harmony("black.spearman.v1.1");
            try { On.Voxels.TowerDefense.GameSetup.Awake += OnAwake; LogOK("MMHOOK"); }
            catch (Exception e) { LogFL("MMHOOK", e); }
            PatchAtk(); PatchRng();
        }

        private void Start()
        {
            LogI("Target: '" + NewKey + "' from '" + TplKey + "'");
            LogI("Stats: Dmg=x" + DmgM + " KB=x" + KbM + " Armor=x" + ArmM + " Scale=x" + ScM + " Range=x" + RngM);
            SubSpawn();
            InvokeRepeating("Beat", 20f, 60f);
            LogB("Ready");
        }

        private void Beat()
        {
            int a = 0;
            foreach (var x in BlackAgents) if (!ReferenceEquals(x, null) && x != null && !ReferenceEquals(x.aliveState, null) && x.aliveState.active) a++;
            LogI("[BEAT] Reg=" + _reg + " Patches=" + _patchesOk + " Spn=" + _spn + " Mod=" + _mod + " Alive=" + a + " Atk=" + _atk + " Rng=" + _rng);
        }
        // ====== REGISTRATION ======

        private void OnAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake o, GameSetup s)
        {
            o(s);
            // 防止多个协程同时运行
            if (_reg || _waiting) return;
            _waiting = true;
            StartCoroutine(WaitAndRegister());
        }

        private IEnumerator WaitAndRegister()
        {
            for (int i = 1; i <= MaxRetry; i++)
            {
                yield return new WaitForSeconds(0.5f);
                if (_reg) { _waiting = false; yield break; }
                try
                {
                    int n = LevelStateObjectReferences.dict.Count;
                    if (i <= 3 || i % 20 == 0) LogI("[WAIT] " + i + "/" + MaxRetry + " dict=" + n);
                    if (n > 0 && LevelStateObjectReferences.dict.ContainsKey(TplKey))
                    {
                        LogI("[WAIT] Dict ready (" + n + " entries)!");
                        Register();
                        _waiting = false;
                        yield break;
                    }
                }
                catch (Exception e) { LogE("[WAIT] " + e.Message); }
            }
            _waiting = false;
            LogFL("Dict never populated after " + MaxRetry + " attempts", null);
        }

        private void Register()
        {
            if (_reg) return;
            _reg = true;
            LogB("REGISTRATION");
            DumpD();

            if (LevelStateObjectReferences.dict.ContainsKey(NewKey))
            { LogI("[REG] Already in dict"); InsVR(NewKey); return; }

            LogI("[REG] Step1: template '" + TplKey + "'");
            UnityEngine.Object obj;
            if (!LevelStateObjectReferences.dict.TryGetValue(TplKey, out obj))
            { LogFL("Template not found!", null); DumpD(); return; }
            var tpl = obj as VikingReference;
            if (ReferenceEquals(tpl, null)) { LogFL("Not VR!", null); return; }
            InsVR(tpl, "TEMPLATE");

            LogI("[REG] Step2: clone prefab");
            var vf = typeof(VikingReference).GetField("viking", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            GameObject pf = null;
            if (!ReferenceEquals(vf, null))
            { var v = vf.GetValue(tpl); pf = v as GameObject; if (ReferenceEquals(pf, null) && v is Component c) pf = c.gameObject; }
            if (ReferenceEquals(pf, null) && !ReferenceEquals(tpl.vikingClone, null))
            { pf = tpl.vikingClone.gameObject; }
            if (ReferenceEquals(pf, null)) { LogFL("No prefab!", null); return; }
            DmpH(pf.transform, 3);

            LogI("[REG] Step3: clone+recolor");
            var blk = Instantiate(pf); blk.name = "BlackSpearman_Prefab";
            DontDestroyOnLoad(blk); blk.SetActive(false);
            int rc = Recolor(blk.transform);
            int sw = NoSwords(blk.transform);
            LogI("[REG] Recolored=" + rc + " SwordsOff=" + sw);

            LogI("[REG] Step4: clone VR GO");
            var vg = Instantiate(tpl.gameObject); vg.name = NewKey;
            DontDestroyOnLoad(vg); vg.SetActive(false);
            var nv = vg.GetComponent<VikingReference>();
            if (ReferenceEquals(nv, null)) { LogFL("No VR!", null); Destroy(vg); return; }
            if (!ReferenceEquals(vf, null))
            { var va = blk.GetComponent<VikingAgent>(); vf.SetValue(nv, !ReferenceEquals(va, null) ? (object)va : blk); }
            nv.bounty = Mathf.Max(tpl.bounty + 1, 4);
            var vcf = typeof(VikingReference).GetField("vikingClone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(vcf, null)) vcf.SetValue(nv, null);
            nv.SendMessage("Start", SendMessageOptions.DontRequireReceiver);

            LogI("[REG] Step5: dict['" + NewKey + "']=" + nv.name);
            LevelStateObjectReferences.dict[NewKey] = nv;

            LogI("[REG] Step6: campaign config");
            CfgCampaign(vg);
            InsVR(nv, "REGISTERED");
            DumpD();
            LogB("REGISTERED: " + NewKey);
        }

        private int Recolor(Transform r)
        {
            var all = r.GetComponentsInChildren<BatchedSprite>(true);
            if (all == null || all.Length == 0) return 0;
            var cp = typeof(BatchedSprite).GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ReferenceEquals(cp, null)) return 0;
            int c = 0;
            foreach (var bs in all)
            { if (ReferenceEquals(bs, null)) continue; try { var o = (Color)cp.GetValue(bs, null); cp.SetValue(bs, new Color(o.r, o.g, 0.01f, o.a), null); c++; } catch { } }
            return c;
        }

        private int NoSwords(Transform r)
        {
            int c = 0;
            for (int i = r.childCount - 1; i >= 0; i--)
            { var ch = r.GetChild(i); var cn = ch.name.ToLower(); if (cn.Contains("sword") || cn.Contains("weapon") || cn.Contains("blade") || cn.Contains("r_weapon") || cn.Contains("l_weapon")) { ch.gameObject.SetActive(false); c++; continue; } c += NoSwords(ch); }
            return c;
        }

        private void CfgCampaign(GameObject g)
        {
            try
            {
                var lr = g.GetComponent<LevelRule>(); var lg = g.GetComponent<LevelGuessable>();
                if (!ReferenceEquals(lr, null))
                { var cf = typeof(LevelRule).GetField("condition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(cf, null)) { var co = cf.GetValue(lr); if (!ReferenceEquals(co, null)) { var ef = co.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(ef, null)) ef.SetValue(co, "(fraction > 0.3 && fraction < 0.85)"); } } }
                if (!ReferenceEquals(lg, null))
                { var pf = typeof(LevelGuessable).GetField("probability", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(pf, null)) { var pr = pf.GetValue(lg); if (!ReferenceEquals(pr, null)) { var ef = pr.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(ef, null)) ef.SetValue(pr, "0.9"); } } }
                LogI("[CAMPAIGN] LR=" + !ReferenceEquals(lr, null) + " LG=" + !ReferenceEquals(lg, null));
            }
            catch (Exception e) { LogE("[CAMPAIGN] " + e); }
        }

        // ====== SPAWN (polling) ======

        private void SubSpawn()
        { InvokeRepeating("Poll", 3f, 2f); LogOK("Poll every 2s"); }

        private void Poll()
        {
            try
            {
                var all = FindObjectsOfType<Agent>();
                if (all == null) return;
                foreach (var a in all)
                {
                    if (ReferenceEquals(a, null) || !a.isViking) continue;
                    if (BlackAgents.Contains(a)) continue;
                    if (!IsBlack(a)) continue;
                    _spn++;
                    LogB("FOUND: " + a.name + " #" + _spn);
                    ApplyStats(a);
                }
            }
            catch (Exception e) { LogE("[POLL] " + e.Message); }
        }

        private static bool IsBlack(Agent a)
        {
            try
            {
                var all = a.GetComponentsInChildren<BatchedSprite>(true);
                if (all == null || all.Length == 0) return false;
                var cp = typeof(BatchedSprite).GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(cp, null)) return false;
                int bc = 0;
                foreach (var bs in all)
                { if (ReferenceEquals(bs, null)) continue; try { var c = (Color)cp.GetValue(bs, null); if (c.b < 0.05f) bc++; } catch { } }
                return (float)bc / all.Length > 0.5f;
            }
            catch { return false; }
        }

        internal static void ApplyStats(Agent a)
        {
            if (ReferenceEquals(a, null) || BlackAgents.Contains(a)) return;
            BlackAgents.Add(a); Instance._mod++;
            LogI("[MOD] " + a.name + " scale " + a.scale + "->" + (a.scale * ScM));
            a.scale *= ScM;
            var s = a.brain as Swordsman;
            if (!ReferenceEquals(s, null)) { SclArr(s.damageLevels, DmgM); SclArr(s.knockbackLevels, KbM); }
            var ar = a.GetComponent<Armor>();
            if (!ReferenceEquals(ar, null))
            {
                var af = typeof(Armor).GetField("armor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(af, null))
                { var o = af.GetValue(ar) as float[]; if (!ReferenceEquals(o, null)) { var cp = new float[o.Length]; Array.Copy(o, cp, o.Length); for (int i = 0; i < cp.Length; i++) cp[i] *= ArmM; af.SetValue(ar, cp); } }
            }
            var ch = SpearChargeComponent.AddTo(a); if (!ReferenceEquals(ch, null)) ch.Setup(a);
            a.gameObject.AddComponent<SpearStabAction>();
            RegBA(a);
            LogB("READY #" + Instance._mod + ": " + a.name);
        }

        private static void SclArr(float[] ar, float m) { if (ar == null) return; for (int i = 0; i < ar.Length; i++) ar[i] *= m; }

        private static void RegBA(Agent a)
        {
            try
            {
                var s = a.brain as Swordsman; if (ReferenceEquals(s, null)) return;
                var af = typeof(Brain).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (ReferenceEquals(af, null)) return;
                var acts = af.GetValue(s) as System.Collections.IList; if (ReferenceEquals(acts, null)) return;
                var ch = a.GetComponent<SpearChargeComponent>(); if (!ReferenceEquals(ch, null) && !acts.Contains(ch)) acts.Add(ch);
                var st = a.GetComponent<SpearStabAction>(); if (!ReferenceEquals(st, null) && !acts.Contains(st)) acts.Add(st);
            }
            catch (Exception e) { LogE("[BRAIN] " + e); }
        }

        // ====== ATTACK (__instance name!!) ======

        private void PatchAtk()
        {
            try
            {
                var m = typeof(Swordsman).GetMethod("GetAttack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy, null, new[] { typeof(Agent) }, null);
                if (!ReferenceEquals(m, null)) { _h.Patch(m, new HarmonyMethod(typeof(Plugin).GetMethod("AtkPre", BindingFlags.NonPublic | BindingFlags.Static))); _patchesOk = true; LogOK("GetAttack"); }
                else LogW("GetAttack not found!");
            }
            catch (Exception e) { LogFL("GetAttack", e); }
        }

        private void PatchRng()
        {
            try
            {
                var p = typeof(Swordsman).GetProperty("range", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(p, null)) { var g = p.GetGetMethod(true); if (!ReferenceEquals(g, null)) { _h.Patch(g, new HarmonyMethod(typeof(Plugin).GetMethod("RngPre", BindingFlags.NonPublic | BindingFlags.Static))); LogOK("range"); } }
            }
            catch (Exception e) { LogW("[RANGE] " + e); }
        }

        private static bool AtkPre(Swordsman __instance, Agent target, ref Attack __result)
        {
            if (!BlackAgents.Contains(__instance.agent)) return true;
            if (ReferenceEquals(target, null)) { __result = default(Attack); return false; }
            Instance._atk++; bool dbg = Instance._atk <= 3 || Instance._atk % 30 == 0;
            try
            {
                int lv = __instance.agent.squad != null ? __instance.agent.squad.level : 0;
                float d = 2.5f, k = 1.2f, s = 6f;
                var df = typeof(Swordsman).GetField("damageLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(df, null)) { var a = df.GetValue(__instance) as float[]; if (a != null && lv < a.Length) d = Mathf.Max(d, a[lv]); }
                var kf = typeof(Swordsman).GetField("knockbackLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(kf, null)) { var a = kf.GetValue(__instance) as float[]; if (a != null && lv < a.Length) k = Mathf.Max(k, a[lv]); }
                var sf = typeof(Swordsman).GetField("stunLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(sf, null)) { var a = sf.GetValue(__instance) as float[]; if (a != null && lv < a.Length) s = Mathf.Max(s, a[lv]); }
                Vector3 dir = (target.chestPos - __instance.agent.chestPos).normalized; dir.y = 0f; if (dir.sqrMagnitude < 0.001f) dir = __instance.transform.forward;
                __result = new Attack(new AttackSettings(d, k, 0f, s), dir, (target.wChestPos + __instance.agent.wChestPos) / 2f, __instance, __instance.agent.squad, "Sfx/English/Spear");
                if (dbg) LogI("[ATK#" + Instance._atk + "] t=" + target.name + " d=" + d.ToString("F1") + " k=" + k.ToString("F1") + " s=" + s.ToString("F0"));
                return false;
            }
            catch (Exception e) { LogE("[ATK] " + e); return true; }
        }

        private static bool RngPre(Swordsman __instance, ref float __result)
        {
            if (!BlackAgents.Contains(__instance.agent)) return true;
            Instance._rng++; __result = __instance.agent.radius * 0.7f * RngM;
            return false;
        }

        // ====== DIAG (NO string.Join - .NET 3.5 compat) ======

        private void DumpD()
        {
            try
            {
                string s = ""; int n = 0;
                foreach (var k in LevelStateObjectReferences.dict.Keys)
                {
                    if (s.Length > 0) s += " | ";
                    var o = LevelStateObjectReferences.dict[k]; var vr = o as VikingReference;
                    string vi = ""; if (!ReferenceEquals(vr, null)) vi = " b=" + vr.bounty + " t=" + vr.type;
                    s += k + "(" + (o != null ? o.GetType().Name : "NULL") + vi + ")"; n++;
                }
                LogI("[DICT] " + n + ": [" + s + "]");
            }
            catch (Exception e) { LogE("[DICT] " + e); }
        }

        private void InsVR(VikingReference vr, string l)
        {
            if (ReferenceEquals(vr, null)) { LogI("[VR:" + l + "] NULL"); return; }
            try
            {
                var vf = typeof(VikingReference).GetField("viking", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                string vv = "?"; if (!ReferenceEquals(vf, null)) { var v = vf.GetValue(vr); vv = v != null ? v.GetType().Name : "NULL"; }
                LogI("[VR:" + l + "] nm=" + vr.name + " b=" + vr.bounty + " t=" + vr.type + " v=" + vv + " vc=" + (vr.vikingClone != null ? vr.vikingClone.name : "NULL"));
            }
            catch (Exception e) { LogE("[VR:" + l + "] " + e); }
        }

        private void InsVR(string k)
        { if (!LevelStateObjectReferences.dict.ContainsKey(k)) { LogI("[VR] '" + k + "' not in dict"); return; } InsVR(LevelStateObjectReferences.dict[k] as VikingReference, k); }

        private void DmpH(Transform t, int md)
        {
            if (ReferenceEquals(t, null)) return;
            string s = ""; DmpTx(t, "", 0, md, ref s);
            LogI("[HIER] " + t.name + ":\n" + s);
        }

        private void DmpTx(Transform t, string ind, int d, int md, ref string s)
        {
            if (ReferenceEquals(t, null) || d > md) return;
            var comps = t.GetComponents<Component>();
            string cn = "";
            foreach (var c in comps)
            {
                if (c == null) continue;
                if (cn.Length > 0) cn += ", ";
                cn += c.GetType().Name;
                if (c is BatchedSprite) cn += "(BS)";
                else if (c is SpriteAnimator) cn += "(SA)";
            }
            s += ind + "[" + d + "] " + t.name + (t.gameObject.activeSelf ? "" : " OFF") + " | " + cn + "\n";
            for (int i = 0; i < t.childCount; i++) DmpTx(t.GetChild(i), ind + "  ", d + 1, md, ref s);
        }

        // ====== LOG ======

        private static void LogB(string m) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogInfo("[BS1.1] ======== " + m + " ========"); }
        private static void LogOK(string m) { LogI("[OK] " + m); }
        private static void LogFL(string c, Exception e) { if (e != null) SharedLogger.LogError("[BS1.1] [FAIL:" + c + "] " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); else SharedLogger.LogError("[BS1.1] [FAIL:" + c + "]"); }
        internal static void LogI(string m) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogInfo("[BS1.1] " + m); }
        internal static void LogW(string m) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogWarning("[BS1.1] " + m); }
        internal static void LogE(string m) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogError("[BS1.1] " + m); }

        private void OnDestroy()
        {
            try { On.Voxels.TowerDefense.GameSetup.Awake -= OnAwake; } catch { }
            CancelInvoke();
            int a = 0; foreach (var x in BlackAgents) if (!ReferenceEquals(x, null) && x != null && !ReferenceEquals(x.aliveState, null) && x.aliveState.active) a++;
            LogB("SHUTDOWN: Spn=" + _spn + " Mod=" + _mod + " Atk=" + _atk + " Alive=" + a);
        }
    }
}
