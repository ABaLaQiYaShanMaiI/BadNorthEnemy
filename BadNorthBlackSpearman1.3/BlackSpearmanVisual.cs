using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵黑色外观：一次性染色 + 持续对抗 AgentTextureBaker 的纹理重烘焙覆盖。
    /// ★ 第二十三轮：黑色改由 SwordRemover 烘进部件贴图克隆（PartTex 整体压暗），顶点色 B 恢复 1.0——
    ///   旧版用顶点色 B=0.01 乘算做黑，若被重置/未生效身体就显示原色（暖棕）→ "身体颜色不对劲+闪烁"。
    ///   这里只在 B 偏离 1.0 时修复（防每帧写网格顶点色）；前 60 帧每帧重刷、之后每 30 帧兜底一次。
    /// </summary>
    public class BlackSpearmanVisual : MonoBehaviour
    {
        Agent _agent;
        int _frames;

        public void ApplyOnce(Agent agent)
        {
            _agent = agent;
            Recolor();
        }

        void LateUpdate()
        {
            if (_frames < 60)
            {
                _frames++;
                Recolor();
            }
            else if (Time.frameCount % 30 == 0)
            {
                Recolor();
            }
        }

        void Recolor()
        {
            if (_agent == null) return;
            try
            {
                var all = _agent.GetComponentsInChildren<BatchedSprite>(true);
                if (all == null) return;
                if (_frames < 5)
                {
                    BSLog.Info("[VISUAL] 染色帧#" + _frames + ": " + all.Length + " 个 BatchedSprite");
                    foreach (var bs in all)
                    {
                        if (bs == null) continue;
                        Color c = bs.color;
                        BSLog.Info("[VISUAL]   · " + bs.name + ": color=(" +
                            c.r.ToString("F2") + "," + c.g.ToString("F2") + "," + c.b.ToString("F2") + "," + c.a.ToString("F2") + ")");
                    }
                }
                foreach (var bs in all)
                {
                    if (bs == null) continue;
                    try
                    {
                        var c = bs.color;
                        // 黑色已烘进部件贴图克隆 → B 恢复 1.0（不再乘算压黑）
                        if (Mathf.Abs(c.b - 1f) > 0.02f)
                            bs.color = new Color(c.r, c.g, 1f, c.a);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
