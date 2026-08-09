using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.CampaignGeneration.CampaignAc3;
using Voxels.TowerDefense.RaidGeneration;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman
{
    [BepInPlugin("black.spearman", "Bad North - Black Spearman", "1.19")]
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
        private Harmony _harmony;

        private static FieldInfo _levelRuleConditionField;
        private static FieldInfo _levelGuessableProbabilityField;
        private static bool _levelFieldsCached;

        // 武器系统
        internal static bool WeaponCached;
        internal static GameObject CachedSpearAnim;
        internal static object CachedSpearSprite2;  // v1.18: Pikeman 的 sprite2（用于替换 SwordShield 的剑+盾）
        internal static Vector3 SpearLocalPos = Vector3.zero;
        internal static Vector3 SpearLocalScale = Vector3.one;
        internal static Quaternion SpearLocalRot = Quaternion.identity;
        private static int _weaponSearchAttempts;
        private const int MaxWeaponSearchAttempts = 30;
        private static bool _weaponNullLogged;

        private static bool _firstConversionDiagnosticDone;

        // ============ BepInEx ============

        private void Start()
        {
            Instance = this;
            SharedLogger = Logger;
            Logger.LogInfo("[BlackSpearman] ====== v1.19 (Sword Destroy + Shield Keep + Black Body + Icon Tint) ======");
            _harmony = new Harmony("black.spearman");
            _harmony.PatchAll(typeof(Patches));
            RegisterBlackSpearmanBrainPatches();
            
            // v1.18: 主动运行时诊断 — 扫描所有已加载 Assembly 中的 Spear 类型
            DumpSpearTypes();
            // v1.18: Hook Brain.Setup 以最早捕获 Spear brain
            PatchBrainSetup();
        }

        /// <summary>
        /// 注册 BlackSpearmanBrain 的 Harmony Patch
        /// （拦截 Swordsman.GetAttack + range 属性 getter）
        /// </summary>
        private void RegisterBlackSpearmanBrainPatches()
        {
            try
            {
                // v1.16: Use FlattenHierarchy + NonPublic to find GetAttack even if declared in parent class
                var getAttackMethod = typeof(Swordsman).GetMethod("GetAttack",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy,
                    null, new[] { typeof(Agent) }, null);
                if (!ReferenceEquals(getAttackMethod, null))
                {
                    var prefix = typeof(BlackSpearmanBrain).GetMethod("GetAttackPrefix",
                        BindingFlags.Public | BindingFlags.Static);
                    if (!ReferenceEquals(prefix, null))
                    {
                        _harmony.Patch(getAttackMethod, new HarmonyMethod(prefix));
                        LogInfo("[Brain] GetAttack patch OK: " + getAttackMethod.DeclaringType.Name + "." + getAttackMethod.Name);
                    }
                }
                else
                {
                    LogWarn("[Brain] GetAttack(Agent) NOT FOUND on Swordsman! Checking CloseCombatBrain...");
                    getAttackMethod = typeof(CloseCombatBrain).GetMethod("GetAttack",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy,
                        null, new[] { typeof(Agent) }, null);
                    if (!ReferenceEquals(getAttackMethod, null))
                    {
                        var prefix = typeof(BlackSpearmanBrain).GetMethod("GetAttackPrefix",
                            BindingFlags.Public | BindingFlags.Static);
                        if (!ReferenceEquals(prefix, null))
                        {
                            _harmony.Patch(getAttackMethod, new HarmonyMethod(prefix));
                            LogInfo("[Brain] GetAttack patch OK (CloseCombatBrain): " + getAttackMethod.Name);
                        }
                    }
                    else
                    {
                        LogErr("[Brain] GetAttack(Agent) NOT FOUND anywhere! BlackSpearmanBrain will NOT work!");
                    }
                }

                var rangeProp = typeof(Swordsman).GetProperty("range",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(rangeProp, null))
                {
                    var getter = rangeProp.GetGetMethod(true);
                    if (!ReferenceEquals(getter, null))
                    {
                        var prefixRange = typeof(BlackSpearmanBrain).GetMethod("RangeGetterPrefix",
                            BindingFlags.Public | BindingFlags.Static);
                        if (!ReferenceEquals(prefixRange, null))
                            _harmony.Patch(getter, new HarmonyMethod(prefixRange));
                    }
                }
                LogInfo("[Brain] Harmony patches registered for GetAttack + range");
            }
            catch (Exception ex) { LogErr("[Brain] Patch registration error: " + ex); }
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        // ============ Harmony Patches ============

        private static class Patches
        {
            [HarmonyPatch(typeof(GameSetup), "Awake")]
            [HarmonyPostfix]
            private static void GameSetupAwakePostfix(GameSetup __instance)
            {
                if (Instance == null) return;
                try
                {
                    Instance.EnsureSwordShieldAlwaysAvailable();
                    Instance.RegisterBlackSpearmanReference();
                    // 预加载 Pikeman 武器素材（从 VikingReference 预制件）
                    SearchForPikemanWeapon();
                }
                catch (Exception ex) { LogErr("GameSetup: " + ex); }
            }

            [HarmonyPatch(typeof(Landing), nameof(Landing.Spawn))]
            [HarmonyPostfix]
            private static void LandingSpawnPostfix(Landing __instance, ref Longship __result)
            {
                try
                {
                    if (!ReferenceEquals(__result, null) && __result.agents != null)
                        foreach (var a in __result.agents)
                            if (!ReferenceEquals(a, null)) OnAgentSpawnedHandler(a);
                }
                catch (Exception ex) { LogErr("Landing: " + ex); }
            }

            // 修复 Full Unlock Mod 导致的 SquadSize 升级等级越界崩溃
            [HarmonyPatch(typeof(Voxels.TowerDefense.Upgrades.SquadSizeUpgrade), "OnAppliedToSquad")]
            [HarmonyPrefix]
            private static bool SquadSizeUpgrade_OnAppliedToSquad_Prefix(
                object __instance, ref int upgradeLevel)
            {
                try
                {
                    if (upgradeLevel < 0)
                    {
                        LogWarn("[SquadSizeFix] upgradeLevel=" + upgradeLevel + " < 0, clamping to 0");
                        upgradeLevel = 0;
                        return true;
                    }

                    var fields = __instance.GetType().GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                    int[] sizeArray = null;
                    foreach (var f in fields)
                    {
                        if (f.FieldType.Name == "Int32[]" || f.FieldType.FullName == "System.Int32[]")
                        {
                            sizeArray = f.GetValue(__instance) as int[];
                            if (sizeArray != null && sizeArray.Length > 0) break;
                        }
                    }

                    if (sizeArray == null || sizeArray.Length == 0) return true;

                    if (upgradeLevel >= sizeArray.Length)
                    {
                        LogWarn("[SquadSizeFix] upgradeLevel=" + upgradeLevel +
                            " >= arrayLength=" + sizeArray.Length + ", clamping to " + (sizeArray.Length - 1));
                        upgradeLevel = sizeArray.Length - 1;
                    }
                }
                catch (Exception ex)
                {
                    LogErr("[SquadSizeFix] Error: " + ex.Message);
                }
                return true;
            }
        }

        // ============ 武器搜索 ============

        private static bool _dictDiagnosticDone;

        internal static void SearchForPikemanWeapon()
        {
            // v1.18: Check CachedSpearAnim still valid (Unity destroyed-object null)
            if (WeaponCached && CachedSpearAnim != null) return;

            // v1.18: 移除硬性最大重试限制，改为按需重试
            if (_weaponSearchAttempts >= MaxWeaponSearchAttempts * 10)
            {
                if (_weaponSearchAttempts == MaxWeaponSearchAttempts * 10)
                    LogWarn("[WEAPON] Search limit reached, will retry on new spawns (every 300 frames)");
                _weaponSearchAttempts++;
                if (Time.frameCount % 300 != 0) return;
            }
            _weaponSearchAttempts++;

            try
            {
                // v1.18: 一次性诊断 — 导出 dict 所有键名
                if (!_dictDiagnosticDone)
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var key in LevelStateObjectReferences.dict.Keys)
                        {
                            if (sb.Length > 0) sb.Append(", ");
                            sb.Append(key);
                        }
                        if (sb.Length > 0)
                        {
                            _dictDiagnosticDone = true;
                            LogInfo("[WEAPON] Dict keys: [" + sb.ToString() + "]");
                        }
                        else
                        {
                            // v1.18: dict 为空，延迟诊断到下次调用
                            LogWarn("[WEAPON] Dict is empty at frame " + Time.frameCount + " — will retry");
                        }
                    }
                    catch (Exception ex) { LogErr("[WEAPON] DictDiag: " + ex.Message); }
                }

                // 方式1: VikingReference 预制件
                if (TryExtractFromVikingRef())
                {
                    LogInfo("[WEAPON] Cached from VikingReference prefab");
                    ApplyWeaponToAllConverted();
                    return;
                }

                // 方式2: 活跃 Agent 中的 Spear brain
                if (SearchAndExtractFromLiveAgents())
                {
                    ApplyWeaponToAllConverted();
                    return;
                }

                // v1.18: 方式3: Resources 资源
                if (TryExtractFromResources())
                {
                    LogInfo("[WEAPON] Cached via Resources!");
                    ApplyWeaponToAllConverted();
                    return;
                }

                // v1.18: 所有方法均失败 — 首次时记录诊断
                if (_weaponSearchAttempts <= 3 || _weaponSearchAttempts % 120 == 0)
                    LogWarn("[WEAPON] All " + _weaponSearchAttempts + " search attempts failed — no Spear/Pike source found yet");
            }
            catch (Exception ex) { LogErr("[WEAPON] " + ex.Message); }
        }

        /// <summary>
        /// v1.18: 搜索活跃 Agent 中的 Spear/Pike brain（扩展匹配）
        /// </summary>
        private static int _lastBrainDiagAttempt;
        private static bool SearchAndExtractFromLiveAgents()
        {
            try
            {
                var allAgents = UnityEngine.Object.FindObjectsOfType<Agent>();
                
                // v1.18: 周期性诊断（每 30 次搜索或首次），确保能捕获延迟出现的 Agent
                if (_weaponSearchAttempts <= 2 || _weaponSearchAttempts - _lastBrainDiagAttempt >= 30)
                {
                    _lastBrainDiagAttempt = _weaponSearchAttempts;
                    var brainTypes = new HashSet<string>();
                    int nonVikingCount = 0;
                    foreach (var a in allAgents)
                    {
                        if (ReferenceEquals(a, null) || a.isViking) continue;
                        nonVikingCount++;
                        var bb = a.brain;
                        if (!ReferenceEquals(bb, null))
                            brainTypes.Add(bb.GetType().Name + (a.name.Contains("Pike") || a.name.Contains("Spear") ? "[" + a.name + "]" : ""));
                    }
                    if (brainTypes.Count > 0)
                    {
                        var sbDiag = new System.Text.StringBuilder();
                        foreach (var bt in brainTypes) { if (sbDiag.Length > 0) sbDiag.Append(", "); sbDiag.Append(bt); }
                        LogInfo("[WEAPON] Attempt#" + _weaponSearchAttempts + " Non-Viking agents=" + nonVikingCount + " brains: [" + sbDiag.ToString() + "]");
                    }
                    else if (_weaponSearchAttempts <= 2)
                        LogWarn("[WEAPON] No non-Viking Agents with brain found in scene!");
                }

                foreach (var a in allAgents)
                {
                    if (ReferenceEquals(a, null) || a.isViking) continue;
                    var b = a.brain;
                    if (ReferenceEquals(b, null)) continue;
                    var n = b.GetType().Name;
                    if (n == "Spear" || n == "Spearman" || n == "Pikeman"
                        || n.Contains("Spear") || n.Contains("Pike"))
                    {
                        LogInfo("[WEAPON] Live Spear brain=" + n + " on " + a.name);
                        if (ExtractWeapon(b)) { LogInfo("[WEAPON] Cached: " + n); return true; }
                    }
                }

                // v1.18: 兜底 — 在所有 Agent（含 Viking）中反射查找 spearAnim 字段
                foreach (var a in allAgents)
                {
                    if (ReferenceEquals(a, null)) continue;
                    var b = a.brain;
                    if (ReferenceEquals(b, null)) continue;
                    // 跳过已验证过的类型
                    var bn = b.GetType().Name;
                    if (bn == "Spear" || bn == "Spearman" || bn == "Pikeman"
                        || bn.Contains("Spear") || bn.Contains("Pike")) continue;
                    
                    var spearAnimField = b.GetType().GetField("spearAnim",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(spearAnimField, null))
                    {
                        var val = spearAnimField.GetValue(b);
                        if (!ReferenceEquals(val, null))
                        {
                            LogInfo("[WEAPON] Found spearAnim via reflection on brain=" + bn + " agent=" + a.name);
                            if (ExtractWeapon(b)) { LogInfo("[WEAPON] Cached via reflection: " + bn); return true; }
                        }
                    }
                }
            }
            catch (Exception ex) { LogErr("[WEAPON] Live: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// v1.18: 通过 Resources 查找长矛模型
        /// </summary>
        private static bool TryExtractFromResources()
        {
            try
            {
                // 尝试已知路径
                foreach (var path in new[] { "Weapons/Spear", "Prefabs/Spear", "English/Pikeman" })
                {
                    var prefab = Resources.Load<GameObject>(path);
                    if (prefab == null) continue;
                    foreach (var brain in prefab.GetComponentsInChildren<Brain>(true))
                    {
                        if (brain == null) continue;
                        if (brain.GetType().Name.Contains("Spear") || brain.GetType().Name.Contains("Pike"))
                        { if (ExtractWeapon(brain)) return true; }
                    }
                    var bs = prefab.GetComponentInChildren<BatchedSprite>(true);
                    if (bs != null)
                    {
                        CachedSpearAnim = bs.gameObject;
                        SpearLocalPos = bs.transform.localPosition;
                        SpearLocalRot = bs.transform.localRotation;
                        SpearLocalScale = bs.transform.localScale;
                        WeaponCached = true;
                        LogInfo("[WEAPON] BatchedSprite: " + path);
                        return true;
                    }
                }
                // 模糊匹配 Resources
                foreach (var res in Resources.LoadAll<GameObject>(""))
                {
                    if (res == null) continue;
                    if (!res.name.ToLower().Contains("spear") && !res.name.ToLower().Contains("pike")) continue;
                    var bs = res.GetComponentInChildren<BatchedSprite>(true);
                    if (bs == null) continue;
                    CachedSpearAnim = bs.gameObject;
                    SpearLocalPos = bs.transform.localPosition;
                    SpearLocalRot = bs.transform.localRotation;
                    SpearLocalScale = bs.transform.localScale;
                    WeaponCached = true;
                    LogInfo("[WEAPON] Fuzzy: " + res.name);
                    return true;
                }
            }
            catch (Exception ex) { LogErr("[WEAPON] Resources: " + ex.Message); }
            return false;
        }

        private static bool TryExtractFromVikingRef()
        {
            try
            {
                // v1.18: 尝试多种可能的键名
                string[] candidateKeys = {
                    "Viking_Pikeman", "English_Pikeman",
                    "Viking_Spear", "English_Spear",
                    "Viking_Spearman", "English_Spearman",
                    "Pikeman", "Spear", "Spearman",
                };

                foreach (var key in candidateKeys)
                {
                    UnityEngine.Object obj;
                    if (!LevelStateObjectReferences.dict.TryGetValue(key, out obj)) continue;

                    var vr = obj as VikingReference;
                    if (ReferenceEquals(vr, null)) continue;
                    var vc = vr.vikingClone;
                    if (ReferenceEquals(vc, null)) continue;
                    var prefabAgent = vc.agent;
                    if (ReferenceEquals(prefabAgent, null)) continue;
                    var brain = prefabAgent.brain;
                    if (ReferenceEquals(brain, null)) continue;

                    var brainTypeName = brain.GetType().Name;
                    LogInfo("[WEAPON] VR key=" + key + " brain=" + brainTypeName);

                    if (brainTypeName != "Spear" && brainTypeName != "Spearman"
                        && !brainTypeName.Contains("Spear") && !brainTypeName.Contains("Pike"))
                        continue;

                    if (ExtractWeapon(brain))
                    {
                        LogInfo("[WEAPON] From VR key=" + key);
                        return true;
                    }
                }

                // v1.18: 遍历所有 VR 条目，查找 spearAnim 字段
                if (_weaponSearchAttempts == 1 || _weaponSearchAttempts % 30 == 0)
                {
                    foreach (var kvp in LevelStateObjectReferences.dict)
                    {
                        var vr2 = kvp.Value as VikingReference;
                        if (vr2 == null || vr2.vikingClone == null || vr2.vikingClone.agent == null) continue;
                        var brain2 = vr2.vikingClone.agent.brain;
                        if (brain2 == null) continue;
                        var saf = brain2.GetType().GetField("spearAnim",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(saf, null) && saf.GetValue(brain2) != null)
                        {
                            LogInfo("[WEAPON] spearAnim in dict key=" + kvp.Key + " brain=" + brain2.GetType().Name);
                            if (ExtractWeapon(brain2)) return true;
                        }
                    }
                }
            }
            catch (Exception ex) { LogErr("[WEAPON] VikingRef: " + ex.Message); }
            return false;
        }

        private static void ApplyWeaponToAllConverted()
        {
            if (!WeaponCached) return;
            int count = 0;
            foreach (var agent in ConvertedAgents)
            {
                if (!ReferenceEquals(agent, null) && agent.isViking)
                {
                    ReapplyWeaponIfNeeded(agent);
                    ApplySprite2Replacement(agent);
                    ApplyBlackColor(agent); // v1.19: 追溯应用黑色外观
                    count++;
                }
            }
            if (count > 0)
                LogInfo("[WEAPON] Applied spear+sprite2+black to " + count + " BlackSpearmans");
        }

        /// <summary>
        /// v1.18: 将 Agent 的 SpriteAnimator.sprite2 替换为 Pikeman 的 sprite2
        /// </summary>
        private static void ApplySprite2Replacement(Agent agent)
        {
            if (CachedSpearSprite2 == null) return;
            var allSA = agent.GetComponentsInChildren<SpriteAnimator>(true);
            if (allSA == null) return;
            foreach (var sa in allSA)
            {
                if (ReferenceEquals(sa, null)) continue;
                
                // 方式 A: SetSprite2 方法（推荐）
                try
                {
                    var setMethod = typeof(SpriteAnimator).GetMethod("SetSprite2",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(setMethod, null))
                    {
                        setMethod.Invoke(sa, new[] { CachedSpearSprite2 });
                        LogInfo("[WEAPON] SetSprite2(pikeman) on " + sa.name + " retroactively");
                        return;
                    }
                }
                catch (Exception ex) { LogErr("[WEAPON] SetSprite2 retro: " + ex.Message); }

                // 方式 B: 字段赋值
                try
                {
                    var field = typeof(SpriteAnimator).GetField("sprite2",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(field, null))
                    {
                        field.SetValue(sa, CachedSpearSprite2);
                        LogInfo("[WEAPON] sprite2 field → pikeman on " + sa.name + " retroactively");
                        return;
                    }
                }
                catch (Exception ex) { LogErr("[WEAPON] sprite2 field retro: " + ex.Message); }
            }
        }

        private static bool ExtractWeapon(Brain brain)
        {
            var saf = brain.GetType().GetField("spearAnim", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ReferenceEquals(saf, null)) { LogErr("[WEAPON] No spearAnim field"); return false; }

            var spearAnim = saf.GetValue(brain) as Transform;
            if (ReferenceEquals(spearAnim, null)) { LogErr("[WEAPON] spearAnim is null"); return false; }

            var bs = spearAnim.GetComponentInChildren<BatchedSprite>(true);
            if (ReferenceEquals(bs, null)) { LogErr("[WEAPON] spearAnim has no BatchedSprite child"); return false; }

            CachedSpearAnim = spearAnim.gameObject;
            SpearLocalPos = spearAnim.localPosition;
            SpearLocalRot = spearAnim.localRotation;
            SpearLocalScale = spearAnim.localScale;

            // v1.18: 同时捕获 Pikeman 的 sprite2 用于替换 SwordShield 的剑+盾
            var pikemanAgent = brain.GetComponent<Agent>();
            if (!ReferenceEquals(pikemanAgent, null))
            {
                var pikemanSA = pikemanAgent.GetComponentInChildren<SpriteAnimator>(true);
                if (!ReferenceEquals(pikemanSA, null))
                {
                    try
                    {
                        var s2Field = typeof(SpriteAnimator).GetField("sprite2",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(s2Field, null))
                        {
                            CachedSpearSprite2 = s2Field.GetValue(pikemanSA);
                            LogInfo("[WEAPON] Captured Pikeman sprite2: " + (CachedSpearSprite2 != null ? CachedSpearSprite2.ToString() : "null"));
                        }
                    }
                    catch (Exception ex) { LogErr("[WEAPON] sprite2 capture: " + ex.Message); }
                }
            }

            WeaponCached = true;
            LogInfo("[WEAPON] Weapon cached via spearAnim->BatchedSprite: " + CachedSpearAnim.name);
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
            LogInfo("[SPAWN] Converting " + agent.name + " to BlackSpearman (#" + ConvertedAgents.Count + ")");
            try { ApplyBlackSpearman(agent); } catch (Exception ex) { LogErr("Apply: " + ex); }
            if (Instance != null) Instance._totalConvertedCount++;
        }

        // ============ 转化链 ============

        internal static void ApplyBlackSpearman(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return;

            // 武器搜索重试 + 应用
            SearchForPikemanWeapon();

            ReapplyWeaponIfNeeded(agent);

            // 数值
            // 移除原有武器（仅剑的渲染子对象，保留盾牌）


            RemoveOriginalWeapons(agent);

            // 黑色外观（敌方风格，与玩家 Pikeman 区分）
            ApplyBlackColor(agent);

            agent.scale *= ScaleMultiplier;
            var s = agent.brain as Swordsman;
            if (!ReferenceEquals(s, null))
            {
                ScaleFloatArray(s.damageLevels, DamageMultiplier);
                ScaleFloatArray(s.knockbackLevels, KnockbackMultiplier);
            }
            ApplyArmor(agent);
            ApplySpearCombatStats(agent);

            // 技能组件（IBrainAction 注入 — v1.15 改进）
            // SpearChargeComponent 通过 IBrainAction.MaybeAct 由 Brain 调度
            var charge = SpearChargeComponent.AddTo(agent);
            if (!ReferenceEquals(charge, null)) charge.Setup(agent);
            // SpearStabAction 通过 IBrainAction.MaybeAct 由 Brain 调度
            agent.gameObject.AddComponent<SpearStabAction>();

            // v1.16: 手动注册 IBrainAction 到 Swordsman.actions 列表
            // （Brain.Setup() 已执行，新增组件不会自动被收集）
            RegisterBrainActions(agent);

            UpdateVikingReference(agent);

            if (!_firstConversionDiagnosticDone)
            {
                _firstConversionDiagnosticDone = true;
                LogInfo("===== v1.19 (Sword Destroy + Shield Keep + Black Body + Icon Tint) =====");
                LogInfo("  WeaponCached: " + WeaponCached);
                LogInfo("  SearchAttempts: " + _weaponSearchAttempts);
                LogInfo("  Brain: GetAttack() -> Spear-style 4D vector");
                LogInfo("  Charge: IBrainAction -> Swordsman.actions");
                LogInfo("  Stab: IBrainAction -> Swordsman.actions");
                LogInfo("  Body: Black color (B=0.02, R/G preserved) + Shield kept");
                LogInfo("  Icon: Tinted for preview/kill-stat differentiation");
                LogInfo("  ColorPersistence: LateUpdate re-check in SpearChargeComponent");
                BlackSpearmanBrain.DumpConvertedAgents();
            }
        }

        // ============ 黑色外观（敌方风格） ============

        /// <summary>
        /// 将 Agent 身上所有 BatchedSprite 的颜色改为暗黑色调。
        /// 保留 R/G 通道（UV 编码），仅将 B 通道降至 0.02，模拟敌方单位的黑色外观。
        /// 与玩家 Pikeman 形成明显区分。
        /// </summary>
        private static void ApplyBlackColor(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return;
            try
            {
                var allBS = agent.GetComponentsInChildren<BatchedSprite>(true);
                if (allBS == null || allBS.Length == 0) return;

                var colorProp = typeof(BatchedSprite).GetProperty("color",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(colorProp, null)) return;

                int modified = 0;
                foreach (var bs in allBS)
                {
                    if (ReferenceEquals(bs, null)) continue;
                    try
                    {
                        var oldColor = (Color)colorProp.GetValue(bs, null);
                        // v1.19: 更激进的暗色 — 不仅 B=0.02，R/G 也压暗至 30%
                        // 保留 R/G 的比值关系（UV 编码），但整体压暗
                        // 同时 B 通道设 0 以完全消除蓝色调（敌方单位特征）
                        float r = oldColor.r * 0.35f;
                        float g = oldColor.g * 0.35f;
                        colorProp.SetValue(bs, new Color(r, g, 0.01f, oldColor.a), null);
                        modified++;
                    }
                    catch { }
                }

                if (modified > 0)
                    LogInfo("[COLOR] Applied black body to " + modified + " BatchedSprites on " + agent.name);
            }
            catch (Exception ex) { LogErr("[COLOR] ApplyBlackColor error: " + ex.Message); }
        }

        /// <summary>
        /// v1.18: 清除原有武器渲染 — 替换 sprite2（剑 → 长矛身体姿态）+ 仅禁用剑子对象
        /// Hierarchy from log: BodyAnim/BodySprite/BodySprite[SpriteAnimator] + Weapon child
        /// Shield: BounceAnim/ShieldAimer/ShieldAnim — 保留！
        /// </summary>
        private static void RemoveOriginalWeapons(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return;
            try
            {
                bool cleared = false;

                // Step 1: 用 Pikeman sprite2 替换 SwordShield 的 sprite2（剑+盾 → 长矛姿势）
                var allSA = agent.GetComponentsInChildren<SpriteAnimator>(true);
                if (allSA != null && CachedSpearSprite2 != null)
                {
                    foreach (var sa in allSA)
                    {
                        if (ReferenceEquals(sa, null)) continue;
                        
                        // 方式 A: SetSprite2(pikemanSprite2) 方法
                        try
                        {
                            var setMethod = typeof(SpriteAnimator).GetMethod("SetSprite2",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (!ReferenceEquals(setMethod, null))
                            {
                                setMethod.Invoke(sa, new[] { CachedSpearSprite2 });
                                cleared = true;
                                LogInfo("[WEAPON] Replaced sprite2 with Pikeman's on " + sa.name 
                                    + " path=" + GetTransformPath(sa.transform, agent.transform));
                                continue;
                            }
                        }
                        catch (Exception ex) { LogErr("[WEAPON] SetSprite2: " + ex.Message); }

                        // 方式 B: 直接设 sprite2 字段
                        try
                        {
                            var field = typeof(SpriteAnimator).GetField("sprite2",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (!ReferenceEquals(field, null))
                            {
                                field.SetValue(sa, CachedSpearSprite2);
                                cleared = true;
                                LogInfo("[WEAPON] sprite2 field → Pikeman on " + sa.name 
                                    + " path=" + GetTransformPath(sa.transform, agent.transform));
                                continue;
                            }
                        }
                        catch (Exception ex) { LogErr("[WEAPON] sprite2 field: " + ex.Message); }
                    }
                }

                // Fallback: 没有 Pikeman sprite2 时不操作（设 null 会导致死亡 NPE）
                if (!cleared && allSA != null && CachedSpearSprite2 == null)
                {
                    LogInfo("[WEAPON] sprite2 left as-is (no Pikeman ref, null causes death crash)");
                }

                // Step 2: 递归禁用武器相关子对象（仅剑，保留盾牌）
                int disabled = DisableWeaponChildren(agent.transform);

                // Step 3: 盾渲染层次 (BounceAnim) — 保留！黑矛兵持盾+长矛
                // 不再禁用 BounceAnim，盾牌保留

                if (cleared || disabled > 0)
                {
                    // v1.19: 额外按 BatchedSprite.sprite 名称查找并禁用剑
                    int spriteDisabled = DisableSwordBatchedSprites(agent);
                    if (!cleared) LogInfo("[WEAPON] sprite2 not found, disabled " + disabled + " sword objects + " + spriteDisabled + " sword sprites instead");
                    else if (disabled > 0 || spriteDisabled > 0)
                        LogInfo("[WEAPON] sprite2 replaced, additionally disabled " + disabled + " objects + " + spriteDisabled + " sword sprites");
                    return;
                }

                // Step 4: 完全失败 → 层级诊断（仅首次）
                if (!_hierarchyDiagDone)
                {
                    _hierarchyDiagDone = true;
                    var sb = new System.Text.StringBuilder();
                    DumpTransformHierarchy(agent.transform, "", sb, 4);
                    LogWarn("[WEAPON] Agent hierarchy for " + agent.name + ":\n" + sb.ToString());
                }
            }
            catch (Exception ex) { LogErr("[WEAPON] RemoveWeapons: " + ex.Message); }
        }

        /// <summary>
        /// 递归搜索并禁用武器子对象
        /// </summary>
        private static int DisableWeaponChildren(Transform root)
        {
            int count = 0;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var c = root.GetChild(i);
                if (ReferenceEquals(c, null)) continue;
                var cn = c.name.ToLower();

                if (cn.Contains("sword") || cn.Contains("weapon")
                    || cn.Contains("右") || cn.Contains("左") || cn.Contains("r_weapon")
                    || cn.Contains("l_weapon"))
                {
                    c.gameObject.SetActive(false);
                    count++;
                    continue; // 已禁用，无需递归
                }

                // 递归
                count += DisableWeaponChildren(c);
            }
            return count;
        }

        /// <summary>
        /// v1.19: 按 BatchedSprite.sprite 名称查找并禁用剑的 BatchedSprite
        /// （GameObject 名称匹配不到的剑可能在 BatchedSprite 层级）
        /// </summary>
        private static int DisableSwordBatchedSprites(Agent agent)
        {
            int count = 0;
            try
            {
                if (!_batchedSpriteSpritePropCached)
                {
                    _batchedSpriteSpritePropCached = true;
                    _batchedSpriteSpriteProp = typeof(BatchedSprite).GetProperty("sprite",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (ReferenceEquals(_batchedSpriteSpriteProp, null)) return 0;

                var allBS = agent.GetComponentsInChildren<BatchedSprite>(true);
                if (allBS == null || allBS.Length == 0) return 0;

                foreach (var bs in allBS)
                {
                    if (ReferenceEquals(bs, null)) continue;
                    try
                    {
                        var sprite = _batchedSpriteSpriteProp.GetValue(bs, null) as Sprite;
                        if (ReferenceEquals(sprite, null)) continue;
                        string sn = sprite.name.ToLower();
                        // 剑相关 sprite 名称匹配
                        if (sn.Contains("sword") || sn.Contains("blade") || sn.Contains("weapon")
                            || sn.Contains("viking_sword") || sn.Contains("viking_axe"))
                        {
                            bs.gameObject.SetActive(false);
                            count++;
                            LogInfo("[WEAPON] Disabled sword BatchedSprite: " + sprite.name + " on " + bs.gameObject.name);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { LogErr("[WEAPON] DisableSwordBatchedSprites error: " + ex.Message); }
            return count;
        }

        private static PropertyInfo _batchedSpriteSpriteProp;
        private static bool _batchedSpriteSpritePropCached;
        /// </summary>
        private static string GetTransformPath(Transform t, Transform root)
        {
            if (ReferenceEquals(t, null) || ReferenceEquals(t, root)) return t != null ? t.name : "null";
            var path = t.name;
            var parent = t.parent;
            while (!ReferenceEquals(parent, null) && !ReferenceEquals(parent, root))
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static bool _hierarchyDiagDone;

        private static void DumpTransformHierarchy(Transform t, string indent, System.Text.StringBuilder sb, int maxDepth)
        {
            if (t == null || maxDepth < 0) return;
            var comps = t.GetComponents<Component>();
            var compNames = new List<string>();
            foreach (var c in comps)
            {
                if (c == null) continue;
                var typeName = c.GetType().Name;
                if (typeName != "Transform" && typeName != "RectTransform")
                    compNames.Add(typeName);
            }
            // v1.18: 手动拼接组件名，避免 string.Join(IEnumerable) 在 Mono CLR 2.0 不可用
            string compStr = "";
            if (compNames.Count > 0)
            {
                var sbComp = new System.Text.StringBuilder();
                foreach (var cn in compNames)
                {
                    if (sbComp.Length > 0) sbComp.Append(", ");
                    sbComp.Append(cn);
                }
                compStr = " [" + sbComp.ToString() + "]";
            }
            sb.AppendLine(indent + t.name + compStr);
            for (int i = 0; i < t.childCount; i++)
                DumpTransformHierarchy(t.GetChild(i), indent + "  ", sb, maxDepth - 1);
        }

        public static void ReapplyWeaponIfNeeded(Agent agent)
        {
            if (CachedSpearAnim == null)
            {
                // v1.18: 记录为何无法添加长矛
                if (!_weaponNullLogged)
                {
                    _weaponNullLogged = true;
                    LogWarn("[WEAPON] Cannot add spear: CachedSpearAnim is null (weapon not yet found)");
                }
                return;
            }

            var existing = agent.transform.Find("Spear");
            if (!ReferenceEquals(existing, null)) return;

            var spearClone = UnityEngine.Object.Instantiate(CachedSpearAnim);
            spearClone.name = "Spear";
            spearClone.transform.SetParent(agent.transform);
            // v1.18: 修正位置 — Pikeman 的 localPos 是相对 Pikeman agent 的，
            // SwordShield agent 结构不同，用 agent.radius 推算手持高度
            spearClone.transform.localPosition = new Vector3(0, agent.radius * 1.4f, agent.radius * 0.6f);
            spearClone.transform.localRotation = Quaternion.identity;
            spearClone.transform.localScale = Vector3.one * 0.8f;
            LogInfo("[WEAPON] Spear added to " + agent.name);
        }

        /// <summary>
        /// v1.16: 将 IBrainAction 组件手动注册到 Swordsman.actions 列表。
        /// Brain.Setup() 在 Agent 生成时已执行，运行时新增的 IBrainAction 组件
        /// 不会自动被 GetComponentsInChildren 收集，必须手动添加到 actions 列表。
        /// </summary>
        private static void RegisterBrainActions(Agent agent)
        {
            try
            {
                var s = agent.brain as Swordsman;
                if (ReferenceEquals(s, null)) return;

                // 获取 Brain.actions (protected List<IBrainAction>)
                var actionsField = typeof(Brain).GetField("actions",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(actionsField, null))
                {
                    LogErr("[BRAIN] Cannot find Brain.actions field!");
                    return;
                }

                var actions = actionsField.GetValue(s) as System.Collections.IList;
                if (ReferenceEquals(actions, null))
                {
                    LogErr("[BRAIN] Brain.actions is null!");
                    return;
                }

                // 注册 SpearChargeComponent
                var charge = agent.GetComponent<SpearChargeComponent>();
                if (!ReferenceEquals(charge, null) && !actions.Contains(charge))
                {
                    actions.Add(charge);
                    LogInfo("[BRAIN] Registered SpearChargeComponent to actions");
                }

                // 注册 SpearStabAction
                var stab = agent.GetComponent<SpearStabAction>();
                if (!ReferenceEquals(stab, null) && !actions.Contains(stab))
                {
                    actions.Add(stab);
                    LogInfo("[BRAIN] Registered SpearStabAction to actions");
                }
            }
            catch (Exception ex)
            {
                LogErr("[BRAIN] RegisterBrainActions error: " + ex.Message);
            }
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
                _agentRadiusField.SetValue(agent, cur * 1.5f);
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

        /// <summary>
        /// v1.19: 预制件主动剥离 — 启动时克隆 SwordShield 预制件，
        /// 物理删除剑 + 染黑所有 BatchedSprite → 注册为黑矛兵专用预制件。
        /// </summary>
        private void RegisterBlackSpearmanReference()
        {
            if (LevelStateObjectReferences.dict.ContainsKey(BlackSpearmanRefName)) return;
            CacheLevelFields();

            UnityEngine.Object obj;
            if (!LevelStateObjectReferences.dict.TryGetValue("Viking_SwordShield", out obj)) return;
            var origVR = obj as VikingReference;
            if (ReferenceEquals(origVR, null)) return;

            // 获取原始 viking 预制件并克隆
            var origVikingField = typeof(VikingReference).GetField("viking",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            GameObject origPrefab = null;
            if (!ReferenceEquals(origVikingField, null))
            {
                var vikingVal = origVikingField.GetValue(origVR);
                if (vikingVal is GameObject prefabGO) origPrefab = prefabGO;
                else if (vikingVal is Component comp) origPrefab = comp.gameObject;
            }

            if (ReferenceEquals(origPrefab, null))
            {
                LogWarn("[PREFAB] Could not get viking prefab, using fallback");
                RegisterBlackSpearmanReferenceFallback(origVR);
                return;
            }

            // 深克隆预制件 → 剥离剑 → 染黑
            var strippedPrefab = UnityEngine.Object.Instantiate(origPrefab);
            strippedPrefab.name = "BlackSpearman_Stripped";
            DontDestroyOnLoad(strippedPrefab);
            strippedPrefab.SetActive(false);
            StripSwordFromPrefab(strippedPrefab);
            BlackenPrefab(strippedPrefab);
            ApplyPikemanSpriteToPrefab(strippedPrefab);
            strippedPrefab.SetActive(true);

            // 创建 BlackSpearman 的 VikingReference
            var go = new GameObject(BlackSpearmanRefName);
            DontDestroyOnLoad(go);
            var nr = go.AddComponent<VikingReference>();
            CopyVikingReferenceFields(origVR, nr);
            if (!ReferenceEquals(origVikingField, null))
                origVikingField.SetValue(nr, strippedPrefab);

            // 图标用 Pikeman 的 sprite2
            if (!ReferenceEquals(CachedSpearSprite2, null))
            {
                var s2f = typeof(VikingReference).GetField("sprite2",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(s2f, null))
                    s2f.SetValue(nr, CachedSpearSprite2);
            }

            ApplyIconTint(nr);
            SetLevelExpr(go.AddComponent<LevelRule>(), _levelRuleConditionField, "true");
            SetLevelExpr(go.AddComponent<LevelGuessable>(), _levelGuessableProbabilityField, "1");
            LevelStateObjectReferences.AddToDict(nr);

            LogInfo("[PREFAB] ✅ Stripped prefab: sword=" + _prefabSwordStripped + " black=" + _prefabSpritesBlackened);
        }

        private static int _prefabSwordStripped;
        private static int _prefabSpritesBlackened;

        /// <summary>
        /// 从预制件中物理删除所有剑相关的 GameObject 和 BatchedSprite
        /// </summary>
        private static void StripSwordFromPrefab(GameObject prefab)
        {
            _prefabSwordStripped = 0;
            if (ReferenceEquals(prefab, null)) return;
            try
            {
                var allT = prefab.GetComponentsInChildren<Transform>(true);
                foreach (var t in allT)
                {
                    if (ReferenceEquals(t, null)) continue;
                    string ln = t.name.ToLower();
                    if (ln.Contains("sword") || ln.Contains("blade") || ln.Contains("weapon")
                        || ln.Contains("右") || ln.Contains("左") || ln == "r_weapon" || ln == "l_weapon")
                    {
                        UnityEngine.Object.DestroyImmediate(t.gameObject);
                        _prefabSwordStripped++;
                    }
                }
                // 按 BatchedSprite.sprite 名称删除
                var allBS = prefab.GetComponentsInChildren<BatchedSprite>(true);
                var sp = typeof(BatchedSprite).GetProperty("sprite",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(sp, null))
                {
                    foreach (var bs in allBS)
                    {
                        if (ReferenceEquals(bs, null)) continue;
                        try
                        {
                            var s = sp.GetValue(bs, null) as Sprite;
                            if (ReferenceEquals(s, null)) continue;
                            string sn = s.name.ToLower();
                            if (sn.Contains("sword") || sn.Contains("blade") || sn.Contains("viking_sword"))
                            {
                                UnityEngine.Object.DestroyImmediate(bs.gameObject);
                                _prefabSwordStripped++;
                            }
                        }
                        catch { }
                    }
                }
                LogInfo("[PREFAB] Stripped " + _prefabSwordStripped + " sword objects");
            }
            catch (Exception ex) { LogErr("[PREFAB] Strip error: " + ex.Message); }
        }

        /// <summary>
        /// 将预制件所有 BatchedSprite 染黑（R/G→35%, B→0.01）
        /// </summary>
        private static void BlackenPrefab(GameObject prefab)
        {
            _prefabSpritesBlackened = 0;
            if (ReferenceEquals(prefab, null)) return;
            try
            {
                var allBS = prefab.GetComponentsInChildren<BatchedSprite>(true);
                var cp = typeof(BatchedSprite).GetProperty("color",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(cp, null)) return;
                foreach (var bs in allBS)
                {
                    if (ReferenceEquals(bs, null)) continue;
                    try
                    {
                        var c = (Color)cp.GetValue(bs, null);
                        cp.SetValue(bs, new Color(c.r * 0.35f, c.g * 0.35f, 0.01f, c.a), null);
                        _prefabSpritesBlackened++;
                    }
                    catch { }
                }
                LogInfo("[PREFAB] Blackened " + _prefabSpritesBlackened + " BatchedSprites");
            }
            catch (Exception ex) { LogErr("[PREFAB] Blacken error: " + ex.Message); }
        }

        /// <summary>
        /// 将预制件中 SpriteAnimator.sprite2 替换为 Pikeman 的（持矛身体姿态）
        /// </summary>
        private static void ApplyPikemanSpriteToPrefab(GameObject prefab)
        {
            if (ReferenceEquals(prefab, null) || ReferenceEquals(CachedSpearSprite2, null)) return;
            try
            {
                var allSA = prefab.GetComponentsInChildren<SpriteAnimator>(true);
                var s2f = typeof(SpriteAnimator).GetField("sprite2",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(s2f, null)) return;
                int applied = 0;
                foreach (var sa in allSA)
                {
                    if (ReferenceEquals(sa, null)) continue;
                    try { s2f.SetValue(sa, CachedSpearSprite2); applied++; }
                    catch { }
                }
                if (applied > 0)
                    LogInfo("[PREFAB] Applied Pikeman sprite2 to " + applied + " SpriteAnimators");
            }
            catch (Exception ex) { LogErr("[PREFAB] PikemanSprite error: " + ex.Message); }
        }

        /// <summary>
        /// 降级方案：获取不到 viking 预制件时回退
        /// </summary>
        private void RegisterBlackSpearmanReferenceFallback(VikingReference origVR)
        {
            var go = new GameObject(BlackSpearmanRefName);
            DontDestroyOnLoad(go);
            var nr = go.AddComponent<VikingReference>();
            CopyVikingReferenceFields(origVR, nr);
            ApplyIconTint(nr);
            SetLevelExpr(go.AddComponent<LevelRule>(), _levelRuleConditionField, "true");
            SetLevelExpr(go.AddComponent<LevelGuessable>(), _levelGuessableProbabilityField, "1");
            LevelStateObjectReferences.AddToDict(nr);
            LogWarn("[PREFAB] Fallback registration (runtime stripping will be used)");
        }

        private void CopyVikingReferenceFields(VikingReference src, VikingReference dst)
        {
            foreach (string n in new[] { "type", "viking", "bounty", "sprite2", "icon", "infoSprite", "previewSprite", "iconSprite" })
            {
                var f = typeof(VikingReference).GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(f, null)) f.SetValue(dst, f.GetValue(src));
            }
        }

        /// <summary>
        /// 为黑矛兵的 VikingReference 图标应用暗色调，
        /// 使其在关卡兵种预览和击杀统计中与普通 SwordShield 区分。
        /// </summary>
        private static void ApplyIconTint(VikingReference vr)
        {
            if (ReferenceEquals(vr, null)) return;
            try
            {
                // 方式1: 修改 icon sprite 的 BatchedSprite 颜色（如果图标是 BatchedSprite 渲染）
                if (!ReferenceEquals(vr.vikingClone, null))
                {
                    var allBS = vr.vikingClone.GetComponentsInChildren<BatchedSprite>(true);
                    if (allBS != null)
                    {
                        var colorProp = typeof(BatchedSprite).GetProperty("color",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(colorProp, null))
                        {
                            foreach (var bs in allBS)
                            {
                                if (ReferenceEquals(bs, null)) continue;
                                try
                                {
                                    var oldColor = (Color)colorProp.GetValue(bs, null);
                                    colorProp.SetValue(bs, new Color(oldColor.r * 0.35f, oldColor.g * 0.35f, 0.01f, oldColor.a), null);
                                }
                                catch { }
                            }
                            LogInfo("[ICON] Tinted vikingClone BatchedSprites for BlackSpearman preview icon");
                        }
                    }
                }

                // 方式2: 尝试修改 VikingReference 上的 SpriteRenderer 颜色
                var sr = vr.GetComponentInChildren<SpriteRenderer>(true);
                if (!ReferenceEquals(sr, null))
                {
                    sr.color = new Color(0.3f, 0.3f, 0.3f, 1f);
                    LogInfo("[ICON] Tinted SpriteRenderer for BlackSpearman icon");
                }
            }
            catch (Exception ex) { LogErr("[ICON] ApplyIconTint error: " + ex.Message); }
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

        // ============ v1.18 主动诊断 ============

        /// <summary>
        /// 扫描 AppDomain 中所有 Spear 类型，并尝试 Resources.FindObjectsOfTypeAll 找预制件
        /// </summary>
        private static void DumpSpearTypes()
        {
            try
            {
                var asms = System.AppDomain.CurrentDomain.GetAssemblies();
                var spearTypes = new List<Type>();
                foreach (var asm in asms)
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "Spear" && t.IsSubclassOf(typeof(Brain)))
                                spearTypes.Add(t);
                        }
                    }
                    catch { }
                }
                if (spearTypes.Count > 0)
                {
                    foreach (var st in spearTypes)
                        LogInfo("[DIAG] Found Spear type: " + st.FullName + " in " + st.Assembly.GetName().Name);
                    
                    // v1.18: 主动从预制件提取武器
                    try
                    {
                        var allSpears = Resources.FindObjectsOfTypeAll(spearTypes[0]);
                        LogInfo("[DIAG] FindObjectsOfTypeAll(Spear): " + (allSpears != null ? allSpears.Length : 0) + " instances");
                        if (!WeaponCached && allSpears != null && allSpears.Length > 0)
                        {
                            foreach (var obj in allSpears)
                            {
                                if (ReferenceEquals(obj, null)) continue;
                                var spearBrain = obj as Brain;
                                if (!ReferenceEquals(spearBrain, null))
                                {
                                    LogInfo("[DIAG] Extracting weapon from prefab: " + obj.name);
                                    if (ExtractWeapon(spearBrain))
                                    {
                                        LogInfo("[DIAG] ✅ Weapon extracted proactively!");
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { LogErr("[DIAG] FindObjectsOfTypeAll: " + ex.Message); }
                }
                else
                {
                    LogWarn("[DIAG] No Spear type found in any loaded assembly!");
                    // 列出所有 Brain 子类帮助诊断
                    var sb = new System.Text.StringBuilder();
                    foreach (var asm in asms)
                    {
                        try
                        {
                            foreach (var t in asm.GetTypes())
                            {
                                if (t.IsSubclassOf(typeof(Brain)) && !t.IsAbstract)
                                {
                                    if (sb.Length > 0) sb.Append(", ");
                                    sb.Append(t.Name);
                                }
                            }
                        }
                        catch { }
                    }
                    LogInfo("[DIAG] All Brain subclasses: [" + sb.ToString() + "]");
                }
            }
            catch (Exception ex) { LogErr("[DIAG] DumpSpearTypes: " + ex.Message); }
        }

        /// <summary>
        /// Hook Brain.Setup() — 任何 Brain（包含 Spear）实例化时立即捕获
        /// </summary>
        private void PatchBrainSetup()
        {
            try
            {
                // Brain.Setup 可能是 virtual/abstract，用 Swordsman.Setup 或 Spear.Setup 代替
                var setupMethod = typeof(Spear).GetMethod("Setup",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(setupMethod, null))
                {
                    // fallback: 用 Brain 的声明方法
                    setupMethod = typeof(Brain).GetMethod("Setup", 
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                if (ReferenceEquals(setupMethod, null))
                {
                    LogWarn("[DIAG] Cannot hook Brain.Setup — trying Agent.Setup instead");
                    setupMethod = typeof(Agent).GetMethod("Setup",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (!ReferenceEquals(setupMethod, null))
                {
                    var postfix = typeof(Plugin).GetMethod("BrainSetupPostfix",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(setupMethod, null, new HarmonyMethod(postfix));
                    LogInfo("[DIAG] Hooked " + setupMethod.DeclaringType.Name + "." + setupMethod.Name);
                }
            }
            catch (Exception ex) { LogErr("[DIAG] PatchBrainSetup: " + ex.Message); }
        }

        /// <summary>
        /// Brain.Setup() Postfix — 捕获任何 Spear brain 的最早出现
        /// </summary>
        private static void BrainSetupPostfix(Brain __instance)
        {
            if (ReferenceEquals(__instance, null)) return;
            var typeName = __instance.GetType().Name;
            if (typeName == "Spear" || typeName.Contains("Spear") || typeName.Contains("Pike"))
            {
                LogInfo("[DIAG] Brain.Setup: " + typeName + " on " + __instance.name + " frame=" + Time.frameCount);
                
                // 主动触发武器缓存
                if (!WeaponCached)
                {
                    LogInfo("[DIAG] Proactively extracting weapon from " + typeName + " at Setup time!");
                    SearchForPikemanWeapon();
                }
            }
        }

        internal static void LogInfo(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogInfo("[BlackSpearman] " + msg); }
        internal static void LogWarn(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogWarning("[BlackSpearman] " + msg); }
        internal static void LogErr(string msg) { if (!ReferenceEquals(SharedLogger, null)) SharedLogger.LogError("[BlackSpearman] " + msg); }
    }
}
