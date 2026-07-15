using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// Bad North 黑色长矛手敌人 Mod
    /// 纯 BepInEx + 事件订阅 + Agent轮询fallback，不含 Harmony
    /// </summary>
    [BepInPlugin("black.spearman", "Bad North - Black Spearman", "1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;

        /// <summary>SpearChargeComponent 可用的公共日志</summary>
        public static BepInEx.Logging.ManualLogSource SharedLogger;

        internal static readonly HashSet<Agent> ConvertedAgents = new HashSet<Agent>();

        internal const float ConversionChance = 0.4f;
        internal static readonly Color BlackColor = new Color(0.08f, 0.02f, 0.02f, 1f);
        internal const float DamageMultiplier = 1.6f;
        internal const float KnockbackMultiplier = 2.5f;
        internal const float ScaleMultiplier = 1.2f;
        internal const float ChargeSpeedMultiplier = 3.5f;
        internal const float ChargeDuration = 1.5f;
        internal const float ChargeCooldown = 8.0f;

        // ==============================
        // 反射缓存
        // ==============================

        private static FieldInfo _armorField;
        private static bool _armorFieldAttempted;

        /// <summary>onAgentSpawned 字段的 FieldInfo（通过反射检测运行时是否真实存在）</summary>
        private static FieldInfo _onAgentSpawnedField;
        private static bool _onAgentSpawnedFieldAttempted;

        /// <summary>运行时确认 onAgentSpawned 事件可用（false 时走 Agent 轮询 fallback）</summary>
        internal static bool OnAgentSpawnedAvailable;

        /// <summary>已订阅 onAgentSpawned 的小队对象（防止重复订阅）</summary>
        private static readonly HashSet<object> _subscribedSquads = new HashSet<object>();

        /// <summary>上次 Agent 轮询扫描时间</summary>
        private float _lastAgentScanTime;

        /// <summary>约束修改重试计数（解决首次轮询时 dict 未就绪问题）</summary>
        private int _constraintRetryCount;
        private const int MaxConstraintRetries = 10;

        /// <summary>转化计数统计</summary>
        private int _totalConvertedCount;

        /// <summary>转化统计上次打印时的值（防止每3秒重复打印）</summary>
        private int _lastReportedConvertedCount;

        /// <summary>首次转化全量诊断是否已完成</summary>
        private static bool _firstConversionDiagnosticDone;

        /// <summary>队列订阅成功计数</summary>
        private int _squadSubscribedCount;
        private int _lastReportedSquadCount;

        // ==============================
        // BepInEx 生命周期
        // ==============================

        private void Start()
        {
            Instance = this;
            SharedLogger = Logger;
            Logger.LogInfo("[BlackSpearman] ====== v1.0 START ======");

            // 诊断日志：运行时环境 & 关键类型检查
            try
            {
                Logger.LogInfo($"[BlackSpearman] BepInEx version: {typeof(BaseUnityPlugin).Assembly.GetName().Version}");
                Logger.LogInfo($"[BlackSpearman] Assembly-CSharp loaded: {typeof(Faction).Assembly.FullName}");
                Logger.LogInfo($"[BlackSpearman] CLR: {Environment.Version}");
                Logger.LogInfo($"[BlackSpearman] OS: {Environment.OSVersion}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlackSpearman] Env log failed: {ex}");
            }

            // 关键类型存在性扫描
            // 使用 ReferenceEquals 判空避免 Mono 2.0 的 Type.op_Inequality 问题
            try
            {
                Logger.LogInfo($"[BlackSpearman] === Type Check ===");
                Logger.LogInfo($"[BlackSpearman] Faction type OK: {!ReferenceEquals(typeof(Faction), null)}");
                Logger.LogInfo($"[BlackSpearman] VikingAgent type OK: {!ReferenceEquals(typeof(VikingAgent), null)}");
                Logger.LogInfo($"[BlackSpearman] Agent type OK: {!ReferenceEquals(typeof(Agent), null)}");
                Logger.LogInfo($"[BlackSpearman] Swordsman type OK: {!ReferenceEquals(typeof(Swordsman), null)}");
                Logger.LogInfo($"[BlackSpearman] Stun type OK: {!ReferenceEquals(typeof(Stun), null)}");
                Logger.LogInfo($"[BlackSpearman] Armor type OK: {!ReferenceEquals(typeof(Armor), null)}");
                Logger.LogInfo($"[BlackSpearman] LevelStateObjectReferences.dict exists: {!ReferenceEquals(LevelStateObjectReferences.dict, null)}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlackSpearman] Type check failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            // 每 3 秒轮询一次
            try { InvokeRepeating("PollForSquads", 1f, 3f); }
            catch (Exception ex)
            {
                Logger.LogError($"[BlackSpearman] InvokeRepeating failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }

            Logger.LogInfo("[BlackSpearman] ====== INIT COMPLETE ======");
        }

        private void OnDestroy()
        {
            CancelInvoke("PollForSquads");
        }

        // ==============================
        // 主轮询逻辑
        // ==============================

        /// <summary>
        /// 轮询扫描所有 Viking Squad，尝试两种途径捕获新 Agent：
        /// 1. 通过 squad.onAgentSpawned 事件订阅（运行时反射验证存在性）
        /// 2. Fallback：每 3 秒直接扫描所有 Viking Agent（独立于事件订阅）
        /// </summary>
        private void PollForSquads()
        {
            // 途径 1：尝试订阅 onAgentSpawned 事件
            // 使用反射运行时检测字段是否存在，而非依赖源文件
            EnsureOnAgentSpawnedAvailable();

            if (OnAgentSpawnedAvailable)
            {
                var factions = FindObjectsOfType<Faction>();
                foreach (var faction in factions)
                {
                    if (faction == null || faction.allSquads == null) continue;

                    foreach (var squad in faction.allSquads)
                    {
                        if (squad == null) continue;
                        if (squad.faction.side != Faction.Side.Viking) continue;

                        // 防止重复订阅
                        if (_subscribedSquads.Contains(squad)) continue;
                        _subscribedSquads.Add(squad);

                        try
                        {
                            var action = _onAgentSpawnedField.GetValue(squad) as Action<Agent>;
                            if (action != null)
                            {
                                action -= OnAgentSpawnedHandler;
                                action += OnAgentSpawnedHandler;
                                _onAgentSpawnedField.SetValue(squad, action);
                            }
                            else
                            {
                                // 首次订阅：直接通过 += 委托组合
                                var newAction = new Action<Agent>(OnAgentSpawnedHandler);
                                _onAgentSpawnedField.SetValue(squad, newAction);
                            }
                        }
                        catch (Exception ex)
                        {
                            SharedLogger?.LogWarning($"[BlackSpearman] Squad subscribe failed: {ex.Message}");
                            continue;
                        }
                    }
                }
            }
            else
            {
                // 途径 2 Fallback：直接扫描所有 Agent
                // onAgentSpawned 字段不存在时使用此方式
                // 仅每 3 秒扫描一次新出现的 Agent
                var agents = FindObjectsOfType<Agent>();
                foreach (var agent in agents)
                {
                    if (agent == null) continue;
                    OnAgentSpawnedHandler(agent);
                }
            }

            // 约束修改（带重试）
            TryUpdateConstraints();
        }

        /// <summary>
        /// 运行时验证 onAgentSpawned 字段是否存在。
        /// 使用反射从 Squad 类型中查找，比依赖源文件更可靠。
        /// </summary>
        private void EnsureOnAgentSpawnedAvailable()
        {
            if (_onAgentSpawnedFieldAttempted) return;
            _onAgentSpawnedFieldAttempted = true;

            try
            {
                // 从 Faction.allSquads 获取一个 Squad 实例来检查其类型
                var factions = FindObjectsOfType<Faction>();
                foreach (var faction in factions)
                {
                    if (faction == null || faction.allSquads == null) continue;
                    foreach (var squad in faction.allSquads)
                    {
                        if (squad == null) continue;
                        var squadType = squad.GetType();
                        // 查找所有字段中含 "AgentSpawned" 的（不区分大小写）
                        foreach (var field in squadType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (field.Name.IndexOf("AgentSpawned", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _onAgentSpawnedField = field;
                                OnAgentSpawnedAvailable = true;
                                SharedLogger?.LogInfo($"[BlackSpearman] Found onAgentSpawned field: '{field.Name}' on {squadType.Name}");
                                return;
                            }
                        }
                        // 只检查第一个找到的 Squad 实例
                        break;
                    }
                    break;
                }

                if (!OnAgentSpawnedAvailable)
                {
                    SharedLogger?.LogWarning("[BlackSpearman] onAgentSpawned field NOT found. Using Agent polling fallback.");
                }
            }
            catch (Exception ex)
            {
                SharedLogger?.LogWarning($"[BlackSpearman] onAgentSpawned detection error: {ex.Message}. Using Agent polling fallback.");
            }
        }

        // ==============================
        // 战役约束修改（带重试）
        // ==============================

        private void TryUpdateConstraints()
        {
            if (_constraintRetryCount >= MaxConstraintRetries) return;

            try
            {
                if (!LevelStateObjectReferences.dict.TryGetValue("Viking_SwordShield", out UnityEngine.Object obj))
                {
                    // dict 中还没有 SwordShield 引用，等待下次轮询重试
                    _constraintRetryCount++;
                    if (_constraintRetryCount == 1)
                        SharedLogger?.LogInfo($"[BlackSpearman] Constraints: Waiting for Viking_SwordShield in dict... (retry {_constraintRetryCount}/{MaxConstraintRetries})");
                    return;
                }

                _constraintRetryCount = MaxConstraintRetries; // 成功，标记完成

                var vref = obj as VikingReference;
                if (vref == null) return;

                // 反射 LevelGuessable.probability
                var guessableType = Type.GetType(
                    "Voxels.TowerDefense.CampaignGeneration.CampaignAc3.LevelGuessable, Assembly-CSharp");
                if (!ReferenceEquals(guessableType, null))
                {
                    var guessable = vref.GetComponent(guessableType);
                    if (!ReferenceEquals(guessable, null))
                    {
                        var probField = guessableType.GetField("probability",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(probField, null) &&
                            LevelStateObjectReferences.dict.TryGetValue("Viking_AxeThrower", out UnityEngine.Object axeObj))
                        {
                            var axeGuessable = (axeObj as VikingReference)?.GetComponent(guessableType);
                            if (!ReferenceEquals(axeGuessable, null))
                                probField.SetValue(guessable, probField.GetValue(axeGuessable));
                        }
                    }
                }

                // ============================================
                // 关键：不覆盖 LevelRule.condition.expression！
                // SwordShield 不是 Berserker——它原版已有正常的出现条件。
                // 覆盖 condition 会永久改变原版 SwordShield 的战役平衡。
                //
                // 黑矛兵作为 SwordShield 的 40% 变体，随着 SwordShield
                // 正常出现即可。不需要额外修改条件表达式。
                //
                // 只调整 probability 以匹配 AxeThrower 的频率。
                // ============================================

                // 读取原版 condition 用于诊断日志
                var ruleType = Type.GetType(
                    "Voxels.TowerDefense.CampaignGeneration.CampaignAc3.LevelRule, Assembly-CSharp");
                string origCondition = "(unknown)";
                if (!ReferenceEquals(ruleType, null))
                {
                    var rule = vref.GetComponent(ruleType);
                    if (!ReferenceEquals(rule, null))
                    {
                        var condField = ruleType.GetField("condition",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(condField, null))
                        {
                            var condObj = condField.GetValue(rule);
                            if (!ReferenceEquals(condObj, null))
                            {
                                var exprField = condObj.GetType().GetField("expression",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (!ReferenceEquals(exprField, null))
                                    origCondition = exprField.GetValue(condObj) as string ?? "(null)";
                            }
                        }
                    }
                }

                SharedLogger?.LogInfo($"[BlackSpearman] Campaign constraints updated. " +
                    $"Original condition preserved: '{origCondition}'. " +
                    $"Probability synced to AxeThrower.");
            }
            catch (Exception ex)
            {
                SharedLogger?.LogWarning($"[BlackSpearman] Constraints error (retry {_constraintRetryCount}): {ex.Message}");
                _constraintRetryCount++;
            }
        }

        // ==============================
        // Agent 生成处理
        // ==============================

        /// <summary>
        /// 当新的 Viking Agent 生成时处理。
        /// 同时支持 onAgentSpawned 事件回调 和 Agent 轮询 fallback。
        /// ConvertedAgents HashSet 保证幂等性。
        /// </summary>
        internal static void OnAgentSpawnedHandler(Agent agent)
        {
            if (agent == null || !agent.isViking) return;

            var va = agent.GetComponent<VikingAgent>();
            if (va == null || va.type != VikingAgent.Type.SwordShield) return;

            if (ConvertedAgents.Contains(agent)) return;
            if (UnityEngine.Random.value > ConversionChance) return;

            ConvertedAgents.Add(agent);

            try { ApplyBlackSpearman(agent); }
            catch (Exception ex)
            {
                if (Instance != null)
                    Instance.Logger.LogError($"[BlackSpearman] Apply failed: {ex.Message}");
            }

            // 每新增 10 个转化或首个转化时打印统计
            if (Instance != null)
            {
                Instance._totalConvertedCount++;
                if (Instance._totalConvertedCount == 1 ||
                    Instance._totalConvertedCount % 10 == 0)
                {
                    Instance.Logger.LogInfo(
                        $"[BlackSpearman] Converted #{Instance._totalConvertedCount}: " +
                        $"type={va.type}, hp={agent.health:F1}/{agent.maxHealth:F1}, " +
                        $"scale={agent.scale:F2}, squad={agent.squad?.name ?? "?"}");
                }
            }
        }

        // ==============================
        // 黑矛兵属性应用
        // ==============================

        // ==============================
        // 首次转化全量诊断（仅在第一个 Agent 转化后运行一次）
        // ==============================
        private static void RunFirstConversionDiagnostic(Agent agent)
        {
            var lines = new List<string>();
            lines.Add("===== First Conversion Diagnostic =====");

            // 1. Brain 类型检查
            var swordsman = agent.brain as Swordsman;
            if (swordsman != null)
            {
                lines.Add($"  [PASS] Brain: Swordsman (OK)");
                lines.Add($"         damageLevels[0]={swordsman.damageLevels[0]:F2} " +
                          $"knockbackLevels[0]={swordsman.knockbackLevels[0]:F2}");
            }
            else
            {
                var brainType = agent.brain != null ? agent.brain.GetType().FullName : "null";
                lines.Add($"  [FAIL] Brain: expected Swordsman, got {brainType}");
                lines.Add($"         Damage/knockback multipliers NOT applied!");
            }

            // 2. Armor 组件检查
            var armor = agent.GetComponent<Armor>();
            if (armor != null)
            {
                var armorField = typeof(Armor).GetField("armor",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(armorField, null))
                {
                    var vals = armorField.GetValue(armor) as float[];
                    if (vals != null)
                        lines.Add($"  [PASS] Armor: present, values=[{vals[0]:F2}, {vals[1]:F2}, {vals[2]:F2}]");
                    else
                        lines.Add($"  [WARN] Armor: component present but 'armor' field is null");
                }
                else
                    lines.Add($"  [WARN] Armor: component present but 'armor' field not found by reflection");
            }
            else
            {
                lines.Add($"  [FAIL] Armor: component NOT found. Armor multiplier NOT applied!");
            }

            // 3. 颜色渲染检查
            int batchedSpriteCount = 0;
            int colorAppliedCount = 0;
            var allComps = agent.GetComponentsInChildren<Component>(true);
            foreach (var comp in allComps)
            {
                if (comp == null) continue;
                var typeName = comp.GetType().Name;
                if (typeName == "BatchedSprite" || typeName == "SpriteAnimator")
                {
                    batchedSpriteCount++;
                    var prop = comp.GetType().GetProperty("color",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(prop, null))
                    {
                        colorAppliedCount++;
                    }
                }
            }

            if (batchedSpriteCount == 0)
                lines.Add($"  [FAIL] Color: no BatchedSprite/SpriteAnimator found in children!");
            else if (colorAppliedCount == batchedSpriteCount)
                lines.Add($"  [PASS] Color: {colorAppliedCount}/{batchedSpriteCount} BatchedSprite/SpriteAnimator set to black");
            else
                lines.Add($"  [WARN] Color: only {colorAppliedCount}/{batchedSpriteCount} components set - " +
                          $"color property reflection partially failed");

            // 4. Stun 免疫策略（从 SpearChargeComponent 读取静态字段）
            var stunStrategyField = typeof(SpearChargeComponent).GetField("_stunStrategy",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (!ReferenceEquals(stunStrategyField, null))
            {
                var strategyVal = stunStrategyField.GetValue(null);
                lines.Add($"  [INFO] Stun immunity strategy: {strategyVal}");
            }

            // 5. 转化参数确认
            lines.Add($"  [INFO] ConversionChance={ConversionChance} ScaleMultiplier={ScaleMultiplier} " +
                      $"DamageMultiplier={DamageMultiplier} KnockbackMultiplier={KnockbackMultiplier}");

            lines.Add("==========================================");
            SharedLogger?.LogInfo(string.Join("\n", lines));
        }

        internal static void ApplyBlackSpearman(Agent agent)
        {
            if (agent == null) return;

            ApplyBlackColor(agent);
            agent.scale *= ScaleMultiplier;

            var swordsman = agent.brain as Swordsman;
            if (swordsman != null)
            {
                ScaleFloatArray(swordsman.damageLevels, DamageMultiplier);
                ScaleFloatArray(swordsman.knockbackLevels, KnockbackMultiplier);
            }

            ApplyArmor(agent);

            var charge = SpearChargeComponent.AddTo(agent);
            if (charge != null) charge.Setup(agent);

            // 首个转化时运行完整诊断（一次性）
            if (!_firstConversionDiagnosticDone)
            {
                _firstConversionDiagnosticDone = true;
                RunFirstConversionDiagnostic(agent);
            }
        }

        private static void ApplyBlackColor(Agent agent)
        {
            var allComps = agent.GetComponentsInChildren<Component>(true);
            foreach (var comp in allComps)
            {
                if (comp == null) continue;
                var typeName = comp.GetType().Name;
                if (typeName == "BatchedSprite" || typeName == "SpriteAnimator")
                {
                    var prop = comp.GetType().GetProperty("color",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(prop, null))
                        prop.SetValue(comp, BlackColor, null);
                }
            }
        }

        private static void ScaleFloatArray(float[] array, float multiplier)
        {
            if (array == null) return;
            for (int i = 0; i < array.Length; i++)
                array[i] *= multiplier;
        }

        private static void ApplyArmor(Agent agent)
        {
            var armor = agent.GetComponent<Armor>();
            if (armor == null) return;

            if (!_armorFieldAttempted)
            {
                _armorField = typeof(Armor).GetField("armor",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _armorFieldAttempted = true;
            }

            if (!ReferenceEquals(_armorField, null))
            {
                var values = _armorField.GetValue(armor) as float[];
                ScaleFloatArray(values, 1.3f);
            }
        }
    }
}