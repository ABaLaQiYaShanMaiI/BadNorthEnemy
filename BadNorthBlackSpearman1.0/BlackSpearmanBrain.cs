using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// 黑矛兵 Brain 层拦截器。
    /// 
    /// 设计原理：
    /// - Swordsman 拥有完整的状态机（pursuing/hunting/attack/clash/adjecent网络等），
    ///   完全替换 Brain 会破坏整个近战战斗系统。
    /// - 通过在 Swordsman.GetAttack() 方法上挂载 Harmony Prefix，
    ///   仅拦截「攻击向量生成」这一环节，替换为长矛兵风格的四维攻击参数。
    /// - 这样黑矛兵保留 Swordsman 的全部 AI 行为（追逐、围猎、碰撞格挡等），
    ///   仅在命中时使用长矛的伤害/击退/眩晕/长距离判定。
    ///
    /// 参考：
    /// - Spear.cs 的 GetAttack() 基于 spearLength + spearAim 计算命中点
    /// - Swordsman.cs 的 GetAttack() 基于 damage/knockback/stun + swordSound
    /// - CloseCombatBrain 抽象仅要求 GetAttack(Agent) 返回 Attack 结构体
    /// </summary>
    public static class BlackSpearmanBrain
    {
        // 长矛兵风格攻击参数（四维向量增强）
        // 与 Spear.cs 中的 attackSettings 对齐：
        //   damage ≈ 2.0~3.0, knockback ≈ 0.5~1.5, launchImpulse ≈ 0~0.3, stun ≈ 3~8
        private const float SpearBaseDamage = 2.5f;
        private const float SpearBaseKnockback = 1.2f;
        private const float SpearBaseStun = 6f;
        private const float SpearLaunchImpulse = 0f;

        // 距离缩放系数（模拟 spearLength=0.6 的长距离判定）
        // Swordsman 原生 range = agent.radius * 0.7f，约 0.08~0.12
        // 长矛兵等效 range 约 spearLength + radius ≈ 0.6 + 0.12 = 0.72
        // 因此 range 乘数 ≈ 0.72 / 0.10 ≈ 7.0x
        private const float SpearRangeMultiplier = 3.5f;

        // 伤害等级数组缓存（避免每帧反射）
        private static FieldInfo _damageLevelsField;
        private static FieldInfo _knockbackLevelsField;
        private static FieldInfo _stunLevelsField;
        private static bool _fieldsCached;
        private static bool _interceptLogged;

        private static void CacheFields()
        {
            if (_fieldsCached) return;
            _fieldsCached = true;
            var t = typeof(Swordsman);
            _damageLevelsField = t.GetField("damageLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _knockbackLevelsField = t.GetField("knockbackLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _stunLevelsField = t.GetField("stunLevels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        /// <summary>
        /// Harmony Prefix：拦截 Swordsman.GetAttack() 
        /// 为黑矛兵生成长矛风格的四维攻击向量
        /// </summary>
        public static bool GetAttackPrefix(Swordsman __instance, Agent target, ref Attack __result)
        {
            // 仅对已转化的黑矛兵生效
            if (!Plugin.ConvertedAgents.Contains(__instance.agent))
                return true; // 非黑矛兵走原方法

            // v1.19: 首次命中日志（确认拦截生效）
            if (!_interceptLogged)
            {
                _interceptLogged = true;
                Plugin.LogInfo("[Brain] 🔒 INTERCEPT: Swordsman.GetAttack → Spear-style for " + __instance.agent.name);
            }

            if (ReferenceEquals(target, null))
            {
                __result = default(Attack);
                return false;
            }

            try
            {
                CacheFields();

                // 获取升级后的伤害/击退/眩晕值（已被 Plugin.ScaleFloatArray 缩放）
                int level = __instance.agent.squad != null
                    ? Mathf.Clamp(__instance.agent.squad.level, 0, int.MaxValue)
                    : 0;

                float damage = SpearBaseDamage;
                float knockback = SpearBaseKnockback;
                float stun = SpearBaseStun;

                // 如果原字段可读，叠加升级等级缩放后的值
                if (!ReferenceEquals(_damageLevelsField, null))
                {
                    var arr = _damageLevelsField.GetValue(__instance) as float[];
                    if (arr != null && level < arr.Length)
                        damage = Mathf.Max(damage, arr[level]);
                }
                if (!ReferenceEquals(_knockbackLevelsField, null))
                {
                    var arr = _knockbackLevelsField.GetValue(__instance) as float[];
                    if (arr != null && level < arr.Length)
                        knockback = Mathf.Max(knockback, arr[level]);
                }
                if (!ReferenceEquals(_stunLevelsField, null))
                {
                    var arr = _stunLevelsField.GetValue(__instance) as float[];
                    if (arr != null && level < arr.Length)
                        stun = Mathf.Max(stun, arr[level]);
                }

                // 计算方向：从攻击者胸部指向目标胸部
                Vector3 direction = (target.chestPos - __instance.agent.chestPos).normalized;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.001f)
                    direction = __instance.transform.forward;

                // 命中位置：取两者胸部中点（模拟长矛穿透到目标位置）
                Vector3 pos = (target.wChestPos + __instance.agent.wChestPos) / 2f;

                // 构建 AttackSettings（四维向量）
                var settings = new AttackSettings(damage, knockback, SpearLaunchImpulse, stun);

                // 构建 Attack 结构体
                // 使用 Spear 风格的 sound（"Sfx/English/Spear" + "/Hit"）
                __result = new Attack(
                    settings,
                    direction,
                    pos,
                    __instance,
                    __instance.agent.squad,
                    "Sfx/English/Spear"  // 长矛命中音效前缀
                );

                // 日志（仅首次或每 120 帧以降低开销）
                if (Time.frameCount % 120 == 0)
                {
                    Plugin.LogInfo($"[Brain] Spear-style attack → {target.name}: dmg={damage:F1} kb={knockback:F1} stun={stun:F1} dist={Vector3.Distance(__instance.transform.position, target.transform.position):F2}");
                }

                return false; // 跳过原方法
            }
            catch (Exception ex)
            {
                Plugin.LogErr("[Brain] GetAttackPrefix error: " + ex.Message);
                return true; // 出错时回退到原方法
            }
        }

        /// <summary>
        /// Harmony Prefix：拦截 Swordsman.range 属性 getter
        /// 为黑矛兵扩大攻击判定范围
        /// </summary>
        public static bool RangeGetterPrefix(Swordsman __instance, ref float __result)
        {
            if (!Plugin.ConvertedAgents.Contains(__instance.agent))
                return true;

            // 扩大 range，模拟长矛的长度优势
            float baseRange = __instance.agent.radius * 0.7f;
            __result = baseRange * SpearRangeMultiplier;
            return false;
        }

        /// <summary>
        /// v1.19: 诊断 — 检查 ConvertedAgents 中实际有多少 agent 被追踪
        /// </summary>
        public static void DumpConvertedAgents()
        {
            int alive = 0;
            foreach (var a in Plugin.ConvertedAgents)
            {
                if (!ReferenceEquals(a, null) && !ReferenceEquals(a.aliveState, null) && a.aliveState.active)
                    alive++;
            }
            Plugin.LogInfo("[Brain] ConvertedAgents count=" + Plugin.ConvertedAgents.Count + " alive=" + alive);
        }
    }
}