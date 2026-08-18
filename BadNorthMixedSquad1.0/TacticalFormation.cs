using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthMixedSquad1_0
{
    /// <summary>M2 战术分层站位（挂在混编 Longship 上）：盾前/矛中/弓后 三列 + 防御状态机。
    /// HOLD（敌远）：全队走向格位、朝敌站桩（覆盖大脑 walkDir = 压制"冲建筑"）；
    /// ENGAGE（敌入范围）：盾兵保持前排钉位顶线（原地近战=稳住阵脚），矛/弓放开各自作战。
    /// 死亡收拢；登岛(onMain)后才激活，船上让 Pirate 正常下船。</summary>
    public class TacticalFormation : MonoBehaviour
    {
        const float EngageRange = 14f;   // 进入此范围 → 交战
        const float LookRange = 40f;     // 更远范围找敌人定朝向
        const float Spacing = 0.45f;     // 同排横向间距
        const float RowGap = 0.5f;       // 前后排间距（盾→矛→弓）
        const float SlotArrive = 0.2f;   // 距格位 < 此值 → 站桩

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
            if (_engaged) EngageUpdate();
            else HoldUpdate();
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
            for (int i = 0; i < _shields.Count; i++) MoveToSlot(_shields[i], 0, _shields.Count, i, true);
            for (int i = 0; i < _spears.Count; i++) MoveToSlot(_spears[i], 1, _spears.Count, i, true);
            for (int i = 0; i < _archers.Count; i++) MoveToSlot(_archers[i], 2, _archers.Count, i, true);
        }

        void EngageUpdate()
        {
            // 盾兵保持前排钉位（稳住阵脚、原地近战）；弓手钉后列（后排射击不贴脸）；矛放开冲锋/刺击
            for (int i = 0; i < _shields.Count; i++) MoveToSlot(_shields[i], 0, _shields.Count, i, true);
            for (int i = 0; i < _archers.Count; i++) MoveToSlot(_archers[i], 2, _archers.Count, i, true);
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
    }
}
