using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// 黑矛兵冲刺技能组件。
    /// v1.14 改进：
    /// - 使用 Attack 结构体 + DealDamage 完整攻击链路（护甲/击退/眩晕/音效/特效全部生效）
    /// - 使用 Physics.OverlapSphere 替代 FindObjectsOfType 遍历
    /// - 参考 Spear.cs 官方实现，movability = 0.5f 而非完全禁用 AI
    /// </summary>
    public class SpearChargeComponent : MonoBehaviour
    {
        // 探测和命中范围：对齐长矛兵 spearLength(0.6) + radius 等效距离
        private const float DetectionRadius = 7.0f;
        private const float ChargeDistance = 5.0f;
        private const float ChargeSpeed = 6.0f;
        private const float ChargeCooldown = 8.0f;
        private const float RecoveryTime = 0.4f;
        private const float HitRadius = 3.0f;
        private const float HitInterval = 0.1f;
        private const float ChargeDuration = 0.83f;

        // 冲刺攻击参数
        private const float ChargeDamage = 3.33f;
        private const float ChargeKnockback = 0.5f;
        private const float ChargeStun = 10f;

        private enum Phase { Idle, Charging, Cooldown }
        private Phase _phase = Phase.Idle;

        private Agent _agent;
        private Squad _squad;
        private bool _setupDone;
        private float _phaseTimer;
        private Vector3 _chargeDirection;
        private float _chargeDistanceTraveled;
        private float _originalMaxSpeed;
        private bool _weaponTryDone;

        private HashSet<Agent> _hitAgents = new HashSet<Agent>();
        private float _lastHitTime = -999f;

        // 碰撞检测缓存
        private Collider[] _hitBuffer = new Collider[32];
        private int _englishLayerMask;

        // 眩晕免疫
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
            _squad = _agent.squad;
            _originalMaxSpeed = _agent.maxSpeed;
            CacheStun();
            _stunComponent = _agent.GetComponent<Stun>();
            // 缓存 English faction 的 layer mask
            _englishLayerMask = LayerMask.GetMask("English");
            if (_englishLayerMask == 0)
            {
                // 回退：使用默认 layer（Bad North 中 English 通常在 Default layer）
                _englishLayerMask = 1; // Default layer
            }
            _phase = Phase.Idle;
            _phaseTimer = 0.5f;
            Log("Setup OK. maxSpeed=" + _originalMaxSpeed.ToString("F1") + " layerMask=" + _englishLayerMask);
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

            // 参考 Spear.cs 官方实现：movability = 0.5f（半限制而非完全禁用）
            _agent.movability = 0.5f;
            _agent.maxSpeed = ChargeSpeed;
            _agent.walkDir = _chargeDirection;
            _agent.LookInDirection(_chargeDirection, 720f, 20f);

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

        /// <summary>
        /// 使用 Physics.OverlapSphere 检测命中，替代全场景 FindObjectsOfType 遍历
        /// </summary>
        private void DetectAndApplyHit()
        {
            int hitCount = Physics.OverlapSphereNonAlloc(
                _agent.transform.position, HitRadius,
                _hitBuffer, _englishLayerMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hitCount; i++)
            {
                Collider col = _hitBuffer[i];
                if (ReferenceEquals(col, null)) continue;

                Agent other = col.GetComponentInParent<Agent>();
                if (ReferenceEquals(other, null)) continue;
                if (ReferenceEquals(other, _agent)) continue;
                if (other.isViking) continue;
                if (_hitAgents.Contains(other)) continue;
                if (!ReferenceEquals(other.aliveState, null) && !other.aliveState.active) continue;

                _hitAgents.Add(other);
                _lastHitTime = Time.time;
                ApplyChargeDamage(other);
            }
        }

        /// <summary>
        /// ✅ v1.14 改进：使用 Attack 结构体 + DealDamage 完整攻击链路
        /// 护甲(Armor.ModifyAttack)、击退、眩晕(Stun.PostAttack)、音效、特效全部生效
        /// </summary>
        private void ApplyChargeDamage(Agent target)
        {
            if (ReferenceEquals(target, null)) return;
            try
            {
                // ✅ 使用原版 AttackSettings 四维向量
                var settings = new AttackSettings
                {
                    damage = ChargeDamage,
                    knockback = ChargeKnockback,
                    launchImpulse = 0f,
                    stun = ChargeStun
                };

                Vector3 dirToTarget = (target.transform.position - _agent.transform.position).normalized;
                dirToTarget.y = 0f;

                // ✅ 使用原版 Attack 结构体
                var attack = new Attack(
                    settings,
                    dirToTarget,
                    target.transform.position,
                    this,
                    _squad,
                    "Spear"
                );

                // ✅ 走完整 DealDamage 链路
                // → IAttackResponder 修正链（Armor 护甲减伤）
                // → knockback 击退
                // → launchImpulse 击飞
                // → health 扣血
                // → Stun.PostAttack 眩晕累加
                // → 死亡特效/音效
                target.DealDamage(attack);
            }
            catch (Exception ex)
            {
                Plugin.LogErr("[Charge] DmgErr: " + ex.Message);
            }
        }

        private bool HasNearbyEnemy(out Vector3 direction)
        {
            direction = Vector3.zero;

            // 使用 Physics.OverlapSphere 检测附近敌人
            int hitCount = Physics.OverlapSphereNonAlloc(
                _agent.transform.position, DetectionRadius,
                _hitBuffer, _englishLayerMask, QueryTriggerInteraction.Ignore);

            Agent closest = null;
            float closestDist = DetectionRadius;

            for (int i = 0; i < hitCount; i++)
            {
                Collider col = _hitBuffer[i];
                if (ReferenceEquals(col, null)) continue;

                Agent other = col.GetComponentInParent<Agent>();
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