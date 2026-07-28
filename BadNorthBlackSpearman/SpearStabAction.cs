using System;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// 黑矛兵长矛刺击技能 — IBrainAction 实现。
    /// 
    /// v1.15 改进：
    /// - 实现 IBrainAction 接口，替代独立 Update 循环
    /// - 由 Brain.MaybeAct() 在 idle 状态（每 hz8 节拍）调度
    /// - 使用 Attack 结构体 + DealDamage 完整攻击链路
    /// - 与 Swordsman pursuing/hunting 状态协调：仅在进入近战状态时允许刺击
    /// 
    /// 调度机制：
    /// - Brain.Setup() 通过 GetComponentsInChildren<IBrainAction>() 自动收集
    /// - Brain.IdleUpdate() 每 hz8 调用 MaybeAct() 轮询所有 IBrainAction
    /// - MaybeAct 返回 true 表示消耗了这一帧的 action 机会
    ///
    /// 参考：
    /// - AxeThrowing.cs（原版 IBrainAction 实现模式）
    /// - Spear.cs（原版长矛兵刺击行为）
    /// - Brain.IdleUpdate() 中的 if(base.MaybeAct()) return;
    /// </summary>
    public class SpearStabAction : MonoBehaviour, IBrainAction
    {
        // 刺击参数：对齐原版 spearLength(0.6) + radius 等效范围
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

        /// <summary>
        /// IBrainAction 接口 — 由 Brain 调度系统在 idle 状态 hz8 节拍调用
        /// </summary>
        bool IBrainAction.MaybeAct(Brain brain)
        {
            // 冷却检查
            if (Time.time - _lastStabTime < StabCooldown)
                return false;

            if (ReferenceEquals(_agent, null))
                return false;

            // 存活状态检查
            if (ReferenceEquals(_agent.aliveState, null) || !_agent.aliveState.active)
                return false;

            // 只在敌人 active 时尝试刺击
            if (!_agent.dangerous)
                return false;

            // Swordsman 的 pursuing/hunting 状态是 IBrainAction 激活时的状态
            // 原 AxeThrowing 模式：仅在进入近战行为后允许触发
            if (!IsInMeleeCombatState())
                return false;

            // 目标有效性检查
            var enemy = _agent.enemyAgent;
            if (ReferenceEquals(enemy, null))
                return false;
            if (ReferenceEquals(enemy.aliveState, null) || !enemy.aliveState.active)
                return false;

            // 距离检查
            float dist = Vector3.Distance(_agent.transform.position, enemy.transform.position);
            if (dist > StabRange)
                return false;

            // 锥角检查（模拟长矛只能向前刺）
            Vector3 toTarget = (enemy.chestPos - _agent.transform.position).normalized;
            float angle = Vector3.Angle(_agent.transform.forward, toTarget);
            if (angle > StabAngle * 0.5f)
                return false;

            // 执行刺击
            _lastStabTime = Time.time;
            PerformStab(enemy);
            return true; // 消耗 action 机会
        }

        /// <summary>
        /// 检查 Swordsman 是否处于近战战斗状态（pursuing/hunting）
        /// AxeThrowing 模式：订阅 pursuing.OnActivate / hunting.OnActivate
        /// 这里使用更简单的方式 — 检查状态 active 标志
        /// </summary>
        private bool IsInMeleeCombatState()
        {
            if (ReferenceEquals(_swordsman, null))
                return true; // 没有 Swordsman 组件则跳过检查（兜底）

            try
            {
                if (!ReferenceEquals(_swordsman.pursuing, null) && _swordsman.pursuing.active)
                    return true;
                if (!ReferenceEquals(_swordsman.hunting, null) && _swordsman.hunting.active)
                    return true;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 执行刺击 — 使用 Attack 结构体 + DealDamage 完整攻击链路
        /// </summary>
        private void PerformStab(Agent target)
        {
            if (ReferenceEquals(target, null)) return;
            try
            {
                float prevHealth = target.health;

                // 使用原版 AttackSettings 四维向量
                var settings = new AttackSettings
                {
                    damage = StabDamage,
                    knockback = StabKnockback,
                    launchImpulse = 0f,
                    stun = StabStun
                };

                Vector3 kbDir = (target.transform.position - _agent.transform.position).normalized;
                kbDir.y = 0f;

                // 使用原版 Attack 结构体（构造签名：Attack(AttackSettings, Vector3 direction, Vector3 pos, MonoBehaviour, Squad, string weapon)）
                var attack = new Attack(
                    settings,
                    kbDir,
                    target.transform.position,
                    this,
                    _squad,
                    "Sfx/English/Spear"  // Spear 风格音效前缀 → "Sfx/English/Spear/Hit"
                );

                // 走完整 DealDamage 链路
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