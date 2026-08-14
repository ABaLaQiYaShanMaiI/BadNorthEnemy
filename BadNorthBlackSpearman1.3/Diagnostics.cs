using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.RaidGeneration;
using Voxels.TowerDefense.Upgrades;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 运行时诊断探针：周期心跳 + 按键(F8)触发完整转储。
    /// 收集"比报错日志更多"的现场信息：生成池注册表、新建预制件状态、敌舰装载、Agent 组件等。
    /// </summary>
    public class DiagnosticsComponent : MonoBehaviour
    {
        public static DiagnosticsComponent Instance { get; private set; }

        const float HeartbeatInterval = 8f;
        float _lastHeartbeat;
        float _autoTimer = 0.1f;

        void Awake()
        {
            Instance = this;
        }

        void Update()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    BSLog.Raw("\n==================== 手动完整诊断 (F8) ====================");
                    DumpFull();
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

            AutoMeasurePikeCharge();
        }

        void AutoMeasurePikeCharge()
        {
            _autoTimer -= Time.deltaTime;
            if (_autoTimer > 0f) return;
            _autoTimer = 0.1f;

            var pccs = Resources.FindObjectsOfTypeAll<PikeChargeComponent>();
            bool any = false;
            foreach (var pcc in pccs)
            {
                if (pcc != null && pcc.pikeCharge.active) { any = true; break; }
            }
            if (!any) return;

            BSLog.Raw("\n[自动测量] PikeCharge 进行中:");
            foreach (var pcc in pccs)
            {
                if (pcc == null || !pcc.pikeCharge.active) continue;
                try
                {
                    BSLog.Raw($"  PCC {pcc.name}: anticipation={pcc.anticipation.active} charge={pcc.charge.active} travelling={pcc.travelling.active} arrived={pcc.arrived.active} energy={pcc.energy}");
                    var spear = pcc.spear;
                    if (spear != null)
                    {
                        if (spear.spearAim != null)
                            BSLog.Raw($"    spearAim.worldRot(euler)={spear.spearAim.rotation.eulerAngles.ToString("F1")} localPos={spear.spearAim.localPosition.ToString("F3")}");
                        if (spear.spearAnim != null)
                            BSLog.Raw($"    spearAnim.localRot(euler)={spear.spearAnim.localRotation.eulerAngles.ToString("F1")} worldRot(euler)={spear.spearAnim.rotation.eulerAngles.ToString("F1")} localPos={spear.spearAnim.localPosition.ToString("F3")}");
                        BSLog.Raw($"    spear: up={spear.spearUp.active} down={spear.spearDown.active} stabbing={spear.stabbing.active} idealTip={spear.idealSpearTipDir.ToString("F2")}");
                    }
                }
                catch (Exception e) { BSLog.Warn("[自动测量] " + e); }
            }
            BSLog.Raw("[自动测量结束]");
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

        public static void DumpShip(Longship ship)
        {
            try
            {
                if (ship == null || ship.agents == null) { BSLog.Raw("\n[敌舰] ship 或 agents 为空"); return; }
                BSLog.Raw($"\n[敌舰] agents 数量={ship.agents.Count}");
                foreach (var a in ship.agents)
                {
                    if (a == null) { BSLog.Raw("  - <null>"); continue; }
                    var va = a.GetComponent<VikingAgent>();
                    string vref = va != null && va.vikingReference != null ? va.vikingReference.name : "null";
                    string brain = a.brain != null ? a.brain.GetType().Name : "null";
                    BSLog.Raw($"  - {a.name} vikingRef={vref} brain={brain}");
                }
            }
            catch (Exception e) { BSLog.Error("DumpShip 异常: " + e); }
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
    }
}
