using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;
using Voxels.TowerDefense.Upgrades;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 运行时诊断探针：周期心跳 + 按键(F8)触发完整转储。
    /// 收集"比报错日志更多"的现场信息：生成池注册表、新建预制件状态、敌舰装载、Agent 组件等。
    /// </summary>
    public class DiagnosticsComponent : MonoBehaviour
    {
        const float HeartbeatInterval = 8f;
        float _lastHeartbeat;

        void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    BSLog.Raw("\n==================== 手动完整诊断 (F8) ====================");
                    DumpFull();
                    DumpBlackSpearmanRender();
                    DumpBlackSpearmanShields();
                    DumpPrefabAnalysis();
                    BSLog.Raw("==================== 手动完整诊断结束 ====================\n");
                }
                if (Input.GetKeyDown(KeyCode.F9))
                {
                    DumpSpearPoses();
                }
            }
            catch { }

            if (Time.time - _lastHeartbeat >= HeartbeatInterval)
            {
                _lastHeartbeat = Time.time;
                Heartbeat();
            }
        }

        /// <summary>
        /// 黑矛兵渲染健康检查（F8 触发）：打印每个黑矛兵的 SpriteAnimator color/sprite2 纹理、
        /// Body 状态、Animator Speed、BodySprite 是否 active —— 用于定位"去剑后身体消失"的渲染链路问题。
        /// </summary>
        void DumpBlackSpearmanRender()
        {
            try
            {
                var vr = Plugin.BlackSpearman;
                if (vr == null) { BSLog.Raw("[渲染诊断] BlackSpearman 未注册"); return; }
                var agents = Resources.FindObjectsOfTypeAll<Agent>();
                int n = 0;
                foreach (var a in agents)
                {
                    if (a == null) continue;
                    var va = a.GetComponent<VikingAgent>();
                    if (va == null || !ReferenceEquals(va.vikingReference, vr)) continue;
                    n++;
                    BSLog.Raw("\n[渲染诊断] 黑矛兵#" + n + " " + a.name +
                        " pos=" + a.transform.position.ToString("F2") +
                        " navPos=" + a.navPos.pos.ToString("F2") +
                        " alive=" + a.aliveState.active + " grounded=" + a.groundedState.active);

                    // SpriteAnimator 细节
                    var sas = a.GetComponentsInChildren<SpriteAnimator>(true);
                    BSLog.Raw("  SpriteAnimator x" + sas.Length);
                    foreach (var sa in sas)
                    {
                        if (sa == null) continue;
                        Color c = sa.color;
                        string s2info = "null";
                        if (sa.sprite2 != null && sa.sprite2.texture != null)
                            s2info = sa.sprite2.texture.name + " " + sa.sprite2.texture.width + "x" + sa.sprite2.texture.height;
                        string rectInfo = (sa.sprite2 != null) ? sa.sprite2.textureRect.ToString() : "null";
                        BSLog.Raw("  · " + sa.name + ": color=(" + c.r.ToString("F3") + "," + c.g.ToString("F3") + "," + c.b.ToString("F3") + "," + c.a.ToString("F3") +
                            ") sprite2=" + s2info + " rect=" + rectInfo + " activeSelf=" + sa.gameObject.activeSelf);
                    }

                    // Body / 动画
                    if (a.body != null)
                        BSLog.Raw("  Body: stand=" + a.body.standing.active + " step=" + a.body.stepping.active +
                            " slide=" + a.body.sliding.active + " hop=" + a.body.hopping.active +
                            " moveAnimate=" + a.moveAnimate);
                    if (a.animator != null)
                    {
                        try { BSLog.Raw("  Animator Speed=" + a.animator.GetFloat("Speed").ToString("F2")); }
                        catch { BSLog.Raw("  Animator Speed=读取失败"); }
                    }

                    // 身体主 SpriteRenderer 是否 active / 有 sprite
                    var sprs = a.GetComponentsInChildren<SpriteRenderer>(true);
                    foreach (var spr in sprs)
                    {
                        if (spr == null) continue;
                        BSLog.Raw("  SpriteRenderer · " + spr.name + ": enabled=" + spr.enabled +
                            " active=" + spr.gameObject.activeSelf +
                            " sprite=" + (spr.sprite != null ? spr.sprite.name : "null") +
                            " color=(" + spr.color.r.ToString("F2") + "," + spr.color.g.ToString("F2") + "," + spr.color.b.ToString("F2") + ")");
                    }

                    // 网格子对象材质块纹理（_MainTex 是否为去剑克隆、_PartTex 部件贴图）——验证擦除覆盖全部变体
                    var mrs = a.GetComponentsInChildren<MeshRenderer>(true);
                    foreach (var mr in mrs)
                    {
                        if (mr == null) continue;
                        var block = new MaterialPropertyBlock();
                        try { mr.GetPropertyBlock(block); } catch { continue; }
                        Texture mt = null, pt = null;
                        try { mt = block.GetTexture("_MainTex"); } catch { }
                        try { pt = block.GetTexture("_PartTex"); } catch { }
                        string mtName = mt != null ? mt.name : "null";
                        string ptName = pt != null ? pt.name : "null";
                        if (mt != null && SwordRemover.IsSharedClone(mt))
                            mtName += " ←去剑克隆✓";
                        else if (mtName.IndexOf("_NoSword", StringComparison.Ordinal) >= 0 ||
                                 ptName.IndexOf("_NoSword", StringComparison.Ordinal) >= 0)
                            mtName += " ←去剑克隆";
                        var mf = mr.GetComponent<MeshFilter>();
                        int verts = (mf != null && mf.sharedMesh != null) ? mf.sharedMesh.vertexCount : -1;
                        BSLog.Raw("  MeshRenderer · " + mr.gameObject.name + ": enabled=" + mr.enabled +
                            " verts=" + verts + " isVisible=" + mr.isVisible +
                            " block._MainTex=" + mtName + " block._PartTex=" + ptName);
                    }

                    // ★ 第十四轮：持矛手对齐诊断——矛根 vs Weapon 锚点（持剑手）间距，量化"手脱离身躯"
                    try
                    {
                        Transform spearT = a.transform.Find("Spear_BlackSpearman");
                        Transform anchor = BlackSpearmanWeapon.FindSwordAnchor(a.transform);
                        string spearInfo = "无长矛";
                        if (spearT != null)
                            spearInfo = "矛根local=" + spearT.localPosition.ToString("F3") +
                                " 世界=" + spearT.position.ToString("F2") +
                                " active=" + spearT.gameObject.activeSelf;
                        string anchorInfo = "无Weapon锚点";
                        if (anchor != null)
                            anchorInfo = "锚点local=" + anchor.localPosition.ToString("F3") +
                                " 世界=" + anchor.position.ToString("F2") +
                                " activeSelf=" + anchor.gameObject.activeSelf;
                        string gap = "n/a";
                        if (spearT != null && anchor != null)
                            gap = Vector3.Distance(spearT.position, anchor.position).ToString("F3") +
                                "m ← 0=矛根正好在持剑手上";
                        BSLog.Raw("  持矛手对齐: " + spearInfo + " | " + anchorInfo + " | 矛根↔手距离=" + gap);
                    }
                    catch (Exception e) { BSLog.Warn("[渲染诊断] 持矛手对齐异常: " + e); }
                }
                BSLog.Raw("\n[渲染诊断] 共 " + n + " 个黑矛兵\n");
            }
            catch (Exception e) { BSLog.Warn("[渲染诊断] 异常: " + e); }
        }

        /// <summary>黑矛兵盾牌两维体检（F8 触发）：每个黑矛兵分别输出 美术资源 与 实际效果。</summary>
        void DumpBlackSpearmanShields()
        {
            try
            {
                var vr = Plugin.BlackSpearman;
                if (vr == null) { BSLog.Raw("[盾牌体检] BlackSpearman 未注册"); return; }
                var agents = Resources.FindObjectsOfTypeAll<Agent>();
                int n = 0;
                foreach (var a in agents)
                {
                    if (a == null) continue;
                    var va = a.GetComponent<VikingAgent>();
                    if (va == null || !ReferenceEquals(va.vikingReference, vr)) continue;
                    n++;
                    BlackSpearmanWeapon.DumpShieldHealth(a, "[盾牌体检] #" + n + " " + a.name);
                }
                BSLog.Raw("\n[盾牌体检] 共 " + n + " 个黑矛兵\n");
            }
            catch (Exception e) { BSLog.Warn("[盾牌体检] 异常: " + e); }
        }

        /// <summary>
        /// 去剑诊断（黑矛兵生成时对前几只自动调用 + 供 F8 复用）：
        /// 打印完整 Transform 层级 + 每个 SpriteRenderer / SpriteAnimator 的 sprite/sprite2 详情，
        /// 用于确定"剑"的来源：(a) 独立子对象（名字未命中禁用关键字）(b) 动画帧 (c) sprite2 部件贴图。
        /// </summary>
        public static void DumpAgentSprites(Agent a)
        {
            try
            {
                if (a == null) return;
                BSLog.Raw("\n==== 去剑诊断: " + a.name + " ====");
                BSLog.Raw(BSLog.DumpHierarchy(a.gameObject, 8));
                var srs = a.GetComponentsInChildren<SpriteRenderer>(true);
                BSLog.Raw("  SpriteRenderer x" + srs.Length);
                foreach (var sr in srs)
                {
                    if (sr == null) continue;
                    string spr = "null";
                    string rect = "-";
                    if (sr.sprite != null)
                    {
                        spr = sr.sprite.name + "/" + (sr.sprite.texture != null ? sr.sprite.texture.name : "null");
                        rect = sr.sprite.textureRect.ToString();
                    }
                    string s2 = "-";
                    string col = "-";
                    var sa = sr.GetComponent<SpriteAnimator>();
                    if (sa != null)
                    {
                        if (sa.sprite2 != null)
                            s2 = sa.sprite2.name + "/" + (sa.sprite2.texture != null ? sa.sprite2.texture.name : "null");
                        Color c = sa.color;
                        col = "(" + c.r.ToString("F2") + "," + c.g.ToString("F2") + "," + c.b.ToString("F2") + "," + c.a.ToString("F2") + ")";
                    }
                    BSLog.Raw("  · " + sr.gameObject.name + " active=" + sr.gameObject.activeSelf +
                        " sprite=" + spr + " rect=" + rect +
                        (sa != null ? " | SpriteAnimator.sprite2=" + s2 + " color=" + col : ""));
                }
                if (a.animator != null)
                {
                    try { BSLog.Raw("  Animator Speed=" + a.animator.GetFloat("Speed").ToString("F2")); } catch { }
                }
                BSLog.Raw("==== 去剑诊断结束 ====");
            }
            catch (Exception e) { BSLog.Warn("[诊断] DumpAgentSprites 异常: " + e); }
        }

        void Heartbeat()
        {
            try
            {
                var vr = Plugin.BlackSpearman;
                BSLog.Raw("\n---- [心跳] ----");
                BSLog.Raw($"dict 注册表条目数 = {LevelStateObjectReferences.dict.Count}");
                BSLog.Raw($"dict 键 = {BSLog.Join(LevelStateObjectReferences.dict.Keys)}");
                BSLog.Raw($"新单位已注册 = {vr != null}");
                if (vr != null)
                    BSLog.Raw($"新单位 vikingClone={vr.vikingClone != null}, agent={vr.vikingClone != null && vr.vikingClone.agent != null}");
                BSLog.Raw($"已处理黑矛兵 Agent 数 = {Plugin.TrackedAgentCount}");
                BSLog.Raw("----------------");
            }
            catch (Exception e) { BSLog.Error("心跳异常: " + e); }
        }

        public void DumpFull()
        {
            try
            {
                DumpDict();
                DumpBlackSpearman();
                DumpSceneAgents();
            }
            catch (Exception e) { BSLog.Error("DumpFull 异常: " + e); }
        }

        static void DumpDict()
        {
            BSLog.Raw("\n[生成池注册表 LevelStateObjectReferences.dict]");
            foreach (var kv in LevelStateObjectReferences.dict)
            {
                var v = kv.Value;
                string desc = v == null ? "null" : (v ? v.GetType().Name : "destroyed");
                if (v is VikingReference vr)
                    desc += $" type={vr.type} bounty={vr.bounty} vikingClone={vr.vikingClone != null} agent={vr.vikingClone != null && vr.vikingClone.agent != null}";
                BSLog.Raw($"  {kv.Key} → {desc}");
            }
        }

        static void DumpBlackSpearman()
        {
            var vr = Plugin.BlackSpearman;
            if (vr == null) { BSLog.Raw("\n[新单位] 尚未注册"); return; }
            BSLog.Raw("\n[新单位 VikingReference 字段]");
            BSLog.Raw(BSLog.DumpFields(vr));
            if (vr.vikingClone != null)
            {
                BSLog.Raw("[新单位 vikingClone 层级]");
                BSLog.Raw(BSLog.DumpHierarchy(vr.vikingClone.gameObject, 4));
            }
        }

        static void DumpSceneAgents()
        {
            BSLog.Raw("\n[场景中的 VikingAgent 概况]");
            var all = UnityEngine.Object.FindObjectsOfType<VikingAgent>();
            int bs = 0;
            foreach (var va in all)
            {
                if (va == null) continue;
                if (va.vikingReference == Plugin.BlackSpearman) bs++;
            }
            BSLog.Raw($"  VikingAgent 总数={all.Length}, 黑矛兵={bs}, 其他={all.Length - bs}");
        }

        // ============ 供 Plugin 调用的快捷转储 ============

        public static void DumpVikingReference(VikingReference vr, string tag)
        {
            try
            {
                BSLog.Raw($"\n[{tag}] VikingReference 字段:");
                BSLog.Raw(BSLog.DumpFields(vr));
                BSLog.Raw($"[{tag}] GameObject 层级:");
                BSLog.Raw(BSLog.DumpHierarchy(vr.gameObject, 4));
            }
            catch (Exception e) { BSLog.Error("DumpVikingReference 异常: " + e); }
        }

        public static void DumpAgent(Agent a, string tag)
        {
            try
            {
                if (a == null) { BSLog.Raw($"[{tag}] Agent=null"); return; }
                var va = a.GetComponent<VikingAgent>();
                string vref = va != null && va.vikingReference != null ? va.vikingReference.name : "null";
                string brain = a.brain != null ? a.brain.GetType().Name : "null";
                BSLog.Raw($"\n[{tag}] Agent name={a.name} vikingRef={vref} type={(va != null ? va.type.ToString() : "?")} brain={brain}");
                BSLog.Raw(BSLog.DumpHierarchy(a.gameObject, 3));
            }
            catch (Exception e) { BSLog.Error("DumpAgent 异常: " + e); }
        }

        public static void DumpEnemies(List<VikingReference> enemies, string tag)
        {
            try
            {
                BSLog.Raw($"\n[{tag}] 本关敌人生成池 ({(enemies != null ? enemies.Count : 0)} 种):");
                if (enemies != null)
                    foreach (var e in enemies)
                        BSLog.Raw($"  - {(e != null ? e.name : "null")} (type={(e != null ? e.type.ToString() : "?")}, bounty={(e != null ? e.bounty : 0)})");
            }
            catch (Exception ex) { BSLog.Error("DumpEnemies 异常: " + ex); }
        }

        /// <summary>
        /// 测量我方长矛兵 Spear 的 spearAim/spearAnim 骨骼在"举矛/放矛"下的真实旋转，
        /// 用于校准黑矛兵 RaiseSpear 的角度。按 F9 触发。
        /// </summary>
        public static void DumpSpearPoses()
        {
            BSLog.Raw("\n========== [测量] 我方长矛兵 spear 骨骼姿态 ==========");
            try
            {
                var spears = Resources.FindObjectsOfTypeAll<Spear>();
                BSLog.Raw($"找到 Spear 组件 {spears.Length} 个");
                int n = 0;
                foreach (var s in spears)
                {
                    if (s == null) continue;
                    if (n++ >= 8) break;
                    try
                    {
                        BSLog.Raw($"── {s.name} ──");
                        BSLog.Raw($"   状态: spearUp={s.spearUp.active} spearDown={s.spearDown.active} charging={s.charging.active} stabbing={s.stabbing.active}");
                        BSLog.Raw($"   idealSpearTipDir={s.idealSpearTipDir.ToString("F3")}");
                        if (s.spearAim != null)
                            BSLog.Raw($"   spearAim.localPos={s.spearAim.localPosition.ToString("F3")}  localRot(euler)={s.spearAim.localRotation.eulerAngles.ToString("F2")}  worldRot(euler)={s.spearAim.rotation.eulerAngles.ToString("F2")}");
                        if (s.spearAnim != null)
                            BSLog.Raw($"   spearAnim.localPos={s.spearAnim.localPosition.ToString("F3")}  localRot(euler)={s.spearAnim.localRotation.eulerAngles.ToString("F2")}  worldRot(euler)={s.spearAnim.rotation.eulerAngles.ToString("F2")}");
                        BSLog.Raw($"   spearLength={s.spearLength}  spearSprite={s.spearSprite != null}");
                    }
                    catch (Exception e) { BSLog.Warn("[测量] " + e); }
                }
            }
            catch (Exception e) { BSLog.Warn("[测量] 扫描 Spear 失败: " + e); }

            // ---- 黑矛兵的长矛（我们克隆的 Spear_BlackSpearman） ----
            try
            {
                var allTrans = Resources.FindObjectsOfTypeAll<Transform>();
                int bn = 0;
                foreach (var t in allTrans)
                {
                    if (t == null || t.name != "Spear_BlackSpearman") continue;
                    if (bn++ >= 8) break;
                    try
                    {
                        BSLog.Raw($"  黑矛兵长矛 {t.name}: worldRot(euler)={t.rotation.eulerAngles.ToString("F1")}  localRot(euler)={t.localRotation.eulerAngles.ToString("F1")}  localPos={t.localPosition.ToString("F3")}");
                    }
                    catch (Exception e2) { BSLog.Warn("[测量] 黑矛 " + e2); }
                }
                if (bn == 0) BSLog.Raw("  （未找到黑矛兵长矛，可能尚未生成）");
            }
            catch (Exception e) { BSLog.Warn("[测量] 扫描黑矛兵长矛失败: " + e); }

            // ---- PikeChargeComponent 阶段状态 ----
            try
            {
                var pccs = Resources.FindObjectsOfTypeAll<PikeChargeComponent>();
                BSLog.Raw($"\n[测量] PikeChargeComponent {pccs.Length} 个");
                foreach (var pcc in pccs)
                {
                    if (pcc == null) continue;
                    try
                    {
                        BSLog.Raw($"  PCC {pcc.name}: pikeCharge={pcc.pikeCharge.active} anticipation={pcc.anticipation.active} charge={pcc.charge.active} travelling={pcc.travelling.active} arrived={pcc.arrived.active} energy={pcc.energy} walkSpeed={pcc.walkSpeed}");
                    }
                    catch (Exception e2) { BSLog.Warn("[测量] PCC " + e2); }
                }
            }
            catch (Exception e) { BSLog.Warn("[测量] 扫描 PikeChargeComponent 失败: " + e); }

            // ---- PikeChargeAbility 状态（含私有字段） ----
            try
            {
                var pcas = Resources.FindObjectsOfTypeAll<PikeChargeAbility>();
                BSLog.Raw($"\n[测量] PikeChargeAbility {pcas.Length} 个");
                foreach (var pca in pcas)
                {
                    if (pca == null) continue;
                    try
                    {
                        string chargingState = (pca.charging != null) ? pca.charging.active.ToString() : "null";
                        BSLog.Raw($"  PCA {pca.name}: charging={chargingState} dir={pca.dir.ToString("F2")}");
                        if (pca.settings != null)
                            BSLog.Raw($"    settings.range={pca.settings.range}  settings.speed={pca.settings.speed}");
                        else
                            BSLog.Raw("    settings=null");
                        BSLog.Raw(BSLog.DumpFields(pca));
                    }
                    catch (Exception e2) { BSLog.Warn("[测量] PCA " + e2); }
                }
            }
            catch (Exception e) { BSLog.Warn("[测量] 扫描 PikeChargeAbility 失败: " + e); }

            BSLog.Raw("========== 测量结束 ==========\n");
        }

        /// <summary>第十八轮：预制件分析（F8 触发）——注册表 VikingReference + 模板 + 运行实例的完整结构，
        /// 回答“黑矛兵到底由哪些预制件组成、哪些动画/状态可能引发抽动”。</summary>
        void DumpPrefabAnalysis()
        {
            try
            {
                var vr = Plugin.BlackSpearman;
                if (vr == null) { BSLog.Raw("\n[预制件分析] BlackSpearman 未注册"); return; }
                BSLog.Raw("\n[预制件分析] === VikingReference: type=" + vr.type + " bounty=" + vr.bounty);
                if (vr.agent != null)
                {
                    BSLog.Raw("[预制件分析] --- agent(运行实例)=" + vr.agent.name +
                        " pos=" + vr.agent.transform.position.ToString("F2") + " ---");
                    DumpHierarchy(vr.agent.transform, 0, 6);
                    DumpAgentPrefabDetail(vr.agent, "agent(实例)");
                }
                var f = typeof(VikingReference).GetField("viking",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                var viking = !ReferenceEquals(f, null) ? f.GetValue(vr) as VikingAgent : null;
                if (viking != null)
                {
                    BSLog.Raw("[预制件分析] --- viking(模板)=" + viking.name + " ---");
                    DumpHierarchy(viking.transform, 0, 6);
                    var vAgent = viking.GetComponent<Agent>();
                    if (vAgent != null) DumpAgentPrefabDetail(vAgent, "viking(模板)");
                }
                BSLog.Raw("[预制件分析] === 结束 ===\n");
            }
            catch (Exception e) { BSLog.Warn("[预制件分析] 失败: " + e); }
        }

        static void DumpHierarchy(Transform t, int depth, int maxDepth)
        {
            try
            {
                if (t == null || depth > maxDepth) return;
                string indent = new string(' ', depth * 2);
                var sb = new System.Text.StringBuilder(indent + t.name + "  [");
                bool first = true;
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(c.GetType().Name);
                    var sr = c as SpriteRenderer;
                    if (sr != null) sb.Append(" sprite=" + (sr.sprite != null ? sr.sprite.name : "null"));
                    var mr = c as MeshRenderer;
                    if (mr != null) sb.Append(" on=" + mr.enabled + " vis=" + mr.isVisible);
                }
                sb.Append("]  active=" + t.gameObject.activeSelf +
                    " localPos=" + t.localPosition.ToString("F3") +
                    " localRot=" + t.localRotation.eulerAngles.ToString("F1"));
                BSLog.Raw(sb.ToString());
                for (int i = 0; i < t.childCount; i++)
                {
                    try { DumpHierarchy(t.GetChild(i), depth + 1, maxDepth); }
                    catch { }
                }
            }
            catch { }
        }

        void DumpAgentPrefabDetail(Agent a, string label)
        {
            try
            {
                BSLog.Raw("[预制件分析] " + label + ": radius=" + a.radius.ToString("F2") +
                    " scale=" + a.scale.ToString("F2") + " maxSpeed=" + a.maxSpeed.ToString("F2") +
                    " movability=" + a.movability.ToString("F2") + " dangerous=" + a.dangerous +
                    " moveAnimate=" + a.moveAnimate +
                    " navValid=" + a.navPos.valid + " onMain=" + (a.navPos.valid ? a.navPos.onMain.ToString() : "-"));
                if (a.animator != null)
                {
                    var ctrl = a.animator.runtimeAnimatorController;
                    BSLog.Raw("[预制件分析]   Animator: controller=" + (ctrl != null ? ctrl.name : "null") +
                        " animSpeed=" + a.animator.speed.ToString("F2") +
                        " updateMode=" + a.animator.updateMode + " culling=" + a.animator.cullingMode);
                    try
                    {
                        var clips = ctrl != null ? ctrl.animationClips : null;
                        int clipN = clips != null ? clips.Length : -1;
                        BSLog.Raw("[预制件分析]   动画片段数=" + clipN);
                        var ci = a.animator.GetCurrentAnimatorClipInfo(0);
                        if (ci != null && ci.Length > 0 && ci[0].clip != null)
                            BSLog.Raw("[预制件分析]   当前动画=" + ci[0].clip.name);
                    }
                    catch { }
                }
                var sa = a.GetComponentInChildren<SpriteAnimator>(true);
                if (sa != null)
                    BSLog.Raw("[预制件分析]   SpriteAnimator: sprite=" + (sa.sprite != null ? sa.sprite.name : "null") +
                        " sprite2=" + (sa.sprite2 != null ? sa.sprite2.name + " rect=" + sa.sprite2.textureRect.ToString() : "null") +
                        " color=" + sa.color.ToString("F3"));
                Transform anchor = BlackSpearmanWeapon.FindSwordAnchor(a.transform);
                if (anchor != null)
                    BSLog.Raw("[预制件分析]   持剑锚点(Weapon骨)=" + anchor.name +
                        " local=" + anchor.localPosition.ToString("F3") + " active=" + anchor.gameObject.activeSelf +
                        " 路径=" + TransformPath(anchor));
                Transform spear = a.transform.Find("Spear_BlackSpearman");
                if (spear != null)
                    BSLog.Raw("[预制件分析]   长矛=" + spear.name + " active=" + spear.gameObject.activeSelf +
                        " localPos=" + spear.localPosition.ToString("F3") +
                        " worldRot=" + spear.rotation.eulerAngles.ToString("F1"));
            }
            catch (Exception e) { BSLog.Warn("[预制件分析] " + label + " 细节失败: " + e); }
        }

        static string TransformPath(Transform t)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                Transform cur = t;
                while (cur != null)
                {
                    if (sb.Length > 0) sb.Insert(0, "/");
                    sb.Insert(0, cur.name);
                    cur = cur.parent;
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }
    }
}
