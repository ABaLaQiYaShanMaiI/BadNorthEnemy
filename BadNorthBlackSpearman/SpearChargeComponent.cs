using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{

    public class SpearChargeComponent : MonoBehaviour
    {
        private enum ChargeState
        {
            Idle,
            Watching,
            Charging,
            Cooldown
        }

        private enum StunImmunityStrategy
        {
            None,
            StunMultiplier,
            StunEnabled
        }

        private const float ChargeDistance = 3.5f;

        /// <summary>冲刺速度（米/秒）</summary>
        private const float ChargeSpeed = 6.0f;

        /// <summary>冲刺冷却时间（秒）</summary>
        private const float ChargeCooldown = 8.0f;

        /// <summary>触发冲刺的检测半径</summary>
        private const float DetectionRadius = 5.0f;

        /// <summary>冲刺结束后的短暂硬直时间</summary>
        private const float RecoveryTime = 0.4f;

        /// <summary>冲刺伤害碰撞检测半径</summary>
        private const float HitRadius = 1.2f;

        /// <summary>冲刺伤害间隔（秒），防止同一帧多次伤害</summary>
        private const float HitInterval = 0.15f;

        private Agent _agent;
        private ChargeState _state = ChargeState.Idle;
        private float _stateTimer;
        private Vector3 _chargeDirection;
        private Vector3 _chargeStartPos;
        private float _chargeDistanceTraveled;
        private bool _setupDone;

        // 冲刺期间保存的原始值
        private float _originalMaxSpeed;
        private Vector3 _originalWalkDir;

        // 冲刺伤害追踪：记录已命中的 Agent，避免重复伤害
        private HashSet<Agent> _hitAgents = new HashSet<Agent>();
        private float _lastHitTime;

        // NavPos 同步缓存
        private static PropertyInfo _navPosProperty;
        private static bool _navPosCached;

        // ==============================
        // Stun 反射缓存
        // ==============================

        private static StunImmunityStrategy _stunStrategy = StunImmunityStrategy.None;
        private static bool _stunFieldAttempted;
        private static FieldInfo _stunMultiplierField;
        private float _originalStunMultiplier = 1f;
        private static PropertyInfo _stunEnabledProp;
        private static FieldInfo _stunEnabledField;
        private Stun _stunComponent;

        // 日志防刷屏
        private float _lastLogTime = -999f;

        // ==============================
        // 公共 API
        // ==============================

        public static SpearChargeComponent AddTo(Agent agent)
        {
            if (ReferenceEquals(agent, null))
                return null;

            SpearChargeComponent existing = agent.GetComponent<SpearChargeComponent>();
            if (!ReferenceEquals(existing, null))
                return existing;

            return agent.gameObject.AddComponent<SpearChargeComponent>();
        }

        public void Setup(Agent agent)
        {
            if (_setupDone) return;

            _agent = agent;
            _setupDone = true;

            if (ReferenceEquals(_agent, null))
            {
                Plugin.SharedLogger?.LogError("[BlackSpearman] SpearChargeComponent.Setup: agent is null!");
                Destroy(this);
                return;
            }

            _originalMaxSpeed = _agent.maxSpeed;
            _stunComponent = _agent.GetComponent<Stun>();
            CacheStunStrategy();
            CacheNavPos();

            if (_stunStrategy == StunImmunityStrategy.StunMultiplier && !ReferenceEquals(_stunComponent, null))
            {
                object val = _stunMultiplierField.GetValue(_stunComponent);
                if (val is float f) _originalStunMultiplier = f;
            }

            _state = ChargeState.Idle;
            LogStatus("Setup complete. Waiting for spawn...");
        }

        // ==============================
        // Update 主循环
        // ==============================

        private void Update()
        {
            if (!_setupDone || ReferenceEquals(_agent, null)) return;

            // 检查死亡
            if (!ReferenceEquals(_agent.aliveState, null) && !_agent.aliveState.active)
            {
                if (_state == ChargeState.Charging) EndCharge();
                Destroy(this);
                return;
            }

            switch (_state)
            {
                case ChargeState.Idle:      UpdateIdle();      break;
                case ChargeState.Watching:  UpdateWatching();  break;
                case ChargeState.Charging:  UpdateCharging();  break;
                case ChargeState.Cooldown:  UpdateCooldown();  break;
            }
        }

        private void OnDestroy()
        {
            if (_state == ChargeState.Charging)
                EndCharge();
        }

        // ==============================
        // 状态机实现
        // ==============================

        private void UpdateIdle()
        {
            if (!ReferenceEquals(_agent.spawned, null) && _agent.spawned.active)
            {
                _state = ChargeState.Watching;
                _stateTimer = 0f;
                LogStatus("Agent spawned. Watching for landing...");
            }
        }

        private void UpdateWatching()
        {
            _stateTimer += Time.deltaTime;

            // 30秒超时
            if (_stateTimer > 30f)
            {
                LogStatus("Watching timeout. Giving up.");
                Destroy(this);
                return;
            }

            // 必须已登岛
            if (!_agent.navPos.island) return;

            // 额外等待确保AI初始化
            if (_stateTimer < 0.3f) return;

            // 检测附近是否有敌方单位（玩家的单位）
            if (!HasNearbyEnemy(out _chargeDirection))
                return;

            // 开始直线冲刺！
            StartCharge();
        }

        private void UpdateCharging()
        {
            _stateTimer -= Time.deltaTime;

            // 计算本帧移动距离
            float moveDelta = ChargeSpeed * Time.deltaTime;
            _chargeDistanceTraveled += moveDelta;

            // 检查是否到达冲刺距离终点
            if (_chargeDistanceTraveled >= ChargeDistance || _stateTimer <= 0f)
            {
                EndCharge();
                _state = ChargeState.Cooldown;
                _stateTimer = ChargeCooldown;
                LogStatus($"Charge complete. Distance={_chargeDistanceTraveled:F1}m. Cooldown {ChargeCooldown}s.");
                return;
            }

            // ✅ 核心：直接控制 Transform 位置沿直线移动
            // 完全绕过 Agent 的 AI 移动系统
            Vector3 newPos = _agent.transform.position + _chargeDirection * moveDelta;

            // 碰撞检测：前方是否有障碍物
            RaycastHit hit;
            float checkDist = moveDelta + 0.3f;
            int v = LayerMask.NameToLayer("Voxels");
            int h = LayerMask.NameToLayer("Houses");
            int ab = LayerMask.NameToLayer("ArrowBlocker");
            int terrainLayer = (1 << (v >= 0 ? v : 0)) | (1 << (h >= 0 ? h : 0)) | (1 << (ab >= 0 ? ab : 0));
            if (Physics.Raycast(_agent.transform.position, _chargeDirection, out hit, checkDist,
                terrainLayer, QueryTriggerInteraction.Ignore))
            {
                _agent.transform.position = hit.point - _chargeDirection * 0.2f;
                EndCharge();
                _state = ChargeState.Cooldown;
                _stateTimer = ChargeCooldown;
                LogStatus($"Charge BLOCKED by {hit.collider.name}. Cooldown {ChargeCooldown}s.");
                return;
            }

            _agent.transform.position = newPos;

            // ============ 冲刺伤害检测 ============
            if (Time.time - _lastHitTime >= HitInterval)
            {
                int vikingsLayer = LayerMask.NameToLayer("Vikings");
                int agentsLayer = LayerMask.NameToLayer("Agents");
                int mask = 0;
                if (vikingsLayer >= 0) mask |= (1 << vikingsLayer);
                if (agentsLayer >= 0) mask |= (1 << agentsLayer);
                // 回退：如果未找到特定层，使用 DefaultRaycastLayers
                if (mask == 0) mask = Physics.DefaultRaycastLayers;

                Collider[] hits = Physics.OverlapSphere(_agent.transform.position, HitRadius, mask);
                foreach (var col in hits)
                {
                    if (ReferenceEquals(col, null)) continue;
                    var otherAgent = col.GetComponentInParent<Agent>();
                    if (ReferenceEquals(otherAgent, null)) continue;
                    if (ReferenceEquals(otherAgent, _agent)) continue;
                    if (otherAgent.isViking) continue; // 不要伤害同阵营 Viking
                    if (_hitAgents.Contains(otherAgent)) continue; // 已命中过
                    if (!ReferenceEquals(otherAgent.aliveState, null) && !otherAgent.aliveState.active) continue;

                    // 标记已命中
                    _hitAgents.Add(otherAgent);
                    _lastHitTime = Time.time;

                    // 施加伤害
                    ApplyChargeDamage(otherAgent);
                }
            }

            // 让朝向跟随冲刺方向
            if (_chargeDirection.sqrMagnitude > 0.001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(_chargeDirection, Vector3.up);
                _agent.transform.rotation = Quaternion.Slerp(
                    _agent.transform.rotation, targetRot, Time.deltaTime * 10f);
            }

            // 冲刺期间：冻结AI移动
            _agent.walkDir = Vector3.zero;
            _agent.maxSpeed = 0f;
        }

        private void UpdateCooldown()
        {
            _stateTimer -= Time.deltaTime;

            if (_stateTimer > ChargeCooldown - RecoveryTime)
            {
                _agent.walkDir = Vector3.zero;
                _agent.maxSpeed = 0f;
            }
            else
            {
                _agent.maxSpeed = _originalMaxSpeed;
            }

            if (_stateTimer <= 0f)
            {
                _state = ChargeState.Watching;
                _stateTimer = 0f;
                _chargeDistanceTraveled = 0f;
                _hitAgents.Clear();
                LogStatus("Cooldown ended. Watching for next target...");
            }
        }

        // ==============================
        // 冲刺伤害
        // ==============================

        /// <summary>
        /// 对命中的玩家 Agent 施加伤害、击退和眩晕。
        /// 模仿我方长矛兵冲刺技能的效果。
        /// </summary>
        private void ApplyChargeDamage(Agent target)
        {
            if (ReferenceEquals(target, null)) return;

            // 伤害：使用当前 Agent 的伤害值（已被 Plugin 乘以 1.6）
            float damage = 2.0f;
            var swordsman = _agent.brain as Swordsman;
            if (!ReferenceEquals(swordsman, null) && swordsman.damageLevels != null && swordsman.damageLevels.Length > 0)
            {
                damage = swordsman.damageLevels[0];
            }

            // 击退力
            float knockback = 4.0f;
            if (!ReferenceEquals(swordsman, null) && swordsman.knockbackLevels != null && swordsman.knockbackLevels.Length > 0)
            {
                knockback = swordsman.knockbackLevels[0];
            }

            // 眩晕
            float stunDuration = 0.5f;

            // 计算击退方向 = 冲刺方向
            Vector3 knockbackDir = _chargeDirection.normalized;
            knockbackDir.y = 0;

            // 直接修改目标生命值
            try
            {
                // 通过反射调用 Damage 相关方法，或直接扣血
                float newHealth = target.health - damage;
                target.health = Mathf.Max(0f, newHealth);

                // 施加击退：设置 walkDir 瞬时推离
                target.transform.position += knockbackDir * 0.3f;

                // 施加眩晕：找到 Stun 组件并触发
                var stun = target.GetComponent<Stun>();
                if (!ReferenceEquals(stun, null))
                {
                    // 尝试调用 Stun 的触发方法
                    var stunMethod = typeof(Stun).GetMethod("Begin",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (!ReferenceEquals(stunMethod, null))
                    {
                        stunMethod.Invoke(stun, new object[] { stunDuration });
                    }
                    else
                    {
                        // 回退：直接设 stunMultiplier 让目标更容易被眩晕
                        var smField = typeof(Stun).GetField("stunMultiplier",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(smField, null))
                        {
                            smField.SetValue(stun, 10f); // 极高倍数 → 下一击必晕
                        }
                    }
                }

                LogStatus($"HIT {target.name}! Dmg={damage:F1} HP={target.health:F1}/{target.maxHealth:F1}");
            }
            catch (Exception ex)
            {
                Plugin.SharedLogger?.LogError($"[BlackSpearman] [Charge] Damage failed: {ex.Message}");
            }
        }

        // ==============================
        // 冲刺控制
        // ==============================

        private void StartCharge()
        {
            _state = ChargeState.Charging;
            _stateTimer = ChargeDistance / ChargeSpeed;
            _chargeStartPos = _agent.transform.position;
            _chargeDistanceTraveled = 0f;
            _hitAgents.Clear();

            // 保存原始移动状态
            _originalMaxSpeed = _agent.maxSpeed;
            _originalWalkDir = _agent.walkDir;

            // 免疫眩晕
            SetStunImmunity(true);

            LogStatus($"CHARGE! Dir={_chargeDirection.ToString("F1")}, " +
                      $"Speed={ChargeSpeed}m/s, MaxDist={ChargeDistance}m, " +
                      $"Duration={_stateTimer:F2}s");
        }

        private void EndCharge()
        {
            // 恢复AI控制
            _agent.maxSpeed = _originalMaxSpeed;

            // 解除眩晕免疫
            SetStunImmunity(false);

            // ✅ P1修复：同步 navPos，防止 AI 寻路认为 Agent 还在原位置
            SyncNavPos();

            LogStatus($"Charge ended. Pos delta={Vector3.Distance(_agent.transform.position, _chargeStartPos):F1}m");
        }

        // ==============================
        // NavPos 同步（P1 修复 #5）
        // ==============================

        private static void CacheNavPos()
        {
            if (_navPosCached) return;
            _navPosCached = true;

            _navPosProperty = typeof(Agent).GetProperty("navPos",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (ReferenceEquals(_navPosProperty, null))
            {
                // 尝试字段
                var fi = typeof(Agent).GetField("navPos",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!ReferenceEquals(fi, null))
                {
                    Plugin.SharedLogger?.LogInfo("[BlackSpearman] navPos: found as field.");
                }
            }
            else
            {
                Plugin.SharedLogger?.LogInfo("[BlackSpearman] navPos: found as property.");
            }
        }

        private void SyncNavPos()
        {
            try
            {
                if (!ReferenceEquals(_navPosProperty, null))
                {
                    var currentNav = _navPosProperty.GetValue(_agent, null);
                    if (!ReferenceEquals(currentNav, null))
                    {
                        // NavPos 是值类型，尝试设置其 position
                        var posField = currentNav.GetType().GetField("position",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (!ReferenceEquals(posField, null))
                        {
                            posField.SetValue(currentNav, _agent.transform.position);
                        }
                        // 写回
                        _navPosProperty.SetValue(_agent, currentNav, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.SharedLogger?.LogWarning($"[BlackSpearman] NavPos sync failed: {ex.Message}");
            }
        }

        // ==============================
        // 敌人检测
        // ==============================

        /// <summary>
        /// 检测附近是否有敌方单位（玩家的士兵）。
        /// 遍历所有 Agent，找到最近的玩家方 Agent，设置冲刺方向。
        /// </summary>
        private bool HasNearbyEnemy(out Vector3 direction)
        {
            direction = Vector3.zero;

            var allAgents = UnityEngine.Object.FindObjectsOfType<Agent>();
            Agent closestEnemy = null;
            float closestDist = DetectionRadius;

            foreach (var other in allAgents)
            {
                if (ReferenceEquals(other, null)) continue;
                if (ReferenceEquals(other, _agent)) continue;
                if (other.isViking) continue; // 跳过维京人，只找玩家方的
                if (!ReferenceEquals(other.aliveState, null) && !other.aliveState.active) continue;

                float dist = Vector3.Distance(_agent.transform.position, other.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestEnemy = other;
                }
            }

            if (closestEnemy == null)
                return false;

            direction = (closestEnemy.transform.position - _agent.transform.position).normalized;
            return true;
        }

        // ==============================
        // 眩晕免疫
        // ==============================

        private void SetStunImmunity(bool immune)
        {
            if (ReferenceEquals(_stunComponent, null)) return;

            switch (_stunStrategy)
            {
                case StunImmunityStrategy.StunMultiplier:
                    if (!ReferenceEquals(_stunMultiplierField, null))
                        _stunMultiplierField.SetValue(_stunComponent, immune ? 0f : _originalStunMultiplier);
                    break;

                case StunImmunityStrategy.StunEnabled:
                    if (!ReferenceEquals(_stunEnabledProp, null))
                        _stunEnabledProp.SetValue(_stunComponent, !immune, null);
                    else if (!ReferenceEquals(_stunEnabledField, null))
                        _stunEnabledField.SetValue(_stunComponent, !immune);
                    break;
            }
        }

        private static void CacheStunStrategy()
        {
            if (_stunFieldAttempted) return;
            _stunFieldAttempted = true;

            var stunType = typeof(Stun);

            _stunMultiplierField = stunType.GetField("stunMultiplier",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(_stunMultiplierField, null))
            {
                _stunStrategy = StunImmunityStrategy.StunMultiplier;
                Plugin.SharedLogger?.LogInfo("[BlackSpearman] Stun immunity: using Stun.stunMultiplier (float) strategy.");
                return;
            }

            foreach (var field in stunType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType.Equals(typeof(float)) &&
                    field.Name.IndexOf("Multiplier", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _stunMultiplierField = field;
                    _stunStrategy = StunImmunityStrategy.StunMultiplier;
                    Plugin.SharedLogger?.LogInfo(
                        $"[BlackSpearman] Stun immunity: using Stun.{field.Name} (float).");
                    return;
                }
            }

            _stunEnabledProp = stunType.GetProperty("enabled",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(_stunEnabledProp, null))
            {
                _stunStrategy = StunImmunityStrategy.StunEnabled;
                Plugin.SharedLogger?.LogInfo("[BlackSpearman] Stun immunity: using Stun.enabled (bool property).");
                return;
            }

            _stunEnabledField = stunType.GetField("enabled",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!ReferenceEquals(_stunEnabledField, null))
            {
                _stunStrategy = StunImmunityStrategy.StunEnabled;
                Plugin.SharedLogger?.LogInfo("[BlackSpearman] Stun immunity: using Stun.enabled (bool field).");
                return;
            }

            _stunStrategy = StunImmunityStrategy.None;
            Plugin.SharedLogger?.LogWarning(
                "[BlackSpearman] Stun immunity: NO viable strategy found.");
        }

        // ==============================
        // 工具
        // ==============================

        private void LogStatus(string msg)
        {
            if (Time.time - _lastLogTime < 2f) return;
            _lastLogTime = Time.time;
            Plugin.SharedLogger?.LogInfo($"[BlackSpearman] [Charge] {msg}");
        }
    }
}