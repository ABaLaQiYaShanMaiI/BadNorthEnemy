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
        private const float ChargeDistance = 3.5f;
        private const float ChargeSpeed = 6.0f;
        private const float ChargeCooldown = 8.0f;
        private const float RecoveryTime = 0.4f;
        private const float HitRadius = 1.2f;
        private const float HitInterval = 0.15f;
        private const float ChargeDuration = 0.58f;

        private enum Phase { Idle, Charging, Cooldown }
        private Phase _phase = Phase.Idle;
        private Agent _agent;
        private bool _setupDone;
        private float _phaseTimer;
        private Vector3 _chargeDirection;
        private float _chargeDistanceTraveled;
        private float _originalMaxSpeed;

        private HashSet<Agent> _hitAgents = new HashSet<Agent>();
        private float _lastHitTime = -999f;
        private bool _hitDiagnosticDone;

        private enum StunImmunityStrategy { None, StunMultiplier }
        private static StunImmunityStrategy _stunStrategy;
        private static bool _stunCached;
        private static FieldInfo _stunMultiplierField;
        private float _originalStunMultiplier = 1f;
        private Stun _stunComponent;

        private float _lastLogTime = -999f;

        public static SpearChargeComponent AddTo(Agent agent)
        {
            if (ReferenceEquals(agent, null)) return null;
            SpearChargeComponent existing = agent.GetComponent<SpearChargeComponent>();
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
            _phaseTimer = 0f;
            Log("Setup OK. origMaxSpeed=" + _originalMaxSpeed.ToString("F1"));
        }

        private void Update()
        {
            if (!_setupDone || ReferenceEquals(_agent, null)) return;

            if (!ReferenceEquals(_agent.aliveState, null) && !_agent.aliveState.active)
            {
                TryEndCharge();
                Destroy(this);
                return;
            }

            bool spawned = !ReferenceEquals(_agent.spawned, null) && _agent.spawned.active;
            if (!spawned) return;

            switch (_phase)
            {
                case Phase.Idle:
                    UpdateIdle();
                    break;
                case Phase.Charging:
                    DoCharging();
                    break;
                case Phase.Cooldown:
                    UpdateCooldown();
                    break;
            }
        }

        private void OnDestroy()
        {
            TryEndCharge();
        }

        // ===== Idle =====
        private void UpdateIdle()
        {
            if (!_agent.navPos.island) return;
            Vector3 dir;
            if (HasNearbyEnemy(out dir))
            {
                _chargeDirection = dir;
                StartCharge();
            }
        }

        // ===== Charging =====
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
            _agent.maxSpeed = 0f;

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
            _agent.maxSpeed = 0f; // recovery 期间冻结
            Log("Charge ended. Cooldown " + ChargeCooldown + "s");
        }

        // ===== Cooldown =====
        private void UpdateCooldown()
        {
            _phaseTimer -= Time.deltaTime;
            float recoveryEnd = ChargeCooldown - RecoveryTime;

            if (_phaseTimer > recoveryEnd)
            {
                // recovery 期内冻结
                _agent.maxSpeed = 0f;
                _agent.walkDir = Vector3.zero;
            }
            else
            {
                // ⭐ 恢复 AI 控制
                _agent.maxSpeed = _originalMaxSpeed;
            }

            if (_phaseTimer <= 0f)
            {
                _phase = Phase.Idle;
                _phaseTimer = 0f;
                _hitAgents.Clear();
                _agent.maxSpeed = _originalMaxSpeed;
                Log("Cooldown over. Ready.");
            }
        }

        private void TryEndCharge()
        {
            if (_phase != Phase.Charging) return;
            EndCharge();
        }

        // ===== 伤害 =====
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

                if (!_hitDiagnosticDone)
                {
                    _hitDiagnosticDone = true;
                    LogDirect("[Charge] HIT DETECT: " + other.name + " d=" + dist.ToString("F2"));
                }

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
                MethodInfo dealDamage = typeof(Agent).GetMethod("DealDamage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (!ReferenceEquals(dealDamage, null))
                {
                    ConstructorInfo atkCtor = typeof(Attack).GetConstructor(new Type[] {
                        typeof(AttackSettings), typeof(Vector3), typeof(Vector3),
                        typeof(Component), typeof(Squad), typeof(string), typeof(GameObject)
                    });

                    if (!ReferenceEquals(atkCtor, null))
                    {
                        AttackSettings s = new AttackSettings();
                        s.damage = 3.33f;
                        s.knockback = 10.62f;
                        s.stun = 0.5f;

                        Vector3 dir = _chargeDirection.normalized;
                        dir.y = 0;

                        object atk = atkCtor.Invoke(new object[] { s, dir, target.chestPos, this, null, "Spear", null });
                        dealDamage.Invoke(target, new object[] { atk });
                        Log("HIT " + target.name);
                        return;
                    }
                }

                float nh = target.health - 3.3f;
                if (nh < 0f) nh = 0f;
                target.health = nh;
                Log("HIT(fb) " + target.name);
            }
            catch (Exception ex)
            {
                LogDirect("[Charge] DmgErr: " + ex.Message);
                float nh = target.health - 3.3f;
                if (nh < 0f) nh = 0f;
                target.health = nh;
            }
        }

        // ===== 敌人检测 =====
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
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = other;
                }
            }

            if (ReferenceEquals(closest, null)) return false;
            direction = (closest.transform.position - _agent.transform.position).normalized;
            return true;
        }

        // ===== 眩晕 =====
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
            _stunMultiplierField = typeof(Stun).GetField("stunMultiplier",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(_stunMultiplierField, null))
            {
                _stunStrategy = StunImmunityStrategy.StunMultiplier;
                return;
            }
            _stunStrategy = StunImmunityStrategy.None;
        }

        // ===== 日志 =====
        private void Log(string msg)
        {
            if (Time.time - _lastLogTime < 2f) return;
            _lastLogTime = Time.time;
            LogDirect("[Charge] " + msg);
        }

        private void LogDirect(string msg)
        {
            if (!ReferenceEquals(Plugin.SharedLogger, null))
                Plugin.SharedLogger.LogInfo("[BlackSpearman] " + msg);
        }
    }
}