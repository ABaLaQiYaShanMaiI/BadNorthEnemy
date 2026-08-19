using UnityEngine;
using Voxels.TowerDefense;

namespace BadNorthMixedSquad1_0
{
    /// <summary>箭矢追踪：挂在混编弓手射出的箭矢上，飞行中每帧把速度朝目标当前位置修正（命中率提升）。
    /// 原版是 TrajectoryUtility 抛物线预判，目标移动后箭矢会射空；本组件按强度把弹道拉向目标当前身位。</summary>
    public class ArrowTracking : MonoBehaviour
    {
        Agent _target;
        float _strength;
        Rigidbody _rb;
        bool _done;

        public void Setup(Agent target, float strength)
        {
            _target = target;
            _strength = Mathf.Clamp01(strength);
        }

        void Update()
        {
            if (_done) return;
            if (_target == null || _target.aliveState == null || !_target.aliveState.active)
            {
                _done = true;
                Destroy(this);
                return;
            }
            if (ReferenceEquals(_rb, null))
            {
                _rb = GetComponent<Rigidbody>();
                if (ReferenceEquals(_rb, null))
                {
                    _done = true;
                    Destroy(this);
                    return;
                }
            }
            Vector3 vel = _rb.velocity;
            if (vel.sqrMagnitude < 0.0001f) return;
            Vector3 to = _target.wChestPos - transform.position;
            if (to.sqrMagnitude < 0.0001f) return;
            // 垂直弱修正（保留抛物线下坠感），水平强修正（追人）
            to.y *= 0.4f;
            Vector3 want = Vector3.Slerp(vel.normalized, to.normalized, _strength) * vel.magnitude;
            _rb.velocity = want;
        }
    }
}