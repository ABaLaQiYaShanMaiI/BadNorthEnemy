using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    public class SpearChargeComponent : MonoBehaviour
    {
        private const float DetectionRadius = 5.0f;
        private const float ChargeDistance = 5.0f;
        private const float ChargeSpeed = 6.0f;
        private const float ChargeCooldown = 8.0f;
        private const float RecoveryTime = 0.4f;
        private const float HitRadius = 2.5f;
        private const float HitInterval = 0.1f;
        private const float ChargeDuration = 0.83f;

        private enum Phase { Idle, Charging, Cooldown }
        private Phase _phase = Phase.Idle;

        private Agent _agent;
        private bool _setupDone;
        private float _phaseTimer;
        private Vector3 _chargeDirection;
        private float _chargeDistanceTraveled;
        private float _originalMaxSpeed;
        private bool _weaponTryDone;
        private int _frameCount;

        private HashSet<Agent> _hitAgents = new HashSet<Agent>();
        private float _lastHitTime = -999f;

        private enum StunImmunityStrategy { None, StunMultiplier }
        private static StunImmunityStrategy _stunStrategy        private static bool _stunCached;
        private static FieldInfo _stunMultiplierField;
        private float _originalStunMultiplier = 1f;
        private Stun _stunComponent;

        private float _lastLogTime = -999f;

        public static SpearChargeComponent AddTo(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return null;
            var existing = agent.GetComponent<SpearChargeComponent>();
            if (!ReferenceEquals(existing, null)) return existing;
            return agent.gameObject.AddComponent<SpearChargeComponent>();
        }

        public void Setup(Agent agent)
        {
            if (_setupDone) return;
            _setupDone = true;
            _agent = agent;
            if (ReferenceEquals(_agent, null)) { Destroy(this); return; }
            _originalMaxSpeed = _agent.maxSpeed;
            CacheStun();
            _stunComponent = _agent.GetComponent<Stun>();
            _phase = Phase.Idle;
            _phaseTimer = 0.5f;
            Log("Setup OK. maxSpeed=" + _originalMaxSpeed.ToString("F1"));
        }

        private void Update()
        {
            _frameCount++;

            if (!_setupDone || ReferenceEquals(_agent, null)) return;

            if (!ReferenceEquals(_agent.aliveState, null) && !_agent.aliveState.active)
            {
                TryEndCharge();
                Destroy(this);
                return;
            }

            bool spawned = !ReferenceEquals(_agent.spawned, null) && _agent.spawned.active;
            if (!spawned) return;

            // 登岛后首次尝试武器搜索和武器替换
            if (!_weaponTryDone && gameObject.activeInHierarchy)
            {
                _weaponTryDone = true;
                Log("First island frame. WeaponCached=" + Plugin.WeaponCached + " activeInHierarchy=" + gameObject.activeInHierarchy);

                if (!Plugin.WeaponCached)
                    Plugin.SearchForPikemanWeapon();

                if (Plugin.WeaponCached)
                    Plugin.ReapplyWeaponIfNeeded(_agent);
            }

            switch (_phase)
            {
                case Phase.Idle: UpdateIdle(); break;
                case Phase.Charging: DoCharging(); break;
                case Phase.Cooldown: UpdateCooldown(); break;
            }
        }

        private void OnDestroy() { TryEndCharge(); }

        private void UpdateIdle()
        {
            if (!_agent.navPos.island) return;

            if (!Plugin.WeaponCached)
                Plugin.SearchForPikemanWeapon();

            if (Plugin.WeaponCached)
                Plugin.ReapplyWeaponIfNeeded(_agent);

            Vector3 dir;
            if (HasNearbyEnemy(out dir))
            {
                _chargeDirection = dir;
                StartCharge();
            }
        }

        private void StartCharge()
        {
            _phase = Phase.Charging;
            _chargeDistanceTraveled = 0f;
            _hitAgents.Clear();
            _phaseTimer = ChargeDuration;
            _originalMaxSpeed = _agent.maxSpeed;
            SetStunImmunity(true);
            Log("CHARGE! Dir=" + _chargeDirection.ToString("F1"));
        }

        private void DoCharging()
        {
            float dt = Time.deltaTime;
            _chargeDistanceTraveled += ChargeSpeed * dt;

            Vector3 newPos = _agent.transform.position + _chargeDirection * ChargeSpeed * dt;
            _agent.transform.position = newPos;

            try { _agent.navPos = new NavPos(_agent.navPos.navigationMesh, newPos, true, 1f); }
            catch { }

            _agent.LookInDirection(_chargeDirection, 720f, 20f);
            _agent.movability = 0f;
            _agent.maxSpeed = 0f;
            _agent.walkDir = Vector3.zero;

            if (Time.time - _lastHitTime >= HitInterval)
                DetectAndApplyHit();

            if (_chargeDistanceTraveled >= ChargeDistance)
                EndCharge();
        }

        private void EndCharge()
        {
            _phase = Phase.Cooldown;
            _phaseTimer = ChargeCooldown;
            SetStunImmunity(false);
            _agent.maxSpeed = 0f;
            Log("Charge ended. Hits: " + _hitAgents.Count);
        }

        private void UpdateCooldown()
        {
            _phaseTimer -= Time.deltaTime;
            float recoveryEnd = ChargeCooldown - RecoveryTime;

            if (_phaseTimer > recoveryEnd)
            {
                _agent.movability = 0.35f;
                _agent.maxSpeed = 0f;
                _agent.walkDir = Vector3.zero;
            }
            else
            {
                _agent.movability = 1f;
                _agent.maxSpeed = _originalMaxSpeed;
            }

            if (_phaseTimer <= 0f)
            {
                _phase = Phase.Idle;
                _phaseTimer = 0f;
                _hitAgents.Clear();
                _agent.movability = 1f;
                _agent.maxSpeed = _originalMaxSpeed;
            }
        }

        private void TryEndCharge() { if (_phase == Phase.Charging) EndCharge(); }

        private void DetectAndApplyHit()
        {
            Agent[] allAgents = UnityEngine.Object.FindObjectsOfType<Agent>();

            for (int i = 0; i < allAgents.Length; i++)
            {
                Agent other = allAgents[i];
                if (ReferenceEquals(other, null)) continue;
                if (ReferenceEquals(other, _agent)) continue;
                if (other.isViking) continue;
                if (_hitAgents.Contains(other)) continue;
                if (!ReferenceEquals(other.aliveState, null) && !other.aliveState.active) continue;

                float dist = Vector3.Distance(_agent.transform.position, other.transform.position);
                if (dist > HitRadius) continue;

                _hitAgents.Add(other);
                _lastHitTime = Time.time;
                ApplyChargeDamage(other);
            }
        }

        private void ApplyChargeDamage(Agent target)
        {
            if (ReferenceEquals(target, null)) return;
            try
            {
                float dmg = 3.33f;
                float nh = target.health - dmg;
                if (nh < 0f) nh = 0f;
                target.health = nh;

                Vector3 kb = _chargeDirection.normalized * 0.5f;
                kb.y = 0;
                target.transform.position += kb;

                var stun = target.GetComponent<Stun>();
                if (!ReferenceEquals(stun, null))
                {
                    var smf = typeof(Stun).GetField("stunMultiplier", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(smf, null)) smf.SetValue(stun, 10f);
                }
            }
            catch (Exception ex) { Plugin.LogErr("[Charge] DmgErr: " + ex.Message); }
        }

        private bool HasNearbyEnemy(out Vector3 direction)
        {
            direction = Vector3.zero;
            Agent[] allAgents = UnityEngine.Object.FindObjectsOfType<Agent>();
            Agent closest = null;
            float closestDist = DetectionRadius;

            for (int i = 0; i < allAgents.Length; i++)
            {
                Agent other = allAgents[i];
                if (ReferenceEquals(other, null)) continue;
                if (ReferenceEquals(other, _agent)) continue;
                if (other.isViking) continue;
                if (!ReferenceEquals(other.aliveState, null) && !other.aliveState.active) continue;

                float dist = Vector3.Distance(_agent.transform.position, other.transform.position);
                if (dist < closestDist) { closestDist = dist; closest = other; }
            }

            if (ReferenceEquals(closest, null)) return false;
            direction = (closest.transform.position - _agent.transform.position).normalized;
            return true;
        }

        private void SetStunImmunity(bool immune)
        {
            if (ReferenceEquals(_stunComponent, null)) return;
            if (_stunStrategy == StunImmunityStrategy.StunMultiplier && !ReferenceEquals(_stunMultiplierField, null))
                _stunMultiplierField.SetValue(_stunComponent, immune ? 0f : _originalStunMultiplier);
        }

        private static void CacheStun()
        {
            if (_stunCached) return;
            _stunCached = true;
            _stunMultiplierField = typeof(Stun).GetField("stunMultiplier", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _stunStrategy = !ReferenceEquals(_stunMultiplierField, null) ? StunImmunityStrategy.StunMultiplier : StunImmunityStrategy.None;
        }

        private void Log(string msg)
        {
            if (Time.time - _lastLogTime < 1f) return;
            _lastLogTime = Time.time;
            Plugin.LogInfo("[Charge] " + msg);
        }
    }
}