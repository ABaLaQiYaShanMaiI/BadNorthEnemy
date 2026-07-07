using System.Reflection;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman
{
    /// <summary>
    /// 黑色矛兵冲刺组件。
    /// 
    /// 状态机：
    ///   Idle → Watching（等待 Agent 生成完毕）
    ///   Watching → Charging（检测到登岛，开始冲刺）
    ///   Charging → Cooldown（冲刺 1.5 秒后进入冷却）
    ///   Cooldown → Watching（冷却 8 秒后可再次冲刺）
    /// 
    /// 冲刺行为：
    ///   - 锁定冲刺方向（敌我连线方向）
    ///   - 高速移动（3.5x 正常速度）
    ///   - 免疫眩晕
    ///   - 持续 1.5 秒
    /// </summary>
    public class SpearChargeComponent : MonoBehaviour
    {
        private enum ChargeState
        {
            Idle,
            Watching,
            Charging,
            Cooldown
        }

        private Agent _agent;
        private ChargeState _state = ChargeState.Idle;
        private float _stateTimer;
        private float _originalMaxSpeed;
        private Vector3 _chargeDirection;
        private bool _setupDone;

        // 反射缓存（Mono 2.0 兼容）
        private static FieldInfo _stunMultiplierField;
        private static bool _stunFieldAttempted;
        private float _originalStunMultiplier = 1f;
        private Stun _stunComponent;

        // 上次打印日志的时间（防止刷屏）
        private float _lastLogTime = -999f;

        /// <summary>
        /// 静态工厂方法：为目标 Agent 添加 SpearChargeComponent。
        /// </summary>
        public static SpearChargeComponent AddTo(Agent agent)
        {
            if (ReferenceEquals(agent, null))
                return null;

            SpearChargeComponent existing = agent.GetComponent<SpearChargeComponent>();
            if (!ReferenceEquals(existing, null))
                return existing;

            return agent.gameObject.AddComponent<SpearChargeComponent>();
        }

        /// <summary>
        /// 初始化组件，绑定 Agent 引用。
        /// </summary>
        public void Setup(Agent agent)
        {
            if (_setupDone)
                return;

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

            // 获取原始 stunMultiplier
            CacheStunField();
            if (!ReferenceEquals(_stunMultiplierField, null) && !ReferenceEquals(_stunComponent, null))
            {
                object val = _stunMultiplierField.GetValue(_stunComponent);
                if (val is float f)
                    _originalStunMultiplier = f;
            }

            _state = ChargeState.Idle;
            LogStatus("Setup complete. Waiting for spawn...");
        }

        private void Update()
        {
            if (!_setupDone || ReferenceEquals(_agent, null))
                return;

            // 检查 Agent 是否存活
            if (!ReferenceEquals(_agent.aliveState, null) && !_agent.aliveState.active)
            {
                // Agent 已死亡，清除组件
                if (_state == ChargeState.Charging)
                    EndCharge();
                Destroy(this);
                return;
            }

            switch (_state)
            {
                case ChargeState.Idle:
                    UpdateIdle();
                    break;
                case ChargeState.Watching:
                    UpdateWatching();
                    break;
                case ChargeState.Charging:
                    UpdateCharging();
                    break;
                case ChargeState.Cooldown:
                    UpdateCooldown();
                    break;
            }
        }

        private void OnDestroy()
        {
            // 确保在销毁时恢复属性
            if (_state == ChargeState.Charging)
            {
                EndCharge();
            }
        }

        /// <summary>
        /// Idle 状态：等待 Agent 完全生成（spawned 状态激活）。
        /// </summary>
        private void UpdateIdle()
        {
            // 检查 spawned 状态是否已激活
            if (!ReferenceEquals(_agent.spawned, null) && _agent.spawned.active)
            {
                _state = ChargeState.Watching;
                _stateTimer = 0f;
                LogStatus("Agent spawned. Watching for landing...");
            }
        }

        /// <summary>
        /// Watching 状态：检测 Agent 是否已经下船登岛。
        /// Agent 在船上时 navPos.island 为 false，登岛后变为 true。
        /// </summary>
        private void UpdateWatching()
        {
            _stateTimer += Time.deltaTime;

            // 最多等待 30 秒，超时则放弃（防止永久卡在 watching）
            if (_stateTimer > 30f)
            {
                LogStatus("Watching timeout (30s). Giving up.");
                Destroy(this);
                return;
            }

            // 检测是否已登岛
            if (_agent.navPos.island)
            {
                // 额外等待 0.3 秒，确保 AI 已初始化并获取到 enemyDir
                if (_stateTimer < 0.3f)
                    return;

                // 确定冲刺方向
                if (_agent.enemyDir.sqrMagnitude > 0.01f)
                {
                    _chargeDirection = _agent.enemyDir.normalized;
                }
                else
                {
                    // 没有敌人方向，朝前方冲刺
                    _chargeDirection = _agent.transform.forward.normalized;
                }

                StartCharge();
            }
        }

        /// <summary>
        /// Charging 状态：高速冲刺中，每帧覆盖移动方向和速度。
        /// </summary>
        private void UpdateCharging()
        {
            _stateTimer -= Time.deltaTime;

            if (_stateTimer <= 0f)
            {
                // 冲刺结束
                EndCharge();
                _state = ChargeState.Cooldown;
                _stateTimer = Plugin.ChargeCooldown;
                LogStatus($"Charge complete. Cooldown {Plugin.ChargeCooldown}s.");
                return;
            }

            // 每帧设置移动方向和速度（覆盖 AI 指令）
            _agent.walkDir = _chargeDirection;
            _agent.maxSpeed = _originalMaxSpeed * Plugin.ChargeSpeedMultiplier;
        }

        /// <summary>
        /// Cooldown 状态：冲刺冷却中。冷却结束后回到 Watching 状态。
        /// </summary>
        private void UpdateCooldown()
        {
            _stateTimer -= Time.deltaTime;

            if (_stateTimer <= 0f)
            {
                _state = ChargeState.Watching;
                _stateTimer = 0f;
                LogStatus("Cooldown ended. Watching for next charge opportunity...");
            }
        }

        /// <summary>
        /// 开始冲刺：
        /// - 记录原始 maxSpeed
        /// - 设置冲刺状态和计时器
        /// - 免疫眩晕
        /// </summary>
        private void StartCharge()
        {
            _state = ChargeState.Charging;
            _stateTimer = Plugin.ChargeDuration;
            _originalMaxSpeed = _agent.maxSpeed;

            // 免疫眩晕
            SetStunImmunity(true);

            LogStatus($"CHARGE! Direction: {_chargeDirection}, Speed: x{Plugin.ChargeSpeedMultiplier}");
        }

        /// <summary>
        /// 结束冲刺：
        /// - 恢复 maxSpeed
        /// - 恢复眩晕倍率
        /// </summary>
        private void EndCharge()
        {
            _agent.maxSpeed = _originalMaxSpeed;
            SetStunImmunity(false);
        }

        /// <summary>
        /// 设置/取消眩晕免疫。
        /// 通过反射修改 Stun.stunMultiplier。
        /// </summary>
        private void SetStunImmunity(bool immune)
        {
            CacheStunField();

            if (ReferenceEquals(_stunMultiplierField, null) || ReferenceEquals(_stunComponent, null))
                return;

            if (immune)
            {
                // 设为极小值 ≈ 免疫眩晕
                _stunMultiplierField.SetValue(_stunComponent, 0f);
            }
            else
            {
                // 恢复原始值
                _stunMultiplierField.SetValue(_stunComponent, _originalStunMultiplier);
            }
        }

        /// <summary>
        /// 缓存 Stun.stunMultiplier 的 FieldInfo（一次性反射）。
        /// Mono 2.0 兼容：使用 ReferenceEquals 判空。
        /// </summary>
        private static void CacheStunField()
        {
            if (_stunFieldAttempted)
                return;

            _stunFieldAttempted = true;
            _stunMultiplierField = typeof(Stun).GetField("stunMultiplier",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (ReferenceEquals(_stunMultiplierField, null))
                Plugin.SharedLogger?.LogWarning("[BlackSpearman] Stun.stunMultiplier field not found (shown once).");
        }

        /// <summary>
        /// 状态日志（防止刷屏：相同消息最多每 2 秒打印一次）。
        /// </summary>
        private void LogStatus(string msg)
        {
            if (Time.time - _lastLogTime < 2f)
                return;

            _lastLogTime = Time.time;
            Plugin.SharedLogger?.LogInfo($"[BlackSpearman] [Charge] {msg}");
        }
    }
}