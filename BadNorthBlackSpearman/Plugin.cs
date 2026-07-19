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

            // ⭐ 修复击杀重复计数：改为非 SwordShield 类型
            // 尝试通过反射设置 VikingAgent.Type 为大于现有enum值的数值
            TryChangeVikingType(newRef);

            SetLevelExpr(newGo.AddComponent<LevelRule>(), _levelRuleConditionField, "true");
            SetLevelExpr(newGo.AddComponent<LevelGuessable>(), _levelGuessableProbabilityField, "1");

            LevelStateObjectReferences.AddToDict(newRef);
            SharedLogger?.LogInfo($"[BlackSpearman] Registered independent VikingReference: '{BlackSpearmanRefName}'.");
        }

        // ⭐ 尝试改变 VikingAgent.Type 以修复击杀重复计数
        private static void TryChangeVikingType(VikingReference vref)
        {
            try
            {
                var vf = typeof(VikingReference).GetField("viking",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(vf, null)) return;
                var vikingObj = vf.GetValue(vref);
                if (ReferenceEquals(vikingObj, null)) return;

                var vikingType = vikingObj.GetType();
                var vaField = vikingType.GetField("agent",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(vaField, null)) return;
                var agent = vaField.GetValue(vikingObj) as Agent;
                if (ReferenceEquals(agent, null)) return;

                var va = agent.GetComponent<VikingAgent>();
                if (ReferenceEquals(va, null)) return;

                // VikingAgent.Type 是枚举，尝试直接用反射设成int值（如99 = Brute等）
                var typeField = typeof(VikingAgent).GetField("type",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(typeField, null))
                {
                    int bruteValue = 3; // 通常 Brute = 3
                    typeField.SetValue(va, bruteValue);
                    SharedLogger?.LogInfo($"[BlackSpearman] Changed VikingAgent.type to {bruteValue} (Brute) to avoid double counting.");
                }
            }
            catch (Exception ex)
            {
                SharedLogger?.LogWarning($"[BlackSpearman] TryChangeVikingType failed: {ex.Message}");
            }
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

            ApplyPikemanSprite(agent);

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
        // 武器外观替换
        // ==============================

        private static bool _pikemanSpriteAttempted;
        private static Sprite _pikemanSprite;
        private static Texture2D _pikemanSprite2;
        private static bool _pikemanFound;

        private static void ApplyPikemanSprite(Agent agent)
        {
            if (!_pikemanSpriteAttempted)
            {
                _pikemanSpriteAttempted = true;
                CachePikemanSprite();
            }

            if (!_pikemanFound)
            {
                // 找不到长矛兵，尝试直接禁用盾牌子对象
                DisableShieldChild(agent);
                return;
            }

            // 遍历所有子组件，找到 SpriteAnimator 替换
            foreach (var comp in agent.GetComponentsInChildren<Component>(true))
            {
                if (ReferenceEquals(comp, null)) continue;
                var tn = comp.GetType().FullName;
                if (tn == null || !tn.EndsWith(".SpriteAnimator")) continue;

                try
                {
                    if (!ReferenceEquals(_pikemanSprite, null))
                    {
                        var spriteProp = comp.GetType().GetProperty("sprite",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(spriteProp, null))
                            spriteProp.SetValue(comp, _pikemanSprite, null);
                    }

                    if (!ReferenceEquals(_pikemanSprite2, null))
                    {
                        var sprite2Prop = comp.GetType().GetProperty("sprite2",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(sprite2Prop, null))
                            sprite2Prop.SetValue(comp, _pikemanSprite2, null);
                    }

                    SharedLogger?.LogInfo("[BlackSpearman] Pikeman sprite applied to agent.");
                }
                catch (Exception ex)
                {
                    SharedLogger?.LogWarning($"[BlackSpearman] Sprite swap failed: {ex.Message}");
                }
                break;
            }
        }

        /// <summary>
        /// 找不到长矛兵时的回退方案：遍历子GameObject，禁用盾牌相关对象
        /// </summary>
        private static void DisableShieldChild(Agent agent)
        {
            int disabledCount = 0;
            foreach (Transform child in agent.transform)
            {
                if (ReferenceEquals(child, null)) continue;
                var name = child.name.ToLower();
                if (name.Contains("shield") || name.Contains("盾"))
                {
                    child.gameObject.SetActive(false);
                    disabledCount++;
                    SharedLogger?.LogInfo($"[BlackSpearman] Disabled shield child: '{child.name}'");
                }
            }
            if (disabledCount == 0)
            {
                // 深度搜索所有子对象
                foreach (Transform child in agent.GetComponentsInChildren<Transform>(true))
                {
                    if (ReferenceEquals(child, null) || child == agent.transform) continue;
                    var name = child.name.ToLower();
                    if (name.Contains("shield") || name.Contains("盾"))
                    {
                        child.gameObject.SetActive(false);
                        disabledCount++;
                        SharedLogger?.LogInfo($"[BlackSpearman] Disabled shield child (deep): '{child.name}'");
                        break;
                    }
                }
            }
            if (disabledCount == 0)
            {
                SharedLogger?.LogWarning("[BlackSpearman] No shield child found to disable.");
                // 输出所有子对象名称帮助诊断
                var names = new List<string>();
                foreach (Transform child in agent.transform)
                {
                    if (!ReferenceEquals(child, null))
                        names.Add($"'{child.name}'");
                }
                SharedLogger?.LogInfo("[BlackSpearman] Agent children: [" + string.Join(", ", names.ToArray()) + "]");
            }
        }

        private static FieldInfo _vrVikingField;
        private static FieldInfo _vrAgentField;
        private static FieldInfo _squadMinionPrefabField;

        private static void CachePikemanSprite()
        {
            try
            {
                if (ReferenceEquals(_vrVikingField, null))
                    _vrVikingField = typeof(VikingReference).GetField("viking",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(_vrAgentField, null))
                {
                    Type vikingType = null;
                    if (!ReferenceEquals(_vrVikingField, null))
                        vikingType = _vrVikingField.FieldType;
                    if (!ReferenceEquals(vikingType, null))
                        _vrAgentField = vikingType.GetField("agent",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }

                // 策略1：搜索 Faction 中的 EnglishSquad，找 Pikeman
                var faction = UnityEngine.Object.FindObjectOfType<Faction>();
                if (!ReferenceEquals(faction, null) && faction.allSquads != null)
                {
                    if (ReferenceEquals(_squadMinionPrefabField, null))
                        _squadMinionPrefabField = typeof(Squad).GetField("minionPrefab",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    foreach (var squad in faction.allSquads)
                    {
                        if (ReferenceEquals(squad, null)) continue;
                        
                        // 先通过 squad 的名称判断
                        var squadName = squad.name ?? "";
                        bool isPikeman = squadName.Contains("Pikeman") || squadName.Contains("Pike");

                        // 通过 minionPrefab 的 brain 类型判断
                        if (!isPikeman && !ReferenceEquals(_squadMinionPrefabField, null))
                        {
                            var minionPrefab = _squadMinionPrefabField.GetValue(squad) as Agent;
                            if (!ReferenceEquals(minionPrefab, null))
                            {
                                var brain = minionPrefab.brain;
                                if (!ReferenceEquals(brain, null) && brain.GetType().FullName != null &&
                                    brain.GetType().FullName.Contains("Pikeman"))
                                {
                                    isPikeman = true;
                                    SharedLogger?.LogInfo($"[BlackSpearman] Found Pikeman via brain: {brain.GetType().FullName}");
                                }
                            }
                        }

                        if (isPikeman)
                        {
                            // 从 squad 中提取 Sprite
                            // EnglishSquad 有 heroAgent 和 minionPrefab
                            var heroAgentField = typeof(Squad).GetField("heroAgent",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (!ReferenceEquals(heroAgentField, null))
                            {
                                var heroAgent = heroAgentField.GetValue(squad) as Agent;
                                if (!ReferenceEquals(heroAgent, null))
                                {
                                    ExtractSpriteFromAgent(heroAgent);
                                    if (!ReferenceEquals(_pikemanSprite, null))
                                    {
                                        _pikemanFound = true;
                                        SharedLogger?.LogInfo($"[BlackSpearman] Pikeman sprite found via squad '{squadName}' heroAgent.");
                                        return;
                                    }
                                }
                            }

                            if (!ReferenceEquals(_squadMinionPrefabField, null))
                            {
                                var minionPrefab = _squadMinionPrefabField.GetValue(squad) as Agent;
                                if (!ReferenceEquals(minionPrefab, null))
                                {
                                    ExtractSpriteFromAgent(minionPrefab);
                                    if (!ReferenceEquals(_pikemanSprite, null))
                                    {
                                        _pikemanFound = true;
                                        SharedLogger?.LogInfo($"[BlackSpearman] Pikeman sprite found via squad '{squadName}' minionPrefab.");
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }

                // 策略2：遍历 dict 找含 Pikeman 的 VikingReference
                foreach (var kvp in LevelStateObjectReferences.dict)
                {
                    var vr = kvp.Value as VikingReference;
                    if (ReferenceEquals(vr, null)) continue;
                    Agent agent = GetAgentFromVikingReference(vr);
                    if (ReferenceEquals(agent, null)) continue;
                    var brain = agent.brain;
                    if (!ReferenceEquals(brain, null) && brain.GetType().FullName != null &&
                        (brain.GetType().FullName.Contains("Pikeman") ||
                         brain.GetType().FullName.Contains("Spearman") ||
                         brain.GetType().FullName.Contains("Pike")))
                    {
                        ExtractSpriteFromAgent(agent);
                        if (!ReferenceEquals(_pikemanSprite, null))
                        {
                            _pikemanFound = true;
                            SharedLogger?.LogInfo($"[BlackSpearman] Pikeman sprite found via dict key '{kvp.Key}'.");
                            return;
                        }
                    }
                }

                // 策略3：直接搜索场景中所有 Agent 找 Pikeman brain
                var allAgents = UnityEngine.Object.FindObjectsOfType<Agent>();
                foreach (var a in allAgents)
                {
                    if (ReferenceEquals(a, null)) continue;
                    var b = a.brain;
                    if (ReferenceEquals(b, null) || b.GetType().FullName == null) continue;
                    if (b.GetType().FullName.Contains("Pikeman"))
                    {
                        ExtractSpriteFromAgent(a);
                        if (!ReferenceEquals(_pikemanSprite, null))
                        {
                            _pikemanFound = true;
                            SharedLogger?.LogInfo($"[BlackSpearman] Pikeman sprite found via scene Agent with Pikeman brain.");
                            return;
                        }
                    }
                }

                _pikemanFound = false;
                SharedLogger?.LogWarning("[BlackSpearman] Could not find any Pikeman reference for sprite cloning. Will attempt to disable shield instead.");
            }
            catch (Exception ex)
            {
                _pikemanFound = false;
                SharedLogger?.LogError($"[BlackSpearman] CachePikemanSprite failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static Agent GetAgentFromVikingReference(VikingReference vr)
        {
            if (ReferenceEquals(vr, null) || ReferenceEquals(_vrVikingField, null)) return null;
            var vikingObj = _vrVikingField.GetValue(vr);
            if (ReferenceEquals(vikingObj, null) || ReferenceEquals(_vrAgentField, null)) return null;
            return _vrAgentField.GetValue(vikingObj) as Agent;
        }

        private static void ExtractSpriteFromAgent(Agent source)
        {
            if (ReferenceEquals(source, null)) return;

            foreach (var comp in source.GetComponentsInChildren<Component>(true))
            {
                if (ReferenceEquals(comp, null)) continue;
                var tn = comp.GetType().FullName;
                if (tn == null || !tn.EndsWith(".SpriteAnimator")) continue;

                try
                {
                    var spriteProp = comp.GetType().GetProperty("sprite",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(spriteProp, null))
                        _pikemanSprite = spriteProp.GetValue(comp, null) as Sprite;

                    var sprite2Prop = comp.GetType().GetProperty("sprite2",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(sprite2Prop, null))
                        _pikemanSprite2 = sprite2Prop.GetValue(comp, null) as Texture2D;

                    if (!ReferenceEquals(_pikemanSprite, null))
                        SharedLogger?.LogInfo($"[BlackSpearman] Extracted sprite from agent: {source.name}");
                }
                catch (Exception ex)
                {
                    SharedLogger?.LogDebug($"[BlackSpearman] ExtractSprite: {ex.Message}");
                }
                break;
            }
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
                : "  [FAIL] Brain: " + (agent.brain != null ? agent.brain.GetType().FullName : "null"));

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

            // 输出子对象结构
            var childNames = new List<string>();
            foreach (Transform child in agent.transform)
            {
                if (!ReferenceEquals(child, null))
                    childNames.Add($"{child.name}(active={child.gameObject.activeSelf})");
            }
            l.Add("  [INFO] Children: [" + string.Join(", ", childNames.ToArray()) + "]");

            // 颜色诊断
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
            l.Add($"  [INFO] PikemanFound: {_pikemanFound}");
            l.Add($"  [INFO] MMHOOK: {_hooksRegistered}");
            l.Add("=========================================");
            SharedLogger?.LogInfo(string.Join("\n", new string[] { l.ToArray().Length > 0 ? l[0] : "" }));
            // 逐行输出解决 .NET 3.5 兼容问题
            foreach (var line in l)
                SharedLogger?.LogInfo(line);
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