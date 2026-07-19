using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.CampaignGeneration.CampaignAc3;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman
{
    [BepInPlugin("black.spearman", "Bad North - Black Spearman", "1.13")]
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

        // 武器系统
        internal static bool WeaponCached;
        internal static GameObject CachedSpearAnim;
        internal static Vector3 SpearLocalPos = Vector3.zero;
        internal static Vector3 SpearLocalScale = Vector3.one;
        internal static Quaternion SpearLocalRot = Quaternion.identity;
        private static int _weaponSearchAttempts;
        private const int MaxWeaponSearchAttempts = 30;

        private static bool _firstConversionDiagnosticDone;

        // ============ BepInEx ============

        private void Start()
        {
            Instance = this;
            SharedLogger = Logger;
            Logger.LogInfo("[BlackSpearman] ====== v1.13 (back to basics) ======");
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

        private void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake orig, GameSetup self)
        {
            orig(self);
            try { EnsureSwordShieldAlwaysAvailable(); RegisterBlackSpearmanReference(); }
            catch (Exception ex) { LogErr("GameSetup: " + ex); }
        }

        private Voxels.TowerDefense.Longship OnLandingSpawn(On.Voxels.TowerDefense.RaidGeneration.Landing.orig_Spawn orig, Voxels.TowerDefense.RaidGeneration.Landing self)
        {
            var longship = orig(self);
            try
            {
                if (!ReferenceEquals(longship, null) && longship.agents != null)
                    foreach (var a in longship.agents)
                        if (!ReferenceEquals(a, null)) OnAgentSpawnedHandler(a);
            }
            catch (Exception ex) { LogErr("Landing: " + ex); }
            return longship;
        }

        // ============ 武器搜索 ============

        internal static void SearchForPikemanWeapon()
        {
            if (WeaponCached) return;
            if (_weaponSearchAttempts >= MaxWeaponSearchAttempts) return;
            _weaponSearchAttempts++;

            try
            {
                var allAgents = UnityEngine.Object.FindObjectsOfType<Agent>();
                foreach (var a in allAgents)
                {
                    if (ReferenceEquals(a, null) || a.isViking) continue;
                    var b = a.brain;
                    if (ReferenceEquals(b, null)) continue;
                    if (b.GetType().Name == "Spear")
                    {
                        LogInfo("[WEAPON] FOUND Spear brain on " + a.name + " at frame " + Time.frameCount);
                        if (ExtractWeapon(b))
                        {
                            LogInfo("[WEAPON] Cached! ActiveInHierarchy=" + a.gameObject.activeInHierarchy);
                            foreach (var agent in ConvertedAgents)
                                if (!ReferenceEquals(agent, null) && agent.isViking)
                                    ReapplyWeaponIfNeeded(agent);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) { LogErr("[WEAPON] " + ex.Message); }
        }

        private static bool ExtractWeapon(Brain brain)
        {
            var saf = brain.GetType().GetField("spearAnim", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ReferenceEquals(saf, null)) { LogErr("[WEAPON] No spearAnim field"); return false; }

            var spearAnim = saf.GetValue(brain) as Transform;
            if (ReferenceEquals(spearAnim, null)) { LogErr("[WEAPON] spearAnim is null"); return false; }

            CachedSpearAnim = spearAnim.gameObject;
            SpearLocalPos = spearAnim.localPosition;
            SpearLocalRot = spearAnim.localRotation;
            SpearLocalScale = spearAnim.localScale;

            WeaponCached = true;
            LogInfo("[WEAPON] Weapon cached: " + CachedSpearAnim.name);
            return true;
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
            try { ApplyBlackSpearman(agent); } catch (Exception ex) { LogErr("Apply: " + ex); }
            if (Instance != null) Instance._totalConvertedCount++;
        }

        // ============ 转化链（最小化：只做盾+数值+技能，不碰颜色！） ============

        internal static void ApplyBlackSpearman(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return;

            // 武器（如果已缓存）
            ReapplyWeaponIfNeeded(agent);

            // 盾禁用
            agent.shield = false;
            foreach (var t in agent.GetComponentsInChildren<Transform>(true))
            {
                if (ReferenceEquals(t, null)) continue;
                if (t.name.ToLower().Contains("shield")) t.gameObject.SetActive(false);
            }

            // ⚠️ 不再修改 BatchedSprite/SpriteAnimator 颜色！
            //    a583701 版本证明：颜色修改破坏了模型渲染。
            //    保留 SwordShield 原始颜色（如需变黑，用 AgentTextureBaker 层面方案）。

            // 数值
            agent.scale *= ScaleMultiplier;
            var s = agent.brain as Swordsman;
            if (!ReferenceEquals(s, null))
            {
                ScaleFloatArray(s.damageLevels, DamageMultiplier);
                ScaleFloatArray(s.knockbackLevels, KnockbackMultiplier);
            }
            ApplyArmor(agent);
            ApplySpearCombatStats(agent);

            // 技能组件
            var c = SpearChargeComponent.AddTo(agent);
            if (!ReferenceEquals(c, null)) c.Setup(agent);
            agent.gameObject.AddComponent<SpearStabAction>();
            UpdateVikingReference(agent);

            if (!_firstConversionDiagnosticDone)
            {
                _firstConversionDiagnosticDone = true;
                LogInfo("===== v1.13 =====");
                LogInfo("  WeaponCached: " + WeaponCached);
                LogInfo("  NO color modification (preserving original SwordShield visuals)");
            }
        }

        /// <summary>
        /// 在 Agent 激活后重新尝试武器替换
        /// </summary>
        public static void ReapplyWeaponIfNeeded(Agent agent)
        {
            if (ReferenceEquals(CachedSpearAnim, null)) return;

            var existing = agent.transform.Find("Spear");
            if (!ReferenceEquals(existing, null)) return;

            var spearClone = UnityEngine.Object.Instantiate(CachedSpearAnim);
            spearClone.name = "Spear";
            spearClone.transform.SetParent(agent.transform);
            spearClone.transform.localPosition = SpearLocalPos;
            spearClone.transform.localRotation = SpearLocalRot;
            spearClone.transform.localScale = SpearLocalScale;
            LogInfo("[WEAPON] Spear added to " + agent.name);
        }

        // ============ 数值修改 ============

        private static void ApplyArmor(Agent agent)
        {
            var a = agent.GetComponent<Armor>();
            if (ReferenceEquals(a, null)) return;
            if (!_armorFieldAttempted) { _armorField = typeof(Armor).GetField("armor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); _armorFieldAttempted = true; }
            if (ReferenceEquals(_armorField, null)) return;

            var original = _armorField.GetValue(a) as float[];
            if (ReferenceEquals(original, null)) return;

            var copy = new float[original.Length];
            Array.Copy(original, copy, original.Length);
            for (int i = 0; i < copy.Length; i++) copy[i] *= ArmorMultiplier;
            _armorField.SetValue(a, copy);
        }

        private static FieldInfo _agentRadiusField;
        private static bool _agentRadiusFieldCached;

        private static void ApplySpearCombatStats(Agent agent)
        {
            var s = agent.brain as Swordsman;
            if (ReferenceEquals(s, null)) return;

            if (!_agentRadiusFieldCached)
            {
                _agentRadiusFieldCached = true;
                _agentRadiusField = typeof(Agent).GetField("radius", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? typeof(Agent).GetField("_radius", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (!ReferenceEquals(_agentRadiusField, null))
            {
                float cur = (float)_agentRadiusField.GetValue(agent);
                _agentRadiusField.SetValue(agent, cur * 1.15f);
            }

            var ascField = typeof(Swordsman).GetField("attackStaminaCost", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(ascField, null))
            {
                float cur = (float)ascField.GetValue(s);
                ascField.SetValue(s, cur * 0.7f);
            }
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
            var le = field.GetValue(comp);
            if (ReferenceEquals(le, null)) return;
            if (ReferenceEquals(_levelExpressionField, null))
                _levelExpressionField = le.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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
            foreach (string n in new[] { "type", "viking", "bounty", "sprite2" })
            {
                var f = typeof(VikingReference).GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

        private static void ScaleFloatArray(float[] arr, float mult)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++) arr[i] *= mult;
        }

        internal static void LogInfo(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogInfo("[BlackSpearman] " + msg); }
        internal static void LogWarn(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogWarning("[BlackSpearman] " + msg); }
        internal static void LogErr(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogError("[BlackSpearman] " + msg); }
    }
}