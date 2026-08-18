using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthMixedSquad1_0
{
    /// <summary>
    /// 死亡腾空/受击双影诊断探针：①死亡→落地紧凑轨迹；②钩 CorpseManager.AddCorpse 判双尸
    /// （静态尸体烘焙 vs 飞行身体偏移）；③探测分身三类来源（镜像复启用/主身离位/SpriteRenderer 双重渲染）。
    /// 按 BSLog.DeathTrace / BSLog.HitDoubleTrace 门控。
    /// </summary>
    public class BlackSpearmanDiagProbe : MonoBehaviour
    {
        Agent _agent;
        SpriteAnimator _sa;
        bool _wasAlive = true;
        bool _deathStarted;
        float _deathStartY;
        int _flightFrames;
        bool _corpseNearLogged;
        float _hitWarnTimer;
        bool _mirrorStateWarned;
        bool _spriteDoubleWarned;
        bool _bodyDivWarned;
        // 去重影：SingleBodyMode（0=关 1=禁镜像 2=只留动画主身）
        int _singleBodyMode;
        float _reassertTimer;

        static bool _hooked;   // CorpseManager.AddCorpse 钩子只注册一次
        static readonly HashSet<int> _trackedSaIds = new HashSet<int>();

        public void Setup(Agent agent)
        {
            _agent = agent;
            if (_agent == null) { Destroy(this); return; }
            _sa = _agent.GetComponentInChildren<SpriteAnimator>(true);
            if (_sa != null) _trackedSaIds.Add(_sa.GetInstanceID());
            _wasAlive = _agent.aliveState != null && _agent.aliveState.active;
            RegisterCorpseHook();
            ApplySingleBodyMode();   // 去重影（按 Diag.SingleBodyMode）
        }

        void OnDestroy()
        {
            if (_sa != null) _trackedSaIds.Remove(_sa.GetInstanceID());
        }

        // ============ 去重影（SingleBodyMode） ============
        // ColoredCharacter 着色器是 Cull Off（shader_076.txt 实测）→ 主身与 _MIRROR_ON 镜像会同时绘制。
        // 日志实测启动时 4 个身体渲染器（2主+2镜像）全 enabled → 4 份重叠绘制 = 全局重影。
        // 由于 Cull Off，禁镜像不会让背对时角色消失（主身永远绘制），可安全禁用。

        void ApplySingleBodyMode()
        {
            try
            {
                _singleBodyMode = ModConfig.DiagSingleBodyMode != null ? ModConfig.DiagSingleBodyMode.Value : 0;
                if (_singleBodyMode <= 0) return;
                if (_sa == null) return;
                var mrs = _sa.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs == null) return;
                int mirrors = 0, mains = 0, disabled = 0;
                bool mirrorEnabled = false, mainEnabled = false;
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    string nm = mr.gameObject.name != null ? mr.gameObject.name : "?";
                    bool isMirror = nm.IndexOf("_MIRROR_ON", StringComparison.Ordinal) >= 0;
                    if (isMirror) { mirrors++; if (mr.enabled) mirrorEnabled = true; }
                    else { mains++; if (mr.enabled) mainEnabled = true; }
                }
                if (mirrorEnabled && mainEnabled)
                    BSLog.Warn("[单身] ⚠️ 改造前 主身与镜像渲染器同时启用（Cull Off → 多副本同绘 = 重影来源）主身=" + mains + " 镜像=" + mirrors);

                // 主身渲染器有"静态"与"动画"两份——
                // [死亡分裂] 实测：前面的主身 UV0 恒 (0.152,0.384)=Swordsman0001 静态帧（不随动画更新），
                // 最后一个主身 UV0 随帧变化 = 动画/律动渲染器。Mode2 必须**保留最后一个主身**（动画源），
                // 禁用前面的静态主身 → 单渲染器无重影 + 动画律动保留。
                int mainKept = 0;
                string keptInfo = "";
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    string nm = mr.gameObject.name != null ? mr.gameObject.name : "?";
                    bool isMirror = nm.IndexOf("_MIRROR_ON", StringComparison.Ordinal) >= 0;
                    if (_singleBodyMode >= 1 && isMirror)
                    {
                        if (mr.enabled) { mr.enabled = false; disabled++; }
                        continue;
                    }
                    if (_singleBodyMode >= 2 && !isMirror)
                    {
                        mainKept++;
                        if (mainKept < mains)
                        {
                            if (mr.enabled) { mr.enabled = false; disabled++; }
                        }
                        else if (mr.enabled)
                        {
                            var mf = mr.GetComponent<MeshFilter>();
                            string uv0 = (mf != null && mf.sharedMesh != null && mf.sharedMesh.uv != null && mf.sharedMesh.uv.Length > 0)
                                ? mf.sharedMesh.uv[0].ToString("F3") : "?";
                            keptInfo = nm + " uv0=" + uv0;
                        }
                    }
                }
                if (disabled > 0)
                    BSLog.Warn("[单身] 已按 SingleBodyMode=" + _singleBodyMode + " 禁用 " + disabled +
                        " 个身体渲染器（主身=" + mains + " 镜像=" + mirrors + "），保留动画主身[" + keptInfo + "] → 无重影且律动保留；若朝向/背对异常请改回 cfg=0");
            }
            catch { }
        }

        /// <summary>周期复断言：防游戏在运行期把被禁用的渲染器重新启用（每 ~1s）。</summary>
        void ReassertSingleBody()
        {
            try
            {
                if (_singleBodyMode <= 0 || _sa == null) return;
                var mrs = _sa.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs == null) return;
                int mains = 0;
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    string nm = mr.gameObject.name != null ? mr.gameObject.name : "?";
                    if (nm.IndexOf("_MIRROR_ON", StringComparison.Ordinal) < 0) mains++;
                }
                int mainKept = 0;
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null) continue;
                    var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                    if (sh == null || sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) < 0) continue;
                    string nm = mr.gameObject.name != null ? mr.gameObject.name : "?";
                    bool isMirror = nm.IndexOf("_MIRROR_ON", StringComparison.Ordinal) >= 0;
                    if (_singleBodyMode >= 1 && isMirror)
                    {
                        if (mr.enabled) mr.enabled = false;
                        continue;
                    }
                    if (_singleBodyMode >= 2 && !isMirror)
                    {
                        mainKept++;
                        if (mainKept < mains && mr.enabled) mr.enabled = false;
                    }
                }
            }
            catch { }
        }

        // ============ ① 静态尸体烘焙钩子（CorpseManager.AddCorpse） ============

        static void RegisterCorpseHook()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                On.Voxels.TowerDefense.CorpseManager.AddCorpse += CorpseManagerAddCorpseHook;
                BSLog.Info("[探针] 已钩住 CorpseManager.AddCorpse（静态尸体烘焙时刻追踪）");
            }
            catch (Exception e)
            {
                BSLog.Warn("[探针] CorpseManager.AddCorpse 钩子注册失败: " + e);
            }
        }

        static void CorpseManagerAddCorpseHook(
            On.Voxels.TowerDefense.CorpseManager.orig_AddCorpse orig,
            CorpseManager self, Matrix4x4 matrix, SpriteAnimator spriteAnimator, NavPos navPos)
        {
            try
            {
                if (spriteAnimator != null && _trackedSaIds.Contains(spriteAnimator.GetInstanceID()))
                {
                    Transform st = spriteAnimator.transform;
                    Vector3 corpsePos = matrix.MultiplyPoint(Vector3.zero);
                    Vector3 bodyPos = st != null ? st.position : corpsePos;
                    float off = Vector3.Distance(corpsePos, bodyPos);
                    // 0.1~0.3m 多为"脚部 pivot 差"（尸体铺地 vs 身体 billboard 中心），不算双尸；
                    // 真正双尸 = 身体还在明显腾空（抬升大）时尸体已铺到地面。
                    string verdict;
                    if (off > 0.3f)
                        verdict = " ← 偏移>0.3m：身体与静态尸明显分离 = 双尸候选";
                    else if (off > 0.05f)
                        verdict = " ← 偏移小(pivot差)，落地正常烘焙";
                    else
                        verdict = " ← 身体与尸体同位（正常）";
                    BSLog.Warn("[腾空·尸体烘焙] 静态尸体被烘焙 AddCorpse" +
                        " 尸体位=" + corpsePos.ToString("F2") +
                        " 身体位=" + bodyPos.ToString("F2") +
                        " 偏移=" + off.ToString("F2") + "m" + verdict);
                }
            }
            catch (Exception e)
            {
                BSLog.Warn("[探针] AddCorpse 钩子异常: " + e);
            }
            try { orig(self, matrix, spriteAnimator, navPos); }
            catch (Exception e) { BSLog.Warn("[探针] AddCorpse 原版调用异常: " + e); }
        }

        // ============ ② 死亡腾空轨迹（每 ~5 帧一行） ============

        void Update()
        {
            try
            {
                if (_agent == null || _agent.aliveState == null) return;
                bool alive = _agent.aliveState.active;

                if (_wasAlive && !alive && !_deathStarted)
                {
                    _deathStarted = true;
                    _deathStartY = _agent.transform.position.y;
                    _flightFrames = 0;
                    _corpseNearLogged = false;
                    if (BSLog.DeathTrace)
                    {
                        BSLog.Warn("[腾空]  死亡开始（死亡→落地追踪开启）" +
                            " pos=" + _agent.transform.position.ToString("F2") +
                            " sprite=" + (_sa != null && _sa.sprite != null ? _sa.sprite.name : "?") +
                            " 顶点色=" + (_sa != null ? _sa.color.ToString("F2") : "?") +
                            " ragdoll=" + (_agent.ragdoller != null && _agent.ragdoller.enabled));
                    }
                }

                if (_deathStarted)
                {
                    _flightFrames++;
                    if (BSLog.DeathTrace && _flightFrames % 5 == 0) TraceFlight();
                    if (_flightFrames > 75) enabled = false;   // ≈1.25s 兜底（FinalDeath 会 Destroy 本组件）
                }
                else if (BSLog.HitDoubleTrace)
                {
                    CheckDoubleImage();
                }

                // 周期复断言去重影（防游戏运行期重新启用被禁的渲染器）
                if (Time.time - _reassertTimer > 1f)
                {
                    _reassertTimer = Time.time;
                    ReassertSingleBody();
                }

                _wasAlive = alive;
            }
            catch { }
        }

        void TraceFlight()
        {
            try
            {
                if (_sa == null) return;
                var mrs = _sa.GetComponentsInChildren<MeshRenderer>(true);
                int mainMesh = 0, total = 0, nullBlock = 0;
                bool meshBad = false;   // 顶点全零=正常 billboard；只当 uv2 缺失/空才算坏
                string second = "无";
                var block = new MaterialPropertyBlock();
                if (mrs != null)
                {
                    Vector3 bodyPos = _agent.transform.position;
                    for (int i = 0; i < mrs.Length; i++)
                    {
                        var mr = mrs[i];
                        if (mr == null) continue;
                        var sh = mr.sharedMaterial != null ? mr.sharedMaterial.shader : null;
                        bool isBody = sh != null && sh.name.IndexOf("ColoredCharacter", StringComparison.Ordinal) >= 0;
                        string nm = mr.gameObject.name != null ? mr.gameObject.name : "?";
                        if (isBody)
                        {
                            total++;
                            if (mr.enabled) mainMesh++;
                            var mf = mr.GetComponent<MeshFilter>();
                            if (mf != null && mf.sharedMesh != null)
                            {
                                var uv2 = mf.sharedMesh.uv2;
                                var tg = mf.sharedMesh.tangents;
                                if (uv2 == null || uv2.Length < 4 || tg == null || tg.Length < 4 ||
                                    (uv2.Length >= 4 && uv2[0].sqrMagnitude < 0.0001f))
                                    meshBad = true;
                            }
                            else meshBad = true;
                            try { mr.GetPropertyBlock(block); } catch { }
                            Texture mt = null, pt = null;
                            try { mt = block.GetTexture("_MainTex"); } catch { }
                            try { pt = block.GetTexture("_PartTex"); } catch { }
                            if (mt == null || pt == null) nullBlock++;
                        }
                        else if (mr.enabled)
                        {
                            // 非身体渲染器仍启用且离身体有明显偏移 = 第二身影候选
                            float d = Vector3.Distance(mr.transform.position, bodyPos);
                            if (d > 0.08f)
                                second = " [" + nm + " 偏移=" + d.ToString("F2") + "m]";
                        }
                    }
                }
                string meshState = meshBad ? "uv2/tangent缺失(异常)" : "正常";
                string blockState = nullBlock > 0 ? ("空块×" + nullBlock + "=白") : "克隆✓";

                // 静态尸体是否已烘焙到附近（CorpseObject 组合网格包围盒）
                string corpseNear = "无";
                if (!_corpseNearLogged)
                {
                    Vector3 cpos = _agent.transform.position;
                    var cos = Resources.FindObjectsOfTypeAll<CorpseObject>();
                    if (cos != null)
                    {
                        for (int i = 0; i < cos.Length; i++)
                        {
                            var co = cos[i];
                            if (co == null) continue;
                            var mr = co.GetComponent<MeshRenderer>();
                            if (mr == null || !mr.gameObject.activeInHierarchy) continue;
                            float d = Vector3.Distance(mr.bounds.center, cpos);
                            if (d < 0.8f)
                            {
                                corpseNear = "是(偏移=" + d.ToString("F2") + "m)";
                                _corpseNearLogged = true;
                            }
                        }
                    }
                }

                BSLog.Warn("[腾空] f=" + _flightFrames +
                    " 身位=" + _agent.transform.position.ToString("F2") +
                    " 抬升=" + (_agent.transform.position.y - _deathStartY).ToString("F2") + "m" +
                    " 网格=" + meshState + " 块=" + blockState +
                    " 主身=" + mainMesh + "/" + total +
                    " 第二身=" + second +
                    " 静态尸=" + corpseNear);
            }
            catch { }
        }

        // ============ ③ 受击/平时：两重分身候选探测 ============

        void CheckDoubleImage()
        {
            try
            {
                if (_sa == null) return;
                var mrs = _sa.GetComponentsInChildren<MeshRenderer>(true);
                if (mrs == null) return;

                // a) _MIRROR_ON 镜像渲染器被启用（应已被 SwordRemover 禁用，若复启用 = 双影候选）
                if (!_mirrorStateWarned)
                {
                    for (int i = 0; i < mrs.Length; i++)
                    {
                        var mr = mrs[i];
                        if (mr == null || !mr.enabled) continue;
                        string nm = mr.gameObject.name != null ? mr.gameObject.name : "?";
                        if (nm.IndexOf("_MIRROR_ON", StringComparison.Ordinal) >= 0)
                        {
                            _mirrorStateWarned = true;
                            BSLog.Warn("[分身] ⚠️ _MIRROR_ON 镜像渲染器处于启用状态: " + nm +
                                " w=" + mr.transform.position.ToString("F2") +
                                " —— 镜像翻面与主身偏移 = 两重分身候选来源（每黑矛兵仅报一次）");
                        }
                    }
                }

                // b) 两个主身体 MeshRenderer 世界位置离位（应恒同位）
                if (!_bodyDivWarned)
                {
                    Vector3? first = null;
                    for (int i = 0; i < mrs.Length; i++)
                    {
                        var mr = mrs[i];
                        if (mr == null || !mr.enabled) continue;
                        string nm = mr.gameObject.name != null ? mr.gameObject.name : "?";
                        if (nm.IndexOf("_MIRROR_ON", StringComparison.Ordinal) >= 0) continue;
                        if (nm.IndexOf("BodySprite", StringComparison.Ordinal) < 0) continue;
                        if (first == null) first = mr.transform.position;
                        else if (Vector3.Distance(first.Value, mr.transform.position) > 0.06f)
                        {
                            _bodyDivWarned = true;
                            BSLog.Warn("[分身] ⚠️ 两个主身体 MeshRenderer 离位 " +
                                Vector3.Distance(first.Value, mr.transform.position).ToString("F2") + "m" +
                                " —— 双影来源（每黑矛兵仅报一次）");
                        }
                    }
                }

                // c) BodySprite 的 SpriteRenderer(原始帧) 同时启用且有 alpha —— 双重渲染候选
                // （节流 2s；正常应 disabled 或 alpha=0；若可见 = 原始剑/亮身压在黑色克隆上）
                if (!_spriteDoubleWarned && Time.time - _hitWarnTimer > 2f)
                {
                    var srs = _sa.GetComponentsInChildren<SpriteRenderer>(true);
                    if (srs != null)
                    {
                        for (int i = 0; i < srs.Length; i++)
                        {
                            var sr = srs[i];
                            if (sr == null || !sr.enabled || sr.sprite == null) continue;
                            string nm = sr.gameObject.name != null ? sr.gameObject.name : "?";
                            if (nm.IndexOf("BodySprite", StringComparison.Ordinal) < 0) continue;
                            if (sr.color.a > 0.5f)
                            {
                                _spriteDoubleWarned = true;
                                _hitWarnTimer = Time.time;
                                BSLog.Warn("[分身] ⚠️ BodySprite.SpriteRenderer 可见(alpha=" +
                                    sr.color.a.ToString("F2") + ") spr=" + sr.sprite.name +
                                    " 与黑色克隆 MeshRenderer 同时渲染 = 双重渲染（原始帧可见）");
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }
}

