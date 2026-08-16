using System;
using UnityEngine;
using Voxels.TowerDefense;
using Voxels.TowerDefense.Ballistics;

namespace BadNorthBlackSpearman1_3
{
    /// <summary>
    /// 黑矛兵盾牌格挡（复刻 Shield.ModifyAttack）：盾牌正面朝向攻击来袭方向时生效——
    /// 近战正面格挡归零、箭矢 ×0.05 弹开/砸落、飞斧归零、长矛 ×0.2。
    /// 由 BlackSpearmanWeapon 在基底盾牌子对象上挂载；cfg EnableShield=false 时仅剩视觉。
    /// 另承担\"实际效果\"维度体检：每 30s 输出盾牌是否真的上屏(isVisible)、距身、格挡触发计数，
    /// 与 BlackSpearmanWeapon 挂载时的\"美术资源\"静态体检互补。
    /// </summary>
    public class BlackSpearmanShield : MonoBehaviour, IAttackResponder
    {
        public Agent agent;
        public Transform shield;
        public bool enabledByCfg = true;
        public Transform swordAnchor;   // 持剑锚点（移盾遮蔽剑柄）：每帧把盾牌贴到该位置，覆盖 Animator 的盾位驱动

        string blockSound = "Sfx/English/SwordShield/Block";
        string arrowBounceSound = "Sfx/English/SwordShield/DeflectArrow";

        // ★ 实际效果统计（美术资源之外的第二维度：格挡是否真的触发过，供体检/F8 读取）
        public int BlockMelee, BlockArrow, BlockAxe, BlockSpear;
        float _healthTimer = 15f;   // 首个体检延迟 15s（等首次上屏）
        string _lastHealthLog;

        public void Setup(Agent a, Transform shieldTf, bool cfgEnabled)
        {
            agent = a;
            shield = shieldTf;
            enabledByCfg = cfgEnabled;
            if (agent != null && agent.attackResponders != null && !agent.attackResponders.Contains(this))
                agent.attackResponders.Add(this);
        }

        public void ModifyAttack(ref Attack attack)
        {
            if (!enabledByCfg || agent == null || shield == null) return;
            if (attack.damage <= 0f) return;

            // 盾牌必须朝向攻击来袭方向才生效（原版 Shield.cs:166：Dot(shield.forward, -attack.direction) > 0.5）
            float facing = Vector3.Dot(shield.forward, -attack.direction.normalized);
            if (facing <= 0.5f) return;

            // ① 近战：正面格挡 → 伤害归零（原版 parry 分支；黑矛兵无 parry 状态，简化成"正面近战必格挡"）
            if (attack.monoAttacker is CloseCombatBrain)
            {
                attack.damage = 0f;
                attack.stun *= 0.4f;
                attack.soundSuffix = "Shield";
                try { IslandGameplayManager.RequestCombatAudio(blockSound, agent.gameObject); } catch { }
                BlockMelee++;
                BSLog.Info("[盾牌] 格挡近战 " + (attack.monoAttacker != null ? attack.monoAttacker.name : "?") + " facing=" + facing.ToString("F2"));
                return;
            }

            // ② 箭矢/投掷物：正面 → 伤害大幅减免 + 弹开/钉住
            var shootable = attack.monoAttacker as Shootable;
            if (shootable != null)
            {
                attack.damage *= agent.isEnglish ? 0f : 0.05f;   // 敌方盾牌挡箭 ×0.05，我方 ×0
                attack.stun *= 0.2f;
                attack.soundSuffix = "ShieldBounce";
                var arrow = shootable as Arrow;
                if (arrow != null)
                {
                    // 原版 Shield.cs:185-199：随机弹开/砸落（Stick 需 Shield 的 bounds+spriteStamper，简化掉）
                    if (UnityEngine.Random.value > 0.5f)
                    {
                        attack.soundSuffix = "ShieldSmash";
                        try { arrow.Smash(shield.position, shield.forward); } catch { }
                    }
                    else
                    {
                        attack.soundSuffix = "ShieldBounce";
                        try { arrow.Bounce(shield.forward + Vector3.up); } catch { }
                    }
                }
                try { IslandGameplayManager.RequestCombatAudio(arrowBounceSound, agent.gameObject); } catch { }
                BlockArrow++;
                BSLog.Info("[盾牌] 挡箭 " + (attack.monoAttacker != null ? attack.monoAttacker.name : "?") + " dmg→" + attack.damage.ToString("F2"));
                return;
            }

            // ③ 飞斧：正面 → 伤害归零 + 弹开
            var axe = attack.monoAttacker as ThrowingAxe;
            if (axe != null)
            {
                attack.damage = 0f;
                attack.stun *= 0.2f;
                attack.soundSuffix = "ShieldBounce";
                try { axe.Bounce(Vector3.up, Vector3.up * 7f); } catch { }
                try { IslandGameplayManager.RequestCombatAudio(arrowBounceSound, agent.gameObject); } catch { }
                BlockAxe++;
                BSLog.Info("[盾牌] 挡飞斧 " + (attack.monoAttacker != null ? attack.monoAttacker.name : "?"));
                return;
            }

            // ④ 我方长矛：正面 → 伤害 ×0.2（原版 spearShield 特殊格挡，黑矛兵无 spearShield 状态，恒生效）
            if (attack.monoAttacker is Spear)
            {
                attack.damage *= 0.2f;
                attack.stun *= 0.4f;
                attack.soundSuffix = "Shield";
                BlockSpear++;
                BSLog.Info("[盾牌] 格挡长矛 " + (attack.monoAttacker != null ? attack.monoAttacker.name : "?") + " dmg→" + attack.damage.ToString("F2"));
            }
        }

