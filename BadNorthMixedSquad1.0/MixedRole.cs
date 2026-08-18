using UnityEngine;

namespace BadNorthMixedSquad1_0
{
    /// <summary>混编角色类型。</summary>
    public enum MixedRoleType { None, Shield, Spear, Archer }

    /// <summary>混编角色标记：挂在每个混编生成的 agent 上，供阵型/战术（M2/M3）识别。
    /// 模板构建时不会经过 Brain.Setup，故作为独立标记组件而非 Brain 依赖。</summary>
    public class MixedRole : MonoBehaviour
    {
        public MixedRoleType role = MixedRoleType.None;
    }
}
