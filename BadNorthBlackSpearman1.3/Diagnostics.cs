using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.RaidGeneration;

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
            }
            catch { }

            if (Time.time - _lastHeartbeat >= HeartbeatInterval)
            {
                _lastHeartbeat = Time.time;
                Heartbeat();
            }
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
    }
}
