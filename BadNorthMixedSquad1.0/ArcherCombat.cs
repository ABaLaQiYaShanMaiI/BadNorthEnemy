using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.Ballistics;

namespace BadNorthMixedSquad1_0
{
    /// <summary>弓手战术（M4 威慑强化）：
    /// 1) 集火点射——Patch LineOfSight.get_target：混编弓手锁定 ArcherCombat.FocusTarget
    ///    （由 TacticalFormation 每帧选"低血量目标"，同船弓手统一目标 = 集中输出点杀）；
    /// 2) 命中率提升——Patch Archery.Shoot：混编弓手射出的箭矢挂 ArrowTracking，
    ///    飞行中每帧把弹道朝目标当前位置修正（原版是抛物线预判，目标一动就易射空）。
    /// 仅混编弓手生效（按 MixedRole==Archer 判定），不碰玩家弓手/原版行为。</summary>
    public static class ArcherCombat
    {
        /// <summary>集火目标（TacticalFormation 每帧写入；null=无集火）。</summary>
        public static Agent FocusTarget;
        /// <summary>cfg 开关：EnableArcherFocus。</summary>
        public static bool Enabled = true;
        /// <summary>cfg：ArrowTrackingStrength（0~1，箭矢追踪强度）。</summary>
        public static float TrackingStrength = 0.5f;

        static bool _patchFocus;
        static bool _patchTracking;

        /// <summary>两条 Patch 是否都注册成功（供启动总览）。</summary>
        public static bool PatchOK => _patchFocus && _patchTracking;

        public static void PatchAll(Harmony harmony)
        {
            TryPatch("LineOfSight.get_target（弓手集火点射）", () =>
            {
                var prop = typeof(LineOfSight).GetProperty("target");
                var m = !ReferenceEquals(prop, null) ? prop.GetGetMethod() : null;
                if (ReferenceEquals(m, null)) throw new Exception("LineOfSight.target getter 不存在");
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(ArcherCombat).GetMethod("LineOfSightGetTargetPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
            }, ref _patchFocus);

            TryPatch("Archery.Shoot（箭矢追踪·命中率提升）", () =>
            {
                var m = typeof(Archery).GetMethod("Shoot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ReferenceEquals(m, null)) throw new Exception("Archery.Shoot 不存在");
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(ArcherCombat).GetMethod("ArcheryShootPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
            }, ref _patchTracking);
        }

        // ⚠️ .NET 3.5 雷区：禁 System.Action/Func → 自定义委托；反射判空用 ReferenceEquals。
        delegate void PatchJobA();

        static void TryPatch(string label, PatchJobA apply, ref bool ok)
        {
            try
            {
                apply();
                ok = true;
                BSLog.Info("[PATCH] ✅ " + label);
            }
            catch (Exception e)
            {
                ok = false;
                BSLog.Error("[PATCH] ❌ " + label + " 失败: " + e);
            }
        }

        /// <summary>混编弓手：目标锁定为集火目标（同一目标 = 点射集火）。非混编弓手保持原版。</summary>
        static void LineOfSightGetTargetPostfix(LineOfSight __instance, ref LineOfSight.Sight __result)
        {
            try
            {
                if (!Enabled) return;
                Agent focus = FocusTarget;
                if (focus == null || focus.aliveState == null || !focus.aliveState.active) return;
                if (!IsMixedArcher(__instance)) return;
                __result = new LineOfSight.Sight { agent = focus };
            }
            catch { }
        }

        /// <summary>混编弓手射出的箭矢挂追踪组件（命中率提升）。</summary>
        static void ArcheryShootPostfix(Archery __instance, ref Shootable __result)
        {
            try
            {
                if (!Enabled || TrackingStrength <= 0f) return;
                if (ReferenceEquals(__result, null) || __instance == null || __instance.agent == null) return;
                if (!IsMixedArcher(__instance)) return;
                Agent focus = FocusTarget;
                if (focus == null || focus.aliveState == null || !focus.aliveState.active) return;
                var tr = __result.GetComponent<ArrowTracking>();
                if (ReferenceEquals(tr, null)) tr = __result.gameObject.AddComponent<ArrowTracking>();
                if (!ReferenceEquals(tr, null)) tr.Setup(focus, TrackingStrength);
            }
            catch { }
        }

        /// <summary>判定组件所属 agent 是否为混编弓手（MixedRole==Archer）。</summary>
        static bool IsMixedArcher(Component c)
        {
            if (ReferenceEquals(c, null)) return false;
            var role = c.GetComponent<MixedRole>();
            return !ReferenceEquals(role, null) && role.role == MixedRoleType.Archer;
        }
    }
}