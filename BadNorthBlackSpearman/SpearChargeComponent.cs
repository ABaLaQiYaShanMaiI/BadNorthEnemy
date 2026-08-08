using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    public class SpearChargeComponent : MonoBehaviour, IBrainAction
    {
        private const float DetectionRadius = 5.0f;
        private const float ReadyDist = 3.5f;
        private const float StabDist = 1.5f;
        private const float ChargeSpeed = 1.0f;
        private const float CooldownTime = 1.5f;
        private const float WindUpDuration = 0.25f;
        private const float ChargingMaxTime = 3.0f;
        private const float StabDamage = 3.0f;
        private const float StabKnockback = 2.5f;
        private const float StabStun = 8f;

        private enum Phase { Idle, WindUp, Charging, Stab, Cooldown }
        private Phase _phase = Phase.Idle;
        private Agent _agent;
        private Squad _squad;
        private bool _setupDone;
        private float _phaseTimer;
        private Vector3 _chargeDirection;
        private float _originalSpeed;
        private Agent _targetAgent;
        private Collider[] _hitBuffer = new Collider[16];
        private float _lastLogTime = -999f;

        public static SpearChargeComponent AddTo(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return null;
            var e = agent.GetComponent<SpearChargeComponent>();
            if (!ReferenceEquals(e, null)) return e;
            return agent.gameObject.AddComponent<SpearChargeComponent>();
        }

        public void Setup(Agent agent)
        {
            if (_setupDone) return;
            _setupDone = true;
            _agent = agent;
            if (ReferenceEquals(_agent, null)) { Destroy(this); return; }
            _squad = _agent.squad;
            _originalSpeed = _agent.maxSpeed;
            Log("Setup OK. speed=" + _originalSpeed.ToString("F1"));
        }

        bool IBrainAction.MaybeAct(Brain brain)
        {
            if (_phase != Phase.Idle) return false;
            if (ReferenceEquals(_agent, null) || ReferenceEquals(_agent.aliveState, null)
                || !_agent.aliveState.active || !_agent.dangerous) return false;
            if (!FindNearestEnemy(out _chargeDirection, out _targetAgent)) return false;
            StartWindUp();
            return true;
        }

        private void Update()
        {
            switch (_phase)
            {
                case Phase.Idle: break;
                case Phase.WindUp: DoWindUp(); break;
                case Phase.Charging: DoCharging(); break;
                case Phase.Stab: DoStab(); break;
                case Phase.Cooldown: UpdateCooldown(); break;
            }
        }

        private void StartWindUp()
        {
            _phase = Phase.WindUp;
            _phaseTimer = WindUpDuration;
            _agent.movability = 0f;
            _agent.maxSpeed = 0f;
            _agent.walkDir = Vector3.zero;
            Log("WIND-UP");
        }

        private void DoWindUp()
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

        private void DoCharging()
        {
            _phaseTimer -= Time.deltaTime;
            if (!ReferenceEquals(_targetAgent, null) && _targetAgent.aliveState.active)
            {
                _chargeDirection = (_targetAgent.transform.position - _agent.transform.position);
                _chargeDirection.y = 0f;
                _chargeDirection.Normalize();
            }
            _agent.maxSpeed = ChargeSpeed;
            _agent.walkDir = _chargeDirection;
            _agent.LookInDirection(_chargeDirection, 720f, 20f);

            float dist = ReferenceEquals(_targetAgent, null) ? 999f :
                Vector3.Distance(_agent.transform.position, _targetAgent.transform.position);
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

        private void DoStab()
        {
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f) { PerformStab(); EndCharge(); }
        }

        private void PerformStab()
        {
            var t = _targetAgent;
            if (ReferenceEquals(t, null)) t = _agent.enemyAgent;
            if (ReferenceEquals(t, null)) return;
            try
            {
                float prev = t.health;
                var s = new AttackSettings { damage = StabDamage, knockback = StabKnockback, launchImpulse = 0f, stun = StabStun };
                Vector3 d = (t.transform.position - _agent.transform.position).normalized;
                d.y = 0f;
                if (d.sqrMagnitude < 0.001f) d = _agent.transform.forward;
                t.DealDamage(new Attack(s, d, t.transform.position, this, _squad, "Sfx/English/Spear"));
                Log("HIT " + t.name + " dmg=" + StabDamage + " hp=" + prev.ToString("F1") + "\u2192" + t.health.ToString("F1"));
            }
            catch (Exception ex) { Plugin.LogErr("[Charge] " + ex.Message); }
        }

        private void EndCharge()
        {
            _phase = Phase.Cooldown;
            _phaseTimer = CooldownTime;
            _agent.maxSpeed = 0f;
            _agent.walkDir = Vector3.zero;
            _agent.movability = 1f;
            _targetAgent = null;
        }

        private void UpdateCooldown()
        {
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f) { _phase = Phase.Idle; _agent.maxSpeed = _originalSpeed; }
        }

        private void OnDestroy()
        {
            if (_phase == Phase.Charging || _phase == Phase.Stab || _phase == Phase.WindUp)
            { _agent.maxSpeed = _originalSpeed; _agent.movability = 1f; }
        }

        private bool FindNearestEnemy(out Vector3 dir, out Agent target)
        {
            dir = Vector3.zero; target = null;
            int n = Physics.OverlapSphereNonAlloc(_agent.transform.position, DetectionRadius, _hitBuffer, ~0, QueryTriggerInteraction.Ignore);
            float best = DetectionRadius;
            for (int i = 0; i < n; i++)
            {
                var c = _hitBuffer[i];
                if (ReferenceEquals(c, null)) continue;
                var a = c.GetComponentInParent<Agent>();
                if (ReferenceEquals(a, null) || ReferenceEquals(a, _agent) || a.isViking) continue;
                if (!ReferenceEquals(a.aliveState, null) && !a.aliveState.active) continue;
                float dist = Vector3.Distance(_agent.transform.position, a.transform.position);
                if (dist < best) { best = dist; target = a; }
            }
            if (ReferenceEquals(target, null)) return false;
            dir = (target.transform.position - _agent.transform.position).normalized;
            dir.y = 0f;
            return true;
        }

        private void Log(string msg) { if (Time.time - _lastLogTime >= 1f) { _lastLogTime = Time.time; Plugin.LogInfo("[Charge] " + msg); } }
    }
}