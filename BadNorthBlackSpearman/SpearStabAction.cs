using System;
using System.Reflection;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// 模拟 Pikeman 的长矛刺击行为。
    /// v1.14 改进：
    /// - 使用 Attack 结构体 + DealDamage 完整攻击链路（护甲/击退/眩晕/音效/特效全部生效）
    /// - 与 Swordsman 原生 JumpAttack 协调：仅在 Brain 的 pursuing/hunting 状态时刺击
    /// - 将眩晕通过 AttackSettings.stun 传递，而非手动设置 stunMultiplier
    /// </summary>
    public class SpearStabAction : MonoBehaviour
    {
        // 刺击距离：对齐原版 spearLength(0.6) + radius 等效范围
        private const float StabRange = 3.5f;
        private const float StabCooldown = 1.4f;
        private const float StabDamage = 2.0f;
        private const float StabKnockback = 3.0f;
        private const float StabAngle = 35f;
        private const float StabStun = 8f;

        private Agent _agent;
        private Swordsman _swordsman;
        private Squad _squad;
        private float _lastStabTime = -999f;

        private void Awake()
        {
            _agent = GetComponent<Agent>();
            _swordsman = GetComponent<Swordsman>();
            _squad = !ReferenceEquals(_agent, null) ? _agent.squad : null;
        }

        private void Update()
        {
            if (Time.time - _lastStabTime < StabCooldown) return;
            if (ReferenceEquals(_agent, null)) return;
            if (ReferenceEquals(_agent.aliveState, null) || !_agent.aliveState.active) return;

            // 只在敌人 active 时尝试刺击
            if (!_agent.dangerous) return;

            var enemy = _agent.enemyAgent;
            if (ReferenceEquals(enemy, null)) return;
            if (ReferenceEquals(enemy.aliveState, null) || !enemy.aliveState.active) return;

            float dist = Vector3.Distance(_agent.transform.position, enemy.transform.position);
            if (dist > StabRange) return;

            Vector3 toTarget = (enemy.chestPos - _agent.transform.position).normalized;
            float angle = Vector3.Angle(_agent.transform.forward, toTarget);
            if (angle > StabAngle * 0.5f) return;

            // 检查 Brain 是否处于 pursuing/hunting 状态，避免与 JumpAttack 同时触发
            // Swordsman 的 pursuing 和 hunting 是其 IBrainAction 激活时的状态
            if (!IsInMeleeCombatState())
                return;

            _lastStabTime = Time.time;
            PerformStab(enemy);
        }

        /// <summary>
        /// 检查 Swordsman 是否处于近战战斗状态（pursuing/hunting）
        /// 避免在 Brain 空闲时仍然刺击
        /// </summary>
        private bool IsInMeleeCombatState()
        {
            if (ReferenceEquals(_swordsman, null)) return true; // 没有 Swordsman 组件则跳过检查

            try
            {
                // Swordsman 的 pursuing 和 hunting 是 brainState 下互斥的子状态
                // 运行时 active 的状态表明 Brain 正在执行该行为
                if (!ReferenceEquals(_swordsman.pursuing, null) && _swordsman.pursuing.active)
                    return true;
                if (!ReferenceEquals(_swordsman.hunting, null) && _swordsman.hunting.active)
                    return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// ✅ v1.14 改进：使用 Attack 结构体 + DealDamage 完整攻击链路
        /// </summary>
        private void PerformStab(Agent target)
        {
            if (ReferenceEquals(target, null)) return;
            try
            {
                float prevHealth = target.health;

                // ✅ 使用原版 AttackSettings 四维向量
                var settings = new AttackSettings
                {
                    damage = StabDamage,
                    knockback = StabKnockback,
                    launchImpulse = 0f,
                    stun = StabStun
                };

                Vector3 kbDir = (target.transform.position - _agent.transform.position).normalized;
                kbDir.y = 0f;

                // ✅ 使用原版 Attack 结构体
                var attack = new Attack(
                    settings,
                    kbDir,
                    target.transform.position,
                    this,
                    _squad,
                    "Spear"
                );

                // ✅ 走完整 DealDamage 链路
                target.DealDamage(attack);

                Plugin.LogInfo("[Stab] Hit " + target.name + " | dmg=" + StabDamage +
                    " | prevHP=" + prevHealth.ToString("F1") + "→" + target.health.ToString("F1") +
                    " | dist=" + Vector3.Distance(_agent.transform.position, target.transform.position).ToString("F2"));
            }
            catch (Exception ex)
            {
                Plugin.LogErr("[Stab] Error: " + ex.Message);
            }
        }
    }
}