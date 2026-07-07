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
    /// 纯 BepInEx + 事件订阅，不含 Harmony
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

        private static FieldInfo _armorField;
        private static bool _armorFieldAttempted;

        private void Start()
        {
            Instance = this;
            SharedLogger = Logger;
            Logger.LogInfo("[BlackSpearman] v1.0 loaded.");

            // 每 3 秒扫描一次新小队
            InvokeRepeating("PollForSquads", 1f, 3f);
        }

        private void OnDestroy()
        {
            CancelInvoke("PollForSquads");
        }

        /// <summary>
        /// 轮询扫描所有 Viking Squad，订阅 onAgentSpawned 事件
        /// </summary>
        private void PollForSquads()
        {
            var factions = FindObjectsOfType<Faction>();
            foreach (var faction in factions)
            {
                if (faction == null || faction.allSquads == null) continue;

                foreach (var squad in faction.allSquads)
                {
                    if (squad == null) continue;

                    // 只处理 Viking Squad
                    if (squad.faction.side != Faction.Side.Viking) continue;

                    // 防止重复订阅
                    squad.onAgentSpawned -= OnAgentSpawnedHandler;
                    squad.onAgentSpawned += OnAgentSpawnedHandler;
                }
            }

            // 尝试修改战役约束
            TryUpdateConstraints();
        }

        private bool _constraintsApplied;

        private void TryUpdateConstraints()
        {
            if (_constraintsApplied) return;
            _constraintsApplied = true;

            try
            {
                if (!LevelStateObjectReferences.dict.TryGetValue("Viking_SwordShield", out UnityEngine.Object obj))
                    return;

                var vref = obj as VikingReference;
                if (vref == null) return;

                // 反射 probability
                var guessableType = Type.GetType(
                    "Voxels.TowerDefense.CampaignGeneration.CampaignAc3.LevelGuessable, Assembly-CSharp");
                if (guessableType != null)
                {
                    var guessable = vref.GetComponent(guessableType);
                    if (guessable != null)
                    {
                        var probField = guessableType.GetField("probability",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (probField != null &&
                            LevelStateObjectReferences.dict.TryGetValue("Viking_AxeThrower", out UnityEngine.Object axeObj))
                        {
                            var axeGuessable = (axeObj as VikingReference)?.GetComponent(guessableType);
                            if (axeGuessable != null)
                                probField.SetValue(guessable, probField.GetValue(axeGuessable));
                        }
                    }
                }

                // 反射 condition.expression
                var ruleType = Type.GetType(
                    "Voxels.TowerDefense.CampaignGeneration.CampaignAc3.LevelRule, Assembly-CSharp");
                if (ruleType != null)
                {
                    var rule = vref.GetComponent(ruleType);
                    if (rule != null)
                    {
                        var condField = ruleType.GetField("condition",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (condField != null)
                        {
                            var condObj = condField.GetValue(rule);
                            if (condObj != null)
                            {
                                var exprField = condObj.GetType().GetField("expression",
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (exprField != null)
                                    exprField.SetValue(condObj, "fraction > 0.25");
                            }
                        }
                    }
                }

                Logger.LogInfo("[BlackSpearman] Campaign constraints updated.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[BlackSpearman] Constraints error: {ex.Message}");
            }
        }

        /// <summary>
        /// 当新的 Viking Agent 生成时处理
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
                    if (prop != null)
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

            if (_armorField != null)
            {
                var values = _armorField.GetValue(armor) as float[];
                ScaleFloatArray(values, 1.3f);
            }
        }
    }
}