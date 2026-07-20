using System;
using System.Reflection;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// 模拟 Pikeman 的长矛刺击行为（独立 Update，不干扰 Swordsman 原生 IBrainAction）。
    /// </summary>
    public class SpearStabAction : MonoBehaviour
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

            _lastStabTime = Time.time;
            PerformStab(enemy);
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