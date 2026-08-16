using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵冲刺技能（借鉴原版 Twohanded 触发 + Pike Charge 表现）：
    /// 状态机 Idle → WindUp → Charging → Retreat → Cooldown；优先取 Swordsman 大脑目标，
    /// 矛中点周围命中 + 能量衰减 + 抵达爆发；位移冲击期间 attack.ignore 免疫。
    /// </summary>
    public class SpearChargeComponent : MonoBehaviour, IBrainAction, IAttackResponder
    {
        const float DetectionRadius = 6.0f;   // 扫描兜底探测范围（优先取 Swordsman 大脑目标）
        const float ChargeSpeed = 5.0f;       // 冲刺速度 5m/s（更快更冲，冲击感）
        const float ChargeOvershoot = 1.5f;   // ★ 穿透余量：冲过锁定格 1.5m，穿透敌阵、冲击阵营
        const float CooldownTime = 10f;       // ★ 冷却（2026-08-15 用户指定：延长到 10s，冲锋更稀有、更像"技能"）
        const float WindUpDuration = 0.5f;    // 起手
        const float RetreatDistance = 0.6f;   // ★ 后退 0.6m（原 1.2 太远，且易退到船）
        const float SpearLength = 0.6f;       // 原版 Spear.spearLength
        const float StabDamage = 3.0f;        // 冲锋刺击基础伤害（随 squad 等级放大）
        const float StabKnockback = 9.0f;     // 击退（撞飞/撞下海）
        const float StabStun = 10f;           // 眩晕
        const float StabLaunch = 8f;          // 命中撞飞（ragdoll 弹起，背海敌人撞下海）
        const float HitRadius = 0.5f;         // ★ 命中判定半径 = 单矛线宽 0.5m（用户指定：减少范围波及；原版单兵 ≈0.46m）
        const float ArrivalBurstRadius = 1.2f; // 抵达终点爆发半径（终点的范围波及，与沿途线宽独立，可单独调）
        const float EnergyDecayPerHit = 0.8f;  // 每命中一次能量衰减（原版 Pike Charge 同款：扫过一排递减）
        const float LevelDamageScale = 0.25f;  // 每级伤害增幅：dmg = StabDamage × (1 + 等级×系数)
        const float ThrustDistance = 0.45f;   // 近战刺击：长矛沿自身前向突刺的距离（视觉"刺"而非"挥砍"）
        const float ThrustRiseTime = 0.06f;   // 刺出速度（快）
        const float ThrustHoldTime = 0.12f;   // 刺出到位后短暂保持（命中窗口），再收回
        const float ThrustFallTime = 0.28f;   // 收回速度（慢）

        enum Phase { Idle, WindUp, Charging, Retreat, Cooldown }

        Phase _phase = Phase.Idle;
        Agent _agent;
        Squad _squad;
        bool _setupDone;
        static int _spriteDiagCount;         // 去剑诊断已打印次数（限前 3 只，避免刷屏）
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
        float _energy = 1f;            // 冲锋能量（每命中 ×EnergyDecayPerHit，扫过一排递减）
        readonly HashSet<Agent> _hitAgents = new HashSet<Agent>();   // 本回合已命中（去重，同目标只结算一次）
        Swordsman _swordsman;          // 近战刺击：读取 Swordsman.attack 状态
        Vector3 _spearBaseLocalPos;    // 长矛挂载基点（突刺偏移在此之上叠加）
        Transform _handAnchor;         // ★ 第十五轮：持剑手锚点（长矛根每帧跟随，消除"持矛手脱离身躯"）
        Vector3 _handMountOffset;      // 矛根相对手的固定本地偏移（挂载时确定）
        Vector3 _thrustOffsetLocal;    // ★ 刺击位移（本地空间：攻击开始瞬间按"对准后的身体朝向"锁定一次，整段不再重算）
        float _thrust;                 // 当前突刺量 0~1
        bool _prevAttackActive;        // 近战诊断：上一帧是否在攻击
        bool _thrustHitDone;           // 本回合矛刺到位后是否已触发伤害（FirstHit）
        float _meleeDiagTimer;         // 近战诊断节流（突刺中每 0.15s 打一次）
        float _idleDiagTimer;          // 待机/移动帧诊断节流（每 2s 打一次）
        float _thrustStartTime;        // ★ 本回合刺击开始时间（节奏曲线用）
        Vector3 _thrustDirWorld;       // ★ 刺击开始时锁定的世界方向（整段刺击不再重算 → 消除鬼畜）
        bool _thrustDirLocked;         // ★ 方向是否已锁定（目标存活才锁；目标消失退回 agent.forward）
        bool _thrustRotLocked;         // ★ 刺击期间矛旋转是否已锁（不再每帧 Slerp 追目标）

        // 长矛穿刺节奏（与 Plugin 的攻击 Patch 协同）：刺出(0.06s)→保持(0.12s)→收回(0.28s)，
        // 总时长 MeleeAttackDuration，攻击结束由 SwordsmanAttackUpdatePrefix 判定。
        static readonly Dictionary<Agent, float> _meleeAttackStart = new Dictionary<Agent, float>();
        const float MeleeAttackDuration = 0.5f;   // 每次刺击总时长（量级 ≈ 原版挥剑 0.6s）

        public static void NotifyMeleeAttackStart(Agent a)
        {
            if (a == null) return;
            _meleeAttackStart[a] = Time.time;
        }

        public static void NotifyMeleeAttackEnd(Agent a)
        {
            if (a == null) return;
            _meleeAttackStart.Remove(a);
        }

        public static bool MeleeAttackDone(Agent a)
        {
            float t;
            return a != null && _meleeAttackStart.TryGetValue(a, out t) && Time.time - t >= MeleeAttackDuration;
        }

        public void Setup(Agent agent)
        {
            if (_setupDone) return;
            _setupDone = true;
            _agent = agent;
            if (_agent == null) { Destroy(this); return; }
            _squad = _agent.squad;
            _originalSpeed = _agent.maxSpeed;
            // 冲刺做成 exclusives 下的独占状态（激活时锁住大脑，避免 walkDir 被覆盖）；
            // 位移冲击期间 attack.ignore 免疫伤害打断。
            _chargeState = new AgentState("BlackSpearmanCharge", _agent.exclusives, false, true);
            if (_agent.attackResponders != null && !_agent.attackResponders.Contains(this))
                _agent.attackResponders.Add(this);
            // 找到挂载的长矛（由 BlackSpearmanWeapon 挂载，命名 Spear_BlackSpearman）
            _spearTransform = _agent.transform.Find("Spear_BlackSpearman");
            _swordsman = GetComponent<Swordsman>();
            if (_spearTransform != null) _spearBaseLocalPos = _spearTransform.localPosition;
            // ★ 第十五轮：长矛跟随持剑手——记录"矛根相对手"的偏移；手随身体动画移动时矛根每帧同步跟随，
            //   消除"持矛手脱离身躯"（旧版矛根固定于挂载瞬间，跑步/刺击时手与矛分离、攻击范围观感偏大）。
            _handAnchor = BlackSpearmanWeapon.FindSwordAnchor(_agent.transform);
            if (_handAnchor != null)
                _handMountOffset = _spearBaseLocalPos - _agent.transform.InverseTransformPoint(_handAnchor.position);
            // ★ 去剑诊断：对前 3 只黑矛兵自动 dump 完整层级 + 所有 sprite/sprite2 详情，
            //   用于确认"剑"到底来自独立子对象 / 动画帧 / sprite2 部件贴图。
            if (_spriteDiagCount < 3)
            {
                _spriteDiagCount++;
                DiagnosticsComponent.DumpAgentSprites(agent);
            }
            Log("Setup OK. speed=" + _originalSpeed.ToString("F1") + " spear=" + (_spearTransform != null));
        }

        /// <summary>刺击期间的身体锁定朝向（供 Plugin.SwordsmanAttackUpdatePrefix 使用：
        /// 让身体朝固定突刺方向站桩，不再每帧追活动目标导致朝向阶跃 → 矛"小的抽动"）。</summary>
        public bool IsThrustDirectionLocked
        {
            get { return _thrustDirLocked; }
        }

        /// <summary>攻击开始瞬间锁定的世界突刺方向（与 IsThrustDirectionLocked 配套使用）。</summary>
        public Vector3 LockedThrustDirection
        {
            get { return _thrustDirWorld; }
        }

        /// <summary>第十八轮：当前状态机阶段名（Idle/WindUp/Charging/Retreat/Cooldown，供抽动探针诊断输出）。</summary>
        public string PhaseLabel
        {
            get { return _phase.ToString(); }
        }

        bool IBrainAction.MaybeAct(Brain brain)
        {
            // Brain.MaybeAct() 只在 Swordsman.IdleUpdate() 的 hz8 节拍被调用（Swordsman.cs:243），
            // 敌人一旦进入 4m 就转 ready/hunting，MaybeAct 不再被调度 → 冲锋会永久错过触发窗口。
            // 因此触发完全交给 Update() 的独立检测（TryTriggerCharge），这里不再重复触发。
            return false;
        }

        void Update()
        {
            if (_agent == null) { Destroy(this); return; }

            // ★ 第十九轮（用户指定"黑矛兵在技能释放过程中可被击杀"）：冲锋/后退不再免疫伤害 →
            //   单位可能冲锋途中死亡 → 立即中止技能状态机（释放 exclusives、恢复速度），避免尸体继续推进 navPos。
            if (_agent.aliveState != null && !_agent.aliveState.active)
            {
                if (_phase != Phase.Idle && _phase != Phase.Cooldown) AbortCharge();
                return;
            }

            TrackSpearToHand();   // ★ 第十五轮：矛根每帧跟随持剑手（身体动画驱动），消除"持矛手脱离身躯"

            UpdateMeleeThrust();   // ★ 近战刺击表现：Swordsman 攻击时长矛前刺（视觉"刺"）

            // ★ 独立触发检测（不依赖 Swordsman 状态机）：每 0.25s 自己扫描一次，
            //    Idle 状态下满足条件就启动冲锋（TryTriggerCharge 内部 _phase 守卫保证不重复）。
            _actScanTimer -= Time.deltaTime;
            if (_phase == Phase.Idle && _actScanTimer <= 0f)
            {
                _actScanTimer = 0.25f;
                // 每 3s 用 log=true 打一次"拦截原因"诊断（定位冲锋为何不触发）
                _diagTimer -= 0.25f;
                TryTriggerCharge(_diagTimer <= 0f);
                if (_diagTimer <= 0f) _diagTimer = 12f;   // 触发拦截诊断降频（登岛前大量"未登主岛"刷屏）
            }

            switch (_phase)
            {
                case Phase.WindUp: DoWindUp(); break;
                case Phase.Charging: DoCharging(); break;
                case Phase.Retreat: DoRetreat(); break;
                case Phase.Cooldown: UpdateCooldown(); break;
            }

            // ★ 第十七轮：待机/移动时维护长矛姿态——有目标矛尖朝敌，无目标则"举矛"（矛尖朝前上方树立），
            //   恢复"长矛始终树立"的设计（船上/未判定到敌人前不再水平持矛等待）。
            UpdateSpearPose();
        }

        /// <summary>★ 第十七轮：无目标时把长矛抬到举矛姿态；有目标（冲锋目标/大脑目标存活）时矛尖指向目标。
        /// 攻击/冲锋/后退期间由 UpdateMeleeThrust / DoCharging / DoRetreat 各自驱动，这里不覆盖。</summary>
        void UpdateSpearPose()
        {
            if (_spearTransform == null || _agent == null) return;
            bool chargeBusy = _phase == Phase.WindUp || _phase == Phase.Charging || _phase == Phase.Retreat;
            bool meleeBusy = _swordsman != null && _swordsman.attack != null && _swordsman.attack.active;
            if (chargeBusy || meleeBusy) return;

            Agent t = _targetAgent;
            if (t == null || t.aliveState == null || !t.aliveState.active)
                t = GetBrainTarget();
            if (t != null && t.aliveState != null && t.aliveState.active)
            {
                // 有敌人：矛尖指向目标（朝敌迎击）
                if (SpearVisual.TryGetAimRotation(_agent, t.chestPos, out _spearTargetRot))
                    _hasSpearTarget = true;
            }
            else
            {
                // 无敌人：举矛（矛尖朝前上方树立）
                if (SpearVisual.TryGetRaisedRotation(_agent, out _spearTargetRot))
                    _hasSpearTarget = true;
                else
                    _hasSpearTarget = false;
            }
        }

        /// <summary>★ 第十五轮：长矛根部每帧跟随持剑手锚点——用"挂载时矛根相对手的偏移"叠加当前手位，
        /// 使矛根始终贴在手上（身体动画跑/刺/待机时手在动，矛根同步动），不再悬空/脱离。</summary>
        void TrackSpearToHand()
        {
            if (_spearTransform == null || _handAnchor == null || _agent == null) return;
            try
            {
                Vector3 handLocal = _agent.transform.InverseTransformPoint(_handAnchor.position);
                _spearBaseLocalPos = handLocal + _handMountOffset;
            }
            catch { }
        }

        /// <summary>
        /// 近战刺击表现：攻击开始瞬间锁定 _thrustDirWorld 与 _spearTargetRot（整段不再重算 → 直线直刺），
        /// 矛沿锁定方向刺出-保持-收回；攻击时站桩（walkDir=0），矛的前刺主导观感。
        /// </summary>
        void UpdateMeleeThrust()
        {
            if (_spearTransform == null) return;
            bool attacking = _swordsman != null && _swordsman.attack != null && _swordsman.attack.active;

            // ★ 攻击上升沿：锁定突刺方向 + 矛朝向（只锁一次，整段不再重算）
            if (attacking && !_prevAttackActive)
            {
                _thrustHitDone = false;      // 新回合刺击：重置命中标记
                _thrustStartTime = Time.time;
                _thrustDirLocked = false;
                _thrustRotLocked = false;
                var t = _swordsman.target != null ? _swordsman.target : _agent.enemyAgent;
                if (t != null && t.aliveState != null && t.aliveState.active)
                {
                    Vector3 toT = t.chestPos - _spearTransform.position;
                    toT.y = 0f;
                    if (toT.sqrMagnitude > 0.0001f)
                    {
                        _thrustDirWorld = toT.normalized;
                        _thrustDirLocked = true;
                    }
                }
                if (!_thrustDirLocked)
                {
                    // 目标不可用（死亡/丢失）→ 退回自身朝向并同样锁定
                    _thrustDirWorld = _agent.transform.forward;
                    _thrustDirLocked = true;
                }

                // ★ 刺击开始瞬间先把身体朝向 snap 到突刺方向（SetDirection 是瞬时旋转），
                //   再在"已对准"的本地系里锁定突刺位移 —— 两处同帧一致，整段刺击不再抖动。
                try { _agent.SetDirection(_thrustDirWorld); } catch { }
                _thrustOffsetLocal = _agent.transform.InverseTransformDirection(_thrustDirWorld) * ThrustDistance;

                // ★ 稳定刺击朝向（2026-08-15 修复"小的抽动"）：虚拟 right = cross(worldUp, dir) 恒 ⊥ dir、
                //   永不退化 —— 避免旧版 LookRotation(dir, agent.right) 在目标位于角色侧向时
                //   roll 翻转 180°（矛精灵上下颠倒，刺击瞬间最刺眼的一种抽动）。
                //   agent 正对目标时 cross(up, dir) ≡ agent.right → 观感与旧版零差异。
                //   ⚠️ 首版误写 cross(dir, up)（符号反了，= −agent.right）→ 刺击 roll 恒差 180°，
                //   实测日志 spearWorldRot=(0,X,180)（冲锋/原版举矛是 (0,X,0)）暴露，已改为 cross(up, dir)。
                Vector3 stableRight = Vector3.Cross(Vector3.up, _thrustDirWorld);
                if (stableRight.sqrMagnitude > 0.001f)
                {
                    _spearTargetRot = Quaternion.LookRotation(_thrustDirWorld, stableRight.normalized) * Quaternion.Euler(0f, 0f, 90f);
                    _hasSpearTarget = true;
                    _thrustRotLocked = true;
                }
                BSLog.Info("[近战] 攻击开始 target=" + (t != null ? t.name : "null") +
                    " dist=" + (t != null ? Vector3.Distance(_agent.transform.position, t.transform.position).ToString("F2") : "-") +
                    " " + DescribeBody(_agent) + " " + DescribeAnimator(_agent) +
                    " range=" + (_swordsman != null ? _swordsman.range.ToString("F2") : "-") +
                    " done=" + _agent.animationDone + " 锁方向=" + _thrustDirWorld.ToString("F2"));
            }
            else if (!attacking && _prevAttackActive)
            {
                _thrustDirLocked = false;
                _thrustRotLocked = false;
                BSLog.Info("[近战] 攻击结束 thrust=" + _thrust.ToString("F2") +
                    " spearLocalPos=" + _spearTransform.localPosition.ToString("F3"));
            }
            _prevAttackActive = attacking;

            if (attacking)
            {
                // ★ 刺出-收回节奏：快速刺出(0.06s) → 短暂保持命中(0.12s) → 收回(0.28s)。
                //   旧版"攻击中只升不降"→ 矛一直顶在最前（thrust=1.00 多帧），观感"伸着"而非"刺"。
                float el = Time.time - _thrustStartTime;
                float thrust;
                if (el < ThrustRiseTime) thrust = Mathf.Clamp01(el / ThrustRiseTime);
                else if (el < ThrustRiseTime + ThrustHoldTime) thrust = 1f;
                else thrust = Mathf.Clamp01(1f - (el - ThrustRiseTime - ThrustHoldTime) / ThrustFallTime);
                _thrust = thrust;

                // 矛刺到位（thrust 首次达 1）手动触发伤害：对齐 Spear.Hit()（TestHit 矛本地球判定、
                // 主×1 副×0.33 贯穿、附 hitEffect；原版由挥剑动画事件触发，穿刺不播动画需自触发）。
                if (_thrust >= 1f && !_thrustHitDone)
                {
                    _thrustHitDone = true;
                    if (_swordsman != null)
                    {
                        try { DoSpearHit(); }
                        catch (Exception e) { BSLog.Warn("[近战] SpearHit 异常: " + e); }
                    }
                }

                // 矛沿锁定方向直线刺出：只放大锁定位移 _thrustOffsetLocal×_thrust，零重算
                // （旧版逐帧 InverseTransformDirection → 本地偏移跳变 = "小的抽动"）。
                _spearTransform.localPosition = _spearBaseLocalPos + _thrustOffsetLocal * _thrust;
                // 攻击期间站桩：walkDir 归零让身体转 stand，矛稳定前刺
                if (_agent != null) _agent.walkDir = Vector3.zero;

                // 近战诊断：突刺过程矛的位置/旋转（每 0.15s）
                _meleeDiagTimer -= Time.deltaTime;
                if (_meleeDiagTimer <= 0f)
                {
                    _meleeDiagTimer = 0.15f;
                    BSLog.Info("[近战] 突刺中 thrust=" + _thrust.ToString("F2") +
                        " spearPos=" + _spearTransform.position.ToString("F2") +
                        " spearLocalPos=" + _spearTransform.localPosition.ToString("F3") +
                        " spearWorldRot=" + _spearTransform.rotation.eulerAngles.ToString("F1") +
                        " agentYaw=" + _agent.transform.rotation.eulerAngles.y.ToString("F1") +
                        " 锁dir=" + _thrustDirWorld.ToString("F2"));
                }
            }
            else
            {
                _thrust = Mathf.Max(0f, _thrust - Time.deltaTime / ThrustFallTime);
                // 待机/移动帧诊断：非攻击时每 2s 记录当前动画状态与帧名（确认待机剑柄来自哪个动画）
                _idleDiagTimer -= Time.deltaTime;
                if (_idleDiagTimer <= 0f)
                {
                    _idleDiagTimer = 2f;
                    try
                    {
                        string spName = "?";
                        var sa = _agent != null ? _agent.GetComponentInChildren<SpriteAnimator>(true) : null;
                        if (sa != null && sa.sprite != null) spName = sa.sprite.name;
                        BSLog.Info("[近战·待机帧] " + DescribeAnimator(_agent) + " " + DescribeBody(_agent) + " sprite=" + spName);
                    }
                    catch { }
                }
            }
            // ★ 非刺出状态回到挂载基点（避免长矛卡在刺出位）
            if (!attacking && _thrust <= 0.001f && _spearTransform.localPosition != _spearBaseLocalPos)
            {
                _spearTransform.localPosition = _spearBaseLocalPos;
            }
        }


        /// <summary>近战刺击命中（对齐 Spear.Hit()）：矛本地空间球判定，主目标 ×1、副目标 ×0.33 贯穿，附 hitEffect。</summary>
        bool DoSpearHit()
        {
            if (_swordsman == null || _agent == null || _spearTransform == null) return false;
            Agent primary = _swordsman.target != null ? _swordsman.target : _agent.enemyAgent;
            if (primary == null) return false;
            bool hitAny = false;

            // 主目标：矛尖指向才中，伤害 ×1
            if (TestHit(primary.chestPos))
            {
                DealSpearDamage(primary, 1f);
                hitAny = true;
            }
            // 副目标：我方敌兵在矛尖范围内 → ×0.33 贯穿（Spear.Hit() 中 num/=2 后 ×0.6667 再对副目标再除）
            Agent secondary = _agent.enemyAgent;
            if (secondary != null && secondary != primary &&
                secondary.aliveState != null && secondary.aliveState.active && TestHit(secondary.chestPos))
            {
                DealSpearDamage(secondary, 0.33f);
            }
            return hitAny;
        }

        /// <summary>对齐 Spear.TestHit：敌人 chest 转矛本地坐标并归一化，落单位球内即命中。</summary>
        bool TestHit(Vector3 enemyPos)
        {
            if (_spearTransform == null) return false;
            Vector3 v = _spearTransform.InverseTransformPoint(enemyPos);
            float d = SpearLength;          // 0.6m
            v /= d;
            v.z -= 0.5f;
            v.x *= 2f;
            v.y *= 2f;
            return v.sqrMagnitude < 1f;
        }

        /// <summary>对齐 Spear.GetAttack：长矛伤害（等级化）+ 命中特效（PrefabManager.hitEffect）。</summary>
        void DealSpearDamage(Agent target, float mult)
        {
            if (target == null || _swordsman == null || _agent == null) return;
            Vector3 dir = _thrustDirLocked ? _thrustDirWorld : (target.chestPos - _agent.chestPos);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) dir = _agent.transform.forward;
            var settings = new AttackSettings
            {
                damage = _swordsman.damage * mult,
                knockback = _swordsman.knockback,
                launchImpulse = 0f,
                stun = _swordsman.stun
            };
            Vector3 pos = Vector3.Lerp(target.chestPos, _spearTransform.position, 0.3f);
            Attack atk = new Attack(settings, dir.normalized, pos, _swordsman, _agent.squad,
                "Sfx/English/Spear", ScriptableObjectSingleton<PrefabManager>.instance.hitEffect);
            // ★ 第十九轮：我方剑盾兵正面格挡（伤害×0.2 + 盾击音效/火花反馈），不再"直接死亡"
            TryShieldBlockSpear(target, ref atk);
            target.DealDamage(atk);
            BSLog.Info("[近战] SpearHit " + target.name + " dmg=" + (settings.damage).ToString("F1") +
                " kb=" + settings.knockback.ToString("F1") + " stun=" + settings.stun.ToString("F1") +
                " mult=" + mult.ToString("F2") + " hitEffect=" + (atk.effect != null));
        }

        /// <summary>
        /// 第十九轮：黑矛长矛 vs 我方剑盾兵 —— 正面格挡反馈 + 免伤。
        /// 原版 Shield.ModifyAttack 对"长矛类"（monoAttacker is Spear）才 ×0.2；黑矛兵刺击的 monoAttacker 是
        /// Swordsman（近战分支非 parry 不减免伤害）、冲锋/爆发是 SpearChargeComponent（原版完全不识别）
        /// → 我方剑盾兵被黑矛兵命中时无免伤无反馈、直接死亡。这里在结算前补盾牌判定：
        /// 盾牌正面（shield.forward 朝向来袭方向）→ 伤害 ×0.2、眩晕 ×0.4（对齐原版长矛格挡），
        /// 播放盾击音效 + 火花特效；CloseCombatBrain 攻击时原版 Shield.ModifyAttack 会自己播反馈，只做减免避免双音效。
        /// </summary>
        bool TryShieldBlockSpear(Agent target, ref Attack atk)
        {
            try
            {
                if (target == null || atk.damage <= 0f) return false;
                var shieldComp = target.GetComponent<Shield>();
                if (shieldComp == null || shieldComp.shield == null) return false;
                if (!target.shield) return false;   // 盾牌未举起
                float facing = Vector3.Dot(shieldComp.shield.forward, -atk.direction.normalized);
                if (facing <= 0.5f) return false;   // 背面/侧面命中不格挡
                atk.damage *= 0.2f;
                atk.stun *= 0.4f;
                atk.soundSuffix = "Shield";
                // 近战(Swordsman)攻击时原版 Shield.ModifyAttack 会自行播放 Deflect/Block 音效+火花；
                // 冲锋/爆发（monoAttacker=本组件）原版不识别 → 由这里补足反馈，避免双音效。
                if (!(atk.monoAttacker is CloseCombatBrain))
                {
                    try { IslandGameplayManager.RequestCombatAudio("Sfx/English/SwordShield/Block", target.gameObject); } catch { }
                    if (shieldComp.shieldSmash != null) atk.effect = shieldComp.shieldSmash;
                }
                BSLog.Info("[盾牌] 黑矛长矛被格挡 target=" + target.name + " facing=" + facing.ToString("F2") +
                    " dmg→" + atk.damage.ToString("F2") + " stun→" + atk.stun.ToString("F2"));
                return true;
            }
            catch (Exception e) { BSLog.Warn("[盾牌] 格挡判定异常: " + e); return false; }
        }


        /// <summary>Animator 状态解析：控制器名 + clip 名 + 状态哈希 + 进度（旧日志只打 hash 看不出播放什么）。</summary>
        static string DescribeAnimator(Agent a)
        {
            try
            {
                if (a == null || a.animator == null) return "anim=null";
                string ctrl = a.animator.runtimeAnimatorController != null ? a.animator.runtimeAnimatorController.name : "?";
                var si = a.animator.GetCurrentAnimatorStateInfo(0);
                string clip = "?";
                try
                {
                    var clips = a.animator.GetCurrentAnimatorClipInfo(0);
                    if (clips != null && clips.Length > 0 && clips[0].clip != null) clip = clips[0].clip.name;
                }
                catch { }
                return "animCtrl=" + ctrl + " clip=" + clip + " hash=" + si.fullPathHash + " norm=" + si.normalizedTime.ToString("F2");
            }
            catch (Exception e) { return "anim=err:" + e.Message; }
        }

        /// <summary>Body 状态解析：看叶子状态 standing/stepping/sliding（hopping 是恒 active 容器，不判它）。</summary>
        static string DescribeBody(Agent a)
        {
            try
            {
                if (a == null || a.body == null) return "body=null";
                var b = a.body;
                return "body=stand:" + b.standing.active + " step:" + b.stepping.active +
                    " slide:" + b.sliding.active + " hop:" + b.hopping.active;
            }
            catch { return "body=err"; }
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

            // ★ 触发逻辑借鉴原版 Twohanded(JumpAttack)：优先取 Swordsman 大脑已锁定的目标
            //   （大脑的追击/狩猎目标），而不是独立扫描 —— 技能跟着大脑的战术走，修复"走路到阵前不冲锋"。
            //   只有大脑没有目标时才退回 6m 扫描兜底。
            Agent nearest = GetBrainTarget();
            Vector3 dir;
            if (nearest != null)
            {
                // 路径判定：朝大脑目标的导航格（Twohanded 的 landPos = target.navPos，用 navPos 精确位置而非 transform）
                dir = nearest.navPos.wPos - _agent.navPos.wPos;
                dir.y = 0f;
                // 协同防重也作用于大脑目标：若该目标已被其他黑矛兵锁定（WindUp/Charging 中），
                // 换扫描目标分散攻击；扫描也全是锁定目标时（软降级）才回到大脑目标。
                if (IsLockedByOthers(nearest))
                {
                    Agent alt = null; Vector3 altDir;
                    if (FindNearestEnemy(out altDir, out alt, log) && alt != null && alt != nearest)
                    {
                        if (log) Log("触发拦截: 大脑目标已被锁定，改冲扫描目标 " + alt.name);
                        nearest = alt; dir = altDir;
                    }
                }
                if (dir.sqrMagnitude < 0.36f)   // 目标已贴脸（<0.6m）→ 无冲刺距离，交给普通攻击
                {
                    if (log) Log("触发拦截: 目标已贴脸(" + dir.magnitude.ToString("F2") + "m)，不冲锋");
                    return false;
                }
            }
            else if (!FindNearestEnemy(out dir, out nearest, log))
            {
                if (log) Log("触发拦截: 无大脑目标且 6m 内无扫描目标（详见 FindNearestEnemy 诊断）");
                return false;
            }
            _chargeDirection = dir.normalized;
            _targetAgent = nearest;   // ★ 记住目标（冲刺完成后转身后退迎击）
            StartWindUp();
            return true;
        }

        /// <summary>协同防重：是否有其他黑矛兵正在 WindUp/Charging 且已锁定该目标。</summary>
        bool IsLockedByOthers(Agent target)
        {
            if (target == null) return false;
            var all = Resources.FindObjectsOfTypeAll<SpearChargeComponent>();
            for (int i = 0; i < all.Length; i++)
            {
                var o = all[i];
                if (o == null || o == this) continue;
                if ((o._phase == Phase.WindUp || o._phase == Phase.Charging) && o._targetAgent == target)
                    return true;
            }
            return false;
        }

        /// <summary>借鉴原版 JumpAttack：优先取 Swordsman 大脑正在追击/狩猎的目标（大脑的战术目标）。</summary>
        Agent GetBrainTarget()
        {
            if (_agent == null) return null;
            var sw = _agent.GetComponent<Swordsman>();
            if (sw != null && sw.target != null && sw.target.aliveState != null && sw.target.aliveState.active)
                return sw.target;
            if (_agent.enemyAgent != null && _agent.enemyAgent.aliveState != null && _agent.enemyAgent.aliveState.active)
                return _agent.enemyAgent;
            return null;
        }

        void StartWindUp()
        {
            _phase = Phase.WindUp;
            _phaseTimer = WindUpDuration;
            if (_chargeState != null) _chargeState.SetActive(true);
            // ★ 第十八轮：movability 拉满（原 0.2 抄自原版 travelling，但原版 travelling 的 navPos 是走路速度
            //   推进；本冲锋 navPos 以 ChargeSpeed=5m/s 推进，movability=0.2 把 transform 追 navPos 的速度限制在
            //   maxSpeed×0.2≈1m/s → 实测 transform 落后 navPos 0.89~1.31m（日志 lag=），冲锋结束恢复 movability 时
            //   角色被"弹回"到 navPos = 人物抽动。movability=1 让身体紧贴 navPos，冲锋不再橡皮筋。
            // ⚠️ 不设 maxSpeed=0：FixedUpdateAgent 末尾 speed=maxSpeed（Agent.cs:941），
            //    maxSpeed=0 会让 Body 的踏步动画追不上 navPos → 视觉"橡皮筋延迟"。
            _agent.movability = 1f;
            _agent.enemyMovability = 1f;
            _agent.maxSpeed = ChargeSpeed;   // 冲锋时提速，让 Body 的移动/动画跟上 navPos 推进（保留跑步动画）
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
                _energy = 1f;
                _hitAgents.Clear();   // 新一回合：重置能量与命中去重
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
            ArrivalBurst();   // ★ 抵达终点爆发（Pike Charge 风格的最后一撞）
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
            // 举矛公式见 SpearVisual.TryGetAimRotation（原版 Spear.LateUpdate：LookRotation(矛尖方向, 角色right) * Euler(0,0,90)）
            if (SpearVisual.TryGetAimRotation(_agent, _spearTransform.position + _chargeDirection, out _spearTargetRot))
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
            if (SpearVisual.TryGetAimRotation(_agent, targetPos, out _spearTargetRot))
                _hasSpearTarget = true;
        }

        void LateUpdate()
        {
            // 举矛/放矛旋转插值。
            // 冲锋位移推进见 Update → DoCharging（固定起点插值 + navPos 整体赋值），这里不再重复推进：
            // 旧版曾在此用 transform.position + dir*speed*dt 增量推进，与 DoCharging 的固定插值每帧互相覆盖
            // （LateUpdate 后执行会把正确的固定插值盖掉），已删除。
            // ★ 刺击期间旋转已锁定（_thrustRotLocked）：直接 snap 到锁定朝向，不每帧 Slerp 追目标 →
            //   矛保持直线直刺（鬼畜根因之一就是刺击时还向移动中的目标做 Slerp 插值）。
            // ★ 第十七轮：无目标时（_hasSpearTarget=false 或目标已死）抬回"举矛"姿态——长矛始终树立。
            if (_spearTransform == null) return;
            if (_thrustRotLocked && _hasSpearTarget)
            {
                _spearTransform.rotation = _spearTargetRot;
            }
            else if (_hasSpearTarget)
            {
                _spearTransform.rotation = Quaternion.Slerp(_spearTransform.rotation, _spearTargetRot, Time.deltaTime * 12f);
            }
            else
            {
                Quaternion idleRot;
                if (SpearVisual.TryGetRaisedRotation(_agent, out idleRot))
                    _spearTransform.rotation = Quaternion.Slerp(_spearTransform.rotation, idleRot, Time.deltaTime * 12f);
            }
        }

        /// <summary>冲锋命中结算（借鉴原版 Pike Charge）：AgentEnumerators 查矛中点周围玩家 Agent，
        /// 每 tick 命中预算 2 个，能量每命中 ×0.8 递减。</summary>
        bool DealChargeDamage()
        {
            // 命中锚点用"视觉长矛"而非 navPos（transform 滞后 navPos 约 1m，用 navPos 会隔空命中）。
            Vector3 basePos;
            if (_spearTransform != null) basePos = _spearTransform.position;
            else basePos = _agent.transform.position;
            Vector3 tipMid = basePos + _chargeDirection * (SpearLength * 0.5f);   // 矛中点（原版 vector + dir*spearLength/2）
            List<Agent> candidates = AgentEnumerators.GetStaticListRadiusSorted(tipMid, HitRadius, _agent.faction.enemy);
            bool hitAny = false;
            int budget = 2;   // 每 tick 命中预算：原版每帧 1 个新目标；0.15s tick 下给 2 个更有"扫过一排"的手感

            // 命中诊断（每 0.5s）：候选数量 + 矛中点位置
            if (candidates.Count > 0 && _hitDiagTimer <= 0f)
            {
                _hitDiagTimer = 0.5f;
                BSLog.Info("[Charge] 命中诊断: 矛中点=" + tipMid.ToString("F2") +
                    " navPos=" + _agent.navPos.pos.ToString("F2") + " 方向=" + _chargeDirection.ToString("F2") +
                    " 半径=" + HitRadius + "m 候选=" + candidates.Count);
            }
            else if (candidates.Count == 0 && _hitDiagTimer <= 0f)
            {
                _hitDiagTimer = 0.5f;
                BSLog.Info("[Charge] 命中诊断: 矛中点=" + tipMid.ToString("F2") + " 半径=" + HitRadius + "m 内无玩家Agent" +
                    " navPos=" + _agent.navPos.pos.ToString("F2"));
            }

            for (int i = 0; i < candidates.Count && budget > 0; i++)
            {
                var a = candidates[i];
                if (a == null || a == _agent) continue;
                if (a.aliveState == null || !a.aliveState.active) continue;
                if (_hitAgents.Contains(a)) continue;   // 同一次冲锋只结算一次
                _hitAgents.Add(a);
                budget--;
                try
                {
                    // ★ 伤害等级化 + 能量衰减（原版 Pike Charge：spear.attackSetting + settings，damage *= energy）：
                    //   基础伤害随 squad 等级增长，每命中一次 ×0.8 → 扫过多个敌人时递减。
                    int lvl = _squad != null ? _squad.level : 0;
                    float dmg = StabDamage * (1f + lvl * LevelDamageScale) * _energy;
                    _energy *= EnergyDecayPerHit;
                    var s = new AttackSettings { damage = dmg, knockback = StabKnockback, launchImpulse = StabLaunch, stun = StabStun };
                    Vector3 d = _chargeDirection;   // 沿锁定冲锋方向撞飞（比"朝敌人当前位置"更稳定）
                    d.y = 0f;
                    if (d.sqrMagnitude < 0.001f) d = _agent.transform.forward;
                    // ★ 第十九轮：冲锋命中同样走盾牌格挡（我方剑盾兵正面免伤 + 盾击反馈）
                    Attack atk = new Attack(s, d, a.transform.position, this, _squad, "Sfx/English/Spear");
                    TryShieldBlockSpear(a, ref atk);
                    a.DealDamage(atk);
                    _hitCount++;
                    hitAny = true;
                    // 原版 JumpAttack 同款：命中时轻微相机震动增强冲击感
                    try { Singleton<CameraShaker>.instance.ShakeOnce(0.02f); } catch { }
                    BSLog.Info("[Charge] HIT " + a.name + " dmg=" + dmg.ToString("F1") + " kb=" + StabKnockback +
                        " stun=" + StabStun + " energy=" + _energy.ToString("F2") + " hp=" + a.health.ToString("F1"));
                }
                catch (Exception ex) { BSLog.Error("[Charge] " + ex); }
            }
            return hitAny;
        }

        /// <summary>抵达终点爆发：对终点周围未命中单位再结算一次（伤害×0.3、击退+2，撞散阵型）。</summary>
        void ArrivalBurst()
        {
            try
            {
                Vector3 endPos = _agent.navPos.valid ? _agent.navPos.wPos : _chargeTargetPos;
                // ★ 借鉴原版 arrival：对终点周围"所有存活"单位再结算一次（沿途已命中的也会被最后一撞波及），
                //   伤害×0.3、击退+2、方向 = 从终点向外推×0.6 + dir；ragdoll 撞飞留在这里（标志性演出）。
                List<Agent> hits = AgentEnumerators.GetStaticListRadiusSorted(endPos, ArrivalBurstRadius, _agent.faction.enemy);
                int lvl = _squad != null ? _squad.level : 0;
                float dmg = StabDamage * (1f + lvl * LevelDamageScale) * 0.3f;
                int n = 0;
                for (int i = 0; i < hits.Count; i++)
                {
                    var a = hits[i];
                    if (a == null || a == _agent) continue;
                    if (a.aliveState == null || !a.aliveState.active) continue;
                    Vector3 d = ((a.transform.position - endPos).normalized * 0.6f + _chargeDirection).normalized;
                    d.y = 0f;
                    if (d.sqrMagnitude < 0.001f) d = _chargeDirection;
                    Vector3 pos2 = Vector3.MoveTowards(a.transform.position, endPos, a.radius * 0.7f) + a.chestOffset;
                    Attack atk2 = new Attack(new AttackSettings { damage = dmg, knockback = StabKnockback + 2f, launchImpulse = StabLaunch, stun = StabStun },
                        d, pos2, this, _squad, "Sfx/English/Spear");
                    TryShieldBlockSpear(a, ref atk2);   // ★ 第十九轮：抵达爆发同样走盾牌格挡
                    a.DealDamage(atk2);
                    _hitCount++;
                    n++;
                    try { Singleton<CameraShaker>.instance.ShakeOnce(0.03f); } catch { }
                }
                if (n > 0) BSLog.Info("[Charge] 抵达爆发 命中 " + n + " 个 终点=" + endPos.ToString("F2"));
            }
            catch (Exception ex) { BSLog.Error("[Charge] 抵达爆发异常: " + ex); }
        }

        void EndCharge()
        {
            // ★ 第十八轮：安全网——冲锋结束 navPos 若与视觉 transform 错位（movability 修复前差 1m+），先重新锚定到
            //   transform，避免恢复正常导航时角色"弹跳回位"（人物抽动）。movability 修复后此分支应罕见。
            try
            {
                if (_agent.navPos.valid)
                {
                    float gap = Vector3.Distance(_agent.navPos.wPos, _agent.transform.position);
                    if (gap > 0.3f)
                    {
                        BSLog.Info("[Charge] 收尾对齐: navPos-transform 差 " + gap.ToString("F2") +
                            "m，重新锚定 navPos 到 transform 防回弹");
                        _agent.navPos = new NavPos(_agent.navPos.navigationMesh, _agent.transform.position, true, 1f);
                    }
                }
            }
            catch { }
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

        /// <summary>第十九轮：单位死亡/失效时中止技能状态机（释放 exclusives、恢复速度/movability），回到 Idle。</summary>
        void AbortCharge()
        {
            if (_chargeState != null) _chargeState.SetActive(false);
            if (_agent != null)
            {
                _agent.maxSpeed = _originalSpeed;
                _agent.movability = 1f;
                _agent.enemyMovability = 1f;
                _agent.walkDir = Vector3.zero;
            }
            _phase = Phase.Idle;
            _phaseTimer = 0f;
        }

        /// <summary>
        /// 第十九轮（用户指定）：黑矛兵在技能释放（举矛/冲锋/后退）过程中**可被击杀**（平衡性）。
        /// 不再设置 attack.ignore 免疫——技能期间照常吃伤害（击退/眩晕/死亡正常结算，冲锋途中可能被射杀）。
        /// 保留 IAttackResponder 注册（attackResponders 列表），若后续想回调"冲锋霸体"在这里设
        /// `if (_phase == Phase.Charging) attack.ignore = true;` 即可。
        /// </summary>
        void IAttackResponder.ModifyAttack(ref Attack attack)
        {
            // 空实现：技能期间可被击杀（第十九轮起不再免疫）。
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

            // ★ 协同防重（借鉴原版 JumpAttack 的同伴检查 + 软降级）：
            //   统计其他黑矛兵正在"准备/冲锋"阶段锁定的目标。优先选未被锁定的；
            //   若全部被锁则软降级——选"锁定者最少"的目标（避免协同防重把所有目标锁光 → 没人冲锋）。
            var lockCount = new Dictionary<Agent, int>();
            var allCharges = Resources.FindObjectsOfTypeAll<SpearChargeComponent>();
            for (int i = 0; i < allCharges.Length; i++)
            {
                var o = allCharges[i];
                if (o == null || o == this) continue;
                if ((o._phase == Phase.WindUp || o._phase == Phase.Charging) && o._targetAgent != null)
                {
                    int c;
                    lockCount.TryGetValue(o._targetAgent, out c);
                    lockCount[o._targetAgent] = c + 1;
                }
            }

            int n = Physics.OverlapSphereNonAlloc(_agent.transform.position, DetectionRadius, _hitBuffer, ~0, QueryTriggerInteraction.Ignore);
            Agent bestUnlocked = null; float bestUnlockedDist = DetectionRadius;
            Agent bestOverlap = null; int bestOverlapLocks = int.MaxValue; float bestOverlapDist = DetectionRadius;
            for (int i = 0; i < n; i++)
            {
                var c = _hitBuffer[i];
                if (c == null) continue;
                var a = c.GetComponentInParent<Agent>();
                if (a == null || a == _agent || a.isViking) continue;
                if (a.aliveState != null && !a.aliveState.active) continue;
                float dist = Vector3.Distance(_agent.transform.position, a.transform.position);
                if (dist >= DetectionRadius) continue;
                int locks;
                lockCount.TryGetValue(a, out locks);
                if (locks == 0)
                {
                    if (dist < bestUnlockedDist) { bestUnlockedDist = dist; bestUnlocked = a; }
                }
                else if (locks < bestOverlapLocks || (locks == bestOverlapLocks && dist < bestOverlapDist))
                {
                    bestOverlapLocks = locks; bestOverlapDist = dist; bestOverlap = a;
                }
            }
            bool downgraded = bestUnlocked == null && bestOverlap != null;
            target = bestUnlocked != null ? bestUnlocked : bestOverlap;
            if (target == null)
            {
                if (log) BSLog.Info("[Charge] FindNearestEnemy: 探测collider=" + n + " 其他黑矛兵=" + (allCharges.Length - 1) +
                    " 已锁定目标=" + lockCount.Count + " → 无可用目标");
                return false;
            }
            float finalDist = bestUnlocked != null ? bestUnlockedDist : bestOverlapDist;
            if (log) BSLog.Info("[Charge] FindNearestEnemy: 锁定目标=" + target.name + " 距离=" + finalDist.ToString("F2") + "m" +
                (downgraded ? " (软降级: 目标已被其他黑矛兵锁定=" + bestOverlapLocks + ")" : ""));
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
