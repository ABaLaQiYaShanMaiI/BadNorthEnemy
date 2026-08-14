using System;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵"混搭"武器处理：
    ///   1. 移除敌方剑盾（agent.shield=false + 禁用 Shield 组件 + 按名称禁用剑/盾子对象）；
    ///   2. 复用我方 Pikeman 的长矛（Spear.spearAnim 骨骼上的 BatchedSprite），克隆一份挂到黑矛兵身上。
    /// 全程记录诊断日志，便于排查渲染链问题。
    /// </summary>
    public static class BlackSpearmanWeapon
    {
        static Transform _spearTemplate;
        static float _lastNoSpearLog = -999f;

        public static void Apply(Agent a)
        {
            if (a == null) return;
            RemoveSwordShield(a);
            MountSpear(a);
        }

        static void RemoveSwordShield(Agent a)
        {
            try
            {
                a.shield = false;
                var shield = a.GetComponent<Shield>();
                if (shield != null) shield.enabled = false;

                int removed = 0;
                string[] keys = { "shield", "sword", "weapon", "盾", "剑" };
                foreach (var t in a.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || t.gameObject == a.gameObject) continue;
                    string n = t.name.ToLowerInvariant();
                    bool hit = false;
                    foreach (var k in keys)
                        if (n.Contains(k.ToLowerInvariant())) { hit = true; break; }
                    if (hit) { t.gameObject.SetActive(false); removed++; }
                }
                BSLog.Info($"[WEAPON] 移除剑盾: shield={a.shield}, 禁用子对象 {removed} 个");
            }
            catch (Exception e) { BSLog.Warn("[WEAPON] 移除剑盾失败: " + e); }
        }

        static void MountSpear(Agent a)
        {
            try
            {
                if (_spearTemplate == null) _spearTemplate = FindSpearTemplate();
                if (_spearTemplate == null) return;

                var clone = UnityEngine.Object.Instantiate(_spearTemplate.gameObject);
                clone.name = "Spear_BlackSpearman";
                clone.transform.SetParent(a.transform, false);

                // 手持高度启发式（基于 agent.radius 推算，与 v1.18 一致）
                float y = a.radius * 1.4f;
                float z = a.radius * 0.6f;
                clone.transform.localPosition = new Vector3(0f, y, z);
                clone.transform.localRotation = Quaternion.identity;
                clone.SetActive(true);

                BSLog.Info($"[WEAPON] 已挂载长矛到 {a.name} " +
                    $"(localPos={clone.transform.localPosition}, children={clone.transform.childCount})");
            }
            catch (Exception e) { BSLog.Warn("[WEAPON] 挂载长矛失败: " + e); }
        }

        static Transform FindSpearTemplate()
        {
            try
            {
                var spears = Resources.FindObjectsOfTypeAll<Spear>();
                foreach (var s in spears)
                {
                    if (s == null) continue;
                    // 优先克隆 spearAim（瞄准骨，其旋转决定"举矛/放矛"），否则克隆 spearAnim
                    if (s.spearAim != null)
                    {
                        BSLog.Info($"[WEAPON] 找到我方长矛模板(aim骨): {s.name} (spearSprite={(s.spearSprite != null)})");
                        return s.spearAim;
                    }
                    if (s.spearAnim != null)
                    {
                        BSLog.Info($"[WEAPON] 找到我方长矛模板(anim骨): {s.name} (spearSprite={(s.spearSprite != null)})");
                        return s.spearAnim;
                    }
                }
                if (Time.time - _lastNoSpearLog > 5f)
                {
                    _lastNoSpearLog = Time.time;
                    BSLog.Info($"[WEAPON] 本局暂无可复用长矛模板（扫描 {spears.Length} 个 Spear，可能本局无长矛兵）");
                }
            }
            catch (Exception e) { BSLog.Warn("[WEAPON] 扫描 Spear 失败: " + e); }
            return null;
        }
    }
}
