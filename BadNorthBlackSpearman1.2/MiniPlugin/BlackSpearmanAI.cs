// ================================================================
// BlackSpearmanAI.cs — BadNorthBlackSpearman v1.2
// 可选迷你 BepInEx 插件：为新单位注入 SpearCharge + SpearStab 技能
//
// 将此 DLL 放入 BepInEx/plugins/ 即可生效。
// 如果不放，黑矛兵仍会出现（仅外观+数值差异，无特殊技能）。
// ================================================================

using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.RaidGeneration;

namespace BadNorthBlackSpearman1_2
{
    [BepInPlugin("badnorth.blackspearman.ai.v1.2",
        "BlackSpearman AI v1.2", "1.2.0")]
    public class BlackSpearmanAI : BaseUnityPlugin
    {
        static HashSet<Agent> _done = new HashSet<Agent>();

        void Awake()
        {
            // Hook Landing.Spawn → 为新生成的 BlackSpearman Agent 注入技能
            On.Voxels.TowerDefense.RaidGeneration.Landing.Spawn +=
                OnLandingSpawn;

            Logger.LogInfo("[BS-AI] Ready — waiting for BlackSpearman spawns");
        }

        static Longship OnLandingSpawn(
            On.Voxels.TowerDefense.RaidGeneration.Landing.orig_Spawn orig,
            Landing self)
        {
            var ship = orig(self);

            if (ship == null || ship.agents == null)
                return ship;

            foreach (var agent in ship.agents)
            {
                if (agent == null) continue;

                var va = agent.GetComponent<VikingAgent>();
                if (va == null) continue;

                // 检查是否为 BlackSpearman（枚举值 8）
                if ((int)va.type != 8) continue;

                if (!_done.Add(agent)) continue;

                // 注入冲刺技能
                var charge = agent.gameObject.AddComponent<SpearChargeComponent>();
                if (charge != null)
                {
                    charge.Setup(agent);
                }

                // 注入刺击技能
                agent.gameObject.AddComponent<SpearStabAction>();

                // 注册到 Brain.actions
                var brain = agent.brain as Swordsman;
                if (brain != null)
                {
                    try
                    {
                        var af = typeof(Brain).GetField("actions",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic);

                        if (af != null)
                        {
                            var acts = af.GetValue(brain) as System.Collections.IList;
                            if (acts != null)
                            {
                                if (charge != null && !acts.Contains(charge))
                                    acts.Add(charge);

                                var stab = agent.GetComponent<SpearStabAction>();
                                if (stab != null && !acts.Contains(stab))
                                    acts.Add(stab);
                            }
                        }
                    }
                    catch { }
                }
            }

            return ship;
        }
    }
}
