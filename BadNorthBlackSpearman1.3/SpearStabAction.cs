using System;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵长矛刺击技能 —— IBrainAction 实现。
    /// 由 Brain.MaybeAct() 在 idle 状态（hz8 节拍）调度；仅在进入近战状态时触发。
    /// 参考原版 AxeThrowing 的 IBrainAction 模式与 Spear 的刺击行为。
    /// </summary>
    public class SpearStabAction : MonoBehaviour, IBrainAction
    {
        const float StabRange = 3.5f;
        const float StabCooldown = 1.4f;
        const float StabDamage = 2.0f;      // 刺击伤害（回退：4.0→2.0）
        const float StabKnockback = 3.0f;   // 击退（回退：4.0→3.0）
        const float StabAngle = 35f;
        const float StabStun = 8f;          // 眩晕（回退：10→8）

        Agent _agent;
        Swordsman _swordsman;
        Squad _squad;
        float _lastStabTime = -999f;

        void Awake()
        {
            _agent = GetComponent<Agent>();
            _swordsman = GetComponent<Swordsman>();
            _squad = _agent != null ? _agent.squad : null;
        }

        bool IBrainAction.MaybeAct(Brain brain)
        {
            if (Time.time - _lastStabTime < StabCooldown) return false;
            if (_agent == null) return false;
            if (_agent.aliveState == null || !_agent.aliveState.active) return false;
            if (!_agent.dangerous) return false;
            if (!IsInMeleeCombatState()) return false;

            var enemy = _agent.enemyAgent;
            if (enemy == null) return false;
            if (enemy.aliveState == null || !enemy.aliveState.active) return false;

            float dist = Vector3.Distance(_agent.transform.position, enemy.transform.position);
            if (dist > StabRange) return false;

            Vector3 toTarget = (enemy.chestPos - _agent.transform.position).normalized;
            float angle = Vector3.Angle(_agent.transform.forward, toTarget);
            if (angle > StabAngle * 0.5f) return false;

            _lastStabTime = Time.time;
            PerformStab(enemy);
            return true;
        }

        bool IsInMeleeCombatState()
        {
            if (_swordsman == null) return true; // 兜底
            try
            {
                if (_swordsman.pursuing != null && _swordsman.pursuing.active) return true;
                if (_swordsman.hunting != null && _swordsman.hunting.active) return true;
            }
            catch { }
            return false;
        }

        void PerformStab(Agent target)
        {
            if (target == null) return;
            try
            {
                var settings = new AttackSettings
                {
                    damage = StabDamage,
                    knockback = StabKnockback,
                    launchImpulse = 0f,
                    stun = StabStun
                };

                Vector3 kbDir = (target.transform.position - _agent.transform.position).normalized;
                kbDir.y = 0f;

                var attack = new Attack(settings, kbDir, target.transform.position, this, _squad, "Sfx/English/Spear");
                SpearVisual.AimAt(_agent, target.chestPos);   // 视觉：长矛刺向目标（盖过挥剑动画）
                target.DealDamage(attack);

                BSLog.Info("[Stab] Hit " + target.name + " | dmg=" + StabDamage +
                    " | dist=" + Vector3.Distance(_agent.transform.position, target.transform.position).ToString("F2"));
            }
            catch (Exception ex)
            {
                BSLog.Error("[Stab] Error: " + ex);
            }
        }
    }
}
