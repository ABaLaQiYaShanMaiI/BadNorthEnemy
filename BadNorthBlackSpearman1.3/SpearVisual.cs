using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman1_3
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
        /// </summary>
        public static bool TryGetAimRotation(Agent agent, Vector3 targetWorldPos, out Quaternion rot)
        {
            rot = Quaternion.identity;
            if (agent == null || agent.transform == null) return false;
            var spear = agent.transform.Find(SpearName);
            if (spear == null) return false;
            Vector3 dir = (targetWorldPos - spear.position).normalized;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) return false;
            rot = Quaternion.LookRotation(dir, agent.transform.right) * Quaternion.Euler(0f, 0f, 90f);
            return true;
        }
    }
}
