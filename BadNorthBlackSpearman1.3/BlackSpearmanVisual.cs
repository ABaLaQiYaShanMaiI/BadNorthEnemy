using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵黑色外观：每帧在 LateUpdate 强制顶点色 B 通道。
    /// ★ 第二十四轮（闪白根治）：原版 `Agent.aliveAndGrounded.OnUpdate += UpdateColor`（Agent.cs:418）每帧把
    ///   `spriteAnimator.color.b = 1 - healthFraction` —— B 通道是游戏的"受击白闪"通道。旧版周期写 B=0.01
    ///   （60/30 帧间隔）打不过它：受击时 b→1 → 身体闪白/暖色 = 用户所见"颜色不对劲+闪烁"。
    ///   现在每帧在 LateUpdate 重写（渲染前最后阶段，必然赢）：
    ///   - 身体（SpriteAnimator）：b 强制 0.02 → 恒黑，抑制受击白闪；
    ///   - 长矛/阴影（普通 BatchedSprite）：b 强制 1.0 → 保持原色（长矛可见、阴影正常）。
    ///   黑色本体由 SwordRemover 烘进部件贴图克隆（整体压暗 ×0.15），双保险。
    /// </summary>
    public class BlackSpearmanVisual : MonoBehaviour
    {
        Agent _agent;
        int _frames;
        // ★ 第二十五轮：长矛手部压暗 —— 玩家长矛精灵（Spear_0/1/2）自带两只暖肤的手（128x18 精灵 y8-12），
        //   克隆纹理并压暗全部暖肤像素（r-b>25 且 r>130），手变黑、矛杆保持原色。静态缓存按纹理实例 ID 共享。
        static readonly Dictionary<int, Texture2D> _spearTexCache = new Dictionary<int, Texture2D>();

        public void ApplyOnce(Agent agent)
        {
            _agent = agent;
            Recolor();
        }

        void LateUpdate()
        {
            Recolor();
            // ★ 第二十七轮（修正）：屏幕像素回读诊断——**只在登岛(onMain)后采样**（敌舰上身体 alpha=0 透明，采到的是海水；
            //   上轮 20 次采样全浪费在登岛前）。每 0.5s 采样第一只登岛黑矛兵的胸口实际渲染色，直到 30 次。
            if (_pixelSampleCount < 30)
            {
                bool onMain = _agent != null && _agent.navPos.valid && _agent.navPos.onMain;
                if (onMain)
                {
                    _pixelSampleTimer -= Time.deltaTime;
                    if (_pixelSampleTimer <= 0f)
                    {
                        _pixelSampleTimer = 0.5f;
                        StartCoroutine(SampleRenderedPixel());
                    }
                }
            }
        }

        float _pixelSampleTimer;
        static int _pixelSampleCount;   // 限前 30 次采样（防刷屏）
        bool _pixelNoCamLogged;         // 找不到相机时打印一次原因

        /// <summary>帧末读屏幕：采样黑矛兵胸口位置的 5x5 像素块，输出平均色/亮度 + 最亮像素。
        /// ★ 相机兜底：游戏主相机可能未标记 MainCamera → Camera.main 为 null 时退回 allCameras[0]；越界钳制；跳过原因打印一次。</summary>
        IEnumerator SampleRenderedPixel()
        {
            yield return new WaitForEndOfFrame();
            try
            {
                if (_agent == null) yield break;
                Camera cam = Camera.main;
                if (ReferenceEquals(cam, null))
                {
                    var cams = Camera.allCameras;
                    if (cams != null && cams.Length > 0) cam = cams[0];
                }
                if (ReferenceEquals(cam, null))
                {
                    if (!_pixelNoCamLogged) { _pixelNoCamLogged = true; BSLog.Warn("[像素采样] 未找到任何相机（Camera.allCameras 空），跳过"); }
                    yield break;
                }
                Vector3 body = _agent.chestPos;
                Vector3 sp = cam.WorldToScreenPoint(body);
                int px = Mathf.Clamp(Mathf.RoundToInt(sp.x), 3, Screen.width - 4);
                int py = Mathf.Clamp(Mathf.RoundToInt(sp.y), 3, Screen.height - 4);
                if (sp.z <= 0f || sp.x < 0 || sp.y < 0 || sp.x > Screen.width || sp.y > Screen.height)
                {
                    // 离屏：不计采样，等下次（不打印，防刷屏）
                    yield break;
                }
                var tex = new Texture2D(5, 5, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(px - 2, py - 2, 5, 5), 0, 0);
                tex.Apply();
                float maxBright = -1f;
                Color maxC = Color.black;
                Color sum = Color.black;
                for (int y = 0; y < 5; y++)
                {
                    for (int x = 0; x < 5; x++)
                    {
                        Color c = tex.GetPixel(x, y);
                        float br = c.r + c.g + c.b;
                        sum += c;
                        if (br > maxBright) { maxBright = br; maxC = c; }
                    }
                }
                UnityEngine.Object.Destroy(tex);
                Color avg = sum / 25f;
                float avgBright = (avg.r + avg.g + avg.b) / 3f;
                string flag = avgBright > 0.35f ? " ⚠️偏亮(疑似白闪!)" : "";
                _pixelSampleCount++;
                BSLog.Info("[像素采样] 胸屏幕(" + px + "," + py + ") 5x5平均=" + avg.ToString("F3") +
                    " 亮度=" + avgBright.ToString("F3") + " 最亮=" + maxC.ToString("F3") + flag);
            }
            catch { }
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
                    _frames++;
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
                        bool isBody = bs is SpriteAnimator;      // 身体 SpriteAnimator 的 R/G 存 UV 编码
                        if (isBody)
                        {
                            // ★ 第二十八轮（闪白根治·核心）：强制身体顶点 **alpha=1**（不透明）——
                            //   `Body.SetGrass` 只在"grass != shadow.a"时写 sprite 颜色，黑矛兵 shadow.a=0 时
                            //   若 grass 判定短路，身体 alpha 恒 0 → AlphaToMask 整块丢弃 → 身体透明、露出背景
                            //   （实测 [像素采样] 战斗中亮度 0.5~0.9 = 采到身后岛屿/海水 = 用户所见"闪白"）。
                            //   B 恒 0.02（黑色由部件贴图分区压暗烘进，B 只做受击白闪抑制）。
                            if (Mathf.Abs(c.b - 0.02f) > 0.02f || Mathf.Abs(c.a - 1f) > 0.02f)
                                bs.color = new Color(c.r, c.g, 0.02f, 1f);
                        }
                        else
                        {
                            // 长矛/阴影：B=1 保持原色；长矛额外压暗手部纹理
                            if (Mathf.Abs(c.b - 1f) > 0.02f)
                                bs.color = new Color(c.r, c.g, 1f, c.a);
                            if (bs.name != null && bs.name.IndexOf("Spear", StringComparison.Ordinal) >= 0)
                                DarkenSpearHand(bs);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>压暗长矛精灵纹理里的暖肤像素（持矛的手）→ 黑手；矛杆/矛头保持原色。
        /// 克隆共享纹理后，通过渲染器材质块 _MainTex 换克隆（与 SwordRemover 同款思路）。</summary>
        static void DarkenSpearHand(BatchedSprite bs)
        {
            try
            {
                Sprite sp = GetSpriteOf(bs);
                if (sp == null || sp.texture == null) return;
                var tex = sp.texture as Texture2D;
                if (tex == null) return;
                int key = tex.GetInstanceID();
                Texture2D clone;
                if (!_spearTexCache.TryGetValue(key, out clone))
                {
                    clone = CloneTex(tex);
                    if (clone == null) return;
                    Color32[] px = clone.GetPixels32();
                    int w = clone.width, h = clone.height;
                    int n = 0;
                    for (int i = 0; i < px.Length; i++)
                    {
                        Color32 c = px[i];
                        if (c.a <= 8) continue;
                        if (c.r - c.b > 25 && c.r > 130)   // 暖肤 = 手
                        {
                            px[i] = new Color32((byte)(c.r * 0.15f), (byte)(c.g * 0.15f), (byte)(c.b * 0.15f), c.a);
                            n++;
                        }
                    }
                    if (n > 0) { clone.SetPixels32(px); clone.Apply(); }
                    _spearTexCache[key] = clone;
                    BSLog.Info("[渲染] 长矛手部压暗 " + n + "px（" + sp.name + "）→ 持矛的手变黑，矛杆保持原色");
                }
                // 换材质块 _MainTex 为克隆（所有渲染器）
                var mrs = bs.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs == null) return;
                var block = new MaterialPropertyBlock();
                for (int i = 0; i < mrs.Length; i++)
                {
                    if (mrs[i] == null) continue;
                    try
                    {
                        mrs[i].GetPropertyBlock(block);
                        block.SetTexture("_MainTex", clone);
                        mrs[i].SetPropertyBlock(block);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static Sprite GetSpriteOf(BatchedSprite bs)
        {
            var sa = bs as SpriteAnimator;
            if (sa != null) return sa.sprite;
            try
            {
                var f = typeof(BatchedSprite).GetField("_spriteRenderer",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (ReferenceEquals(f, null)) return null;
                var sr = f.GetValue(bs) as SpriteRenderer;
                return sr != null ? sr.sprite : null;
            }
            catch { return null; }
        }

        static Texture2D CloneTex(Texture2D src)
        {
            try
            {
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
                var old = RenderTexture.active;
                RenderTexture.active = rt;
                Graphics.Blit(src, rt);
                var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                tex.Apply();
                RenderTexture.active = old;
                RenderTexture.ReleaseTemporary(rt);
                return tex;
            }
            catch { return null; }
        }
    }
}
