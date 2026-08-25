using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthMixedSquad1_0
{
    /// <summary>
    /// 黑矛兵冲刺技能（借鉴原版 Twohanded 触发 + Pike Charge 表现）：
    /// 状态机 Idle → WindUp → Charging → Retreat → Cooldown；优先取 Swordsman 大脑目标，
    /// 矛中点周围命中 + 能量衰减 + 抵达爆发。⚠️ 技能期间不设 attack.ignore 免疫（可被击杀）。
    /// </summary>
    public class SpearChargeComponent : MonoBehaviour, IBrainAction, IAttackResponder
    {
        const float DetectionRadius = 6.0f;   // 扫描兜底探测范围（优先取 Swordsman 大脑目标）
        const float ChargeSpeed = 5.0f;       // 冲刺速度 5m/s（更快更冲，冲击感）
        const float RetreatSpeed = 6f;        // 回马枪：返回速度（比冲锋稍快，突刺后快速退回盾后阵线）
        const float ChargeOvershoot = 2.0f;   // 穿透余量：冲过锁定格 2.0m（1.5→2.0，穿透敌阵、冲击阵营更强）
        const float CooldownTime = 10f;       // 冷却（2026-08-15 用户指定：延长到 10s，冲锋更稀有、更像"技能"）
        const float WindUpDuration = 0.5f;    // 起手
        const float RetreatDistance = 0.6f;   // 后退 0.6m（原 1.2 太远，且易退到船）
        const float SpearLength = 0.6f;       // 原版 Spear.spearLength
        const float StabDamage = 4.0f;        // 冲锋刺击基础伤害（随 squad 等级放大；3→4 更痛）
        const float StabKnockback = 9.0f;     // 击退（撞飞/撞下海）
        const float StabStun = 10f;           // 眩晕
        const float StabLaunch = 8f;          // 命中撞飞（ragdoll 弹起，背海敌人撞下海）
        const float HitRadius = 0.5f;         // 命中判定半径 = 单矛线宽 0.5m（用户指定：减少范围波及；原版单兵 ≈0.46m）
        const float ArrivalBurstRadius = 1.5f; // 抵达终点爆发半径（终点的范围波及；1.2→1.5 爆发更猛）
        const float EnergyDecayPerHit = 0.8f;  // 每命中一次能量衰减（原版 Pike Charge 同款：扫过一排递减）
        const float LevelDamageScale = 0.25f;  // 每级伤害增幅：dmg = StabDamage × (1 + 等级×系数)
        const float ThrustDistance = 0.45f;   // 近战刺击：长矛沿自身前向突刺的距离（视觉"刺"而非"挥砍"）
        const float ThrustRiseTime = 0.06f;   // 刺出速度（快）
        const float ThrustHoldTime = 0.12f;   // 刺出到位后短暂保持（命中窗口），再收回
        const float ThrustFallTime = 0.28f;   // 收回速度（慢）
        const float WalkableStep = 0.5f;       // 地形可行性采样步长（m）：直线路径逐点采样间隔
        const float WalkableEndMargin = 0.2f;  // 终点内收余量（m）：夹回后的终点再往内陆内收，避免踩在海岸线上
        const float BuildingBlockRadius = 0.4f;// 建筑遮挡检测半径（m）：采样点与 House 碰撞体距离小于该值即判定被建筑遮挡
        // ⚠️ 以上技能数值（速度/距离/冷却/伤害/半径等）有意保留为代码常量，未进 cfg（ModConfig）——
        //    技能表现是定稿的一部分，改 cfg 容易破坏手感；若确需暴露再迁到 ModConfig。

        // ===== 优化（2026-08-25，对照原版 PikeCharge / VikingSpearBrain 的丝滑化 + 致命化）=====
        const float TriggerScanInterval = 0.1f;   // 触发扫描节拍（原 0.25s → 0.1s）：技能响应更快、指令更跟手
        const float ChargeRampTime = 0.12f;       // 冲锋加速时间：发射后 0→满速 SmoothStep 升速（消除"静止→瞬移跳"）
        const float ChargeConvergeFactor = 0.25f; // LateUpdate 位移收敛系数：transform 向 navPos 每帧拉近的比例
        const float ChargeConvergeEps = 0.03f;    // 偏差阈值：transform 与 navPos 差 < 此值不插值（让 Body 自己追赶）
        // 阵型架矛（盾后刺击）：混编阵型中列矛兵的"矛长 + 额外延伸"，让矛尖越过盾线稳定戳中接敌前排。
        // 同时作用于：Swordsman.range（攻击距离，Plugin 读取）、TestHit（命中距离）、矛刺视觉（刺出距离）。
        public const float FormationLanceReach = 0.35f;
        const float FormationThrustDistance = 0.70f;   // 阵型架矛刺出距离（0.45→0.70：矛尖越过盾线）

        // ===== 第二轮（2026-08-25 实测反馈：对盾兵杀伤弱 / 冲刺后被杀 / 轻微不顺畅）=====
        // 冲锋破盾：重型冲锋冲击力远超快戳，盾牌格挡从 ×0.2 放宽到 ×0.5、眩晕全吃（盾挡不住冲撞的眩晕）——
        // 对盾墙有真实威慑；普通刺击（矛尖快戳）仍按原版 ×0.2/×0.4（平衡保留）。
        const float ChargeBlockDamageMult = 0.5f;
        const float ChargeBlockStunMult = 1.0f;
        // 冲锋能量回复/秒（借鉴原版 PikeCharge energyRegainSpeed=0.75）：每 tick 衰减后回复、封顶 1，
        // 防止"一次冲过十几人能量衰到 0"导致尾部命中只有 0.1 伤害——保持扫过一排的持续杀伤力。
        const float ChargeEnergyRegen = 0.75f;
        // 阵型穿透冲锋穿透余量（0.8m 取代通用 2.0m）：冲锋停在最深目标附近，不一头扎进敌阵深处被围杀
        // （普通冲锋/自由模式仍用 ChargeOvershoot=2.0m 保持穿透冲击阵营的表现）。
        const float PenetrationOvershoot = 0.8f;

        enum Phase { Idle, WindUp, Charging, Retreat, Cooldown }

        Phase _phase = Phase.Idle;
        bool _ordered;              // M4：收到"冲阵号令"（TacticalFormation 解除阵型门控，允许冲锋）
        Agent _agent;
        Squad _squad;
        bool _setupDone;
        static int _spriteDiagCount;         // 去剑诊断已打印次数（限前 3 只，避免刷屏）
        float _phaseTimer;
        Vector3 _chargeDirection;
        float _originalSpeed;
        readonly Collider[] _hitBuffer = new Collider[16];
        static readonly List<House> _houseCache = new List<House>();   // 建筑缓存：残骸遮挡兜底（遍历 House.bounds）
        static float _houseCacheTime = -999f;
        // 静态注册表：替换 Resources.FindObjectsOfTypeAll<SpearChargeComponent>()（Unity Mono 上每次都是全场景遍历，
        // 含未生成的模板/prefab 副本，是技能触发扫描的卡顿源）。OnEnable/OnDisable 维护，只含已生成的存活实例。
        static readonly List<SpearChargeComponent> _registry = new List<SpearChargeComponent>();
        float _lastLogTime = -999f;
        AgentState _chargeState;
        Transform _spearTransform;
        bool _hasSpearTarget;
        Quaternion _spearTargetRot;
        Vector3 _chargeStartPos;
        float _posLogTimer;
        float _actScanTimer;
        float _chargeElapsed;          // 冲锋已推进时长（s，加速曲线用）
        float _chargeProgress;         // 冲锋已推进距离（m，沿 _chargeStartPos→_chargeDirection）
        bool _isFormationCharge;       // 本次冲锋是否为阵型穿透冲锋（深锥形区目标）→ 用小穿透余量防深陷敌阵
        float _chargeOvershoot = ChargeOvershoot;   // 本次冲锋实际穿透余量（普通 2.0m / 阵型穿透 0.8m）
        // 冲锋/后退渲染同步缓冲——DoCharging/DoRetreat 每帧写入的 navPos 快照，
        // LateUpdate 里重新断言并硬同步 transform（防大脑/导航在 Update 后被改写造成"橡皮筋"回弹）。
        bool _renderSnapPending;
        Vector3 _renderSnapPos;
        NavPos _renderSnapNav;
        Agent _targetAgent;            // 冲锋目标（冲刺结束后转身后退迎击）
        float _chargeDistance;         // 锁定冲锋距离（到目标被定位时位置 + 矛长，不追踪）
        float _chargeDuration;         // 对应时长 = 距离 / 速度
        Vector3 _chargeTargetPos;      // 目标被定位时的锁定单元格（navPos.wPos），冲刺全程不追踪
        Vector3 _retreatEndPos;        // 后退终点（冲刺结束位置沿反向回退 RetreatDistance）
        int _hitCount;                 // 本回合命中数
        float _diagTimer;              // 触发拦截诊断节流（每次 log=true 间隔 12s，见 Update）
        float _hitDiagTimer;           // 命中诊断节流（每 0.5s 打一次）
        float _energy = 1f;            // 冲锋能量（每命中 ×EnergyDecayPerHit，扫过一排递减）
        readonly HashSet<Agent> _hitAgents = new HashSet<Agent>();   // 本回合已命中（去重，同目标只结算一次）
        Swordsman _swordsman;          // 近战刺击：读取 Swordsman.attack 状态
        Vector3 _spearBaseLocalPos;    // 长矛挂载基点（突刺偏移在此之上叠加）
        Transform _handAnchor;         // 持剑手锚点（长矛根每帧跟随，消除"持矛手脱离身躯"）
        Vector3 _handMountOffset;      // 矛根相对手的固定本地偏移（挂载时确定）
        bool _gripPin;                 // 握持点钉死开关（ModConfig.GripPinToHand，配合 SpearHandMode=2）
        Vector3 _gripOffsetLocal;      // 握持点(英军臂洞中心)相对矛根的本地偏移 = -0.309×矛世界宽 + 微调（Setup 计算）
        Vector3 _thrustOffsetLocal;    // 刺击位移（本地空间：攻击开始瞬间按"对准后的身体朝向"锁定一次，整段不再重算）
        float _thrust;                 // 当前突刺量 0~1
        bool _prevAttackActive;        // 近战诊断：上一帧是否在攻击
        bool _thrustHitDone;           // 本回合矛刺到位后是否已触发伤害（FirstHit）
        float _meleeDiagTimer;         // 近战诊断节流（突刺中每 0.2s 打一次）
        float _thrustStartTime;        // 本回合刺击开始时间（节奏曲线用）
        Vector3 _thrustDirWorld;       // 刺击开始时锁定的世界方向（整段刺击不再重算 → 消除鬼畜）
        bool _thrustDirLocked;         // 方向是否已锁定（目标存活才锁；目标消失退回 agent.forward）
        bool _thrustRotLocked;         // 刺击期间矛旋转是否已锁（不再每帧 Slerp 追目标）

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

        void OnEnable()
        {
            if (!_registry.Contains(this)) _registry.Add(this);
        }

        void OnDisable()
        {
            _registry.Remove(this);
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
            // ⚠️ 技能期间不设 attack.ignore 免疫（ModifyAttack 空实现，可被击杀）。
            _chargeState = new AgentState("BlackSpearmanCharge", _agent.exclusives, false, true);
            if (_agent.attackResponders != null && !_agent.attackResponders.Contains(this))
                _agent.attackResponders.Add(this);
            // 找到挂载的长矛（由 BlackSpearmanWeapon 挂载，命名 Spear_BlackSpearman）
            _spearTransform = _agent.transform.Find("Spear_BlackSpearman");
            _swordsman = GetComponent<Swordsman>();
            if (_spearTransform != null) _spearBaseLocalPos = _spearTransform.localPosition;
            // 长矛跟随持剑手——记录"矛根相对手"的偏移；手随身体动画移动时矛根每帧同步跟随，
            // 消除"持矛手脱离身躯"（旧版矛根固定于挂载瞬间，跑步/刺击时手与矛分离、攻击范围观感偏大）。
            _handAnchor = BlackSpearmanWeapon.FindSwordAnchor(_agent.transform);
            if (_handAnchor != null)
                _handMountOffset = _spearBaseLocalPos - _agent.transform.InverseTransformPoint(_handAnchor.position);
            // 第三只手整改（配合 SpearHandMode=2 英军臂改身体色）：握持点钉死——
            // 矛根每帧 = 手 − 旋转×gripOffset，让握持点恒定落在维京拳上（自然握矛、消除旋转时绕 pivot 漂移）。
            try
            {
                _gripPin = ModConfig.GripPinToHand != null && ModConfig.GripPinToHand.Value;
                // 偏差B修正v2：批量精灵渲染器 bounds 在批量坐标系下不可用（实测 105 单位 → 偏移 -32m 失真），
                // 改用 SpearLength=0.6m 作矛世界宽：握持点 = -0.309×0.6 = -0.185m（洞中心 x24.5 / pivot x64）。
                // SpearGripOffset 做微调（默认 0，正=前移/负=后移）。
                float fineTune = ModConfig.SpearGripOffset != null ? ModConfig.SpearGripOffset.Value : 0f;
                _gripOffsetLocal = new Vector3(-0.309f * SpearLength + fineTune, 0f, 0f);
                BSLog.Info("[WEAPON] 握持点偏移=" + _gripOffsetLocal.x.ToString("F3") +
                    "m（基准 -0.309×矛长0.6 微调=" + fineTune.ToString("F3") +
                    " 钉死=" + _gripPin + "）");
            }
            catch { }
            // 去剑诊断：对前 3 只黑矛兵自动 dump 完整层级 + 所有 sprite/sprite2 详情，
            // 用于确认"剑"到底来自独立子对象 / 动画帧 / sprite2 部件贴图。
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

        /// <summary>当前状态机阶段名（Idle/WindUp/Charging/Retreat/Cooldown，供抽动探针诊断输出）。</summary>
        public string PhaseLabel
        {
            get { return _phase.ToString(); }
        }

        /// <summary>是否正在冲阵（起手/冲刺/回撤中）。阵型据此决定是否把矛兵钉回盾后中列：
        /// 非 busy（含 冷却/待机）就拉回盾后 → 回马枪的"返回后长矛在后发起进攻"。</summary>
        public bool IsChargeBusy()
        {
            return _phase == Phase.WindUp || _phase == Phase.Charging || _phase == Phase.Retreat;
        }

        /// <summary>是否已收到冲阵号令（TacticalFormation 解除阵型门控）。供 CommandLanceStabs 判断：
        /// 已收到号令的矛兵不再启动架矛刺击，避免"刺一半突然收手转冲锋"的观感卡顿。</summary>
        public bool IsChargeOrdered()
        {
            return _ordered;
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

            // 冲锋/后退不再免疫伤害 →
            // 单位可能冲锋途中死亡 → 立即中止技能状态机（释放 exclusives、恢复速度），避免尸体继续推进 navPos。
            if (_agent.aliveState != null && !_agent.aliveState.active)
            {
                if (_phase != Phase.Idle && _phase != Phase.Cooldown) AbortCharge();
                return;
            }

            // 攻击（刺击）期间冻结矛根，不再每帧跟随持剑手的动画摆动——
            // 矛沿锁定方向直线刺出；攻击结束后恢复跟随（避免"走一步刺一下"的手-矛分离抖动）。
            bool meleeAttacking = _swordsman != null && _swordsman.attack != null && _swordsman.attack.active;
            if (!meleeAttacking) TrackSpearToHand();   // 矛根每帧跟随持剑手（身体动画驱动），消除"持矛手脱离身躯"

            UpdateMeleeThrust();   // 近战刺击表现：Swordsman 攻击时长矛前刺（视觉"刺"）

            // 独立触发检测（不依赖 Swordsman 状态机）：每 TriggerScanInterval 自己扫描一次，
            // Idle 状态下满足条件就启动冲锋（TryTriggerCharge 内部 _phase 守卫保证不重复）。
            // 0.1s 节拍（原 0.25s）：配合静态注册表（无 FindObjectsOfTypeAll 卡顿）让技能指令更跟手。
            _actScanTimer -= Time.deltaTime;
            if (_phase == Phase.Idle && _actScanTimer <= 0f)
            {
                _actScanTimer = TriggerScanInterval;
                // 每 12s 用 log=true 打一次"拦截原因"诊断（定位冲锋为何不触发；_diagTimer 在下行重置为 12f）
                _diagTimer -= TriggerScanInterval;
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

            // 待机/移动时维护长矛姿态——有目标矛尖朝敌，无目标则"举矛"（矛尖朝前上方树立），
            // 恢复"长矛始终树立"的设计（船上/未判定到敌人前不再水平持矛等待）。
            UpdateSpearPose();
        }

        /// <summary>无目标时把长矛抬到举矛姿态；有目标（冲锋目标/大脑目标存活）时矛尖指向目标。
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

        /// <summary>长矛根部每帧跟随持剑手锚点——用"挂载时矛根相对手的偏移"叠加当前手位，
        /// 使矛根始终贴在手上（身体动画跑/刺/待机时手在动，矛根同步动），不再悬空/脱离。</summary>
        void TrackSpearToHand()
        {
            if (_spearTransform == null || _handAnchor == null || _agent == null) return;
            try
            {
                Vector3 handLocal = _agent.transform.InverseTransformPoint(_handAnchor.position);
                if (_gripPin)
                {
                    // 握持点钉死：矛根 = 手 − 当前旋转×gripOffset → 任何朝向下，握持点(透明洞)都落在拳上。
                    // 旧逻辑把矛根钉在手=把精灵中心钉在手，握持点会随瞄准旋转绕手画圈 → 手"没长在应该的位置"。
                    Vector3 gripOffsetWorld = _spearTransform.rotation * _gripOffsetLocal;
                    _spearBaseLocalPos = handLocal - _agent.transform.InverseTransformDirection(gripOffsetWorld);
                }
                else
                {
                    _spearBaseLocalPos = handLocal + _handMountOffset;
                }
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

            // 攻击上升沿：锁定突刺方向 + 矛朝向（只锁一次，整段不再重算）
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

                // 刺击开始瞬间先把身体朝向 snap 到突刺方向（SetDirection 是瞬时旋转），
                // 再在"已对准"的本地系里锁定突刺位移 —— 两处同帧一致，整段刺击不再抖动。
                try { _agent.SetDirection(_thrustDirWorld); } catch { }
                // 阵型架矛：混编阵型中列矛兵刺出距离加长（0.45→0.70m），矛尖越过盾线（命中距离由 TestHit 同步延伸）
                float thrustDist = ThrustDistance;
                if (_phase == Phase.Idle && TacticalFormation.InFormation(_agent))
                    thrustDist = FormationThrustDistance;
                _thrustOffsetLocal = _agent.transform.InverseTransformDirection(_thrustDirWorld) * thrustDist;

                // 稳定刺击朝向：虚拟 right = cross(up, dir) 恒 ⊥ dir 永不退化（避免侧向目标时
                // LookRotation(dir, agent.right) roll 翻转 180°）；正对目标时 ≡ 旧式。⚠️ 符号别写反（cross(dir,up)=−right）。
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
                // 刺出-收回节奏：快速刺出(0.06s) → 短暂保持命中(0.12s) → 收回(0.28s)。
                // 旧版"攻击中只升不降"→ 矛一直顶在最前（thrust=1.00 多帧），观感"伸着"而非"刺"。
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

                // 近战诊断：突刺过程矛的位置/旋转（只在刺出/保持段记录，收回段 thrust<0.3 不刷屏）
                _meleeDiagTimer -= Time.deltaTime;
                if (_meleeDiagTimer <= 0f && _thrust > 0.3f)
                {
                    _meleeDiagTimer = 0.2f;
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
            }
            // 非刺出状态回到挂载基点（避免长矛卡在刺出位）
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

        /// <summary>对齐 Spear.TestHit：敌人 chest 转矛本地坐标并归一化，落单位球内即命中。
        /// 阵型架矛（盾后刺击）：混编阵型中的矛兵有效矛身长度 +FormationLanceReach，命中覆盖盾线前排敌人。</summary>
        bool TestHit(Vector3 enemyPos)
        {
            if (_spearTransform == null) return false;
            Vector3 v = _spearTransform.InverseTransformPoint(enemyPos);
            float d = SpearLength;          // 0.6m
            if (_agent != null && TacticalFormation.InFormation(_agent))
                d += FormationLanceReach;   // 阵型架矛：矛身有效长度延伸（与 Swordsman.range 加成一致）
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
            // 我方剑盾兵正面格挡（伤害×0.2 + 盾击音效/火花反馈），不再"直接死亡"
            TryShieldBlockSpear(target, ref atk);
            target.DealDamage(atk);
            BSLog.Info("[近战] SpearHit " + target.name + " dmg=" + (settings.damage).ToString("F1") +
                " kb=" + settings.knockback.ToString("F1") + " stun=" + settings.stun.ToString("F1") +
                " mult=" + mult.ToString("F2") + " hitEffect=" + (atk.effect != null));
        }

        /// <summary>
        /// 黑矛长矛 vs 我方剑盾兵：原版 Shield.ModifyAttack 只认 Spear 类攻击者，黑矛兵刺击/冲锋的攻击者
        /// 是 Swordsman/本组件（原版不识别）→ 无免伤无反馈。这里在结算前补正面盾牌判定
        /// （伤害 ×0.2、眩晕 ×0.4、盾击音效）；CloseCombatBrain 攻击时原版会自己播反馈，只做减免避免双音效。
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
                // 冲锋破盾 vs 普通刺击：冲锋的 monoAttacker=本组件（重型冲撞）→ 格挡放宽到 ×0.5、眩晕全吃
                // （盾挡不住冲撞的冲击眩晕）；近战刺击（monoAttacker=Swordsman）保持原版 ×0.2/×0.4。
                bool isChargeAttack = ReferenceEquals(atk.monoAttacker, this);
                if (isChargeAttack)
                {
                    atk.damage *= ChargeBlockDamageMult;
                    atk.stun *= ChargeBlockStunMult;
                }
                else
                {
                    atk.damage *= 0.2f;
                    atk.stun *= 0.4f;
                }
                atk.soundSuffix = "Shield";
                // 近战(Swordsman)攻击时原版 Shield.ModifyAttack 会自行播放 Deflect/Block 音效+火花；
                // 冲锋/爆发（monoAttacker=本组件）原版不识别 → 由这里补足反馈，避免双音效。
                if (!(atk.monoAttacker is CloseCombatBrain))
                {
                    // 原版 Shield.cs:147-158 挡近战是 Deflect（弹击）+ ShieldBlock（闷响）双播；
                    // 旧路径 "Sfx/English/SwordShield/Block" 不存在 → 静音。修正为双播对齐原版。
                    try { IslandGameplayManager.RequestCombatAudio("Sfx/English/SwordShield/Deflect", target.gameObject); } catch { }
                    try { IslandGameplayManager.RequestCombatAudio("Sfx/English/SwordShield/ShieldBlock", target.gameObject); } catch { }
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
            // 架矛刺击中（swordsman.attack.active）→ 先让当前刺击自然结束（0.5s 周期），
            // 下一轮 0.1s 扫描再触发冲锋——避免"刺一半突然收手转冲锋"的观感卡顿。
            if (_swordsman != null && _swordsman.attack != null && _swordsman.attack.active)
                return false;
            // M4 顺序联动门控：冲阵号令模式（EnableWaveCharge=true）下，混编阵型中的矛兵未收到"冲阵号令" → 待命；
            // 自由模式（false）则矛兵自由冲锋——回马枪（突刺→快速回盾后）仍由阵型钉位兜底
            bool waveMode = ModConfig.EnableWaveCharge != null && ModConfig.EnableWaveCharge.Value;
            if (waveMode && TacticalFormation.InFormation(_agent) && !_ordered)
            {
                if (log) Log("触发拦截: 阵型待命（盾线接敌/弓手压制中，等冲阵号令）");
                return false;
            }
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

            // 触发逻辑借鉴原版 Twohanded(JumpAttack)：优先取 Swordsman 大脑已锁定的目标
            // （大脑的追击/狩猎目标），而不是独立扫描 —— 技能跟着大脑的战术走，修复"走路到阵前不冲锋"。
            // 只有大脑没有目标时才退回 6m 扫描兜底。
            // 阵型冲阵号令（waveMode && _ordered）：优先选正前方锥形区内**最远**敌人（贯穿整条敌阵，
            // 途经前排盾兵（能量×0.8 递减、盾牌背向不格挡）→ 终点爆发打到后排弓手/脆皮 = 更致命）。
            // 阵型穿透冲锋用小穿透余量（_chargeOvershoot=0.8m），停在最深目标附近，不深陷敌阵被围杀。
            bool formationCharge = waveMode && _ordered && TacticalFormation.InFormation(_agent);
            _isFormationCharge = false;
            Agent nearest = null;
            Vector3 dir = Vector3.zero;
            if (formationCharge)
            {
                Agent deep; Vector3 deepDir;
                if (FindDeepestEnemyInCone(out deepDir, out deep, log) && deep != null)
                {
                    nearest = deep;
                    dir = deepDir;
                    _isFormationCharge = true;
                }
            }
            if (nearest == null) nearest = GetBrainTarget();
            if (nearest != null)
            {
                if (!formationCharge || dir.sqrMagnitude < 0.0001f)
                {
                    // 路径判定：朝大脑目标的导航格（Twohanded 的 landPos = target.navPos，用 navPos 精确位置而非 transform）。
                    // 阵型穿透目标已给出 dir 时跳过（dir 由 FindDeepestEnemyInCone 提供）；
                    // 锥形区选目标失败回退到大脑目标时 dir 仍为零 → 补算，避免零方向"幽灵冲锋"。
                    dir = nearest.navPos.wPos - _agent.navPos.wPos;
                    dir.y = 0f;
                }
                // 协同防重也作用于大脑/阵型目标：若该目标已被其他黑矛兵锁定（WindUp/Charging 中），
                // 换扫描目标分散攻击；扫描也全是锁定目标时（软降级）才回到原目标。
                if (IsLockedByOthers(nearest))
                {
                    Agent alt = null; Vector3 altDir;
                    if (FindNearestEnemy(out altDir, out alt, log) && alt != null && alt != nearest)
                    {
                        if (log) Log("触发拦截: 大脑目标已被锁定，改冲扫描目标 " + alt.name);
                        nearest = alt; dir = altDir;
                    }
                }
                if (dir.sqrMagnitude < 0.36f)   // 目标已贴脸（<0.6m）→ 无冲刺距离，交给普通攻击（阵型架矛/近战刺击兜底）。
                    // 阵型穿透目标（FindDeepestEnemyInCone）的 dir 已归一化（=1）不受此守卫影响；
                    // 此守卫主要拦"锥形区选目标失败→回退大脑目标"时的贴脸情况（避免零方向幽灵冲锋）。
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
            // 路径底线：目标锁定格与自身之间的直线必须位于主岛可走导航网格上（水面/悬崖/网格外 → 不冲）。
            // 建筑不再硬拦截——目标在建筑后也发起冲锋（撞建筑前停，但能冲出威慑距离并命中沿途敌人）。
            if (nearest == null || !nearest.navPos.valid)
            {
                if (log) Log("触发拦截: 目标 navPos 无效，不发起冲锋");
                return false;
            }
            if (!IsTerrainPathClear(_agent.navPos.wPos, nearest.navPos.wPos))
            {
                // 地形被挡：尝试换一个地形通畅的扫描目标（避免"悬崖后目标"导致不冲锋）
                Agent alt = null; Vector3 altDir;
                if (FindNearestEnemy(out altDir, out alt, log) && alt != null && alt != nearest)
                {
                    if (log) Log("触发拦截: 原目标地形被挡，改冲通畅目标 " + alt.name);
                    nearest = alt; dir = altDir;
                }
                else
                {
                    if (log) Log("触发拦截: 直线路径被地形遮挡(目标=" + nearest.name + ")，不发起冲锋");
                    return false;
                }
            }
            _chargeDirection = dir.normalized;
            _targetAgent = nearest;   // 记住目标（冲刺完成后转身后退迎击）
            StartWindUp();
            return true;
        }

        /// <summary>协同防重：是否有其他黑矛兵正在 WindUp/Charging 且已锁定该目标。静态注册表查询（无 FindObjectsOfTypeAll）。</summary>
        bool IsLockedByOthers(Agent target)
        {
            if (target == null) return false;
            for (int i = 0; i < _registry.Count; i++)
            {
                var o = _registry[i];
                if (o == null || o == this || o._agent == null) continue;   // _agent 为空 = 未 Setup（模板副本），跳过
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

        /// <summary>冲阵号令（TacticalFormation 战斗相位调用）：解除阵型门控并立即重扫，自然触发冲锋。
        /// 错峰由阵型按 WaveInterval 逐个调用控制（波次冲锋）。</summary>
        public void OrderCharge()
        {
            _ordered = true;
            _actScanTimer = 0f;   // 下一帧立即 TryTriggerCharge
        }

        void StartWindUp()
        {
            _phase = Phase.WindUp;
            _phaseTimer = WindUpDuration;
            _renderSnapPending = false;   // 起手阶段不硬同步（站桩举矛）
            if (_chargeState != null) _chargeState.SetActive(true);
            // movability 拉满（原 0.2 抄自原版 travelling，但原版 travelling 的 navPos 是走路速度
            // 推进；本冲锋 navPos 以 ChargeSpeed=5m/s 推进，movability=0.2 把 transform 追 navPos 的速度限制在
            // maxSpeed×0.2≈1m/s → 实测 transform 落后 navPos 0.89~1.31m（日志 lag=），冲锋结束恢复 movability 时
            // 角色被"弹回"到 navPos = 人物抽动。movability=1 让身体紧贴 navPos，冲锋不再橡皮筋。
            // ⚠️ 不设 maxSpeed=0：FixedUpdateAgent 末尾 speed=maxSpeed（Agent.cs:941），
            // maxSpeed=0 会让 Body 的踏步动画追不上 navPos → 视觉"橡皮筋延迟"。
            _agent.movability = 1f;
            _agent.enemyMovability = 1f;
            _agent.maxSpeed = ChargeSpeed;   // 冲锋时提速，让 Body 的移动/动画跟上 navPos 推进（保留跑步动画）
            _agent.walkDir = Vector3.zero;
            RaiseSpear();
            Log("WIND-UP");
        }

        void DoWindUp()
        {
            // 起手转向提速 720→1080°/s：前摇内快速把身体转向冲锋方向，技能释放更果断（不再"转圈"占掉前摇）
            _agent.LookInDirection(_chargeDirection, 1080f, 10f);
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f)
            {
                _phase = Phase.Charging;
                // 发射起点对齐**视觉位置**（transform）：navPos 因动画追赶可领先 transform ≤1m，
                // 若用 navPos 当起点，第一帧位移会把模型从落后处"瞬移"到 navPos = 起手跳。
                // 先同步 navPos 到视觉位，让导航与渲染从同一点出发，再按锁定方向推进。
                Vector3 start = _agent.transform.position;
                start.y = 0f;
                NavPos np0 = _agent.navPos;
                if (np0.valid)
                    _agent.navPos = new NavPos(np0.navigationMesh, start, true, 1f);   // 原版同款回退
                _chargeStartPos = start;
                _hitCount = 0;
                _energy = 1f;
                _hitAgents.Clear();   // 新一回合：重置能量与命中去重
                _chargeElapsed = 0f;
                _chargeProgress = 0f;
                // 阵型穿透冲锋（深锥形区目标）→ 小穿透余量（停在最深目标附近防深陷敌阵）；其余用通用 2.0m
                _chargeOvershoot = _isFormationCharge ? PenetrationOvershoot : ChargeOvershoot;
                // 锁定目标：向目标"被定位时"的单元格（navPos.wPos）冲刺，冲刺全程不追踪。
                // 我方单位横向位移躲闪 → 冲到锁定格落空 → 技能前半段同样算用掉 → 后退迎击。
                if (_targetAgent != null && _targetAgent.aliveState != null && _targetAgent.aliveState.active)
                    _chargeTargetPos = _targetAgent.navPos.wPos;
                else
                    _chargeTargetPos = _chargeStartPos + _chargeDirection * 3f;   // 目标已消失则冲一段
                // 发射瞬间重锁方向（仍是非追踪：只在发射时刻锁线，冲刺全程不追踪）——
                // 0.5s 前摇内目标可能位移，用最新相对位置重锁，让矛线对准目标当前身位（丝滑）
                Vector3 rel = _chargeTargetPos - _chargeStartPos;
                rel.y = 0f;
                if (rel.sqrMagnitude > 0.0001f)
                {
                    _chargeDirection = rel.normalized;
                    // 重锁后同步举矛朝向（矛已举起，这里做最后对准，避免冲锋线与人/矛朝向不一致）
                    if (SpearVisual.TryGetAimRotation(_agent, _chargeStartPos + _chargeDirection * SpearLength, out _spearTargetRot))
                        _hasSpearTarget = true;
                }
                // 冲锋距离 = 到锁定格 + 矛长 + 穿透余量（普通 2.0m / 阵型穿透 0.8m）：穿透敌阵、冲击阵营，命中也冲完整段
                _chargeDistance = Mathf.Max(0.5f, Vector3.Distance(_chargeStartPos, _chargeTargetPos) + SpearLength + _chargeOvershoot);
                // 终点夹回：名义终点可能超出主岛可走网格（目标背靠海/悬崖/建筑时，矛长+穿透余量
                // 会把终点送出岛外或撞进建筑 → 模型浮在海面/穿墙）。把冲锋距离截短到直线最远可走处：
                // 至少到达目标锁定格，穿透段被夹回岸上/建筑前。
                float distToLockedCell = Vector3.Distance(_chargeStartPos, _chargeTargetPos);
                float walkableEnd = MaxWalkableDistAlongRay(_chargeStartPos, _chargeDirection, _chargeDistance, distToLockedCell);
                if (walkableEnd < _chargeDistance - 0.01f)
                {
                    BSLog.Info("[Charge] 终点夹回: 名义终点=" + _chargeDistance.ToString("F2") +
                        "m > 最远可走=" + walkableEnd.ToString("F2") + "m，终点夹回岸上/建筑前");
                    _chargeDistance = Mathf.Max(0.5f, walkableEnd);
                }
                _chargeDuration = _chargeDistance / ChargeSpeed;
                _phaseTimer = _chargeDuration + ChargeRampTime;   // 加速曲线占用的时间一并计入总时长
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

            // 冲锋期间驱动跑步动画：walkDir 指向冲锋方向 + speed=ChargeSpeed → Body 播奔跑而非站姿滑行。
            // 大脑已被 exclusives 锁定（不会覆盖 walkDir），navPos 由本帧 target 直接推进（下一帧覆盖），
            // 位置不受 walkDir 影响，只让身体动画跟上冲锋速度（此前 body=stand animSpeed=0.35 = 站姿滑行）。
            _agent.walkDir = _chargeDirection;
            _agent.speed = ChargeSpeed;

            // 每帧让长矛对准目标（矛尖朝敌人 chest）
            PointSpearAtTarget();

            // 位移推进：固定锁定距离（不追踪），速度沿加速曲线 0→ChargeSpeed（SmoothStep 0.12s）。
            // 旧版 t=elapsed/duration 线性满速起步 → 静止瞬间冲满速 = "起手瞬移跳"；加速曲线让起跑有爆发感且不跳变。
            _chargeElapsed += Time.deltaTime;
            float speedFactor = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_chargeElapsed / ChargeRampTime));
            _chargeProgress = Mathf.Min(_chargeDistance, _chargeProgress + ChargeSpeed * speedFactor * Time.deltaTime);
            Vector3 target = _chargeStartPos + _chargeDirection * _chargeProgress;

            // 冲锋被阻拦：当前直线点若已落在不可通过地形（NavPos.MoveTo 判定不可达/不在主岛），
            // 立即停止冲锋进入冷却；越过锁定目标格（含建筑余量）之后被房屋/残骸占据同样被阻拦——
            // 冲锋不穿墙、不穿水、不穿悬崖（目标格近旁的建筑不算，避免"贴墙打"误判）。
            float distFromStart = Vector3.Distance(_chargeStartPos, target);
            float distToLockedCell = Vector3.Distance(_chargeStartPos, _chargeTargetPos);
            if (!IsPointWalkable(target) ||
                (distFromStart > BuildingBlockRadius && distFromStart > distToLockedCell + BuildingBlockRadius &&
                 IsPointBlockedByHouse(target)))
            {
                BSLog.Info("[Charge] 被阻拦: 冲锋途中遇到不可通过地形/建筑，停在 " + target.ToString("F2"));
                EndCharge();
                return;
            }

            NavPos np = _agent.navPos;
            if (np.valid)
            {
                Vector3 local = np.transform.InverseTransformPoint(target);
                if (!np.MoveTo(local))
                    np = new NavPos(np.navigationMesh, target, true, 1f);   // 原版同款回退
                _agent.navPos = np;
                // 记录本帧 navPos 快照，LateUpdate 重新断言 + 平滑逼近（防大脑改写 / 起手跳 / 橡皮筋回弹）
                _renderSnapPending = true;
                _renderSnapPos = target;
                _renderSnapNav = np;
                // ⚠️ 历史注释（已过时）：曾每帧硬同步 transform（teleport）。现已改为 LateUpdate 按
                // ChargeSpeed×dt 平滑逼近（MoveTowards）——Body.stepping 跑步动画照常播放，视觉连贯无瞬移。
            }

            // 每 1.5s 记录位置 + 长矛旋转 + Body 状态 + 动画参数 + navPos 滞后量（降频：渲染已由 LateUpdate 硬同步）
            _posLogTimer -= Time.deltaTime;
            if (_posLogTimer <= 0f)
            {
                _posLogTimer = 1.5f;
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

            // 穿透式冲锋：命中不打断——矛尖沿途对碰到的敌人持续结算伤害，冲完整段（穿过敌阵）再后退。
            // 命中带撞飞（launchImpulse）：敌人沿冲锋方向被弹起/撞下海。
            DealChargeDamage();

            // 冲到锁定格+余量（或时间到/加速段走完距离）→ 后退迎击（技能算用掉）
            if (_phaseTimer <= 0f || _chargeProgress >= _chargeDistance) { StartRetreat(); return; }
        }

        /// <summary>冲刺结束（命中或落空）：以冲刺速度回退小段距离，稳住阵脚，举矛迎击。</summary>
        void StartRetreat()
        {
            _phase = Phase.Retreat;
            _renderSnapPending = false;   // 后退帧重新记录快照
            _agent.maxSpeed = RetreatSpeed;   // 回撤提速（与 DoRetreat 的 speed=RetreatSpeed 一致，驱动跑步动画）
            // navPos 失效保护：敌人被击飞/冲锋撞崖可能使 navPos 变空（wPos 访问会崩），直接收尾进冷却。
            if (!_agent.navPos.valid) { EndCharge(); return; }
            // 防"退到船/海"：不在主岛 navmesh 上就不后退（否则会退到船建模上，玩家打不到）
            if (!_agent.navPos.onMain) { EndCharge(); return; }
            ArrivalBurst();   // 抵达终点爆发（Pike Charge 风格的最后一撞）
            // M3：优先回退到本船战术阵型的中列格位（若存在阵型）；否则沿冲锋反方向回退 RetreatDistance。
            // 安全校验：格位若已越过冲锋起点（在敌阵方向 = 盾线已被突破/格位不安全），退回冲锋起点（盾后），
            // 避免退到敌阵里被围杀（实测"冲刺后就被击杀"主因之一是深陷敌阵后回退落点仍不安全）。
            Vector3 retreatTarget = _agent.navPos.wPos - _chargeDirection * RetreatDistance;
            try
            {
                var slot = TacticalFormation.GetFormationSlot(_agent);
                if (slot.HasValue)
                {
                    Vector3 slotToStart = _chargeStartPos - slot.Value;
                    slotToStart.y = 0f;
                    // dot > 0 = 格位在冲锋起点之后（己方盾后侧）→ 回格位；否则回起点（盾后安全点）
                    if (Vector3.Dot(_chargeDirection, slotToStart) > 0f)
                        retreatTarget = slot.Value;
                    else
                        retreatTarget = _chargeStartPos;
                }
            }
            catch { }
            _retreatEndPos = retreatTarget;
            BSLog.Info("[回马枪] 突刺结束→快速回撤盾后 目标=" + _retreatEndPos.ToString("F2") +
                " 命中=" + _hitCount + "（返回段无伤害）");
        }

        void DoRetreat()
        {
            // navPos 失效保护（同上）：直接收尾进冷却，避免 NullReferenceException
            if (!_agent.navPos.valid) { EndCharge(); return; }
            // 防退到船/海：后退过程中一旦脱离主岛 navmesh 立即收尾
            if (!_agent.navPos.onMain) { EndCharge(); return; }

            Vector3 to = _retreatEndPos - _agent.navPos.wPos;   // 指向后退终点
            float dist = to.magnitude;
            if (dist < 0.15f) { EndCharge(); return; }          // 已回退到位，抬枪迎击

            // 后退期间驱动移动动画（Body 播跑步/碎步而非站姿滑行）
            Vector3 retreatDir = to.normalized;
            _agent.walkDir = retreatDir;
            _agent.speed = RetreatSpeed;

            // 回马枪：以快于冲锋的速度（RetreatSpeed）快速退回盾后阵位，贴着 navmesh 移动；返回段不结算伤害
            Vector3 step = to.normalized * (RetreatSpeed * Time.deltaTime);
            if (step.magnitude > dist) step = to;
            NavPos np = _agent.navPos;
            if (np.valid)
            {
                Vector3 tgt = _agent.navPos.wPos + step;
                Vector3 local = np.transform.InverseTransformPoint(tgt);
                if (!np.MoveTo(local))
                {
                    // 后退点无效/超出主岛（如朝海/船的方向）→ 原地收尾，不硬退，防止站上船建模
                    EndCharge();
                    return;
                }
                _agent.navPos = np;
                // 后退同样记录渲染同步快照
                _renderSnapPending = true;
                _renderSnapPos = np.wPos;
                _renderSnapNav = np;
            }

            // 转向策略：长距离回撤（回盾后阵位 >1.2m）→ 转身跑回（面向回撤方向）；短距离（<1.2m）→ 面朝敌人
            // 后退迎击（回马枪姿态，矛对敌）；矛保持朝敌迎击。
            if (dist >= 1.2f)
                _agent.LookInDirection(retreatDir, 720f, 20f);
            else if (_targetAgent != null && _targetAgent.aliveState != null && _targetAgent.aliveState.active)
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
            // Body.stepping 是"追赶式"插值——navPos 以冲锋速度(5m/s)推进时 transform 可能滞后。
            // 渲染帧末尾重新断言 navPos 快照（防大脑/导航在 Update 后被改写回弹），并让 transform
            // 以"≤最高移动速度×dt"平滑逼近目标点（非 teleport）：偏差小让 Body 自己追赶、偏差大按比例拉近，
            // 避免旧版每帧硬同步 `transform.position = navPos` 与 Body 插值打架造成的视觉抽动/起手跳。
            // 近战刺击同理（攻击期间 walkDir 已被 AttackUpdate 前缀冻结，navPos 停住，身体站桩、矛直线前刺）。
            if (_agent != null && _agent.aliveState != null && _agent.aliveState.active)
            {
                bool chargeMove = _phase == Phase.Charging || _phase == Phase.Retreat;
                bool meleeMove = _swordsman != null && _swordsman.attack != null && _swordsman.attack.active;
                if (chargeMove)
                {
                    if (_renderSnapPending && _renderSnapNav.valid)
                    {
                        _agent.navPos = _renderSnapNav;   // 重新断言（防大脑改写回弹）
                        // 位移平滑逼近（非 teleport）：transform 以 ≤最高移动速度×dt 向目标点插值，
                        // 偏差 < 阈值（ChargeConvergeEps）不动让 Body 自己追赶；偏差大按比例拉近。
                        // 旧版每帧 `transform.position = _renderSnapPos` 与 Body 插值打架 = 视觉抽动/起手跳。
                        Vector3 cur = _agent.transform.position;
                        Vector3 dest = _renderSnapPos;
                        float gap = Vector3.Distance(cur, dest);
                        if (gap > ChargeConvergeEps)
                        {
                            float maxStep = Mathf.Max(ChargeSpeed, RetreatSpeed) * Time.deltaTime * 1.25f;
                            _agent.transform.position = Vector3.MoveTowards(cur, dest, Mathf.Max(maxStep, gap * ChargeConvergeFactor));
                        }
                    }
                }
                else if (meleeMove && _agent.navPos.valid)
                {
                    _agent.transform.position = _agent.navPos.wPos;
                    // 刺击期消除"蠕动"（实测 clip=Swordsman_Walk + agentYaw 漂移）：
                    // ① 朝向钉死：FixedUpdateAgent.ApplyLook 每物理帧按陈旧 look 目标转动身体，
                    //    与 AttackUpdate 前缀的 SetDirection 瞬移抢位置 = 身体左右摆动。
                    //    渲染帧末尾重新断言锁定朝向，并把 look 目标钉在锁定方向（speed=0 不再转走）。
                    if (_thrustRotLocked && _hasSpearTarget)
                    {
                        _agent.SetDirection(_thrustDirWorld);
                        try { _agent.LookInDirection(_thrustDirWorld, 0f, 0f); } catch { }
                    }
                    // ② 强制站姿：navPos 停住后 Body 要等一个 stepTime(~0.2s) 才从 stepping 切回
                    //    standing，而刺击只有 0.5s → 身体用走路动画原地踏步 = 上下起伏"蠕动"。
                    //    强制 standing + 切 Idle + Speed=0，停掉走步动画（滑动/眩晕期不强制）。
                    try
                    {
                        if (_agent.body != null && _agent.body.standing != null && !_agent.body.standing.active &&
                            (_agent.body.sliding == null || !_agent.body.sliding.active))
                        {
                            _agent.body.standing.SetActive(true);
                            try { _agent.PlayAnimation("Idle"); } catch { }
                        }
                        try { _agent.animator.SetFloat("Speed", 0f); } catch { }
                    }
                    catch { }
                }
            }

            // 举矛/放矛旋转插值（位移推进在 DoCharging 完成，不在此重复）。
            // 刺击期旋转已锁 → 直接 snap 到锁定朝向（不追移动目标，矛保持直线直刺）；无目标时抬回举矛姿态。
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
            _hitDiagTimer -= Time.deltaTime;   // 缺陷修复：旧代码只赋值从不递减 → 命中诊断全程只打一次
            // 命中锚点用"视觉长矛"而非 navPos（transform 滞后 navPos 约 1m，用 navPos 会隔空命中）。
            Vector3 basePos;
            if (_spearTransform != null) basePos = _spearTransform.position;
            else basePos = _agent.transform.position;
            Vector3 tipMid = basePos + _chargeDirection * (SpearLength * 0.5f);   // 矛中点（原版 vector + dir*spearLength/2）
            List<Agent> candidates = AgentEnumerators.GetStaticListRadiusSorted(tipMid, HitRadius, _agent.faction.enemy);
            bool hitAny = false;
            int budget = 2;   // 每帧命中预算：原版每帧 1 个新目标；每帧给 2 个更有"扫过一排"的手感

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
                    // 伤害等级化 + 能量衰减（原版 Pike Charge：spear.attackSetting + settings，damage *= energy）：
                    // 基础伤害随 squad 等级增长，每命中一次 ×0.8 → 扫过多个敌人时递减。
                    int lvl = _squad != null ? _squad.level : 0;
                    float dmg = StabDamage * (1f + lvl * LevelDamageScale) * _energy;
                    _energy *= EnergyDecayPerHit;
                    // 能量回复（借鉴原版 PikeCharge energyRegainSpeed=0.75）：每 tick 衰减后回复、封顶 1，
                    // 防止"一次冲过十几人能量衰到 0"尾部命中只有 0.1 伤害——保持扫过一排的持续杀伤力。
                    _energy = Mathf.Min(1f, _energy + ChargeEnergyRegen * Time.deltaTime);
                    var s = new AttackSettings { damage = dmg, knockback = StabKnockback, launchImpulse = StabLaunch, stun = StabStun };
                    Vector3 d = _chargeDirection;   // 沿锁定冲锋方向撞飞（比"朝敌人当前位置"更稳定）
                    d.y = 0f;
                    if (d.sqrMagnitude < 0.001f) d = _agent.transform.forward;
                    // 冲锋命中特效 + 专属技能音效前缀：原版 PikeChargeComponent.GetAttack 命中链就是
                    // attackPrefix="Sfx/Ability/PikeCharge" + 默认后缀 "Hit" → 比 Spear 命中音更像"技能"。
                    // 特效：Agent.DealDamage 会在结算后 PlayAt(attack.pos)，冲锋原本无火花/血光，现补 hitEffect。
                    Attack atk = new Attack(s, d, a.transform.position, this, _squad, "Sfx/Ability/PikeCharge",
                        ScriptableObjectSingleton<PrefabManager>.instance.hitEffect);
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
                // 借鉴原版 arrival：对终点周围"所有存活"单位再结算一次（沿途已命中的也会被最后一撞波及），
                // 伤害×0.3、击退+2、方向 = 从终点向外推×0.6 + dir；ragdoll 撞飞留在这里（标志性演出）。
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
                        d, pos2, this, _squad, "Sfx/Ability/PikeCharge",
                        ScriptableObjectSingleton<PrefabManager>.instance.hitEffect);
                    TryShieldBlockSpear(a, ref atk2);   // 抵达爆发同样走盾牌格挡
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
            // 安全网——冲锋结束 navPos 若与视觉 transform 错位（movability 修复前差 1m+），先重新锚定到
            // transform，避免恢复正常导航时角色"弹跳回位"（人物抽动）。movability 修复后此分支应罕见。
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
            _renderSnapPending = false;   // 退出冲锋/后退，停止硬同步
            if (_chargeState != null) _chargeState.SetActive(false);
            _agent.maxSpeed = _originalSpeed;   // 恢复冲锋前的速度
            _agent.movability = 1f;
            _agent.enemyMovability = 1f;
            _agent.walkDir = Vector3.zero;
            // 举枪迎击：不 LowerSpear——矛保持朝敌（迎击姿态），技能进入冷却。
            PointSpearAtTarget();
        }

        void UpdateCooldown()
        {
            _phaseTimer -= Time.deltaTime;
            if (_phaseTimer <= 0f)
            {
                _phase = Phase.Idle; _ordered = false; _agent.maxSpeed = _originalSpeed;
                _actScanTimer = 0f;   // 冷却结束下一帧立即重扫（技能响应丝滑，不再等下个 0.1s 节拍）
            }
        }

        /// <summary>单位死亡/失效时中止技能状态机（释放 exclusives、恢复速度/movability），回到 Idle。</summary>
        void AbortCharge()
        {
            if (_agent != null) _meleeAttackStart.Remove(_agent);   // 与 OnDestroy 的清理重复无害（幂等），覆盖早停路径
            if (_chargeState != null) _chargeState.SetActive(false);
            _renderSnapPending = false;   // 中止同步
            if (_agent != null)
            {
                _agent.maxSpeed = _originalSpeed;
                _agent.movability = 1f;
                _agent.enemyMovability = 1f;
                _agent.walkDir = Vector3.zero;
            }
            _phase = Phase.Idle;
            _phaseTimer = 0f;
            _ordered = false;   // M4：中止后需重新等号令
        }

        /// <summary>
        /// 黑矛兵在技能释放（举矛/冲锋/后退）过程中**可被击杀**（平衡性）。
        /// 不再设置 attack.ignore 免疫——技能期间照常吃伤害（击退/眩晕/死亡正常结算，冲锋途中可能被射杀）。
        /// 保留 IAttackResponder 注册（attackResponders 列表），若后续想回调"冲锋霸体"在这里设
        /// `if (_phase == Phase.Charging) attack.ignore = true;` 即可。
        /// </summary>
        void IAttackResponder.ModifyAttack(ref Attack attack)
        {
            // 空实现：技能期间可被击杀（不再免疫）。
        }

        void OnDestroy()
        {
            // 内存卫生：攻击中死亡时 NotifyMeleeAttackEnd 不会执行 → 静态字典残留 Agent 引用，这里兜底清理
            if (_agent != null) _meleeAttackStart.Remove(_agent);
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

            // 协同防重（借鉴原版 JumpAttack 的同伴检查 + 软降级）：
            // 统计其他黑矛兵正在"准备/冲锋"阶段锁定的目标。优先选未被锁定的；
            // 若全部被锁则软降级——选"锁定者最少"的目标（避免协同防重把所有目标锁光 → 没人冲锋）。
            // 静态注册表查询（原 FindObjectsOfTypeAll 每次全场景遍历，替换为 OnEnable/OnDisable 维护的列表）。
            int registryCount = _registry.Count;
            var lockCount = new Dictionary<Agent, int>();
            for (int i = 0; i < registryCount; i++)
            {
                var o = _registry[i];
                if (o == null || o == this || o._agent == null) continue;
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
                if (!a.navPos.valid) continue;
                // 地形通畅过滤：目标与自身直线必须位于主岛可走网格（悬崖后目标排除；建筑后保留——冲锋能冲出距离）
                if (!IsTerrainPathClear(_agent.navPos.wPos, a.navPos.wPos)) continue;
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
                if (log) BSLog.Info("[Charge] FindNearestEnemy: 探测collider=" + n + " 其他黑矛兵=" + (registryCount - 1) +
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

        /// <summary>阵型穿透目标：正前方锥形区（±~33°）内**最远**的存活敌人——冲锋线贯穿整条敌阵，
        /// 途经前排（盾牌背向不格挡）→ 终点爆发波及后排弓手/脆皮。要求直线全程可走且不被建筑遮挡
        /// （穿透冲锋必须真正到达目标，建筑挡路的深目标排除，否则冲锋被夹回、打不到后排）。
        /// 无锥形区目标 → false（回退大脑目标/就近扫描）。</summary>
        bool FindDeepestEnemyInCone(out Vector3 dir, out Agent target, bool log)
        {
            dir = Vector3.zero;
            target = null;
            if (_agent == null || !_agent.navPos.valid) return false;
            // 锥形基准 = 阵型朝向（盾线面对方向）；拿不到则用自身朝向（阵型钉位已让矛朝敌）
            Vector3 fwd;
            Vector3? facing = TacticalFormation.GetFormationFacing(_agent);
            if (facing.HasValue && facing.Value.sqrMagnitude > 0.001f)
                fwd = facing.Value;
            else
            {
                fwd = _agent.transform.forward; fwd.y = 0f;
            }
            if (fwd.sqrMagnitude < 0.001f) fwd = _agent.transform.forward;
            fwd.Normalize();

            int n = Physics.OverlapSphereNonAlloc(_agent.transform.position, DetectionRadius, _hitBuffer, ~0, QueryTriggerInteraction.Ignore);
            Agent best = null; float bestDist = 0f;
            for (int i = 0; i < n; i++)
            {
                var c = _hitBuffer[i];
                if (c == null) continue;
                var a = c.GetComponentInParent<Agent>();
                if (a == null || a == _agent || a.isViking) continue;
                if (a.aliveState != null && !a.aliveState.active) continue;
                if (!a.navPos.valid) continue;
                // 穿透冲锋必须整条直线可走且无建筑遮挡（IsStraightPathWalkable 含建筑判定），
                // 保证冲锋线能贯穿到该目标；被建筑挡路的深目标排除（否则被夹回、终点打不到后排）。
                if (!IsStraightPathWalkable(_agent.navPos.wPos, a.navPos.wPos)) continue;
                Vector3 to = a.transform.position - _agent.transform.position; to.y = 0f;
                if (to.sqrMagnitude < 0.01f) continue;
                to.Normalize();
                if (Vector3.Dot(fwd, to) < 0.55f) continue;   // 正前方锥形区（≈±33°）
                float dist = Vector3.Distance(_agent.transform.position, a.transform.position);
                if (dist < 0.8f) continue;   // 太近（已贴脸）不选——让给普通刺击
                if (dist > bestDist) { bestDist = dist; best = a; }   // 最远 = 贯穿到底
            }
            if (best == null) return false;
            dir = (best.transform.position - _agent.transform.position);
            dir.y = 0f;
            dir.Normalize();
            target = best;
            if (log) BSLog.Info("[Charge] 阵型穿透目标=" + best.name + " 距离=" + bestDist.ToString("F2") +
                "m（锥形区最远=贯穿敌阵，终点爆发打后排）");
            return true;
        }

        /// <summary>地形可走性：判断世界点是否位于主岛可走导航网格上。
        /// 用游戏权威判定 NavPos.MoveTo（NavPos 是 struct，副本调用不影响真实 navPos）：
        /// 反编译 IL 确认 MoveTo 返回 (bestDist == 0f)——目标点真正落在可走三角形内才为 true；
        /// 落在网格外（水面/悬崖等）时返回 false。彻底避免旧"贴回网格偏移容差"对岸边点失效的问题。</summary>
        bool IsPointWalkable(Vector3 p)
        {
            if (_agent == null || !_agent.navPos.valid || !_agent.navPos.onMain) return false;
            NavPos np = _agent.navPos;   // struct 副本，测试不影响真实 navPos
            if (np.transform == null) return false;
            Vector3 local;
            try { local = np.transform.InverseTransformPoint(p); }
            catch { return false; }
            try { return np.MoveTo(local); }
            catch { return false; }
        }

        /// <summary>建筑遮挡：世界点周围 BuildingBlockRadius 内是否有房屋（完好/燃烧中/已烧毁残骸均算）。
        /// 碰撞体检测为主，兜底用 House.bounds（Setup 时由碰撞体角点算出的世界包围盒，保留原占地）
        /// 覆盖"烧毁后碰撞体被禁用"的残骸情况。</summary>
        bool IsPointBlockedByHouse(Vector3 p)
        {
            // ① 物理碰撞体：完好/燃烧中/已烧毁（若碰撞体仍启用）的房屋都算遮挡。
            if (_hitBuffer != null)
            {
                int n = Physics.OverlapSphereNonAlloc(p, BuildingBlockRadius, _hitBuffer, ~0, QueryTriggerInteraction.Collide);
                for (int i = 0; i < n; i++)
                {
                    Collider c = _hitBuffer[i];
                    if (c == null) continue;
                    if (c.GetComponentInParent<House>() != null) return true;
                }
            }
            // ② 兜底：遍历场景内所有 House 的世界包围盒（XZ 平面 + BuildingBlockRadius 膨胀）——
            // 烧毁后残骸同样视为遮挡（排除预制件资产与其零包围盒，避免原点误判）。
            if (Time.time - _houseCacheTime > 2f)
            {
                _houseCacheTime = Time.time;
                _houseCache.Clear();
                try
                {
                    House[] all = Resources.FindObjectsOfTypeAll<House>();
                    for (int i = 0; i < all.Length; i++)
                    {
                        House h = all[i];
                        if (h == null || !h.gameObject.scene.IsValid()) continue;   // 排除预制件资产
                        if (h.bounds.size.sqrMagnitude < 0.001f) continue;           // 排除无效包围盒
                        _houseCache.Add(h);
                    }
                }
                catch { }
            }
            for (int i = 0; i < _houseCache.Count; i++)
            {
                House h = _houseCache[i];
                if (h == null) continue;
                Vector3 c = h.bounds.center;
                Vector3 e = h.bounds.extents;
                if (Mathf.Abs(p.x - c.x) <= e.x + BuildingBlockRadius &&
                    Mathf.Abs(p.z - c.z) <= e.z + BuildingBlockRadius)
                    return true;
            }
            return false;
        }

        /// <summary>地形通畅检查（只判导航网格可走，不判建筑）：起点到终点直线全程位于主岛可走网格上。
        /// 用于冲锋目标选择——悬崖/水面后的目标排除，建筑后的目标保留（冲锋撞建筑前停但能冲出距离）。</summary>
        bool IsTerrainPathClear(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            dir.y = 0f;
            float total = dir.magnitude;
            if (total < 0.01f) return true;
            dir /= total;
            for (float d = 0f; d <= total + WalkableStep * 0.5f; d += WalkableStep)
            {
                if (!IsPointWalkable(from + dir * d)) return false;
            }
            return true;
        }

        /// <summary>建筑/地形遮挡检查：起点到终点锁定格的直线必须全程位于主岛可走导航网格上；
        /// 直线中段（起终点各留 BuildingBlockRadius 余量，避免"贴墙站/贴墙打"误判）被房屋/残骸占据
        /// 同样判定遮挡。用于发起冲锋前的拦截：单位在建筑或不可接触地形后面 → 不释放技能。</summary>
        bool IsStraightPathWalkable(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            dir.y = 0f;
            float total = dir.magnitude;
            if (total < 0.01f) return true;
            dir /= total;
            for (float d = 0f; d <= total + WalkableStep * 0.5f; d += WalkableStep)
            {
                Vector3 p = from + dir * d;
                if (!IsPointWalkable(p)) return false;
                if (d > BuildingBlockRadius && total - d > BuildingBlockRadius && IsPointBlockedByHouse(p)) return false;
            }
            return true;
        }

        /// <summary>终点夹回：沿 dir 方向从 from 起逐点采样，返回最后一个可走的距离。
        /// 地形（IsPointWalkable）全程生效，边界处二分细化并用 WalkableEndMargin 内收，确保终点
        /// 稳稳落在岸上而非踩线/出海；建筑只对越过锁定目标格（targetDist）之后的穿透段判定，
        /// 起终点附近各留 BuildingBlockRadius 余量。至少保证冲锋能到达目标锁定格。</summary>
        float MaxWalkableDistAlongRay(Vector3 from, Vector3 dir, float maxDist, float targetDist)
        {
            if (dir.sqrMagnitude < 0.0001f) return maxDist;
            float last = 0f;
            float firstBad = -1f;
            bool terrainBreak = false;
            for (float d = 0f; d <= maxDist + WalkableStep * 0.5f; d += WalkableStep)
            {
                Vector3 p = from + dir * d;
                if (!IsPointWalkable(p)) { firstBad = d; terrainBreak = true; break; }
                if (d > BuildingBlockRadius && d > targetDist + BuildingBlockRadius && IsPointBlockedByHouse(p)) { firstBad = d; break; }
                last = d;
            }
            if (firstBad < 0f) return Mathf.Max(last, targetDist);   // 全程可走，无需夹回
            float edge = last;
            if (terrainBreak)
            {
                // 二分细化：在 [last, firstBad] 间找精确的可走/不可走交界
                float lo = last, hi = firstBad;
                for (int k = 0; k < 6; k++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (IsPointWalkable(from + dir * mid)) lo = mid; else hi = mid;
                }
                edge = lo;
            }
            float end = terrainBreak ? edge - WalkableEndMargin : last;   // 地形→岸上内收；建筑→检测边界前
            if (firstBad <= targetDist + BuildingBlockRadius)
                return Mathf.Max(Mathf.Min(2.5f, targetDist), end);   // 边界在目标格前：至少冲 min(2.5m,目标距) 威慑距离
            return Mathf.Max(targetDist, end);     // 至少到达目标，穿透段夹回岸上/建筑前
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
