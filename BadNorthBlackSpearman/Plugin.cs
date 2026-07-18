using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.CampaignGeneration.CampaignAc3;

namespace BadNorthBlackSpearman
{
    [BepInPlugin("black.spearman", "Bad North - Black Spearman", "1.1")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
        public static BepInEx.Logging.ManualLogSource SharedLogger;

        internal const string BlackSpearmanRefName = "Viking_BlackSpearman";
        internal const float ConversionChance = 0.4f;
        internal const float DamageMultiplier = 1.6f;
        internal const float KnockbackMultiplier = 2.5f;
        internal const float ArmorMultiplier = 1.3f;
        internal const float ScaleMultiplier = 1.2f;

        internal static readonly HashSet<Agent> ConvertedAgents = new HashSet<Agent>();

        private static FieldInfo _armorField;
        private static bool _armorFieldAttempted;
        private int _totalConvertedCount;
        private static bool _firstConversionDiagnosticDone;
        private static bool _hooksRegistered;

        // LevelRule / LevelGuessable 私有字段缓存
        private static FieldInfo _levelRuleConditionField;
        private static FieldInfo _levelGuessableProbabilityField;
        private static bool _levelFieldsCached;

        // ==============================
        // BepInEx 生命周期
        // ==============================

        private void Start()
        {
            Instance = this;
            SharedLogger = Logger;
            Logger.LogInfo("[BlackSpearman] ====== v1.1 MMHOOK START ======");
            LogEnvironmentInfo();
            RegisterHooks();
            Logger.LogInfo("[BlackSpearman] ====== INIT COMPLETE ======");
        }

        private void OnDestroy()
        {
            try
            {
                On.Voxels.TowerDefense.GameSetup.Awake -= OnGameSetupAwake;
                On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn -= OnLandingSpawn;
            }
            catch { }
            _hooksRegistered = false;
        }

        // ==============================
        // MMHOOK 注册
        // ==============================

        private void RegisterHooks()
        {
            if (_hooksRegistered) return;
            _hooksRegistered = true;

            try
            {
                On.Voxels.TowerDefense.GameSetup.Awake += OnGameSetupAwake;
                Logger.LogInfo("[BlackSpearman] Hook: GameSetup.Awake (MMHOOK)");

                On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn += OnLandingSpawn;
                Logger.LogInfo("[BlackSpearman] Hook: Landing.Spawn (MMHOOK)");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlackSpearman] Hook registration failed: {ex.GetType().Name}: {ex.Message}");
                _hooksRegistered = false;
            }
        }

        // ==============================
        // Hook 1: GameSetup.Awake
        // ==============================

