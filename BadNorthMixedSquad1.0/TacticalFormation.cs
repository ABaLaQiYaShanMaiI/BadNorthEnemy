using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthMixedSquad1_0
{
    /// <summary>M2/M4/M5 战术分层站位（挂在混编 Longship 上）：盾前/矛中/弓后 三列 + 战斗相位状态机。
    /// HOLD（敌远）：全队走向格位、朝敌站桩（覆盖大脑 walkDir = 压制"冲建筑"）；
    /// M4 顺序联动（EnableWaveCharge=true）：盾线接敌(盾顶线) → 弓手压制(集火点射) →
    ///   敌逼近盾线 → 同船矛兵错峰冲阵 → 重整回盾后。false 退化为旧 ENGAGE（矛自由冲锋）。
    /// 死亡收拢；船上阶段（EnableShipShieldFront）盾前挡箭 + 甲板重排；登岛(onMain)后才列阵。</summary>
    public class TacticalFormation : MonoBehaviour
    {
        const float EngageRange = 14f;   // 进入此范围 → 交战
        const float ArcherFocusRadius = 26f;  // 弓手集火目标搜索半径（弓手射程内）
        const float LookRange = 40f;     // 更远范围找敌人定朝向
        const float Spacing = 0.45f;     // 同排横向间距
        const float RowGap = 0.5f;       // 前后排间距（盾→矛→弓）
        const float SlotArrive = 0.2f;   // 距格位 < 此值 → 站桩

        // ===== M4 顺序联动（盾→弓→矛）：战斗相位 =====
        const float WaveIntervalDefault = 0.15f;  // 矛兵错峰冲锋间隔（冲阵波次）
        const float ReformTime = 12f;       // 冲阵后重整时长（≈冲锋冷却节奏，给玩家反打窗口）
        enum BattlePhase { Hold, ShieldBrace, ChargeWave, Reform }
        BattlePhase _battlePhase = BattlePhase.Hold;
        float _waveTimer;                   // 错峰发令计时
        int _waveElapsed;                   // 已发令的矛数
        float _reformTimer;                 // 重整计时
        float _chargeTriggerDist = 5f;      // 敌距盾线触发冲锋（cfg ChargeTriggerDist）

        Longship _ship;
        readonly List<Agent> _agents = new List<Agent>();
        readonly List<Agent> _shields = new List<Agent>();
        readonly List<Agent> _spears = new List<Agent>();
        readonly List<Agent> _archers = new List<Agent>();
        bool _engaged;
        Vector3 _anchor;
        Vector3 _facing = Vector3.forward;
        Vector3 _shipFront = Vector3.zero;      // 船上朝向（船头=敌岛方向）：甲板重排 + 挡箭朝向基准
        bool _arrangedOnShip;                   // 甲板重排（盾前矛中弓后）是否已完成（每船一次）
        static readonly List<TacticalFormation> _active = new List<TacticalFormation>();   // 全场景活跃阵型（供矛兵回退查询格位）

        void OnEnable() { if (!_active.Contains(this)) _active.Add(this); }
        void OnDisable() { _active.Remove(this); }

        public void Setup(Longship ship, List<Agent> mixedAgents)
        {
            _ship = ship;
            _agents.Clear(); _shields.Clear(); _spears.Clear(); _archers.Clear();
            if (mixedAgents == null) return;
            foreach (var a in mixedAgents)
            {
                if (a == null) continue;
                _agents.Add(a);
                var role = a.GetComponent<MixedRole>();
                if (role == null) continue;
                if (role.role == MixedRoleType.Shield) _shields.Add(a);
                else if (role.role == MixedRoleType.Spear) _spears.Add(a);
                else if (role.role == MixedRoleType.Archer) _archers.Add(a);
            }
        }

        // ===== 船上阶段（M5）：盾前挡箭 =====

        /// <summary>敌舰接近（尚未登岛）阶段：甲板按 盾前/矛中/弓后 重排一次 + 全体面朝敌岛（盾牌挡箭）。
        /// 只调朝向、不动 walkDir（不干扰 Pirate 下船）；登岛后由列阵逻辑接管。</summary>
        void OnShipUpdate()
        {
            if (_agents.Count == 0) return;
            // 1) 甲板重排（每船一次）：盾兵换到船头最前、矛中、弓后
            if (!_arrangedOnShip)
            {
                if (_shipFront == Vector3.zero) _shipFront = ComputeShipFront();
                if (_shipFront != Vector3.zero)
                {
                    ArrangeOnShip(_shipFront);
                    _arrangedOnShip = true;   // 尽力而为：失败也标记，避免每帧重试刷屏
                }
            }
            // 2) 全体面朝敌岛（箭矢来向）——盾牌举在前，挡玩家弓手
            Vector3 front = _shipFront != Vector3.zero ? _shipFront : _facing;
            if (front == Vector3.zero) return;
            for (int i = 0; i < _agents.Count; i++)
            {
                var a = _agents[i];
                if (a == null) continue;
                if (!a.navPos.valid || a.navPos.onMain) continue;
                a.LookInDirection(front, 720f, 20f);
            }
        }

        /// <summary>船头方向 = 船 → 最近玩家单位（敌岛方向，船头驶向主岛）。无玩家单位 → zero。</summary>
        Vector3 ComputeShipFront()
        {
            try
            {
                if (_agents.Count == 0) return Vector3.zero;
                var faction = _agents[0].faction;
                if (faction == null || faction.enemy == null) return Vector3.zero;
                Vector3 center = _ship != null ? _ship.transform.position : _agents[0].transform.position;
                center.y = 0f;
                var list = AgentEnumerators.GetStaticListRadius(center, 300f, faction.enemy);
                Agent best = null;
                float bestD = float.MaxValue;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (e == null) continue;
                    Vector3 d = e.transform.position - center; d.y = 0f;
                    float dd = d.sqrMagnitude;
                    if (dd < bestD) { bestD = dd; best = e; }
                }
                if (best == null) return Vector3.zero;
                Vector3 dir = best.transform.position - center; dir.y = 0f;
                return dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.zero;
            }
            catch { return Vector3.zero; }
        }
        /// <summary>甲板重排：把同船混编单位按 盾(船头)/矛(中)/弓(船尾) 交换 navPos 站位。
        /// 已满足盾全在最前则不动；NavPos 是 struct，先快照再赋值防串位。</summary>
        void ArrangeOnShip(Vector3 front)
        {
            if (_ship == null || _agents.Count < 2) return;
            int n = _agents.Count;
            Vector3 center = Vector3.zero;
            int valid = 0;
            for (int i = 0; i < n; i++)
            {
                var a = _agents[i];
                if (a == null || !a.navPos.valid) continue;
                center += a.transform.position; valid++;
            }
            if (valid < 2)
            {
                BSLog.Warn("[船上] 甲板站位不可用（valid=" + valid + "），跳过重排");
                return;
            }
            center /= valid; center.y = 0f;

            // 按投影从大到小排序 = 船头→船尾（简单插入排序，避 LINQ）
            var slots = new List<Agent>(n);
            var proj = new List<float>(n);
            for (int i = 0; i < n; i++)
            {
                var a = _agents[i];
                if (a == null || !a.navPos.valid) continue;
                Vector3 p = a.transform.position - center; p.y = 0f;
                slots.Add(a);
                proj.Add(Vector3.Dot(p, front));
            }
            for (int i = 1; i < slots.Count; i++)
            {
                Agent ai = slots[i]; float pi = proj[i];
                int j = i - 1;
                while (j >= 0 && proj[j] < pi)
                {
                    slots[j + 1] = slots[j]; proj[j + 1] = proj[j]; j--;
                }
                slots[j + 1] = ai; proj[j + 1] = pi;
            }

            // 目标布局：盾(最前) + 矛(中) + 弓(后) + 未分类(最后)
            var target = new List<Agent>(slots.Count);
            AddRoleList(target, _shields);
            AddRoleList(target, _spears);
            AddRoleList(target, _archers);
            for (int i = 0; i < n; i++)
                if (!target.Contains(_agents[i])) target.Add(_agents[i]);
            if (target.Count != slots.Count)
            {
                BSLog.Warn("[船上] 重排目标与站位数量不一致（" + target.Count + " != " + slots.Count + "），跳过");
                return;
            }

            // 已满足（前 shield 格全是盾）→ 不折腾
            bool alreadyOk = true;
            for (int i = 0; i < slots.Count; i++)
            {
                bool isShield = IsShield(slots[i]);
                bool shouldBeShield = i < _shields.Count;
                if (isShield != shouldBeShield) { alreadyOk = false; break; }
            }
            if (alreadyOk)
            {
                BSLog.Info("[船上] 甲板已是 盾前/矛中/弓后，跳过重排（count=" + slots.Count + "）");
                return;
            }

            var snap = new NavPos[slots.Count];
            for (int i = 0; i < slots.Count; i++) snap[i] = slots[i].navPos;
            int moved = 0;
            for (int i = 0; i < target.Count; i++)
            {
                var mover = target[i];
                if (ReferenceEquals(mover, slots[i])) continue;
                try
                {
                    if (!snap[i].valid) continue;
                    mover.navPos = snap[i];
                    mover.transform.position = snap[i].wPos;
                    moved++;
                }
                catch (Exception e) { BSLog.Warn("[船上] 甲板换位异常: " + e.Message); }
            }
            BSLog.Info("[船上] 甲板重排 盾" + _shields.Count + "/矛" + _spears.Count + "/弓" + _archers.Count +
                " 前→后，移动 " + moved + " 格（front=" + front.ToString("F2") + "）");
        }
        static void AddRoleList(List<Agent> target, List<Agent> roleList)
        {
            for (int i = 0; i < roleList.Count; i++)
                if (roleList[i] != null && !target.Contains(roleList[i])) target.Add(roleList[i]);
        }

        static bool IsShield(Agent a)
        {
            var role = a != null ? a.GetComponent<MixedRole>() : null;
            return role != null && role.role == MixedRoleType.Shield;
        }

        void LateUpdate()
        {
            if (_ship == null) return;
            RemoveDead();
            if (_agents.Count == 0) return;

            // 登岛(onMain)后才列阵；船上阶段走 OnShipUpdate（盾前挡箭 + 甲板重排），Pirate 下船不受干扰
            bool onMain = false;
            for (int i = 0; i < _agents.Count; i++)
            {
                var a = _agents[i];
                if (a.navPos.valid && a.navPos.onMain) { onMain = true; break; }
            }
            if (!onMain)
            {
                if (ModConfig.EnableShipShieldFront != null && ModConfig.EnableShipShieldFront.Value)
                    OnShipUpdate();
                return;
            }

            UpdateAnchorAndFacing();
            _engaged = HasEnemiesNear(EngageRange);
            if (!_engaged)
            {
                _battlePhase = BattlePhase.Hold;   // 敌远 → 回待命
                HoldUpdate();
                return;
            }
            bool wave = ModConfig.EnableWaveCharge != null && ModConfig.EnableWaveCharge.Value;
            if (wave) WaveEngageUpdate();   // M4 顺序联动（盾扛→弓射→矛冲）
            else EngageUpdate();            // 旧行为：盾/弓钉位、矛自由
        }

        void RemoveDead()
        {
            for (int i = _agents.Count - 1; i >= 0; i--)
            {
                var a = _agents[i];
                if (a == null || a.aliveState == null || !a.aliveState.active) _agents.RemoveAt(i);
            }
            _shields.Clear(); _spears.Clear(); _archers.Clear();
            foreach (var a in _agents)
            {
                var role = a.GetComponent<MixedRole>();
                if (role == null) continue;
                if (role.role == MixedRoleType.Shield) _shields.Add(a);
                else if (role.role == MixedRoleType.Spear) _spears.Add(a);
                else if (role.role == MixedRoleType.Archer) _archers.Add(a);
            }
        }

        void UpdateAnchorAndFacing()
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < _agents.Count; i++)
            {
                if (_agents[i] == null) continue;
                sum += _agents[i].wPos; n++;
            }
            _anchor = n > 0 ? sum / n : _ship.transform.position;
            _anchor.y = 0f;

            Agent enemy = NearestEnemy(LookRange);
            if (enemy != null)
            {
                Vector3 d = enemy.transform.position - _anchor; d.y = 0f;
                if (d.sqrMagnitude > 0.001f) _facing = d.normalized;
            }
        }

        Agent NearestEnemy(float radius)
        {
            if (_agents.Count == 0) return null;
            var faction = _agents[0].faction;
            if (faction == null || faction.enemy == null) return null;
            var list = AgentEnumerators.GetStaticListRadius(_anchor, radius, faction.enemy);
            Agent best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null) continue;
                float d = Vector3.SqrMagnitude(e.transform.position - _anchor);
                if (d < bestD) { bestD = d; best = e; }
            }
            return best;
        }

        bool HasEnemiesNear(float radius)
        {
            return NearestEnemy(radius) != null;
        }

        void HoldUpdate()
        {
            ArcherCombat.FocusTarget = null;   // 未接敌不集火
            for (int i = 0; i < _shields.Count; i++) MoveToSlot(_shields[i], 0, _shields.Count, i, true);
            for (int i = 0; i < _spears.Count; i++) MoveToSlot(_spears[i], 1, _spears.Count, i, true);
            for (int i = 0; i < _archers.Count; i++) MoveToSlot(_archers[i], 2, _archers.Count, i, true);
        }

        void EngageUpdate()
        {
            UpdateArcherFocus();   // 交战中选集火目标（低血量敌人）→ 同船弓手点射
            // 盾兵保持前排钉位（稳住阵脚、原地近战）；弓手钉后列（后排射击不贴脸）；
            // 矛兵自由冲锋（非波次模式），一旦非冲阵（回撤/冷却/待机）就钉回盾后中列 → 回马枪
            for (int i = 0; i < _shields.Count; i++) MoveToSlot(_shields[i], 0, _shields.Count, i, true);
            for (int i = 0; i < _archers.Count; i++) MoveToSlot(_archers[i], 2, _archers.Count, i, true);
            PinSpearsWhenIdle();
        }

        /// <summary>把非冲阵中（未在 起手/冲刺/回撤）的矛兵钉回盾后中列：冲锋一结束立即回阵，
        /// 保证"返回后剑盾兵在前、长矛兵在后"对单位发起进攻。</summary>
        void PinSpearsWhenIdle()
        {
            for (int i = 0; i < _spears.Count; i++)
            {
                var sp = _spears[i];
                if (sp == null) continue;
                var ch = sp.GetComponent<SpearChargeComponent>();
                if (ch == null || !ch.IsChargeBusy())
                    MoveToSlot(sp, 1, _spears.Count, i, true);
            }
        }

        /// <summary>盾后架矛：对中列每个非冲阵矛兵，若射程内有存活敌人就**直接启动长矛刺击**。
        /// 与 Plugin.SwordsmanAttackPrefix 同链路（attack.SetActive + target + 音效 + MaybeParry +
        /// NotifyMeleeAttackStart），AttackUpdate 前缀会接管刺击周期并自动连刺。
        /// 保守守卫：aliveAndGrounded / 未在攻击 / 未冲阵 / 距离校验 / 非滑动（防眩晕僵硬期误触发）。</summary>
        void CommandLanceStabs()
        {
            for (int i = 0; i < _spears.Count; i++)
            {
                var sp = _spears[i];
                if (sp == null) continue;
                var ch = sp.GetComponent<SpearChargeComponent>();
                if (ch != null && ch.IsChargeBusy()) continue;   // 冲阵/起手/回撤中不架矛
                if (ch != null && ch.IsChargeOrdered()) continue;   // 已收到冲阵号令 → 不再启动刺击，避免"刺一半收手转冲锋"卡顿
                var sw = sp.GetComponent<Swordsman>();
                if (sw == null || sw.attack == null || sw.attack.active) continue;   // 已在刺击
                if (Plugin.GetSwordsmanStamina(sw) < 0.45f) continue;   // 体力不足一次刺击（cost 0.5）→ 尊重原版攻速/恢复节奏
                if (sp.aliveAndGrounded == null || !sp.aliveAndGrounded.active) continue;
                if (sp.body != null && sp.body.sliding != null && sp.body.sliding.active) continue;   // 滑退/眩晕期不刺
                if (sp.navPos == null || !sp.navPos.valid || !sp.navPos.onMain) continue;

                Agent enemy = (sw.target != null && sw.target.aliveState != null && sw.target.aliveState.active)
                    ? sw.target : sp.enemyAgent;
                if (enemy == null || enemy.aliveState == null || !enemy.aliveState.active) continue;
                if (enemy.navPos == null || !enemy.navPos.valid) continue;
                // 距离校验：radius + targetRadius + range（range 已含阵型架矛加成 FormationLanceReach）
                float num = sp.radius + enemy.radius + sw.range;
                if ((enemy.navPos.pos - sp.navPos.pos).sqrMagnitude > num * num) continue;
                try
                {
                    sw.attack.SetActive(true);
                    sw.target = enemy;
                    try { IslandGameplayManager.RequestCombatAudio(sw.swingSound, sp.gameObject); } catch { }
                    var enemySw = enemy.brain as Swordsman;
                    if (enemySw != null && enemySw.shield != null)
                    {
                        try { enemySw.shield.MaybeParry(sw); } catch { }
                    }
                    SpearChargeComponent.NotifyMeleeAttackStart(sp);
                    if (Time.time - _lastLanceLog > 5f)
                    {
                        _lastLanceLog = Time.time;
                        BSLog.Info("[阵型] 盾后架矛: 中列矛兵刺击 " + enemy.name + "（叠刺输出，矛尖越过盾线）");
                    }
                }
                catch (Exception e) { BSLog.Warn("[阵型] 架矛刺击异常: " + e); }
            }
        }

        float _lastLanceLog = -999f;

        /// <summary>M4 顺序联动：盾线接敌 → 弓手压制 → 敌逼近盾线 → 同船矛兵错峰冲阵 → 重整回盾后。</summary>
        void WaveEngageUpdate()
        {
            UpdateArcherFocus();   // 弓手集火（低血量目标）
            if (ModConfig.ChargeTriggerDist != null)
                _chargeTriggerDist = ModConfig.ChargeTriggerDist.Value;
            float interval = ModConfig.WaveInterval != null ? ModConfig.WaveInterval.Value : WaveIntervalDefault;
            if (interval <= 0f) interval = WaveIntervalDefault;

            switch (_battlePhase)
            {
                case BattlePhase.Hold:
                case BattlePhase.Reform:
                    _battlePhase = BattlePhase.ShieldBrace;   // 接敌 → 盾线顶住、矛待命
                    break;

                case BattlePhase.ShieldBrace:
                    // 关联性触发：敌逼近盾线（< 触发距离）→ 冲阵号令
                    if (NearestEnemyDist() <= _chargeTriggerDist)
                    {
                        _battlePhase = BattlePhase.ChargeWave;
                        _waveTimer = 0f;
                        _waveElapsed = 0;
                    }
                    break;

                case BattlePhase.ChargeWave:
                    _waveTimer -= Time.deltaTime;
                    if (_waveElapsed < _spears.Count && _waveTimer <= 0f)
                    {
                        var sp = _spears[_waveElapsed].GetComponent<SpearChargeComponent>();
                        if (!ReferenceEquals(sp, null)) sp.OrderCharge();   // 逐个错峰发令 → 冲击浪
                        _waveElapsed++;
                        _waveTimer = interval;
                    }
                    if (_waveElapsed >= _spears.Count)   // 全部发令 → 重整
                    {
                        _battlePhase = BattlePhase.Reform;
                        _reformTimer = ReformTime;
                    }
                    break;
            }

            if (_battlePhase == BattlePhase.Reform)
            {
                _reformTimer -= Time.deltaTime;
                if (_reformTimer <= 0f) _battlePhase = BattlePhase.ShieldBrace;   // 重整完回接敌
            }

            // 站位：盾顶线、弓后列始终；矛只在冲阵期间（起手/冲刺/回撤=busy）放开，其余时刻钉回盾后中列（回马枪）
            for (int i = 0; i < _shields.Count; i++) MoveToSlot(_shields[i], 0, _shields.Count, i, true);
            for (int i = 0; i < _archers.Count; i++) MoveToSlot(_archers[i], 2, _archers.Count, i, true);
            PinSpearsWhenIdle();
            // 盾后架矛（致命化核心）：接敌/重整阶段中列矛兵矛尖越过盾线，对射程内敌人持续刺击——
            // 盾线顶住仇恨、矛兵叠刺输出，不再"只等冲锋的工具人"。直接启动 Swordsman 攻击状态
            // （走 SwordsmanAttackPrefix 同款链路：矛刺周期由 AttackUpdate 前缀接管与连刺）。
            CommandLanceStabs();
        }

        /// <summary>最近敌人到阵型锚点（≈盾线）的距离。无 → float.MaxValue。</summary>
        float NearestEnemyDist()
        {
            Agent e = NearestEnemy(LookRange);
            if (e == null) return float.MaxValue;
            return Vector3.Distance(e.transform.position, _anchor);
        }

        /// <summary>选集火目标：弓手射程内 HP 最低的存活敌人（点杀脆皮）。无 → 清空。</summary>
        void UpdateArcherFocus()
        {
            if (!ArcherCombat.Enabled)
            {
                ArcherCombat.FocusTarget = null;
                return;
            }
            var faction = _agents[0].faction;
            if (faction == null || faction.enemy == null)
            {
                ArcherCombat.FocusTarget = null;
                return;
            }
            var list = AgentEnumerators.GetStaticListRadius(_anchor, ArcherFocusRadius, faction.enemy);
            Agent best = null;
            float bestHp = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null) continue;
                if (e.aliveState == null || !e.aliveState.active) continue;
                float hp = e.health;
                if (hp < bestHp) { bestHp = hp; best = e; }
            }
            ArcherCombat.FocusTarget = best;
        }

        void MoveToSlot(Agent a, int row, int rowCount, int index, bool holdWhenArrived)
        {
            if (a == null) return;
            Vector3 right = new Vector3(_facing.z, 0f, -_facing.x);   // 朝向的水平右向
            float x = (index - (rowCount - 1) * 0.5f) * Spacing;
            Vector3 slot = _anchor + right * x + _facing * (row * RowGap);
            slot.y = a.wPos.y;
            Vector3 toSlot = slot - a.wPos; toSlot.y = 0f;
            if (toSlot.sqrMagnitude > SlotArrive * SlotArrive)
            {
                a.walkDir = toSlot.normalized;      // 走向格位
                a.LookInDirection(toSlot.normalized, 720f, 20f);
            }
            else if (holdWhenArrived)
            {
                a.walkDir = Vector3.zero;           // 到格位 → 站桩朝敌
                a.LookInDirection(_facing, 720f, 20f);
            }
        }

        /// <summary>返回某 agent 在本阵型中的格位（按其角色行 + 行内序号）。不在本阵型 → null。</summary>
        public Vector3? GetSlot(Agent a)
        {
            if (a == null || _anchor == Vector3.zero) return null;
            List<Agent> rowList; int row;
            if (_shields.Contains(a)) { rowList = _shields; row = 0; }
            else if (_spears.Contains(a)) { rowList = _spears; row = 1; }
            else if (_archers.Contains(a)) { rowList = _archers; row = 2; }
            else return null;
            int index = rowList.IndexOf(a);
            if (index < 0) return null;
            Vector3 right = new Vector3(_facing.z, 0f, -_facing.x);
            float x = (index - (rowList.Count - 1) * 0.5f) * Spacing;
            return _anchor + right * x + _facing * (row * RowGap);
        }

        /// <summary>跨阵型查询：某 agent 若属于某个活跃阵型，返回其格位（供矛兵冲锋回退落点联动）。</summary>
        public static Vector3? GetFormationSlot(Agent a)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                var f = _active[i];
                if (f == null) continue;
                var slot = f.GetSlot(a);
                if (slot.HasValue) return slot;
            }
            return null;
        }

        /// <summary>某 agent 是否属于任意活跃阵型（供 SpearChargeComponent 判断是否受"冲阵号令"门控）。</summary>
        public static bool InFormation(Agent a)
        {
            if (a == null) return false;
            for (int i = 0; i < _active.Count; i++)
            {
                var f = _active[i];
                if (f == null) continue;
                if (f.GetSlot(a).HasValue) return true;
            }
            return false;
        }

        /// <summary>某 agent 所属阵型的朝向（盾线面对方向）。不在阵型/未初始化 → null。
        /// 供 SpearChargeComponent 阵型穿透目标选择（锥形区基准）使用。</summary>
        public static Vector3? GetFormationFacing(Agent a)
        {
            if (a == null) return null;
            for (int i = 0; i < _active.Count; i++)
            {
                var f = _active[i];
                if (f == null || f._agents.Count == 0) continue;
                if (f.GetSlot(a).HasValue)
                    return f._facing.sqrMagnitude > 0.0001f ? f._facing : (Vector3?)null;
            }
            return null;
        }
    }
}
