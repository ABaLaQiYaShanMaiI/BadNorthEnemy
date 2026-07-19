using System;
using System.Reflection;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// 模拟 Pikeman 的长矛刺击行为。
    /// 作为 IBrainAction 挂载在 Swordsman Brain 上，由 Brain.Setup() 自动收集并调用。
    /// </summary>
    public class SpearStabAction : MonoBehaviour, IBrainAction
    {
        private const float StabRange = 2.5f;
        private const float StabCooldown = 1.4f;
        private const float StabDamage = 2.0f;
        private const float StabKnockback = 3.0f;
        private const float StabAngle = 35f;

        private Agent _agent;
        private Swordsman _swordsman;
        private float _lastStabTime = -999f;

        private void Awake()
        {
            _agent = GetComponent<Agent>();
            _swordsman = GetComponent<Swordsman>();
        }

        /// <summary>
        /// IBrainAction 接口：每帧由 Brain 调用，返回 true 表示消耗了这一帧的行动机会
        /// </summary>
        bool IBrainAction.MaybeAct(Brain brain)
        {
            if (Time.time - _lastStabTime < StabCooldown) return false;
            if (ReferenceEquals(_agent, null)) return false;
            if (!_agent.aliveState.active) return false;

            // v1.9 修正：Agent.dangerous 是直接的公有字段（而非 enemyData.dangerous）
            if (!_agent.dangerous) return false;

            var enemy = _agent.enemyAgent;
            if (ReferenceEquals(enemy, null)) return false;
            if (!enemy.aliveState.active) return false;

            float dist = Vector3.Distance(_agent.transform.position, enemy.transform.position);
            if (dist > StabRange) return false;

            Vector3 toTarget = (enemy.chestPos - _agent.transform.position).normalized;
            float angle = Vector3.Angle(_agent.transform.forward, toTarget);
            if (angle > StabAngle * 0.5f) return false;

            _lastStabTime = Time.time;
            PerformStab(enemy);
            return true;
        }

        private void PerformStab(Agent target)
        {
            float prevHealth = target.health;
            target.health = Mathf.Max(0f, target.health - StabDamage);

            Vector3 kbDir = (target.transform.position - _agent.transform.position).normalized;
            kbDir.y = 0f;
            target.transform.position += kbDir * 0.8f;

            var stun = target.GetComponent<Stun>();
            if (!ReferenceEquals(stun, null))
            {
                var smf = typeof(Stun).GetField("stunMultiplier",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(smf, null))
                    smf.SetValue(stun, 8f);
            }

            Plugin.LogInfo("[Stab] Hit " + target.name + " | dmg=" + StabDamage +
                " | prevHP=" + prevHealth.ToString("F1") + "→" + target.health.ToString("F1") +
                " | dist=" + Vector3.Distance(_agent.transform.position, target.transform.position).ToString("F2"));
        }
    }
}