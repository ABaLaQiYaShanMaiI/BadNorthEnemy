using System;
using System.Collections.Generic;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.SpriteMagic;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 第十八轮：人物抽动探针（诊断专用，不改逻辑）。
    /// 逐帧监控每个黑矛兵的：身体位置/朝向跳变、动画倒退、navPos↔transform 错位（橡皮筋）、
    /// 长矛翻转、精灵帧闪动——任一异常打一行 [抽动] 日志（含完整现场：pos/yaw/navLag/clip/phase/矛姿态）。
    /// 由 Plugin.ApplyToAgent 挂载；节流防刷屏（每实例 0.5s 一条）。
    /// 触发口径：
    ///   ① 位置跳变 &gt;0.35m/帧（非滑行）  ② 朝向急转 &gt;40°/帧（冲锋/后退排除）
    ///   ③ 动画倒退（同 clip 下 norm 回落 &gt;0.3）  ④ 橡皮筋（navPos-transform 差速变化 &gt;0.35m/帧）
    ///   ⑤ 长矛本地 yaw 翻转 &gt;90°/帧  ⑥ 精灵帧闪动（1s 内 ≥3 种帧名且变化 ≥4 次）
    /// </summary>
    public class TwitchProbeComponent : MonoBehaviour
    {
        Agent _agent;
        Transform _tf;
        Transform _spear;

        Vector3 _prevPos;
        float _prevYaw = -999f;
        string _prevClip = "";
        float _prevNorm = -1f;
        float _prevNavLag = -1f;
        float _prevSpearYaw = -999f;
        bool _prevValid;

        float _lastLogTime = -999f;

        // 精灵帧闪动检测：近 1s 内的 (时间, 帧名) 序列
        readonly List<KeyValuePair<float, string>> _spriteHistory = new List<KeyValuePair<float, string>>();

        public void Setup(Agent agent)
        {
            _agent = agent;
            if (_agent == null) return;
            _tf = _agent.transform;
            _spear = _tf.Find("Spear_BlackSpearman");
        }

        void LateUpdate()
        {
            if (_agent == null || _tf == null) return;
            try { Probe(); } catch { }
        }

        void Probe()
        {
            float now = Time.time;
            Vector3 pos = _tf.position;
            float yaw = _tf.rotation.eulerAngles.y;

            // 动画状态
            string clip = "?";
            float norm = -1f;
            string animSpeed = "?";
            try
            {
                if (_agent.animator != null)
                {
                    var si = _agent.animator.GetCurrentAnimatorStateInfo(0);
                    clip = si.fullPathHash.ToString();
                    norm = si.normalizedTime;
                    animSpeed = _agent.animator.GetFloat("Speed").ToString("F2");
                }
            }
            catch { }

            // 当前精灵帧名
            string sprite = "?";
            try
            {
                var sa = _agent.GetComponentInChildren<SpriteAnimator>(true);
                if (sa != null && sa.sprite != null) sprite = sa.sprite.name;
            }
            catch { }

            // navPos↔transform 错位（橡皮筋）
            float navLag = -1f;
            try { if (_agent.navPos.valid) navLag = Vector3.Distance(_agent.navPos.wPos, pos); } catch { }

            // 长矛本地 yaw（相对身体的朝向）
            float spearYaw = -999f;
            try { if (_spear != null) spearYaw = _spear.localRotation.eulerAngles.y; } catch { }

            // 冲锋/刺击阶段 + 身体状态
            string phase = "-";
            var charge = GetComponent<SpearChargeComponent>();
            if (charge != null) phase = charge.PhaseLabel;
            string body = DescribeBody();

            bool fire = false;
            string reason = "";

            if (_prevValid)
            {
                float posDelta = Vector3.Distance(pos, _prevPos);
                if (posDelta > 0.35f && (_agent.body == null || !_agent.body.sliding.active))
                {
                    fire = true; reason = "①位置跳变 " + posDelta.ToString("F2") + "m/帧";
                }
                else
                {
                    float yawDelta = Mathf.DeltaAngle(yaw, _prevYaw);
                    if (Mathf.Abs(yawDelta) > 40f && phase != "Charging" && phase != "Retreat" &&
                        (_agent.body == null || !_agent.body.sliding.active))
                    {
                        fire = true; reason = "②朝向急转 " + yawDelta.ToString("F1") + "°/帧";
                    }
                }

                if (clip == _prevClip && _prevNorm >= 0f && norm >= 0f && (norm - _prevNorm) < -0.3f)
                {
                    fire = true; reason = "③动画倒退 " + _prevNorm.ToString("F2") + "→" + norm.ToString("F2");
                }

                if (navLag >= 0f && _prevNavLag >= 0f && Mathf.Abs(navLag - _prevNavLag) > 0.35f)
                {
                    fire = true; reason = "④橡皮筋 navLag " + _prevNavLag.ToString("F2") + "→" + navLag.ToString("F2") + "m";
                }

                if (spearYaw >= -999f && _prevSpearYaw >= -999f && Mathf.Abs(Mathf.DeltaAngle(_prevSpearYaw, spearYaw)) > 90f)
                {
                    fire = true; reason = "⑤长矛翻转 " + _prevSpearYaw.ToString("F1") + "→" + spearYaw.ToString("F1") + "°";
                }
            }

            // ⑥ 精灵帧闪动：只在单位"静止/站桩"时才算异常（走路/跑步时帧循环变化是正常动画）。
            //   第十九轮：旧口径把"移动中帧变化"误报成闪动（1s 内 ≥3 帧名 恒真），刷屏且掩盖真异常；
            //   移动（Body.stepping / navPos 与 transform 有位移 / 上一帧有位移 / 冲锋阶段）时跳过本项。
            bool animMoving = false;
            try
            {
                var b = _agent.body;
                if (b != null && b.stepping != null && b.stepping.active) animMoving = true;
            }
            catch { }
            if (!animMoving)
            {
                try { if (_agent.navPos.valid && Vector3.Distance(_agent.navPos.wPos, pos) > 0.05f) animMoving = true; }
                catch { }
            }
            if (!animMoving && _prevValid && Vector3.Distance(pos, _prevPos) > 0.02f) animMoving = true;
            if (!animMoving && (phase == "Charging" || phase == "WindUp" || phase == "Retreat")) animMoving = true;

            if (!animMoving)
            {
                _spriteHistory.Add(new KeyValuePair<float, string>(now, sprite));
                while (_spriteHistory.Count > 0 && now - _spriteHistory[0].Key > 1f) _spriteHistory.RemoveAt(0);
                int changeCount = 0;
                var distinct = new HashSet<string>();
                for (int i = 0; i < _spriteHistory.Count; i++)
                {
                    distinct.Add(_spriteHistory[i].Value);
                    if (i > 0 && _spriteHistory[i].Value != _spriteHistory[i - 1].Value) changeCount++;
                }
                if (distinct.Count >= 3 && changeCount >= 4)
                {
                    fire = true;
                    var sb = new System.Text.StringBuilder();
                    foreach (var s in distinct) { if (sb.Length > 0) sb.Append("/"); sb.Append(s); }
                    reason = "⑥精灵帧闪动(静止时) 1s内变化" + changeCount + "次 帧=" + sb.ToString();
                }
            }

            if (fire && now - _lastLogTime > 0.5f)
            {
                _lastLogTime = now;
                string spearStr = _spear != null ?
                    "矛world=" + _spear.rotation.eulerAngles.ToString("F1") +
                    " 矛local=" + _spear.localRotation.eulerAngles.ToString("F1") +
                    " 矛localPos=" + _spear.localPosition.ToString("F3") : "矛=无";
                BSLog.Warn("[抽动] " + _agent.name + " [" + reason + "]" +
                    " pos=" + pos.ToString("F2") + " yaw=" + yaw.ToString("F1") +
                    " navLag=" + (navLag >= 0f ? navLag.ToString("F2") : "-") +
                    " clip=" + clip + " norm=" + norm.ToString("F2") + " animSpeed=" + animSpeed +
                    " sprite=" + sprite + " body=" + body + " phase=" + phase + " " + spearStr);
            }

            _prevPos = pos;
            _prevYaw = yaw;
            _prevClip = clip;
            _prevNorm = norm;
            _prevNavLag = navLag;
            _prevSpearYaw = spearYaw;
            _prevValid = true;
        }

        string DescribeBody()
        {
            try
            {
                if (_agent == null || _agent.body == null) return "null";
                var b = _agent.body;
                return "s:" + (b.standing != null && b.standing.active) +
                    "t:" + (b.stepping != null && b.stepping.active) +
                    "l:" + (b.sliding != null && b.sliding.active) +
                    "h:" + (b.hopping != null && b.hopping.active);
            }
            catch { return "err"; }
        }
    }
}

