using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵黑色外观：一次性染色 + 持续对抗 AgentTextureBaker 的纹理重烘焙覆盖。
    /// 前 60 帧每帧重刷，之后每 30 帧重刷一次（与 v1.1 的策略一致）。
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
                foreach (var bs in all)
                {
                    if (bs == null) continue;
                    try
                    {
                        var c = bs.color;
                        // R/G 是 UV 编码，只把 B 通道压到近乎 0 → 黑色
                        bs.color = new Color(c.r, c.g, 0.01f, c.a);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
