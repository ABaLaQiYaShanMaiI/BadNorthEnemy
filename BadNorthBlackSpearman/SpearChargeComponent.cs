using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// 黑矛兵冲刺技能组件 — IBrainAction 实现。
    /// 
    /// v1.15 改进：
    /// - 实现 IBrainAction 接口，冲刺「启动判定」由 Brain.MaybeAct() 调度（替代独立 Update 中的检测逻辑）
    /// - 冲刺「执行阶段」保留独立 Update（movability 控制、物理检测需要每帧运行）
    /// - 参考 AxeThrowing 模式：MaybeAct 触发 prepare，Update 执行 charging/cooldown
    /// - 使用 Physics.OverlapSphere 替代 FindObjectsOfType 遍历
    /// - 使用 Attack 结构体 + DealDamage 完整攻击链路
    /// - 修复 layer mask：改为基于 faction 而非硬编码 "English" 字符串
    ///
    /// 调度机制：
    /// - Brain.idle hz8 → MaybeAct → 检测敌人 → StartCharge
    /// - Update → DoCharging / UpdateCooldown（需要每帧更新）
    /// - 冲刺结束后回退到 idle，等待 Brain 下次调度 MaybeAct
    ///
    /// 参考：
    /// - AxeThrowing.cs（IBrainAction + prepare/axeThrowing 状态机）
    /// - Spear.cs（movability = 0.5f，charging/stabbing 状态机）
    /// - Attack.cs 构造签名验证
    /// </summary>
    public class SpearChargeComponent : MonoBehaviour, IBrainAction
    {
        // 探测和命中范围
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

        private HashSet<Agent> _hitAgents = new HashSet<Agent>();
        private float _lastHitTime = -999f;

        // 碰撞检测缓存
        private Collider[] _hitBuffer = new Collider[32];
        /// <summary>
        /// ✅ v1.15 修复：使用 ~0（所有层）作为兜底，
        /// 实际检测通过 Agent 的 faction 进行过滤（非 Viking 且非自身）
        /// 原代码使用 LayerMask.GetMask("English") 在 Bad North 中可能无效
        /// </summary>
        private int _enemyLayerMask = ~0;

        // 眩晕免疫
        private static FieldInfo _stunMultiplierField;
        private static bool _stunCached;
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

            CacheStun();
            _stunComponent = _agent.GetComponent<Stun>();

            // ✅ v1.15 修复：不再硬编码 "English" layer
            // 使用 ~0（所有 layer），实际过滤通过 faction 判断
            // Bad North 中 English 单位在 Default layer (0)，不是独立 layer
            _enemyLayerMask = ~0;

            _phase = Phase.Idle;
            _phaseTimer = 0.5f;
            _originalMaxSpeed = _agent.maxSpeed;
            Log("Setup OK. maxSpeed=" + _originalMaxSpeed.ToString("F1") + " layerMask=All (faction-filtered)");
        }

        /// <summary>
        /// IBrainAction 接口 — Brain 在 idle 状态 hz8 节拍调用
        /// 检测附近是否有敌人，有则启动冲刺
        /// </summary>
        bool IBrainAction.MaybeAct(Brain brain)
        {
            // 只在 Idle 阶段响应
            if (_phase != Phase.Idle)
                return false;

            if (!_setupDone || ReferenceEquals(_agent, null))
                return false;

            if (!_agent.navPos.island)
                return false;

            // 武器未缓存时尝试搜索
            if (!Plugin.WeaponCached)
                Plugin.SearchForPikemanWeapon();

            if (Plugin.WeaponCached)
                Plugin.ReapplyWeaponIfNeeded(_agent);

            Vector3 dir;
            if (HasNearbyEnemy(out dir))
            {
                _chargeDirection = dir;
                StartCharge();
                return true; // 消耗 action 机会
            }

            return false;
        }

        /// <summary>
        /// 保留独立 Update 用于冲刺执行阶段和冷却恢复
        /// （movability/物理检测/冷却计时需要每帧运行）
        /// </summary>
        private void Update()
        {
            if (!_setupDone || ReferenceEquals(_agent, null)) return;

            // 死亡检查
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
                    // Idle 阶段不做事，等待 IBrainAction.MaybeAct 触发
                    break;
                case Phase.Charging:
                    DoCharging();
                    break;
                case Phase.Cooldown:
                    UpdateCooldown();
                    break;
            }
        }

        private void OnDestroy() { TryEndCharge(); }

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
        /// ✅ 使用 Physics.OverlapSphere 检测命中
        /// ✅ v1.15 修复：使用 ~0 mask + faction 过滤，不再依赖不存在的 "English" layer
        /// </summary>
        private void DetectAndApplyHit()
        {
            int hitCount = Physics.OverlapSphereNonAlloc(
                _agent.transform.position, HitRadius,
                _hitBuffer, _enemyLayerMask, QueryTriggerInteraction.Ignore);

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
        /// ✅ v1.15 验证：使用 Attack 结构体 + DealDamage 完整攻击链路
        /// 构造签名：Attack(AttackSettings settings, Vector3 direction, Vector3 pos, MonoBehaviour monoAttacker, Squad killerSquad, string weapon, ReusableEffect effect = null)
        /// 与原版 Attack.cs 完全一致
        /// </summary>
        private void ApplyChargeDamage(Agent target)
        {
            if (ReferenceEquals(target, null)) return;
            try
            {
                var settings = new AttackSettings
                {
                    damage = ChargeDamage,
                    knockback = ChargeKnockback,
                    launchImpulse = 0f,
                    stun = ChargeStun
                };

                Vector3 dirToTarget = (target.transform.position - _agent.transform.position).normalized;
                dirToTarget.y = 0f;

                var attack = new Attack(
                    settings,          // AttackSettings（四维向量）
                    dirToTarget,       // direction
                    target.transform.position,  // pos
                    this,              // monoAttacker
                    _squad,            // killerSquad
                    "Sfx/English/Spear"  // weapon → sound = "Sfx/English/Spear/Hit"
                );

                // 走完整 DealDamage 链路
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

            int hitCount = Physics.OverlapSphereNonAlloc(
                _agent.transform.position, DetectionRadius,
                _hitBuffer, _enemyLayerMask, QueryTriggerInteraction.Ignore);

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
            if (!ReferenceEquals(_stunMultiplierField, null))
            {
                if (immune)
                {
                    _originalStunMultiplier = (float)_stunMultiplierField.GetValue(_stunComponent);
                    _stunMultiplierField.SetValue(_stunComponent, 0f);
                }
                else
                {
                    _stunMultiplierField.SetValue(_stunComponent, _originalStunMultiplier);
                }
            }
        }

        private static void CacheStun()
        {
            if (_stunCached) return;
            _stunCached = true;
            _stunMultiplierField = typeof(Stun).GetField("stunMultiplier", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        private void Log(string msg)
        {
            if (Time.time - _lastLogTime < 1f) return;
            _lastLogTime = Time.time;
            Plugin.LogInfo("[Charge] " + msg);
        }
    }
}