        private void OnGameSetupAwake(
            On.Voxels.TowerDefense.GameSetup.orig_Awake orig,
            GameSetup self)
        {
            orig(self);
            try
            {
                EnsureSwordShieldAlwaysAvailable();
                RegisterBlackSpearmanReference();
            }
            catch (Exception ex)
            {
                SharedLogger?.LogError($"[BlackSpearman] GameSetup hook: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ==============================
        // Hook 2: Landing.Spawn
        // ==============================

        private Voxels.TowerDefense.Longship OnLandingSpawn(
            On.Voxels.TowerDefense.RaidGeneration.Landing.orig_Spawn orig,
            Voxels.TowerDefense.RaidGeneration.Landing self)
        {
            Voxels.TowerDefense.Longship longship = orig(self);

            try
            {
                if (!ReferenceEquals(longship, null) && longship.agents != null)
                {
                    foreach (var agent in longship.agents)
                    {
                        if (!ReferenceEquals(agent, null))
                            OnAgentSpawnedHandler(agent);
                    }
                }
            }
            catch (Exception ex)
            {
                SharedLogger?.LogError($"[BlackSpearman] Landing.Spawn handler: {ex.GetType().Name}: {ex.Message}");
            }

            return longship;
        }

        // ==============================
        // LevelExpression 反射工具
        // ==============================

        private static void CacheLevelFields()
        {
            if (_levelFieldsCached) return;
            _levelFieldsCached = true;

            _levelRuleConditionField = typeof(LevelRule).GetField("condition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _levelGuessableProbabilityField = typeof(LevelGuessable).GetField("probability",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static FieldInfo _levelExpressionField;

        private static void SetLevelExpr(Component comp, FieldInfo field, string expr)
        {
            if (ReferenceEquals(comp, null) || ReferenceEquals(field, null)) return;
            var le = field.GetValue(comp);
            if (ReferenceEquals(le, null)) return;

            if (ReferenceEquals(_levelExpressionField, null))
                _levelExpressionField = le.GetType().GetField("expression",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (!ReferenceEquals(_levelExpressionField, null))
                _levelExpressionField.SetValue(le, expr);
        }

        // ==============================
        // SwordShield 永驻 + 黑矛兵注册
        // ==============================

        private void EnsureSwordShieldAlwaysAvailable()
        {
            CacheLevelFields();

            UnityEngine.Object obj;
            if (!LevelStateObjectReferences.dict.TryGetValue("Viking_SwordShield", out obj))
            {
                SharedLogger?.LogWarning("[BlackSpearman] Viking_SwordShield not found in dict.");
                return;
            }

            var swordShieldRef = obj as VikingReference;
            if (ReferenceEquals(swordShieldRef, null)) return;

            SetLevelExpr(swordShieldRef.GetComponent<LevelRule>(), _levelRuleConditionField, "true");
            SetLevelExpr(swordShieldRef.GetComponent<LevelGuessable>(), _levelGuessableProbabilityField, "1");
            SharedLogger?.LogInfo("[BlackSpearman] SwordShield LevelRule/LevelGuessable set to always available.");
        }

        private void RegisterBlackSpearmanReference()
        {
            if (LevelStateObjectReferences.dict.ContainsKey(BlackSpearmanRefName))
            {
                SharedLogger?.LogDebug($"[BlackSpearman] '{BlackSpearmanRefName}' already registered.");
                return;
            }
            CacheLevelFields();

            UnityEngine.Object swordObj;
            if (!LevelStateObjectReferences.dict.TryGetValue("Viking_SwordShield", out swordObj))
            {
                SharedLogger?.LogWarning("[BlackSpearman] Cannot find Viking_SwordShield to clone.");
                return;
            }

            var origRef = swordObj as VikingReference;
            if (ReferenceEquals(origRef, null)) return;

            var newGo = new GameObject(BlackSpearmanRefName);
            DontDestroyOnLoad(newGo);

            var newRef = newGo.AddComponent<VikingReference>();
            CopyVikingReferenceFields(origRef, newRef);

            SetLevelExpr(newGo.AddComponent<LevelRule>(), _levelRuleConditionField, "true");
            SetLevelExpr(newGo.AddComponent<LevelGuessable>(), _levelGuessableProbabilityField, "1");

            LevelStateObjectReferences.AddToDict(newRef);
            SharedLogger?.LogInfo($"[BlackSpearman] Registered independent VikingReference: '{BlackSpearmanRefName}'.");
        }

        private void CopyVikingReferenceFields(VikingReference src, VikingReference dst)
        {
            foreach (var name in new[] { "type", "viking", "bounty" })
            {
                var f = typeof(VikingReference).GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(f, null))
                    f.SetValue(dst, f.GetValue(src));
            }
        }

        // ==============================
        // Agent 生成处理
        // ==============================

        internal static void OnAgentSpawnedHandler(Agent agent)
        {
            if (ReferenceEquals(agent, null) || !agent.isViking) return;

            var va = agent.GetComponent<VikingAgent>();
            if (ReferenceEquals(va, null) || va.type != VikingAgent.Type.SwordShield) return;

            if (ConvertedAgents.Contains(agent)) return;
            if (UnityEngine.Random.value > ConversionChance) return;

            ConvertedAgents.Add(agent);

            try { ApplyBlackSpearman(agent); }
            catch (Exception ex) { SharedLogger?.LogError($"[BlackSpearman] Apply: {ex.Message}"); }

            if (Instance != null)
            {
                Instance._totalConvertedCount++;
                if (Instance._totalConvertedCount % 10 == 1 || Instance._totalConvertedCount <= 1)
                    Instance.Logger.LogInfo($"[BlackSpearman] Converted #{Instance._totalConvertedCount}: " +
                        $"type={va.type}, hp={agent.health:F1}/{agent.maxHealth:F1}, scale={agent.scale:F2}");
            }
        }

        // ==============================
        // 五步转化链
        // ==============================

        internal static void ApplyBlackSpearman(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return;

            ApplyBlackColor(agent);
            agent.scale *= ScaleMultiplier;

            var swordsman = agent.brain as Swordsman;
            if (!ReferenceEquals(swordsman, null))
            {
                ScaleFloatArray(swordsman.damageLevels, DamageMultiplier);
                ScaleFloatArray(swordsman.knockbackLevels, KnockbackMultiplier);
            }

            ApplyArmor(agent);

            var charge = SpearChargeComponent.AddTo(agent);
            if (!ReferenceEquals(charge, null)) charge.Setup(agent);

            UpdateVikingReference(agent);

            if (!_firstConversionDiagnosticDone)
            {
                _firstConversionDiagnosticDone = true;
                RunFirstConversionDiagnostic(agent);
            }
        }

        // ==============================
        // P0 修复：保留 R/G（sprite2 UV 编码）
        // ==============================

        private static void ApplyBlackColor(Agent agent)
        {
            var allComps = agent.GetComponentsInChildren<Component>(true);
            foreach (var comp in allComps)
            {
                if (ReferenceEquals(comp, null)) continue;
                var tn = comp.GetType().FullName;

                if (tn.EndsWith(".BatchedSprite") || tn.EndsWith(".SpriteAnimator"))
                {
                    var prop = comp.GetType().GetProperty("color",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (ReferenceEquals(prop, null)) continue;

                    Color old = (Color)prop.GetValue(comp, null);
                    prop.SetValue(comp, new Color(old.r, old.g, 0.02f, 1f), null);
                }
                else if (tn.EndsWith(".SpriteRenderer") || tn.EndsWith(".MeshRenderer") ||
                         tn.EndsWith(".SkinnedMeshRenderer") || tn.Contains("Render"))
                {
                    var mp = comp.GetType().GetProperty("material",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(mp, null))
                    {
                        var mat = mp.GetValue(comp, null) as Material;
                        if (!ReferenceEquals(mat, null) && mat.HasProperty("_Color"))
                            mat.SetColor("_Color", new Color(0.02f, 0.02f, 0.02f, 1f));
                    }
                }
            }
        }

        private static void ApplyArmor(Agent agent)
        {
            var armor = agent.GetComponent<Armor>();
            if (ReferenceEquals(armor, null)) return;
            if (!_armorFieldAttempted)
            {
                _armorField = typeof(Armor).GetField("armor",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _armorFieldAttempted = true;
            }
            if (!ReferenceEquals(_armorField, null))
                ScaleFloatArray(_armorField.GetValue(armor) as float[], ArmorMultiplier);
        }

        // ==============================
        // P2 修复：vikingReference 绑定
        // ==============================

        private static void UpdateVikingReference(Agent agent)
        {
            UnityEngine.Object blackRef;
            if (!LevelStateObjectReferences.dict.TryGetValue(BlackSpearmanRefName, out blackRef)) return;
            var newRef = blackRef as VikingReference;
            if (ReferenceEquals(newRef, null)) return;
            var va = agent.GetComponent<VikingAgent>();
            if (ReferenceEquals(va, null)) return;
            var f = typeof(VikingAgent).GetField("vikingReference",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(f, null)) f.SetValue(va, newRef);
        }

        private static void ScaleFloatArray(float[] arr, float mult)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++) arr[i] *= mult;
        }

        // ==============================
        // 诊断
        // ==============================

        private static void RunFirstConversionDiagnostic(Agent agent)
        {
            var l = new List<string>();
            l.Add("===== BlackSpearman v1.1 Diagnostic =====");

            var sw = agent.brain as Swordsman;
            l.Add(!ReferenceEquals(sw, null)
                ? $"  [PASS] Brain: Swordsman. dmg[0]={sw.damageLevels[0]:F2} knock[0]={sw.knockbackLevels[0]:F2}"
                : $"  [FAIL] Brain: {agent.brain?.GetType().FullName ?? "null"}");

            var armor = agent.GetComponent<Armor>();
            if (!ReferenceEquals(armor, null))
            {
                var af = typeof(Armor).GetField("armor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var v = !ReferenceEquals(af, null) ? af.GetValue(armor) as float[] : null;
                l.Add(v != null ? $"  [PASS] Armor: [{v[0]:F2},{v[1]:F2},{v[2]:F2}]" : "  [WARN] Armor field null");
            }
            else l.Add("  [FAIL] Armor: not found.");

            var va = agent.GetComponent<VikingAgent>();
            if (!ReferenceEquals(va, null))
            {
                var vf = typeof(VikingAgent).GetField("vikingReference",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(vf, null))
                {
                    var vr = vf.GetValue(va) as VikingReference;
                    l.Add(!ReferenceEquals(vr, null)
                        ? $"  [INFO] VikingRef: {vr.name}, type={vr.type}"
                        : "  [WARN] VikingRef: null");
                }
            }

            // 颜色诊断：用字符串搜索替代 typeof(SpriteAnimator)
            foreach (var c in agent.GetComponentsInChildren<Component>(true))
            {
                if (ReferenceEquals(c, null)) continue;
                if (c.GetType().FullName != null && c.GetType().FullName.EndsWith(".SpriteAnimator"))
                {
                    var cp = c.GetType().GetProperty("color",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(cp, null))
                    {
                        var col = (Color)cp.GetValue(c, null);
                        l.Add($"  [INFO] SpriteAnimator.color: R={col.r:F2} G={col.g:F2} B={col.b:F2} A={col.a:F2}");
                    }
                    break;
                }
            }

            l.Add($"  [INFO] {ConversionChance * 100:F0}% chance, Scale×{ScaleMultiplier}, " +
                  $"Dmg×{DamageMultiplier}, Knock×{KnockbackMultiplier}, Armor×{ArmorMultiplier}");
            l.Add($"  [INFO] MMHOOK: {_hooksRegistered}");
            l.Add("=========================================");
            SharedLogger?.LogInfo(string.Join("\n", l.ToArray()));
        }

        private void LogEnvironmentInfo()
        {
            try
            {
                Logger.LogInfo($"[BlackSpearman] BepInEx {typeof(BaseUnityPlugin).Assembly.GetName().Version}, " +
                    $"CLR {Environment.Version}");
                Logger.LogInfo($"[BlackSpearman] Types: Faction={!ReferenceEquals(typeof(Faction), null)} " +
                    $"VikingAgent={!ReferenceEquals(typeof(VikingAgent), null)} " +
                    $"Swordsman={!ReferenceEquals(typeof(Swordsman), null)}");
            }
            catch { }
        }
    }
}