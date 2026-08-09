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
        private bool _reg, _patchesOk;
        private int _spn, _mod, _atk, _rng;
        private int _registerAttempts;
        private const int MaxRegisterAttempts = 60;

        // 使用 Awake 而非 Start，确保在 GameSetup.Awake 之前注册 hook
        private void Awake()
        {
            Instance = this;
            SharedLogger = Logger;
            LogB("v1.1 NEW ENEMY TYPE — Awake");

            _h = new Harmony("black.spearman.v1.1");

            // MMHOOK GameSetup.Awake
            try { On.Voxels.TowerDefense.GameSetup.Awake += OnGameSetupAwake; LogOK("MMHOOK GameSetup.Awake"); }
            catch (Exception e) { LogFL("MMHOOK", e); }

            // Harmony patches (必须在 Start 之前注册，因为 GetAttack 可能是 virtual)
            PatchAtk();
            PatchRng();
        }

        private void Start()
        {
            LogI("Will register '" + NewKey + "' from '" + TplKey + "'");
            LogI("Stats: Dmg=x" + DmgM + " KB=x" + KbM + " Armor=x" + ArmM + " Scale=x" + ScM + " Range=x" + RngM);
            SubSpawn();
            InvokeRepeating("Beat", 20f, 60f);
            LogB("Ready");
        }

        private void Beat()
        {
            int a = 0;
            foreach (var x in BlackAgents)
                if (!ReferenceEquals(x, null) && x != null && !ReferenceEquals(x.aliveState, null) && x.aliveState.active) a++;
            LogI(string.Format("[BEAT] Reg={0} Patches={1} Spn={2} Mod={3} Alive={4} Atk={5} Rng={6}",
                _reg, _patchesOk, _spn, _mod, a, _atk, _rng));
        }
        // ====== REGISTRATION (with retry) ======

        private void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake o, GameSetup s)
        {
            LogI(">>> GameSetup.Awake <<<");
            o(s);
            LogI(">>> orig() returned <<<");
            if (_reg) return;
            // 启动协程：每 0.5 秒检查一次 dict，最多 60 次
            StartCoroutine(RegisterWhenReady());
        }

        private IEnumerator RegisterWhenReady()
        {
            for (int attempt = 1; attempt <= MaxRegisterAttempts; attempt++)
            {
                yield return new WaitForSeconds(0.5f);
                _registerAttempts = attempt;
                try
                {
                    int count = LevelStateObjectReferences.dict.Count;
                    if (attempt <= 3 || attempt % 10 == 0 || count > 0)
                        LogI(string.Format("[REG-WAIT] attempt={0}/{1} dictSize={2}", attempt, MaxRegisterAttempts, count));

                    if (count > 0 && LevelStateObjectReferences.dict.ContainsKey(TplKey))
                    {
                        LogI("[REG-WAIT] Dict ready! Proceeding with registration...");
                        Register();
                        yield break;
                    }
                }
                catch (Exception e) { LogE("[REG-WAIT] Error: " + e.Message); }
            }
            LogFL("Dict never populated after " + MaxRegisterAttempts + " attempts", null);
        }

        private void Register()
        {
            if (_reg) return;
            _reg = true;
            LogB("REGISTRATION START");
            DumpD();

            if (LevelStateObjectReferences.dict.ContainsKey(NewKey))
            { LogI("[REG] Already in dict"); InsVR(NewKey); return; }

            LogI("[REG] Step1: template '" + TplKey + "'");
            UnityEngine.Object obj;
            if (!LevelStateObjectReferences.dict.TryGetValue(TplKey, out obj))
            { LogFL("Template not found after all waits!", null); DumpD(); return; }
            var tpl = obj as VikingReference;
            if (ReferenceEquals(tpl, null)) { LogFL("Not VR! type=" + obj.GetType().Name, null); return; }
            InsVR(tpl, "TEMPLATE");

            LogI("[REG] Step2: clone prefab");
            var vf = typeof(VikingReference).GetField("viking", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            GameObject pf = null;
            if (!ReferenceEquals(vf, null))
            { var v = vf.GetValue(tpl); LogI("viking=" + (v != null ? v.GetType().Name : "NULL")); pf = v as GameObject; if (ReferenceEquals(pf, null) && v is Component c) pf = c.gameObject; }
            if (ReferenceEquals(pf, null) && !ReferenceEquals(tpl.vikingClone, null))
            { pf = tpl.vikingClone.gameObject; LogI("Fallback: vikingClone"); }
            if (ReferenceEquals(pf, null)) { LogFL("No prefab!", null); return; }
            DmpH(pf.transform, 3);

            LogI("[REG] Step3: recolor prefab");
            var blk = Instantiate(pf); blk.name = "BlackSpearman_Prefab";
            DontDestroyOnLoad(blk); blk.SetActive(false);
            int rc = Recolor(blk.transform);
            int sw = NoSwords(blk.transform);
            LogI(string.Format("[REG] Recolored={0} SwordsOff={1}", rc, sw));
            DmpH(blk.transform, 2);

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
            LogB("NEW ENEMY TYPE REGISTERED: " + NewKey);
        }

        private int Recolor(Transform r)
        {
            var all = r.GetComponentsInChildren<BatchedSprite>(true);
            if (all == null || all.Length == 0) { LogW("[RECOLOR] No BS on " + r.name); return 0; }
            var cp = typeof(BatchedSprite).GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ReferenceEquals(cp, null)) return 0;
            int c = 0;
            foreach (var bs in all)
            { if (ReferenceEquals(bs, null)) continue; try { var o = (Color)cp.GetValue(bs, null); cp.SetValue(bs, new Color(o.r, o.g, 0.01f, o.a), null); c++; } catch { } }
            LogI("[RECOLOR] " + c + "/" + all.Length + " on " + r.name);
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
                var lr = g.GetComponent<LevelRule>();
                var lg = g.GetComponent<LevelGuessable>();
                if (!ReferenceEquals(lr, null))
                {
                    var cf = typeof(LevelRule).GetField("condition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(cf, null)) { var co = cf.GetValue(lr); if (!ReferenceEquals(co, null)) { var ef = co.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(ef, null)) ef.SetValue(co, "(fraction > 0.3 && fraction < 0.85)"); } }
                }
                if (!ReferenceEquals(lg, null))
                {
                    var pf = typeof(LevelGuessable).GetField("probability", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(pf, null)) { var pr = pf.GetValue(lg); if (!ReferenceEquals(pr, null)) { var ef = pr.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (!ReferenceEquals(ef, null)) ef.SetValue(pr, "0.9"); } }
                }
                LogI(string.Format("[CAMPAIGN] LR={0} LG={1}", !ReferenceEquals(lr, null), !ReferenceEquals(lg, null)));
            }
            catch (Exception e) { LogE("[CAMPAIGN] " + e); }
        }

        // ====== SPAWN DETECTION (polling, like v1.0) ======

        private void SubSpawn()
        {
            // 用 InvokeRepeating 轮询，每 2 秒检查一次新生成的 Agent
            InvokeRepeating("PollForBlackAgents", 3f, 2f);
            LogOK("Polling for black agents every 2s");
        }

        private void PollForBlackAgents()
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
                    LogB("BLACK SPEARMAN FOUND: " + a.name + " (#" + _spn + ")");
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

        // ====== STATS ======

        internal static void ApplyStats(Agent a)
        {
            if (ReferenceEquals(a, null) || BlackAgents.Contains(a)) return;
            BlackAgents.Add(a);
            Instance._mod++;
            LogI("[MOD] " + a.name + " scale " + a.scale + "->" + (a.scale * ScM));
            a.scale *= ScM;
            var s = a.brain as Swordsman;
            if (!ReferenceEquals(s, null))
            { LogI("[MOD] dmg x" + DmgM); SclArr(s.damageLevels, DmgM); LogI("[MOD] kb x" + KbM); SclArr(s.knockbackLevels, KbM); }
            else LogW("[MOD] brain=" + (a.brain != null ? a.brain.GetType().Name : "NULL"));
            var ar = a.GetComponent<Armor>();
            if (!ReferenceEquals(ar, null))
            {
                var af = typeof(Armor).GetField("armor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(af, null))
                { var o = af.GetValue(ar) as float[]; if (!ReferenceEquals(o, null)) { var cp = new float[o.Length]; Array.Copy(o, cp, o.Length); for (int i = 0; i < cp.Length; i++) cp[i] *= ArmM; af.SetValue(ar, cp); LogI("[MOD] armor done"); } }
            }
            var ch = SpearChargeComponent.AddTo(a);
            if (!ReferenceEquals(ch, null)) ch.Setup(a);
            a.gameObject.AddComponent<SpearStabAction>();
            RegBA(a);
            LogB("BLACK SPEARMAN READY #" + Instance._mod + ": " + a.name);
        }

        private static void SclArr(float[] ar, float m) { if (ar == null) return; for (int i = 0; i < ar.Length; i++) ar[i] *= m; }

        private static void RegBA(Agent a)
        {
            try
            {
                var s = a.brain as Swordsman; if (ReferenceEquals(s, null)) return;
                var af = typeof(Brain).GetField("actions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(af, null)) return;
                var acts = af.GetValue(s) as System.Collections.IList;
                if (ReferenceEquals(acts, null)) return;
                var ch = a.GetComponent<SpearChargeComponent>();
                if (!ReferenceEquals(ch, null) && !acts.Contains(ch)) { acts.Add(ch); LogI("[BRAIN] +Charge"); }
                var st = a.GetComponent<SpearStabAction>();
                if (!ReferenceEquals(st, null) && !acts.Contains(st)) { acts.Add(st); LogI("[BRAIN] +Stab"); }
            }
            catch (Exception e) { LogE("[BRAIN] " + e); }
        }

        // ====== ATTACK PATCHES (FIXED: __instance name) ======

        private void PatchAtk()
        {
            try
            {
                var m = typeof(Swordsman).GetMethod("GetAttack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy, null, new[] { typeof(Agent) }, null);
                if (!ReferenceEquals(m, null))
                {
                    _h.Patch(m, new HarmonyMethod(typeof(Plugin).GetMethod("AtkPre", BindingFlags.NonPublic | BindingFlags.Static)));
                    _patchesOk = true;
                    LogOK("GetAttack");
                }
                else LogW("GetAttack not found!");
            }
            catch (Exception e) { LogFL("GetAttack", e); }
        }

        private void PatchRng()
        {
            try
            {
                var p = typeof(Swordsman).GetProperty("range", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(p, null))
                {
                    var g = p.GetGetMethod(true);
                    if (!ReferenceEquals(g, null))
                    {
                        _h.Patch(g, new HarmonyMethod(typeof(Plugin).GetMethod("RngPre", BindingFlags.NonPublic | BindingFlags.Static)));
                        LogOK("range");
                    }
                }
            }
            catch (Exception e) { LogW("[RANGE] " + e); }
        }

        // CRITICAL: 第一个参数必须命名为 __instance，Harmony 按名称匹配！
        private static bool AtkPre(Swordsman __instance, Agent target, ref Attack __result)
        {
            if (!BlackAgents.Contains(__instance.agent)) return true;
            if (ReferenceEquals(target, null)) { __result = default(Attack); return false; }
            Instance._atk++;
            bool dbg = Instance._atk <= 3 || Instance._atk % 30 == 0;
            try
            {
                int lv = __instance.agent.squad != null ? __instance.agent.squad.level : 0;
                float d = 2.5f, k = 1.2f, s = 6f;
                var df = typeof(Swordsman).GetField("damageLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(df, null)) { var arr = df.GetValue(__instance) as float[]; if (arr != null && lv < arr.Length) d = Mathf.Max(d, arr[lv]); }
                var kf = typeof(Swordsman).GetField("knockbackLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(kf, null)) { var arr = kf.GetValue(__instance) as float[]; if (arr != null && lv < arr.Length) k = Mathf.Max(k, arr[lv]); }
                var sf = typeof(Swordsman).GetField("stunLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(sf, null)) { var arr = sf.GetValue(__instance) as float[]; if (arr != null && lv < arr.Length) s = Mathf.Max(s, arr[lv]); }
                Vector3 dir = (target.chestPos - __instance.agent.chestPos).normalized; dir.y = 0f;
                if (dir.sqrMagnitude < 0.001f) dir = __instance.transform.forward;
                __result = new Attack(new AttackSettings(d, k, 0f, s), dir, (target.wChestPos + __instance.agent.wChestPos) / 2f, __instance, __instance.agent.squad, "Sfx/English/Spear");
                if (dbg) LogI(string.Format("[ATK#{0}] tgt={1} d={2:F1} k={3:F1} s={4:F0} dist={5:F2}", Instance._atk, target.name, d, k, s, Vector3.Distance(__instance.transform.position, target.transform.position)));
                return false;
            }
            catch (Exception e) { LogE("[ATK] " + e); return true; }
        }

        private static bool RngPre(Swordsman __instance, ref float __result)
        {
            if (!BlackAgents.Contains(__instance.agent)) return true;
            Instance._rng++;
            __result = __instance.agent.radius * 0.7f * RngM;
            if (Instance._rng <= 2) LogI("[RNG#" + Instance._rng + "] " + (__instance.agent.radius * 0.7f).ToString("F3") + "->" + __result.ToString("F3"));
            return false;
        }

        // ====== DIAGNOSTICS ======

        private void DumpD()
        {
            try
            {
                var sb = new System.Text.StringBuilder(); int n = 0;
                foreach (var k in LevelStateObjectReferences.dict.Keys)
                {
                    if (sb.Length > 0) sb.Append(" | ");
                    var o = LevelStateObjectReferences.dict[k];
                    var vr = o as VikingReference;
                    string vi = "";
                    if (!ReferenceEquals(vr, null)) vi = string.Format(" b={0} t={1}", vr.bounty, vr.type);
                    sb.Append(k + "(" + (o != null ? o.GetType().Name : "NULL") + vi + ")");
                    n++;
                }
                LogI("[DICT] " + n + " entries: [" + sb + "]");
            }
            catch (Exception e) { LogE("[DICT] " + e); }
        }

        private void InsVR(VikingReference vr, string l)
        {
            if (ReferenceEquals(vr, null)) { LogI("[VR:" + l + "] NULL"); return; }
            try
            {
                var vf = typeof(VikingReference).GetField("viking", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                string vv = "?";
                if (!ReferenceEquals(vf, null)) { var v = vf.GetValue(vr); vv = v != null ? v.GetType().Name : "NULL"; }
                LogI(string.Format("[VR:{0}] nm={1} b={2} t={3} v={4} vc={5}", l, vr.name, vr.bounty, vr.type, vv, vr.vikingClone != null ? vr.vikingClone.name : "NULL"));
            }
            catch (Exception e) { LogE("[VR:" + l + "] " + e); }
        }

        private void InsVR(string k)
        {
            if (!LevelStateObjectReferences.dict.ContainsKey(k)) { LogI("[VR] Key '" + k + "' not in dict"); return; }
            InsVR(LevelStateObjectReferences.dict[k] as VikingReference, k);
        }

        private void DmpH(Transform t, int md)
        {
            if (ReferenceEquals(t, null)) return;
            var sb = new System.Text.StringBuilder();
            DmpTx(t, "", 0, md, sb);
            LogI("[HIER] " + t.name + ":\n" + sb.ToString());
        }

        private void DmpTx(Transform t, string ind, int d, int md, System.Text.StringBuilder sb)
        {
            if (ReferenceEquals(t, null) || d > md) return;
            var comps = t.GetComponents<Component>();
            var cn = new List<string>();
            foreach (var c in comps)
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (c is BatchedSprite) n += "(BS)";
                else if (c is SpriteAnimator) n += "(SA)";
                cn.Add(n);
            }
            sb.AppendLine(ind + "[" + d + "] " + t.name + (t.gameObject.activeSelf ? "" : " OFF") + " | " + string.Join(", ", cn));
            for (int i = 0; i < t.childCount; i++) DmpTx(t.GetChild(i), ind + "  ", d + 1, md, sb);
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
            try { On.Voxels.TowerDefense.GameSetup.Awake -= OnGameSetupAwake; } catch { }
            CancelInvoke();
            int a = 0;
            foreach (var x in BlackAgents) if (!ReferenceEquals(x, null) && x != null && !ReferenceEquals(x.aliveState, null) && x.aliveState.active) a++;
            LogB("SHUTDOWN: Spn=" + _spn + " Mod=" + _mod + " Atk=" + _atk + " Alive=" + a);
        }
    }
}
