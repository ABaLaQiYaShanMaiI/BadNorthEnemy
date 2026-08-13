using System;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵冲刺技能（模仿玩家方 Pike Charge）。
    /// 状态机：Idle → WindUp → Charging → Stab → Cooldown → Idle。
    /// 作为 IBrainAction 注册到 Brain.actions，由 Brain.MaybeAct() 调度。
    /// </summary>
    public class SpearChargeComponent : MonoBehaviour, IBrainAction
    {
        const float DetectionRadius = 5.0f;
        const float ReadyDist = 3.5f;
        const float StabDist = 1.5f;
        const float ChargeSpeed = 1.0f;
        const float CooldownTime = 1.5f;
        const float WindUpDuration = 0.25f;
        const float ChargingMaxTime = 3.0f;
        const float StabDamage = 3.0f;
        const float StabKnockback = 2.5f;
        const float StabStun = 8f;

        enum Phase { Idle, WindUp, Charging, Stab, Cooldown }

        Phase _phase = Phase.Idle;
        Agent _agent;
        Squad _squad;
        bool _setupDone;
        float _phaseTimer;
        Vector3 _chargeDirection;
        float _originalSpeed;
        Agent _targetAgent;
        readonly Collider[] _hitBuffer = new Collider[16];
        float _lastLogTime = -999f;

        public void Setup(Agent agent)
        {
            if (_setupDone) return;
            _setupDone = true;
            _agent = agent;
            if (_agent == null) { Destroy(this); return; }
            _squad = _agent.squad;
            _originalSpeed = _agent.maxSpeed;
            Log("Setup OK. speed=" + _originalSpeed.ToString("F1"));
        }

        bool IBrainAction.MaybeAct(Brain brain)
        {
            if (_phase != Phase.Idle) return false;
            if (_agent == null || _agent.aliveState == null || !_agent.aliveState.active || !_agent.dangerous) return false;
            if (!FindNearestEnemy(out _chargeDirection, out _targetAgent)) return false;
            StartWindUp();
            return true;
        }

        void Update()
        {
            if (_agent == null) { Destroy(this); return; }
            switch (_phase)
            {
                case Phase.WindUp: DoWindUp(); break;
                case Phase.Charging: DoCharging(); break;
                case Phase.Stab: DoStab(); break;
                case Phase.Cooldown: UpdateCooldown(); break;
            }
        }

        void StartWindUp()
        {
            _phase = Phase.WindUp;
            _phaseTimer = WindUpDuration;
            _agent.movability = 0f;
            _agent.maxSpeed = 0f;
            _agent.walkDir = Vector3.zero;
            Log("WIND-UP");
        }

        void DoWindUp()
        {
            _agent.LookInDirection(_chargeDirection, 720f, 10f);
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f)
            {
                _phase = Phase.Charging;
                _phaseTimer = ChargingMaxTime;
                _agent.movability = 1f;
                _agent.maxSpeed = ChargeSpeed;
                _agent.walkDir = _chargeDirection;
                Log("CHARGING speed=" + ChargeSpeed);
            }
        }

        void DoCharging()
        {
            _phaseTimer -= Time.deltaTime;
            bool targetValid = _targetAgent != null
                && _targetAgent.aliveState != null && _targetAgent.aliveState.active;
            if (targetValid)
            {
                _chargeDirection = _targetAgent.transform.position - _agent.transform.position;
                _chargeDirection.y = 0f;
                _chargeDirection.Normalize();
            }
            _agent.maxSpeed = ChargeSpeed;
            _agent.walkDir = _chargeDirection;
            _agent.LookInDirection(_chargeDirection, 720f, 20f);

            float dist = targetValid
                ? Vector3.Distance(_agent.transform.position, _targetAgent.transform.position)
                : 999f;
            if (_phaseTimer <= 0f || dist > ReadyDist) { EndCharge(); return; }
            if (dist <= StabDist)
            {
                _phase = Phase.Stab;
                _phaseTimer = 0.15f;
                _agent.maxSpeed = 0f;
                _agent.walkDir = Vector3.zero;
                _agent.movability = 0f;
            }
        }

        void DoStab()
        {
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f) { PerformStab(); EndCharge(); }
        }

        void PerformStab()
        {
            var t = _targetAgent;
            if (t == null) t = _agent.enemyAgent;
            if (t == null) return;
            try
            {
                var s = new AttackSettings { damage = StabDamage, knockback = StabKnockback, launchImpulse = 0f, stun = StabStun };
                Vector3 d = (t.transform.position - _agent.transform.position).normalized;
                d.y = 0f;
                if (d.sqrMagnitude < 0.001f) d = _agent.transform.forward;
                t.DealDamage(new Attack(s, d, t.transform.position, this, _squad, "Sfx/English/Spear"));
                Log("HIT " + t.name + " dmg=" + StabDamage);
            }
            catch (Exception ex) { Plugin.Log?.LogError("[Charge] " + ex.Message); }
        }

        void EndCharge()
        {
            _phase = Phase.Cooldown;
            _phaseTimer = CooldownTime;
            _agent.maxSpeed = 0f;
            _agent.walkDir = Vector3.zero;
            _agent.movability = 1f;
            _targetAgent = null;
        }

        void UpdateCooldown()
        {
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f) { _phase = Phase.Idle; _agent.maxSpeed = _originalSpeed; }
        }

        void OnDestroy()
        {
            if (_phase == Phase.Charging || _phase == Phase.Stab || _phase == Phase.WindUp)
            {
                _agent.maxSpeed = _originalSpeed;
                _agent.movability = 1f;
            }
        }

        bool FindNearestEnemy(out Vector3 dir, out Agent target)
        {
            dir = Vector3.zero;
            target = null;
            int n = Physics.OverlapSphereNonAlloc(_agent.transform.position, DetectionRadius, _hitBuffer, ~0, QueryTriggerInteraction.Ignore);
            float best = DetectionRadius;
            for (int i = 0; i < n; i++)
            {
                var c = _hitBuffer[i];
                if (c == null) continue;
                var a = c.GetComponentInParent<Agent>();
                if (a == null || a == _agent || a.isViking) continue;
                if (a.aliveState != null && !a.aliveState.active) continue;
                float dist = Vector3.Distance(_agent.transform.position, a.transform.position);
                if (dist < best) { best = dist; target = a; }
            }
            if (target == null) return false;
            dir = (target.transform.position - _agent.transform.position).normalized;
            dir.y = 0f;
            return true;
        }

        void Log(string msg)
        {
            if (Time.time - _lastLogTime >= 1f)
            {
                _lastLogTime = Time.time;
                Plugin.Log?.LogInfo("[Charge] " + msg);
            }
        }
    }
}
