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
        const float DetectionRadius = 6.0f;   // 探测范围（略长，方便先手冲锋）
        const float ChargeSpeed = 4.0f;
        const float CooldownTime = 1.5f;
        const float WindUpDuration = 0.5f;
        const float RetreatDistance = 1.2f;   // 冲刺后回退小段距离（不回到原位，便于抬枪迎击）
        const float SpearLength = 0.6f;   // 原版 Spear.spearLength；冲锋距离 = 到目标锁定格 + 矛长
        const float StabDamage = 3.0f;
        const float StabKnockback = 5.0f;
        const float StabStun = 10f;

        enum Phase { Idle, WindUp, Charging, Retreat, Cooldown }

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
        Transform _spearTransform;
        bool _hasSpearTarget;
        Quaternion _spearTargetRot;
        Vector3 _chargeStartPos;
        float _posLogTimer;
        float _actScanTimer;
        Agent _targetAgent;            // 冲锋目标（冲刺结束后转身后退迎击）
        float _chargeDistance;         // 锁定冲锋距离（到目标被定位时位置 + 矛长，不追踪）
        float _chargeDuration;         // 对应时长 = 距离 / 速度
        Vector3 _chargeTargetPos;      // ★ 目标被定位时的锁定单元格（navPos.wPos），冲刺全程不追踪
        Vector3 _retreatEndPos;        // 后退终点（冲刺结束位置沿反向回退 RetreatDistance）
        int _hitCount;                 // 本回合命中数

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
                case Phase.Retreat: DoRetreat(); break;
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
            { if (log) Log("MaybeAct 拦截: 6m 内无敌人"); return false; }
            _targetAgent = nearest;   // ★ 记住目标（冲刺完成后转身后退迎击）
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
            _agent.maxSpeed = ChargeSpeed;   // 冲锋时提速，让 Body 的移动/动画尽量跟上 navPos 推进（保留跑步动画）
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
                _chargeStartPos = _agent.navPos.wPos;   // 用 navPos 精确位置（transform 可能因动画滞后）
                _hitCount = 0;
                // ★ 锁定目标：向目标"被定位时"的单元格（navPos.wPos）冲刺，冲刺全程不追踪。
                //    我方单位横向位移躲闪 → 冲到锁定格落空 → 技能前半段同样算用掉 → 后退迎击。
                if (_targetAgent != null && _targetAgent.aliveState != null && _targetAgent.aliveState.active)
                    _chargeTargetPos = _targetAgent.navPos.wPos;
                else
                    _chargeTargetPos = _chargeStartPos + _chargeDirection * 3f;   // 目标已消失则冲一段
                _chargeDistance = Mathf.Max(0.5f, Vector3.Distance(_chargeStartPos, _chargeTargetPos) + SpearLength);
                _chargeDuration = _chargeDistance / ChargeSpeed;
                _phaseTimer = _chargeDuration;
                _posLogTimer = 0f;
                BSLog.Info("[Charge] 冲锋起点 pos=" + _chargeStartPos.ToString("F2") +
                    " 锁定目标格=" + _chargeTargetPos.ToString("F2") + " dir=" + _chargeDirection.ToString("F2") +
                    " onMain=" + _agent.navPos.onMain +
                    " 距离=" + _chargeDistance.ToString("F2") + "m 时长=" + _chargeDuration.ToString("F2") + "s");
            }
        }

        void DoCharging()
        {
            _phaseTimer -= Time.deltaTime;

            _agent.LookInDirection(_chargeDirection, 720f, 20f);

            // ★ 每帧让长矛对准目标（矛尖朝敌人 chest）
            PointSpearAtTarget();

            // ★ 位移推进：固定锁定距离（不追踪），速度恒为 ChargeSpeed
            float dur = Mathf.Max(0.0001f, _chargeDistance / ChargeSpeed);
            float elapsed = Mathf.Max(0f, _chargeDuration - _phaseTimer);
            float t = Mathf.Clamp01(elapsed / dur);   // 0→1
            Vector3 target = _chargeStartPos + _chargeDirection * (_chargeDistance * t);
            NavPos np = _agent.navPos;
            if (np.valid)
            {
                Vector3 local = np.transform.InverseTransformPoint(target);
                if (!np.MoveTo(local))
                    np = new NavPos(np.navigationMesh, target, true, 1f);   // 原版同款回退
                _agent.navPos = np;
                // ⚠️ 不做 transform 硬同步：让 Body.stepping 正常驱动（保留跑步动画，避免"滑行平移"）。
                //    navPos 推进比 Body 动画快，transform 会有轻微滞后——但命中判定基于 navPos 的矛尖，
                //    不受滞后影响；观感是"跑步冲刺"而非"快速平移"。
            }

            // 每 0.3s 记录位置 + 长矛旋转 + Body 状态 + 动画参数 + navPos 滞后量
            _posLogTimer -= Time.deltaTime;
            if (_posLogTimer <= 0f)
            {
                _posLogTimer = 0.3f;
                string sr = _spearTransform != null ? _spearTransform.rotation.eulerAngles.ToString("F1") : "null";
                string lr = _spearTransform != null ? _spearTransform.localRotation.eulerAngles.ToString("F1") : "null";
                string lp = _spearTransform != null ? _spearTransform.localPosition.ToString("F3") : "null";
                string bodyState = "?";
                try
                {
                    if (_agent.body != null)
                    {
                        if (_agent.body.standing.active) bodyState = "stand";
                        else if (_agent.body.stepping.active) bodyState = "step";
                        else if (_agent.body.sliding.active) bodyState = "slide";
                        else if (_agent.body.hopping.active) bodyState = "hop";
                    }
                }
                catch { }
                string animSpeed = "?";
                try { if (_agent.animator != null) animSpeed = _agent.animator.GetFloat("Speed").ToString("F2"); } catch { }
                float lag = Vector3.Distance(_agent.navPos.wPos, _agent.transform.position);
                BSLog.Info("[Charge] 冲刺中 pos=" + _agent.transform.position.ToString("F2") +
                    " navPos=" + _agent.navPos.pos.ToString("F2") +
                    " 余距=" + _chargeDistance.ToString("F2") +
                    " body=" + bodyState + " moveAnim=" + _agent.moveAnimate +
                    " animSpeed=" + animSpeed + " lag=" + lag.ToString("F2") +
                    " spearWorldRot=" + sr + " spearLocalRot=" + lr + " spearLocalPos=" + lp);
            }

            // 冲锋途中对"矛尖周围"敌人造成伤害；命中即停 → 后退迎击（技能算用掉）
            if (DealChargeDamage())
            {
                StartRetreat();
                return;
            }

            // 冲到锁定格仍未命中（我方单位横移躲闪）→ 前半段算用掉 → 后退迎击
            if (_phaseTimer <= 0f) { StartRetreat(); return; }
        }

        /// <summary>冲刺结束（命中或落空）：以冲刺速度回退小段距离，稳住阵脚，举矛迎击。</summary>
        void StartRetreat()
        {
            _phase = Phase.Retreat;
            // ★ 不回到发起位置，只沿冲锋方向反向回退一小段（RetreatDistance）——便于抬枪迎击。
            //    命中时拉开刺枪后的间距，落空时也不会退太远，冷却后快速再战。
            _retreatEndPos = _agent.navPos.wPos - _chargeDirection * RetreatDistance;
            BSLog.Info("[Charge] 开始后退 距离=" + RetreatDistance + "m 命中=" + _hitCount);
        }

        void DoRetreat()
        {
            Vector3 to = _retreatEndPos - _agent.navPos.wPos;   // 指向后退终点
            float dist = to.magnitude;
            if (dist < 0.15f) { EndCharge(); return; }          // 已回退到位，抬枪迎击

            // 以冲刺速度（ChargeSpeed）后退，贴着 navmesh 移动
            Vector3 step = to.normalized * (ChargeSpeed * Time.deltaTime);
            if (step.magnitude > dist) step = to;
            NavPos np = _agent.navPos;
            if (np.valid)
            {
                Vector3 tgt = _agent.navPos.wPos + step;
                Vector3 local = np.transform.InverseTransformPoint(tgt);
                if (!np.MoveTo(local)) np = new NavPos(np.navigationMesh, tgt, true, 1f);
                _agent.navPos = np;
            }

            // 稳住阵脚：面朝敌人 + 矛保持迎击（朝敌）
            if (_targetAgent != null && _targetAgent.aliveState != null && _targetAgent.aliveState.active)
                _agent.LookInDirection((_targetAgent.transform.position - _agent.transform.position).normalized, 720f, 20f);
            PointSpearAtTarget();
        }

        void RaiseSpear()
        {
            if (_spearTransform == null) return;
            // 原版 Spear.LateUpdate 的举矛公式：LookRotation(矛尖方向, 角色right) * Euler(0,0,90)
            _spearTargetRot = Quaternion.LookRotation(_chargeDirection, _agent.transform.right) * Quaternion.Euler(0f, 0f, 90f);
            _hasSpearTarget = true;
            BSLog.Info("[Charge] 举矛 targetRot(euler)=" + _spearTargetRot.eulerAngles.ToString("F1") + " dir=" + _chargeDirection.ToString("F2"));
        }

        /// <summary>每帧让长矛对准目标（矛尖指向目标 chest）；目标消失则保持冲锋方向。</summary>
        void PointSpearAtTarget()
        {
            if (_spearTransform == null) return;
            Vector3 targetPos;
            if (_targetAgent != null && _targetAgent.aliveState != null && _targetAgent.aliveState.active)
                targetPos = _targetAgent.chestPos;
            else
                targetPos = _agent.navPos.wPos + _chargeDirection * SpearLength;
            Vector3 dir = (targetPos - _spearTransform.position).normalized;
            if (dir.sqrMagnitude < 0.0001f) return;
            _spearTargetRot = Quaternion.LookRotation(dir, _agent.transform.right) * Quaternion.Euler(0f, 0f, 90f);
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

        /// <summary>
        /// 对"矛尖（navPos 精确位置 + 冲锋方向×矛长）周围 0.3m"的敌人造成伤害；
        /// 返回是否命中（命中即停，矛刺到位不再转用剑补刀）。
        /// 用 navPos.wPos 而非 transform.position：transform 有跑步动画滞后，navPos 才是精确位置。
        /// </summary>
        bool DealChargeDamage()
        {
            Vector3 basePos = _agent.navPos.wPos;
            Vector3 tip = basePos + _chargeDirection * SpearLength;   // 矛尖
            int hn = Physics.OverlapSphereNonAlloc(tip, 0.3f, _hitBuffer, ~0, QueryTriggerInteraction.Ignore);
            bool hitAny = false;
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
                    Vector3 d = (a.transform.position - basePos).normalized;
                    d.y = 0f;
                    if (d.sqrMagnitude < 0.001f) d = _agent.transform.forward;
                    a.DealDamage(new Attack(s, d, a.transform.position, this, _squad, "Sfx/English/Spear"));
                    _hitCount++;
                    hitAny = true;
                    Log("HIT " + a.name + " dmg=" + StabDamage + " kb=" + StabKnockback + " stun=" + StabStun + " hp=" + a.health.ToString("F1"));
                }
                catch (Exception ex) { BSLog.Error("[Charge] " + ex); }
            }
            return hitAny;
        }

        void EndCharge()
        {
            float displacement = Vector3.Distance(_chargeStartPos, _agent.navPos.wPos);
            BSLog.Info("[Charge] 冲刺+后退结束 位移=" + displacement.ToString("F2") + " 命中=" + _hitCount +
                " 终点=" + _agent.navPos.wPos.ToString("F2"));
            _phase = Phase.Cooldown;
            _phaseTimer = CooldownTime;
            if (_chargeState != null) _chargeState.SetActive(false);
            _agent.maxSpeed = _originalSpeed;   // 恢复冲锋前的速度
            _agent.movability = 1f;
            _agent.enemyMovability = 1f;
            _agent.walkDir = Vector3.zero;
            // ★ 举枪迎击：不 LowerSpear——矛保持朝敌（迎击姿态），技能进入冷却。
            PointSpearAtTarget();
        }

        void UpdateCooldown()
        {
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f) { _phase = Phase.Idle; _agent.maxSpeed = _originalSpeed; }
        }

        void OnDestroy()
        {
            if (_chargeState != null) _chargeState.SetActive(false);
            if (_phase == Phase.Charging || _phase == Phase.Retreat || _phase == Phase.WindUp)
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
