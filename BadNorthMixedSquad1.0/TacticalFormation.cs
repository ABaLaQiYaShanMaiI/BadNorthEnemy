using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthMixedSquad1_0
{
    /// <summary>M2/M4 战术分层站位（挂在混编 Longship 上）：盾前/矛中/弓后 三列 + 战斗相位状态机。
    /// HOLD（敌远）：全队走向格位、朝敌站桩（覆盖大脑 walkDir = 压制"冲建筑"）；
    /// M4 顺序联动（EnableWaveCharge=true）：盾线接敌(盾顶线) → 弓手压制(集火点射) →
    ///   敌逼近盾线 → 同船矛兵错峰冲阵 → 重整回盾后。false 退化为旧 ENGAGE（矛自由冲锋）。
    /// 死亡收拢；登岛(onMain)后才激活，船上让 Pirate 正常下船。</summary>
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

        void LateUpdate()
        {
            if (_ship == null) return;
            RemoveDead();
            if (_agents.Count == 0) return;

            // 登岛(onMain)后才列阵——在船上时让 Pirate 正常下船
            bool onMain = false;
            for (int i = 0; i < _agents.Count; i++)
            {
                var a = _agents[i];
                if (a.navPos.valid && a.navPos.onMain) { onMain = true; break; }
            }
            if (!onMain) return;

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
            // 盾兵保持前排钉位（稳住阵脚、原地近战）；弓手钉后列（后排射击不贴脸）；矛放开冲锋/刺击
            for (int i = 0; i < _shields.Count; i++) MoveToSlot(_shields[i], 0, _shields.Count, i, true);
            for (int i = 0; i < _archers.Count; i++) MoveToSlot(_archers[i], 2, _archers.Count, i, true);
        }

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

            // 站位：盾顶线、弓后列始终；矛只在冲阵期间放开，其余相位钉中列待命
            for (int i = 0; i < _shields.Count; i++) MoveToSlot(_shields[i], 0, _shields.Count, i, true);
            for (int i = 0; i < _archers.Count; i++) MoveToSlot(_archers[i], 2, _archers.Count, i, true);
            if (_battlePhase != BattlePhase.ChargeWave)
                for (int i = 0; i < _spears.Count; i++) MoveToSlot(_spears[i], 1, _spears.Count, i, true);
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
    }
}
