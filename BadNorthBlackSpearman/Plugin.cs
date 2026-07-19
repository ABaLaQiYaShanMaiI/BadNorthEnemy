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
        internal const float ConversionChance = 1.0f;
        internal const float DamageMultiplier = 1.6f;
        internal const float KnockbackMultiplier = 2.5f;
        internal const float ArmorMultiplier = 1.3f;
        internal const float ScaleMultiplier = 1.05f;

        internal static readonly HashSet<Agent> ConvertedAgents = new HashSet<Agent>();

        private static FieldInfo _armorField;
        private static bool _armorFieldAttempted;
        private int _totalConvertedCount;
        private static bool _firstConversionDiagnosticDone;
        private static bool _hooksRegistered;

        private static FieldInfo _levelRuleConditionField;
        private static FieldInfo _levelGuessableProbabilityField;
        private static bool _levelFieldsCached;

        // ============ 生命周期 ============

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
                Logger.LogError("[BlackSpearman] Hook failed: " + ex.Message);
                _hooksRegistered = false;
            }
        }

        private void OnGameSetupAwake(On.Voxels.TowerDefense.GameSetup.orig_Awake orig, GameSetup self)
        {
            orig(self);
            try
            {
                EnsureSwordShieldAlwaysAvailable();
                RegisterBlackSpearmanReference();
                CachePikemanWeaponTemplate();
            }
            catch (Exception ex)
            {
                LogErr("GameSetup hook: " + ex.Message);
            }
        }

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
            catch (Exception ex) { LogErr("Landing.Spawn: " + ex.Message); }
            return longship;
        }

        // ============ LevelExpression 反射工具 ============

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
            if (ReferenceEquals(_levelExpressionField, null))
                _levelExpressionField = le.GetType().GetField("expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(_levelExpressionField, null))
                _levelExpressionField.SetValue(le, expr);
        }

        // ============ SwordShield 永驻 + 黑矛兵注册 ============

        private void EnsureSwordShieldAlwaysAvailable()
        {
            CacheLevelFields();
            UnityEngine.Object obj;
            if (!LevelStateObjectReferences.dict.TryGetValue("Viking_SwordShield", out obj))
            {
                LogWarn("Viking_SwordShield not found in dict.");
                return;
            }
            VikingReference swordShieldRef = obj as VikingReference;
            if (ReferenceEquals(swordShieldRef, null)) return;
            SetLevelExpr(swordShieldRef.GetComponent<LevelRule>(), _levelRuleConditionField, "true");
            SetLevelExpr(swordShieldRef.GetComponent<LevelGuessable>(), _levelGuessableProbabilityField, "1");
            LogInfo("SwordShield LevelRule/LevelGuessable set to always available.");
        }

        private void RegisterBlackSpearmanReference()
        {
            if (LevelStateObjectReferences.dict.ContainsKey(BlackSpearmanRefName)) return;
            CacheLevelFields();
            UnityEngine.Object swordObj;
            if (!LevelStateObjectReferences.dict.TryGetValue("Viking_SwordShield", out swordObj))
            {
                LogWarn("Cannot find Viking_SwordShield to clone.");
                return;
            }
            VikingReference origRef = swordObj as VikingReference;
            if (ReferenceEquals(origRef, null)) return;

            GameObject newGo = new GameObject(BlackSpearmanRefName);
            DontDestroyOnLoad(newGo);
            VikingReference newRef = newGo.AddComponent<VikingReference>();
            CopyVikingReferenceFields(origRef, newRef);
            SetLevelExpr(newGo.AddComponent<LevelRule>(), _levelRuleConditionField, "true");
            SetLevelExpr(newGo.AddComponent<LevelGuessable>(), _levelGuessableProbabilityField, "1");
            LevelStateObjectReferences.AddToDict(newRef);
            LogInfo("Registered independent VikingReference: '" + BlackSpearmanRefName + "'.");
        }

        private void CopyVikingReferenceFields(VikingReference src, VikingReference dst)
        {
            foreach (string name in new[] { "type", "viking", "bounty" })
            {
                FieldInfo f = typeof(VikingReference).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(f, null))
                    f.SetValue(dst, f.GetValue(src));
            }
        }

        // ============ Agent 生成处理 ============

        internal static void OnAgentSpawnedHandler(Agent agent)
        {
            if (ReferenceEquals(agent, null) || !agent.isViking) return;
            VikingAgent va = agent.GetComponent<VikingAgent>();
            if (ReferenceEquals(va, null) || va.type != VikingAgent.Type.SwordShield) return;
            if (ConvertedAgents.Contains(agent)) return;
            if (UnityEngine.Random.value > ConversionChance) return;

            ConvertedAgents.Add(agent);
            try { ApplyBlackSpearman(agent); }
            catch (Exception ex) { LogErr("Apply: " + ex.Message); }

            if (Instance != null)
            {
                Instance._totalConvertedCount++;
                if (Instance._totalConvertedCount % 10 == 1 || Instance._totalConvertedCount <= 1)
                    Instance.Logger.LogInfo("[BlackSpearman] Converted #" + Instance._totalConvertedCount + ": type=" + va.type + " hp=" + agent.health.ToString("F1") + "/" + agent.maxHealth.ToString("F1") + " scale=" + agent.scale.ToString("F2"));
            }
        }

        // ============ 转化链 ============

        internal static void ApplyBlackSpearman(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return;
            ApplyBlackColor(agent);
            agent.scale *= ScaleMultiplier;

            Swordsman swordsman = agent.brain as Swordsman;
            if (!ReferenceEquals(swordsman, null))
            {
                ScaleFloatArray(swordsman.damageLevels, DamageMultiplier);
                ScaleFloatArray(swordsman.knockbackLevels, KnockbackMultiplier);
            }

            ApplyArmor(agent);
            ApplyWeaponSwap(agent);

            SpearChargeComponent charge = SpearChargeComponent.AddTo(agent);
            if (!ReferenceEquals(charge, null)) charge.Setup(agent);

            UpdateVikingReference(agent);

            if (!_firstConversionDiagnosticDone)
            {
                _firstConversionDiagnosticDone = true;
                RunFirstConversionDiagnostic(agent);
            }
        }

        // ============ 颜色 ============

        private static void ApplyBlackColor(Agent agent)
        {
            Component[] allComps = agent.GetComponentsInChildren<Component>(true);
            foreach (Component comp in allComps)
            {
                if (ReferenceEquals(comp, null)) continue;
                string tn = comp.GetType().FullName;
                if (tn == null) continue;

                if (tn.EndsWith(".BatchedSprite") || tn.EndsWith(".SpriteAnimator"))
                {
                    PropertyInfo prop = comp.GetType().GetProperty("color", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (ReferenceEquals(prop, null)) continue;
                    Color old = (Color)prop.GetValue(comp, null);
                    prop.SetValue(comp, new Color(old.r, old.g, 0.02f, 1f), null);
                }
                else if (tn.EndsWith(".SpriteRenderer") || tn.EndsWith(".MeshRenderer") || tn.EndsWith(".SkinnedMeshRenderer") || tn.Contains("Render"))
                {
                    PropertyInfo mp = comp.GetType().GetProperty("material", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(mp, null))
                    {
                        Material mat = mp.GetValue(comp, null) as Material;
                        if (!ReferenceEquals(mat, null) && mat.HasProperty("_Color"))
                            mat.SetColor("_Color", new Color(0.02f, 0.02f, 0.02f, 1f));
                    }
                }
            }
        }

        private static void ApplyArmor(Agent agent)
        {
            Armor armor = agent.GetComponent<Armor>();
            if (ReferenceEquals(armor, null)) return;
            if (!_armorFieldAttempted)
            {
                _armorField = typeof(Armor).GetField("armor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _armorFieldAttempted = true;
            }
            if (!ReferenceEquals(_armorField, null))
                ScaleFloatArray(_armorField.GetValue(armor) as float[], ArmorMultiplier);
        }

        // ============ 武器替换（核心新方案）============

        private static Agent _pikemanPrefab;
        private static bool _pikemanCached;

        /// <summary>
        /// 在 GameSetup.Awake 时查找 Pikeman 兵种的 minionPrefab 作为武器模板
        /// </summary>
        private static void CachePikemanWeaponTemplate()
        {
            if (_pikemanCached) return;
            _pikemanCached = true;

            try
            {
                Faction faction = UnityEngine.Object.FindObjectOfType<Faction>();
                if (ReferenceEquals(faction, null) || faction.allSquads == null)
                {
                    LogWarn("Faction or allSquads is null");
                    return;
                }

                FieldInfo minionPrefabField = typeof(Squad).GetField("minionPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo heroAgentField = typeof(Squad).GetField("heroAgent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                LogInfo("=== Faction Squads Diagnostic ===");
                int squadIdx = 0;
                foreach (Squad s in faction.allSquads)
                {
                    if (ReferenceEquals(s, null)) continue;
                    string sName = s.name;
                    if (string.IsNullOrEmpty(sName)) sName = "(null)";
                    string brainType = "?";
                    if (!ReferenceEquals(minionPrefabField, null))
                    {
                        Agent mp = minionPrefabField.GetValue(s) as Agent;
                        if (!ReferenceEquals(mp, null) && !ReferenceEquals(mp.brain, null))
                            brainType = mp.brain.GetType().FullName;
                    }
                    LogInfo("  Squad#" + squadIdx + ": name='" + sName + "' brain=" + brainType);
                    squadIdx++;
                }
                LogInfo("=== End Squad Diagnostic ===");

                // 查找 Pikeman
                foreach (Squad squad in faction.allSquads)
                {
                    if (ReferenceEquals(squad, null)) continue;
                    string sName = squad.name;
                    if (string.IsNullOrEmpty(sName)) sName = "";
                    bool isPikeman = sName.Contains("Pikeman") || sName.Contains("Pike") || sName.Contains("Spear");

                    if (!isPikeman && !ReferenceEquals(minionPrefabField, null))
                    {
                        Agent mp = minionPrefabField.GetValue(squad) as Agent;
                        if (!ReferenceEquals(mp, null) && !ReferenceEquals(mp.brain, null) && mp.brain.GetType().FullName != null)
                            isPikeman = mp.brain.GetType().FullName.Contains("Pikeman");
                    }

                    if (isPikeman)
                    {
                        // 先选 minionPrefab
                        if (!ReferenceEquals(minionPrefabField, null))
                        {
                            _pikemanPrefab = minionPrefabField.GetValue(squad) as Agent;
                            if (!ReferenceEquals(_pikemanPrefab, null))
                            {
                                LogInfo("Pikeman weapon template: minionPrefab from '" + sName + "'");
                                // 输出子对象结构
                                List<string> childNames = new List<string>();
                                foreach (Transform child in _pikemanPrefab.transform)
                                {
                                    if (!ReferenceEquals(child, null))
                                        childNames.Add("'" + child.name + "'");
                                }
                                LogInfo("  Pikeman children: [" + string.Join(", ", childNames.ToArray()) + "]");
                                return;
                            }
                        }
                        // 回退到 heroAgent
                        if (!ReferenceEquals(heroAgentField, null))
                        {
                            _pikemanPrefab = heroAgentField.GetValue(squad) as Agent;
                            if (!ReferenceEquals(_pikemanPrefab, null))
                            {
                                LogInfo("Pikeman weapon template: heroAgent from '" + sName + "'");
                                return;
                            }
                        }
                    }
                }

                LogWarn("No Pikeman squad found in Faction.");
            }
            catch (Exception ex)
            {
                LogErr("CachePikemanWeaponTemplate: " + ex.Message);
            }
        }

        /// <summary>
        /// 将黑矛兵的剑盾替换为长矛
        /// </summary>
        private static void ApplyWeaponSwap(Agent agent)
        {
            // 1. 禁用盾牌和剑
            foreach (Transform child in agent.GetComponentsInChildren<Transform>(true))
            {
                if (ReferenceEquals(child, null) || child == agent.transform) continue;
                string name = child.name.ToLower();
                if (name.Contains("shield") || name.Contains("盾") || name.Contains("sword") || name.Contains("剑"))
                {
                    child.gameObject.SetActive(false);
                    LogInfo("Disabled: '" + child.name + "'");
                }
            }

            // 2. 如果有 Pikeman 模板，复制其 Spear 子对象
            if (!ReferenceEquals(_pikemanPrefab, null))
            {
                // 查找模板中的 Spear
                Transform spearTemplate = null;
                foreach (Transform child in _pikemanPrefab.GetComponentsInChildren<Transform>(true))
                {
                    if (ReferenceEquals(child, null) || child == _pikemanPrefab.transform) continue;
                    string name = child.name.ToLower();
                    if (name.Contains("spear") || name.Contains("长矛") || name.Contains("矛") || name.Contains("pike") || name.Contains("weapon"))
                    {
                        spearTemplate = child;
                        break;
                    }
                }

                if (!ReferenceEquals(spearTemplate, null))
                {
                    // 深拷贝 spear 组件
                    GameObject spearCopy = Instantiate(spearTemplate.gameObject, agent.transform);
                    spearCopy.name = spearTemplate.name;
                    spearCopy.transform.localPosition = spearTemplate.localPosition;
                    spearCopy.transform.localRotation = spearTemplate.localRotation;
                    spearCopy.transform.localScale = spearTemplate.localScale;
                    spearCopy.SetActive(true);
                    LogInfo("Cloned '" + spearTemplate.name + "' from Pikeman prefab.");
                }
                else
                {
                    LogWarn("No Spear child found in Pikeman prefab.");
                }
            }
        }

        // ============ VikingReference 绑定 ============

        private static void UpdateVikingReference(Agent agent)
        {
            UnityEngine.Object blackRef;
            if (!LevelStateObjectReferences.dict.TryGetValue(BlackSpearmanRefName, out blackRef)) return;
            VikingReference newRef = blackRef as VikingReference;
            if (ReferenceEquals(newRef, null)) return;
            VikingAgent va = agent.GetComponent<VikingAgent>();
            if (ReferenceEquals(va, null)) return;
            FieldInfo f = typeof(VikingAgent).GetField("vikingReference", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(f, null)) f.SetValue(va, newRef);
        }

        private static void ScaleFloatArray(float[] arr, float mult)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++) arr[i] *= mult;
        }

        // ============ 诊断 ============

        private static void RunFirstConversionDiagnostic(Agent agent)
        {
            List<string> l = new List<string>();
            l.Add("===== BlackSpearman v1.3 Diagnostic =====");

            Swordsman sw = agent.brain as Swordsman;
            l.Add(!ReferenceEquals(sw, null)
                ? "  [PASS] Brain: Swordsman. dmg[0]=" + sw.damageLevels[0].ToString("F2") + " knock[0]=" + sw.knockbackLevels[0].ToString("F2")
                : "  [FAIL] Brain: " + (agent.brain != null ? agent.brain.GetType().FullName : "null"));

            Armor armor = agent.GetComponent<Armor>();
            if (!ReferenceEquals(armor, null))
            {
                FieldInfo af = typeof(Armor).GetField("armor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                float[] v = !ReferenceEquals(af, null) ? af.GetValue(armor) as float[] : null;
                l.Add(v != null ? "  [PASS] Armor: [" + v[0].ToString("F2") + "," + v[1].ToString("F2") + "," + v[2].ToString("F2") + "]" : "  [WARN] Armor field null");
            }
            else l.Add("  [FAIL] Armor: not found.");

            // 子对象结构
            List<string> childNames = new List<string>();
            foreach (Transform child in agent.transform)
            {
                if (!ReferenceEquals(child, null))
                    childNames.Add(child.name + "(active=" + child.gameObject.activeSelf.ToString() + ")");
            }
            l.Add("  [INFO] Children: [" + string.Join(", ", childNames.ToArray()) + "]");

            // 深度搜索
            List<string> deepChildren = new List<string>();
            foreach (Transform child in agent.GetComponentsInChildren<Transform>(true))
            {
                if (!ReferenceEquals(child, null) && child != agent.transform)
                    deepChildren.Add(child.name);
            }
            l.Add("  [INFO] DeepChildren: [" + string.Join(", ", deepChildren.ToArray()) + "]");

            l.Add("  [INFO] " + (ConversionChance * 100f).ToString("F0") + "% chance, Scale×" + ScaleMultiplier.ToString());
            l.Add("  [INFO] PikemanTemplate: " + (!ReferenceEquals(_pikemanPrefab, null) ? "FOUND" : "NULL"));
            l.Add("=========================================");
            foreach (string line in l)
                LogInfo(line);
        }

        private void LogEnvironmentInfo()
        {
            try
            {
                Logger.LogInfo("[BlackSpearman] BepInEx " + typeof(BaseUnityPlugin).Assembly.GetName().Version + ", CLR " + Environment.Version);
                Logger.LogInfo("[BlackSpearman] Types: Faction=" + (!ReferenceEquals(typeof(Faction), null)) + " VikingAgent=" + (!ReferenceEquals(typeof(VikingAgent), null)) + " Swordsman=" + (!ReferenceEquals(typeof(Swordsman), null)));
            }
            catch { }
        }

        // ============ 日志工具 ============

        private static void LogInfo(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogInfo("[BlackSpearman] " + msg); }
        private static void LogWarn(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogWarning("[BlackSpearman] " + msg); }
        private static void LogErr(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogError("[BlackSpearman] " + msg); }
    }
}