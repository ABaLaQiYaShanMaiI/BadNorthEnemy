using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵冲刺技能（模仿玩家方 Pike Charge）。
    /// 状态机：Idle → WindUp → Charging → Retreat(小段后退迎击) → Cooldown → Idle。
    /// 触发方式：IBrainAction(Brain.MaybeAct) + Update 每 0.25s 独立检测（不依赖 Swordsman Idle 状态）。
    /// 借鉴原版 JumpAttack（Twohanded 跳斩）：位移冲击（冲锋/后退）期间免疫攻击（attack.ignore）。
    /// </summary>
    public class SpearChargeComponent : MonoBehaviour, IBrainAction, IAttackResponder
    {
        const float DetectionRadius = 6.0f;   // 探测范围
        const float ChargeSpeed = 5.0f;       // 冲刺速度 5m/s（更快更冲，冲击感）
        const float ChargeOvershoot = 1.5f;   // ★ 穿透余量：冲过锁定格 1.5m，穿透敌阵、冲击阵营
        const float CooldownTime = 1.5f;      // 冷却
        const float WindUpDuration = 0.5f;    // 起手
        const float RetreatDistance = 0.6f;   // ★ 后退 0.6m（原 1.2 太远，且易退到船）
        const float SpearLength = 0.6f;       // 原版 Spear.spearLength
        const float StabDamage = 3.0f;        // 冲锋刺击伤害
        const float StabKnockback = 9.0f;     // 击退（撞飞/撞下海）
        const float StabStun = 10f;           // 眩晕
        const float StabLaunch = 8f;          // 命中撞飞（ragdoll 弹起，背海敌人撞下海）
        const float HitRadius = 0.4f;         // ★ 命中判定半径 0.4m（原 0.3，提高命中率）

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
        float _diagTimer;              // 触发拦截诊断节流（每 3s 打一次）
        float _hitDiagTimer;           // 命中诊断节流（每 0.5s 打一次）

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
            // 借鉴原版 JumpAttack：位移冲击（冲锋/后退）期间免疫攻击（attack.ignore=true），
            // 技能一旦起手不会被伤害打断——只有击退/地形能影响。
            if (_agent.attackResponders != null && !_agent.attackResponders.Contains(this))
                _agent.attackResponders.Add(this);
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
                // 每 3s 用 log=true 打一次"拦截原因"诊断（定位冲锋为何不触发）
                _diagTimer -= 0.25f;
                TryTriggerCharge(_diagTimer <= 0f);
                if (_diagTimer <= 0f) _diagTimer = 3f;
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
            {
                if (log) Log("触发拦截: alive=" + (_agent != null && _agent.aliveState != null ? _agent.aliveState.active.ToString() : "null") +
                    " dangerous=" + (_agent != null ? _agent.dangerous.ToString() : "null") +
                    " onMain=" + (_agent != null && _agent.navPos.valid ? _agent.navPos.onMain.ToString() : "null"));
                return false;
            }
            if (_agent.aliveAndGrounded != null && !_agent.aliveAndGrounded.active)
            { if (log) Log("触发拦截: aliveAndGrounded=false"); return false; }
            // 还必须已登上主岛导航网格（navPos.onMain）。敌舰上也有有效的 navPos，aliveAndGrounded 在船上同样激活，
            // 不加这一条会在敌舰上就触发冲锋（上一版日志中 navPos 与世界坐标对不上、且紧贴生成点冲锋即为船上触发）。
            if (!_agent.navPos.valid || !_agent.navPos.onMain)
            { if (log) Log("触发拦截: navPos 无效或未登主岛"); return false; }
            Agent nearest = null;
            if (!FindNearestEnemy(out _chargeDirection, out nearest, log))
            { if (log) Log("触发拦截: 6m 内无可冲锋目标（详见 FindNearestEnemy 诊断）"); return false; }
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
                // ★ 冲锋距离 = 到锁定格 + 矛长 + 穿透余量(1.5m)：穿透敌阵、冲击阵营，命中也冲完整段
                _chargeDistance = Mathf.Max(0.5f, Vector3.Distance(_chargeStartPos, _chargeTargetPos) + SpearLength + ChargeOvershoot);
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
                float lag = _agent.navPos.valid ? Vector3.Distance(_agent.navPos.wPos, _agent.transform.position) : -1f;
                if (_agent.navPos.valid)
                {
                    BSLog.Info("[Charge] 冲刺中 pos=" + _agent.transform.position.ToString("F2") +
                        " navPos=" + _agent.navPos.pos.ToString("F2") +
                        " 余距=" + _chargeDistance.ToString("F2") +
                        " body=" + bodyState + " moveAnim=" + _agent.moveAnimate +
                        " animSpeed=" + animSpeed + " lag=" + lag.ToString("F2") +
                        " spearWorldRot=" + sr + " spearLocalRot=" + lr + " spearLocalPos=" + lp);
                }
            }

            // ★ 穿透式冲锋：命中不打断——矛尖沿途对碰到的敌人持续结算伤害，冲完整段（穿过敌阵）再后退。
            //   命中带撞飞（launchImpulse）：敌人沿冲锋方向被弹起/撞下海。
            DealChargeDamage();

            // 冲到锁定格+余量（或时间到）→ 后退迎击（技能算用掉）
            if (_phaseTimer <= 0f) { StartRetreat(); return; }
        }

        /// <summary>冲刺结束（命中或落空）：以冲刺速度回退小段距离，稳住阵脚，举矛迎击。</summary>
        void StartRetreat()
        {
            _phase = Phase.Retreat;
            // ★ navPos 失效保护：敌人被击飞/冲锋撞崖可能使 navPos 变空（wPos 访问会崩），直接收尾进冷却。
            if (!_agent.navPos.valid) { EndCharge(); return; }
            // ★ 防"退到船/海"：不在主岛 navmesh 上就不后退（否则会退到船建模上，玩家打不到）
            if (!_agent.navPos.onMain) { EndCharge(); return; }
            // ★ 不回到发起位置，只沿冲锋方向反向回退一小段（RetreatDistance）——便于抬枪迎击。
            _retreatEndPos = _agent.navPos.wPos - _chargeDirection * RetreatDistance;
            BSLog.Info("[Charge] 开始后退 距离=" + RetreatDistance + "m 命中=" + _hitCount);
        }

        void DoRetreat()
        {
            // ★ navPos 失效保护（同上）：直接收尾进冷却，避免 NullReferenceException
            if (!_agent.navPos.valid) { EndCharge(); return; }
            // ★ 防退到船/海：后退过程中一旦脱离主岛 navmesh 立即收尾
            if (!_agent.navPos.onMain) { EndCharge(); return; }

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
                if (!np.MoveTo(local))
                {
                    // ★ 后退点无效/超出主岛（如朝海/船的方向）→ 原地收尾，不硬退，防止站上船建模
                    EndCharge();
                    return;
                }
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
            Vector3 basePos = _agent.navPos.valid ? _agent.navPos.wPos : _agent.transform.position;   // navPos 失效兜底
            Vector3 tip = basePos + _chargeDirection * SpearLength;   // 矛尖
            int hn = Physics.OverlapSphereNonAlloc(tip, HitRadius, _hitBuffer, ~0, QueryTriggerInteraction.Ignore);
            bool hitAny = false;

            // ★ 命中诊断（每 0.5s）：矛尖位置 + 半径内候选 + 过滤原因
            if (hn > 0 && _hitDiagTimer <= 0f)
            {
                _hitDiagTimer = 0.5f;
                BSLog.Info("[Charge] 命中诊断: 矛尖=" + tip.ToString("F2") +
                    " 黑矛兵navPos=" + _agent.navPos.pos.ToString("F2") + " transform=" + _agent.transform.position.ToString("F2") +
                    " 方向=" + _chargeDirection.ToString("F2") + " 半径=" + HitRadius + " collider=" + hn);
                for (int i = 0; i < hn; i++)
                {
                    var c = _hitBuffer[i];
                    if (c == null) continue;
                    var a = c.GetComponentInParent<Agent>();
                    string reason = "→ 命中!";
                    if (a == null) reason = "无Agent组件(跳过)";
                    else if (a == _agent) reason = "自身(跳过)";
                    else if (a.isViking) reason = "是维京友方(跳过)";
                    else if (a.aliveState == null || !a.aliveState.active) reason = "不存活(跳过)";
                    BSLog.Info("[Charge]   候选 " + c.name + " dist=" + Vector3.Distance(tip, c.transform.position).ToString("F2") + "m " + reason);
                }
            }
            else if (hn == 0 && _hitDiagTimer <= 0f)
            {
                _hitDiagTimer = 0.5f;
                BSLog.Info("[Charge] 命中诊断: 矛尖=" + tip.ToString("F2") + " 半径=" + HitRadius + "m 内无任何collider" +
                    " 黑矛兵navPos=" + _agent.navPos.pos.ToString("F2"));
            }

            for (int i = 0; i < hn; i++)
            {
                var c = _hitBuffer[i];
                if (c == null) continue;
                var a = c.GetComponentInParent<Agent>();
                if (a == null || a == _agent || a.isViking) continue;
                if (a.aliveState == null || !a.aliveState.active) continue;
                try
                {
                    // ★ launchImpulse>0 → Agent.DealDamage 会触发 ragdoll 弹起（Agent.cs:586）：
                    //   沿冲锋方向把敌人撞飞，背靠海/崖的敌人会被撞下海（撞出 navmesh 边界）。
                    var s = new AttackSettings { damage = StabDamage, knockback = StabKnockback, launchImpulse = StabLaunch, stun = StabStun };
                    Vector3 d = _chargeDirection;   // 沿锁定冲锋方向撞飞（比"朝敌人当前位置"更稳定）
                    d.y = 0f;
                    if (d.sqrMagnitude < 0.001f) d = _agent.transform.forward;
                    a.DealDamage(new Attack(s, d, a.transform.position, this, _squad, "Sfx/English/Spear"));
                    _hitCount++;
                    hitAny = true;
                    Log("HIT " + a.name + " dmg=" + StabDamage + " kb=" + StabKnockback + " launch=" + StabLaunch + " stun=" + StabStun + " hp=" + a.health.ToString("F1"));
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

        /// <summary>借鉴原版 JumpAttack：冲锋/后退（位移冲击）期间免疫攻击。</summary>
        void IAttackResponder.ModifyAttack(ref Attack attack)
        {
            if (_phase == Phase.Charging || _phase == Phase.Retreat)
                attack.ignore = true;
        }

        void OnDestroy()
        {
            if (_chargeState != null) _chargeState.SetActive(false);
            if (_agent != null && _agent.attackResponders != null)
                _agent.attackResponders.Remove(this);   // 移除自己，避免残留
            if (_phase == Phase.Charging || _phase == Phase.Retreat || _phase == Phase.WindUp)
            {
                _agent.maxSpeed = _originalSpeed;
                _agent.movability = 1f;
            }
        }

        bool FindNearestEnemy(out Vector3 dir, out Agent target, bool log)
        {
            dir = Vector3.zero;
            target = null;

            // ★ 协同防重（借鉴原版 JumpAttack 的同伴检查）：
            //   先收集其他黑矛兵正在"准备/冲锋"阶段锁定的目标，本黑矛兵自动绕开 → 分散攻击，不挤一起。
            var locked = new HashSet<Agent>();
            var allCharges = Resources.FindObjectsOfTypeAll<SpearChargeComponent>();
            for (int i = 0; i < allCharges.Length; i++)
            {
                var o = allCharges[i];
                if (o == null || o == this) continue;
                if ((o._phase == Phase.WindUp || o._phase == Phase.Charging) && o._targetAgent != null)
                    locked.Add(o._targetAgent);
            }

            int n = Physics.OverlapSphereNonAlloc(_agent.transform.position, DetectionRadius, _hitBuffer, ~0, QueryTriggerInteraction.Ignore);
            float best = DetectionRadius;
            for (int i = 0; i < n; i++)
            {
                var c = _hitBuffer[i];
                if (c == null) continue;
                var a = c.GetComponentInParent<Agent>();
                if (a == null || a == _agent || a.isViking) continue;
                if (a.aliveState != null && !a.aliveState.active) continue;
                if (locked.Contains(a)) continue;   // 已被其他黑矛兵锁定 → 换目标
                float dist = Vector3.Distance(_agent.transform.position, a.transform.position);
                if (dist < best) { best = dist; target = a; }
            }
            if (target == null)
            {
                if (log) BSLog.Info("[Charge] FindNearestEnemy: 探测collider=" + n + " 其他黑矛兵=" + (allCharges.Length - 1) +
                    " 已锁定目标=" + locked.Count + " → 无可用目标");
                return false;
            }
            if (log) BSLog.Info("[Charge] FindNearestEnemy: 锁定目标=" + target.name + " 距离=" + best.ToString("F2") + "m");
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
