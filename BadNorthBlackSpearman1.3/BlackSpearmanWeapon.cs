using System;
using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵武器处理（基底 Viking_SwordShield）：
    /// 1. 移除剑视觉（按名称禁用剑/武器/瞄准骨子对象）；
    /// 2. 复用我方 Pikeman 的长矛（Spear.spearAim 骨上的 BatchedSprite），克隆挂到黑矛兵身上；
    /// 3. EnableShield=false（默认）时完全移除盾牌（效果+美术均不挂载，盾牌子对象禁用）；
    /// true 时才保留盾牌美术并挂 BlackSpearmanShield（剑盾兵格挡效果）。
    /// 盾牌日志固定分两个维度（判断\"盾牌存在\"必须同时看两者，缺一不可）：
    /// [盾牌·美术资源] 静态检查：子对象是否存在、Renderer/Mesh/Sprite/材质是否齐备有效、LevelMesh/BodyColoredMesh 渲染管线组件；
    /// [盾牌·实际效果] 运行期检查：是否真的上屏(isVisible)、姿态/距身/朝外、格挡是否真的触发(计数)。
    /// </summary>
    public static class BlackSpearmanWeapon
    {
        static Transform _spearTemplate;
        static float _lastNoSpearLog = -999f;
        static float _lastNoShieldLog = -999f;
        static int _postMoveDumps;   // 移盾后完整体检次数（限前 2 只，避免刷屏）
        public static bool MountSpearToHand = true;   // 长矛挂到持剑锚点（Weapon 骨=手位），由 ModConfig.SpearMountToHand 设置

        /// <summary>按名称关键字禁用的剑视觉子对象表（不含盾牌——盾牌要保留），供预制体剥离与运行时移除共用。</summary>
        public static readonly string[] VisualChildNameKeys = { "sword", "weapon", "aimer", "剑" };

        /// <summary>盾牌子对象关键字表（用户指定完全移除盾牌：效果+美术）。EnableShield=false 时禁用。</summary>
        public static readonly string[] ShieldChildNameKeys = { "shield", "盾" };

        public static void Apply(Agent a)
        {
            if (a == null) return;
            RemoveSword(a);
            MountSpear(a);
            MountShield(a);
        }

        /// <summary>
        /// 挂载盾牌（预制件装配大改）：美术资源查找/兜底 → 挂格挡组件 → 两维体检 → 姿态退化自愈。
        /// 任何一步的结果都以日志明确区分\"美术资源\"与\"实际效果\"两个维度。
        /// </summary>
        static void MountShield(Agent a)
        {
            try
            {
                // 用户指定完全移除盾牌（效果+美术）→ 不挂盾牌、不加格挡组件（美术已在
                // RemoveSword/剥离模板里禁用）。EnableShield=true 才走下面的挂载流程。
                if (ModConfig.ShieldFullyRemoved)
                {
                    BSLog.Info("[盾牌·美术资源] 按用户配置完全移除盾牌（效果+美术均不挂载，EnableShield=false）");
                    return;
                }
                // ① 美术资源：优先使用基底剑盾兵自带的盾牌子对象
                Transform shieldTf = FindShieldTransform(a.transform);
                if (shieldTf != null)
                    BSLog.Info("[盾牌·美术资源] 使用基底盾牌子对象: " + shieldTf.name);
                else
                    shieldTf = CloneFallbackShield(a);   // ② 兜底：克隆场上剑盾兵盾牌
                if (shieldTf == null)
                {
                    BSLog.Warn("[盾牌·美术资源] 盾牌缺失（基底无盾牌且场上无剑盾兵可克隆）→ 黑矛兵无盾牌视觉/格挡");
                    return;
                }
                shieldTf.gameObject.SetActive(true);

                // ③ 实际效果：挂 BlackSpearmanShield 并启用格挡（走到这里必然是 EnableShield=true；false 已在上面 return = 盾牌完全移除）
                var comp = a.gameObject.GetComponent<BlackSpearmanShield>();
                if (comp == null) comp = a.gameObject.AddComponent<BlackSpearmanShield>();
                if (comp != null) comp.Setup(a, shieldTf, !ModConfig.ShieldFullyRemoved);

                // ④ 美术资源完整体检（含盾牌是否具备可渲染外观）
                DumpShieldHealth(a, "[盾牌·美术资源] 挂载时");

                // ⑤ 姿态退化自愈（scale≈0 / 陷入身体 → 重置到身左侧默认姿态）
                FixDegeneratePose(a, shieldTf);

                // ⑥ 移盾遮蔽剑柄（用户方案）：盾牌贴到基底 Weapon 锚点（原本持剑处），每帧跟随身体动画；
                // 保持朝前——格挡判定 Dot(shield.forward, -攻击方向) 不受影响。
                var anchor = FindSwordAnchor(a.transform);
                if (anchor != null && comp != null)
                {
                    comp.swordAnchor = anchor;
                    RepositionShieldToSwordHand(a, shieldTf, anchor);
                }
                else
                {
                    BSLog.Info("[盾牌·美术资源] 未找到持剑锚点(Weapon)，盾牌保持默认姿态（无移盾遮蔽）");
                }

                // ⑦ 移盾后复检（前 2 只黑矛兵输出完整两维体检，避免刷屏）
                if (_postMoveDumps < 2)
                {
                    _postMoveDumps++;
                    DumpShieldHealth(a, "[盾牌·美术资源] 移盾后");
                }
            }
            catch (Exception e) { BSLog.Warn("[盾牌·美术资源] 挂载盾牌异常: " + e); }
        }

        /// <summary>兜底：基底无盾牌时克隆场上剑盾兵的盾牌（原 MountShieldCover 方案，仅应急；克隆体未必进渲染管线）。</summary>
        static Transform CloneFallbackShield(Agent a)
        {
            try
            {
                var shields = Resources.FindObjectsOfTypeAll<Shield>();
                foreach (var s in shields)
                {
                    if (s == null || s.shield == null) continue;
                    if (Time.time - _lastNoShieldLog > 5f)
                    {
                        _lastNoShieldLog = Time.time;
                        BSLog.Warn("[盾牌·美术资源] 基底无盾牌，克隆场上剑盾兵盾牌兜底: " + s.name + " shield=" + s.shield.name);
                    }
                    var clone = UnityEngine.Object.Instantiate(s.shield.gameObject);
                    clone.name = "Shield_BlackSpearman_Fallback";
                    clone.transform.SetParent(a.transform, false);
                    clone.transform.localPosition = new Vector3(-a.radius * 0.9f, a.radius * 1.4f, a.radius * 0.4f);
                    clone.transform.localScale = Vector3.one * a.radius * 1.2f;
                    clone.transform.localRotation = Quaternion.identity;
                    clone.SetActive(true);
                    return clone.transform;
                }
                if (Time.time - _lastNoShieldLog > 5f)
                {
                    _lastNoShieldLog = Time.time;
                    BSLog.Warn("[盾牌·美术资源] 基底无盾牌，且场上暂无剑盾兵可克隆");
                }
            }
            catch (Exception e) { BSLog.Warn("[盾牌·美术资源] 兜底克隆异常: " + e); }
            return null;
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
                if (shield != null) shield.enabled = false;   // 兜底：剥离失败走源预制体时也禁用其盾牌逻辑

                int removed = DisableChildrenByNames(a.transform, VisualChildNameKeys);
                string shieldNote = "";
                // 用户指定完全移除盾牌（效果+美术）——运行时再禁一次盾牌子对象（模板层已禁，防克隆复活）
                if (ModConfig.ShieldFullyRemoved)
                {
                    int s = DisableChildrenByNames(a.transform, ShieldChildNameKeys);
                    shieldNote = ", 移除盾牌美术 " + s + " 个（EnableShield=false）";
                }
                BSLog.Info($"[WEAPON] 移除剑视觉: shield={a.shield}, 禁用剑/武器子对象 {removed} 个{shieldNote}");
            }
            catch (Exception e) { BSLog.Warn("[WEAPON] 移除剑视觉失败: " + e); }
        }

        /// <summary>递归查找盾牌子对象（基底剑盾兵自带；按名称关键字 shield/盾）。供 Apply / 剥离 / 诊断复用。</summary>
        public static Transform FindShieldTransform(Transform root)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.gameObject == root.gameObject) continue;
                string n = t.name.ToLowerInvariant();
                if (n.Contains("shield") || n.Contains("盾"))
                    return t;
            }
            return null;
        }

        /// <summary>找持剑锚点：基底剑盾兵的 Weapon/Sword 子对象（现已被禁用，但变换仍在——即"原本持剑处"）。
        /// public 供 MountSpear / Diagnostics F8 持矛手对齐诊断复用。</summary>
        public static Transform FindSwordAnchor(Transform root)
        {
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.gameObject == root.gameObject) continue;
                string n = t.name.ToLowerInvariant();
                if (n.Contains("shield") || n.Contains("盾")) continue;
                if (n.Contains("weapon") || n.Contains("sword") || n.Contains("剑"))
                    return t;
            }
            return null;
        }

        /// <summary>把盾牌移到"原本持剑的位置"遮蔽剑柄残留（用户方案）：以基底 Weapon 锚点为基准，
        /// 略向前/上偏移（前移收小 0.12→0.05、抬高 0.1→0.02、放大 1.2→1.5，让持剑手真正落在盾面内），
        /// 保持朝前（格挡判定依赖 shield.forward）；每帧由 BlackSpearmanShield.swordAnchor 持续跟随。</summary>
        static void RepositionShieldToSwordHand(Agent a, Transform shieldTf, Transform anchor)
        {
            try
            {
                if (a == null || shieldTf == null || anchor == null) return;
                Vector3 target = anchor.position + a.transform.forward * (a.radius * 0.05f) + Vector3.up * (a.radius * 0.02f);
                float oldDist = Vector3.Distance(shieldTf.position, a.transform.position);
                shieldTf.position = target;
                shieldTf.rotation = Quaternion.LookRotation(a.transform.forward, Vector3.up);
                shieldTf.localScale = Vector3.one * a.radius * 1.5f;   // 略放大，手落入盾面 bounds
                float newDist = Vector3.Distance(shieldTf.position, a.transform.position);
                // 盾牌是否真的盖住持剑手（Weapon 锚点在盾 Renderer 包围盒内 = 手被遮）
                bool covers = false;
                var srs = shieldTf.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < srs.Length; i++)
                {
                    if (srs[i] != null && srs[i].bounds.Contains(anchor.position)) { covers = true; break; }
                }
                BSLog.Info("[盾牌·美术资源] 盾牌移到持剑位遮蔽剑柄: 锚点=" + anchor.name +
                    " 距身 " + oldDist.ToString("F2") + "m → " + newDist.ToString("F2") + "m" +
                    " 朝外Dot=" + Vector3.Dot(shieldTf.forward, a.transform.forward).ToString("F2") +
                    " 覆盖持剑手=" + (covers ? "是✓" : "否✗") +
                    " 锚点距盾心=" + Vector3.Distance(anchor.position, shieldTf.position).ToString("F2") + "m" +
                    (covers ? "" : " ← 盾没盖住手，需调 RepositionShieldToSwordHand 偏移或加大盾尺寸"));
            }
            catch (Exception e) { BSLog.Warn("[盾牌·美术资源] 移盾到持剑位失败: " + e); }
        }

        /// <summary>盾牌两维体检（美术资源 + 实际效果），供挂载时与 F8 诊断复用。</summary>
        public static void DumpShieldHealth(Agent a, string tag)
        {
            try
            {
                if (a == null) return;
                Transform shieldTf = null;
                var comp = a.GetComponent<BlackSpearmanShield>();
                if (comp != null && comp.shield != null) shieldTf = comp.shield;
                if (shieldTf == null) shieldTf = FindShieldTransform(a.transform);
                if (shieldTf == null)
                {
                    BSLog.Raw(tag + " 盾牌不存在（美术缺失：无视觉也无格挡）");
                    return;
                }

                BSLog.Raw("\n" + tag + " ==== 盾牌体检 ====");
                BSLog.Raw("[美术资源]（静态：子对象/Renderer/Mesh/Sprite/材质/渲染管线组件）");
                BSLog.Raw("· 子对象: " + shieldTf.name + " activeSelf=" + shieldTf.gameObject.activeSelf + " 路径=" + TransformPath(shieldTf));

                var renderers = shieldTf.GetComponentsInChildren<Renderer>(true);
                BSLog.Raw("· Renderer x" + renderers.Length);
                bool anyValid = false;
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    string kind = (r is MeshRenderer) ? "MeshRenderer" : (r is SpriteRenderer) ? "SpriteRenderer" : r.GetType().Name;
                    string asset = "无资源";
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        asset = "mesh=" + mf.sharedMesh.name;
                        if (mf.sharedMesh.bounds.size.sqrMagnitude > 0.0001f) anyValid = true;
                    }
                    var sr = r as SpriteRenderer;
                    if (sr != null && sr.sprite != null)
                    {
                        asset = "sprite=" + sr.sprite.name;
                        anyValid = true;
                    }
                    string mat = (r.sharedMaterial != null) ? r.sharedMaterial.name + " shader=" + r.sharedMaterial.shader.name : "无材质";
                    bool ok = r.sharedMaterial != null && (asset.StartsWith("mesh=") || asset.StartsWith("sprite="));
                    BSLog.Raw("  · " + r.gameObject.name + " [" + kind + "] enabled=" + r.enabled +
                        " 资源=" + asset + " 材质=" + mat + (ok ? " ✓" : " ✗"));
                }
                BSLog.Raw("· 渲染管线组件: LevelMesh=" + (shieldTf.GetComponentInChildren<LevelMesh>(true) != null) +
                    " BodyColoredMesh=" + (shieldTf.GetComponentInChildren<BodyColoredMesh>(true) != null));
                // 白框排查：Palette 材质按网格顶点色采样色板；顶点色缺失/纯白 → 可能渲染成白框
                var srf = shieldTf.GetComponentInChildren<MeshRenderer>(true);
                if (srf != null)
                {
                    var srfMf = srf.GetComponent<MeshFilter>();
                    if (srfMf != null && srfMf.sharedMesh != null)
                    {
                        var vc = srfMf.sharedMesh.colors32;
                        if (vc != null && vc.Length > 0)
                            BSLog.Raw("· 网格顶点色[0]=" + vc[0] + (vc[0].r >= 250 && vc[0].g >= 250 && vc[0].b >= 250
                                ? " ← 纯白顶点色（Palette 可能渲染成白框）" : ""));
                        else
                            BSLog.Raw("· 网格无顶点色 ← Palette 材质可能渲染为纯白（白框）");
                    }
                }
                BSLog.Raw("· 外观判定: " + (anyValid ? "具备可渲染资源，盾牌应有可见外观" : "无有效渲染资源 → 盾牌必然不可见!"));

                BSLog.Raw("[实际效果]（运行期动态）");
                BSLog.Raw("· 本地姿态: pos=" + shieldTf.localPosition.ToString("F3") +
                    " scale=" + shieldTf.localScale.ToString("F3") +
                    " rot(euler)=" + shieldTf.localRotation.eulerAngles.ToString("F1"));
                BSLog.Raw("· 世界姿态: pos=" + shieldTf.position.ToString("F3") +
                    " 距身体中心=" + Vector3.Distance(shieldTf.position, a.transform.position).ToString("F3") + "m");
                BSLog.Raw("· 朝外判定 Dot(shield.forward, agent.forward)=" + Vector3.Dot(shieldTf.forward, a.transform.forward).ToString("F2") +
                    "（格挡生效条件: Dot(shield.forward, -攻击方向) > 0.5）");
                if (renderers.Length > 0)
                    BSLog.Raw("· 上屏状态 isVisible=" + renderers[0].isVisible + "（挂载当帧为 False 属正常，15s 后由定期体检再报）");
                if (comp != null)
                    BSLog.Raw("· 格挡组件: 已挂载 cfgEnable=" + comp.enabledByCfg +
                        " 触发块[近战=" + comp.BlockMelee + " 箭=" + comp.BlockArrow + " 斧=" + comp.BlockAxe + " 矛=" + comp.BlockSpear + "]");
                else
                    BSLog.Raw("· 格挡组件: 未挂载");
            }
            catch (Exception e) { BSLog.Warn(tag + " 盾牌体检异常: " + e); }
        }

        /// <summary>姿态退化自愈：本地 scale≈0 或盾牌陷入身体中心 → 重置到身左侧默认姿态并告警。</summary>
        static void FixDegeneratePose(Agent a, Transform shieldTf)
        {
            try
            {
                if (shieldTf.localScale.sqrMagnitude < 0.0025f ||
                    Vector3.Distance(shieldTf.position, a.transform.position) < a.radius * 0.2f)
                {
                    shieldTf.localPosition = new Vector3(-a.radius * 0.9f, a.radius * 1.4f, a.radius * 0.4f);
                    shieldTf.localScale = Vector3.one * a.radius * 1.2f;
                    shieldTf.localRotation = Quaternion.identity;
                    BSLog.Warn("[盾牌·美术资源] 盾牌姿态异常（scale≈0 或陷入身体），已重置到身左侧默认姿态 pos=" +
                        shieldTf.localPosition.ToString("F3") + " scale=" + shieldTf.localScale.ToString("F3"));
                }
            }
            catch (Exception e) { BSLog.Warn("[盾牌·美术资源] 姿态自愈失败: " + e); }
        }

        /// <summary>拼接 Transform 完整路径（定位盾牌子对象在层级中的位置）。</summary>
        static string TransformPath(Transform t)
        {
            if (t == null) return "null";
            string path = t.name;
            var cur = t.parent;
            while (cur != null)
            {
                path = cur.name + "/" + path;
                cur = cur.parent;
            }
            return path;
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

                // 长矛对齐持剑手。旧固定偏移 (0, radius*1.4, radius*0.6) 在身体正中，
                // 与基底剑盾兵"持剑的手"（Weapon 锚点，偏离身体中心 ~0.2m）错位 → 观感"持矛手脱离身躯、
                // 攻击范围异常大"。改为把矛根挂到 Weapon 锚点（手位），矛尖朝前；找不到锚点则退回旧偏移。
                Vector3 mountPos = new Vector3(0f, a.radius * 1.4f, a.radius * 0.6f);
                string anchorInfo = "未找到持剑锚点，用旧固定偏移";
                if (MountSpearToHand)
                {
                    Transform anchor = FindSwordAnchor(a.transform);
                    if (anchor != null)
                    {
                        Vector3 handLocal = a.transform.InverseTransformPoint(anchor.position);
                        anchorInfo = "锚点=" + anchor.name +
                            " 锚点localPos=" + anchor.localPosition.ToString("F3") +
                            " 锚点世界=" + anchor.position.ToString("F2");
                        // 矛根贴到手上，略抬高到握持高度、略前移避免矛身穿入身体（本地前向=+Z）
                        mountPos = handLocal + new Vector3(0f, a.radius * 0.1f, a.radius * 0.15f);
                    }
                    else
                    {
                        anchorInfo = "未找到持剑锚点，用旧固定偏移";
                    }
                }
                clone.transform.localPosition = mountPos;
                clone.transform.localRotation = Quaternion.identity;
                clone.SetActive(true);

                BSLog.Info($"[WEAPON] 已挂载长矛到 {a.name} (localPos={clone.transform.localPosition}, children={clone.transform.childCount})");
                BSLog.Info("[WEAPON] 长矛握持位: " + anchorInfo + " → 矛根localPos=" + mountPos.ToString("F3"));
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
