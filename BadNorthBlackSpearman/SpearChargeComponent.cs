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
        private const float HitRadius = 1.2f;
        private const float HitInterval = 0.15f;
        private const float ChargeDuration = 0.58f;

        private Agent _agent;
        private bool _setupDone;
        private float _stateTimer;
        private Vector3 _chargeDirection;
        private float _chargeDistanceTraveled;

        private HashSet<Agent> _hitAgents = new HashSet<Agent>();
        private float _lastHitTime = -999f;
        private bool _hitDiagnosticDone;

        private object _chargeExclusive;
        private static Type _agentExclusivesType;
        private static Type _agentStateType;
        private static bool _typesCached;

        private enum StunImmunityStrategy { None, StunMultiplier, StunEnabled }
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

            CacheTypes();
            CacheStun();
            _stunComponent = _agent.GetComponent<Stun>();

            bool typeOk = !ReferenceEquals(_agentExclusivesType, null) && !ReferenceEquals(_agentStateType, null);
            if (typeOk)
            {
                try
                {
                    FieldInfo exclusivesField = typeof(Agent).GetField("exclusives",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(exclusivesField, null))
                    {
                        object exclusives = exclusivesField.GetValue(_agent);
                        if (!ReferenceEquals(exclusives, null))
                        {
                            _chargeExclusive = Activator.CreateInstance(_agentStateType,
                                "BlackSpearCharge", exclusives, false, true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!ReferenceEquals(Plugin.SharedLogger, null))
                        Plugin.SharedLogger.LogWarning("[BlackSpearman] AgentState creation failed: " + ex.Message);
                }
            }

            string hasState = ReferenceEquals(_chargeExclusive, null) ? "NO" : "YES";
            Log("Setup OK. AgentState=" + hasState);
        }

        private void Update()
        {
            if (!_setupDone || ReferenceEquals(_agent, null)) return;

            if (!ReferenceEquals(_agent.aliveState, null) && !_agent.aliveState.active)
            {
                EndCharge();
                Destroy(this);
                return;
            }

            bool spawned = !ReferenceEquals(_agent.spawned, null) && _agent.spawned.active;
            bool onIsland = _agent.navPos.island;
            if (!spawned || !onIsland) return;

            _stateTimer -= Time.deltaTime;

            if (_stateTimer <= 0f && HasNearbyEnemy(out _chargeDirection))
            {
                StartCharge();
            }
        }

        private void OnDestroy()
        {
            EndCharge();
        }

        private void StartCharge()
        {
            SetChargeActive(true);
            _chargeDistanceTraveled = 0f;
            _hitAgents.Clear();
            _stateTimer = ChargeDuration;
            SetStunImmunity(true);
            Log("CHARGE! Dir=" + _chargeDirection.ToString("F1") + " Speed=" + ChargeSpeed + "m/s");
        }

        private void UpdateCharging()
        {
            float dt = Time.deltaTime;
            _chargeDistanceTraveled += ChargeSpeed * dt;

            Vector3 newPos = _agent.transform.position + _chargeDirection * ChargeSpeed * dt;
            _agent.navPos = new NavPos(_agent.navPos.navigationMesh, newPos, true, 1f);

            _agent.LookInDirection(_chargeDirection, 720f, 20f);

            if (Time.time - _lastHitTime >= HitInterval)
            {
                DetectAndApplyHit();
            }

            if (_chargeDistanceTraveled >= ChargeDistance)
            {
                EndCharge();
            }
        }

        private void LateUpdate()
        {
            if (!_setupDone || ReferenceEquals(_agent, null)) return;
            if (ReferenceEquals(_chargeExclusive, null)) return;
            if (ReferenceEquals(_agentStateType, null)) return;

            try
            {
                PropertyInfo activeProp = _agentStateType.GetProperty("active",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(activeProp, null))
                {
                    bool isActive = (bool)activeProp.GetValue(_chargeExclusive, null);
                    if (isActive)
                    {
                        UpdateCharging();
                    }
                }
            }
            catch { }
        }

        private void EndCharge()
        {
            if (ReferenceEquals(_agent, null)) return;
            SetChargeActive(false);
            SetStunImmunity(false);
            _stateTimer = ChargeCooldown;
            Log("Charge ended.");
        }

        private void SetChargeActive(bool active)
        {
            if (ReferenceEquals(_agentStateType, null)) return;
            if (ReferenceEquals(_chargeExclusive, null)) return;
            try
            {
                MethodInfo method = _agentStateType.GetMethod("SetActive",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(method, null))
                    method.Invoke(_chargeExclusive, new object[] { active });
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(Plugin.SharedLogger, null))
                    Plugin.SharedLogger.LogWarning("[BlackSpearman] SetActive(" + active + ") failed: " + ex.Message);
            }
        }

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
                    if (!ReferenceEquals(Plugin.SharedLogger, null))
                        Plugin.SharedLogger.LogInfo("[BlackSpearman] [Charge] HIT: " + other.name + " dist=" + dist.ToString("F2"));
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
                MethodInfo dealDamageMethod = typeof(Agent).GetMethod("DealDamage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(dealDamageMethod, null))
                {
                    Type attackType = typeof(Attack);
                    ConstructorInfo ctor = attackType.GetConstructor(new Type[] {
                        typeof(AttackSettings), typeof(Vector3), typeof(Vector3),
                        typeof(Component), typeof(Squad), typeof(string), typeof(GameObject)
                    });
                    if (!ReferenceEquals(ctor, null))
                    {
                        AttackSettings settings = new AttackSettings();
                        settings.damage = 2.08f * 1.6f;
                        settings.knockback = 4.25f * 2.5f;
                        settings.stun = 0.5f;

                        Vector3 pos = target.chestPos;
                        Vector3 dir = _chargeDirection.normalized;
                        dir.y = 0;

                        Component source = _agent.brain as Component;
                        if (ReferenceEquals(source, null)) source = this;

                        object attack = ctor.Invoke(new object[] {
                            settings, dir, pos, source, null, "Spear", null
                        });

                        dealDamageMethod.Invoke(target, new object[] { attack });
                        Log("HIT " + target.name + "! Dmg=" + settings.damage.ToString("F1"));
                        return;
                    }
                }

                // fallback
                float newHealth = target.health - 3.3f;
                if (newHealth < 0f) newHealth = 0f;
                target.health = newHealth;
                Vector3 kbDir = _chargeDirection.normalized;
                kbDir.y = 0;
                target.transform.position += kbDir * 0.5f;
                Log("HIT(fb) " + target.name + " HP=" + target.health.ToString("F1"));
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(Plugin.SharedLogger, null))
                    Plugin.SharedLogger.LogError("[BlackSpearman] [Charge] Damage error: " + ex.Message);
            }
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
                if (!ReferenceEquals(Plugin.SharedLogger, null))
                    Plugin.SharedLogger.LogInfo("[BlackSpearman] Stun: using stunMultiplier");
                return;
            }
            _stunStrategy = StunImmunityStrategy.None;
            if (!ReferenceEquals(Plugin.SharedLogger, null))
                Plugin.SharedLogger.LogWarning("[BlackSpearman] Stun: NO strategy found");
        }

        private static void CacheTypes()
        {
            if (_typesCached) return;
            _typesCached = true;
            Assembly asm = typeof(Agent).Assembly;
            _agentExclusivesType = asm.GetType("Voxels.TowerDefense.AgentExclusives");
            _agentStateType = asm.GetType("Voxels.TowerDefense.AgentState");

            if (ReferenceEquals(_agentExclusivesType, null))
            {
                if (!ReferenceEquals(Plugin.SharedLogger, null))
                    Plugin.SharedLogger.LogWarning("[BlackSpearman] AgentExclusives type not found");
            }
            if (ReferenceEquals(_agentStateType, null))
            {
                if (!ReferenceEquals(Plugin.SharedLogger, null))
                    Plugin.SharedLogger.LogWarning("[BlackSpearman] AgentState type not found");
            }
            else
            {
                if (!ReferenceEquals(Plugin.SharedLogger, null))
                    Plugin.SharedLogger.LogInfo("[BlackSpearman] AgentState type found");
            }
        }

        private void Log(string msg)
        {
            if (Time.time - _lastLogTime < 2f) return;
            _lastLogTime = Time.time;
            if (!ReferenceEquals(Plugin.SharedLogger, null))
                Plugin.SharedLogger.LogInfo("[BlackSpearman] [Charge] " + msg);
        }
    }
}