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
            // 使用 NavMesh 或简单 raycast 检测
            RaycastHit hit;
            float checkDist = moveDelta + 0.3f; // 稍微提前检测
            // 只检测地形/建筑物，避免被队友或尸体阻挡
            // Bad North 地形层: Voxels + Houses（文档 08 §4.1）：arrowLow = Voxels + Houses + ArrowBlocker
            int v = LayerMask.NameToLayer("Voxels");
            int h = LayerMask.NameToLayer("Houses");
            int ab = LayerMask.NameToLayer("ArrowBlocker");
            int terrainLayer = (1 << (v >= 0 ? v : 0)) | (1 << (h >= 0 ? h : 0)) | (1 << (ab >= 0 ? ab : 0));
            if (Physics.Raycast(_agent.transform.position, _chargeDirection, out hit, checkDist,
                terrainLayer, QueryTriggerInteraction.Ignore))
            {
                // 撞到东西，提前结束冲刺
                _agent.transform.position = hit.point - _chargeDirection * 0.2f;
                EndCharge();
                _state = ChargeState.Cooldown;
                _stateTimer = ChargeCooldown;
                LogStatus($"Charge BLOCKED by {hit.collider.name}. Cooldown {ChargeCooldown}s.");
                return;
            }

            _agent.transform.position = newPos;

            // 让朝向跟随冲刺方向（旋转脸朝敌人）
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

            // 恢复期内让 Agent 短暂不能动（硬直效果）
            if (_stateTimer > ChargeCooldown - RecoveryTime)
            {
                _agent.walkDir = Vector3.zero;
                _agent.maxSpeed = 0f;
            }
            else
            {
                // 恢复正常移动
                _agent.maxSpeed = _originalMaxSpeed;
            }

            if (_stateTimer <= 0f)
            {
                _state = ChargeState.Watching;
                _stateTimer = 0f;
                _chargeDistanceTraveled = 0f;
                LogStatus("Cooldown ended. Watching for next target...");
            }
        }

        // ==============================
        // 冲刺控制
        // ==============================

        private void StartCharge()
        {
            _state = ChargeState.Charging;
            _stateTimer = ChargeDistance / ChargeSpeed; // 根据距离和速度计算持续时间
            _chargeStartPos = _agent.transform.position;
            _chargeDistanceTraveled = 0f;

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

            LogStatus($"Charge ended. Pos delta={Vector3.Distance(_agent.transform.position, _chargeStartPos):F1}m");
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

            var allAgents = Object.FindObjectsOfType<Agent>();
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