using System;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵冲刺技能（模仿玩家方 Pike Charge）。
    /// 状态机：Idle → WindUp → Charging → Stab → Cooldown → Idle。
    /// 作为 IBrainAction 注册到 Brain.actions，由 Brain.MaybeAct() 调度。
    /// </summary>
    public class SpearChargeComponent : MonoBehaviour, IBrainAction
    {
        const float DetectionRadius = 5.0f;
        const float ReadyDist = 3.5f;
        const float StabDist = 1.5f;
        const float ChargeSpeed = 4.0f;
        const float CooldownTime = 1.5f;
        const float WindUpDuration = 0.5f;
        const float ChargingMaxTime = 0.5f;
        const float StabDamage = 3.0f;
        const float StabKnockback = 5.0f;
        const float StabStun = 10f;

        enum Phase { Idle, WindUp, Charging, Stab, Cooldown }

        Phase _phase = Phase.Idle;
        Agent _agent;
        Squad _squad;
        bool _setupDone;
        float _phaseTimer;
        Vector3 _chargeDirection;
        float _originalSpeed;
        Agent _targetAgent;
        readonly Collider[] _hitBuffer = new Collider[16];
        float _lastLogTime = -999f;
        AgentState _chargeState;
        float _hitTimer;
        Transform _spearTransform;
        Quaternion _spearRestRotation;
        bool _hasSpearTarget;
        Quaternion _spearTargetRot;
        Vector3 _chargeStartPos;
        float _posLogTimer;

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
            if (_spearTransform != null) _spearRestRotation = _spearTransform.localRotation;
            Log("Setup OK. speed=" + _originalSpeed.ToString("F1") + " spear=" + (_spearTransform != null));
        }

        bool IBrainAction.MaybeAct(Brain brain)
        {
            if (_phase != Phase.Idle) return false;
            if (_agent == null || _agent.aliveState == null || !_agent.aliveState.active || !_agent.dangerous) return false;
            if (_agent.aliveAndGrounded != null && !_agent.aliveAndGrounded.active) return false; // 未下船/未落地不触发
            if (!FindNearestEnemy(out _chargeDirection, out _targetAgent)) return false;
            StartWindUp();
            return true;
        }

        void Update()
        {
            if (_agent == null) { Destroy(this); return; }
            switch (_phase)
            {
                case Phase.WindUp: DoWindUp(); break;
                case Phase.Charging: DoCharging(); break;
                case Phase.Stab: DoStab(); break;
                case Phase.Cooldown: UpdateCooldown(); break;
            }
        }

        void StartWindUp()
        {
            _phase = Phase.WindUp;
            _phaseTimer = WindUpDuration;
            if (_chargeState != null) _chargeState.SetActive(true);
            _agent.movability = 0f;
            _agent.maxSpeed = 0f;
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
                BSLog.Info("[Charge] 冲锋起点 pos=" + _chargeStartPos.ToString("F2") + " dir=" + _chargeDirection.ToString("F2"));
            }
        }

        void DoCharging()
        {
            _phaseTimer -= Time.deltaTime;

            _agent.LookInDirection(_chargeDirection, 720f, 20f);

            // 每 0.3s 记录位置 + 长矛实际旋转（全自动，无需 F9）
            _posLogTimer -= Time.deltaTime;
            if (_posLogTimer <= 0f)
            {
                _posLogTimer = 0.3f;
                string sr = _spearTransform != null ? _spearTransform.rotation.eulerAngles.ToString("F1") : "null";
                BSLog.Info("[Charge] 冲刺中 pos=" + _agent.transform.position.ToString("F2") + " spearRot=" + sr);
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

            // 冲锋位移：在 LateUpdate 做（所有 Update 之后），避免被 Agent.Update 同步回导航位置覆盖
            if (_phase == Phase.Charging && _agent != null)
            {
                Vector3 newPos = _agent.transform.position + _chargeDirection * ChargeSpeed * Time.deltaTime;
                _agent.transform.position = newPos;
                try { _agent.navPos.pos = newPos; } catch { }
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
                    Log("HIT " + a.name + " dmg=" + StabDamage);
                }
                catch (Exception ex) { BSLog.Error("[Charge] " + ex); }
            }
        }

        void DoStab()
        {
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f) { PerformStab(); EndCharge(); }
        }

        void PerformStab()
        {
            var t = _targetAgent;
            if (t == null) t = _agent.enemyAgent;
            if (t == null) return;
            try
            {
                var s = new AttackSettings { damage = StabDamage, knockback = StabKnockback, launchImpulse = 0f, stun = StabStun };
                Vector3 d = (t.transform.position - _agent.transform.position).normalized;
                d.y = 0f;
                if (d.sqrMagnitude < 0.001f) d = _agent.transform.forward;
                t.DealDamage(new Attack(s, d, t.transform.position, this, _squad, "Sfx/English/Spear"));
                Log("HIT " + t.name + " dmg=" + StabDamage);
            }
            catch (Exception ex) { BSLog.Error("[Charge] " + ex); }
        }

        void EndCharge()
        {
            float displacement = Vector3.Distance(_chargeStartPos, _agent.transform.position);
            BSLog.Info("[Charge] 冲锋结束 位移=" + displacement.ToString("F2") + " 终点=" + _agent.transform.position.ToString("F2"));
            _phase = Phase.Cooldown;
            _phaseTimer = CooldownTime;
            if (_chargeState != null) _chargeState.SetActive(false);
            _agent.maxSpeed = 0f;
            _agent.walkDir = Vector3.zero;
            _agent.movability = 1f;
            _targetAgent = null;
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
            if (_phase == Phase.Charging || _phase == Phase.Stab || _phase == Phase.WindUp)
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
