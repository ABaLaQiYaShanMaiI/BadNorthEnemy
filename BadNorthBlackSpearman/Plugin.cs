using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.CampaignGeneration.CampaignAc3;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman
{
    [BepInPlugin("black.spearman", "Bad North - Black Spearman", "1.8")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static BepInEx.Logging.ManualLogSource SharedLogger;

        internal const string BlackSpearmanRefName = "Viking_BlackSpearman";
        internal const float ConversionChance = 1.0f;
        internal const float DamageMultiplier = 1.6f;
        internal const float KnockbackMultiplier = 2.5f;
        internal const float ArmorMultiplier = 1.3f;
        internal const float ScaleMultiplier = 1.05f;

        internal static readonly HashSet<Agent> ConvertedAgents = new HashSet<Agent>();

        private static FieldInfo _armorField;
        private static bool _armorFieldAttempted;
        private int _totalConvertedCount;
        private static bool _hooksRegistered;

        private static FieldInfo _levelRuleConditionField;
        private static FieldInfo _levelGuessableProbabilityField;
        private static bool _levelFieldsCached;

        // ⭐ 武器数据
        internal static bool WeaponCached;
        internal static Sprite SpearSprite;
        internal static Vector3 SpearLocalPos = Vector3.zero;
        internal static Vector3 SpearLocalScale = Vector3.one;
        internal static Quaternion SpearLocalRot = Quaternion.identity;

        private static bool _weaponSearchDone;
        private static bool _firstConversionDiagnosticDone;

        // ============ BepInEx ============

        private void Start()
        {
            Instance = this;
            SharedLogger = Logger;
            Logger.LogInfo("[BlackSpearman] ====== v1.8 ======");
            RegisterHooks();
        }

        private void OnDestroy()
        {
            try { On.Voxels.TowerDefense.GameSetup.Awake -= OnGameSetupAwake; On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn -= OnLandingSpawn; } catch { }
        }

        private void RegisterHooks()
        {
            if (_hooksRegistered) return;
            _hooksRegistered = true;
            On.Voxels.TowerDefense.GameSetup.Awake += OnGameSetupAwake;
            On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn += OnLandingSpawn;
        }

        // ============ Hook 1 ============

        private void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake orig, GameSetup self)
        {
            orig(self);
            try { EnsureSwordShieldAlwaysAvailable(); RegisterBlackSpearmanReference(); } catch (Exception ex) { LogErr("GameSetup: " + ex.Message); }
        }

        // ============ Hook 2 ============

        private Voxels.TowerDefense.Longship OnLandingSpawn(On.Voxels.TowerDefense.RaidGeneration.Landing.orig_Spawn orig, Voxels.TowerDefense.RaidGeneration.Landing self)
        {
            var longship = orig(self);
            try
            {
                if (!ReferenceEquals(longship, null) && longship.agents != null)
                    foreach (var a in longship.agents)
                        if (!ReferenceEquals(a, null)) OnAgentSpawnedHandler(a);
            }
            catch (Exception ex) { LogErr("Landing: " + ex.Message); }
            return longship;
        }

        // ============ 武器搜索 ============

        internal static void SearchForPikemanWeapon()
        {
            if (WeaponCached || _weaponSearchDone) return;
            _weaponSearchDone = true;

            try
            {
                var allAgents = UnityEngine.Object.FindObjectsOfType<Agent>();
                var brainsFound = new List<string>();

                foreach (var a in allAgents)
                {
                    if (ReferenceEquals(a, null) || a.isViking) continue;
                    var b = a.brain;
                    if (ReferenceEquals(b, null)) continue;

                    string bn = b.GetType().Name;
                    if (!brainsFound.Contains(bn)) brainsFound.Add(bn);

                    if (bn == "Spear")
                    {
                        LogInfo("FOUND Spear brain: " + a.name);
                        if (ExtractWeapon(b, a))
                        {
                            LogInfo("Pikeman weapon extracted! Re-applying to " + ConvertedAgents.Count + " converted agents.");
                            // ⭐ 对已转化的黑矛兵重新应用武器
                            foreach (var agent in ConvertedAgents)
                                if (!ReferenceEquals(agent, null) && agent.isViking)
                                    ApplyWeaponSwap(agent);
                            return;
                        }
                    }
                }

                if (brainsFound.Count > 0)
                    LogInfo("English brains: " + string.Join(", ", brainsFound.ToArray()));
                else
                    LogWarn("No English agents in scene yet");
            }
            catch (Exception ex) { LogErr("Weapon search: " + ex.Message); }
        }

        private static bool ExtractWeapon(Brain brain, Agent agentRef)
        {
            Type st = brain.GetType();
            FieldInfo saf = st.GetField("spearAnim", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (!ReferenceEquals(saf, null))
            {
                Transform spearAnim = saf.GetValue(brain) as Transform;
                if (!ReferenceEquals(spearAnim, null))
                {
                    SpearLocalPos = spearAnim.localPosition;
                    SpearLocalRot = spearAnim.localRotation;
                    SpearLocalScale = spearAnim.localScale;

                    // ⭐ 根因 #1 修复：spearSprite 在 Spear.Setup() 中赋值 (spearAnim.GetComponentInChildren<BatchedSprite>)
                    var bs = spearAnim.GetComponentInChildren<BatchedSprite>(true);
                    if (!ReferenceEquals(bs, null))
                    {
                        // ⭐ 根因 #2 修复：BatchedSprite 属性访问 — 不用 standard SpriteRenderer
                        Type bst = bs.GetType();

                        // 尝试 sprite 属性
                        var sp = bst.GetProperty("sprite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(sp, null))
                        {
                            SpearSprite = sp.GetValue(bs, null) as Sprite;
                            if (ReferenceEquals(SpearSprite, null))
                            {
                                // 备用：通过 spriteName 创建新 Sprite
                                var sn = bst.GetProperty("spriteName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (!ReferenceEquals(sn, null))
                                {
                                    string name = sn.GetValue(bs, null) as string;
                                    LogInfo("  spriteName: " + name);
                                }
                            }
                            else
                            {
                                LogInfo("  spearSprite.sprite: " + SpearSprite.name);
                            }
                        }

                        // 诊断颜色
                        var cp = bst.GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(cp, null))
                        {
                            Color c = (Color)cp.GetValue(bs, null);
                            LogInfo("  spearSprite.color: R=" + c.r.ToString("F3") + " G=" + c.g.ToString("F3") + " B=" + c.b.ToString("F3"));
                        }

                        // 诊断所有可用属性
                        foreach (var p in bst.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            try
                            {
                                var val = p.GetValue(bs, null);
                                if (!ReferenceEquals(val, null) && val is Sprite)
                                    LogInfo("  Property '" + p.Name + "' type=Sprite value=" + (val as Sprite).name                            }
                            catch { }
                        }
                    }
                    else
                    {
                        LogWarn("  spearAnim has no BatchedSprite child");
                        // 诊断 spearAnim 的完整子对象结构
                        var allChildren = new List<string>();
                        foreach (Transform t in spearAnim.GetComponentsInChildren<Transform>(true))
                        {
                            if (!ReferenceEquals(t, null))
                            {
                                var compNames = new List<string>();
                                foreach (var comp in t.GetComponents<Component>())
                                    if (!ReferenceEquals(comp, null)) compNames.Add(comp.GetType().Name);
                                allChildren.Add(t.name + "(" + string.Join(",", compNames.ToArray()) + ")");
                            }
                        }
                        LogInfo("  spearAnim children: [" + string.Join(", ", allChildren.ToArray()) + "]");
                    }

                    WeaponCached = true;
                    return true;
                }
            }

            if (!ReferenceEquals(SpearSprite, null))
            {
                WeaponCached = true;
                return true;
            }
            return false;
        }

        // ============ Agent 生成处理 ============

        internal static void OnAgentSpawnedHandler(Agent agent)
        {
            if (ReferenceEquals(agent, null) || !agent.isViking) return;
            var va = agent.GetComponent<VikingAgent>();
            if (ReferenceEquals(va, null) || va.type != VikingAgent.Type.SwordShield) return;
            if (ConvertedAgents.Contains(agent)) return;
            if (UnityEngine.Random.value > ConversionChance) return;
            ConvertedAgents.Add(agent);
            try { ApplyBlackSpearman(agent); } catch (Exception ex) { LogErr("Apply: " + ex.Message); }
            if (Instance != null) Instance._totalConvertedCount++;
        }

        // ============ 转化链 ============

        internal static void ApplyBlackSpearman(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return;

            ApplyWeaponSwap(agent);
            DisableShield(agent);
            ApplyBlackColor(agent);

            agent.scale *= ScaleMultiplier;
            var s = agent.brain as Swordsman;
            if (!ReferenceEquals(s, null))
            {
                ScaleFloatArray(s.damageLevels, DamageMultiplier);
                ScaleFloatArray(s.knockbackLevels, KnockbackMultiplier);
            }
            ApplyArmor(agent);

            var c = SpearChargeComponent.AddTo(agent);
            if (!ReferenceEquals(c, null)) c.Setup(agent);

            UpdateVikingReference(agent);

            if (!_firstConversionDiagnosticDone)
            {
                _firstConversionDiagnosticDone = true;
                LogInfo("===== v1.8 =====");
                LogInfo("  SpearSprite: " + (!ReferenceEquals(SpearSprite, null) ? SpearSprite.name : "NULL"));
                LogInfo("  WeaponCached: " + WeaponCached);
            }
        }

        // ============ 武器替换 ============

        private static void ApplyWeaponSwap(Agent agent)
        {
            if (ReferenceEquals(SpearSprite, null)) return;

            var spearObj = new GameObject("Spear");
            spearObj.transform.SetParent(agent.transform);
            spearObj.transform.localPosition = SpearLocalPos;
            spearObj.transform.localRotation = SpearLocalRot;
            spearObj.transform.localScale = SpearLocalScale;

            var bs = spearObj.AddComponent<BatchedSprite>();
            if (ReferenceEquals(bs, null)) { LogErr("AddComponent<BatchedSprite> failed"); return; }

            var bst = bs.GetType();
            var sp = bst.GetProperty("sprite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var cp = bst.GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(sp, null)) sp.SetValue(bs, SpearSprite, null);
            if (!ReferenceEquals(cp, null)) cp.SetValue(bs, new Color(0.02f, 0.25f, 0.02f, 1f), null);

            LogInfo("Spear BatchedSprite created: " + SpearSprite.name);
        }

        private static void DisableShield(Agent agent)
        {
            agent.shield = false;
            var sc = agent.GetComponent<Shield>();
            if (!ReferenceEquals(sc, null)) UnityEngine.Object.Destroy(sc);
            foreach (var t in agent.GetComponentsInChildren<Transform>(true))
            {
                if (ReferenceEquals(t, null)) continue;
                if (t.name.ToLower().Contains("shield")) t.gameObject.SetActive(false);
            }
        }

        private static void ApplyBlackColor(Agent agent)
        {
            foreach (var comp in agent.GetComponentsInChildren<Component>(true))
            {
                if (ReferenceEquals(comp, null)) continue;
                string tn = comp.GetType().FullName;
                if (tn == null) continue;
                if (tn.EndsWith(".BatchedSprite") || tn.EndsWith(".SpriteAnimator"))
                {
                    var p = comp.GetType().GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (ReferenceEquals(p, null)) continue;
                    var old = (Color)p.GetValue(comp, null);
                    p.SetValue(comp, new Color(old.r, old.g, 0.02f, 1f), null);
                }
            }
        }

        private static void ApplyArmor(Agent agent)
        {
            var a = agent.GetComponent<Armor>();
            if (ReferenceEquals(a, null)) return;
            if (!_armorFieldAttempted) { _armorField = typeof(Armor).GetField("armor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); _armorFieldAttempted = true; }
            if (!ReferenceEquals(_armorField, null)) ScaleFloatArray(_armorField.GetValue(a) as float[], ArmorMultiplier);
        }

        // ============ LevelExpression ============

        private static void CacheLevelFields()
        {
            if (_levelFieldsCached) return;
            _levelFieldsCached = true;
            _levelRuleConditionField = typeof(LevelRule).GetField("condition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _levelGuessableProbabilityField = typeof(LevelGuessable).GetField("probability", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static FieldInfo _levelExpressionField;
        private static void SetLevelExpr(Component comp, FieldInfo field, string expr)
        {
            if (ReferenceEquals(comp, null) || ReferenceEquals(field, null)) return;
            object le = field.GetValue(comp);
            if (ReferenceEquals(le, null)) return;
            if (ReferenceEquals(_levelExpressionField, null)) _levelExpressionField = le.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(_levelExpressionField, null)) _levelExpressionField.SetValue(le, expr);
        }

        private void EnsureSwordShieldAlwaysAvailable()
        {
            CacheLevelFields();
            UnityEngine.Object obj;
            if (!LevelStateObjectReferences.dict.TryGetValue("Viking_SwordShield", out obj)) return;
            var vr = obj as VikingReference;
            if (ReferenceEquals(vr, null)) return;
            SetLevelExpr(vr.GetComponent<LevelRule>(), _levelRuleConditionField, "true");
            SetLevelExpr(vr.GetComponent<LevelGuessable>(), _levelGuessableProbabilityField, "1");
        }

        private void RegisterBlackSpearmanReference()
        {
            if (LevelStateObjectReferences.dict.ContainsKey(BlackSpearmanRefName)) return;
            CacheLevelFields();
            UnityEngine.Object obj;
            if (!LevelStateObjectReferences.dict.TryGetValue("Viking_SwordShield", out obj)) return;
            var orig = obj as VikingReference;
            if (ReferenceEquals(orig, null)) return;
            var go = new GameObject(BlackSpearmanRefName);
            DontDestroyOnLoad(go);
            var nr = go.AddComponent<VikingReference>();
            CopyVikingReferenceFields(orig, nr);
            SetLevelExpr(go.AddComponent<LevelRule>(), _levelRuleConditionField, "true");
            SetLevelExpr(go.AddComponent<LevelGuessable>(), _levelGuessableProbabilityField, "1");
            LevelStateObjectReferences.AddToDict(nr);
        }

        private void CopyVikingReferenceFields(VikingReference src, VikingReference dst)
        {
            foreach (string n in new[] { "type", "viking", "bounty", "icon", "infoSprite" })
            {
                FieldInfo f = typeof(VikingReference).GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(f, null)) f.SetValue(dst, f.GetValue(src));
            }
        }

        private static void UpdateVikingReference(Agent agent)
        {
            UnityEngine.Object o;
            if (!LevelStateObjectReferences.dict.TryGetValue(BlackSpearmanRefName, out o)) return;
            var nr = o as VikingReference;
            if (ReferenceEquals(nr, null)) return;
            var va = agent.GetComponent<VikingAgent>();
            if (ReferenceEquals(va, null)) return;
            var f = typeof(VikingAgent).GetField("vikingReference", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(f, null)) f.SetValue(va, nr);
        }

        private static void ScaleFloatArray(float[] arr, float mult) { if (arr != null) for (int i = 0; i < arr.Length; i++) arr[i] *= mult; }

        internal static void LogInfo(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogInfo("[BlackSpearman] " + msg); }
        internal static void LogWarn(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogWarning("[BlackSpearman] " + msg); }
        internal static void LogErr(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogError("[BlackSpearman] " + msg); }
    }
}