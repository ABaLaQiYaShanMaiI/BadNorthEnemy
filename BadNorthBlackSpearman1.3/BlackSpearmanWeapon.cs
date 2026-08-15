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
        static float _lastNoShieldLog = -999f;

        /// <summary>按名称关键字禁用的共享表（盾/剑/武器/aimer 视觉残留），供运行时移除与预制体剥离共用。</summary>
        public static readonly string[] VisualChildNameKeys = { "shield", "sword", "weapon", "aimer", "盾", "剑" };

        public static void Apply(Agent a)
        {
            if (a == null) return;
            RemoveSwordShield(a);
            MountSpear(a);
            Transform shieldTf = MountShieldCover(a);
            // ★ 盾牌真实效果（剑盾兵格挡）：挂载 BlackSpearmanShield（IAttackResponder）。
            //   EnableShield=false 时仅剩视觉、不参与格挡。
            if (shieldTf != null)
            {
                var comp = a.gameObject.GetComponent<BlackSpearmanShield>();
                if (comp == null) comp = a.gameObject.AddComponent<BlackSpearmanShield>();
                if (comp != null) comp.Setup(a, shieldTf, Plugin.EnableShield != null && Plugin.EnableShield.Value);
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

        static void RemoveSwordShield(Agent a)
        {
            try
            {
                a.shield = false;
                var shield = a.GetComponent<Shield>();
                if (shield != null) shield.enabled = false;

                int removed = DisableChildrenByNames(a.transform, VisualChildNameKeys);
                BSLog.Info($"[WEAPON] 移除剑盾: shield={a.shield}, 禁用子对象 {removed} 个");
            }
            catch (Exception e) { BSLog.Warn("[WEAPON] 移除剑盾失败: " + e); }
        }

        static Transform _shieldTemplate;

        /// <summary>
        /// 剑柄遮挡（用户建议）：剑柄与持剑的手在帧内重叠、PartTex 同色 → 像素无法分离。
        /// 方案：从场上 SwordShield 单位的盾牌（Shield.shield 是 public Transform）克隆一个盾牌，
        /// 挂到黑矛兵身体右侧（剑柄区域），视觉遮挡剑柄。静态挂载（不随动画摆动），
        /// 优先找已生成实例，找不到则下次再试。可被 cfg RemoveSword=false 一并禁用。
        /// </summary>
        static Transform MountShieldCover(Agent a)
        {
            try
            {
                if (_shieldTemplate == null) _shieldTemplate = FindShieldTemplate();
                if (_shieldTemplate == null)
                {
                    if (Time.time - _lastNoShieldLog > 8f)
                    {
                        _lastNoShieldLog = Time.time;
                        BSLog.Info("[WEAPON] 暂无可复用的盾牌模板（场上无 SwordShield），跳过剑柄遮挡");
                    }
                    return null;
                }

                var clone = UnityEngine.Object.Instantiate(_shieldTemplate.gameObject);
                clone.name = "Shield_Cover_BlackSpearman";
                clone.transform.SetParent(a.transform, false);
                // ★ 剑柄位于身体右侧（1P 视角即屏幕右侧、持剑手处）。盾牌挂到身侧偏右、
                //   手部高度附近，遮挡剑柄残留；scale 取 SwordShield 盾牌原尺寸的 1.15 倍，
                //   确保盖住剑柄而不显得过大。位置可后续按观感微调。
                float s = a.radius * 1.15f;
                clone.transform.localPosition = new Vector3(a.radius * 0.8f, a.radius * 1.2f, a.radius * 0.3f);
                clone.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);
                clone.transform.localScale = new Vector3(s, s, s);
                clone.SetActive(true);
                BSLog.Info("[WEAPON] 已挂载盾牌遮挡剑柄: " + clone.name +
                    " (localPos=" + clone.transform.localPosition.ToString("F3") +
                    ", localScale=" + clone.transform.localScale.ToString("F3") +
                    ", children=" + clone.transform.childCount + ")");
                return clone.transform;
            }
            catch (Exception e) { BSLog.Warn("[WEAPON] 挂载盾牌失败: " + e); }
            return null;
        }

        static Transform FindShieldTemplate()
        {
            try
            {
                // 找场上 SwordShield 单位的盾牌（Shield.shield 是 public Transform，已烘焙进层级）
                var shields = Resources.FindObjectsOfTypeAll<Shield>();
                foreach (var s in shields)
                {
                    if (s == null) continue;
                    if (s.shield != null)
                    {
                        BSLog.Info("[WEAPON] 找到盾牌模板: " + s.name + " shield=" + s.shield.name +
                            " (bounds=" + (s.shield.GetComponent<MeshFilter>() != null ? "mesh" : "无mesh") + ")");
                        return s.shield;
                    }
                }
                if (Time.time - _lastNoShieldLog > 5f)
                {
                    _lastNoShieldLog = Time.time;
                    BSLog.Info("[WEAPON] 本局暂无可复用盾牌模板（扫描 " + shields.Length + " 个 Shield，可能本局无剑盾兵）");
                }
            }
            catch (Exception e) { BSLog.Warn("[WEAPON] 扫描 Shield 失败: " + e); }
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
