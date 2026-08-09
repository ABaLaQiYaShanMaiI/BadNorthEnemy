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
        private Harmony _h; private bool _reg, _waiting, _patchesOk, _colorDiagDone;
        private int _spn, _mod, _atk, _rng, _pollCount;
        private const int MaxRetry = 120;

        private void Awake()
        {
            Instance = this; SharedLogger = Logger;
            LogB("v1.1 SPY EDITION");
            _h = new Harmony("black.spearman.v1.1");
            try { On.Voxels.TowerDefense.GameSetup.Awake += OnAwake; LogOK("MMHOOK"); }
            catch (Exception e) { LogFL("MMHOOK", e); }
            PatchAtk(); PatchRng(); SetupSpies();
        }
        private void Start() { SubSpawn(); InvokeRepeating("Beat", 20f, 60f); LogB("Ready"); }
        private void Beat() { int a = 0; foreach (var x in BlackAgents) if (!ReferenceEquals(x, null) && x != null && !ReferenceEquals(x.aliveState, null) && x.aliveState.active) a++; LogI("[BEAT] Reg=" + _reg + " Pty=" + _patchesOk + " Spn=" + _spn + " Mod=" + _mod + " Alive=" + a + " Atk=" + _atk); }


        // ====== SPIES ======
        private void SetupSpies()
        {
            // Spy 1: LevelNode.Setup
            try
            {
                var m = typeof(LevelNode).GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(m, null))
                    _h.Patch(m, postfix: new HarmonyMethod(typeof(Plugin).GetMethod("SpyLevel", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { LogW("[SPY] LevelNode: " + e.Message); }
            // Spy 2: Raid generation
            try
            {
                var m = typeof(IslandGameplayManager).GetMethod("PlayIslandRoutine", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (!ReferenceEquals(m, null))
                    _h.Patch(m, postfix: new HarmonyMethod(typeof(Plugin).GetMethod("SpyIsland", BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch (Exception e) { LogW("[SPY] Island: " + e.Message); }
            LogOK("Spies set up");
        }

        private static void SpyLevel(LevelNode __instance)
        {
            try
            {
                if (ReferenceEquals(__instance, null)) return;
                var ef = typeof(LevelNode).GetField("enemies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(ef, null)) return;
                var enemies = ef.GetValue(__instance) as System.Collections.IList;
                if (enemies == null) return;

                // 诊断：当前敌人列表
                string s = ""; bool hasUs = false;
                foreach (var e in enemies)
                {
                    if (s.Length > 0) s += ", ";
                    if (e is UnityEngine.Object uo) { s += uo.name; if (uo.name == NewKey) hasUs = true; }
                    else s += (e != null ? e.GetType().Name : "NULL");
                }
                LogI("[SPY:LEVEL] " + __instance.name + " enemies(" + enemies.Count + "): [" + s + "]");

                // ===== 注入黑矛兵！ =====
                if (!hasUs && LevelStateObjectReferences.dict.ContainsKey(NewKey))
                {
                    var ourVR = LevelStateObjectReferences.dict[NewKey] as VikingReference;
                    if (!ReferenceEquals(ourVR, null) && !enemies.Contains(ourVR))
                    {
                        enemies.Add(ourVR);
                        LogB("[INJECT] Added " + NewKey + " to " + __instance.name + "! (now " + enemies.Count + " enemies)");
                    }
                }
                else if (hasUs) LogB("[SPY:LEVEL] Already has BlackSpearman");
            }
            catch (Exception ex) { LogE("[SPY:LEVEL] " + ex.Message); }
        }

        private static void SpyIsland(IslandGameplayManager __instance)
        {
            try
            {
                if (ReferenceEquals(__instance, null) || ReferenceEquals(__instance.island, null)) return;
                var ln = __instance.island.levelNode;
                if (ReferenceEquals(ln, null)) return;
                var ef = typeof(LevelNode).GetField("enemies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(ef, null)) return;
                var enemies = ef.GetValue(ln) as System.Collections.IList;
                if (enemies == null) return;

                string s = ""; bool hasUs = false;
                foreach (var e in enemies)
                { if (s.Length > 0) s += ", "; if (e is UnityEngine.Object uo) { s += uo.name; if (uo.name == NewKey) hasUs = true; } else s += (e != null ? e.GetType().Name : "NULL"); }
                LogB("[ISLAND] " + ln.name + " (" + enemies.Count + "): [" + s + "]");

                // 注入！此时注册一定已完成
                if (!hasUs && LevelStateObjectReferences.dict.ContainsKey(NewKey))
                {
                    var vr = LevelStateObjectReferences.dict[NewKey] as VikingReference;
                    if (!ReferenceEquals(vr, null) && !enemies.Contains(vr))
                    { enemies.Add(vr); LogB("[INJECT] Added BlackSpearman!"); }
                }
            }
            catch (Exception ex) { LogE("[ISLAND] " + ex.Message); }
        }


        // ====== REGISTRATION ======
        private void OnAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake o, GameSetup s) { o(s); if (_reg || _waiting) return; _waiting = true; StartCoroutine(WaitAndReg()); }
        private IEnumerator WaitAndReg() { for (int i = 1; i <= MaxRetry; i++) { yield return new WaitForSeconds(0.5f); if (_reg) { _waiting = false; yield break; } try { int n = LevelStateObjectReferences.dict.Count; if (i <= 3 || i % 20 == 0) LogI("[WAIT] " + i + "/" + MaxRetry + " dict=" + n); if (n > 0 && LevelStateObjectReferences.dict.ContainsKey(TplKey)) { LogI("[WAIT] Ready! (" + n + ")"); Reg(); _waiting = false; yield break; } } catch (Exception e) { LogE("[WAIT] " + e.Message); } } _waiting = false; LogFL("Dict never populated", null); }

        private void Reg()
        {
            if (_reg) return; _reg = true; LogB("REG"); DumpD();
            if (LevelStateObjectReferences.dict.ContainsKey(NewKey)) { LogI("[REG] Already in dict"); InsVR(NewKey); return; }
            UnityEngine.Object obj; if (!LevelStateObjectReferences.dict.TryGetValue(TplKey, out obj)) { LogFL("No tpl!", null); return; }
            var tpl = obj as VikingReference; if (ReferenceEquals(tpl, null)) { LogFL("Not VR!", null); return; }
            InsVR(tpl, "TEMPLATE");
            var vf = typeof(VikingReference).GetField("viking", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            GameObject pf = null; if (!ReferenceEquals(vf, null)) { var v = vf.GetValue(tpl); pf = v as GameObject; if (ReferenceEquals(pf, null) && v is Component c) pf = c.gameObject; }
            if (ReferenceEquals(pf, null) && !ReferenceEquals(tpl.vikingClone, null)) pf = tpl.vikingClone.gameObject;
            if (ReferenceEquals(pf, null)) { LogFL("No prefab!", null); return; }
            var blk = Instantiate(pf); blk.name = "BlackSpearman_Prefab"; DontDestroyOnLoad(blk); blk.SetActive(false);
            int rc = Recolor(blk.transform); int sw = NoSwords(blk.transform); LogI("[REG] Recolored=" + rc + " SwordsOff=" + sw);
            var vg = Instantiate(tpl.gameObject); vg.name = NewKey; DontDestroyOnLoad(vg);
            var nv = vg.GetComponent<VikingReference>(); if (ReferenceEquals(nv, null)) { LogFL("No VR!", null); Destroy(vg); return; }
            if (!ReferenceEquals(vf, null)) { var va = blk.GetComponent<VikingAgent>(); vf.SetValue(nv, !ReferenceEquals(va, null) ? (object)va : blk); }
            nv.bounty = Mathf.Max(tpl.bounty + 1, 4);
            vg.SetActive(true); var vcf = typeof(VikingReference).GetField("vikingClone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(vcf, null)) vcf.SetValue(nv, null); nv.SendMessage("Start", SendMessageOptions.DontRequireReceiver); vg.SetActive(false);
            LevelStateObjectReferences.dict[NewKey] = nv;
            CfgCampaign(vg); InsVR(nv, "REGISTERED"); DumpD(); LogB("REGISTERED: " + NewKey);
        }

        private int Recolor(Transform r) { var all = r.GetComponentsInChildren<BatchedSprite>(true); if (all == null || all.Length == 0) return 0; var cp = typeof(BatchedSprite).GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (ReferenceEquals(cp, null)) return 0; int c = 0; foreach (var bs in all) { if (ReferenceEquals(bs, null)) continue; try { var o = (Color)cp.GetValue(bs, null); cp.SetValue(bs, new Color(o.r, o.g, 0.01f, o.a), null); c++; } catch { } } return c; }
        private int NoSwords(Transform r) { int c = 0; for (int i = r.childCount - 1; i >= 0; i--) { var ch = r.GetChild(i); var cn = ch.name.ToLower(); if (cn.Contains("sword") || cn.Contains("weapon") || cn.Contains("blade") || cn.Contains("r_weapon") || cn.Contains("l_weapon")) { ch.gameObject.SetActive(false); c++; continue; } c += NoSwords(ch); } return c; }
        private void CfgCampaign(GameObject g) { try { var lr = g.GetComponent<LevelRule>(); var lg = g.GetComponent<LevelGuessable>(); if (!ReferenceEquals(lr, null)) { var cf = typeof(LevelRule).GetField("condition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(cf, null)) { var co = cf.GetValue(lr); if (!ReferenceEquals(co, null)) { var ef = co.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(ef, null)) ef.SetValue(co, "true"); } } } if (!ReferenceEquals(lg, null)) { var pf = typeof(LevelGuessable).GetField("probability", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(pf, null)) { var pr = pf.GetValue(lg); if (!ReferenceEquals(pr, null)) { var ef = pr.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(ef, null)) ef.SetValue(pr, "1"); } } } LogI("[CAMPAIGN] LR=" + !ReferenceEquals(lr, null) + " LG=" + !ReferenceEquals(lg, null)); } catch (Exception e) { LogE("[CAMPAIGN] " + e); } }


        // ====== POLL + COLOR DIAG ======
        private void SubSpawn() { InvokeRepeating("Poll", 3f, 2f); LogOK("Poll every 2s"); }
        private void Poll()
        {
            _pollCount++;
            try
            {
                var all = FindObjectsOfType<Agent>();
                if (all == null) { if (_pollCount <= 3) LogI("[POLL#" + _pollCount + "] No agents"); return; }
                int vc = 0, ss = 0;
                foreach (var a in all)
                {
                    if (ReferenceEquals(a, null) || !a.isViking) continue;
                    vc++;
                    if (BlackAgents.Contains(a)) continue;
                    if (!IsBlack(a))
                    {
                        var va = a.GetComponent<VikingAgent>();
                        if (!ReferenceEquals(va, null) && va.type == VikingAgent.Type.SwordShield)
                        { ss++; if (!_colorDiagDone) DumpColors(a, "SS#" + ss); }
                        continue;
                    }
                    _spn++; LogB("FOUND: " + a.name + " #" + _spn); ApplyStats(a);
                }
                if (_pollCount <= 5 || _pollCount % 10 == 0)
                    LogI("[POLL#" + _pollCount + "] total=" + all.Length + " viks=" + vc + " ss=" + ss + " conv=" + _mod);
                if (ss >= 3) _colorDiagDone = true;
            }
            catch (Exception e) { LogE("[POLL] " + e.Message); }
        }

        private static void DumpColors(Agent a, string lb)
        {
            try
            {
                var all = a.GetComponentsInChildren<BatchedSprite>(true);
                LogI("[CLR:" + lb + "] " + a.name + " has " + (all != null ? all.Length : 0) + " BatchedSprites");
                if (all == null) return;
                var cp = typeof(BatchedSprite).GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(cp, null)) return;
                foreach (var bs in all)
                {
                    if (ReferenceEquals(bs, null)) continue;
                    try { var c = (Color)cp.GetValue(bs, null); LogI("[CLR:" + lb + "]   " + bs.name + "(" + bs.GetType().Name + ") R=" + c.r.ToString("F3") + " G=" + c.g.ToString("F3") + " B=" + c.b.ToString("F3") + " A=" + c.a.ToString("F3")); } catch { }
                }
            }
            catch (Exception e) { LogE("[CLR] " + e.Message); }
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
                foreach (var bs in all) { if (ReferenceEquals(bs, null)) continue; try { var c = (Color)cp.GetValue(bs, null); if (c.b < 0.05f) bc++; } catch { } }
                return (float)bc / all.Length > 0.5f;
            }
            catch { return false; }
        }


        // ====== STATS ======
        internal static void ApplyStats(Agent a)
        {
            if (ReferenceEquals(a, null) || BlackAgents.Contains(a)) return;
            BlackAgents.Add(a); Instance._mod++; a.scale *= ScM;
            var s = a.brain as Swordsman; if (!ReferenceEquals(s, null)) { SclArr(s.damageLevels, DmgM); SclArr(s.knockbackLevels, KbM); }
            var ar = a.GetComponent<Armor>(); if (!ReferenceEquals(ar, null)) { var af = typeof(Armor).GetField("armor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(af, null)) { var o = af.GetValue(ar) as float[]; if (!ReferenceEquals(o, null)) { var cp = new float[o.Length]; Array.Copy(o, cp, o.Length); for (int i = 0; i < cp.Length; i++) cp[i] *= ArmM; af.SetValue(ar, cp); } } }
            var ch = SpearChargeComponent.AddTo(a); if (!ReferenceEquals(ch, null)) ch.Setup(a);
            a.gameObject.AddComponent<SpearStabAction>(); RegBA(a); LogB("READY #" + Instance._mod + ": " + a.name);
        }
        private static void SclArr(float[] ar, float m) { if (ar == null) return; for (int i = 0; i < ar.Length; i++) ar[i] *= m; }
        private static void RegBA(Agent a) { try { var s = a.brain as Swordsman; if (ReferenceEquals(s, null)) return; var af = typeof(Brain).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (ReferenceEquals(af, null)) return; var acts = af.GetValue(s) as System.Collections.IList; if (ReferenceEquals(acts, null)) return; var ch = a.GetComponent<SpearChargeComponent>(); if (!ReferenceEquals(ch, null) && !acts.Contains(ch)) acts.Add(ch); var st = a.GetComponent<SpearStabAction>(); if (!ReferenceEquals(st, null) && !acts.Contains(st)) acts.Add(st); } catch { } }

        // ====== ATTACK ======
        private void PatchAtk() { try { var m = typeof(Swordsman).GetMethod("GetAttack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy, null, new[] { typeof(Agent) }, null); if (!ReferenceEquals(m, null)) { _h.Patch(m, new HarmonyMethod(typeof(Plugin).GetMethod("AtkPre", BindingFlags.NonPublic | BindingFlags.Static))); _patchesOk = true; LogOK("GetAttack"); } } catch (Exception e) { LogFL("GetAttack", e); } }
        private void PatchRng() { try { var p = typeof(Swordsman).GetProperty("range", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(p, null)) { var g = p.GetGetMethod(true); if (!ReferenceEquals(g, null)) _h.Patch(g, new HarmonyMethod(typeof(Plugin).GetMethod("RngPre", BindingFlags.NonPublic | BindingFlags.Static))); } } catch { } }
        private static bool AtkPre(Swordsman __instance, Agent target, ref Attack __result) { if (!BlackAgents.Contains(__instance.agent)) return true; if (ReferenceEquals(target, null)) { __result = default(Attack); return false; } Instance._atk++; try { int lv = __instance.agent.squad != null ? __instance.agent.squad.level : 0; float d = 2.5f, k = 1.2f, s = 6f; var df = typeof(Swordsman).GetField("damageLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(df, null)) { var a = df.GetValue(__instance) as float[]; if (a != null && lv < a.Length) d = Mathf.Max(d, a[lv]); } var kf = typeof(Swordsman).GetField("knockbackLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(kf, null)) { var a = kf.GetValue(__instance) as float[]; if (a != null && lv < a.Length) k = Mathf.Max(k, a[lv]); } var sf = typeof(Swordsman).GetField("stunLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(sf, null)) { var a = sf.GetValue(__instance) as float[]; if (a != null && lv < a.Length) s = Mathf.Max(s, a[lv]); } Vector3 dir = (target.chestPos - __instance.agent.chestPos).normalized; dir.y = 0f; if (dir.sqrMagnitude < 0.001f) dir = __instance.transform.forward; __result = new Attack(new AttackSettings(d, k, 0f, s), dir, (target.wChestPos + __instance.agent.wChestPos) / 2f, __instance, __instance.agent.squad, "Sfx/English/Spear"); return false; } catch { return true; } }
        private static bool RngPre(Swordsman __instance, ref float __result) { if (!BlackAgents.Contains(__instance.agent)) return true; Instance._rng++; __result = __instance.agent.radius * 0.7f * RngM; return false; }


        // ====== DIAG + LOG ======
        private void DumpD() { try { string s = ""; int n = 0; foreach (var k in LevelStateObjectReferences.dict.Keys) { if (s.Length > 0) s += " | "; var o = LevelStateObjectReferences.dict[k]; var vr = o as VikingReference; string vi = ""; if (!ReferenceEquals(vr, null)) vi = " b=" + vr.bounty + " t=" + vr.type; s += k + "(" + (o != null ? o.GetType().Name : "NULL") + vi + ")"; n++; } LogI("[DICT] " + n + ": [" + s + "]"); } catch { } }
        private void InsVR(VikingReference vr, string l) { if (ReferenceEquals(vr, null)) return; try { var vf = typeof(VikingReference).GetField("viking", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); string vv = "?"; if (!ReferenceEquals(vf, null)) { var v = vf.GetValue(vr); vv = v != null ? v.GetType().Name : "NULL"; } LogI("[VR:" + l + "] nm=" + vr.name + " b=" + vr.bounty + " t=" + vr.type + " v=" + vv + " vc=" + (vr.vikingClone != null ? vr.vikingClone.name : "NULL")); } catch { } }
        private void InsVR(string k) { if (!LevelStateObjectReferences.dict.ContainsKey(k)) return; InsVR(LevelStateObjectReferences.dict[k] as VikingReference, k); }

        private static void LogB(string m) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogInfo("[BS1.1] ======== " + m + " ========"); }
        private static void LogOK(string m) { LogI("[OK] " + m); }
        private static void LogFL(string c, Exception e) { if (e != null) SharedLogger.LogError("[BS1.1] [FAIL:" + c + "] " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace); else SharedLogger.LogError("[BS1.1] [FAIL:" + c + "]"); }
        internal static void LogI(string m) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogInfo("[BS1.1] " + m); }
        internal static void LogW(string m) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogWarning("[BS1.1] " + m); }
        internal static void LogE(string m) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogError("[BS1.1] " + m); }
        private void OnDestroy() { try { On.Voxels.TowerDefense.GameSetup.Awake -= OnAwake; } catch { } CancelInvoke(); int a = 0; foreach (var x in BlackAgents) if (!ReferenceEquals(x, null) && x != null && !ReferenceEquals(x.aliveState, null) && x.aliveState.active) a++; LogB("SHUTDOWN: Spn=" + _spn + " Mod=" + _mod + " Atk=" + _atk + " Alive=" + a); }
    }
}