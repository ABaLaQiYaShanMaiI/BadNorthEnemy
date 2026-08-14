using System;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵冲刺技能（模仿玩家方 Pike Charge）。
    /// 状态机：Idle → WindUp → Charging → Cooldown → Idle。
    /// 触发方式：IBrainAction(Brain.MaybeAct) + Update 每 0.25s 独立检测（不依赖 Swordsman Idle 状态）。
    /// </summary>
    public class SpearChargeComponent : MonoBehaviour, IBrainAction
    {
        const float DetectionRadius = 5.0f;
        const float ChargeSpeed = 4.0f;
        const float CooldownTime = 1.5f;
        const float WindUpDuration = 0.5f;
        const float ChargingMaxTime = 0.5f;
        const float StabDamage = 3.0f;
        const float StabKnockback = 5.0f;
        const float StabStun = 10f;

        enum Phase { Idle, WindUp, Charging, Cooldown }

        Phase _phase = Phase.Idle;
        Agent _agent;
        Squad _squad;
        bool _setupDone;
        float _phaseTimer;
        Vector3 _chargeDirection;
        float _originalSpeed;
        readonly Collider[] _hitBuffer = new Collider[16];
        float _lastLogTime = -999f;
        AgentState _chargeState;
        float _hitTimer;
        Transform _spearTransform;
        bool _hasSpearTarget;
        Quaternion _spearTargetRot;
        Vector3 _chargeStartPos;
        float _posLogTimer;
        float _actScanTimer;

        public void Setup(Agent agent)
        {
            if (_setupDone) return;
            _setupDone = true;
            _agent = agent;
            if (_agent == null) { Destroy(this); return; }
            _squad = _agent.squad;
            _originalSpeed = _agent.maxSpeed;
            // ★ 关键：把冲刺做成 exclusives 下的独占状态，激活时锁住 Swordsman 大脑，
            //    避免大脑每帧覆盖 walkDir 导致的"瞬移回原位"。
            _chargeState = new AgentState("BlackSpearmanCharge", _agent.exclusives, false, true);
            // 找到挂载的长矛（由 BlackSpearmanWeapon 挂载，命名 Spear_BlackSpearman）
            _spearTransform = _agent.transform.Find("Spear_BlackSpearman");
            Log("Setup OK. speed=" + _originalSpeed.ToString("F1") + " spear=" + (_spearTransform != null));
        }

        bool IBrainAction.MaybeAct(Brain brain)
        {
            // Brain.MaybeAct() 只在 Swordsman.IdleUpdate() 的 hz8 节拍被调用（Swordsman.cs:243）。
            // 敌人一旦进入 4m 就转 ready/hunting，MaybeAct 不再被调度 → 冲锋会永久错过触发窗口。
            // 因此真正的触发交给 Update() 的独立检测（TryTriggerCharge），这里静默参与、避免日志刷屏。
            return TryTriggerCharge(false);
        }

        void Update()
        {
            if (_agent == null) { Destroy(this); return; }

            // ★ 独立触发检测（不依赖 Swordsman 状态机）：每 0.25s 自己扫描一次，
            //    Idle 状态下满足条件就启动冲锋（TryTriggerCharge 内部 _phase 守卫保证不重复）。
            _actScanTimer -= Time.deltaTime;
            if (_phase == Phase.Idle && _actScanTimer <= 0f)
            {
                _actScanTimer = 0.25f;
                TryTriggerCharge(false);
            }

            switch (_phase)
            {
                case Phase.WindUp: DoWindUp(); break;
                case Phase.Charging: DoCharging(); break;
                case Phase.Cooldown: UpdateCooldown(); break;
            }
        }

        bool TryTriggerCharge(bool log)
        {
            if (_phase != Phase.Idle) return false;
            if (_agent == null || _agent.aliveState == null || !_agent.aliveState.active || !_agent.dangerous)
            { if (log) Log("MaybeAct 拦截: alive/dangerous=false"); return false; }
            if (_agent.aliveAndGrounded != null && !_agent.aliveAndGrounded.active)
            { if (log) Log("MaybeAct 拦截: aliveAndGrounded=false"); return false; }
            // 还必须已登上主岛导航网格（navPos.onMain）。敌舰上也有有效的 navPos，aliveAndGrounded 在船上同样激活，
            // 不加这一条会在敌舰上就触发冲锋（上一版日志中 navPos 与世界坐标对不上、且紧贴生成点冲锋即为船上触发）。
            if (!_agent.navPos.valid || !_agent.navPos.onMain)
            { if (log) Log("MaybeAct 拦截: navPos 无效或未登主岛"); return false; }
            Agent nearest = null;
            if (!FindNearestEnemy(out _chargeDirection, out nearest))
            { if (log) Log("MaybeAct 拦截: 5m 内无敌人"); return false; }
            StartWindUp();
            return true;
        }

        void StartWindUp()
        {
            _phase = Phase.WindUp;
            _phaseTimer = WindUpDuration;
            if (_chargeState != null) _chargeState.SetActive(true);
            // 原版 PikeChargeComponent.travelling 的取值：movability=0.2, enemyMovability=0.1。
            // ⚠️ 不设 maxSpeed=0：FixedUpdateAgent 末尾 speed=maxSpeed（Agent.cs:941），
            //    maxSpeed=0 会让 Body 的踏步动画追不上 navPos → 视觉"橡皮筋延迟"。
            _agent.movability = 0.2f;
            _agent.enemyMovability = 0.1f;
            _agent.walkDir = Vector3.zero;
            RaiseSpear();
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
                _chargeStartPos = _agent.transform.position;
                _posLogTimer = 0f;
                BSLog.Info("[Charge] 冲锋起点 pos=" + _chargeStartPos.ToString("F2") + " dir=" + _chargeDirection.ToString("F2") + " onMain=" + _agent.navPos.onMain);
            }
        }

        void DoCharging()
        {
            _phaseTimer -= Time.deltaTime;

            _agent.LookInDirection(_chargeDirection, 720f, 20f);

            // ★ 位移推进（原版 PikeChargeAbility.charging 的做法）：
            //   从固定起点沿方向插值，整体赋值 agent.navPos（NavPos 是结构体，必须整体赋，
            //   改字段无效）。ChargeSpeed=4 × ChargingMaxTime=0.5 → 理论位移 2m。
            float t = 1f - Mathf.Clamp01(_phaseTimer / ChargingMaxTime);   // 0→1
            Vector3 target = _chargeStartPos + _chargeDirection * (ChargeSpeed * ChargingMaxTime * t);
            NavPos np = _agent.navPos;
            if (np.valid)
            {
                Vector3 local = np.transform.InverseTransformPoint(target);
                if (!np.MoveTo(local))
                    np = new NavPos(np.navigationMesh, target, true, 1f);   // 原版同款回退
                _agent.navPos = np;
                // ★ 硬同步 transform 到 navPos：Body 的踏步动画是"追赶式"（每帧 Lerp），
                //    冲锋推进快时 transform 会滞后于 navPos（实测 navPos 推进 0.7m、transform 只动 0.13m）。
                //    渲染时强制 transform 贴 navPos.wPos，视觉即时跟随、无橡皮筋。
                _agent.transform.position = _agent.navPos.wPos;
            }

            // 每 0.3s 记录位置 + 长矛实际旋转 + navPos 同步（暴露"瞬移回位"的奥秘）
            _posLogTimer -= Time.deltaTime;
            if (_posLogTimer <= 0f)
            {
                _posLogTimer = 0.3f;
                string sr = _spearTransform != null ? _spearTransform.rotation.eulerAngles.ToString("F1") : "null";
                string lr = _spearTransform != null ? _spearTransform.localRotation.eulerAngles.ToString("F1") : "null";
                string lp = _spearTransform != null ? _spearTransform.localPosition.ToString("F3") : "null";
                BSLog.Info("[Charge] 冲刺中 pos=" + _agent.transform.position.ToString("F2") +
                    " navPos=" + _agent.navPos.pos.ToString("F2") +
                    " spearWorldRot=" + sr + " spearLocalRot=" + lr + " spearLocalPos=" + lp);
            }

            // 冲锋途中每 0.15s 对半径 0.4m 内敌人造成伤害+击退
            _hitTimer -= Time.deltaTime;
            if (_hitTimer <= 0f)
            {
                _hitTimer = 0.15f;
                DealChargeDamage();
            }

            if (_phaseTimer <= 0f) { EndCharge(); return; }
        }

        void RaiseSpear()
        {
            if (_spearTransform == null) return;
            // 原版 Spear.LateUpdate 的举矛公式：LookRotation(矛尖方向, 角色right) * Euler(0,0,90)
            _spearTargetRot = Quaternion.LookRotation(_chargeDirection, _agent.transform.right) * Quaternion.Euler(0f, 0f, 90f);
            _hasSpearTarget = true;
            BSLog.Info("[Charge] 举矛 targetRot(euler)=" + _spearTargetRot.eulerAngles.ToString("F1") + " dir=" + _chargeDirection.ToString("F2"));
        }

        void LowerSpear()
        {
            if (_spearTransform == null) return;
            // 放矛：矛尖朝上（原版 spearDown 时 idealSpearTipDir = Vector3.up）
            _spearTargetRot = Quaternion.LookRotation(Vector3.up, _agent.transform.right) * Quaternion.Euler(0f, 0f, 90f);
            _hasSpearTarget = true;
        }

        void LateUpdate()
        {
            // 举矛/放矛旋转插值
            if (_hasSpearTarget && _spearTransform != null)
                _spearTransform.rotation = Quaternion.Slerp(_spearTransform.rotation, _spearTargetRot, Time.deltaTime * 12f);

            // 冲锋位移：Bad North 的 Agent 每帧都把 transform 从 navPos 同步（FixedUpdateAgent 里 wPos = navPos.wPos，
            // Body 的踏步/滑动动画据此驱动 transform），所以直接写 transform.position、或对结构体副本写 navPos.pos 都无效。
            // 照搬原版 PikeChargeComponent.charge.OnUpdate 的做法：把 agent.navPos 整体赋值为
            // “沿冲锋方向推进的新 NavPos”，让 Agent 自身的导航/踏步系统去跟随。
            if (_phase == Phase.Charging && _agent != null)
            {
                try
                {
                    Vector3 newPos = _agent.transform.position + _chargeDirection * ChargeSpeed * Time.deltaTime;
                    NavPos np = _agent.navPos;
                    if (np.valid)
                    {
                        // MoveTo 期望 navmesh 本地坐标；失败（如撞到悬崖边界）则回退为世界坐标重建 NavPos。
                        Vector3 localTarget = np.transform.InverseTransformPoint(newPos);
                        if (!np.MoveTo(localTarget))
                            np = new NavPos(np.navigationMesh, newPos, true, 1f);
                        _agent.navPos = np;
                    }
                }
                catch { }
            }
        }

        void DealChargeDamage()
        {
            int hn = Physics.OverlapSphereNonAlloc(_agent.transform.position, 0.4f, _hitBuffer, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hn; i++)
            {
                var c = _hitBuffer[i];
                if (c == null) continue;
                var a = c.GetComponentInParent<Agent>();
                if (a == null || a == _agent || a.isViking) continue;
                if (a.aliveState == null || !a.aliveState.active) continue;
                try
                {
                    var s = new AttackSettings { damage = StabDamage, knockback = StabKnockback, launchImpulse = 0f, stun = StabStun };
                    Vector3 d = (a.transform.position - _agent.transform.position).normalized;
                    d.y = 0f;
                    if (d.sqrMagnitude < 0.001f) d = _agent.transform.forward;
                    a.DealDamage(new Attack(s, d, a.transform.position, this, _squad, "Sfx/English/Spear"));
                    Log("HIT " + a.name + " dmg=" + StabDamage + " kb=" + StabKnockback + " stun=" + StabStun + " hp=" + a.health.ToString("F1"));
                }
                catch (Exception ex) { BSLog.Error("[Charge] " + ex); }
            }
        }

        void EndCharge()
        {
            float displacement = Vector3.Distance(_chargeStartPos, _agent.transform.position);
            BSLog.Info("[Charge] 冲锋结束 位移=" + displacement.ToString("F2") + " 终点=" + _agent.transform.position.ToString("F2"));
            _phase = Phase.Cooldown;
            _phaseTimer = CooldownTime;
            if (_chargeState != null) _chargeState.SetActive(false);
            // 不冻结 maxSpeed：让黑矛兵冷却期间仍可正常移动（原版冷却时可行动）
            _agent.movability = 1f;
            _agent.enemyMovability = 1f;
            _agent.walkDir = Vector3.zero;
            LowerSpear();
        }

        void UpdateCooldown()
        {
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f) { _phase = Phase.Idle; _agent.maxSpeed = _originalSpeed; }
        }

        void OnDestroy()
        {
            if (_chargeState != null) _chargeState.SetActive(false);
            if (_phase == Phase.Charging || _phase == Phase.WindUp)
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
                BSLog.Info("[Charge] " + msg);
            }
        }
    }
}
