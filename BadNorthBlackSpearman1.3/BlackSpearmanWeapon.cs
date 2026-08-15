using System;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵武器处理（基底 Viking_SwordShield）：
    ///   1. 移除剑视觉（按名称禁用剑/武器/瞄准骨子对象），保留盾牌美术；
    ///   2. 复用我方 Pikeman 的长矛（Spear.spearAim 骨上的 BatchedSprite），克隆挂到黑矛兵身上；
    ///   3. 在保留的盾牌上挂 BlackSpearmanShield（剑盾兵格挡效果）。
    /// </summary>
    public static class BlackSpearmanWeapon
    {
        static Transform _spearTemplate;
        static float _lastNoSpearLog = -999f;

        /// <summary>按名称关键字禁用的剑视觉子对象表（不含盾牌——盾牌要保留），供预制体剥离与运行时移除共用。</summary>
        public static readonly string[] VisualChildNameKeys = { "sword", "weapon", "aimer", "剑" };

        public static void Apply(Agent a)
        {
            if (a == null) return;
            RemoveSword(a);
            MountSpear(a);
            // ★ 盾牌真实格挡：使用基底自带的盾牌子对象挂载 BlackSpearmanShield（IAttackResponder）。
            //   EnableShield=false 时仅剩视觉、不参与格挡。
            Transform shieldTf = FindShieldTransform(a);
            if (shieldTf != null)
            {
                var comp = a.gameObject.GetComponent<BlackSpearmanShield>();
                if (comp == null) comp = a.gameObject.AddComponent<BlackSpearmanShield>();
                if (comp != null) comp.Setup(a, shieldTf, Plugin.EnableShield != null && Plugin.EnableShield.Value);
            }
            else
            {
                BSLog.Warn("[WEAPON] 未找到基底盾牌子对象，盾牌格挡不挂载");
            }
        }

        /// <summary>按名称关键字禁用 root 下的视觉残留子对象（Plugin.BuildStrippedTemplate 也复用）。返回禁用数量。</summary>
        public static int DisableChildrenByNames(Transform root, string[] keys)
        {
            if (root == null || keys == null || keys.Length == 0) return 0;
            int removed = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.gameObject == root.gameObject) continue;
                string n = t.name.ToLowerInvariant();
                bool hit = false;
                for (int i = 0; i < keys.Length; i++)
                {
                    if (n.Contains(keys[i].ToLowerInvariant())) { hit = true; break; }
                }
                if (hit) { t.gameObject.SetActive(false); removed++; }
            }
            return removed;
        }

        static void RemoveSword(Agent a)
        {
            try
            {
                a.shield = false;
                var shield = a.GetComponent<Shield>();
                if (shield != null) shield.enabled = false;   // 兜底：剥离失败走源预制体时也禁用其盾牌逻辑（保留盾牌美术）

                int removed = DisableChildrenByNames(a.transform, VisualChildNameKeys);
                BSLog.Info($"[WEAPON] 移除剑视觉: shield={a.shield}, 禁用剑/武器子对象 {removed} 个");
            }
            catch (Exception e) { BSLog.Warn("[WEAPON] 移除剑视觉失败: " + e); }
        }

        /// <summary>在 agent 层级递归查找盾牌子对象（基底 Viking_SwordShield 自带，供 BlackSpearmanShield 判定正面朝向）。</summary>
        static Transform FindShieldTransform(Agent a)
        {
            if (a == null) return null;
            foreach (var t in a.transform.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.gameObject == a.gameObject) continue;
                string n = t.name.ToLowerInvariant();
                if (n.Contains("shield") || n.Contains("盾"))
                {
                    BSLog.Info("[WEAPON] 使用基底盾牌: " + t.name + " (localPos=" + t.localPosition.ToString("F3") + ")");
                    return t;
                }
            }
            return null;
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