        /// <summary>移盾遮蔽剑柄：每帧把盾牌贴到持剑锚点（LateUpdate 在动画之后，保证覆盖 Animator 驱动）。</summary>
        void LateUpdate()
        {
            try
            {
                if (swordAnchor == null || shield == null || agent == null) return;
                shield.position = swordAnchor.position +
                    agent.transform.forward * (agent.radius * 0.25f) + Vector3.up * (agent.radius * 0.1f);
                shield.rotation = Quaternion.LookRotation(agent.transform.forward, Vector3.up);
            }
            catch { }
        }

        /// <summary>定期输出盾牌\"实际效果\"体检（每 30s；仅状态变化或不可见时输出，避免刷屏）。</summary>
        void Update()
        {
            try
            {
                _healthTimer -= Time.deltaTime;
                if (_healthTimer > 0f) return;
                _healthTimer = 30f;
                LogEffectHealth();
            }
            catch { }
        }

        void LogEffectHealth()
        {
            if (agent == null || shield == null) return;
            var r = shield.GetComponentInChildren<Renderer>(true);
            bool visible = r != null && r.isVisible && r.gameObject.activeInHierarchy;
            float dist = Vector3.Distance(shield.position, agent.transform.position);
            string msg = "[盾牌·实际效果] " + agent.name +
                " 可见=" + (visible ? "是" : "否") +
                " 距身=" + dist.ToString("F2") + "m" +
                " 朝外Dot=" + Vector3.Dot(shield.forward, agent.transform.forward).ToString("F2") +
                " 块[近战=" + BlockMelee + " 箭=" + BlockArrow + " 斧=" + BlockAxe + " 矛=" + BlockSpear + "]" +
                " cfg=" + enabledByCfg;
            if (!visible)
                BSLog.Warn(msg + " ← 盾牌当前不可见（美术或渲染链路问题）");
            else if (msg != _lastHealthLog)
                BSLog.Info(msg);
            _lastHealthLog = msg;
        }

        void OnDestroy()
        {
            if (agent != null && agent.attackResponders != null)
                agent.attackResponders.Remove(this);
        }
    }
}
