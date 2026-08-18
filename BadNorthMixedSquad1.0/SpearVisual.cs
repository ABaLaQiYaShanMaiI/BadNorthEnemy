using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthMixedSquad1_0
{
    /// <summary>
    /// 长矛朝向统一工具：冲锋、刺击、普通攻击共用同一套举矛公式（原版 Spear.LateUpdate：
    /// LookRotation(矛尖方向, up) * Euler(0,0,90)），避免各处写法不一导致矛的观感不一致。
    /// </summary>
    public static class SpearVisual
    {
        const string SpearName = "Spear_BlackSpearman";

        public static Transform FindSpear(Agent agent)
        {
            if (agent == null || agent.transform == null) return null;
            return agent.transform.Find(SpearName);
        }

        /// <summary>
        /// 立即把长矛指向 targetWorldPos（普通攻击/刺击用，直接 snap 不插值）。
        /// 返回是否成功（找到长矛且方向有效）。
        /// </summary>
        public static bool AimAt(Agent agent, Vector3 targetWorldPos)
        {
            if (agent == null) return false;
            var spear = FindSpear(agent);
            if (spear == null) return false;
            if (!TryGetAimRotation(agent, targetWorldPos, out Quaternion rot)) return false;
            spear.rotation = rot;
            return true;
        }

        /// <summary>
        /// 计算长矛应朝向的目标旋转（举矛公式），不立即应用 —— 冲锋技能用，
        /// 由 SpearChargeComponent.LateUpdate 对它做 Slerp 平滑插值。返回是否成功。
        /// 修复"抽动"：方向限制在前半球（目标在身后时混合回朝前，不再 180° 翻转），
        /// roll 用虚拟 right=cross(up, dir)（恒 ⊥ dir、永不退化），不再用 agent.right（随身体自旋会翻转）。
        /// </summary>
        public static bool TryGetAimRotation(Agent agent, Vector3 targetWorldPos, out Quaternion rot)
        {
            rot = Quaternion.identity;
            if (agent == null || agent.transform == null) return false;
            var spear = agent.transform.Find(SpearName);
            if (spear == null) return false;
            Vector3 dir = (targetWorldPos - spear.position);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) return false;
            dir.Normalize();
            // 前半球限制：目标在侧后/正后时把矛混合回朝前，避免冲锋穿透后矛 180° 翻转（肉眼可见的抽动）
            Vector3 fwd = agent.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();
            float dot = Vector3.Dot(dir, fwd);
            if (dot < 0.15f)
            {
                float k = Mathf.Clamp01((0.15f - dot) / (0.15f + 0.4f));   // 正后方 → 完全朝前；侧后 → 混合
                dir = Vector3.Slerp(dir, fwd, k);
            }
            Vector3 stableRight = Vector3.Cross(Vector3.up, dir);
            if (stableRight.sqrMagnitude < 0.001f) stableRight = Vector3.right;
            rot = Quaternion.LookRotation(dir, stableRight.normalized) * Quaternion.Euler(0f, 0f, 90f);
            return true;
        }

        /// <summary>
        /// 无目标时的"举矛"姿态——矛尖朝前上方（y 抬 1.0、前 0.6 ≈ 55°），
        /// 恢复"长矛始终树立"的设计（船上/待机/移动都举着矛，不再水平持矛等待目标）。
        /// </summary>
        public static bool TryGetRaisedRotation(Agent agent, out Quaternion rot)
        {
            rot = Quaternion.identity;
            if (agent == null || agent.transform == null) return false;
            var spear = agent.transform.Find(SpearName);
            if (spear == null) return false;
            Vector3 fwd = agent.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 dir = (fwd * 0.6f + Vector3.up * 1.0f).normalized;
            Vector3 stableRight = Vector3.Cross(Vector3.up, fwd);
            if (stableRight.sqrMagnitude < 0.001f) stableRight = Vector3.right;
            rot = Quaternion.LookRotation(dir, stableRight.normalized) * Quaternion.Euler(0f, 0f, 90f);
            return true;
        }
    }
}
